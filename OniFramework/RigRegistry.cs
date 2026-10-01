using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Arms an opt-in rig from a command-line flag.
	///
	/// WHY THIS EXISTS. <c>Patches.cs</c> in Mod 1 held fifteen copies of the same eight lines,
	/// and Mod 2 held more:
	/// <code>
	/// if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mod1-thing") &gt;= 0)
	/// {
	///     var thingObject = new GameObject("Mod1ThermoFluidThing");
	///     thingObject.AddComponent&lt;Mod1ThingProbe&gt;();
	///     UnityEngine.Object.DontDestroyOnLoad(thingObject);
	/// }
	/// </code>
	/// Each copy carried a valuable per-rig comment and an identical body, which is the wrong way
	/// round: the comment is the part worth keeping and the body is the part worth writing once.
	///
	/// It also fixes something the copies could not do individually. A run that mistypes its flag
	/// arms nothing and looks exactly like a run whose rig produced no output, because nothing
	/// ever announced which rigs were armed. <see cref="LogArmed"/> prints the list.
	/// </summary>
	public static class RigRegistry
	{
		private static readonly List<string> armed = new List<string>();

		/// <summary>
		/// True when any of <paramref name="flags"/> is on the command line.
		///
		/// Exposed separately because a few call sites need the answer without registering
		/// anything -- a rig that changes how ANOTHER rig behaves, for instance.
		/// </summary>
		public static bool AnyFlag(params string[] flags)
		{
			if (flags == null)
			{
				return false;
			}

			string[] args = Environment.GetCommandLineArgs();
			for (int i = 0; i < flags.Length; i++)
			{
				if (Array.IndexOf(args, flags[i]) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// If any of <paramref name="flags"/> is present, creates a <c>DontDestroyOnLoad</c>
		/// GameObject named <paramref name="objectName"/> carrying a <typeparamref name="T"/>.
		/// Returns whether it did.
		///
		/// More than one flag is accepted because a couple of rigs are genuinely reachable two
		/// ways -- the overlay screenshot runs under both <c>--mod1-screenshot</c> and
		/// <c>--mod1-rdcprep</c>, the second being the same scenario left idling for RenderDoc to
		/// attach to.
		/// </summary>
		public static bool Register<T>(string objectName, params string[] flags)
			where T : Component
		{
			return Register<T>(objectName, null, flags);
		}

		/// <summary>
		/// As <see cref="Register{T}(string, string[])"/>, plus a hook that runs on the freshly
		/// added component before the object is marked <c>DontDestroyOnLoad</c>.
		///
		/// For the rigs that need a line of setup at registration time rather than in their own
		/// <c>Update</c> -- the run recorder reads its fps and quality off the command line this
		/// way, and it has to happen before the first frame it might capture.
		/// </summary>
		public static bool Register<T>(string objectName, Action<T> configure, params string[] flags)
			where T : Component
		{
			if (!AnyFlag(flags))
			{
				return false;
			}

			var host = new GameObject(objectName);
			var component = host.AddComponent<T>();
			if (configure != null)
			{
				configure(component);
			}
			UnityEngine.Object.DontDestroyOnLoad(host);

			armed.Add(flags != null && flags.Length > 0 ? flags[0] : objectName);
			return true;
		}

		/// <summary>
		/// The rigs this launch armed, by their first flag, in registration order. Empty when
		/// none are.
		///
		/// Same list <see cref="LogArmed"/> prints, exposed because a log line is only readable
		/// while you still have the log: <see cref="BuildStamp"/> puts this on the build
		/// watermark so a screenshot or a recorded video carries what run it was of. Returns a
		/// copy -- the registry owns the list.
		/// </summary>
		public static string[] Armed
		{
			get { return armed.ToArray(); }
		}

		/// <summary>
		/// The conventional prefix of a rig/scenario flag in this project: every one of them is
		/// <c>--mod&lt;N&gt;-something</c>. Not invented here -- <c>Mod1RunRecorder</c> already
		/// decides whether a run "has a scenario in it at all" with exactly this test.
		/// </summary>
		public const string FlagPrefix = "--mod";

		/// <summary>
		/// Every <see cref="FlagPrefix"/> argument on this launch's command line, in the order
		/// given.
		///
		/// <b>Why this exists alongside <see cref="Armed"/>.</b> <see cref="Armed"/> only knows
		/// about rigs that went through <see cref="Register{T}(string, string[])"/>, and not
		/// every rig does: canvas rigs arm through <c>BlueprintRig</c>, and some read
		/// <c>Environment.GetCommandLineArgs()</c> themselves. A label built from the registries
		/// alone therefore could not distinguish "nothing armed" from "armed by a mechanism I do
		/// not watch", which is the worst answer a run label can give.
		///
		/// The command line does not have that problem: every rig in this project arms off a
		/// flag, so the flags ARE the ground truth of what a run was asked to do, whoever ends up
		/// reading them. It also captures the ones no registry could -- <c>--mod1-vanillaheat</c>
		/// is not a rig at all, it is the A/B control switch, and a control arm mislabelled as
		/// the experiment is exactly the confusion a run label exists to prevent.
		/// </summary>
		public static string[] CommandLineFlags
		{
			get
			{
				var found = new List<string>();
				try
				{
					string[] args = Environment.GetCommandLineArgs();
					for (int i = 0; i < args.Length; i++)
					{
						// Only the flags themselves: a value that follows one (a number after
						// --mod1-record-fps) does not start with the prefix and is skipped.
						if (args[i] != null && args[i].StartsWith(FlagPrefix))
						{
							found.Add(args[i]);
						}
					}
				}
				catch (Exception)
				{
					// An unreadable command line is not worth failing a label over.
				}
				return found.ToArray();
			}
		}

		/// <summary>
		/// Logs which rigs this launch armed, or says plainly that it armed none.
		///
		/// Call once after all the <see cref="Register{T}(string, string[])"/> calls. The
		/// no-rigs-armed case is the one worth printing: it is what a mistyped flag looks like,
		/// and without this line it is indistinguishable from a rig that ran and said nothing.
		/// </summary>
		public static void LogArmed(string logPrefix)
		{
			if (armed.Count == 0)
			{
				Debug.Log(logPrefix + "no rig flags on the command line; nothing armed");
				return;
			}

			Debug.Log(logPrefix + "armed " + armed.Count + " rig(s): "
				+ string.Join(" ", armed.ToArray()));

			// The build watermark composed itself at the main menu, a scene before any of these
			// registered, so its RIG: segment would otherwise stay empty for the whole run --
			// and that segment is the reason a screenshot or a recorded video of an automated
			// run says what run it was of. This is the one point where the list is known to be
			// complete. No-op when BuildStamp was never installed.
			BuildStamp.Refresh();
		}
	}
}
