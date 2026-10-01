using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Replaces the game's build watermark with this platform's own identity.
	///
	/// <b>What it displaces.</b> Vanilla's top-left label is <c>BuildWatermark.RefreshText</c>
	/// formatting <c>UI.DEVELOPMENTBUILDS.WATERMARK</c> ("BUILD: {0}") with
	/// <c>GetBuildText()</c>, which concatenates <c>LaunchInitializer.BuildPrefix()</c> ("U59"),
	/// the changelist, and <c>DlcManager.GetSubscribedContentLetters()</c> ("SCRAPAND") -- so
	/// the shipped string is three quarters distribution trivia and one quarter the only part
	/// that identifies the build. This keeps the changelist, drops the rest, and spends the
	/// space on the two versions vanilla cannot know about.
	///
	/// <b>Why it is worth a patch at all.</b> Neither half of this platform could previously be
	/// identified from inside a running game. The SimDLL is invisible by construction (see
	/// <see cref="SimVersion"/>) and the framework never announced itself either, so a
	/// screenshot, a recorded run or a bug report carried no evidence of what produced it. That
	/// has already cost a full debugging round here. The watermark is the one piece of UI that
	/// is always on screen, in every scene, from the main menu onward, and it is already
	/// spending its pixels on the least useful available text.
	///
	/// <b>Shape of the line.</b>
	/// <code>Sal: #1ef63ec | SIM 0.1.50* | API 0.1.73* | ONI 744825 | GC STOCK | D RIG:TURBINEFED</code>
	/// The <c>*</c> marks a component built from a dirty working tree; <c>#</c> is the first
	/// seven hex of the SHA-256 of the SimDLL file actually on disk, which is the part that
	/// cannot be talked out of being true. <c>SIM STOCK</c> replaces the version when the loaded
	/// SimDLL is Klei's. <c>GC</c> names the Mono runtime (<see cref="GcRuntime"/>): <c>STOCK</c>,
	/// a replacement runtime's version, or <c>?#</c> and a hash for anything else.
	/// Everything else -- full paths, the DLC letters, the vanilla string this
	/// replaced -- moves to the hover tooltip, which vanilla leaves empty on this branch.
	///
	/// <b>It never loads the SimDLL itself.</b> The watermark is first stamped on the main
	/// menu, and the Steam game does not load SimDLL until a world starts. Asking the library its
	/// version there would load the game's own before a player has answered
	/// <see cref="SimDllDelivery"/>'s consent dialog, and accepting would then need a restart.
	/// So until the library is loaded the line shows only the hash of the file on disk (which
	/// already tells the SDK library from the game's) and <c>SIM -</c>, and the whole line is
	/// composed again at <c>Game.OnSpawn</c>, once the game has loaded it.
	///
	/// <b>Installed by the framework mod itself</b>, from <c>FrameworkMod.OnLoad</c>: the
	/// framework is what puts the SDK library in place for a player, so the label that says which
	/// library ran must not depend on which other mods are installed. <see cref="Install"/> stays
	/// public and idempotent, so a consumer's own call is harmless.
	/// </summary>
	public static class BuildStamp
	{
		/// <summary>
		/// Prefix on the watermark line. Short on purpose: it is there to make the label
		/// unmistakably this project's at a glance in a screenshot, not to spend the width.
		/// </summary>
		public const string Prefix = "Sal: ";

		// Flags that change what a run MEANS rather than which scenario it is, registered by the
		// consumer mod because the framework has no business knowing a gameplay mod's flag names.
		// Key is the exact command-line flag, value is the label to show.
		private static readonly List<KeyValuePair<string, string>> controlFlags
			= new List<KeyValuePair<string, string>>();

		private static bool installed;
		private static string cachedLine;
		private static string cachedTooltip;
		private static bool logged;
		private static bool composedBeforeLoad;

		/// <summary>
		/// Patches <c>BuildWatermark.RefreshText</c>. Idempotent; safe to call from more than
		/// one consumer mod, which is the normal case once several of them depend on the
		/// framework.
		/// </summary>
		/// <summary>
		/// Declares a command-line flag that must ALWAYS be visible on the line when present,
		/// separately from the rig name.
		///
		/// <b>What this is for.</b> Some flags are not a scenario, they are a modifier of what
		/// the scenario MEANS -- the A/B control switch <c>--mod1-vanillaheat</c> is the case
		/// this exists for. Both arms of an A/B run the same rig, so a label that showed only the
		/// rig would render a control run and its experiment identically, and a control arm
		/// mistaken for the experiment is the single most expensive confusion this label can
		/// cause. Relegating it to the tooltip was tried and rejected.
		///
		/// Registered by the consumer, not hard-coded here: the framework ships no gameplay and
		/// has no business knowing which flags a given mod treats as a control (mod.yaml).
		/// Idempotent; call from <c>UserMod2.OnLoad</c> before anything composes.
		/// </summary>
		/// <param name="flag">Exact command-line flag, e.g. <c>--mod1-vanillaheat</c>.</param>
		/// <param name="label">Short label for the line, e.g. <c>VANILLAHEAT</c>.</param>
		public static void MarkControlFlag(string flag, string label)
		{
			if (string.IsNullOrEmpty(flag) || string.IsNullOrEmpty(label))
			{
				return;
			}
			for (int i = 0; i < controlFlags.Count; i++)
			{
				if (controlFlags[i].Key == flag)
				{
					return;
				}
			}
			controlFlags.Add(new KeyValuePair<string, string>(flag, label));

			// A registration after a compose would otherwise be invisible until something else
			// invalidated the cache.
			cachedLine = null;
			cachedTooltip = null;
		}

		public static void Install(Harmony harmony)
		{
			if (installed || harmony == null)
			{
				return;
			}

			try
			{
				MethodInfo target = AccessTools.Method(typeof(BuildWatermark), "RefreshText");
				if (target == null)
				{
					Debug.LogWarning("[OniFramework] BuildStamp: BuildWatermark.RefreshText not found; watermark left alone");
					return;
				}

				harmony.Patch(target, null,
					new HarmonyMethod(AccessTools.Method(typeof(BuildStamp), "Restamp")));
				// The line first composed on the main menu has no SIM version yet; see the class
				// summary.
				harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), null,
					new HarmonyMethod(AccessTools.Method(typeof(BuildStamp), "AfterGameSpawn")));
				installed = true;
			}
			catch (Exception e)
			{
				// A cosmetic label must never be able to take a mod down on load.
				Debug.LogWarning("[OniFramework] BuildStamp: could not patch the watermark: " + e);
			}
		}

		/// <summary>
		/// The composed watermark line. Built once and cached: every input is fixed for the life
		/// of the process except the armed-rig list, which is fixed before the first watermark
		/// ever spawns.
		/// </summary>
		public static string Line
		{
			get
			{
				if (cachedLine == null)
				{
					Compose();
				}
				return cachedLine;
			}
		}

		/// <summary>
		/// The long form: everything the line had to leave out, one item per row.
		/// </summary>
		public static string Detail
		{
			get
			{
				if (cachedTooltip == null)
				{
					Compose();
				}
				return cachedTooltip;
			}
		}

		/// <summary>
		/// Discards the composed line and re-stamps the live watermark, if there is one.
		///
		/// <b>Why this is needed at all.</b> The watermark is spawned and stamped at the MAIN
		/// MENU, and rigs register themselves from <c>Game.OnSpawn</c> -- a whole scene later. So
		/// the first composition happens while <see cref="RigRegistry.Armed"/> is still empty and
		/// a cached line would never grow its <c>RIG:</c> segment, which is exactly the part that
		/// makes a screenshot of an automated run self-describing. Called from
		/// <see cref="RigRegistry.LogArmed"/>, which already runs once after every rig has
		/// registered.
		///
		/// Safe when nothing is installed and safe when no watermark exists yet: it clears the
		/// cache either way, so the next composition is current.
		/// </summary>
		public static void Refresh()
		{
			cachedLine = null;
			cachedTooltip = null;
			logged = false;
			try
			{
				if (BuildWatermark.Instance != null)
				{
					BuildWatermark.Instance.RefreshText();
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] BuildStamp: could not refresh the watermark: " + e);
			}
		}

		private static void AfterGameSpawn()
		{
			if (composedBeforeLoad)
			{
				Refresh();
			}
		}

		private static void Restamp(BuildWatermark __instance)
		{
			try
			{
				if (__instance == null || __instance.textDisplay == null)
				{
					return;
				}

				__instance.textDisplay.SetText(Line);
				if (__instance.toolTip != null)
				{
					// Vanilla's own branch calls ClearMultiStringTooltip() here and sets
					// nothing, so this occupies space that was empty rather than displacing a
					// tooltip a player might have wanted.
					__instance.toolTip.SetSimpleTooltip(Detail);
				}

				if (!logged)
				{
					// Once, and only from here rather than from Install: this runs well after
					// startup, so the native SimDLL is certainly resolvable by now, and the line
					// then lands in Player.log where a run archive keeps it beside the result it
					// belongs to.
					logged = true;
					Debug.Log("[OniFramework] BuildStamp: " + Line);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] BuildStamp: could not restamp the watermark: " + e);
			}
		}

		/// <summary>
		/// <c>MAJOR.MINOR.REV</c> plus the dirty marker, dropping the <c>+SHA</c> build metadata.
		///
		/// <b>Why the line and the tooltip differ.</b> The label has a hard width budget and it
		/// is not negotiable: on the main menu Klei's promo cards begin at x=1018 of a 1918-wide
		/// window, which is about 85 characters, and the full form of both versions spent 72 of
		/// them before the rig segment started -- so the part naming the run was pushed under the
		/// cards and became unreadable. Measured, not estimated.
		///
		/// <b>The commit is what gives, because it gives up the least.</b> REV is
		/// <c>git rev-list --count HEAD</c>, so within a branch it already identifies the commit;
		/// the SHA only adds disambiguation across branches. The deployed-file hash beside it is
		/// untouched, and that is the one that answers "is this the build I made". The tooltip
		/// keeps both versions whole.
		/// </summary>
		private static string ShortVersion(string version)
		{
			if (string.IsNullOrEmpty(version))
			{
				return version;
			}
			int plus = version.IndexOf('+');
			if (plus < 0)
			{
				return version;
			}
			// Keep a trailing dirty marker: a build made from uncommitted edits has to say so on
			// the line, not only in a tooltip nobody hovers.
			return version.Substring(0, plus) + (version.EndsWith("*") ? "*" : "");
		}

		private static void Compose()
		{
			var line = new List<string>();
			var detail = new List<string>();

			// --- the sim -------------------------------------------------------------------
			//
			// THE FILE HASH LEADS, immediately after the prefix and as its own segment. It is
			// the only value here that cannot be argued with -- the version is what a DLL claims
			// about itself, the hash is what its bytes actually are -- so it earns the position
			// the eye lands on first. It is the SimDLL's hash and sits beside SIM for that
			// reason, but it is separated rather than appended: a reader comparing it against
			// `sha256sum sim/build/SimDLL.dll` wants to find it without reading past a version.
			//
			// It carries the prefix when it is present so the line always opens the same way;
			// when the file could not be hashed the prefix falls through to the SIM segment
			// rather than leaving a dangling "Sal: |".
			// SimVersion.Version calls into the library, which loads it; see the class summary.
			bool loaded = SimVersion.SimLoaded();
			composedBeforeLoad = !loaded;
			string simVersion = loaded ? SimVersion.Version : null;
			string simHash = SimVersion.FileHash;
			string simSegment = "SIM " + (!loaded ? "-" : simVersion != null ? ShortVersion(simVersion) : "STOCK");
			if (simHash != null)
			{
				line.Add(Prefix + "#" + simHash);
				line.Add(simSegment);
			}
			else
			{
				line.Add(Prefix + simSegment);
			}

			detail.Add(!loaded
				? "SimDLL: not loaded yet (the game loads it when a world starts); the hash is the file on disk"
				: simVersion != null
				? "SimDLL: custom, " + simVersion
				: "SimDLL: STOCK (Klei's -- no SIM_Version export)");
			if (simHash != null)
			{
				detail.Add("  sha256: " + simHash + "...  " + (SimVersion.FilePath ?? "?"));
			}

			// --- the framework -------------------------------------------------------------
			line.Add("API " + ShortVersion(GeneratedVersion.Value));
			detail.Add("OniFramework: " + GeneratedVersion.Value);
			try
			{
				detail.Add("  " + typeof(BuildStamp).Assembly.Location);
			}
			catch (Exception)
			{
				// Location throws for a dynamic assembly. Not worth a branch beyond this.
			}

			// --- the game ------------------------------------------------------------------
			// Read at RUNTIME, not as the compile-time constant it is. `KleiVersion.ChangeList`
			// is a `const uint`, so referencing it directly would bake 744825 into this assembly
			// and keep displaying it after a game update -- a staleness bug in the one feature
			// whose entire purpose is to catch staleness. GetRawConstantValue reads the value
			// out of the assembly that is actually loaded.
			string changeList = "?";
			try
			{
				FieldInfo field = typeof(KleiVersion).GetField("ChangeList",
					BindingFlags.Public | BindingFlags.Static);
				if (field != null)
				{
					changeList = Convert.ToString(field.GetRawConstantValue());
				}
			}
			catch (Exception)
			{
				changeList = KleiVersion.ChangeList.ToString();
			}
			line.Add("ONI " + changeList);
			detail.Add("Oxygen Not Included: build " + changeList + ", branch " + KleiVersion.BuildBranch);

			// --- the garbage collector -------------------------------------------------------
			// Before the flags, so on the main menu (where the promo cards cap the line at about
			// 85 characters, and no rig has registered yet) it is still visible. The helper
			// version is a diagnostic tool's, not the collector's, so it stays in the tooltip.
			try
			{
				line.Add("GC " + GcRuntime.Label);
				detail.Add(GcRuntime.Detail);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] BuildStamp: could not read the GC runtime: " + e.Message);
			}

			try
			{
				detail.Add("  vanilla watermark: " + BuildWatermark.GetBuildText());
			}
			catch (Exception)
			{
				// GetBuildText touches DistributionPlatform; not worth failing the label over.
			}

			// --- state flags ----------------------------------------------------------------
			var flags = new List<string>();
			try
			{
				if (DebugHandler.enabled)
				{
					flags.Add("D");
					detail.Add("Debug: enabled");
				}
			}
			catch (Exception)
			{
			}

			// CONTROL FLAGS SIT WITH THE STATE FLAGS, not in the rig list, because that is what
			// they are: --mod1-vanillaheat does not name a scenario, it changes which arm of an
			// A/B the scenario is being run as. Emitted BEFORE the rig segment so nothing trails
			// the rig name, and shown whether or not a rig is armed.
			string[] presentArgs = RigRegistry.CommandLineFlags;
			var controlPresent = new List<string>();
			for (int i = 0; i < controlFlags.Count; i++)
			{
				if (Array.IndexOf(presentArgs, controlFlags[i].Key) >= 0)
				{
					controlPresent.Add(controlFlags[i].Value);
					flags.Add(controlFlags[i].Value);
					detail.Add("Control flag: " + controlFlags[i].Key
						+ " -- this run is NOT the default arm");
				}
			}

			// THE COMMAND LINE IS THE SOURCE OF TRUTH, not the registries. Rigs arm three
			// different ways in this project -- RigRegistry.Register, BlueprintRig, and a couple
			// that read Environment.GetCommandLineArgs() themselves -- so a label built from the
			// registries could not tell "nothing armed" from "armed by a mechanism I do not
			// watch". Every rig arms off a --mod* flag, so the flags say what the run was asked
			// to do regardless of who reads them, and RigRegistry.CommandLineFlags cannot miss
			// one. It also catches --mod1-vanillaheat, which is not a rig at all but the A/B
			// control switch: a control arm mislabelled as the experiment is precisely the
			// confusion this segment exists to prevent.
			//
			// The registries are still consulted, for the tooltip and as a union, so a rig that
			// somehow armed without a recognisable flag would still be named.
			string[] flagArgs = RigRegistry.CommandLineFlags;
			var rigNames = new List<string>();
			for (int i = 0; i < flagArgs.Length; i++)
			{
				// MODIFIERS COLLAPSE INTO WHAT THEY MODIFY. --mod1-record brings
				// --mod1-record-fps and --mod1-record-quality with it, and three near-identical
				// words would crowd out the rig they are options for. A flag that is another
				// present flag plus "-something" is an option of it, not a scenario of its own.
				bool isModifier = false;
				for (int j = 0; j < flagArgs.Length; j++)
				{
					if (j != i && flagArgs[i].StartsWith(flagArgs[j] + "-"))
					{
						isModifier = true;
						break;
					}
				}
				if (isModifier)
				{
					continue;
				}

				// Strip the --modN- prefix: it is the same on every flag and this label is
				// short. The tooltip keeps them whole.
				string name = flagArgs[i];
				int dash = name.IndexOf('-', RigRegistry.FlagPrefix.Length);
				string shortName = (dash >= 0 && dash + 1 < name.Length
					? name.Substring(dash + 1) : name).ToUpperInvariant();
				// A control flag is already shown as a state flag; listing it here too could make
				// it rigNames[0] and label the run after its own control switch.
				if (!rigNames.Contains(shortName) && !controlPresent.Contains(shortName))
				{
					rigNames.Add(shortName);
				}
			}

			string canvasRig = null;
			try
			{
				canvasRig = BlueprintRig.ArmedRigTag;
			}
			catch (Exception)
			{
			}
			if (!string.IsNullOrEmpty(canvasRig))
			{
				// FIRST, not appended: only rigNames[0] reaches the line, and the canvas rig is
				// the one that names the run. Removed from wherever the flag scan already put it
				// so it is not listed twice in the tooltip's union.
				string canvasName = canvasRig.ToUpperInvariant();
				rigNames.Remove(canvasName);
				rigNames.Insert(0, canvasName);
			}

			string[] armed = RigRegistry.Armed;
			if (armed != null)
			{
				for (int i = 0; i < armed.Length; i++)
				{
					string name = armed[i];
					int dash = name.LastIndexOf('-');
					string shortName = (dash >= 0 && dash + 1 < name.Length
						? name.Substring(dash + 1) : name).ToUpperInvariant();
					if (!rigNames.Contains(shortName))
					{
						rigNames.Add(shortName);
					}
				}
			}

			if (rigNames.Count > 0)
			{
				// ONE NAME ON THE LINE. The rest of the run's flags are in the tooltip, and the
				// line stops at the rig rather than trailing options behind it.
				//
				// Two earlier shapes were tried and dropped: the whole comma-separated list (too
				// long -- on the MAIN MENU Klei's promo cards begin at x=1018 of a 1918-wide
				// window, about 85 characters, and the tail rendered underneath them), and a
				// two-name cap with a "+1" remainder (a count names no rig, and a label whose
				// job is to say what a run was should not pose a question).
				//
				// THE CANVAS RIG WINS over the first flag when there is one, because the flag
				// order is whatever the caller typed: `--mod1-vanillaheat --mod1-turbinefed`
				// would otherwise label the run after its control switch instead of its
				// scenario. Falling back to the first flag covers rigs that arm no canvas.
				//
				// KNOWN COST, recorded rather than hidden: a control arm (--mod1-vanillaheat)
				// now looks identical to the experiment on the line, and is distinguishable only
				// in the tooltip's "Run flags:" row.
				flags.Add("RIG:" + rigNames[0]);
				detail.Add("Run flags: " + (flagArgs.Length > 0
					? string.Join(" ", flagArgs) : "none"));
				detail.Add("  confirmed armed:"
					+ (string.IsNullOrEmpty(canvasRig) ? "" : " canvas=" + canvasRig)
					+ (armed != null && armed.Length > 0
						? " registered=" + string.Join(",", armed) : "")
					+ (string.IsNullOrEmpty(canvasRig) && (armed == null || armed.Length == 0)
						? " none yet (rigs register a scene after this composes)" : ""));
			}
			else
			{
				detail.Add("Run flags: none -- an ordinary launch, no rig and no control arm");
			}

			if (flags.Count > 0)
			{
				line.Add(string.Join(" ", flags.ToArray()));
			}

			// RELEASE RECORDINGS. `--stamp-release=<version>` replaces the development build's
			// hashes and version numbers with the release's version, for footage that is published:
			// the line keeps what a viewer can check (the release, the game build, the demonstration)
			// and the tooltip drops the hashes and the paths, which name local folders. Without the
			// flag nothing changes.
			string release = ReleaseLabel();
			if (release != null)
			{
				var pub = new List<string> { Prefix + "SDK " + release, "ONI " + changeList };
				// The GC field stays: it names the collector (STOCK or a variant), not a version
				// number, and with it absent the GC timing mod appends a segment of its own.
				string gc = line.Find(seg => seg.StartsWith("GC "));
				if (gc != null)
				{
					pub.Add(gc);
				}
				if (flags.Count > 0)
				{
					pub.Add(string.Join(" ", flags.ToArray()));
				}
				cachedLine = string.Join(" | ", pub.ToArray());
				cachedTooltip = "Oxygen Not Included simulation SDK " + release
					+ "\nGame build " + changeList;
				return;
			}

			cachedLine = string.Join(" | ", line.ToArray());
			cachedTooltip = string.Join("\n", detail.ToArray());
		}

		internal const string ReleaseFlag = "--stamp-release=";

		/// <summary>The value of <c>--stamp-release=</c>, or null when the launch has none.</summary>
		private static string ReleaseLabel()
		{
			try
			{
				string[] args = Environment.GetCommandLineArgs();
				for (int i = 0; i < args.Length; i++)
				{
					if (args[i] != null && args[i].StartsWith(ReleaseFlag)
						&& args[i].Length > ReleaseFlag.Length)
					{
						return args[i].Substring(ReleaseFlag.Length);
					}
				}
			}
			catch (Exception)
			{
			}
			return null;
		}
	}
}
