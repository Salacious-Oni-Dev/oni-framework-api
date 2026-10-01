using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Which <c>OniFramework</c> a consumer mod is actually talking to, whether there is more than
	/// one of it in the process, and whether it is new enough to have the API that consumer expects.
	///
	/// <b>The problem this exists for.</b> Mono loads one assembly per simple name, so whichever
	/// copy of <c>OniFramework</c> loads first wins for the entire process -- every other mod's
	/// patched code included, and a version skew between the copies throws a
	/// <see cref="TypeLoadException"/>. The SDK's own mods reference the framework with
	/// <c>&lt;Private&gt;false&lt;/Private&gt;</c>; nothing holds a third party to that, and an
	/// outside author who adds <c>OniFramework.dll</c> with Copy Local = true reproduces the
	/// failure exactly.
	///
	/// <b>Why that needs a detector rather than only a rule.</b> The mod that throws is the victim,
	/// not the culprit -- the same asymmetry as the BepInEx/Harmony conflict, where the assembly
	/// that gets hijacked is the one that reports the crash. A duplicate-copy failure therefore
	/// lands as a bug report on somebody innocent, describing a type that plainly exists. Turning
	/// that into a named diagnosis at load time is most of the value here.
	///
	/// <b>The versioning half.</b> <see cref="SimVersion"/> answers the same question for the
	/// NATIVE DLL and this deliberately mirrors its shape: a version the component reports about
	/// itself, plus the path it was actually loaded from, because those two fail differently. The
	/// version string itself already existed -- <c>build.sh</c> writes it into
	/// <c>Version.generated.cs</c> from the <c>VERSION</c> file plus git -- but it was
	/// <c>internal</c> and fed only the on-screen watermark, so no consumer could check against it.
	/// This publishes it and adds the floor check.
	///
	/// <b>Compatibility rule, taken from the VERSION file rather than invented here.</b> MINOR
	/// advances when the public API GAINS a surface; MAJOR advances when an existing one CHANGES
	/// SHAPE. So a consumer built against <c>M.m</c> is satisfied by the same <c>M</c> at a minor
	/// no lower than <c>m</c>, and by nothing else. A differing MAJOR is incompatible in both
	/// directions -- a newer one has moved something the consumer calls.
	///
	/// <b>This refuses nothing on a consumer's behalf.</b> <see cref="Require"/> returns a bool and
	/// logs; it does not throw. Throwing out of a <c>UserMod2.OnLoad</c> breaks the mod loader for
	/// everyone downstream, which is a worse failure than the skew being reported, and only the
	/// consumer knows whether it can degrade or must stop.
	/// </summary>
	public static class FrameworkVersion
	{
		/// <summary>
		/// Assembly simple name this guards. One place, because it appears in a scan predicate and
		/// in several messages, and a typo in the predicate would silently disable the detector.
		/// </summary>
		private const string AssemblyName = "OniFramework";

		private static bool parsed;
		private static int major = -1;
		private static int minor = -1;
		private static bool duplicateReported;

		/// <summary>
		/// The full version of the loaded framework: <c>MAJOR.MINOR.REV+SHA</c>, with a trailing
		/// <c>*</c> when it was built from a dirty working tree. Never null -- the csproj refuses
		/// to compile without a generated stamp, so an unstamped assembly cannot ship.
		/// </summary>
		public static string Version
		{
			get { return GeneratedVersion.Value; }
		}

		/// <summary>
		/// MAJOR component, or -1 if the stamp could not be parsed.
		/// </summary>
		public static int Major
		{
			get { Parse(); return major; }
		}

		/// <summary>
		/// MINOR component, or -1 if the stamp could not be parsed.
		/// </summary>
		public static int Minor
		{
			get { Parse(); return minor; }
		}

		/// <summary>
		/// True when this framework was built from a working tree with uncommitted edits. Worth
		/// surfacing in a bug report: a dirty build's version identifies no reproducible source.
		/// </summary>
		public static bool IsDirty
		{
			get { return Version != null && Version.EndsWith("*"); }
		}

		/// <summary>
		/// The path this assembly was loaded from, or <c>null</c> for a dynamic assembly. The
		/// counterpart to <see cref="SimVersion.FilePath"/>, and for the same reason: when a
		/// duplicate is in play, the path is what distinguishes the copy that won from the copy
		/// that was meant to.
		/// </summary>
		public static string Location
		{
			get
			{
				try
				{
					return typeof(FrameworkVersion).Assembly.Location;
				}
				catch (Exception)
				{
					// Thrown for a dynamic assembly. Not an error, just an absent answer.
					return null;
				}
			}
		}

		/// <summary>
		/// Declare the minimum framework API a consumer needs. Call once from
		/// <c>UserMod2.OnLoad</c>, before composing anything.
		///
		/// Returns true when the loaded framework satisfies the floor. Returns false -- having
		/// logged an error naming both versions and the consumer -- when it does not, leaving the
		/// decision to degrade or stop with the caller, which is the only party that knows which
		/// is right.
		///
		/// Also runs <see cref="CheckSingleInstance"/>, so a consumer that declares a floor gets
		/// the duplicate diagnosis for free and without a second call to remember.
		/// </summary>
		/// <param name="requiredMajor">MAJOR the consumer was built against.</param>
		/// <param name="requiredMinor">Lowest MINOR carrying the API the consumer calls.</param>
		/// <param name="consumer">
		/// Name of the calling mod, used in the log line. A skew message that does not say WHO
		/// wanted what is a message the reader cannot act on.
		/// </param>
		public static bool Require(int requiredMajor, int requiredMinor, string consumer)
		{
			CheckSingleInstance();

			string who = string.IsNullOrEmpty(consumer) ? "a consumer mod" : consumer;
			Parse();

			if (major < 0 || minor < 0)
			{
				// Unparseable stamp. Report it and let the consumer through: refusing to run on
				// the strength of a version string we failed to READ would turn our own bug into
				// their outage, and every other signal here says the assembly is fine.
				Debug.LogWarning("[OniFramework] FrameworkVersion: could not parse version '" + Version
					+ "'; cannot verify the " + requiredMajor + "." + requiredMinor
					+ " floor required by " + who + ". Allowing.");
				return true;
			}

			if (major != requiredMajor)
			{
				FrameworkLog.Error("VERSION SKEW: " + who + " requires OniFramework API "
					+ requiredMajor + "." + requiredMinor + ", but the loaded framework is "
					+ Version + " (MAJOR " + major + "). A differing MAJOR means an API this mod "
					+ "calls has changed shape, so this is incompatible in both directions. "
					+ "Loaded from: " + (Location ?? "<unknown>"));
				return false;
			}

			if (minor < requiredMinor)
			{
				FrameworkLog.Error("VERSION SKEW: " + who + " requires OniFramework API "
					+ requiredMajor + "." + requiredMinor + " or newer, but the loaded framework is "
					+ Version + ". The API surface this mod calls was added after this build. "
					+ "Update OniFramework. Loaded from: " + (Location ?? "<unknown>"));
				return false;
			}

			return true;
		}

		/// <summary>
		/// <see cref="Require(int, int, string)"/>, and hands back the extension-registry owner
		/// token minted from <paramref name="consumer"/>.
		///
		/// <b>Why the token comes from here.</b> An extension property's name is
		/// <c>&lt;owner&gt;.&lt;property&gt;</c>, and the owner half is a permanent on-disk key
		/// -- it is how a saved property's bytes are found again. The sim enforces the shape of
		/// a name and nothing else: it cannot tell which mod sent a message, so nothing native
		/// can stop one mod registering under another's prefix. That check has to live in the
		/// managed wrapper, and this is the call a consumer already makes, so the identity it
		/// already declares is the identity it registers under. A mod that never declared a
		/// floor has no token and cannot register.
		///
		/// The token is minted even when this returns false -- a version-skew decision belongs
		/// to the caller, and a consumer that chooses to degrade rather than stop may still want
		/// to register what it can.
		/// </summary>
		/// <param name="owner">The caller's extension prefix. Check
		/// <see cref="ExtOwner.IsValid"/>: a consumer name that folds to nothing legal mints an
		/// invalid token rather than a made-up prefix.</param>
		public static bool Require(int requiredMajor, int requiredMinor, string consumer,
			out ExtOwner owner)
		{
			owner = SimExtRegistry.ClaimOwner(consumer);
			return Require(requiredMajor, requiredMinor, consumer);
		}

		/// <summary>
		/// Log loudly if more than one <c>OniFramework</c> is loaded in this process, naming every
		/// copy's path and which one won.
		///
		/// <b>Timing, and the limit it imposes.</b> Assemblies load lazily, so a bundled duplicate
		/// inside another mod may not be loaded yet when this runs and cannot be seen. That makes
		/// this best-effort by construction. It is therefore safe and useful to call more than once
		/// -- from <c>OnLoad</c> and again once the game is running -- so the check is cheap,
		/// repeatable, and logs at most one report per session rather than latching a clean answer
		/// it may be too early to give.
		/// </summary>
		public static void CheckSingleInstance()
		{
			if (duplicateReported)
			{
				return;
			}

			var copies = new List<Assembly>();
			try
			{
				foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					try
					{
						if (asm.GetName().Name == AssemblyName)
						{
							copies.Add(asm);
						}
					}
					catch (Exception)
					{
						// A single unreadable assembly name must not abort the scan; the copy we
						// are looking for may be the next one.
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] FrameworkVersion: could not enumerate assemblies: " + e.Message);
				return;
			}

			if (copies.Count <= 1)
			{
				// Not latched: see the timing note above. A duplicate that has not loaded yet is
				// still able to load later.
				return;
			}

			duplicateReported = true;

			Assembly winner = typeof(FrameworkVersion).Assembly;
			var report = new System.Text.StringBuilder();
			report.Append("DUPLICATE FRAMEWORK: ").Append(copies.Count)
				.Append(" copies of ").Append(AssemblyName).AppendLine(" are loaded in this process.");
			report.AppendLine("Mono binds one assembly per name, so ONE of these serves every mod, and any "
				+ "mod built against a different copy can fail with a TypeLoadException naming a type that "
				+ "plainly exists. The mod that throws is the VICTIM, not the cause.");

			for (int i = 0; i < copies.Count; i++)
			{
				string path;
				try { path = copies[i].Location; }
				catch (Exception) { path = "<dynamic>"; }
				report.Append(ReferenceEquals(copies[i], winner) ? "  [IN USE] " : "  [shadowed] ")
					.Append(string.IsNullOrEmpty(path) ? "<unknown path>" : path)
					.Append("  ").AppendLine(copies[i].FullName);
			}

			report.Append("FIX: OniFramework must be INSTALLED AS ITS OWN MOD and referenced with "
				+ "Copy Local = false (<Private>false</Private>). Never bundle or ILMerge it into a "
				+ "consuming mod.");

			FrameworkLog.Error(report.ToString());
		}

		/// <summary>
		/// Split MAJOR and MINOR out of <c>MAJOR.MINOR.REV+SHA</c>. Tolerant on purpose: anything
		/// it cannot read leaves both at -1, which <see cref="Require"/> treats as "unknown, allow"
		/// rather than as a failed comparison.
		/// </summary>
		private static void Parse()
		{
			if (parsed)
			{
				return;
			}
			parsed = true;

			string v = Version;
			if (string.IsNullOrEmpty(v))
			{
				return;
			}

			// Cut the +SHA (and any trailing dirty marker with it) before splitting on '.', so the
			// numeric parse never sees hex.
			int plus = v.IndexOf('+');
			if (plus >= 0)
			{
				v = v.Substring(0, plus);
			}

			string[] parts = v.Split('.');
			if (parts.Length < 2)
			{
				return;
			}

			int m, n;
			if (int.TryParse(parts[0], out m) && int.TryParse(parts[1], out n))
			{
				major = m;
				minor = n;
			}
		}
	}
}
