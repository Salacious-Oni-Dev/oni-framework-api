using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using KMod;

namespace OniFramework
{
	/// <summary>
	/// The framework's mod entry point. It puts the SDK's SimDLL in place when the mod carries one
	/// (<see cref="SimDllDelivery"/>), applies the framework's attribute patches, as the game's
	/// default entry point does, stamps the build watermark (<see cref="BuildStamp"/>), then
	/// checks the mod load order.
	/// </summary>
	internal sealed class FrameworkMod : UserMod2
	{
		public override void OnLoad(Harmony harmony)
		{
			// First, before anything here can call into SimDLL and so make the game load its own.
			SimDllDelivery.Run(harmony, mod.ContentPath);
			base.OnLoad(harmony);
			// The framework decides which simulation library runs, so it says which one did.
			BuildStamp.Install(harmony);
			LoadOrderRepair.Check(harmony, mod);
			// Does nothing unless the player has put a sim-tunables.json beside the framework.
			SimTunablesConfig.Load(harmony, mod.ContentPath);
		}
	}

	/// <summary>
	/// Moves the framework above any mod that depends on it and restarts the game once, telling
	/// the player before and after.
	///
	/// The game loads mods strictly in list order and resolves no assembly from another mod's
	/// folder, so a mod that references OniFramework and is listed above it fails to load, and
	/// its half-loaded assembly then breaks the game's startup. A newly installed framework is
	/// appended to the end of the list, below mods installed with it, so this is the normal
	/// first-install order. The failed assembly cannot be recovered in the same process: the
	/// runtime keeps a type that failed to load failed, so the repair needs a restart.
	///
	/// When the framework loads, every mod above it has already loaded or failed. Any assembly
	/// in memory that references OniFramework and lives in one of those mods' folders is a
	/// dependent in the wrong place. If there is none, nothing is patched. Otherwise a postfix on
	/// <c>KMod.Manager.Load</c> runs on the game's next call to it, after the game has disabled
	/// the failed mods: it re-enables them, moves the framework above the first of them, clears
	/// the game's loading-in-progress flag (a restart with it set starts the game in mod safe
	/// mode), saves the mod list, shows a message box and restarts the game. A marker file in
	/// the game's mods folder carries the list of moved mods across the restart, and the next
	/// start shows it in a dialog on the main menu.
	/// </summary>
	internal static class LoadOrderRepair
	{
		private const string Title = "OniFramework: mod load order";
		private const string MarkerName = "OniFramework-load-order.txt";

		private static List<Mod> misplaced;
		private static Mod framework;
		private static bool repaired;
		private static string reportText;

		internal static void Check(Harmony harmony, Mod self)
		{
			try
			{
				ShowReportAfterRestart(harmony);

				var manager = Global.Instance != null ? Global.Instance.modManager : null;
				if (manager == null || self == null)
					return;
				int selfIndex = manager.mods.IndexOf(self);
				if (selfIndex <= 0)
					return;

				var found = FindDependentsAbove(manager.mods, selfIndex);
				if (found.Count == 0)
					return;

				misplaced = found;
				framework = self;
				harmony.Patch(AccessTools.Method(typeof(Manager), nameof(Manager.Load)),
					postfix: new HarmonyMethod(typeof(LoadOrderRepair), nameof(AfterLoad)));
				FrameworkLog.Warn("load order: " + Titles(found, ", ") + " depend on OniFramework but " +
					"are listed above it; the order is repaired and the game restarts after mods load");
			}
			catch (Exception e)
			{
				// The repair is a convenience; it must never stop the framework itself loading.
				FrameworkLog.Warn("load order: check failed: " + e);
			}
		}

		private static List<Mod> FindDependentsAbove(List<Mod> mods, int selfIndex)
		{
			string ours = typeof(LoadOrderRepair).Assembly.GetName().Name;
			var dependentDirs = new List<string>();
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (asm == typeof(LoadOrderRepair).Assembly || asm.IsDynamic)
					continue;
				string location;
				AssemblyName[] refs;
				try
				{
					location = asm.Location;
					refs = asm.GetReferencedAssemblies();
				}
				catch (Exception)
				{
					continue;
				}
				if (string.IsNullOrEmpty(location))
					continue;
				foreach (var r in refs)
				{
					if (string.Equals(r.Name, ours, StringComparison.OrdinalIgnoreCase))
					{
						dependentDirs.Add(Normalize(Path.GetDirectoryName(location)));
						break;
					}
				}
			}

			var found = new List<Mod>();
			if (dependentDirs.Count == 0)
				return found;
			for (int i = 0; i < selfIndex; i++)
			{
				var m = mods[i];
				if (!m.IsEnabledForActiveDlc())
					continue;
				string root = Normalize(m.ContentPath);
				foreach (var dir in dependentDirs)
				{
					if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					{
						found.Add(m);
						break;
					}
				}
			}
			return found;
		}

