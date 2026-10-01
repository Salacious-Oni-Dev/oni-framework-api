using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Takes the SAVE FILE MIGRATION dialog off the main menu on the development install.
	///
	/// <b>What the dialog is.</b> Vanilla's, not ours. <c>MainMenu.OnSpawn</c> calls
	/// <c>LoadScreen.Instance.ShowMigrationIfNecessary(fromMainMenu: true)</c>, which counts
	/// saves sitting loose in the save root (<c>CountValidSaves(root,
	/// SearchOption.TopDirectoryOnly)</c>) plus loose autosaves, and opens a modal panel offering
	/// to move them into per-colony folders whenever that count is non-zero. It is the game
	/// tidying up after a save-layout change, and for an ordinary player it is correct and should
	/// be left alone.
	///
	/// <b>Why it is in our way.</b> The loose saves are ours: three rigs used to write straight
	/// into the save root (fixed in <see cref="RigSaves"/>). Opening the panel activates
	/// <c>LoadScreen</c>, which runs <c>WorldGen.LoadSettings</c> synchronously -- 12.3 s,
	/// measured, on the way to a menu an automated run only wants to hand off to
	/// <c>AutoResumeSaveFile</c>. A modal screen on the main menu of an unattended run is also
	/// simply the wrong thing to have.
	///
	/// <b>DEVELOPMENT INSTALL ONLY, and that is a hard gate.</b> The test is whether a
	/// <c>DevData</c> directory sits beside <c>OxygenNotIncluded_Data</c>. On a retail install this patch
	/// returns immediately and vanilla behaves exactly as shipped. Suppressing a real player's
	/// migration prompt would leave their saves stranded in a layout the game has moved on from,
	/// which is a much worse outcome than a dialog.
	///
	/// <b>It suppresses; it does not migrate, and it must not.</b> Moving those files from
	/// inside the running game would break the very runs it exists to help:
	/// <c>oni-run.sh</c> resolves <c>--save NAME</c> to an absolute path BEFORE launching and
	/// hands it to the game as <c>AutoResumeSaveFile</c>. A handler that relocated saves during
	/// <c>MainMenu.OnSpawn</c> would move the file out from under a path the game had already
	/// been told to load, turning a cosmetic dialog into a failed run. Tidying loose saves is a
	/// job for the shell, with the game closed.
	///
	/// <b>It is loud on purpose.</b> Handling something quietly and hiding it are the same
	/// action with different logging, so this names every loose file it suppressed the prompt
	/// for. A suppressed dialog that nobody can see the reason for is how the leftovers get
	/// forgotten.
	/// </summary>
	public static class SaveMigrationHandler
	{
		private static bool installed;
		private static int devInstall = -1;

		/// <summary>
		/// Whether this is the development install: a <c>DevData</c> directory beside
		/// <c>OxygenNotIncluded_Data</c>. Same test <c>env.sh</c> uses. Evaluated once.
		/// </summary>
		public static bool IsDevInstall
		{
			get
			{
				if (devInstall < 0)
				{
					devInstall = 0;
					try
					{
						DirectoryInfo root = Directory.GetParent(Application.dataPath);
						if (root != null && Directory.Exists(Path.Combine(root.FullName, "DevData")))
						{
							devInstall = 1;
						}
					}
					catch (Exception)
					{
						// Unresolvable path: treat as retail, i.e. change nothing.
					}
				}
				return devInstall == 1;
			}
		}

		/// <summary>
		/// Patches <c>LoadScreen.ShowMigrationIfNecessary</c>. Idempotent. Does nothing at all on
		/// a retail install -- the patch is not even applied there, so there is no per-boot cost
		/// and nothing to reason about on a player's machine.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (installed || harmony == null)
			{
				return;
			}

			if (!IsDevInstall)
			{
				return;
			}

			try
			{
				MethodInfo target = AccessTools.Method(typeof(LoadScreen), "ShowMigrationIfNecessary",
					new Type[] { typeof(bool) });
				if (target == null)
				{
					Debug.LogWarning("[OniFramework] SaveMigrationHandler: "
						+ "LoadScreen.ShowMigrationIfNecessary not found; the migration dialog is "
						+ "left to vanilla");
					return;
				}

				harmony.Patch(target,
					new HarmonyMethod(AccessTools.Method(typeof(SaveMigrationHandler), "Suppress")));
				installed = true;
				Debug.Log("[OniFramework] SaveMigrationHandler installed (development install); "
					+ "the save-migration prompt will be handled here instead of shown.");
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] SaveMigrationHandler: could not patch the "
					+ "migration prompt: " + e);
			}
		}

		/// <summary>
		/// Harmony prefix. Returns false to skip vanilla's body entirely, after reproducing the
		/// branch vanilla itself takes when there is nothing to migrate.
		/// </summary>
		private static bool Suppress(LoadScreen __instance, bool fromMainMenu)
		{
			try
			{
				ReportLooseSaves();

				// Exactly what vanilla does when the count is zero: from the main menu the load
				// screen is deactivated on the way past, otherwise it simply returns. Reproduced
				// rather than skipped, because leaving LoadScreen activated here is what the
				// 12.3 s WorldGen.LoadSettings hangs off.
				if (fromMainMenu && __instance != null)
				{
					__instance.Deactivate();
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] SaveMigrationHandler: " + e);
			}
			return false;
		}

		private static void ReportLooseSaves()
		{
			try
			{
				string root = SaveLoader.GetSavePrefixAndCreateFolder();
				string[] loose = Directory.GetFiles(root, "*.sav", SearchOption.TopDirectoryOnly);
				if (loose.Length == 0)
				{
					// Nothing to migrate; vanilla would have shown nothing either. Silent, so
					// the log line below always means something real.
					return;
				}

				var names = new System.Text.StringBuilder();
				for (int i = 0; i < loose.Length; i++)
				{
					names.Append(i == 0 ? " " : ", ");
					names.Append(Path.GetFileName(loose[i]));
				}

				Debug.Log("[OniFramework] SaveMigrationHandler: suppressed the SAVE FILE MIGRATION "
					+ "prompt for " + loose.Length + " loose save(s) in " + root + ":" + names
					+ ". Nothing was moved -- relocating a save while the game holds an "
					+ "AutoResumeSaveFile path to it would break the run. Tidy them from the "
					+ "shell with the game closed if they are no longer wanted; new rig saves go "
					+ "to " + RigSaves.Subfolder + "/.");
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] SaveMigrationHandler: could not list loose saves: "
					+ e.Message);
			}
		}
	}
}
