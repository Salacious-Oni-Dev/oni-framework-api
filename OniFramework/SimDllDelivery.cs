using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Puts the SDK's SimDLL.dll in place for each run of the game when the framework mod carries one, and
	/// puts the game's own back when the game quits. This is how a player who installs the
	/// framework from the Steam Workshop gets the replacement library without running an
	/// installer.
	///
	/// <b>Why a file swap, and why for each run.</b> The game imports the library by the bare name
	/// <c>SimDLL</c>, but the Unity player loads plugins by their full path under
	/// <c>Plugins/x86_64</c>, so a copy loaded from anywhere else first is simply a second module
	/// the game never calls. The file in the game folder has to be ours. The game does not load it
	/// until a world starts, and the framework loads before any mod that depends on it, so at
	/// <c>OnLoad</c> the file can still be replaced. It is put back at quit so that disabling or
	/// unsubscribing the framework leaves the game as Steam installed it.
	///
	/// <b>What the mod folder carries</b>, in <c>native/</c> (a subfolder, because the game's mod
	/// loader calls Assembly.LoadFrom on every *.dll at the top of a mod folder):
	/// <c>SimDLL.dll</c>, <c>SHA256SUMS</c> (the library's SHA-256), <c>supported-builds.txt</c>
	/// (the SHA-256 of the game's own SimDLL.dll for each game build this release supports, the
	/// same file the zip installer uses) and <c>VERSION</c>. Without <c>native/SimDLL.dll</c> this
	/// class does nothing at all, which is the case for a framework built without the library.
	///
	/// <b>Files beside the game's SimDLL.dll</b>: <c>SimDLL.dll.vanilla</c>, the game's own
	/// library while ours is in place; <c>SimDLL.dll.oniframework</c>, one line, the SHA-256 and
	/// version of what this class put there; <c>SimDLL.dll.sdk-old</c>, a library renamed aside
	/// while it was loaded, deleted at the next start. Every decision is made from file hashes,
	/// never from a file merely existing: a game update or a Steam file check replaces
	/// SimDLL.dll and leaves the others behind.
	///
	/// <b>Left alone</b>: an install made by the zip installer (<c>SimDLL.dll.sdk</c> exists), one
	/// made by hand (SimDLL.dll is not a supported game build's own, SimDLL.dll.vanilla is, and
	/// there is no marker of ours), a game build not listed in supported-builds.txt, a SimDLL.dll that is neither the game's own
	/// nor one this class installed, and anything other than Windows.
	///
	/// <b>Consent</b> is asked once, in a dialog on the main menu, and kept in
	/// <c>OniFramework-simdll.txt</c> in the game's mods folder. Deleting that file asks again.
	/// </summary>
	internal static class SimDllDelivery
	{
		private const string Title = "OniFramework: simulation library";
		private const string ConsentName = "OniFramework-simdll.txt";
		private const string Accepted = "accepted";
		private const string Declined = "declined";

		private static string live;
		private static string payload;
		private static string payloadHash;
		private static string payloadVersion;
		private static Dictionary<string, string> builds;

		/// <summary>True once this run of the game put our library in place before the game loaded one.</summary>
		private static bool installedThisRun;

		private static bool askConsent;
		private static string notice;

		private enum LiveState { GameOwn, Ours, Unknown }

		internal static void Run(Harmony harmony, string modFolder)
		{
			try
			{
				string native = Path.Combine(modFolder, "native");
				payload = Path.Combine(native, "SimDLL.dll");
				if (!File.Exists(payload))
					return;
				if (Environment.OSVersion.Platform != PlatformID.Win32NT)
				{
					Notice("The SDK simulation library runs on Windows only. The game's own library is in use, so " +
						"mods that need the SDK library run in a limited mode or not at all.");
					HookMainMenu(harmony);
					return;
				}

				live = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "x86_64"), "SimDLL.dll");
				CleanUpRenamed();

				if (File.Exists(live + ".sdk"))
				{
					FrameworkLog.Info("SimDLL delivery: the zip installer manages this game's SimDLL.dll " +
						"(SimDLL.dll.sdk is present), so the framework leaves it alone");
					return;
				}

				if (!ReadPayload(native))
				{
					HookMainMenu(harmony);
					return;
				}

				if (ReplacedByHand())
				{
					FrameworkLog.Info("SimDLL delivery: SimDLL.dll was replaced by hand (SimDLL.dll.vanilla is the " +
						"game's own and neither the framework nor the zip installer put it there), so the framework " +
						"leaves it alone");
					return;
				}

				string consent = ReadConsent();
				LiveState state = Classify();
				FrameworkLog.Info("SimDLL delivery: bundled library " + payloadVersion + " (" + Short(payloadHash) +
					"), game library " + state + ", consent " + (consent ?? "not asked"));

				if (consent != Accepted)
				{
					// A library of ours still in place means the last run of the game ended without quitting
					// cleanly. Without consent it must not stay.
					if (state == LiveState.Ours)
						Restore();
					if (consent == null)
					{
						askConsent = true;
						HookMainMenu(harmony);
					}
					return;
				}

				if (!TryInstall(state))
				{
					HookMainMenu(harmony);
					return;
				}
				Application.quitting += OnQuit;
			}
			catch (Exception e)
			{
				// Delivery is a convenience; it must never stop the framework itself loading.
				FrameworkLog.Warn("SimDLL delivery: failed: " + e);
			}
		}

		// ------------------------------------------------------------- the swap

		/// <summary>Puts the bundled library in place until the game quits. False, with a notice set,
		/// when it cannot or must not.</summary>
		private static bool TryInstall(LiveState state)
		{
			if (state == LiveState.Unknown)
			{
				Notice("The game's SimDLL.dll is not the game's own library for a version this release of " +
					"OniFramework supports, and OniFramework did not put it there, so it is left alone and the " +
					"SDK library is not used. Mods that need the SDK library run in a limited mode or not at all. " +
					"After a game update, an update to OniFramework for the new version fixes this. If another " +
					"tool replaced SimDLL.dll, undo that to let OniFramework provide the SDK library.");
				return false;
			}
			if (IsLoaded())
			{
				Notice("The game had already loaded its simulation library before OniFramework started, so the " +
					"SDK library cannot be used until the game restarts. Move OniFramework to the top of the mod list.");
				return false;
			}
			try
			{
				Install(state);
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("SimDLL delivery: install failed: " + e);
				Notice("OniFramework could not put the SDK simulation library in place (" + e.Message + "). The game's " +
					"own library is in use until the game restarts.");
				return false;
			}
			installedThisRun = true;
			FrameworkLog.Info("SimDLL delivery: the SDK library " + payloadVersion + " is in place until the game quits");
			return true;
		}

		/// <summary>Moves the game's own library to SimDLL.dll.vanilla (unless ours is already in
		/// place from a run that did not quit cleanly) and copies the bundled one in, checked
		/// by hash before it takes the live name.</summary>
		private static void Install(LiveState state)
		{
			string backup = live + ".vanilla";
			if (state == LiveState.GameOwn)
			{
				if (File.Exists(backup))
					File.Delete(backup);
				File.Move(live, backup);
			}
			else if (Hash(live) == payloadHash)
			{
				WriteMarker();
				return;
			}
			else
			{
				MoveAside(live);
			}

			string temp = live + ".tmp";
			if (File.Exists(temp))
				File.Delete(temp);
			File.Copy(payload, temp);
			string got = Hash(temp);
			if (got != payloadHash)
			{
				File.Delete(temp);
				// Put the game's own back before giving up, so a failed copy never leaves no library.
				if (!File.Exists(live) && File.Exists(backup))
					File.Move(backup, live);
				throw new IOException("the copied library has SHA-256 " + got + ", expected " + payloadHash);
			}
			File.Move(temp, live);
			WriteMarker();
		}

		/// <summary>Puts the game's own library back. Works while ours is loaded: a loaded DLL can
		/// be renamed, though not deleted or overwritten.</summary>
		private static void Restore()
		{
			string backup = live + ".vanilla";
			if (!File.Exists(backup) || !builds.ContainsKey(Hash(backup)))
			{
				FrameworkLog.Warn("SimDLL delivery: cannot restore the game's own library: SimDLL.dll.vanilla is " +
					"missing or is not a supported game build. Steam's 'Verify integrity of game files' restores it.");
				return;
			}
			MoveAside(live);
			File.Move(backup, live);
			DeleteQuietly(live + ".oniframework");
			FrameworkLog.Info("SimDLL delivery: restored the game's own library");
		}

		private static void OnQuit()
		{
			if (!installedThisRun)
				return;
			installedThisRun = false;
			try
			{
				Restore();
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("SimDLL delivery: restore at quit failed; the next start restores or reuses it: " + e);
			}
		}

		/// <summary>What SimDLL.dll is now. When it is the game's own again while our marker is still
		/// there, a game update or a Steam file check replaced ours, and the old marker and backup
		/// are cleared. With an unknown SimDLL.dll nothing is touched: SimDLL.dll.vanilla may then be
		/// the only copy of the game's own library.</summary>
		private static LiveState Classify()
		{
			if (!File.Exists(live))
				return LiveState.Unknown;
			string hash = Hash(live);
			if (builds.ContainsKey(hash))
			{
				ClearStale();
				return LiveState.GameOwn;
			}
			string marker = ReadMarker();
			string backup = live + ".vanilla";
			if (marker != null && marker == hash && File.Exists(backup) && builds.ContainsKey(Hash(backup)))
				return LiveState.Ours;
			return LiveState.Unknown;
		}

		/// <summary>SimDLL.dll swapped by hand, as the install guide describes: the game's own
		/// library, of a supported build, kept as SimDLL.dll.vanilla, something else in place, and no
		/// marker of ours. The player chose that library, so it is not replaced or asked about.</summary>
		private static bool ReplacedByHand()
		{
			string backup = live + ".vanilla";
			return File.Exists(live) && File.Exists(backup) && ReadMarker() == null &&
				!builds.ContainsKey(Hash(live)) && builds.ContainsKey(Hash(backup));
		}

		private static void ClearStale()
		{
			if (ReadMarker() == null)
				return;
			// The marker proves the backup is ours; without it, SimDLL.dll.vanilla is left alone.
			DeleteQuietly(live + ".vanilla");
			DeleteQuietly(live + ".oniframework");
			FrameworkLog.Info("SimDLL delivery: SimDLL.dll was replaced since the framework last ran (a game " +
				"update or a Steam file check); cleared the framework's old backup");
		}

		private static void CleanUpRenamed()
		{
			string old = live + ".sdk-old";
			if (File.Exists(old))
				DeleteQuietly(old);
		}

		private static void MoveAside(string path)
		{
			string old = live + ".sdk-old";
			if (File.Exists(old))
				File.Delete(old);
			File.Move(path, old);
			DeleteQuietly(old);
		}

		// ------------------------------------------------------------- the main-menu dialogs

		private static bool hooked;

		private static void HookMainMenu(Harmony harmony)
		{
			if (hooked)
				return;
			hooked = true;
			harmony.Patch(AccessTools.Method(typeof(Manager), nameof(Manager.Report)),
				postfix: new HarmonyMethod(typeof(SimDllDelivery), nameof(AfterReport)));
		}

		private static void AfterReport(GameObject parent)
		{
			try
			{
				if (notice != null)
				{
					string text = notice;
					notice = null;
					Manager.Dialog(parent, Title, text);
				}
				if (askConsent)
				{
					askConsent = false;
					Manager.Dialog(parent, Title, ConsentText(), "Use the SDK library", () => OnAnswer(parent, true),
						"Keep the game's own", () => OnAnswer(parent, false));
				}
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("SimDLL delivery: could not show the dialog: " + e.Message);
			}
		}

		private static string ConsentText()
		{
			return "OniFramework carries the SDK's simulation library, an expanded replacement for the game's " +
				"SimDLL.dll. Mods built on OniFramework need it; without it they run in a limited mode or not at all.\n\n" +
				"If you accept, then each time the game starts OniFramework renames the game's own SimDLL.dll to " +
				"SimDLL.dll.vanilla and puts the SDK library (version " + payloadVersion + ", checked by SHA-256) in " +
				"its place, and when the game quits it puts the game's own file back. It only does this on a game " +
				"version this release supports. The library's source code is public.\n\n" +
				"Saves made while the SDK library is in use can hold data the game's own library cannot read. " +
				"Keep OniFramework enabled for those saves.\n\n" +
				"To change your answer later, delete " + ConsentName + " from the game's mods folder.";
		}

		private static void OnAnswer(GameObject parent, bool accept)
		{
			try
			{
				WriteConsent(accept ? Accepted : Declined);
				FrameworkLog.Info("SimDLL delivery: player " + (accept ? "accepted" : "declined"));
				if (!accept)
					return;
				LiveState state = Classify();
				if (!IsLoaded())
				{
					if (TryInstall(state))
						Application.quitting += OnQuit;
					else if (notice != null)
						AfterReport(parent);
					return;
				}
				// Something already loaded the game's library in this run. Put ours in place for the
				// next start and offer the restart; no restore at quit, or it would undo this.
				if (state == LiveState.Unknown)
				{
					TryInstall(state);
					AfterReport(parent);
					return;
				}
				Install(state);
				FrameworkLog.Info("SimDLL delivery: the SDK library is in place for the next start");
				Manager.Dialog(parent, Title, "The SDK library is in place and is used from the next start. The game " +
					"needs to restart once.", "Restart now", Restart, "Later", () => { });
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("SimDLL delivery: acting on the answer failed: " + e);
			}
		}

		private static void Restart()
		{
			// The same restart the load-order repair uses: Restarter.exe relaunches once this process
			// has exited. Quitting normally would run the restore at quit, which is not wanted here.
			var self = System.Diagnostics.Process.GetCurrentProcess();
			string exe = Path.GetFullPath(self.MainModule.FileName);
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
				Path.Combine(Path.GetDirectoryName(exe), "Restarter.exe"))
			{
				UseShellExecute = true,
				CreateNoWindow = true,
				Arguments = "\"" + exe + "\""
			});
			self.Kill();
		}

		private static void Notice(string text)
		{
			notice = text;
			FrameworkLog.Warn("SimDLL delivery: " + text);
		}

		// ------------------------------------------------------------- files

		private static bool ReadPayload(string native)
		{
			string sums = Path.Combine(native, "SHA256SUMS");
			string list = Path.Combine(native, "supported-builds.txt");
			if (!File.Exists(sums) || !File.Exists(list))
			{
				Notice("OniFramework's native folder is incomplete (SHA256SUMS or supported-builds.txt is missing), " +
					"so the SDK simulation library is not used. Reinstall OniFramework.");
				return false;
			}
			payloadHash = null;
			foreach (string line in File.ReadAllLines(sums))
			{
				string[] f = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
				if (f.Length == 2 && f[1] == "SimDLL.dll")
					payloadHash = f[0].ToLowerInvariant();
			}
			string actual = Hash(payload);
			if (payloadHash == null || actual != payloadHash)
			{
				Notice("OniFramework's copy of the SDK simulation library does not match its recorded SHA-256, so it " +
					"is not used. Reinstall OniFramework.");
				return false;
			}
			builds = new Dictionary<string, string>();
			foreach (string line in File.ReadAllLines(list))
			{
				string t = line.Trim();
				if (t.Length == 0 || t[0] == '#')
					continue;
				string[] f = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (f.Length >= 2)
					builds[f[0].ToLowerInvariant()] = f[1];
			}
			string version = Path.Combine(native, "VERSION");
			payloadVersion = File.Exists(version) ? File.ReadAllText(version).Trim() : "unknown";
			return true;
		}

		private static string ConsentPath()
		{
			return Path.Combine(Manager.GetDirectory(), ConsentName);
		}

		private static string ReadConsent()
		{
			string path = ConsentPath();
			if (!File.Exists(path))
				return null;
			foreach (string line in File.ReadAllLines(path))
			{
				string t = line.Trim().ToLowerInvariant();
				if (t == Accepted || t == Declined)
					return t;
			}
			return null;
		}

		private static void WriteConsent(string answer)
		{
			File.WriteAllText(ConsentPath(), answer + "\r\n" +
				"# OniFramework's answer to using the SDK simulation library. Delete this file to be asked again.\r\n");
		}

		private static string ReadMarker()
		{
			string path = live + ".oniframework";
			if (!File.Exists(path))
				return null;
			string[] f = File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			return f.Length > 0 ? f[0].ToLowerInvariant() : null;
		}

		private static void WriteMarker()
		{
			File.WriteAllText(live + ".oniframework", payloadHash + " " + payloadVersion + "\r\n");
		}

		private static void DeleteQuietly(string path)
		{
			try
			{
				if (File.Exists(path))
					File.Delete(path);
			}
			catch (Exception)
			{
				// A file still loaded by this process cannot be deleted; the next start tries again.
			}
		}

		private static string Hash(string path)
		{
			using (var sha = SHA256.Create())
			using (var s = File.OpenRead(path))
			{
				var sb = new StringBuilder(64);
				foreach (byte b in sha.ComputeHash(s))
					sb.Append(b.ToString("x2"));
				return sb.ToString();
			}
		}

		private static string Short(string hash)
		{
			return hash != null && hash.Length > 8 ? hash.Substring(0, 8) : hash;
		}

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW(string name);

		/// <summary>Whether this process has loaded a SimDLL.dll already. Throws off Windows.</summary>
		internal static bool IsLoaded()
		{
			return GetModuleHandleW("SimDLL.dll") != IntPtr.Zero;
		}
	}
}
