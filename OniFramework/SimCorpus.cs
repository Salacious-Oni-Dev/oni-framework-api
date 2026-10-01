using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// FORCES EVERY REACHABLE SIM MESSAGE TO BE SENT, SO A CORPUS CAN BE RECORDED WITHOUT A SAVE
	/// AND WITHOUT A PLAYER.
	///
	/// <b>The problem this exists for.</b> A message corpus is recorded by the native passthrough
	/// shim while the game runs, so its contents are whatever the player happened to provoke.
	/// "Load a colony and play for a minute" works -- <c>LoadTables()</c> reads exactly two ids out of an 11 MB corpus,
	/// <c>Elements_CreateTable</c> and <c>Disease_CreateTable</c>, and both fire on every boot --
	/// but it is not DETERMINISTIC, and it is not COMPLETE. A corpus that varies per contributor
	/// is a poor thing to hang goldens or message-discovery work on, and a message handler nothing
	/// in any suite exercises is a handler nothing tests.
	///
	/// <b>Why a save-free world makes the completeness problem WORSE before it makes it better.</b>
	/// A blueprint canvas does not initialise what a loaded save does -- no zone data, no backwall,
	/// no cluster layout -- and that gap is exactly the DRAIN.md rare list: <c>SetWorldZones</c>,
	/// <c>DefineWorldOffsets</c>, <c>ModifyCellWorldZone</c>, <c>SimData_FreeCells</c>,
	/// <c>SimData_ResizeAndInitializeVacuumCells</c>, <c>ModifyRadiationEmitter</c>. So going
	/// save-free would quietly THIN the corpus unless something sends those on purpose. That
	/// something is this class.
	///
	/// <b>THE DESIGN DECISION THAT MATTERS, and it changed during the build.</b> The recorded plan
	/// was to drive <c>Sim.SIM_HandleMessage</c> directly from a table of hand-built payloads
	/// ported from <c>diffsim</c>. Reading the game's own <c>SimMessages</c> showed that to be the
	/// wrong call. <c>SimMessages</c> wraps 47 of the 62 ids with plain-argument static methods --
	/// including <c>ModifyCellWorldZone</c>, <c>DefineWorldOffsets</c>, <c>SimDataFreeCells</c> and
	/// all three radiation emitters, none of which the plan expected to be wrapped -- so this class
	/// calls THE GAME'S OWN WRAPPERS wherever one exists.
	///
	/// That is not a convenience, it is the whole point of a corpus. The shim's own header says it
	/// records "the real bytes so the offline driver can replay them verbatim", because
	/// reconstructing payloads by hand is what produced three separate layout bugs. A corpus built
	/// from OUR payload builders would be a recording of our own ABI beliefs wearing the costume of
	/// independent ground truth, and it would agree with our sim for exactly the wrong reason: a
	/// test that shares a defect cannot see it. Every byte
	/// this class puts in the corpus is a byte Klei's own managed code laid out.
	///
	/// <b>Five ids are UNREACHABLE and this class does not fake them.</b> Searching every call site
	/// in the game's managed assemblies finds nothing that sends <c>SetVisibleCells</c>,
	/// <c>AddDiseaseConsumer</c>, <c>ModifyDiseaseConsumer</c> or <c>RemoveDiseaseConsumer</c>:
	/// they are declared in <c>SimMessageHashes</c>, have no <c>SimMessages</c> wrapper and no
	/// caller anywhere. <c>RadiationSickness</c> is not a message at all -- it is in the enum
	/// because -727746602 is the SDBM hash of the DISEASE of that name, which is why
	/// <c>sim/radiation.h</c> carries the identical constant as <c>kRadiationSicknessHash</c>.
	/// Inventing payloads for those four-and-a-bit would put invented bytes in a file whose entire
	/// value is that its bytes are not invented, so they are reported as unreachable and left out.
	/// Achievable coverage is 57 of 62, and that number is honest.
	///
	/// <b>"THE BOOT SENDS IT" IS A CLAIM ABOUT A BOOT.</b> Four ids that look boot-owned are not
	/// sent on this rig's boot. Three of them (<c>AllocateCells</c>, <c>ClearUnoccupiedCells</c>, <c>Load</c>) are sent by
	/// <c>SaveLoader</c> on the LOAD path, and a blueprint canvas does not take that path:
	/// <c>BlueprintWorld</c> replaces the <c>AllocateCells</c> + <c>Load</c> pair with a single
	/// <c>SimDataInitializeFromCells</c>. The fourth, <c>Elements_CreateInteractions</c>, is not
	/// boot-owned on any path -- nothing in either shipped assembly calls its wrapper. All four
	/// are forced in phase 5, because this rig deliberately boots differently from a normal game.
	///
	/// <b>WHAT "THE SAME BYTES" MEANS, precisely.</b> Byte-identity holds across runs of the same
	/// game build with the same DLC and the same mods, and that is the property the rig exists to
	/// give. It does NOT hold across different DLC ownership or different element/disease content,
	/// and it never could: <c>Elements_CreateTable</c> is the element table (216 rows on this
	/// machine) and <c>Disease_CreateTable</c> is the disease table, and both are content. What
	/// changed with this rig is that everything the RUN contributes is now fixed; what remains
	/// variable is what the INSTALL contributes, and <c>sim_shim.log</c> travels beside the corpus
	/// to say which install it was.
	///
	/// <b>Handles are real, because the alternative crashes.</b> The Add/Modify/Remove families
	/// need the sim handle the SIM allocates, which comes back asynchronously through
	/// <c>Game.Instance.simComponentCallbackManager</c>. This class registers genuine callbacks
	/// exactly as <c>SimComponent.SimRegister</c> does and waits a tick for the answers, rather
	/// than guessing handle values. Where the game's own wrappers default a <c>callbackIdx</c> to
	/// -1 this class passes -1 too, because that is the value the game itself sends and therefore
	/// the value proven safe: <c>Game.OnSimDataUpdate</c> calls <c>Release</c> on the
	/// mass-consumed and radiation-consumed handles with NO <c>IsVersionValid</c> guard, so a
	/// made-up index there would be an exception on the game thread rather than a corpus record.
	///
	/// <b>Ordering is deliberate and the last phase is destructive.</b> World-geometry messages go
	/// last, alone, immediately before the rig quits: <c>SimData_ResizeAndInitializeVacuumCells</c>
	/// and <c>SimData_FreeCells</c> genuinely rewrite grid structure (the game sends them only when
	/// a world is added to or removed from the cluster), so they are aimed at a scratch rectangle
	/// the blueprint reserves for being destroyed, and nothing is measured afterwards.
	///
	/// <b>Inert unless armed.</b> Nothing here runs without <see cref="Flag"/> on the command line.
	/// </summary>
	public static class SimCorpus
	{
		/// <summary>Command-line flag that arms the corpus rig.</summary>
		public const string Flag = "--corpus-rig";

		/// <summary>
		/// PINS THE SIM'S TIMESTEP TO THE FRAME, and this is the single change that turns a
		/// recording into an artefact. Inert unless <see cref="Flag"/> is on the command line.
		///
		/// <b>What the wall clock was leaking into the file.</b> Three recordings made after every
		/// other source of variance had been closed still disagreed, in exactly three records, and
		/// all three were <c>SimFrameManager_NewGameFrame</c> during boot. <c>Game.SimEveryTick</c>
		/// accumulates <c>Time.deltaTime</c> and advances one sub-tick per 1/60 s of it; every
		/// twelfth sub-tick is the 200 ms sim tick, which is the only frame that sends
		/// <c>NewGameFrame(0.2f)</c> -- every other frame sends <c>NewGameFrame(0f)</c>. So WHICH
		/// of the first few frames carried the tick was a function of how fast the machine
		/// rendered, and the sunlight intensity in the same payload moved with it, because
		/// <c>TimeOfDay.UpdateSunlightIntensity</c> reads <c>GameClock</c> and <c>GameClock</c>
		/// advances on sim ticks.
		///
		/// <b>Why replacing dt is honest here.</b> The value written is 1/60 s exactly -- the
		/// quantum <c>SimEveryTick</c> already divides by -- so the game does precisely what it
		/// always does, one sub-tick per rendered frame, and the sim tick lands on frame 12 on
		/// every machine instead of on whichever frame the clock happened to reach. Nothing about
		/// the MESSAGES changes: the payloads are still built by Klei's own code and still carry
		/// the two values it produces (0f and 0.2f). What changes is which frame produces which,
		/// and that was never information about the game -- only about the machine.
		///
		/// A rig may not do this to measure something, and no other rig in this project does. This
		/// one is not measuring; it is recording, and a recording whose contents depend on the
		/// recorder's frame rate is not a recording of anything.
		/// </summary>
		/// <param name="owningModId">
		/// The mods.json id of the mod that supplies the rig — its canvas and its registration.
		/// Passed explicitly rather than sniffed with <c>Assembly.GetCallingAssembly</c>, which
		/// would name the FRAMEWORK from anywhere inside the framework and can be optimised away
		/// besides. It joins <c>OniFramework</c> in the allow-list that
		/// <see cref="SuppressForeignContent"/> enforces, so getting it wrong disables the rig's
		/// own mod and the run records nothing — loudly, rather than subtly.
		/// </param>
		public static void Install(Harmony harmony, string owningModId)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (string.IsNullOrEmpty(owningModId))
			{
				throw new ArgumentException("a corpus run has to know which mod owns the rig, or "
					+ "it will disable it along with everything else", "owningModId");
			}
			if (!RigRegistry.AnyFlag(Flag))
			{
				return;
			}

			MethodInfo target = AccessTools.Method(typeof(Game), "SimEveryTick");
			if (target == null)
			{
				// Loud rather than silent: without this patch the rig still runs and still
				// produces a corpus, and the corpus is still WRONG in a way that only shows up
				// when somebody diffs two of them a week later.
				FrameworkLog.Error("SimCorpus: Game.SimEveryTick not found -- the sim timestep "
					+ "cannot be pinned, so this recording will vary with the frame rate. Do not "
					+ "publish its output.");
				return;
			}

			harmony.Patch(target, new HarmonyMethod(
				AccessTools.Method(typeof(SimCorpus), "PinTimestepPrefix")));
			FrameworkLog.Info("SimCorpus: sim timestep pinned to 1/60 s per frame for this run.");

			BlockKeyboardInput(harmony);
			SuppressForeignContent(harmony, owningModId);
		}

		/// <summary>
		/// TAKES EVERY MOD BUT THE RIG'S OWN OUT OF THE RUN, because a corpus is supposed to be a
		/// recording of the shipped game and a contributor's mod list is not part of it.
		///
		/// <b>The defect this closes, measured rather than imagined.</b> One of the corpus's 111
		/// records is <c>Elements_CreateTable</c>, and that table is built MANAGED-side from
		/// <c>ElementLoader.elements</c> — the SimDLL is its consumer, never its source, so no
		/// amount of swapping the DLL back to Klei's affects it. Any mod that adds an element is
		/// in that list by the time the game sends it, and <c>ElementLoader</c> SORTS, so a new
		/// element does not append, it lands mid-table and shifts every index after it: four extra
		/// elements can enter at index 133 of Klei's 212, and every stored index after them then
		/// names a different element. Nothing about the file looks wrong.
		///
		/// <b>Two mechanisms, because one cannot cover both cases.</b>
		///
		/// For elements added through this framework, <see cref="ElementRegistry"/> is the single
		/// seam they all pass through, and its contribution happens at
		/// <c>ElementLoader.CollectElementsFromYAML</c> — long after every mod's <c>OnLoad</c>.
		/// Refusing there is exact and order-independent, and that is the mechanism that actually
		/// guarantees the table.
		///
		/// For everyone else's mods there is no such seam, so this patches the gate Klei itself
		/// consults: <c>KMod.Mod.IsEnabledForActiveDlc</c>, which <c>Manager.Load</c> asks before
		/// loading each mod, before <c>PostLoad</c>, and again in its crashed-mod sweep. Returning
		/// false for everything outside the allow-list takes them out of the run without touching
		/// <c>mods.json</c>, so nothing persists into the next launch and there is nothing to
		/// restore afterwards — which matters, because the game rewrites mods.json on exit and
		/// silently undoes a pre-launch edit of it.
		///
		/// <b>What this cannot do, stated plainly.</b> It is installed from a mod's own
		/// <c>OnLoad</c>, so a mod that loads BEFORE this one has already run its <c>OnLoad</c>
		/// and applied its Harmony patches; this stops its <c>PostLoad</c> and its content, not
		/// what it has already done. That is why the rig still asserts the finished element table
		/// against Klei's <c>SimHashes</c> in <see cref="Begin"/> and fails the run rather than
		/// recording a corpus it cannot vouch for. A guard nobody checks is a guess.
		/// </summary>
		private static void SuppressForeignContent(Harmony harmony, string owningModId)
		{
			// The elements first, because this is the half that is airtight.
			ElementRegistry.Suppressed = true;

			// The rig's own two mods, and nothing else: the framework, and the mod that supplies
			// the canvas and the registration. Both are mods.json ids, which for a dev-installed
			// mod is its folder name.
			allowedMods.Clear();
			allowedMods.Add("OniFramework");
			allowedMods.Add(owningModId);

			MethodInfo gate = AccessTools.Method(typeof(KMod.Mod), "IsEnabledForActiveDlc");
			if (gate == null)
			{
				FrameworkLog.Warn("SimCorpus: KMod.Mod.IsEnabledForActiveDlc not found, so other "
					+ "mods are NOT suppressed. The element-table assertion in Begin still guards "
					+ "the corpus.");
				return;
			}

			harmony.Patch(gate, null,
				new HarmonyMethod(AccessTools.Method(typeof(SimCorpus), "ModGatePostfix")));
			FrameworkLog.Info("SimCorpus: mod loading hijacked -- only "
				+ string.Join(" and ", allowedMods.ToArray()) + " stay enabled for this run, and "
				+ "registered elements are withheld from the game's table.");
		}

		/// <summary>The mods a corpus recording is allowed to load.</summary>
		private static readonly List<string> allowedMods = new List<string>();

		/// <summary>
		/// The gate. Klei asks this per mod, several times, during <c>Manager.Load</c>; anything
		/// outside the allow-list answers no for the rest of the process.
		/// </summary>
		private static void ModGatePostfix(KMod.Mod __instance, ref bool __result)
		{
			if (!__result || __instance == null || __instance.label.id == null)
			{
				return;
			}
			if (!allowedMods.Contains(__instance.label.id))
			{
				__result = false;
			}
		}

		/// <summary>
		/// TAKES THE KEYBOARD AWAY FROM WHOEVER IS SITTING AT THE MACHINE, for the length of the
		/// recording.
		///
		/// This is not politeness, it is the same requirement as the timestep pin. Space pauses,
		/// 1/2/3 change speed and Escape opens the pause menu -- all three stop or re-rate the sim
		/// ticks, and the tick count is what the recorded world blob is a function of. A single
		/// keypress during a thirty-second run silently produces a corpus that differs from every
		/// other machine's, with nothing in the file to say why. The banner Mod 1 puts up says
		/// "do not touch this machine"; this is what makes the sentence true rather than hopeful.
		///
		/// <c>KInputManager.Update</c> and <c>Dispatch</c> are where every binding is delivered.
		/// <c>GameInputManager</c> overrides <c>Update</c> and calls <c>base.Update()</c>, and a
		/// Harmony patch on the base is what that call reaches, so one patch covers both. The
		/// controller bookkeeping in the override still runs; only the delivery stops.
		///
		/// NOT patched: <c>KInputManager.isFocused</c>, which would have been one postfix instead
		/// of two prefixes. Forcing it false tells the whole game the window lost focus, and what
		/// the game does with that -- including whether it pauses -- is a settings question. A rig
		/// that pauses itself while claiming to pin the tick rate would be worse than no patch.
		/// </summary>
		private static void BlockKeyboardInput(Harmony harmony)
		{
			MethodInfo update = AccessTools.Method(typeof(KInputManager), "Update");
			MethodInfo dispatch = AccessTools.Method(typeof(KInputManager), "Dispatch");
			HarmonyMethod skip = new HarmonyMethod(AccessTools.Method(typeof(SimCorpus), "SkipPrefix"));

			int patched = 0;
			if (update != null) { harmony.Patch(update, skip); patched++; }
			if (dispatch != null) { harmony.Patch(dispatch, skip); patched++; }

			if (patched == 2)
			{
				FrameworkLog.Info("SimCorpus: keyboard and controller input blocked for this run.");
			}
			else
			{
				FrameworkLog.Warn("SimCorpus: only " + patched + " of 2 input entry points were "
					+ "found, so input is NOT fully blocked. A keypress during this run can change "
					+ "what it records.");
			}
		}

		/// <summary>A prefix that skips its target outright.</summary>
		private static bool SkipPrefix()
		{
			return false;
		}

		/// <summary>
		/// The other half of the input block, and it cannot be a Harmony patch: mouse clicks on
		/// the game's own UI arrive through Unity's <c>EventSystem</c>, not through
		/// <c>KInputManager</c>, so blocking the bindings leaves every HUD button live -- and one
		/// of them opens the pause menu.
		///
		/// Called by the rig once its world exists, rather than at load, because that is when the
		/// sim starts ticking and therefore when a click starts costing something. The
		/// <c>EventSystem</c> does not re-enable itself, so once is enough.
		/// </summary>
		public static void BlockPointerInput()
		{
			UnityEngine.EventSystems.EventSystem current = UnityEngine.EventSystems.EventSystem.current;
			if (current == null)
			{
				log("pointer input: no EventSystem to disable (nothing to block)");
				return;
			}
			current.enabled = false;
			log("pointer input: EventSystem disabled -- UI clicks cannot reach the game");
		}

		/// <summary>
		/// The prefix itself. Kept private and named rather than a lambda so Harmony's own error
		/// messages name something a reader can find.
		/// </summary>
		private static void PinTimestepPrefix(ref float dt)
		{
			dt = 1f / 60f;
		}

		// ------------------------------------------------------------------ the canvas contract
		//
		// These match blueprints/corpus.yaml and are asserted against the live grid before
		// anything is sent. They are constants rather than a region lookup because the blueprint
		// and this file have to agree exactly, and a shared constant that is wrong fails loudly
		// in one place instead of aiming messages at whatever happens to be there.

		/// <summary>A cell inside the oxygen region.</summary>
		public const int GasX = 14, GasY = 12;

		/// <summary>A cell inside the water region.</summary>
		public const int LiquidX = 30, LiquidY = 12;

		/// <summary>A cell inside the sandstone region.</summary>
		public const int SolidX = 46, SolidY = 12;

		/// <summary>
		/// The rectangle phase 5 is allowed to destroy. Vacuum in the blueprint, touched by
		/// nothing else, and the run ends immediately after it is used.
		/// </summary>
		public const int ScratchX = 8, ScratchY = 30, ScratchW = 12, ScratchH = 8;

		// ------------------------------------------------------------------ coverage bookkeeping

		/// <summary>How a given message id reaches the corpus.</summary>
		public enum Reach
		{
			/// <summary>The game sends it during boot; the shim's in-order phase captures it.</summary>
			Boot,

			/// <summary>This class sends it.</summary>
			Forced,

			/// <summary>Nothing in the shipped game sends it, and this class will not invent it.</summary>
			Unreachable,

			/// <summary>
			/// Reachable, sendable, and IMPOSSIBLE TO RECORD, which is a different thing and has
			/// exactly one member. <c>Elements_CreateInteractions</c>'s payload is
			/// <c>{int numInteractions; ElementInteraction* interactions;}</c> -- twelve bytes, of
			/// which eight are a POINTER into the sender's stack. A corpus record of it holds an
			/// address, so it differs on every run by construction and, far worse, replaying it
			/// offline would hand the sim a stale pointer to dereference.
			///
			/// This was found by recording it: the rig sent it, three runs disagreed in bytes 4-9
			/// of that record and agreed everywhere else, and the address is what those bytes are.
			/// It is the only message in <c>SimMessages</c> whose payload contains a pointer --
			/// every other table (elements, diseases, zones, cells) is serialised inline.
			/// </summary>
			Unrecordable,
		}

		private struct Plan
		{
			public SimMessageHashes id;
			public Reach reach;
			public string note;

			public Plan(SimMessageHashes id, Reach reach, string note)
			{
				this.id = id;
				this.reach = reach;
				this.note = note;
			}
		}

		/// <summary>
		/// Every id in <c>SimMessageHashes</c>, classified. The list is the specification: a
		/// message added to the game in a future build shows up as an id with no row here, and
		/// <see cref="Report"/> says so rather than silently ignoring it.
		/// </summary>
		private static readonly Plan[] kPlan =
		{
			// --- sent by the game's own boot sequence, before any rig code runs -----------------
			//
			// A blueprint canvas does not take the shipped game's LOAD path: it replaces
			// SaveLoader's Sim.AllocateCells + Sim.Load pair with one SimDataInitializeFromCells
			// (BlueprintWorld), so AllocateCells, ClearUnoccupiedCells and Load are not sent by
			// the boot. Elements_CreateInteractions is not boot-owned on ANY path, because nothing
			// in either shipped assembly calls SimMessages.CreateElementInteractions. All four are
			// forced below.
			new Plan(SimMessageHashes.Elements_CreateTable, Reach.Boot, "ElementLoader, at load"),
			new Plan(SimMessageHashes.Disease_CreateTable, Reach.Boot, "Db.Diseases, at load"),
			new Plan(SimMessageHashes.Start, Reach.Boot, "Sim.Start; its return is the GameDataUpdate the Grid arrays bind to"),
			new Plan(SimMessageHashes.SimData_InitializeFromCells, Reach.Boot, "BlueprintWorld, taking SaveLoader.OnSpawn's worldgen branch"),
			new Plan(SimMessageHashes.PrepareGameData, Reach.Boot, "every sim frame"),
			new Plan(SimMessageHashes.SimFrameManager_NewGameFrame, Reach.Boot, "every sim frame"),

			// --- forced here ------------------------------------------------------------------
			new Plan(SimMessageHashes.SetSavedOptions, Reach.Forced, "SetSavedOptionValue"),
			new Plan(SimMessageHashes.SetDebugProperties, Reach.Forced, "SetDebugProperties"),
			new Plan(SimMessageHashes.ToggleProfiler, Reach.Forced, "raw, zero-length, as DebugHandler sends it"),

			new Plan(SimMessageHashes.Dig, Reach.Forced, "Dig"),
			new Plan(SimMessageHashes.ModifyCell, Reach.Forced, "ModifyCell"),
			new Plan(SimMessageHashes.ModifyCellEnergy, Reach.Forced, "ModifyEnergy"),
			new Plan(SimMessageHashes.SetInsulationValue, Reach.Forced, "SetInsulation"),
			new Plan(SimMessageHashes.SetStrengthValue, Reach.Forced, "SetStrength"),
			new Plan(SimMessageHashes.ChangeCellProperties, Reach.Forced, "SetCellProperties + ClearCellProperties"),
			new Plan(SimMessageHashes.ModifyBackwallData, Reach.Forced, "SetBackwallData"),
			new Plan(SimMessageHashes.MassConsumption, Reach.Forced, "ConsumeMass"),
			new Plan(SimMessageHashes.MassEmission, Reach.Forced, "EmitMass"),
			new Plan(SimMessageHashes.CellDiseaseModification, Reach.Forced, "ModifyDiseaseOnCell"),
			new Plan(SimMessageHashes.ConsumeDisease, Reach.Forced, "ConsumeDisease"),
			new Plan(SimMessageHashes.CellRadiationModification, Reach.Forced, "ModifyRadiationOnCell"),
			new Plan(SimMessageHashes.RadiationParamsModification, Reach.Forced, "ModifyRadiationParams"),

			new Plan(SimMessageHashes.AddElementConsumer, Reach.Forced, "AddElementConsumer"),
			new Plan(SimMessageHashes.SetElementConsumerData, Reach.Forced, "SetElementConsumerData"),
			new Plan(SimMessageHashes.RemoveElementConsumer, Reach.Forced, "RemoveElementConsumer"),
			new Plan(SimMessageHashes.AddElementEmitter, Reach.Forced, "AddElementEmitter"),
			new Plan(SimMessageHashes.ModifyElementEmitter, Reach.Forced, "ModifyElementEmitter"),
			new Plan(SimMessageHashes.RemoveElementEmitter, Reach.Forced, "RemoveElementEmitter"),

			new Plan(SimMessageHashes.AddElementChunk, Reach.Forced, "AddElementChunk"),
			new Plan(SimMessageHashes.SetElementChunkData, Reach.Forced, "SetElementChunkData"),
			new Plan(SimMessageHashes.MoveElementChunk, Reach.Forced, "MoveElementChunk"),
			new Plan(SimMessageHashes.ModifyElementChunkEnergy, Reach.Forced, "ModifyElementChunkEnergy"),
			new Plan(SimMessageHashes.ModifyChunkTemperatureAdjuster, Reach.Forced, "ModifyElementChunkTemperatureAdjuster"),
			new Plan(SimMessageHashes.RemoveElementChunk, Reach.Forced, "RemoveElementChunk"),

			new Plan(SimMessageHashes.AddBuildingHeatExchange, Reach.Forced, "AddBuildingHeatExchange"),
			new Plan(SimMessageHashes.ModifyBuildingHeatExchange, Reach.Forced, "ModifyBuildingHeatExchange"),
			new Plan(SimMessageHashes.ModifyBuildingEnergy, Reach.Forced, "ModifyBuildingEnergy"),
			new Plan(SimMessageHashes.RemoveBuildingHeatExchange, Reach.Forced, "RemoveBuildingHeatExchange"),
			new Plan(SimMessageHashes.AddBuildingToBuildingHeatExchange, Reach.Forced, "RegisterBuildingToBuildingHeatExchange -- note the NAME MISMATCH, see SendBuildings"),
			new Plan(SimMessageHashes.AddInContactBuildingToBuildingToBuildingHeatExchange, Reach.Forced, "AddBuildingToBuildingHeatExchange -- see SendBuildings"),
			new Plan(SimMessageHashes.RemoveBuildingInContactFromBuildingToBuildingHeatExchange, Reach.Forced, "RemoveBuildingInContactFromBuildingToBuildingHeatExchange"),
			new Plan(SimMessageHashes.RemoveBuildingToBuildingHeatExchange, Reach.Forced, "RemoveBuildingToBuildingHeatExchange"),

			new Plan(SimMessageHashes.AddDiseaseEmitter, Reach.Forced, "AddDiseaseEmitter"),
			new Plan(SimMessageHashes.ModifyDiseaseEmitter, Reach.Forced, "ModifyDiseaseEmitter"),
			new Plan(SimMessageHashes.RemoveDiseaseEmitter, Reach.Forced, "RemoveDiseaseEmitter"),

			new Plan(SimMessageHashes.AddRadiationEmitter, Reach.Forced, "AddRadiationEmitter"),
			new Plan(SimMessageHashes.ModifyRadiationEmitter, Reach.Forced, "ModifyRadiationEmitter"),
			new Plan(SimMessageHashes.RemoveRadiationEmitter, Reach.Forced, "RemoveRadiationEmitter"),

			new Plan(SimMessageHashes.SetWorldZones, Reach.Forced, "raw, one byte per game cell, as SubworldZoneRenderData.InitSimZones sends it"),
			new Plan(SimMessageHashes.ModifyCellWorldZone, Reach.Forced, "ModifyCellWorldZone"),
			new Plan(SimMessageHashes.DefineWorldOffsets, Reach.Forced, "DefineWorldOffsets, with this cluster's real WorldContainers"),
			new Plan(SimMessageHashes.SimData_ResizeAndInitializeVacuumCells, Reach.Forced, "SimDataResizeGridAndInitializeVacuumCells, aimed at the scratch rect"),
			new Plan(SimMessageHashes.SimData_FreeCells, Reach.Forced, "SimDataFreeCells, aimed at the scratch rect"),

			// --- nothing in the shipped game sends these --------------------------------------
			// --- forced in phase 5, because each of them rewrites something the run needs ------
			//
			// Ordered here the way SendWorldGeometry sends them, which is the way SaveLoader
			// sends them: AllocateCells, DefineWorldOffsets, ClearUnoccupiedCells, Load. The rig
			// runs that sequence backwards in destructiveness -- Load first while the world is
			// still coherent, AllocateCells last -- see SendWorldGeometry for why each one sits
			// where it does.
			new Plan(SimMessageHashes.Load, Reach.Forced, "Sim.Save then Sim.Load: a payload this sim itself produced, replayed through Klei's own reader"),
			new Plan(SimMessageHashes.ClearUnoccupiedCells, Reach.Forced, "raw, zero-length, as SaveLoader sends it right after DefineWorldOffsets"),
			new Plan(SimMessageHashes.AllocateCells, Reach.Forced, "Sim.AllocateCells with the grid's own dimensions -- the most destructive thing the rig does, and the last"),

			new Plan(SimMessageHashes.Elements_CreateInteractions, Reach.Unrecordable, "payload carries a POINTER to the interaction array, so a record of it holds an address"),

			new Plan(SimMessageHashes.SetVisibleCells, Reach.Unreachable, "declared in SimMessageHashes; no wrapper and no caller in either game assembly"),
			new Plan(SimMessageHashes.AddDiseaseConsumer, Reach.Unreachable, "declared; no wrapper and no caller. sim/simdll.cpp notes DiseaseConsumer has only Register/Unregister"),
			new Plan(SimMessageHashes.ModifyDiseaseConsumer, Reach.Unreachable, "declared; no wrapper and no caller"),
			new Plan(SimMessageHashes.RemoveDiseaseConsumer, Reach.Unreachable, "declared; no wrapper and no caller"),
			new Plan(SimMessageHashes.RadiationSickness, Reach.Unreachable, "NOT A MESSAGE: -727746602 is the SDBM hash of the DISEASE, see sim/radiation.h kRadiationSicknessHash"),
		};

		/// <summary>Ids this run actually put on the wire, in send order.</summary>
		private static readonly List<SimMessageHashes> sent = new List<SimMessageHashes>();

		private static readonly HashSet<int> sentSet = new HashSet<int>();

		private static Action<string> log = delegate { };

		private static void Note(SimMessageHashes id)
		{
			if (sentSet.Add((int)id))
			{
				sent.Add(id);
			}
		}

		// ------------------------------------------------------------------ resolved per run

		private static int gasCell, liquidCell, solidCell, scratchCell;
		private static ushort oxygenIdx, waterIdx, sandstoneIdx, vacuumIdx;

		private static int consumerHandle = -1;
		private static int emitterHandle = -1;
		private static int chunkHandle = -1;
		private static int buildingHandle = -1;
		private static int building2Handle = -1;
		private static int diseaseEmitterHandle = -1;
		private static int radiationEmitterHandle = -1;

		/// <summary>Disease index used by the disease traffic, or 255 when the table is empty.</summary>
		private static byte diseaseIdx = byte.MaxValue;

		/// <summary>
		/// Whether <see cref="SendWorldSnapshot"/> has run. It runs from BlueprintWorld, BEFORE
		/// <see cref="Begin"/> clears the sent-id set, so without this the coverage report would
		/// say Load never went out.
		/// </summary>
		private static bool snapshotSent;

		// ------------------------------------------------------------------ phases

		/// <summary>
		/// Resolves the cells and element indices the later phases aim at, and reports whether the
		/// canvas is the one this class was written against.
		///
		/// Returns false when it is not. Every phase after this one writes into specific cells, so
		/// a canvas mismatch would aim real messages at whatever happens to be at those
		/// coordinates -- which is the silent-wrong-world failure <c>RigHarness.BlueprintFile</c>
		/// exists to prevent, one level down.
		/// </summary>
		public static bool Begin(Func<bool, string, bool> check, Action<string> logger)
		{
			log = logger ?? delegate { };
			sent.Clear();
			sentSet.Clear();
			if (snapshotSent)
			{
				// Sent before this method was called; see the field's remarks.
				Note(SimMessageHashes.Load);
			}
			consumerHandle = emitterHandle = chunkHandle = -1;
			buildingHandle = building2Handle = -1;
			diseaseEmitterHandle = radiationEmitterHandle = -1;

			gasCell = Grid.XYToCell(GasX, GasY);
			liquidCell = Grid.XYToCell(LiquidX, LiquidY);
			solidCell = Grid.XYToCell(SolidX, SolidY);
			scratchCell = Grid.XYToCell(ScratchX, ScratchY);

			if (!check(Grid.IsValidCell(gasCell) && Grid.IsValidCell(liquidCell)
				&& Grid.IsValidCell(solidCell) && Grid.IsValidCell(scratchCell),
				"the four anchor cells are inside the world"))
			{
				return false;
			}

			// THE ELEMENT TABLE MUST BE KLEI'S ALONE, and this is the only assertion in the rig
			// about something the rig does not itself send.
			//
			// `Elements_CreateTable` is one of the 111 records, it is built MANAGED-side from
			// ElementLoader.elements, and every mod that adds an element is in that list by the
			// time the game sends it. Such a mod does not merely append rows -- ElementLoader
			// sorts, so a new element lands in the middle and SHIFTS every index after it.
			// Measured: a corpus recorded with Mod 2 enabled carried 216 elements
			// against Klei's 212, the four extras went in at index 133, and `vftest` went from
			// "all PASS" to a failed v16-fixture assertion, because the fixture's stored indices
			// no longer named the same elements. Nothing about the corpus LOOKED wrong.
			//
			// Klei's own SimHashes enum is the discriminator and it is exact rather than
			// heuristic: all 212 elements of a clean corpus resolve in it, and all four of Mod 2's
			// do not. That also makes this test general -- it catches a contributor's Workshop
			// mods, not just this project's, which is the case that actually matters once anyone
			// else records one.
			var foreign = new List<string>();
			for (int i = 0; i < ElementLoader.elements.Count; i++)
			{
				Element e = ElementLoader.elements[i];
				if (!Enum.IsDefined(typeof(SimHashes), (int)e.id))
				{
					foreign.Add(e.id.ToString() + " (index " + i + ")");
				}
			}
			if (!check(foreign.Count == 0,
				"the element table is Klei's alone -- " + ElementLoader.elements.Count
				+ " elements, none outside SimHashes"))
			{
				log("the element table carries " + foreign.Count + " element(s) Klei never "
					+ "shipped, so this corpus would not be a recording of the shipped game: "
					+ string.Join(", ", foreign.ToArray()));
				log("record with the rig's own mods enabled and nothing else");
				return false;
			}

			oxygenIdx = ElementLoader.GetElementIndex(SimHashes.Oxygen);
			waterIdx = ElementLoader.GetElementIndex(SimHashes.Water);
			sandstoneIdx = ElementLoader.GetElementIndex(SimHashes.SandStone);
			vacuumIdx = ElementLoader.GetElementIndex(SimHashes.Vacuum);

			if (!check(oxygenIdx != ushort.MaxValue && waterIdx != ushort.MaxValue
				&& sandstoneIdx != ushort.MaxValue,
				"the element table resolves oxygen, water and sandstone"))
			{
				return false;
			}

			// The disease table is real content and can legitimately be empty in a stripped build,
			// so an absent disease is reported rather than assumed. 255 is Klei's own "no disease"
			// sentinel and every disease-carrying payload accepts it, so the messages still go --
			// they simply carry no germs.
			diseaseIdx = byte.MaxValue;
			if (Db.Get() != null && Db.Get().Diseases != null && Db.Get().Diseases.Count > 0)
			{
				// FoodGerms rather than index 0 or RadiationPoisoning: it is added
				// unconditionally in the Diseases constructor, while RadiationPoisoning is behind
				// DlcManager.FeatureRadiationEnabled() and would make the corpus depend on which
				// DLC the recording machine owns -- the exact per-contributor variation this rig
				// exists to remove.
				diseaseIdx = Db.Get().Diseases.GetIndex(Db.Get().Diseases.FoodGerms.id);
			}
			log("disease index for the disease traffic: "
				+ (diseaseIdx == byte.MaxValue ? "none (255, no-disease sentinel)" : diseaseIdx.ToString()));

			log(string.Format("anchors: gas={0} ({1},{2}) liquid={3} ({4},{5}) solid={6} ({7},{8}) "
				+ "scratch={9} ({10},{11}) {12}x{13}",
				gasCell, GasX, GasY, liquidCell, LiquidX, LiquidY, solidCell, SolidX, SolidY,
				scratchCell, ScratchX, ScratchY, ScratchW, ScratchH));
			return true;
		}

		/// <summary>
		/// PHASE 1. Every Add* message, each with a REAL callback registered the way
		/// <c>SimComponent.SimRegister</c> registers one, so the sim handles come back and the
		/// Modify/Remove phases have genuine values to send.
		///
		/// The handles are not available when this returns: the sim replies through
		/// <c>componentStateChangedMessages</c> on a later frame, which is why the caller has to
		/// put checkpoints between the phases.
		/// </summary>
		public static void SendRegistrations()
		{
			SimMessages.AddElementConsumer(gasCell, ElementConsumer.Configuration.Element,
				SimHashes.Oxygen, 1, RegisterCallback(delegate(int h) { consumerHandle = h; }));
			Note(SimMessageHashes.AddElementConsumer);

			// max_pressure 0 means "never blocked by back pressure", and -1 for the blocked and
			// unblocked callbacks is the wrapper's own default: those two fire into
			// Game.callbackManager rather than the component manager, and this rig has no
			// building for them to notify.
			SimMessages.AddElementEmitter(0f,
				RegisterCallback(delegate(int h) { emitterHandle = h; }));
			Note(SimMessageHashes.AddElementEmitter);

			SimMessages.AddElementChunk(solidCell, SimHashes.SandStone, 100f, 300f, 1f, 1f, 1f,
				RegisterCallback(delegate(int h) { chunkHandle = h; }));
			Note(SimMessageHashes.AddElementChunk);

			// Two building heat exchangers, because the building-TO-building family below needs
			// two handles to relate to each other.
			SimMessages.AddBuildingHeatExchange(new Extents(GasX, GasY, 2, 2),
				200f, 300f, 1f, 0f, sandstoneIdx,
				RegisterCallback(delegate(int h) { buildingHandle = h; }));
			SimMessages.AddBuildingHeatExchange(new Extents(GasX + 4, GasY, 2, 2),
				200f, 310f, 1f, 0f, sandstoneIdx,
				RegisterCallback(delegate(int h) { building2Handle = h; }));
			Note(SimMessageHashes.AddBuildingHeatExchange);

			SimMessages.AddDiseaseEmitter(
				RegisterCallback(delegate(int h) { diseaseEmitterHandle = h; }));
			Note(SimMessageHashes.AddDiseaseEmitter);

			SimMessages.AddRadiationEmitter(
				RegisterCallback(delegate(int h) { radiationEmitterHandle = h; }),
				liquidCell, 3, 3, 100f, 1f, 1f, 0f, 360f,
				RadiationEmitter.RadiationEmitterType.Constant);
			Note(SimMessageHashes.AddRadiationEmitter);

			log("phase 1: 7 registrations sent, waiting for the sim to hand back their handles");
		}

		/// <summary>
		/// Whether every registration from phase 1 has been answered. The caller checkpoints until
		/// this is true rather than waiting a fixed number of frames, because the number of frames
		/// depends on the machine.
		/// </summary>
		public static bool RegistrationsComplete
		{
			get
			{
				return Sim.IsValidHandle(consumerHandle) && Sim.IsValidHandle(emitterHandle)
					&& Sim.IsValidHandle(chunkHandle) && Sim.IsValidHandle(buildingHandle)
					&& Sim.IsValidHandle(building2Handle)
					&& Sim.IsValidHandle(diseaseEmitterHandle)
					&& Sim.IsValidHandle(radiationEmitterHandle);
			}
		}

		/// <summary>A one-line account of which handles came back, for the log and for a failure.</summary>
		public static string HandleReport()
		{
			return string.Format("consumer={0} emitter={1} chunk={2} building={3} building2={4} "
				+ "diseaseEmitter={5} radiationEmitter={6}",
				consumerHandle, emitterHandle, chunkHandle, buildingHandle, building2Handle,
				diseaseEmitterHandle, radiationEmitterHandle);
		}

		/// <summary>
		/// PHASE 2. The Modify*/Set* families, each aimed at the handle its Add returned in
		/// phase 1.
		/// </summary>
		public static void SendModifications()
		{
			if (Sim.IsValidHandle(consumerHandle))
			{
				SimMessages.SetElementConsumerData(consumerHandle, gasCell, 0.5f);
				Note(SimMessageHashes.SetElementConsumerData);
			}

			if (Sim.IsValidHandle(emitterHandle))
			{
				SimMessages.ModifyElementEmitter(emitterHandle, gasCell, 1, SimHashes.Oxygen,
					1f, 0.1f, 300f, 2f, diseaseIdx, diseaseIdx == byte.MaxValue ? 0 : 100);
				Note(SimMessageHashes.ModifyElementEmitter);
			}

			if (Sim.IsValidHandle(chunkHandle))
			{
				SimMessages.SetElementChunkData(chunkHandle, 320f, 100f);
				Note(SimMessageHashes.SetElementChunkData);

				SimMessages.MoveElementChunk(chunkHandle, solidCell + 1);
				Note(SimMessageHashes.MoveElementChunk);

				SimMessages.ModifyElementChunkEnergy(chunkHandle, 5f);
				Note(SimMessageHashes.ModifyElementChunkEnergy);

				SimMessages.ModifyElementChunkTemperatureAdjuster(chunkHandle, 310f, 50f, 1f);
				Note(SimMessageHashes.ModifyChunkTemperatureAdjuster);
			}

			if (Sim.IsValidHandle(buildingHandle))
			{
				SimMessages.ModifyBuildingHeatExchange(buildingHandle,
					new Extents(GasX, GasY, 2, 2), 200f, 305f, 1f, 400f, 0.5f, sandstoneIdx);
				Note(SimMessageHashes.ModifyBuildingHeatExchange);

				SimMessages.ModifyBuildingEnergy(buildingHandle, 1f, 0f, 400f);
				Note(SimMessageHashes.ModifyBuildingEnergy);
			}

			if (Sim.IsValidHandle(diseaseEmitterHandle))
			{
				SimMessages.ModifyDiseaseEmitter(diseaseEmitterHandle, gasCell, 1, diseaseIdx,
					1f, diseaseIdx == byte.MaxValue ? 0 : 100);
				Note(SimMessageHashes.ModifyDiseaseEmitter);
			}

			if (Sim.IsValidHandle(radiationEmitterHandle))
			{
				SimMessages.ModifyRadiationEmitter(radiationEmitterHandle, liquidCell, 4, 4,
					150f, 1f, 1f, 0f, 360f, RadiationEmitter.RadiationEmitterType.Pulsing);
				Note(SimMessageHashes.ModifyRadiationEmitter);
			}

			SendBuildingToBuilding();
			log("phase 2: modifications sent");
		}

		/// <summary>
		/// The building-to-building heat exchange family, split out because ITS NAMES DO NOT LINE
		/// UP and the mismatch is easy to get wrong.
		///
		/// <c>SimMessageHashes.AddBuildingToBuildingHeatExchange</c> (-1338718217) carries
		/// {callbackIdx, structureTemperatureHandler} and is sent by
		/// <c>SimMessages.RegisterBuildingToBuildingHeatExchange</c>, while
		/// <c>SimMessageHashes.AddInContactBuildingToBuildingToBuildingHeatExchange</c>
		/// (-1586724321) carries {selfHandler, buildingInContactHandle, cellsInContact} and is
		/// sent by the method actually CALLED <c>AddBuildingToBuildingHeatExchange</c>. Reading
		/// either name and reaching for the same-named thing on the other side sends the wrong
		/// message with a payload of the wrong size.
		/// </summary>
		private static void SendBuildingToBuilding()
		{
			if (!Sim.IsValidHandle(buildingHandle) || !Sim.IsValidHandle(building2Handle))
			{
				return;
			}

			SimMessages.RegisterBuildingToBuildingHeatExchange(buildingHandle);
			SimMessages.RegisterBuildingToBuildingHeatExchange(building2Handle);
			Note(SimMessageHashes.AddBuildingToBuildingHeatExchange);

			SimMessages.AddBuildingToBuildingHeatExchange(buildingHandle, building2Handle, 2);
			Note(SimMessageHashes.AddInContactBuildingToBuildingToBuildingHeatExchange);

			SimMessages.RemoveBuildingInContactFromBuildingToBuildingHeatExchange(
				buildingHandle, building2Handle);
			Note(SimMessageHashes.RemoveBuildingInContactFromBuildingToBuildingHeatExchange);

			SimMessages.RemoveBuildingToBuildingHeatExchange(building2Handle);
			Note(SimMessageHashes.RemoveBuildingToBuildingHeatExchange);
		}

		/// <summary>
		/// PHASE 3. The per-cell traffic: everything that names a cell rather than a handle.
		///
		/// These are the messages a running colony sends constantly, so a played corpus already
		/// has them -- but a canvas with no duplicants, no buildings and no dig orders sends none
		/// of them, which is exactly the thinning this class exists to prevent.
		/// </summary>
		public static void SendCellTraffic()
		{
			SimMessages.Dig(solidCell);
			Note(SimMessageHashes.Dig);

			SimMessages.ModifyCell(gasCell, oxygenIdx, 300f, 1.8f, byte.MaxValue, 0);
			Note(SimMessageHashes.ModifyCell);

			SimMessages.ModifyEnergy(gasCell, 1f, 400f, SimMessages.EnergySourceID.DebugHeat);
			Note(SimMessageHashes.ModifyCellEnergy);

			SimMessages.SetInsulation(gasCell, 0.5f);
			Note(SimMessageHashes.SetInsulationValue);

			SimMessages.SetStrength(solidCell, 1, 1f);
			Note(SimMessageHashes.SetStrengthValue);

			// Both directions of the same id, because `set` is a payload field rather than a
			// separate message and a corpus with only one of them cannot show that.
			SimMessages.SetCellProperties(solidCell, (byte)Sim.Cell.Properties.Unbreakable);
			SimMessages.ClearCellProperties(solidCell, (byte)Sim.Cell.Properties.Unbreakable);
			Note(SimMessageHashes.ChangeCellProperties);

			SimMessages.SetBackwallData(gasCell, sandstoneIdx, 100f, 300f);
			Note(SimMessageHashes.ModifyBackwallData);

			// -1 for the callback, which is the wrapper's own default and therefore the value the
			// game itself sends. It has to be: Game.OnSimDataUpdate calls Release on the
			// mass-consumed handle with NO IsVersionValid guard, so an invented index here would
			// be an exception on the game thread.
			SimMessages.ConsumeMass(liquidCell, SimHashes.Water, 0.1f, 1);
			Note(SimMessageHashes.MassConsumption);

			SimMessages.EmitMass(liquidCell, waterIdx, 0.1f, 300f, diseaseIdx,
				diseaseIdx == byte.MaxValue ? 0 : 100);
			Note(SimMessageHashes.MassEmission);

			if (diseaseIdx != byte.MaxValue)
			{
				SimMessages.ModifyDiseaseOnCell(gasCell, diseaseIdx, 1000);
				Note(SimMessageHashes.CellDiseaseModification);
			}

			SimMessages.ConsumeDisease(gasCell, 0.5f, 100, -1);
			Note(SimMessageHashes.ConsumeDisease);

			SimMessages.ModifyRadiationOnCell(liquidCell, 10f);
			Note(SimMessageHashes.CellRadiationModification);

			SimMessages.ModifyRadiationParams(RadiationParams.LingerRate, 0.5f);
			Note(SimMessageHashes.RadiationParamsModification);

			SimMessages.SetSavedOptionValue(SimMessages.SimSavedOptions.ENABLE_DIAGONAL_FALLING_SAND, 1);
			Note(SimMessageHashes.SetSavedOptions);

			Sim.DebugProperties props = default(Sim.DebugProperties);
			props.buildingTemperatureScale = 1f;
			props.buildingToBuildingTemperatureScale = 1f;
			props.isDebugEditing = 0;
			SimMessages.SetDebugProperties(props);
			Note(SimMessageHashes.SetDebugProperties);

			// Zero-length, sent raw, exactly as DebugHandler does on the profiler key. There is no
			// SimMessages wrapper for it and no payload to get wrong.
			SendRaw(SimMessageHashes.ToggleProfiler, null);

			log("phase 3: per-cell traffic sent");
		}

		/// <summary>
		/// PHASE 4. The Remove* family, which has to come after the Modify* phase or there would
		/// be no live registration left to modify.
		/// </summary>
		public static void SendRemovals()
		{
			if (Sim.IsValidHandle(consumerHandle))
			{
				SimMessages.RemoveElementConsumer(-1, consumerHandle);
				Note(SimMessageHashes.RemoveElementConsumer);
			}
			if (Sim.IsValidHandle(emitterHandle))
			{
				SimMessages.RemoveElementEmitter(-1, emitterHandle);
				Note(SimMessageHashes.RemoveElementEmitter);
			}
			if (Sim.IsValidHandle(chunkHandle))
			{
				SimMessages.RemoveElementChunk(chunkHandle, -1);
				Note(SimMessageHashes.RemoveElementChunk);
			}
			if (Sim.IsValidHandle(buildingHandle))
			{
				SimMessages.RemoveBuildingHeatExchange(buildingHandle);
				Note(SimMessageHashes.RemoveBuildingHeatExchange);
			}
			if (Sim.IsValidHandle(diseaseEmitterHandle))
			{
				SimMessages.RemoveDiseaseEmitter(-1, diseaseEmitterHandle);
				Note(SimMessageHashes.RemoveDiseaseEmitter);
			}
			if (Sim.IsValidHandle(radiationEmitterHandle))
			{
				SimMessages.RemoveRadiationEmitter(-1, radiationEmitterHandle);
				Note(SimMessageHashes.RemoveRadiationEmitter);
			}
			log("phase 4: removals sent");
		}

		/// <summary>
		/// PHASE 5, AND IT IS DESTRUCTIVE ON PURPOSE. The world-geometry messages -- the DRAIN.md
		/// rare list, and the whole reason a save-free corpus needs a rig rather than a blank
		/// canvas.
		///
		/// <c>SimData_ResizeAndInitializeVacuumCells</c> and <c>SimData_FreeCells</c> are sent by
		/// the game only when a world is added to or removed from the cluster
		/// (<c>Grid.cs</c>), and they genuinely rewrite grid structure. They are aimed at the
		/// scratch rectangle the blueprint reserves, they are the last thing this class does, and
		/// the rig quits immediately afterwards. NOTHING MAY BE MEASURED AFTER THIS RETURNS.
		/// </summary>
		public static void SendWorldGeometry()
		{
			// AllocateCells goes LAST, and everything else is in Klei's own order.
			// SendWorldSnapshot (Load) is not here at all -- see its own remarks for why it has
			// to happen before the rig has touched anything.
			SendWorldZones();

			SimMessages.ModifyCellWorldZone(gasCell, 1);
			Note(SimMessageHashes.ModifyCellWorldZone);

			SendWorldOffsets();

			// Zero-length and raw, which is how SaveLoader sends it -- there is no wrapper, and
			// it goes immediately after DefineWorldOffsets in both of SaveLoader's two paths
			// because "unoccupied" means "outside every world offset just defined".
			SendRaw(SimMessageHashes.ClearUnoccupiedCells, null);

			SimMessages.SimDataResizeGridAndInitializeVacuumCells(
				new Vector2I(Grid.WidthInCells, Grid.HeightInCells),
				ScratchW, ScratchH, ScratchX, ScratchY);
			Note(SimMessageHashes.SimData_ResizeAndInitializeVacuumCells);

			SimMessages.SimDataFreeCells(ScratchW, ScratchH, ScratchX, ScratchY);
			Note(SimMessageHashes.SimData_FreeCells);

			// THE LAST MESSAGE OF THE RUN. Sim.AllocateCells reallocates every per-cell array in
			// the world; the Grid arrays the managed side holds are bound to the pointers
			// Sim.Start handed back and are not rebound by this, so after it returns the game is
			// reading memory the sim has replaced. That is survivable for exactly as long as
			// nothing simulates or renders -- which is why the rig quits in this same frame, and
			// why this call is placed after everything else rather than in Klei's order.
			//
			// Same dimensions as the grid already has: the payload the game sends is (width,
			// height, radiationEnabled, headless), and a differing size would be a resize this
			// canvas has no reason to record.
			Sim.AllocateCells(Grid.WidthInCells, Grid.HeightInCells);
			Note(SimMessageHashes.AllocateCells);

			log("phase 5: world geometry sent -- the scratch rect is now undefined and the run is over");
		}

		/// <summary>
		/// PHASE 0, AND IT MUST BE FIRST. <c>Load</c>, carrying a payload THIS SIM PRODUCED rather
		/// than one this file invented.
		///
		/// <c>Load</c> is the one id in the plan whose payload is a serialised world: it is what
		/// <c>SaveLoader</c> feeds the sim out of a save file's <c>Sim</c> stream, and there is no
		/// honest way to hand-build one. So the rig does what the save path does in reverse --
		/// <c>Sim.Save</c> (which is <c>SIM_BeginSave</c>/<c>SIM_EndSave</c>, an export rather
		/// than a message, so it records nothing itself) writes the live world out, and
		/// <c>Sim.Load</c> reads exactly those bytes back through Klei's own reader. The corpus
		/// gets a real <c>Load</c> record whose contents are a real world, and the state the sim
		/// ends in is the state it was already in.
		///
		/// A canvas run reaches this and a save-loading run does not, which is the opposite of
		/// what the first version of this file assumed: <c>BlueprintWorld</c> replaces
		/// <c>SaveLoader</c>'s <c>AllocateCells</c> + <c>Load</c> pair with a single
		/// <c>SimDataInitializeFromCells</c>, so on a canvas nothing sends <c>Load</c> unless the
		/// rig does.
		///
		/// <b>WHY IT IS FIRST AND NOT LAST, which is where it started.</b> This record is the only
		/// one in the corpus whose contents are the WORLD rather than an argument list, so it is
		/// the only one that changes when the world does. Sent from phase 5 it recorded a world
		/// that had absorbed every message the rig had just sent and then evolved for whatever
		/// number of sim ticks the machine's frame rate happened to allow: three recordings agreed
		/// on 110 of 111 records and disagreed on 17,247 bytes of this one. Sent first, it records
		/// the canvas as the blueprint built it -- and the blueprint is sealed and uniform
		/// (see corpus.yaml's two tanks) precisely so that "as built" and "after N ticks" are the
		/// same world.
		/// </summary>
		public static void SendWorldSnapshot()
		{
			// Called from BlueprintWorld before the rig exists, so `log` has not been set yet.
			// Nothing here may depend on Begin having run.
			Action<string> say = log ?? (Action<string>)delegate(string m)
			{
				FrameworkLog.Info("SimCorpus: " + m);
			};

			byte[] blob;
			using (MemoryStream stream = new MemoryStream())
			{
				using (BinaryWriter writer = new BinaryWriter(stream))
				{
					// (0, 0) is this canvas's world offset -- SaveLoader passes each world's own
					// WorldOffset, and a blueprint canvas has exactly one world, at the origin.
					Sim.Save(writer, 0, 0);
					writer.Flush();
				}
				blob = stream.ToArray();
			}

			// Sim.Save writes an int32 length followed by the bytes, and Sim.Load reads the same
			// pair, so the blob is handed back whole rather than re-framed here.
			int result = Sim.Load(new FastReader(blob));
			snapshotSent = true;
			Note(SimMessageHashes.Load);
			say("Load: " + blob.Length + " bytes saved out of the sim before its first tick and "
				+ "read straight back (Sim.Load returned " + result + ", 0 is success)");
		}

		/// <summary>
		/// <c>SetWorldZones</c>, sent raw because it has no <c>SimMessages</c> wrapper: the game's
		/// only sender is <c>SubworldZoneRenderData.InitSimZones</c>, which pins a
		/// <c>byte[]</c> and calls <c>Sim.SIM_HandleMessage</c> directly.
		///
		/// ONE BYTE PER GAME CELL. The SimDLL's <c>SetWorldZones</c> reads <c>width - 2</c> bytes for each of
		/// <c>height - 2</c> rows -- but those are <c>SimData</c>'s PADDED dimensions
		/// (<c>sim/world.h</c>: <c>width_ = game_width + 2</c>), so <c>width - 2</c> is the full
		/// GAME width and the payload it wants is <c>Grid.CellCount</c> bytes. A short payload
		/// over-reads silently in release, because Klei's end-of-buffer check only reaches
		/// <c>DebugBreak</c> when a debugger is attached, and about one time in a hundred the tail
		/// lands within 24 bytes of an unmapped page and faults.
		/// </summary>
		private static void SendWorldZones()
		{
			byte[] zones = new byte[Grid.CellCount];
			for (int i = 0; i < zones.Length; i++)
			{
				// Two zones, split down the middle, so the payload is not uniform and a reader
				// that ignored it would be distinguishable from one that did not.
				zones[i] = (byte)((i % Grid.WidthInCells) < (Grid.WidthInCells / 2) ? 0 : 1);
			}
			SendRaw(SimMessageHashes.SetWorldZones, zones);
			log("SetWorldZones: " + zones.Length + " bytes, one per game cell ("
				+ Grid.WidthInCells + "x" + Grid.HeightInCells + ")");
		}

		/// <summary>
		/// <c>DefineWorldOffsets</c> with THIS cluster's real worlds, taken from
		/// <c>ClusterManager</c> exactly as <c>SaveLoader</c> takes them.
		///
		/// Real data rather than a synthetic rectangle, because the message stages into the sim's
		/// pending world-offset list and only becomes live at the next allocation
		/// (<c>sim/world.h</c>) -- so sending the truth is both safe and the only version worth
		/// recording.
		/// </summary>
		private static void SendWorldOffsets()
		{
			var offsets = new List<SimMessages.WorldOffsetData>();
			if (ClusterManager.Instance != null && ClusterManager.Instance.WorldContainers != null)
			{
				foreach (WorldContainer world in ClusterManager.Instance.WorldContainers)
				{
					SimMessages.WorldOffsetData d = default(SimMessages.WorldOffsetData);
					d.worldOffsetX = world.WorldOffset.x;
					d.worldOffsetY = world.WorldOffset.y;
					d.worldSizeX = world.WorldSize.x;
					d.worldSizeY = world.WorldSize.y;
					offsets.Add(d);
				}
			}
			if (offsets.Count == 0)
			{
				// A canvas with no cluster layout is exactly the gap this rig documents, so the
				// message still goes -- with the one world that does exist, described by the grid
				// itself. Saying so in the log matters more than the value.
				SimMessages.WorldOffsetData d = default(SimMessages.WorldOffsetData);
				d.worldOffsetX = 0;
				d.worldOffsetY = 0;
				d.worldSizeX = Grid.WidthInCells;
				d.worldSizeY = Grid.HeightInCells;
				offsets.Add(d);
				log("DefineWorldOffsets: no ClusterManager worlds on this canvas (the documented "
					+ "canvas gap), describing the grid itself instead");
			}
			SimMessages.DefineWorldOffsets(offsets);
			Note(SimMessageHashes.DefineWorldOffsets);
			log("DefineWorldOffsets: " + offsets.Count + " world(s)");
		}

		// ------------------------------------------------------------------ reporting

		/// <summary>
		/// The coverage table: every id in <see cref="kPlan"/>, what was expected of it, and
		/// whether it happened. This is the rig's verdict.
		/// </summary>
		public static void Report(Func<bool, string, bool> check, Action<string> logger)
		{
			int forced = 0, forcedSent = 0, unreachable = 0, unrecordable = 0, boot = 0;
			var missing = new List<string>();

			for (int i = 0; i < kPlan.Length; i++)
			{
				Plan p = kPlan[i];
				bool wasSent = sentSet.Contains((int)p.id);
				switch (p.reach)
				{
					case Reach.Boot:
						boot++;
						break;
					case Reach.Unreachable:
						unreachable++;
						break;
					case Reach.Unrecordable:
						unrecordable++;
						break;
					case Reach.Forced:
						forced++;
						if (wasSent)
						{
							forcedSent++;
						}
						else
						{
							missing.Add(p.id.ToString() + " (" + p.note + ")");
						}
						break;
				}
			}

			logger(string.Format("coverage: {0} declared = {1} boot-owned + {2} forced + "
				+ "{3} unreachable + {4} unrecordable", kPlan.Length, boot, forced, unreachable,
				unrecordable));
			logger("this run forced " + forcedSent + " of " + forced
				+ "; the corpus should hold " + (boot + forced) + " distinct ids");

			for (int i = 0; i < missing.Count; i++)
			{
				logger("  NOT SENT: " + missing[i]);
			}

			check(missing.Count == 0,
				"every forceable message id was put on the wire (" + forcedSent + "/" + forced + ")");

			// A guard against the game gaining a message and this file not noticing. The plan is
			// the specification, so an id in the enum with no row here is a gap in the
			// specification rather than a gap in the run.
			var planned = new HashSet<int>();
			for (int i = 0; i < kPlan.Length; i++)
			{
				planned.Add((int)kPlan[i].id);
			}
			var unplanned = new List<string>();
			foreach (SimMessageHashes id in Enum.GetValues(typeof(SimMessageHashes)))
			{
				if (!planned.Contains((int)id))
				{
					unplanned.Add(id.ToString());
				}
			}
			for (int i = 0; i < unplanned.Count; i++)
			{
				logger("  UNCLASSIFIED: " + unplanned[i] + " is in SimMessageHashes but has no row "
					+ "in SimCorpus.kPlan -- classify it");
			}
			check(unplanned.Count == 0,
				"every id in SimMessageHashes is classified in this file's plan");
		}

		// ------------------------------------------------------------------ plumbing

		/// <summary>
		/// Registers a genuine sim-component callback and returns its index, which is what a
		/// message's <c>callbackIdx</c> field wants.
		///
		/// The same two lines <c>SimComponent.SimRegister</c> runs. Doing it any other way -- a
		/// guessed index, or -1 where the sim will report back -- means either no handle ever
		/// arrives or an out-of-range index reaches <c>HandleVector</c> on the game thread.
		/// </summary>
		private static int RegisterCallback(Action<int> onHandle)
		{
			HandleVector<Game.ComplexCallbackInfo<int>>.Handle h =
				Game.Instance.simComponentCallbackManager.Add(
					delegate(int handle, object data) { onHandle(handle); },
					null, "SimCorpus");
			return h.index;
		}

		/// <summary>
		/// Sends a message with no <c>SimMessages</c> wrapper, the way the game's own raw senders
		/// do it. Three ids need this: <c>SetWorldZones</c> (a pinned <c>byte[]</c>, as
		/// <c>SubworldZoneRenderData.InitSimZones</c> sends it), <c>ToggleProfiler</c> and
		/// <c>ClearUnoccupiedCells</c> (both zero-length, as <c>DebugHandler</c> and
		/// <c>SaveLoader</c> send them).
		/// </summary>
		private static unsafe void SendRaw(SimMessageHashes id, byte[] payload)
		{
			if (payload == null || payload.Length == 0)
			{
				Sim.SIM_HandleMessage((int)id, 0, null);
			}
			else
			{
				fixed (byte* msg = payload)
				{
					Sim.SIM_HandleMessage((int)id, payload.Length, msg);
				}
			}
			Note(id);
		}
	}
}
