using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// RE-SKINS ONI'S CRASH DIALOG FOR AN AUTOMATED RUN, so a rig that dies says what it was
	/// doing instead of asking you to switch off the mods you are testing.
	///
	/// <b>What is wrong with the vanilla screen HERE, as opposed to in a player's game.</b>
	/// <c>KCrashReporter.ShowDialog</c> picks between two modes, and it picks <c>DisableMods</c>
	/// whenever <c>modManager.HasCrashableMods()</c> is true -- which on any launch of this
	/// project is always. That mode's whole proposition is "uncheck all of the mods below and we
	/// will be able to help next time": a reasonable thing to say to a player with thirty Workshop
	/// subscriptions, and precisely the wrong thing to say to a run whose mods ARE the experiment.
	/// Acting on it is how a rig run silently becomes meaningless -- the same failure mode as a
	/// stale DLL, and harder to notice, because the game boots and behaves.
	///
	/// (The auto-uncheck itself does not reach us: <c>ReportErrorDialog.BuildModsList</c> skips
	/// <c>Label.DistributionPlatform.Dev</c>, and everything in <c>mods/dev</c> is Dev. It still
	/// lists them, in red, each with a live toggle.)
	///
	/// <b>What it replaces them with.</b> Everything the process already knows and the dialog
	/// throws away: which rig was running, the build watermark, the assertion tally at the moment
	/// of the throw, the phase name, the sim seconds, and the exception's own first line -- plus
	/// the stack trace opened rather than parked behind MORE INFO, which is the only thing on the
	/// screen anybody actually wants.
	///
	/// <b>Why a postfix and not a transpiler or a replacement prefab.</b> Klei's <c>Start</c>
	/// builds the layout, wires four buttons and fills the mod list; running it and then editing
	/// the result is a dozen lines, survives a game update that changes any of that, and cannot
	/// break the screen for a player -- which matters more than usual here, because this is the
	/// screen that shows up when something is ALREADY broken. Every step is individually guarded
	/// and the whole thing is wrapped: a crash screen that throws while decorating itself would
	/// hide the crash it exists to report.
	///
	/// <b>Inert unless a rig is running.</b> The patch is installed unconditionally and decides
	/// at dialog time, which is the only moment the question has a reliable answer: rigs arm from
	/// three different places (<see cref="RigRegistry"/>, <see cref="BlueprintRig"/>, and a rig's
	/// own flag read), some of them after this installs. With no rig armed the postfix returns
	/// having touched nothing and the player sees Klei's screen exactly as Klei wrote it.
	/// </summary>
	public static class RigCrashScreen
	{
		private static bool installed;

		/// <summary>
		/// Errors this run is going to provoke ON PURPOSE, declared by whoever provokes them.
		/// </summary>
		private static readonly List<string> expected = new List<string>();

		/// <summary>
		/// Declares that something in this run deliberately causes the game to log an error, and
		/// says why, so the crash dialog can tell a reader that the alarm is the test working.
		///
		/// <b>This is not a cosmetic nicety, it is the difference between a green run and a bug
		/// report.</b> ONI opens the crash dialog for any logged error -- <c>KCrashReporter</c>'s
		/// filter is <c>type != LogType.Exception &amp;&amp; type != LogType.Error</c>, so an
		/// <c>Error</c> is enough -- and it opens it with Klei's "Oops-a-daisy, uncheck your mods"
		/// text. The EXTREGISTRY rig hits that on a PERFECTLY GREEN RUN: it registers the same
		/// extension property twice and writes to an unregistered property index, both on
		/// purpose, because a registry that silently accepted either would be far worse than one
		/// that refuses. The refusals ARE the assertions. Without a declaration here the screen
		/// says a passing test failed.
		///
		/// A declaration never suppresses anything and never claims the error is harmless: the
		/// message still shows, the stack trace still shows, and the screen says to match the one
		/// against the other. A rig that provokes errors can still crash for real, and no static
		/// list can tell the difference -- only the reader can, which is why the list is put in
		/// front of them rather than used to hide the dialog.
		/// </summary>
		/// <param name="who">Who provokes it, e.g. <c>"selftest"</c> -- short, it prefixes a bullet.</param>
		/// <param name="why">What is provoked and what the right answer is.</param>
		public static void DeclareExpectedError(string who, string why)
		{
			if (string.IsNullOrEmpty(why))
			{
				return;
			}
			string line = string.IsNullOrEmpty(who) ? why : who + ": " + why;
			if (!expected.Contains(line))
			{
				expected.Add(line);
			}
		}

		/// <summary>
		/// Patches <c>ReportErrorDialog.Start</c>. Safe to call more than once and safe to call
		/// when the type is missing.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (installed || harmony == null)
			{
				return;
			}

			try
			{
				MethodInfo target = AccessTools.Method(typeof(ReportErrorDialog), "Start");
				if (target == null)
				{
					FrameworkLog.Warn("RigCrashScreen: ReportErrorDialog.Start not found; the "
						+ "vanilla crash screen is left alone.");
					return;
				}

				harmony.Patch(target, null,
					new HarmonyMethod(AccessTools.Method(typeof(RigCrashScreen), "Restyle")));
				installed = true;
			}
			catch (Exception e)
			{
				// Same rule as BuildStamp: a cosmetic patch may never take the mod down on load.
				FrameworkLog.Warn("RigCrashScreen: could not patch the crash dialog: " + e);
			}
		}

		/// <summary>
		/// The postfix. Runs after Klei has built the dialog, so everything it edits exists.
		/// </summary>
		private static void Restyle(ReportErrorDialog __instance)
		{
			try
			{
				string rig = ArmedRig();
				if (rig == null)
				{
					// Not an automated run. Klei's screen is the right screen.
					return;
				}

				Traverse t = Traverse.Create(__instance);
				string blob = t.Field("m_stackTrace").GetValue<string>();

				// 1. THE HEADLINE. Klei's LocText is TMP-backed and its own strings use markup
				//    (UI.CRASHSCREEN.UPLOAD_FAILED carries a <u> tag), so tags here render.
				var label = t.Field("CrashLabel").GetValue<LocText>();
				if (label != null)
				{
					label.text = Headline(rig, blob);
				}

				// 2. THE MOD LIST GOES AWAY. Hiding rather than emptying: BuildModsList has
				//    already run and already wired a toggle per mod, and an empty panel would
				//    still be a panel inviting the one action nobody should take here.
				var mods = t.Field("ModsInfo").GetValue<GameObject>();
				if (mods != null)
				{
					mods.SetActive(false);
				}

				// 3. THE STACK TRACE COMES OUT FROM BEHIND THE BUTTON, through Klei's own public
				//    method rather than a copy of it -- OnSelect_MOREINFO fills the panel, shows
				//    it, and re-labels the button to COPY TO CLIPBOARD, which is exactly the
				//    state this screen should open in.
				__instance.OnSelect_MOREINFO();

				// 4. AND THE DETAIL GOES IN THAT PANEL, not in the headline.
				//
				//    The headline sits in a fixed slot sized for Klei's five-line string, and it
				//    does not scroll or push the buttons down -- it grows straight through them
				//    and off the bottom of the window. Measured the hard way: the first version
				//    of this screen put the expected-error list up there and the last third of it
				//    was behind the button row. The panel on the left is half the dialog, was
				//    showing three lines of text, and is where a reader is already looking for
				//    detail.
				var panel = t.Field("StackTrace").GetValue<GameObject>();
				if (panel != null)
				{
					LocText body = panel.GetComponentInChildren<LocText>();
					if (body != null)
					{
						body.text = Detail(blob);
					}
				}

				// 5. THE BUTTONS SAY WHAT THEY DO TO A RUN. "Quit to desktop" is accurate and
				//    tells you nothing; this dialog is the end of a measurement, and the button
				//    that ends it should say so.
				Relabel(t.Field("quitButton").GetValue<KButton>(), "END THE RUN");
				Relabel(t.Field("continueGameButton").GetValue<KButton>(), "KEEP THE GAME OPEN");

				FrameworkLog.Info("RigCrashScreen: crash dialog re-skinned for rig " + rig + ".");
			}
			catch (Exception e)
			{
				// The one place in this project where swallowing is unambiguously right: we are
				// already inside the failure report. A half-decorated screen still shows Klei's
				// stack trace; an exception thrown from here would replace it with nothing.
				FrameworkLog.Warn("RigCrashScreen: could not re-skin the crash dialog, so it is "
					+ "showing vanilla: " + e);
			}
		}

		/// <summary>
		/// The replacement headline. FOUR LINES, and the count is a constraint rather than a
		/// preference: this text lands in a fixed slot that neither scrolls nor pushes the button
		/// row down, so anything longer runs through the buttons and off the bottom of the
		/// window. Everything that does not fit goes to <see cref="Detail"/> and the left panel.
		/// </summary>
		private static string Headline(string rig, string blob)
		{
			RigHarness.LiveStatus s = RigHarness.Status;

			string tally;
			if (s.Running)
			{
				// "0 PASS / 0 FAIL" reads as a rig that ran and asserted nothing. The rig that
				// actually reaches this screen most often -- EXTREGISTRY -- provokes its first
				// refusal before its first assertion, so this is the common case, not the edge.
				string counts = (s.Pass == 0 && s.Fail == 0)
					? "no assertions yet"
					: s.Pass + " PASS / " + s.Fail + " FAIL";
				tally = string.Format("{0}  ·  phase {1}  ·  sim {2:F1} s",
					counts, string.IsNullOrEmpty(s.Phase) ? "(none)" : s.Phase, s.SimSeconds);
			}
			else
			{
				// Armed but never started, which is itself the finding. "0 PASS / 0 FAIL" would
				// read as a rig that ran and asserted nothing -- a different and much more
				// confusing thing.
				tally = "armed, but this happened before its first assertion";
			}

			string verdictLine = expected.Count > 0
				? "<b>THIS RUN PROVOKES ERRORS ON PURPOSE</b> — the panel on the left says which."
				: FirstLines(blob, 1);

			return "<b>RIG:" + rig + "  —  "
				+ (expected.Count > 0 ? "THE GAME LOGGED AN ERROR" : "RUN ENDED ON AN EXCEPTION")
				+ "</b>\n"
				+ StampWithoutRig() + "\n"
				+ tally + "\n"
				+ verdictLine;
		}

		/// <summary>
		/// The left panel: everything the headline had no room for, ending with the stack trace
		/// Klei put there. Ordered by what a reader needs first — what this run asks for, then
		/// what the game actually said, then where the transcript is, then the trace.
		/// </summary>
		private static string Detail(string blob)
		{
			var b = new System.Text.StringBuilder();

			if (expected.Count > 0)
			{
				b.Append("EXPECTED ERRORS FOR THIS RUN\n");
				for (int i = 0; i < expected.Count; i++)
				{
					b.Append("  • ").Append(expected[i]).Append('\n');
				}
				b.Append("ONI opens this dialog for ANY logged error, one a test asked for "
					+ "included. If the message below is on that list, the run is doing what it "
					+ "was written to do.\n\n");
			}

			b.Append("WHAT THE GAME SAID\n").Append(FirstLines(blob, 4)).Append("\n\n");
			b.Append("transcript: ").Append(LogPath()).Append('\n');
			b.Append("The mod list is hidden on purpose: these mods are the experiment, and "
				+ "unchecking them is how a run stops measuring anything.\n\n");
			// Only when there IS one. A logged error carries no trace, and repeating the same
			// two sentences under a heading promising a stack trace is worse than saying there
			// isn't one -- it reads as a truncated trace and sends the reader looking for the
			// rest of it.
			if (HasStackTrace(blob))
			{
				b.Append("FULL STACK TRACE (COPY TO CLIPBOARD takes it whole)\n");
				b.Append(blob);
			}
			else
			{
				b.Append("No stack trace: the game LOGGED this, it did not throw it. "
					+ "COPY TO CLIPBOARD still takes the whole record.");
			}
			return b.ToString();
		}

		/// <summary>
		/// The build watermark without its trailing <c>RIG:</c> segment, which the headline
		/// directly above it has already said in larger type.
		/// </summary>
		private static string StampWithoutRig()
		{
			string line = BuildStamp.Line ?? string.Empty;
			int cut = line.IndexOf("RIG:", StringComparison.Ordinal);
			if (cut > 0)
			{
				line = line.Substring(0, cut);
			}
			return line.TrimEnd(' ', '|');
		}

		/// <summary>
		/// The head of the error, not just its first line.
		///
		/// One line is often not enough, and the reason is on the screenshot this was written
		/// from: the native side's refusals arrive under a <c>SimDLL Crash Dump</c> banner, so
		/// line one is the banner and line two is the only sentence that says anything. The
		/// caller picks the budget — one line for the headline's fixed slot, four for the panel —
		/// and either way this stops at the stack trace, which has its own place below.
		/// </summary>
		private static string FirstLines(string blob, int maxLines)
		{
			if (string.IsNullOrEmpty(blob))
			{
				return "(the game reported no message)";
			}

			string[] lines = blob.Replace("\r", string.Empty).Split('\n');
			var b = new System.Text.StringBuilder();
			int taken = 0;
			for (int i = 0; i < lines.Length && taken < maxLines; i++)
			{
				string line = lines[i].Trim();
				if (line.Length == 0)
				{
					continue;
				}
				// The stack trace starts here and is not part of the message.
				if (line.StartsWith("at ", StringComparison.Ordinal)
					|| line.StartsWith("UnityEngine.", StringComparison.Ordinal))
				{
					break;
				}
				if (taken > 0)
				{
					b.Append('\n');
				}
				b.Append(line);
				taken++;
			}

			string text = b.ToString();
			if (text.Length > 400)
			{
				text = text.Substring(0, 400) + "...";
			}
			return text.Length == 0 ? "(the game reported no message)" : text;
		}

		/// <summary>
		/// Whether the blob carries a real call stack, as opposed to a message the game logged.
		/// Klei composes it as <c>error + "\n\n" + stack_trace</c> and passes an empty trace for
		/// a plain <c>LogType.Error</c>, so the test is whether any line looks like a frame.
		/// </summary>
		private static bool HasStackTrace(string blob)
		{
			if (string.IsNullOrEmpty(blob))
			{
				return false;
			}
			string[] lines = blob.Replace("\r", string.Empty).Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].TrimStart();
				if (line.StartsWith("at ", StringComparison.Ordinal)
					|| line.StartsWith("UnityEngine.", StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Where the run's transcript is, asked of Unity rather than reconstructed. Naming the
		/// wrong path on a crash screen is worse than naming none.
		/// </summary>
		private static string LogPath()
		{
			try
			{
				string p = Application.consoleLogPath;
				return string.IsNullOrEmpty(p) ? "(Unity reports no log file)" : p;
			}
			catch (Exception)
			{
				return "(Unity reports no log file)";
			}
		}

		/// <summary>Retitles a button, if it exists and has a label.</summary>
		private static void Relabel(KButton button, string text)
		{
			if (button == null)
			{
				return;
			}
			LocText label = button.GetComponentInChildren<LocText>();
			if (label != null)
			{
				label.text = text;
			}
		}

		/// <summary>
		/// The rig this launch armed, or null. Three sources because rigs arm three ways, in
		/// increasing order of how much they know: the one that actually ran, the canvas that was
		/// armed for the load, and the registry's own list.
		/// </summary>
		private static string ArmedRig()
		{
			RigHarness.LiveStatus s = RigHarness.Status;
			if (s.Running && !string.IsNullOrEmpty(s.Rig))
			{
				return s.Rig;
			}
			if (!string.IsNullOrEmpty(BlueprintRig.ArmedRigTag))
			{
				return BlueprintRig.ArmedRigTag;
			}
			string[] armed = RigRegistry.Armed;
			if (armed != null && armed.Length > 0)
			{
				return armed[0];
			}
			return null;
		}
	}
}
