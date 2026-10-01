using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The framework's log levels, and the reason it does not use Unity's directly.
	///
	/// <b>ONI TREATS <c>Debug.LogError</c> AS A CRASH.</b> This is not a style preference, it is
	/// what the game's own code does with the call. From <c>KCrashReporter.HandleLog</c>:
	/// <code>
	///     if (!(errorScreen == null) || (type != LogType.Exception &amp;&amp; type != 0) || ...)
	///             return;                     // LogType.Error IS 0, so an error does NOT return
	///     ...
	///     SpeedControlScreen.Instance.Pause(playSound: true, isCrashed: true);
	///     ShowDialog(text2, text3);
	/// </code>
	/// and from <c>ShowDialog</c>, when any crashable mod is loaded:
	/// <code>
	///     Global.Instance.modManager.SearchForModsInStackTrace(stackTrace2);
	///     errorDialog.PopupDisableModsDialog(stackTrace, OnQuitToDesktopCrashed,
	///         (modManager.IsInDevMode() || !terminateOnError) ? OnCloseErrorDialog : null);
	/// </code>
	/// So one <c>Debug.LogError</c> anywhere in this assembly:
	/// <list type="number">
	/// <item>pauses the simulation with <c>isCrashed: true</c>;</item>
	/// <item>pops the "Oops-a-daisy" modal over whatever is on screen;</item>
	/// <item>walks the live stack and marks every mod found in it
	///   (<c>ReportErrorDialog.BuildModsList</c> then calls <c>EnableMod(label, false)</c> on
	///   each one that is not a Dev-distribution mod, so the player's mod list is edited);</item>
	/// <item>and, because <c>terminateOnError</c> defaults to true and a player's install has no
	///   Dev mod in it, offers <b>no Continue button at all</b> -- quit only.</item>
	/// </list>
	///
	/// Two properties of THIS project make that unacceptable rather than merely loud. First, we
	/// ship a replacement SimDLL, so anything that looks like our fault is read as the DLL's
	/// fault, which is the one thing the whole distribution story cannot afford. Second, this is
	/// a shared library: a third-party mod's bad argument reaches a validation branch in here,
	/// and the stack at that branch contains the caller -- so escalating <i>their</i> mistake
	/// disables <i>their</i> mod through a dialog with our name on it, for a condition this API
	/// already reported through its return value.
	///
	/// For example, <c>--ext-selftest</c> deliberately registers the same extension property twice
	/// to assert that the sim refuses it; if the refusal path logged an error, a passing run would
	/// end at that modal. A test asserting a documented refusal is not a crash.
	///
	/// <b>SO: SEVERITY TRAVELS IN THE TEXT, NOT IN THE LOG LEVEL.</b> <see cref="Error"/> writes
	/// at <c>Debug.LogWarning</c> and puts the word ERROR in the line, where a human reading the
	/// log and a tool grepping it both still see it. <c>RigDiagnostics</c> reached the same
	/// conclusion independently for its own findings; this generalises it to the whole assembly,
	/// and <c>build.sh</c> now fails the build on a <c>Debug.LogError</c> in these sources so the
	/// rule cannot be lost to the next file somebody adds.
	///
	/// <b>WHAT TO DO WHEN A CRASH REALLY IS A CRASH.</b> Throw. An exception reaches
	/// <c>KCrashReporter</c> as <c>LogType.Exception</c>, and that path builds its stack from
	/// <c>DebugUtil.RetrieveLastExceptionLogged()</c> -- the exception's OWN stack, so the mod
	/// actually at fault is the one named and disabled. A <c>Debug.LogError</c> from inside a
	/// <c>catch</c> does the opposite: by then the stack is ours, so we take the blame for a bug
	/// we contained. <c>SimExtFrame.Install</c> and <c>FrameworkVersion</c> already throw where
	/// throwing is right.
	/// </summary>
	public static class FrameworkLog
	{
		/// <summary>Every line this class writes starts with it, so one grep finds the lot.</summary>
		public const string Prefix = "[OniFramework] ";

		/// <summary>
		/// A condition that IS an error -- a refused registration, a blueprint that will not
		/// build, a version floor that cannot be met -- written at warning level for the reasons
		/// in the class summary. The caller has already been told through a return value; this is
		/// the human-readable half.
		///
		/// Pass the message WITHOUT a "[OniFramework] " prefix of its own; this adds it. A caller
		/// with its own tag (a rig, say) may still pass one -- it lands after the marker, which
		/// reads correctly: <c>[OniFramework] ERROR [MOD1] BRIDGE: ...</c>.
		/// </summary>
		public static void Error(string message)
		{
			Debug.LogWarning(Prefix + "ERROR " + message);
		}

		/// <summary>
		/// A condition worth a look that is not an error: a fallback was taken, a value was
		/// clamped, something was borrowed. Same level as <see cref="Error"/> and deliberately
		/// so -- the level carries no information here, the text does.
		/// </summary>
		public static void Warn(string message)
		{
			Debug.LogWarning(Prefix + message);
		}

		/// <summary>Ordinary progress. Plain <c>Debug.Log</c>, prefixed.</summary>
		public static void Info(string message)
		{
			Debug.Log(Prefix + message);
		}
	}
}