		private static void AfterLoad(Manager __instance)
		{
			if (repaired || misplaced == null)
				return;
			repaired = true;
			try
			{
				var mods = __instance.mods;
				int first = int.MaxValue;
				foreach (var m in misplaced)
				{
					m.SetEnabledForActiveDlc(true);
					m.Uncrash();
					first = Math.Min(first, mods.IndexOf(m));
				}
				int selfIndex = mods.IndexOf(framework);
				if (first < 0 || selfIndex < 0 || first > selfIndex)
					return;
				mods.RemoveAt(selfIndex);
				mods.Insert(first, framework);
				// Saves the list with the flag cleared, so the restart is a normal start.
				__instance.SetModLoadingInProgress(false);
				FrameworkLog.Warn("load order: moved OniFramework above " + Titles(misplaced, ", ") +
					" and re-enabled them; restarting");

				string list = Titles(misplaced, "\n    ");
				WriteMarker(list);
				MessageBox("These mods need OniFramework, but were listed above it in the mod list, " +
					"so the game could not load them:\n\n    " + list + "\n\n" +
					"OniFramework has been moved above them. The game will now restart once to load " +
					"them in the right order.\n\nThis is not a crash, and it will not happen again " +
					"unless the mod order changes.");
				Restart();
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("load order: repair failed: " + e);
			}
		}

		// ------------------------------------------------------------- after the restart

		private static string MarkerPath()
		{
			return Path.Combine(Manager.GetDirectory(), MarkerName);
		}

		private static void WriteMarker(string list)
		{
			try
			{
				File.WriteAllText(MarkerPath(), list);
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("load order: could not write " + MarkerPath() + ": " + e.Message);
			}
		}

		/// <summary>If the previous start repaired the order, shows what it did once the main
		/// menu reports mod events. The marker is removed first, so the dialog shows once.</summary>
		private static void ShowReportAfterRestart(Harmony harmony)
		{
			string path = MarkerPath();
			if (!File.Exists(path))
				return;
			string list = File.ReadAllText(path).Trim();
			File.Delete(path);
			if (list.Length == 0)
				return;
			reportText = "OniFramework found mods that depend on it listed above it in the mod list, " +
				"so they could not load. It moved itself above them and restarted the game once:\n\n    " +
				list + "\n\nThey are loaded now. Keep OniFramework above any mod that uses it.";
			harmony.Patch(AccessTools.Method(typeof(Manager), nameof(Manager.Report)),
				postfix: new HarmonyMethod(typeof(LoadOrderRepair), nameof(AfterReport)));
		}

		private static void AfterReport(UnityEngine.GameObject parent)
		{
			if (reportText == null)
				return;
			string text = reportText;
			reportText = null;
			try
			{
				Manager.Dialog(parent, Title, text);
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("load order: could not show the repair dialog: " + e.Message);
			}
		}

		// ------------------------------------------------------------- helpers

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

		[DllImport("user32.dll")]
		private static extern IntPtr GetActiveWindow();

		/// <summary>A native message box, because the repair runs before the game has any UI.
		/// It blocks until the player dismisses it.</summary>
		private static void MessageBox(string text)
		{
			const uint MB_OK = 0x0, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
			try
			{
				MessageBoxW(GetActiveWindow(), text, Title, MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST);
			}
			catch (Exception e)
			{
				// Not on Windows, or user32 is unavailable: the log line and the dialog after the
				// restart still say what happened.
				FrameworkLog.Warn("load order: could not show the message box: " + e.Message);
			}
		}

		/// <summary>Starts the game's Restarter, which relaunches the executable once this process
		/// exits, then ends this process at once. The game's own <c>App.Restart</c> quits at the
		/// end of the frame, and until then startup carries on over the dependents' half-loaded
		/// assemblies and raises the crash screen. The mod list is already saved.</summary>
		private static void Restart()
		{
			var self = Process.GetCurrentProcess();
			string exe = Path.GetFullPath(self.MainModule.FileName);
			Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(exe), "Restarter.exe"))
			{
				UseShellExecute = true,
				CreateNoWindow = true,
				Arguments = "\"" + exe + "\""
			});
			self.Kill();
		}

		private static string Normalize(string path)
		{
			string full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
			return full.EndsWith(Path.DirectorySeparatorChar.ToString()) ? full : full + Path.DirectorySeparatorChar;
		}

		private static string Titles(List<Mod> mods, string separator)
		{
			var names = new List<string>();
			foreach (var m in mods)
				names.Add(m.title);
			return string.Join(separator, names.ToArray());
		}
	}
}
