using System.IO;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Where a rig writes a save it builds, and why it is not where a rig used to write one.
	///
	/// <b>The bug this closes.</b> Three rigs built saves with
	/// <c>Path.Combine(SaveLoader.GetSavePrefixAndCreateFolder(), Name + ".sav")</c>. That helper
	/// returns the save ROOT, not a colony folder, so every save they produced landed loose in
	/// the root -- and a loose save is exactly what the game calls an unmigrated one.
	/// <c>LoadScreen.ShowMigrationIfNecessary</c> counts them with
	/// <c>CountValidSaves(root, SearchOption.TopDirectoryOnly)</c> on every boot from the main
	/// menu, and a non-zero count opens the SAVE FILE MIGRATION panel. So three rigs were
	/// silently arranging for a modal dialog to greet every launch, including every later run of
	/// every other rig.
	///
	/// <b>It was not only a dialog.</b> Opening that panel activates <c>LoadScreen</c>, which
	/// runs <c>WorldGen.LoadSettings</c> synchronously -- about 12 s on a typical install, paid
	/// on every launch while a loose save sits in the root.
	///
	/// <b>Why a subfolder of our own rather than an existing one.</b> <c>save_files/mod1/</c>
	/// already holds older, same-named copies of two of those three saves. Writing into it would
	/// have overwritten them, and writing beside them would have left two files with one name --
	/// which matters because <c>oni-run.sh</c> resolves <c>--save NAME</c> by searching, and a
	/// search that finds two answers picks one of them. A dedicated folder keeps rig output in
	/// one predictable place and destroys nothing that was already there.
	///
	/// <b>This does not move anything.</b> Saves already written stay where they are; see
	/// <see cref="SaveMigrationHandler"/> for why moving them from inside the running game would
	/// be actively dangerous.
	/// </summary>
	public static class RigSaves
	{
		/// <summary>
		/// Subfolder of the save root that rig-built saves live in.
		/// </summary>
		public const string Subfolder = "rigs";

		/// <summary>
		/// Whether a blueprint canvas may be written out as a <c>.sav</c>. <b>OFF by default, and
		/// raised per rig</b> by <c>BlueprintRig.Install(..., canvasSaves: true)</c> at the arming
		/// site -- so it is visible next to the blueprint that rig runs on, and it is never on for a
		/// run that did not ask.
		///
		/// <b>Why opt-in rather than simply available.</b> A rig is rebuilt from its blueprint every
		/// run. The YAML is the artifact under version control; a <c>.sav</c> beside it is derived,
		/// and derived binaries go stale against their source without saying so. So a rig that
		/// measures and quits has no business writing one, and the default says that rather than
		/// leaving it to each rig's judgement. This is a TOOL FOR RIGS THAT TEST PERSISTENCE.
		///
		/// <b>Its claimant.</b> <c>Mod1PipeMatterSaveTest</c> (PIPESAVE), whose deliverable IS a save:
		/// it proves KSerialization hands trapped conduit condensate back on load, across two game
		/// processes precisely so that the live dictionary cannot be the thing that answers -- the
		/// "oracle shares the bug" failure. No runtime rebuild
		/// can stand in for that claim.
		///
		/// <b>What it gates.</b> Two halves of one feature, useless apart: <see cref="BlueprintWorld"/>
		/// restoring the active save path that <c>ArmForNextLoad</c> nulls on purpose, and a rig
		/// actually calling <c>SaveLoader.Save</c>. Both read this flag.
		///
		/// <b>What is still unfinished behind it</b>, so that "on" is not read as "finished": the save
		/// is written and re-loads to <c>-- GAME --</c> with no exceptions, and that is the whole
		/// proven claim. Its colony-preview thumbnail renders BLACK, <c>Timelapser.Render</c> throws on
		/// a null <c>OverlayScreen.Instance</c> on every canvas, and nothing yet loads a canvas-built
		/// save back and asserts on its contents.
		/// </summary>
		public static bool CanvasSavesEnabled = false;

		/// <summary>
		/// The rig save folder, created if it does not exist. Falls back to the save root if the
		/// folder cannot be created, which restores the old behaviour rather than losing a save
		/// a rig has already spent a run building.
		/// </summary>
		public static string Folder()
		{
			string root = SaveLoader.GetSavePrefixAndCreateFolder();
			try
			{
				string folder = Path.Combine(root, Subfolder);
				if (!Directory.Exists(folder))
				{
					Directory.CreateDirectory(folder);
				}
				return folder;
			}
			catch (System.Exception e)
			{
				Debug.LogWarning("[OniFramework] RigSaves: could not create " + Subfolder
					+ "/ under " + root + "; falling back to the save root, which will make the "
					+ "game offer to migrate it. " + e.Message);
				return root;
			}
		}

		/// <summary>
		/// Full path for a rig-owned file by name, extension included -- a save
		/// (<c>"Mod1PipeSave.sav"</c>) or a sidecar written beside it
		/// (<c>"Mod1PipeSave.pipematter.txt"</c>). A sidecar has to follow its save into the same
		/// folder or the half of the rig that reads it back looks in the wrong place.
		///
		/// The game writes its own <c>.png</c> thumbnail beside a save; that one is not ours and
		/// belongs here too. A screenshot a rig CAPTURES is a different thing -- see
		/// <see cref="ScreenshotPathFor"/>.
		/// </summary>
		public static string PathFor(string fileName)
		{
			return Path.Combine(Folder(), fileName);
		}

		/// <summary>
		/// Subfolder of <see cref="Folder"/> that rig-captured screenshots live in.
		/// </summary>
		public const string ScreenshotSubfolder = "screenshots";

		/// <summary>
		/// Full path for a screenshot a rig captures.
		///
		/// <b>Why these moved.</b> Five demos wrote their <c>.png</c> straight into the save root
		/// with <c>Path.Combine(SaveLoader.GetSavePrefixAndCreateFolder(), name)</c>. That was
		/// harmless in the sense that mattered -- the game counts <c>.sav</c> when it decides to
		/// offer migration, not <c>.png</c>, so these never opened the dialog. It was not harmless
		/// in the sense that bit us: it left the exact bad idiom modelled five times in the tree
		/// for the next rig to copy, and one string literal separated a copy of it from a save in
		/// the root. Removing the model is the point; a tidy root is the side effect.
		///
		/// <c>ScreenCapture.CaptureScreenshot</c> will not create a missing directory and fails
		/// silently when one is, so the folder is created here rather than assumed.
		/// </summary>
		public static string ScreenshotPathFor(string fileName)
		{
			string folder = Path.Combine(Folder(), ScreenshotSubfolder);
			try
			{
				if (!Directory.Exists(folder))
				{
					Directory.CreateDirectory(folder);
				}
			}
			catch (System.Exception e)
			{
				Debug.LogWarning("[OniFramework] RigSaves: could not create " + ScreenshotSubfolder
					+ "/ under " + folder + "; falling back to the rig folder. " + e.Message);
				return PathFor(fileName);
			}
			return Path.Combine(folder, fileName);
		}

		/// <summary>
		/// Root of the run-recording tree, <c>save_files/recordings/</c>.
		///
		/// <b>Why this one is NOT under <see cref="Folder"/> with everything else.</b> It predates
		/// the rig folder, video tooling expects <c>save_files/recordings</c>, and archived
		/// recordings already live there. Moving it would break that and orphan them to tidy a
		/// directory that was never the
		/// problem -- the bug class here is a loose FILE in the save root, and this has always
		/// been a subdirectory.
		///
		/// It goes through RigSaves anyway so that <c>GetSavePrefixAndCreateFolder</c> has exactly
		/// one caller in the whole codebase, which lets a check forbid the idiom outright instead of
		/// pattern-matching the dangerous half of it.
		/// </summary>
		public static string RecordingsFolder()
		{
			return Path.Combine(SaveLoader.GetSavePrefixAndCreateFolder(), "recordings");
		}
	}
}
