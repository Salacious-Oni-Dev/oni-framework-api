using System;
using System.Collections.Generic;
using System.Reflection;
using Delaunay.Geo;
using HarmonyLib;
using Klei;
using Klei.CustomSettings;
using ProcGen;
using ProcGenGame;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Generates a world directly from a <see cref="RigBlueprint"/>, with no procedural worldgen,
	/// no save file, and no <c>WorldGenDataSave.worldgen</c> on disk.
	///
	/// HOW THIS WORKS, from the game's own code.
	/// <c>SaveLoader.OnSpawn</c> has exactly two branches: load a save, or -- when there is no
	/// active save file path -- call <c>LoadFromWorldGen()</c>. That second one is far narrower
	/// than its name suggests. It does not generate anything. It reads ONE file and hands the
	/// bytes to the sim:
	///
	///     reader   = new FastReader(File.ReadAllBytes(WorldGen.WORLDGEN_SAVE_FILENAME));
	///     m_cluster = Cluster.Load(reader);              // sizes, offsets
	///     m_cluster.LoadClusterSim(pooledList, reader);  // one SimSaveFileStructure per world
	///     GridSettings.Reset(...); Sim.AllocateCells(...);
	///     foreach (item in pooledList) Sim.Load(new FastReader(item.Sim));   // ALL the terrain
	///
	/// Procedural generation lives entirely UPSTREAM of that, in <c>Cluster.BeginGeneration</c>,
	/// whose only product is that file. So this class replaces the whole branch and skips both.
	///
	/// WHY THERE IS NO BLOB AND NO FILE. That byte blob exists only because vanilla worldgen runs
	/// in a SEPARATE HEADLESS SIM -- <c>WorldGenSimUtil.DoSettleSim</c> initialises one, settles it
	/// 500 steps, and <c>Sim.Save</c>s the result for the game process to <c>Sim.Load</c> back. It
	/// is a hand-off between two sims. We are already in-process at the moment the world is
	/// created, so there is nothing to hand off: <c>SimMessages.SimDataInitializeFromCells</c>
	/// seeds the live sim from three plain arrays directly. That is the same message worldgen uses
	/// to prime its own sim, and its <c>headless</c> parameter is just a byte in the payload --
	/// the game itself passes 0 in normal play.
	///
	/// THIS ALSO AVOIDS A HAZARD OF THE MULTI-BLOB LOAD PATH. <c>LoadFromWorldGen</c> allocates
	/// the full cluster and then sends ONE Load-shaped blob PER WORLD, each sized to its own
	/// smaller region (the replacement SimDLL's <c>World::LoadIntoOffset</c>), so a naive
	/// <c>FromBlob</c> would reallocate the grid down to the second world's size and
	/// <c>Grid.InitializeCells</c> would run off the end of the undersized array. That entire hazard
	/// belongs to the multi-blob load path. One in-memory canvas never enters it.
	///
	/// OPT-IN, ALWAYS. OniFramework must never patch anything merely by being installed (see
	/// Mod1ThermoFluid's Mod.cs for the same rule applied to the heat rules), so
	/// <see cref="Install"/> is an explicit call, and even once patched the prefix does nothing
	/// until <see cref="Pending"/> is set. With no blueprint pending, vanilla's own
	/// <c>LoadFromWorldGen</c> runs untouched.
	/// </summary>
	public static class BlueprintWorld
	{
		/// <summary>
		/// The blueprint the next world generation will build. Set it before the backend scene
		/// loads (see <see cref="ArmForNextLoad"/>); null means "let vanilla do whatever it was
		/// going to do".
		/// </summary>
		public static RigBlueprint Pending { get; set; }

		/// <summary>
		/// Set once a canvas has actually been built, so a rig can assert it is running on its own
		/// blueprint rather than on whatever save was lying around. Cleared by
		/// <see cref="ArmForNextLoad"/>.
		/// </summary>
		public static RigBlueprint Active { get; private set; }

		/// <summary>
		/// The world yaml the canvas borrows its <c>WorldGenSettings</c> from. Nothing is generated
		/// from it -- the size, every cell and the start position all come from the blueprint --
		/// but <c>WorldGen</c>'s constructor insists on a name that <c>SettingsCache</c> knows.
		///
		/// IT MUST BE A NAME THE CACHE ACTUALLY HOLDS, WHICH IS NOT THE SAME AS A FILE THAT EXISTS.
		/// This was `worlds/TinyEmpty` first, chosen off the worldgen directory listing because it
		/// is vanilla's smallest and emptiest world. The first live run of this path died on it:
		/// `SettingsCache.worlds` is populated by `LoadReferencedWorlds`, so it holds only the
		/// worlds some CLUSTER refers to, and nothing ships a cluster referring to TinyEmpty. The
		/// file was there and the key was not. See <see cref="ResolveCanvasWorldName"/>, which no
		/// longer trusts this constant on its own.
		/// </summary>
		private const string CanvasWorldName = "worlds/SandstoneDefault";

		/// <summary>The random stream handed to the sim. Fixed, because a rig that cannot be
		/// re-run identically is not a measurement.</summary>
		private const uint CanvasSimSeed = 1u;

		private static bool installed;

		/// <summary>
		/// Patches <c>SaveLoader.LoadFromWorldGen</c>. Idempotent, and harmless until a blueprint
		/// is pending.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (installed || harmony == null)
			{
				return;
			}
			installed = true;

			harmony.Patch(
				AccessTools.Method(typeof(SaveLoader), "LoadFromWorldGen"),
				prefix: new HarmonyMethod(AccessTools.Method(typeof(BlueprintWorld), nameof(LoadFromWorldGenPrefix))));

			// Installed here rather than separately: it guards a hazard that only a canvas makes
			// reachable, so it should arm exactly when canvases do and never on its own.
			StructureTemperatureGuard.Install(harmony);

			MethodInfo generateZones = AccessTools.Method(typeof(SubworldZoneRenderData), "GenerateTexture");
			if (generateZones != null)
			{
				harmony.Patch(generateZones,
					postfix: new HarmonyMethod(AccessTools.Method(typeof(BlueprintWorld),
						nameof(ApplyCanvasZone))));
			}
			else
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: SubworldZoneRenderData."
					+ "GenerateTexture not found, so every cell of a canvas will keep the game's "
					+ "seeded SubWorld.ZoneType.Space and Grid.IsCellOpenToSpace will be true "
					+ "everywhere on it. See RigBlueprint.CanvasSpec.zone.");
			}

			MethodInfo worldDetails = AccessTools.Method(typeof(WorldContainer), "SetWorldDetails");
			if (worldDetails != null)
			{
				harmony.Patch(worldDetails,
					postfix: new HarmonyMethod(AccessTools.Method(typeof(BlueprintWorld),
						nameof(SealCanvasBorder))));
			}
			else
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: WorldContainer.SetWorldDetails "
					+ "not found, so a canvas keeps the game's reserved top-border strip and the "
					+ "red hazard band will render across the top of every rig. See "
					+ nameof(SealCanvasBorder) + ".");
			}

			// THE ONE VANILLA NULL CHECK A CANVAS NEEDS AND KLEI DOES NOT HAVE. See
			// SuppressPinnedPanelNullRef for the whole argument; installed alongside the rest so
			// it is armed exactly when canvases are.
			MethodInfo worldInventoryUpdate = AccessTools.Method(typeof(WorldInventory), "Update");
			if (worldInventoryUpdate != null)
			{
				harmony.Patch(worldInventoryUpdate,
					finalizer: new HarmonyMethod(AccessTools.Method(typeof(BlueprintWorld),
						nameof(SuppressPinnedPanelNullRef))));
			}
			else
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: WorldInventory.Update not found; "
					+ "a canvas run will keep throwing its one-shot PinnedResourcesPanel NRE.");
			}

			// THE SECOND ONE, and the same argument. See SuppressCellSelectionNullRef.
			MethodInfo cellSelectionUpdateValues = AccessTools.Method(
				typeof(CellSelectionObject), "UpdateValues");
			if (cellSelectionUpdateValues != null)
			{
				harmony.Patch(cellSelectionUpdateValues,
					finalizer: new HarmonyMethod(AccessTools.Method(typeof(BlueprintWorld),
						nameof(SuppressCellSelectionNullRef))));
			}
			else
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: CellSelectionObject.UpdateValues "
					+ "not found; a canvas run will throw a DetailsScreen NRE twice a second for "
					+ "as long as anything is selected.");
			}

			MethodInfo minionSelect = AccessTools.Method(typeof(MinionSelectScreen), "OnSpawn");
			if (minionSelect != null)
			{
				harmony.Patch(minionSelect,
					postfix: new HarmonyMethod(AccessTools.Method(typeof(BlueprintWorld),
						nameof(SkipDuplicantSelection))));
			}
			else
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: MinionSelectScreen.OnSpawn not "
					+ "found, so a blueprint world will stop at the duplicant selection screen "
					+ "and cannot be watched while it runs.");
			}

			Debug.Log("[OniFramework] BlueprintWorld installed; SaveLoader.LoadFromWorldGen will build "
				+ "from a pending blueprint when one is set.");
		}

		/// <summary>
		/// Belt and braces on the canvas's subworld zone: re-asserts <c>worldZoneTypes</c> after
		/// <c>SubworldZoneRenderData.GenerateTexture</c> has run.
		///
		/// THIS IS NO LONGER THE FIX, AND THAT IS THE POINT OF THIS COMMENT. The fix is upstream,
		/// in <c>BuildCanvasWorld</c>, which now gives the canvas a real
		/// <c>WorldDetailSave.OverworldCell</c> covering the whole grid -- so GenerateTexture's own
		/// loop writes <c>worldZoneTypes</c>, the index texture, the colour texture and the sim's
		/// zone bytes, all from one source and all in agreement. See that method for why feeding
		/// the input beats patching the output.
		///
		/// What this postfix buys is a guarantee that does not depend on polygon arithmetic being
		/// right: <c>Grid.IsCellOpenToSpace</c> consults <c>worldZoneTypes</c> and nothing else, and
		/// a rig that silently believes its sealed chamber is open to space produces measurements
		/// that look plausible and are wrong. Every cell, unconditionally, costs one pass over an
		/// array at world build and removes that failure mode. If the polygon is working -- and the
		/// log line below reports the count it wrote -- this loop writes the value that is already
		/// there.
		///
		/// The zone array is inert for physics: <c>SetWorldZones</c> is bit-identical to the game's
		/// own library over fifty ticks with a real two-zone split and per-cell edits, so no zone
		/// byte can move a temperature.
		/// </summary>
		private static void ApplyCanvasZone(SubworldZoneRenderData __instance)
		{
			if (Active == null || __instance == null || __instance.worldZoneTypes == null)
			{
				return;
			}

			SubWorld.ZoneType zone = Active.canvas != null
				? Active.canvas.zone
				: SubWorld.ZoneType.Sandstone;

			int cells = __instance.worldZoneTypes.Length;
			for (int i = 0; i < cells; i++)
			{
				__instance.worldZoneTypes[i] = zone;
			}

			Debug.Log($"[OniFramework] BlueprintWorld: canvas '{Active.name}' is "
				+ $"SubWorld.ZoneType.{zone} for all {cells} cells (the game seeds Space, which "
				+ "would make Grid.IsCellOpenToSpace true everywhere on it).");
		}

		/// <summary>
		/// Closes a canvas's world border, which is what removes the red/black hazard band that
		/// renders across the top of every rig.
		///
		/// WHAT THE BAND ACTUALLY IS. <c>Grid.TopBorderHeight</c> is 2, and
		/// <c>Grid.IsValidBuildingCell</c> refuses the top two rows of any world
		/// (<c>y &lt;= world.maximumBounds.y - TopBorderHeight</c>). The game advertises that
		/// reservation through a single shader global, set every frame in
		/// <c>PropertyTextures.LateUpdate</c>:
		///
		///     Shader.SetGlobalFloat(_TopBorderHeight,
		///         ClusterManager.Instance.activeWorld.FullyEnclosedBorder ? 0f : Grid.TopBorderHeight);
		///
		/// and <c>ClusterCoverPostFX</c> keys <c>_HideSurface</c> off the same flag. So one switch
		/// turns off both, which is why this is one patch rather than two.
		///
		/// IT IS NOT A CANVAS BUG, AND THAT MATTERS FOR WHAT THE FIX IS ALLOWED TO CLAIM.
		/// <c>WorldContainer.SetWorldDetails</c> computes the flag as
		/// <c>GetBoolSetting("DrawWorldBorder") &amp;&amp; GetBoolSetting("DrawWorldBorderOverVacuum")</c>,
		/// and the second key exists in NO shipped world file -- grepped across
		/// <c>StreamingAssets/worldgen</c> on build 744825: <c>DrawWorldBorder</c> appears in
		/// defaults.yaml and five worlds, <c>DrawWorldBorderOverVacuum</c> in none of them.
		/// <c>WorldGenSettings.GetSetting</c> returns <c>default(T)</c> for a missing key, so the
		/// flag is FALSE on every generated world in the game and the strip is drawn on all of
		/// them. The only place vanilla ever sets it true is
		/// <c>WorldContainer.SetRocketInteriorWorldDetails</c> -- a rocket interior, i.e. exactly
		/// the case a canvas is.
		///
		/// So this is presentation, not correctness: on a 384-tall asteroid two reserved rows sit
		/// far above anything anyone looks at, and on a 32-tall canvas framed to fit the window
		/// they are a large red feature in the middle of the shot. Same rendering, wildly
		/// different prominence.
		///
		/// THE RESERVATION ITSELF IS UNTOUCHED, DELIBERATELY. This writes the render flag and
		/// nothing else: <c>Grid.TopBorderHeight</c> still refuses those rows to
		/// <c>IsValidBuildingCell</c>, and <c>Game.SanityCheckBoundsNextFrame</c> plus the
		/// <c>solidInfo</c> sweep in <c>Game.Sim200msCallback</c> still dig out any solid cell a
		/// blueprint puts there (both gated on <c>IsModuleInterior</c>, which this does not set).
		/// A canvas that ever reaches its own ceiling will still be refused; hiding the stripe
		/// does not make the rows buildable and must not be read as though it had.
		///
		/// Gated on <see cref="Active"/> so it is inert outside a canvas: a normal asteroid keeps
		/// the band the game has always drawn on it.
		/// </summary>
		private static void SealCanvasBorder(WorldContainer __instance)
		{
			if (Active == null || __instance == null)
			{
				return;
			}

			Traverse field = Traverse.Create(__instance).Field("fullyEnclosedBorder");
			if (!field.FieldExists())
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: WorldContainer has no "
					+ "'fullyEnclosedBorder' field, so the reserved-top-border strip will keep "
					+ "rendering as a red hazard band across the top of this canvas.");
				return;
			}

			field.SetValue(true);
			Debug.Log("[OniFramework] BlueprintWorld: canvas '" + Active.name + "' declares a "
				+ "fully enclosed border, so PropertyTextures stops drawing the "
				+ Grid.TopBorderHeight + "-row reserved strip. The rows are still not buildable.");
		}

		/// <summary>
		/// Dismisses the duplicant selection screen on a blueprint world, so the run is VISIBLE
		/// while it happens instead of simulating behind a menu.
		///
		/// THE PROBLEM THIS SOLVES. A blueprint takes the NEW GAME branch, not the load-a-save
		/// branch -- that is the whole point of <see cref="LoadFromWorldGenPrefix"/>. ONI's new
		/// game flow then shows <c>MinionSelectScreen</c> and waits for a human to press Embark.
		/// Nothing was broken by that: the world is already built and the sim is already running
		/// underneath, which is why every rig has always produced its verdict anyway. But the
		/// screen sits on top of it, so nobody can watch the thing they are measuring, and a rig
		/// you cannot watch is a rig you can only ever debug through its own log.
		///
		/// THE ESCAPE IS KLEI'S OWN, not a reimplementation of it. <c>MinionSelectScreen</c>
		/// subscribes to game event <c>-1992507039</c> with a handler that does exactly what is
		/// wanted here -- <c>StopFE</c>, <c>StartBE</c>, <c>SetGameStarted</c>, <c>Deactivate</c>
		/// -- for the case where a base already exists and the screen has nothing to ask. A
		/// blueprint world is precisely that case. Firing their event runs their handler; the
		/// alternative, calling those four methods here, would be a copy that silently rots the
		/// day Klei changes one of them.
		///
		/// It has to be a postfix on <c>OnSpawn</c> rather than anything earlier, because the
		/// subscription is made in <c>OnPrefabInit</c> -- an event fired before that has no
		/// listener and vanishes.
		///
		/// NO DUPLICANTS ARE PLACED, which is correct rather than a limitation: skipping
		/// <c>OnProceed</c> means no <c>NewBaseScreen.Init</c>, so no telepad and no starting
		/// crew. A rig canvas does not want three duplicants wandering through its measurement,
		/// and every rig written so far has run with none -- they simply ran with none behind a
		/// menu instead of in front of one.
		/// </summary>
		private static void SkipDuplicantSelection()
		{
			if (Active == null || Game.Instance == null)
			{
				return;
			}
			Debug.Log("[OniFramework] BlueprintWorld: dismissing the duplicant selection screen "
				+ "for blueprint world '" + Active.name + "' -- the run is watchable from here.");
			Game.Instance.Trigger(-1992507039);

			// THE COLONY NEEDS A NAME, because a vanilla system dereferences it without a null
			// check: RetireColonyUtility.SaveColonySummaryData passes SaveGame.Instance.BaseName
			// straight into StripInvalidCharacters, which indexes it on its first line. That save
			// is triggered by ColonyAchievementTracker as soon as any achievement fires, which on
			// a canvas is immediate -- revealing the test area sets CLEAR_FOW -- so it threw red
			// text on screen on every run before the rig had drawn a frame.
			//
			// SET HERE AND NOT IN BuildCanvasWorld, which was the first attempt and did nothing:
			// SaveGame.Instance does not exist that early, the null guard skipped silently, and
			// the only evidence that it had skipped was the exception still being there. Hence
			// the else branch below -- a fix that can quietly not happen has to say so.
			if (SaveGame.Instance != null)
			{
				SaveGame.Instance.SetBaseName(!string.IsNullOrEmpty(Active.name)
					? Active.name : "Blueprint Canvas");
			}
			else
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: no SaveGame instance to name the "
					+ "colony on; RetireColonyUtility will throw when an achievement fires.");
			}
		}

		/// <summary>
		/// Swallows the single NullReferenceException <c>WorldInventory.Update</c> throws on a
		/// blueprint canvas, and only that one.
		///
		/// WHAT VANILLA DOES. Update finishes counting the inventory once, flips
		/// <c>hasValidCount</c>, and then calls
		/// <c>PinnedResourcesPanel.Instance.ClearExcessiveNewItems()</c> with no null check. On a
		/// colony loaded from a save that panel has long since spawned and set its static. A
		/// canvas has no such panel anywhere in the scene -- <c>FindObjectsByType</c> including
		/// inactive objects finds none, and nothing in the game's code constructs one -- so the
		/// call throws, on screen, every run.
		///
		/// WHY A FINALIZER AND NOT A FIX. There is nothing to fix from outside: the panel cannot
		/// be woken because it does not exist, and the alternative is a transpiler that rewrites
		/// Klei's method body to add the null check they omitted. A finalizer that returns null
		/// tells Harmony to drop the exception, and NOTHING IS LOST BY DROPPING IT --
		/// <c>hasValidCount</c> is already true by the time the throw happens, so the next frame
		/// resumes exactly where this one stopped.
		///
		/// THREE CONDITIONS, so this can never hide anything else. It suppresses only a
		/// NullReferenceException, only while a blueprint canvas is active, and only while
		/// <c>PinnedResourcesPanel.Instance</c> is actually null -- the precise cause. On a normal
		/// colony, or once a panel exists, or for any other exception type, the finalizer returns
		/// the exception untouched and the game reports it exactly as it would have. It also says
		/// so once, so a run that suppresses something is a run that admits to it.
		/// </summary>
		private static Exception SuppressPinnedPanelNullRef(Exception __exception)
		{
			if (__exception == null)
			{
				return null;
			}
			if (!(__exception is NullReferenceException)
				|| Active == null
				|| PinnedResourcesPanel.Instance != null)
			{
				return __exception;
			}

			if (!pinnedPanelNullRefReported)
			{
				pinnedPanelNullRefReported = true;
				Debug.Log("[OniFramework] BlueprintWorld: suppressed WorldInventory.Update's "
					+ "one-shot NullReferenceException -- vanilla dereferences the static "
					+ "PinnedResourcesPanel.Instance without a null check, and a canvas world has "
					+ "no such panel. Suppressed only on a canvas, only for a null reference, and "
					+ "only while that static is null.");
			}
			return null;
		}

		/// <summary>So the suppression is announced once per session rather than once per frame.</summary>
		private static bool pinnedPanelNullRefReported;

		/// <summary>
		/// Turns <see cref="SuppressCellSelectionNullRef"/> off. The CONTROL ARM of that
		/// suppression, not a setting: Mod 1's CELLSELECT rig raises it under
		/// <c>--mod1-cellselect-nofix</c> so the guard can be watched failing on the defect it
		/// was written for. A guard whose failing arm has never been run has never been shown to
		/// detect anything, and this one measures an ABSENCE, which is the easiest thing in the
		/// world to report by accident.
		/// </summary>
		public static bool CellSelectionSuppressionDisabled;

		/// <summary>
		/// Swallows the NullReferenceException <c>CellSelectionObject.UpdateValues</c> throws on a
		/// blueprint canvas, and only that one. The SECOND vanilla null check a canvas needs and
		/// Klei does not have; <see cref="SuppressPinnedPanelNullRef"/> above is the first, and
		/// the reasoning is the same one.
		///
		/// WHAT VANILLA DOES. UpdateValues reads the selected cell into its fields and then calls
		/// <c>DetailsScreen.Instance.Trigger(-1514841199)</c> with no null check.
		/// <c>CellSelectionObject.Update</c> calls it every 0.5 s for as long as
		/// <c>SelectTool.Instance.selected</c> is that object, and <c>OnObjectSelected</c> calls
		/// it once more the moment the selection is made. So on a canvas -- where
		/// <c>DetailsScreen.Instance</c> is null -- the instant anything selects a cell, the run
		/// throws twice a second until it ends.
		///
		/// HOW IT WAS FOUND, and why it looked intermittent when it is not. It was reported off a
		/// recorded run: 147 exceptions in 74 s of one AIRLOOP film, against ZERO in the nine
		/// other rig runs of the same evening on the same build. The difference is not the build,
		/// it is that somebody clicked on that one. Nothing in this harness selects anything, so
		/// the trigger only ever arrives from a human watching the run -- which is exactly when
		/// the exception is most expensive, because it is burnt into the video.
		///
		/// GROUND TRUTH FOR "IT IS THAT STATIC AND NOT ANOTHER". The trace reports
		/// <c>UpdateValues () [0x000c7]</c>. Mono reports the offset a STATEMENT begins at, which
		/// the caller frame in the same trace confirms: <c>Update () [0x002b5]</c>, where IL_02b5
		/// is the <c>ldarg.0</c> opening the <c>UpdateValues()</c> call at IL_02b6. In
		/// <c>UpdateValues</c>, IL_00c7 is <c>ldsfld class DetailsScreen DetailsScreen::Instance</c>
		/// and IL_00d2 the <c>callvirt</c> against it; the statement before,
		/// <c>mSelectable.SetName(...)</c>, opens at IL_00ab, so a null selectable would have been
		/// reported there instead.
		///
		/// WHY A FINALIZER AND NOT A FIX. The screen cannot be woken, because it is not there to
		/// wake: <c>FindObjectsByType&lt;DetailsScreen&gt;</c> including inactive objects finds
		/// NONE on a canvas -- measured, after the wake-it approach was written, deployed and run.
		/// <c>DetailsScreen.Instance</c> is assigned in <c>OnPrefabInit</c>, so an object that was
		/// never instantiated never sets it, and nothing in the game's code constructs one. That
		/// leaves a finalizer or a transpiler that rewrites Klei's method body, and the finalizer
		/// loses nothing the throw was not already losing: everything before the call has already
		/// run and been stored, and everything after it is unreachable today.
		///
		/// THREE CONDITIONS, so this can never hide anything else. Only a NullReferenceException,
		/// only while a blueprint canvas is active, and only while <c>DetailsScreen.Instance</c>
		/// is actually null -- the precise cause. On a normal colony, or for any other exception
		/// type, the exception is returned untouched and the game reports it exactly as it would
		/// have. It says so once, so a run that suppresses something is a run that admits to it.
		/// </summary>
		private static Exception SuppressCellSelectionNullRef(Exception __exception)
		{
			if (__exception == null)
			{
				return null;
			}
			if (CellSelectionSuppressionDisabled
				|| !(__exception is NullReferenceException)
				|| Active == null
				|| DetailsScreen.Instance != null)
			{
				return __exception;
			}

			cellSelectionNullRefCount++;
			if (!cellSelectionNullRefReported)
			{
				cellSelectionNullRefReported = true;
				Debug.Log("[OniFramework] BlueprintWorld: suppressed CellSelectionObject."
					+ "UpdateValues' NullReferenceException -- vanilla dereferences the static "
					+ "DetailsScreen.Instance without a null check, and a canvas world has no "
					+ "such screen. This one repeats twice a second for as long as a cell is "
					+ "selected, so only the first is announced; the count is "
					+ nameof(CellSelectionNullRefCount) + ".");
			}
			return null;
		}

		/// <summary>So the suppression is announced once per session rather than twice a second.</summary>
		private static bool cellSelectionNullRefReported;

		private static int cellSelectionNullRefCount;

		/// <summary>
		/// How many times <see cref="SuppressCellSelectionNullRef"/> has fired since the game started.
		///
		/// Exposed because a suppression that is working looks exactly like a defect that was
		/// never there, and the two need to be distinguishable from a transcript. Mod 1's
		/// CELLSELECT rig reads it to assert that its own selection really did provoke the call
		/// it claims to be guarding.
		/// </summary>
		public static int CellSelectionNullRefCount => cellSelectionNullRefCount;

		/// <summary>
		/// Arms the next scene load to build <paramref name="blueprint"/>, and clears the active
		/// save file path so <c>SaveLoader.OnSpawn</c> takes the worldgen branch at all. Validates
		/// first: a blueprint with errors is refused here, where the errors can still be read,
		/// rather than half-built into a world.
		/// </summary>
		public static bool ArmForNextLoad(RigBlueprint blueprint)
		{
			if (blueprint == null)
			{
				FrameworkLog.Error("BlueprintWorld.ArmForNextLoad: null blueprint");
				return false;
			}

			List<string> errors;
			if (!blueprint.Validate(out errors))
			{
				FrameworkLog.Error($"BlueprintWorld: blueprint '{blueprint.name}' has "
					+ $"{errors.Count} error(s) and will not be built:");
				for (int i = 0; i < errors.Count; i++)
				{
					FrameworkLog.Error("  " + errors[i]);
				}
				return false;
			}

			Pending = blueprint;
			Active = null;
			SaveLoader.SetActiveSaveFilePath(null);
			Debug.Log($"[OniFramework] BlueprintWorld armed with '{blueprint.name}' "
				+ $"({blueprint.canvas.width}x{blueprint.canvas.height}), save path cleared.");
			return true;
		}

		/// <summary>
		/// Replaces <c>LoadFromWorldGen</c> when a blueprint is pending. Returning false skips the
		/// original entirely, so the file it would have read is never touched.
		/// </summary>
		private static bool LoadFromWorldGenPrefix(SaveLoader __instance, ref bool __result)
		{
			RigBlueprint blueprint = Pending;
			if (blueprint == null)
			{
				return true;
			}

			try
			{
				__result = BuildCanvasWorld(__instance, blueprint);
			}
			catch (Exception e)
			{
				FrameworkLog.Error("BlueprintWorld: canvas generation threw, falling back to "
					+ "a failed load rather than a half-built world: " + e);
				__result = false;
			}

			Pending = null;
			return false;
		}

		/// <summary>
		/// Finds a world definition the running game will actually accept, for the canvas to borrow
		/// its settings from. Nothing about the canvas comes FROM that world -- every cell is
		/// overwritten from the blueprint -- but `WorldGen` will not construct without a name the
		/// settings cache knows.
		///
		/// Three steps, most specific first: the configured name as-is; then any cached key whose
		/// tail matches it, which is what picks up a DLC prefix; then, failing both, nothing, with
		/// the available names logged. That last part matters more than it looks: a log that says
		/// only which name was missing, not which ones exist, leaves the fix to guesswork.
		/// </summary>
		private static string ResolveCanvasWorldName()
		{
			if (SettingsCache.worlds == null)
			{
				FrameworkLog.Error("BlueprintWorld: SettingsCache.worlds is null after "
					+ "WorldGen.LoadSettings; cannot pick a world for the canvas.");
				return null;
			}

			if (SettingsCache.worlds.HasWorld(CanvasWorldName))
			{
				return CanvasWorldName;
			}

			List<string> names = SettingsCache.worlds.GetNames();
			string tail = CanvasWorldName;
			int slash = tail.LastIndexOf('/');
			string bareName = slash >= 0 ? tail.Substring(slash + 1) : tail;

			for (int i = 0; i < names.Count; i++)
			{
				if (names[i] != null && names[i].EndsWith("worlds/" + bareName, StringComparison.Ordinal))
				{
					Debug.Log($"[OniFramework] BlueprintWorld: '{CanvasWorldName}' is not a cache key "
						+ $"on this install; using '{names[i]}' instead.");
					return names[i];
				}
			}

			// ANY cached world will do, and taking one is better than refusing to build. The
			// canvas overwrites every cell it is given, sets its own dimensions through
			// GridSettings.Reset, and never runs a single step of procedural generation -- the
			// world definition is borrowed purely to get past WorldGenSettings' constructor.
			if (names.Count > 0)
			{
				Debug.LogWarning($"[OniFramework] BlueprintWorld: no world named '{CanvasWorldName}' "
					+ $"and none ending in 'worlds/{bareName}'; borrowing '{names[0]}'. Nothing of "
					+ "that world reaches the canvas, but the name is worth correcting.");
				return names[0];
			}

			FrameworkLog.Error("BlueprintWorld: the world cache is empty after "
				+ "WorldGen.LoadSettings, so there is no name to build the canvas under.");
			return null;
		}

		/// <summary>
		/// The whole world-creation path, in the same order vanilla's own does it, minus the file
		/// read and minus the Sim.Save/Sim.Load round trip.
		/// </summary>
		private static bool BuildCanvasWorld(SaveLoader loader, RigBlueprint blueprint)
		{
			CanvasSpec canvas = blueprint.canvas;
			int width = canvas.width;
			int height = canvas.height;

			WorldGen.LoadSettings();

			// KLEI'S OWN NEXT LINE, and it has to be here for the same reason it is there.
			// SaveLoader pairs `WorldGen.LoadSettings()` with `LoadClusters()` (SaveLoader.cs,
			// immediately after LogActiveMods) because the first fills SettingsCache.clusterLayouts
			// and the second is what copies those into CustomGameSettingConfigs.ClusterLayout's
			// `levels` list, via StompLevels. Only three call sites in the game do this: the two
			// New Game screens and the save loader. A canvas boot goes through NONE of them -- it
			// intercepts LoadFromWorldGen -- so `levels` stayed null.
			//
			// A null `levels` is not a quiet omission: ListSettingConfig.GetLevel dereferences
			// `levels.Count` on its first line, so EVERY caller of GetCurrentClusterLayout threw a
			// NullReferenceException. Two did so on every canvas run, and both were red text on
			// screen before the rig had drawn a frame -- WattsonMessage.OnPrefabInit off the
			// duplicant select screen, and Unlocks.OnSpawn off Game start. ResolveDlcIds below
			// swallowed a third into a warning and recorded no DLC ids as a result.
			CustomGameSettings.Instance?.LoadClusters();

			WorldGen.SetupDefaultElements();

			// The managed-side world description. `Data` is trivially constructible; the only
			// fields that matter downstream are the chunk (size and offset, which GridSettings and
			// DefineWorldOffsets read) and baseStartPos, which sets the camera and the initial
			// reveal. Everything else -- overworld cells, biomes, rivers, the voronoi tree -- is
			// worldgen bookkeeping that nothing on the load path requires.
			var data = new Data();
			data.world = new Chunk(0, 0, width, height);
			data.gameSpawnData.baseStartPos = new Vector2I(canvas.startX, canvas.startY);

			// RESOLVED AGAINST THE LIVE CACHE, never hardcoded. `WorldGenSettings`' constructor
			// asserts on `SettingsCache.worlds.HasWorld(name)`, and the cache is keyed by
			// `Worlds.GetName(path, addPrefix)` -- which carries a DLC prefix, so the same file is
			// `worlds/TinyEmpty` on one install and `expansion1::worlds/TinyEmpty` on another.
			// Measured on the first live blueprint run, which died on exactly this:
			// "Assert failed: Failed to load world worlds/TinyEmpty", then an ArgumentNullException
			// out of the half-built settings.
			string worldName = ResolveCanvasWorldName();
			if (worldName == null)
			{
				return false;
			}

			var worldGen = new WorldGen(worldName, data, new List<string>(), new List<string>(), false);
			worldGen.isStartingWorld = true;

			// THIS FIELD ARRIVES NULL NO MATTER WHICH WORLD IS BORROWED, and something downstream
			// iterates it. `ProcGen.World.generatedSubworlds` is [NonSerialized], so it never comes
			// from the world yaml, and `WorldGenSettings` hands every `WorldGen` a
			// `SerializingCloner.Copy` of the cached `World` -- which drops exactly the fields marked
			// that way. Vanilla fills it in `Cluster.Save` from `worldGen.TerrainCells` and restores
			// it in `Cluster.Load` (`worldGen2.Settings.world.generatedSubworlds =
			// world.generatedSubworlds`); both halves are procedural-generation bookkeeping this path
			// deliberately skips, so nothing on the canvas path ever sets it.
			//
			// Measured, not guessed: the first live canvas run died in
			// `WorldContainer.SetWorldDetails` at IL 0x001bc -- `ldfld generatedSubworlds` feeding
			// the `foreach` two lines later -- reached from `ClusterManager.OnWorldGenComplete` ->
			// `CreateAsteroidWorldContainer`, invoked by this method's own `OnWorldGenComplete` call
			// (BuildCanvasWorld IL 0x0025a in the same trace). Note the earlier `m_generatedSubworlds
			// = ...` assignment on that same field is a plain `ldfld` and survives a null happily;
			// only the enumeration throws.
			//
			// EMPTY is the honest value: a canvas has no subworlds because nothing generated any.
			// The list is written into our own deep copy, so `SettingsCache` is not touched.
			worldGen.Settings.world.generatedSubworlds = new List<string>();

			// Cluster's only public constructor runs the whole world-mixing pipeline against
			// SettingsCache. The parameterless one it uses for its own Load path does none of that,
			// which is exactly what is wanted here, and is private only because Klei had no other
			// caller for it.
			var cluster = (Cluster)Activator.CreateInstance(typeof(Cluster), nonPublic: true);
			cluster.worlds.Add(worldGen);
			cluster.currentWorld = worldGen;
			cluster.size = new Vector2I(width, height);
			cluster.Id = worldName;

			Traverse loaderFields = Traverse.Create(loader);
			loaderFields.Field("m_cluster").SetValue(cluster);

			// THE CANVAS'S OWN IDENTITY, and then -- separately, and behind a flag -- its save
			// pointer. Two different things that were found at the same time, which is exactly why
			// they are written apart here.
			//
			// THE IDENTITY IS NOT OPTIONAL, measured by turning it off. A canvas whose
			// GameInfo.dlcIds is null makes `Game.IsDlcActiveForCurrentSave` dereference it, and that
			// is called from a per-frame UI refresh -- one run logged 1,573 of:
			//
			//     NullReferenceException
			//       at Game.IsDlcActiveForCurrentSave (System.String dlcId) [0x00049]
			//       at MeterScreen_Electrobanks.InternalRefresh ()
			//
			// against zero in the run that set it. So this is a canvas gap in its own right, of the
			// same family as the subworld zone and the cluster layout, and it stays on whether or not
			// anything ever saves. It was only ever FOUND while chasing the save, which is why it used
			// to sit inside the save's block.
			string colonyName = string.IsNullOrEmpty(blueprint.name) ? "RigCanvas" : blueprint.name;

			SaveGame.GameInfo gameInfo = loader.GameInfo;
			gameInfo.clusterId = cluster.Id;
			gameInfo.colonyGuid = Guid.NewGuid();
			gameInfo.dlcIds = ResolveDlcIds();
			gameInfo.baseName = colonyName;
			loaderFields.Property("GameInfo").SetValue(gameInfo);

			// THE SAVE POINTER IS THE WHOLE SAVE FEATURE, and it is OFF -- see
			// RigSaves.CanvasSavesEnabled for why. Kept rather than deleted because it was
			// expensive to localise.
			//
			// A scenario built on a canvas that then calls `SaveLoader.Instance.Save` dies there:
			//
			//     NullReferenceException
			//       at SaveLoader.Save (System.String filename, ...) [0x00374]
			//
			// leaving a ZERO-BYTE .sav on disk. Zero bytes is the diagnosis, not a detail:
			// `File.Open(filename, FileMode.Create)` had already run, so the entire serialise into the
			// MemoryStream ahead of it had succeeded, and the throw landed before the very first
			// `binaryWriter.Write`. Exactly one call sits between those two points --
			// `SaveGame.Instance.GetSaveHeader`, whose first line is
			// `SaveLoader.GetOriginalSaveFileName(SaveLoader.GetActiveSaveFilePath())`. That helper
			// opens with `filename.Contains("/")` against a parameter it never null-checks, and
			// `GetActiveSaveFilePath` is `KPlayerPrefs.GetString`, which returns NULL for a missing or
			// empty key rather than "". The single frame in the trace is the inner `throw ex4;`
			// resetting the stack, not evidence that Save's own body threw.
			//
			// AND THE NULL IS OURS. `ArmForNextLoad` clears the active save file path deliberately,
			// because that is what makes `SaveLoader.OnSpawn` take the worldgen branch this class
			// intercepts. Nothing ever put it back. Vanilla has the same hole for exactly one screen's
			// width: `WorldGenScreen` nulls it too, and `BaseNaming.OnEndEdit` fills it in again the
			// moment the player names the colony -- together with `SaveGame.Instance.SetBaseName`, the
			// other half of the same two-line ritual. A canvas skips that screen. This is that screen's
			// work, not a null guard bolted onto Klei's helper: feed the input rather than patch the
			// output, the same argument the overworld cell below is built on.
			//
			// NOTE the colony NAME is set regardless, and not here: SkipDuplicantSelection does it,
			// because RetireColonyUtility indexes SaveGame.Instance.BaseName unguarded the moment any
			// achievement fires. `GetSaveHeader` reads the name off SaveGame's own private field, NOT
			// off the GameInfo written above, so the two are genuinely separate writes.
			if (RigSaves.CanvasSavesEnabled)
			{
				if (SaveGame.Instance != null)
				{
					SaveGame.Instance.SetBaseName(colonyName);
				}
				else
				{
					FrameworkLog.Error("BlueprintWorld: SaveGame.Instance is null while the canvas is "
						+ "being built, so this world's save header would carry no colony name.");
				}

				// The path only has to be a real, plausible one. A rig's own `Save(path)` call ends by
				// pointing this at wherever it actually wrote, so this value survives only until the
				// first save -- its whole job is to be non-null while that save is being written.
				SaveLoader.SetActiveSaveFilePath(RigSaves.PathFor(colonyName + ".sav"));
				Debug.Log($"[OniFramework] BlueprintWorld: colony named '{colonyName}', save pointer "
					+ $"set to {SaveLoader.GetActiveSaveFilePath()} -- a canvas world is savable only "
					+ "once both of those exist.");
			}

			GridSettings.Reset(width, height);
			BackwallManager.Clear();
			if (Application.isPlaying)
			{
				Singleton<KBatchedAnimUpdater>.Instance.InitializeGrid();
			}

			// THE CANVAS'S OWN OVERWORLD CELL -- one rectangle covering the whole grid, and it is
			// what stops a rig rendering as outer space.
			//
			// Vanilla assembles this list from every world's serialised overworld cells. It is
			// tempting to give a canvas an EMPTY one, since a canvas has no biomes to describe,
			// but that misreads what the list is FOR.
			// `SubworldZoneRenderData.GenerateTexture` seeds all four
			// of its outputs to space -- `worldZoneTypes[i] = Space`, index texture 255, colour
			// texture `zoneColours[7]`, and the sim's own zone byte 255 -- and then overwrites
			// only the cells covered by a polygon in this list. With no polygons nothing is ever
			// overwritten, so every cell stays space in all four, and the shader draws the space
			// backdrop over the canvas.
			//
			// FEEDING THE INPUT, NOT PATCHING THE OUTPUT, and that distinction is the whole fix.
			// Letting GenerateTexture seed space and then writing the index texture and re-sending
			// the sim's zone bytes over the top from a postfix is three separate writes that have to agree, and a fourth consumer
			// (`OnActiveWorldChanged`, which runs immediately after and repaints from this same
			// list) that would undo them on the next world change. Handing the game one polygon
			// instead means the game's own code writes all four, in its own order, consistently,
			// and `OnActiveWorldChanged` reproduces rather than reverts them.
			//
			// Klei's own precedent for constructing one of these by hand is a rocket interior --
			// `WorldContainer.CreateInteriorWorld` builds a Polygon, sets zoneType/tags and adds
			// it to exactly this list. This is the same move with a simpler polygon.
			//
			// StartWorld is the honest tag: `BuildOutsideStartBiome` is the only consumer that
			// reads tags, and it awards an achievement for building OUTSIDE the start biome. A
			// canvas is the only world there is, so every rig building on it is inside it.
			SubWorld.ZoneType zone = canvas.zone;
			var detail = new WorldDetailSave();
			detail.overworldCells.Add(new WorldDetailSave.OverworldCell
			{
				// Half-open by construction: `Polygon.PointInPolygon` bounds-checks with
				// `Rect.Contains`, which is `>= min && < max`, so this covers x in [0,width) and
				// y in [0,height) -- every cell of the grid exactly once.
				poly = new Polygon(new Rect(0f, 0f, width, height)),
				zoneType = zone,
				tags = new TagSet { WorldGenTags.StartWorld },
				// 255 is `TerrainCell.biomeIdx`'s own default, meaning "no biome mask". It reaches
				// the index texture's green channel; `GroundRenderer` resolves its biome from
				// `worldZoneTypes` instead, so this is the inert value rather than a guess.
				biomeIdx = byte.MaxValue,
			});
			loaderFields.Property("clusterDetailSave").SetValue(detail);

			Sim.SIM_Initialize(Sim.DLL_MessageHandler);
			SimMessages.CreateSimElementsTable(ElementLoader.elements);
			SimMessages.CreateDiseaseTable(Db.Get().Diseases);

			WorldgenSimData simData = BuildSimData(blueprint, width, height);

			// This one message replaces vanilla's Sim.AllocateCells + Sim.Load pair. The native
			// handler allocates to the dimensions in the payload and seeds every cell's phase,
			// properties, insulation, strength, disease and backwall, then sizes the frame buffers
			// and textures to match (the replacement SimDLL's SimData_InitializeFromCells).
			// `headless: false` is what the game itself passes in normal play; the flag only picks
			// which half of the unstable-cell path runs.
			SimMessages.SimDataInitializeFromCells(width, height, CanvasSimSeed, ref simData, headless: false);

			// After the allocate that happened inside the message above, not before it.
			var offsets = new List<SimMessages.WorldOffsetData>
			{
				new SimMessages.WorldOffsetData
				{
					worldOffsetX = worldGen.WorldOffset.x,
					worldOffsetY = worldGen.WorldOffset.y,
					worldSizeX = worldGen.WorldSize.x,
					worldSizeY = worldGen.WorldSize.y,
				},
			};
			SimMessages.DefineWorldOffsets(offsets);

			// VANILLA SENDS `ClearUnoccupiedCells` HERE AND THIS DELIBERATELY DOES NOT. It looks like
			// "a no-op for a single world that covers the whole grid" and is not: the handler
			// (`case ClearUnoccupiedCells` in the SimDLL) stamps
			// `{vacuum, 0, 0}` into EVERY cell of the allocated grid, unconditionally -- it is not
			// selective, and the "unoccupied" in its name describes what is left over after the
			// per-world loads that follow it in vanilla, not what it touches.
			//
			// Vanilla's ordering is `AllocateCells` -> `DefineWorldOffsets` -> `ClearUnoccupiedCells`
			// -> one `Sim.Load` PER WORLD, and the wipe is what gives the gaps BETWEEN worlds a real
			// resolved Vacuum instead of element index 0, before each world's blob overwrites its own
			// rectangle. `SimDataInitializeFromCells` both allocates and seeds in one message, so on
			// this path there is no window between the two for that job to exist in -- and sending it
			// afterwards simply erased the canvas.
			//
			// Measured: with the wipe still in, the world built and every terrain
			// assertion failed with the whole grid reading Vacuum / 0.000 kg (border not solid, the
			// Oxygen fill missing, both jacket rings open, 88 sealing-ring cells reported by
			// `jacket-shell-gap`), while everything placed at RUNTIME -- buildings, footprints,
			// conduit prefill, reservoir charge -- was untouched and passing. That split is the
			// signature of a seed that landed and was then overwritten.

			// THE CORPUS RIG'S WORLD SNAPSHOT, AND THIS IS THE ONLY MOMENT IT CAN BE TAKEN.
			//
			// SimCorpus records one message whose payload is the WORLD -- `Load`, produced by
			// Sim.Save and read straight back -- and a world that has had even one 200 ms sim
			// tick is a world whose liquid has already shuffled. Measured: taken from the rig's
			// last phase the record differed by 17,247 bytes between recordings; taken from its
			// first phase, still 228, all of them the mass field of the twelve-by-eight water
			// block. Here, between `SimDataInitializeFromCells` and `Sim.Start`, the sim has a
			// world and has never stepped it, so the blob is exactly what the canvas built.
			//
			// It is also where the GAME sends `Load`: SaveLoader does AllocateCells, then
			// Sim.LoadWorld, then Sim.Start, in that order, because Start hands back the
			// GameDataUpdate whose pointers Grid binds to and a load after it would leave those
			// pointers behind. Sending it here keeps that ordering rather than working around it.
			//
			// Guarded by the rig's own flag, so an ordinary blueprint run does not do it.
			if (RigRegistry.AnyFlag(SimCorpus.Flag))
			{
				SimCorpus.SendWorldSnapshot();
			}

			Sim.Start();
			SceneInitializer.Instance.PostLoadPrefabs();
			SceneInitializer.Instance.NewSaveGamePrefab();

			loaderFields.Property("cachedGSD").SetValue(worldGen.SpawnData);

			Active = blueprint;

			if (loader.OnWorldGenComplete != null)
			{
				loader.OnWorldGenComplete(cluster);
			}
			if (StoryManager.Instance != null)
			{
				StoryManager.Instance.InitialSaveSetup();
			}

			Debug.Log($"[OniFramework] BlueprintWorld built '{blueprint.name}': {width}x{height}, "
				+ $"background {canvas.background}, border {canvas.border} x{canvas.borderThickness}, "
				+ $"start ({canvas.startX},{canvas.startY}). No worldgen ran and no file was read.");
			return true;
		}

		/// <summary>
		/// The cell arrays themselves: background, then border, then rectangular fills in order,
		/// then explicit per-tile overrides. Later stages overwrite earlier ones, so a blueprint
		/// can paint broadly and then correct one awkward tile.
		/// </summary>
		private static WorldgenSimData BuildSimData(RigBlueprint blueprint, int width, int height)
		{
			CanvasSpec canvas = blueprint.canvas;
			int cellCount = width * height;

			WorldgenSimData simData = default(WorldgenSimData);
			simData.Init(cellCount);

			// A zeroed DiseaseCell means disease index 0, which is a REAL disease, not "none".
			// Klei's own sentinel for empty is 0xFF, carried by Sim.DiseaseCell.Invalid.
			for (int i = 0; i < cellCount; i++)
			{
				simData.diseaseCells[i] = Sim.DiseaseCell.Invalid;
			}

			ushort backgroundIdx = ElementIndex(canvas.background);
			float backgroundMass = MassFor(canvas.background, canvas.backgroundMass);
			float backgroundTemp = TemperatureFor(canvas.background, canvas.backgroundTemperature);
			for (int i = 0; i < cellCount; i++)
			{
				simData.cells[i].SetValues(backgroundIdx, backgroundTemp, backgroundMass);
			}

			ushort borderIdx = ElementIndex(canvas.border);
			float borderMass = MassFor(canvas.border, 0f);
			float borderTemp = TemperatureFor(canvas.border, canvas.backgroundTemperature);
			int thickness = canvas.borderThickness;
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					bool onBorder = x < thickness || y < thickness
						|| x >= width - thickness || y >= height - thickness;
					if (onBorder)
					{
						simData.cells[y * width + x].SetValues(borderIdx, borderTemp, borderMass);
					}
				}
			}

			if (blueprint.fills != null)
			{
				for (int i = 0; i < blueprint.fills.Count; i++)
				{
					ApplyFill(blueprint, blueprint.fills[i], ref simData, width, height);
				}
			}

			if (blueprint.jacket != null)
			{
				ApplyJacket(blueprint, ref simData, width, height);
			}

			if (blueprint.cells != null)
			{
				for (int i = 0; i < blueprint.cells.Count; i++)
				{
					TemplateClasses.Cell cell = blueprint.cells[i];
					if (cell == null || !InBounds(cell.location_x, cell.location_y, width, height))
					{
						continue;
					}
					int index = cell.location_y * width + cell.location_x;
					simData.cells[index].SetValues(ElementIndex(cell.element),
						TemperatureFor(cell.element, cell.temperature),
						MassFor(cell.element, cell.mass));

					if (cell.backwallElement != SimHashes.Vacuum && cell.backwallMass > 0f)
					{
						simData.backwallCells[index].SetValues(ElementIndex(cell.backwallElement),
							cell.backwallMass, cell.backwallTemperature);
					}
				}
			}

			return simData;
		}

		/// <summary>
		/// One rectangular fill. A mixture fill writes its TOTAL mass as the FIRST listed species
		/// here, because a sim cell holds exactly one element; the remaining species are converted
		/// across at runtime by <see cref="BlueprintBuilder"/> using
		/// <c>GasMixtureFacade.ConvertFromVanilla</c>, which takes from the vanilla layer exactly
		/// what it puts into the mixture layer and so cannot create or destroy mass.
		/// </summary>
		private static void ApplyFill(RigBlueprint blueprint, FillSpec fill, ref WorldgenSimData simData,
			int width, int height)
		{
			if (fill == null)
			{
				return;
			}

			RegionSpec rect = RectOf(blueprint, fill.region, fill.x, fill.y, fill.w, fill.h);
			if (rect == null)
			{
				return;
			}

			SimHashes element = fill.element;
			float mass = fill.mass;
			if (fill.mixture != null && fill.mixture.Count > 0 && fill.mixture[0] != null)
			{
				element = fill.mixture[0].element;
				mass = fill.TotalMass();
			}

			ushort elementIdx = ElementIndex(element);
			float resolvedMass = MassFor(element, mass);
			float resolvedTemp = TemperatureFor(element, fill.temperature);

			// The backwall, if this fill asked for one. Resolved once outside the loop: it is the
			// same three values for every cell, and a fill can cover several hundred of them.
			bool wantsBackwall = fill.backwall != SimHashes.Vacuum && fill.backwallMass > 0f;
			ushort backwallIdx = wantsBackwall ? ElementIndex(fill.backwall) : (ushort)0;
			float backwallTemp = wantsBackwall
				? (fill.backwallTemperature > 0f ? fill.backwallTemperature : resolvedTemp)
				: 0f;

			foreach (Vector2I point in rect.Cells())
			{
				if (!InBounds(point.x, point.y, width, height))
				{
					continue;
				}
				int index = point.y * width + point.x;
				simData.cells[index].SetValues(elementIdx, resolvedTemp, resolvedMass);
				if (wantsBackwall)
				{
					simData.backwallCells[index].SetValues(backwallIdx, fill.backwallMass,
						backwallTemp);
				}
			}
		}

		/// <summary>
		/// The test-volume jacket, painted straight into the canvas: a solid ring around the
		/// protected rectangle, rings of vacuum outside that, then one more solid ring outside
		/// those. Painting it here rather than at runtime means there is never a frame in which
		/// the volume is unsealed, and no question of whether a cell was the rig's or was
		/// inherited.
		///
		/// THE INNER RING IS LOAD-BEARING. A blueprint's protected rectangle is a volume of gas,
		/// and vacuum laid straight against gas is not a boundary -- the gas expands into it and
		/// the jacket fills; see <c>DemoRig.PaintVacuumJacket</c> for the numbers.
		/// </summary>
		private static void ApplyJacket(RigBlueprint blueprint, ref WorldgenSimData simData,
			int width, int height)
		{
			JacketSpec jacket = blueprint.jacket;
			RegionSpec rect = RectOf(blueprint, jacket.region, jacket.x, jacket.y, jacket.w, jacket.h);
			if (rect == null)
			{
				return;
			}

			ushort shellIdx = ElementIndex(jacket.shell);
			float shellMass = MassFor(jacket.shell, jacket.shellMass);
			float shellTemp = TemperatureFor(jacket.shell, blueprint.canvas.backgroundTemperature);

			PaintRing(ref simData, width, height, rect, 1, shellIdx, shellTemp, shellMass);

			ushort vacuumIdx = ElementIndex(SimHashes.Vacuum);
			for (int ring = 2; ring <= jacket.vacuumRing + 1; ring++)
			{
				PaintRing(ref simData, width, height, rect, ring, vacuumIdx, 0f, 0f);
			}

			PaintRing(ref simData, width, height, rect, jacket.vacuumRing + 2, shellIdx, shellTemp,
				shellMass);
		}

		/// <summary>
		/// Paints the single-cell-wide outline at <paramref name="distance"/> cells outside
		/// <paramref name="rect"/> -- an outline, not a filled rectangle, so an inner ring already
		/// painted is not overwritten by an outer one.
		/// </summary>
		private static void PaintRing(ref WorldgenSimData simData, int width, int height,
			RegionSpec rect, int distance, ushort elementIdx, float temperature, float mass)
		{
			int minX = rect.x - distance;
			int minY = rect.y - distance;
			int maxX = rect.x + rect.w - 1 + distance;
			int maxY = rect.y + rect.h - 1 + distance;

			for (int x = minX; x <= maxX; x++)
			{
				PaintOne(ref simData, width, height, x, minY, elementIdx, temperature, mass);
				PaintOne(ref simData, width, height, x, maxY, elementIdx, temperature, mass);
			}
			for (int y = minY + 1; y <= maxY - 1; y++)
			{
				PaintOne(ref simData, width, height, minX, y, elementIdx, temperature, mass);
				PaintOne(ref simData, width, height, maxX, y, elementIdx, temperature, mass);
			}
		}

		private static void PaintOne(ref WorldgenSimData simData, int width, int height,
			int x, int y, ushort elementIdx, float temperature, float mass)
		{
			if (!InBounds(x, y, width, height))
			{
				return;
			}
			simData.cells[y * width + x].SetValues(elementIdx, temperature, mass);
		}

		/// <summary>Resolves a named region or an inline rectangle, without re-reporting errors
		/// that <see cref="RigBlueprint.Validate"/> has already surfaced.</summary>
		private static RegionSpec RectOf(RigBlueprint blueprint, string regionName,
			int x, int y, int w, int h)
		{
			if (!string.IsNullOrEmpty(regionName))
			{
				return blueprint.FindRegion(regionName);
			}
			if (w > 0 && h > 0)
			{
				return new RegionSpec { name = "inline", x = x, y = y, w = w, h = h };
			}
			return null;
		}

		private static bool InBounds(int x, int y, int width, int height)
		{
			return x >= 0 && y >= 0 && x < width && y < height;
		}

		private static ushort ElementIndex(SimHashes id)
		{
			Element element = ElementLoader.FindElementByHash(id);
			if (element == null)
			{
				Debug.LogWarning($"[OniFramework] BlueprintWorld: unknown element {id}, using Vacuum");
				element = ElementLoader.FindElementByHash(SimHashes.Vacuum);
			}
			return (ushort)ElementLoader.elements.IndexOf(element);
		}

		/// <summary>
		/// Vacuum is massless whatever the blueprint says; anything else with no stated mass takes
		/// the element's own default, which is what the game would have given it.
		/// </summary>
		private static float MassFor(SimHashes id, float requested)
		{
			if (id == SimHashes.Vacuum)
			{
				return 0f;
			}
			if (requested > 0f)
			{
				return requested;
			}
			Element element = ElementLoader.FindElementByHash(id);
			return element != null ? element.defaultValues.mass : 0f;
		}

		/// <summary>
		/// Sim.Cell.SetValues asserts that a non-zero mass never carries a non-positive
		/// temperature, so a vacuum cell is pinned to zero and everything else is floored to
		/// something physical rather than allowed to trip that assert at load time.
		/// </summary>
		private static float TemperatureFor(SimHashes id, float requested)
		{
			if (id == SimHashes.Vacuum)
			{
				return 0f;
			}
			return requested > 0f ? requested : 293.15f;
		}

		/// <summary>
		/// The DLC ids the save should record. Vanilla reads these off the current cluster layout,
		/// which on a boot that never visited the New Game screen may not be configured at all --
		/// the failure FreshWorldPatch.cs documents at length. An empty list is correct for a
		/// canvas anyway, so this never blocks a rig.
		/// </summary>
		private static List<string> ResolveDlcIds()
		{
			try
			{
				ClusterLayout layout = CustomGameSettings.Instance != null
					? CustomGameSettings.Instance.GetCurrentClusterLayout()
					: null;
				if (layout != null && layout.requiredDlcIds != null)
				{
					return new List<string>(layout.requiredDlcIds);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] BlueprintWorld: no configured cluster layout to read DLC "
					+ "ids from, recording none: " + e.Message);
			}
			return new List<string>();
		}
	}
}
