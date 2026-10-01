using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Arms one rig's blueprint canvas so the launch builds a world instead of loading a save.
	///
	/// WHY THIS IS A SHARED CLASS AND NOT A METHOD ON <see cref="RigHarness"/>. The arming has to
	/// happen in <c>Mod.OnLoad</c>, before <c>SaveLoader</c> has begun loading anything, and a
	/// <see cref="RigHarness"/> instance does not exist until <c>Game.OnSpawn</c> -- far too late
	/// to change which branch <c>SaveLoader.OnSpawn</c> takes. So the entry point is static and
	/// takes the rig's identity as arguments rather than reading it off an instance.
	///
	/// WHAT IT REPLACES. Every blueprint rig carried its own private copy of this: a
	/// <c>static bool armed</c>, an <c>Install</c> that checked its flag and patched
	/// <c>SaveLoader.OnSpawn</c>, and an <c>ArmBeforeLoad</c> that resolved the YAML beside the
	/// mod DLL, parsed it and called <see cref="BlueprintWorld.ArmForNextLoad"/>. Five copies of
	/// roughly fifty lines, differing only in a tag string -- which is exactly the kind of
	/// duplication that lets rigs drift apart in the ways they report and fail.
	///
	/// TWO THINGS ARE BETTER HERE THAN IN THE COPIES, not just fewer.
	///
	/// The blueprint is resolved and PARSED AT INSTALL TIME rather than inside the prefix. A
	/// missing or malformed YAML used to be discovered at <c>SaveLoader.OnSpawn</c>, where the
	/// only thing left to do was log an error and fall through to loading the save -- so the rig
	/// then measured a real colony while believing it was on its own canvas. Now it is discovered
	/// at mod load, before anything has been decided, and it is loud.
	///
	/// And two rigs cannot silently fight over the same load. One <c>SaveLoader.OnSpawn</c> patch
	/// is installed no matter how many rigs ask, and a second rig arming after a first is reported
	/// rather than quietly overwriting it.
	/// </summary>
	public static class BlueprintRig
	{
		private static bool patched;
		private static bool armed;
		private static string pendingRigTag;
		private static RigBlueprint pendingBlueprint;

		/// <summary>The rig whose canvas is armed for the next load, or null.</summary>
		public static string ArmedRigTag => pendingRigTag;

		/// <summary>
		/// Arms <paramref name="blueprintFile"/> as the world to build, if and only if
		/// <paramref name="flag"/> is on the command line.
		/// </summary>
		/// <param name="modTag">The owning mod, for log lines -- e.g. <c>"Mod1ThermoFluid"</c>.</param>
		/// <param name="rigTag">The rig's own tag, matching its <c>RigTag</c>.</param>
		/// <param name="flag">The command-line flag that selects this rig.</param>
		/// <param name="blueprintFile">Path to the YAML, relative to the mod DLL.</param>
		/// <param name="rigType">
		/// Any type from the mod's own assembly. Used only to locate that assembly on disk, so
		/// the blueprint is found beside the mod rather than beside the framework.
		/// </param>
		/// <param name="canvasSaves">
		/// OPT-IN, DEFAULT OFF: let this rig's canvas be written out as a <c>.sav</c>.
		///
		/// A canvas cannot be saved unless something restores the active save path that
		/// <see cref="BlueprintWorld.ArmForNextLoad"/> nulls on purpose, and that restore is what
		/// this switches on -- for this run only, because <c>Install</c> has already returned
		/// unless this rig's own flag is on the command line.
		///
		/// IT IS A TOOL FOR RIGS THAT TEST PERSISTENCE, and it is nobody else's default. A rig
		/// that measures and quits is rebuilt from its YAML every run, so the blueprint is the
		/// artifact and a derived binary beside it can only go stale. The claimant is
		/// <c>Mod1PipeMatterSaveTest</c>, whose deliverable IS a save: it proves KSerialization
		/// hands trapped conduit condensate back on load, across two game processes precisely so
		/// the live dictionary cannot be what answers.
		///
		/// See <see cref="RigSaves.CanvasSavesEnabled"/> for what is still unfinished behind it --
		/// turning it on is not the same as the feature being complete.
		/// </param>
		public static void Install(Harmony harmony, string modTag, string rigTag, string flag,
			string blueprintFile, Type rigType, bool canvasSaves = false)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (rigType == null)
			{
				throw new ArgumentNullException("rigType");
			}
			if (string.IsNullOrEmpty(blueprintFile))
			{
				throw new ArgumentException("a blueprint rig needs a blueprint file", "blueprintFile");
			}

			if (!RigRegistry.AnyFlag(flag))
			{
				return;
			}

			string prefix = "[" + modTag + "] " + rigTag + ": ";

			// Set before the world is built, which is the only time it can matter: BuildCanvasWorld
			// reads it while the canvas is being constructed, long before the rig's own Run().
			// Only ever raised, never lowered -- a run arms exactly one rig, and leaving a
			// previous false to overwrite a true would be a way to silently disarm the opt-in.
			if (canvasSaves)
			{
				RigSaves.CanvasSavesEnabled = true;
				Debug.Log(prefix + "canvas saves are ENABLED for this run -- this rig tests "
					+ "persistence, so its canvas world gets the save pointer a canvas normally "
					+ "does without. No other rig in this process gets one.");
			}

			string assemblyDir = Path.GetDirectoryName(rigType.Assembly.Location);
			if (string.IsNullOrEmpty(assemblyDir))
			{
				FrameworkLog.Error(prefix + "could not locate the mod assembly on disk, so "
					+ blueprintFile + " cannot be found; the launch will load the save instead.");
				return;
			}

			string path = Path.Combine(assemblyDir,
				blueprintFile.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(path))
			{
				FrameworkLog.Error(prefix + blueprintFile + " was not found beside the mod DLL "
					+ "(looked in " + assemblyDir + "); the launch will load the save instead, "
					+ "and every measurement would then be of somebody else's world.");
				return;
			}

			RigBlueprint blueprint = RigBlueprint.Load(path);
			if (blueprint == null)
			{
				FrameworkLog.Error(prefix + "failed to parse " + path + "; the launch will load the "
					+ "save instead, and every measurement would then be of somebody else's "
					+ "world.");
				return;
			}

			if (pendingBlueprint != null)
			{
				FrameworkLog.Error(prefix + "a canvas is already armed by rig " + pendingRigTag
					+ ". Two blueprint rigs cannot share one launch -- run them separately. "
					+ "Keeping " + pendingRigTag + "'s canvas.");
				return;
			}

			pendingRigTag = rigTag;
			pendingBlueprint = blueprint;

			if (!patched)
			{
				MethodInfo target = AccessTools.Method(typeof(SaveLoader), "OnSpawn");
				if (target == null)
				{
					FrameworkLog.Error(prefix + "SaveLoader.OnSpawn not found; the blueprint cannot "
						+ "be armed and the launch will load the save instead.");
					pendingRigTag = null;
					pendingBlueprint = null;
					return;
				}
				harmony.Patch(target, prefix: new HarmonyMethod(
					AccessTools.Method(typeof(BlueprintRig), nameof(ArmBeforeLoad))));
				patched = true;
			}

			Debug.Log(prefix + "armed to build " + blueprintFile + " ("
				+ blueprint.canvas.width + "x" + blueprint.canvas.height
				+ ") instead of loading a save.");
		}

		private static void ArmBeforeLoad()
		{
			if (armed || pendingBlueprint == null)
			{
				return;
			}
			armed = true;
			BlueprintWorld.ArmForNextLoad(pendingBlueprint);
		}
	}
}
