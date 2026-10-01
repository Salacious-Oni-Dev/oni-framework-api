using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// The managed half of the gas-mixture layer: several gases in one cell. The game keeps
	/// seeing one dominant element per cell through <c>GameDataUpdate</c>, whose layout is the
	/// game's and is not extended; this class is how a mod reads and changes the full mixture.
	///
	/// A Harmony patch on a vanilla read like <c>Grid.Element[cell]</c> can call
	/// <see cref="TryGetDominant"/> first; a gas-mixture-active cell substitutes the answer
	/// from here instead of the normal vanilla array, and every other cell falls straight
	/// through to vanilla behavior unchanged.
	///
	/// Unlike <see cref="ThermalMassBonus"/>, which reuses Klei's own
	/// <c>Sim.SIM_HandleMessage</c> P/Invoke (already public in Assembly-CSharp) because it
	/// only ever sends a message, this reads a return value back -- something no vanilla
	/// message does -- so it needs its own <c>SimDLL</c> binding rather than reusing Klei's.
	///
	/// <b>ON A VANILLA SimDLL EVERY READ HERE DEGRADES SILENTLY.</b> Klei's DLL exports none of
	/// these symbols, so the first call raises <see cref="EntryPointNotFoundException"/>; this
	/// class catches it, latches <see cref="Available"/> to false, says so once, and every read
	/// answers "no mixture state for this cell" from then on -- the same answer those methods
	/// already give for a cell the mixture layer does not own, and the same contract the rest of
	/// this assembly keeps. A caller never has to catch it: a lookup polled every frame on a
	/// stock install would otherwise throw once per frame.
	///
	/// <b>The pure functions are the exception, deliberately.</b>
	/// <see cref="CombineTemperature"/>, <see cref="AdiabaticFillTemperature"/>,
	/// <see cref="EqualizeSingleSpeciesMass"/>, <see cref="LiquidVolumeFromMass"/>,
	/// <see cref="EqualizeLiquidVolumeMass"/> and <see cref="ComputePhaseChangeStep"/> return a
	/// physical quantity, not a lookup, so there is no value they could return that means "no
	/// answer" -- 0 K and 0 kg are answers a caller would use. They therefore THROW
	/// <see cref="InvalidOperationException"/> naming the missing export, rather than inventing a
	/// number. Check <see cref="Available"/> once before using them; do not wrap them in a try.
	/// </summary>
	public static class GasMixtureFacade
	{
		// ------------------------------------------------------------------ availability
		//
		// One latch for the whole class rather than one per export: they are all added by the
		// same DLL in the same build, so "SIM_GasComposition is missing" and "SIM_GasPressure is
		// missing" are never separately true, and a per-export latch would mean each one has to
		// throw once before it stops.

		private static bool unavailable;
		private static bool warned;

		/// <summary>
		/// Whether this SimDLL has the gas-mixture exports at all. True until a call has actually
		/// failed -- an export cannot be probed for without calling it, the same shape
		/// <see cref="SimVersion"/> and the extension-registry facades use. (Contrast
		/// <see cref="SimExtPhases.Available"/>, which can answer by asking, because its exports
		/// take no barrier and are legal before a world exists. These are not.)
		///
		/// <b>Check this before calling the pure functions</b> -- they throw rather than invent a
		/// number. The per-cell reads need no check; they answer "no mixture state" on their own.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// Latch, and say so ONCE. Once per frame for the rest of the session is how the defect
		/// this replaces presented, so the log line is deliberately not per call.
		/// </summary>
		private static void MarkUnavailable(string export)
		{
			unavailable = true;
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("GasMixtureFacade: this SimDLL does not export " + export
					+ ", so it is not the custom SimDLL. Every gas-mixture read now answers 'no "
					+ "mixture state' for the rest of the session, and the pure functions "
					+ "(CombineTemperature, AdiabaticFillTemperature, EqualizeSingleSpeciesMass, "
					+ "LiquidVolumeFromMass, EqualizeLiquidVolumeMass, ComputePhaseChangeStep) "
					+ "throw. Check GasMixtureFacade.Available before calling those.");
			}
		}

		/// <summary>
		/// The SimDLL was not resolvable at all -- a different fact from "this DLL has no
		/// gas-mixture exports", so it is reported and NOT latched: caching it would answer
		/// unavailable for the rest of the session on the strength of one bad moment.
		/// <see cref="SimVersion"/> makes the same argument about its own null.
		/// </summary>
		private static void ReportMissingLibrary(DllNotFoundException e)
		{
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("GasMixtureFacade: could not reach the SimDLL: " + e.Message
					+ ". Not latched -- this is retried on the next call.");
			}
		}

		/// <summary>
		/// What a pure function throws instead of returning a number it does not have.
		/// </summary>
		private static InvalidOperationException NoExport(string export)
		{
			return new InvalidOperationException(
				"GasMixtureFacade." + export + " needs the custom SimDLL; this install has the "
				+ "stock one, which does not export it. This method computes a physical quantity, "
				+ "so it has no 'no answer' return value -- check GasMixtureFacade.Available "
				+ "first and take your own path when it is false.");
		}

		// Not part of the game's own Sim class bindings: an export only the replacement SimDLL
		// has, declared here because nothing in the game declares it.
		[DllImport("SimDLL")]
		private static extern ushort SIM_GasDominantElement(int cellIdx, out float outMass);

		/// <summary>
		/// The mixture never holds more than this many species per cell (gas_mixture_abi.h's
		/// <c>kMaxSpeciesPerCell</c>) -- fixed so the reads here can use stack-allocated buffers
		/// instead of allocating on every call.
		///
		/// Public because <see cref="ReadComposition"/> asks the CALLER for buffers, and a caller
		/// cannot size them without this. It is a compile-time constant on the native side, so a
		/// buffer sized by it is sized correctly for the life of the process.
		/// </summary>
		public const int MaxSpeciesPerCell = 8;

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_GasComposition(int cellIdx, ushort* outSpecies, float* outMass, int maxSlots);

		/// <summary>
		/// The species holding the most mass in <paramref name="cell"/>'s gas mixture, or
		/// <c>false</c> if the cell holds no gas-mixture state at all -- the signal a caller
		/// should read as "fall back to the normal vanilla value for this cell."
		/// </summary>
		/// <param name="cell">A game cell index, e.g. from <c>Grid.PosToCell</c> -- the same
		/// space every other per-cell sim call in this framework already uses.</param>
		/// <param name="elementIdx">The ElementTable index of the dominant species, valid
		/// only when this returns <c>true</c>.</param>
		/// <param name="totalMassKg">The TOTAL mass across every species in the mixture, not
		/// just the dominant one's share -- what a vanilla mass reader actually wants to see.
		/// Zero when this returns <c>false</c>.</param>
		public static bool TryGetDominant(int cell, out ushort elementIdx, out float totalMassKg)
		{
			ushort result;
			if (unavailable)
			{
				elementIdx = 0;
				totalMassKg = 0f;
				return false;
			}
			try
			{
				result = SIM_GasDominantElement(cell, out totalMassKg);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_GasDominantElement");
				elementIdx = 0;
				totalMassKg = 0f;
				return false;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				elementIdx = 0;
				totalMassKg = 0f;
				return false;
			}
			if (result == 0xFFFF)
			{
				elementIdx = 0;
				totalMassKg = 0f;
				return false;
			}
			elementIdx = result;
			return true;
		}

		/// <summary>
		/// One species' share of a cell's gas mixture -- what <see cref="TryGetComposition"/>
		/// returns per entry.
		/// </summary>
		public readonly struct GasComponent
		{
			public readonly ushort ElementIdx;
			public readonly float MassKg;

			public GasComponent(ushort elementIdx, float massKg)
			{
				ElementIdx = elementIdx;
				MassKg = massKg;
			}
		}

		/// <summary>
		/// Every species actually occupying <paramref name="cell"/>'s gas mixture, not just
		/// the dominant one -- what <see cref="TryGetDominant"/> collapses to a single winner.
		/// This is the one that can actually show real mixing: two Gas Element Sensors in one
		/// mixing room, one over O2 and one over CO2, only look "mixed" here, not through
		/// TryGetDominant alone. Returns an empty array (not null) if the cell holds no
		/// gas-mixture state.
		/// </summary>
		public static unsafe GasComponent[] TryGetComposition(int cell)
		{
			if (unavailable)
			{
				return System.Array.Empty<GasComponent>();
			}
			ushort* species = stackalloc ushort[MaxSpeciesPerCell];
			float* mass = stackalloc float[MaxSpeciesPerCell];
			int count;
			try
			{
				count = SIM_GasComposition(cell, species, mass, MaxSpeciesPerCell);
			}
			catch (EntryPointNotFoundException)
			{
				// THE SITE THAT PROVED THIS CLASS NEEDED A LATCH. Its caller is an overlay
				// Update(), so on a stock SimDLL it threw once per frame -- 1989 times in an
				// eight-second run -- and Unity logged every one as an unhandled exception.
				MarkUnavailable("SIM_GasComposition");
				return System.Array.Empty<GasComponent>();
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return System.Array.Empty<GasComponent>();
			}
			if (count <= 0)
			{
				return System.Array.Empty<GasComponent>();
			}
			var result = new GasComponent[count];
			for (int i = 0; i < count; i++)
			{
				result[i] = new GasComponent(species[i], mass[i]);
			}
			return result;
		}

		/// <summary>
		/// The same answer as <see cref="TryGetComposition"/> WITHOUT ALLOCATING: fills the
		/// caller's own buffers and returns how many slots were written. 0 means the cell holds
		/// no gas-mixture state, which is the same "fall back to vanilla" signal the empty array
		/// carries.
		///
		/// <b>Why this exists.</b> <see cref="TryGetComposition"/> allocates a
		/// <see cref="GasComponent"/> array on every call, by design -- it is the convenient
		/// shape and most callers ask about one cell. A per-tick sweep over several cells pays
		/// that once per cell per tick, and in ONI allocation rather than CPU time is what causes
		/// visible stutter: the freeze is Mono GC, and the only lever a mod has is how often a
		/// collection happens. A caller in a <c>Sim200ms</c> that already owns its scratch arrays
		/// should use this one and keep the arrays as fields.
		///
		/// <paramref name="species"/> and <paramref name="massKg"/> must each hold at least
		/// <see cref="MaxSpeciesPerCell"/> entries; anything shorter is refused with 0 rather
		/// than partially filled, since a caller that sized its buffer wrong wants to find out
		/// rather than to silently read a truncated composition as the whole of one.
		/// </summary>
		public static unsafe int ReadComposition(int cell, ushort[] species, float[] massKg)
		{
			if (unavailable || species == null || massKg == null
				|| species.Length < MaxSpeciesPerCell || massKg.Length < MaxSpeciesPerCell)
			{
				return 0;
			}
			ushort* speciesBuffer = stackalloc ushort[MaxSpeciesPerCell];
			float* massBuffer = stackalloc float[MaxSpeciesPerCell];
			int count;
			try
			{
				count = SIM_GasComposition(cell, speciesBuffer, massBuffer, MaxSpeciesPerCell);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_GasComposition");
				return 0;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return 0;
			}
			if (count <= 0)
			{
				return 0;
			}
			if (count > MaxSpeciesPerCell)
			{
				// The DLL cannot exceed the cap it was handed, but clamping costs nothing and a
				// caller's buffer is the thing that would be overrun if it ever did.
				count = MaxSpeciesPerCell;
			}
			for (int i = 0; i < count; i++)
			{
				species[i] = speciesBuffer[i];
				massKg[i] = massBuffer[i];
			}
			return count;
		}

		/// <summary>
		/// Adds <paramref name="massKg"/> of vanilla element <paramref name="speciesIdx"/>
		/// (an ElementTable index, not a SimHashes id -- see abi/sim_abi_ext.h) into
		/// <paramref name="cell"/>'s gas mixture, additive to whatever the cell's normal
		/// PhaseEntry mass already holds. The first call any world ever makes activates
		/// mixing for that world.
		///
		/// <paramref name="temperatureK"/> is the temperature the incoming mass arrives at --
		/// blended natively into the cell's one shared PhaseEntry.temperature field,
		/// mass-weighted against whatever the cell already holds. It is required because a cell
		/// that has never held any vanilla mass (a sealed vacuum tank, for example) sits at
		/// vanilla's 0 K for massless cells, and gas injected at 0 K reads back 0 Pa from
		/// <see cref="TryGetPressure"/> (P = nRT/V) however much of it there is. Pass the temperature of whatever real gas the injected mass is
		/// meant to represent -- typically <c>Grid.Temperature</c> at the source cell it came
		/// from.
		///
		/// Unlike <see cref="TryGetDominant"/>, this only ever sends a message and reads
		/// nothing back, so it reuses Klei's own <c>Sim.SIM_HandleMessage</c> P/Invoke --
		/// same pattern as <see cref="ThermalMassBonus.Set"/>.
		/// </summary>
		public static unsafe void Inject(int cell, int speciesIdx, float massKg, float temperatureK)
		{
			byte[] payload = new byte[16];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(speciesIdx), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(massKg), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(temperatureK), 0, payload, 12, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.InjectGasSpecies, payload.Length, msg);
			}
		}

		/// <summary>
		/// Takes <paramref name="massKg"/> out of <paramref name="cell"/>'s vanilla PhaseEntry
		/// mass (whatever single element already occupies that cell), clamped natively so the
		/// cell's mass can never go negative -- a removal request for more than the cell holds
		/// just empties it. The symmetric counterpart to <see cref="Inject"/>; on its own this
		/// only ever takes mass away, it does not add it anywhere.
		/// </summary>
		public static unsafe void RemoveVanillaMass(int cell, float massKg)
		{
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(massKg), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.RemoveVanillaMass, payload.Length, msg);
			}
		}

		/// <summary>
		/// A conserving transfer: injects <paramref name="massKg"/> of vanilla element
		/// <paramref name="speciesIdx"/> into <paramref name="cell"/>'s gas mixture, then takes
		/// the same amount back out of that cell's vanilla PhaseEntry. Nothing appears,
		/// nothing vanishes -- it only moves from the single-element vanilla layer into the
		/// multi-species one. Prefer this over calling <see cref="Inject"/> alone whenever the
		/// mass being injected is meant to represent real atmosphere already sitting in the
		/// cell (as opposed to e.g. a debug/test seed that has no vanilla mass to take from).
		/// </summary>
		public static void ConvertFromVanilla(int cell, int speciesIdx, float massKg)
		{
			// Same-cell transfer -- the cell's own current vanilla temperature is exactly
			// right to carry over (see Inject's temperatureK doc comment).
			Inject(cell, speciesIdx, massKg, Grid.Temperature[cell]);
			RemoveVanillaMass(cell, massKg);
		}

		/// <summary>
		/// The reverse of <see cref="ConvertFromVanilla"/>: removes up to <paramref
		/// name="massKg"/> of <paramref name="speciesIdx"/> from <paramref name="cell"/>'s gas
		/// mixture and adds however much actually came out to that cell's own vanilla
		/// PhaseEntry -- <paramref name="temperatureK"/> is the temperature to merge in (the
		/// mixture layer has no per-species temperature of its own to offer; callers typically
		/// pass the cell's current <c>Grid.Temperature[cell]</c>).
		///
		/// Unlike <see cref="ConvertFromVanilla"/> (two separate messages, safe because the
		/// add side -- Inject -- has no cap to violate), this is ONE atomic native message,
		/// not a RemoveVanillaMass-style pair: two separate messages here would let a caller
		/// asking for more than the mixture actually holds have the removal clamp down while
		/// a naive addition still added the full requested amount, creating mass. The native handler removes first
		/// and only ever adds however much actually came out, so that gap can't exist here by
		/// construction. Refused destinations (e.g. a solid occupying the cell) refund the
		/// removed amount back into the mixture rather than losing it, so this is conserving
		/// on every path, not just the common one.
		/// </summary>
		public static unsafe void ConvertToVanilla(int cell, int speciesIdx, float massKg, float temperatureK)
		{
			byte[] payload = new byte[16];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(speciesIdx), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(massKg), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(temperatureK), 0, payload, 12, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.ConvertToVanillaMass, payload.Length, msg);
			}
		}

		/// <summary>
		/// Promotes whichever room currently contains <paramref name="cell"/> from
		/// vanilla-owned to mixture-owned (<c>kPromoteRoom</c>). If the mixture layer isn't
		/// active for this world yet, the native handler activates it first, so this is safe to
		/// call even before any gas has been injected.
		///
		/// Once promoted, the ordinary gas kernels (conduction, gas pressure, gas displacement and
		/// the emitters) leave the room's cells alone; the caller converts the room's ordinary gas
		/// into the mixture. The replacement SimDLL's GAS-MIXTURES document describes the liquid
		/// gate and persistence. Promote-only; there is no demote call.
		///
		/// Message effects are deferred one tick (same latency every <c>ext::</c> message here
		/// has), so a promotion sent this frame is not observable until the next.
		/// </summary>
		public static unsafe void PromoteRoom(int cell)
		{
			byte[] payload = new byte[4];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.PromoteRoom, payload.Length, msg);
			}
		}

		/// <summary>
		/// What a gas add does when it has nowhere to go. The native values are
		/// <c>OniBlockedGasAddPolicy</c> in <c>abi/sim_ext_api.h</c>.
		/// </summary>
		public enum BlockedGasAddPolicy
		{
			/// <summary>Klei's own <c>AddIntoBlockedCell</c>: the smaller of the two masses (the
			/// incoming gas, or the pocket in its way) is deleted. The default.</summary>
			Vanilla = 0,

			/// <summary>The blocked cell's room is promoted, every gas cell of that room is moved
			/// out of vanilla's grid into the room's mixture, and the incoming gas is mixed into
			/// the blocked cell at its own temperature. Nothing is deleted.</summary>
			PromoteAndMix = 1,
		}

		/// <summary>
		/// Sets what a blocked <c>ModifyCell</c> gas add does
		/// (<c>ext::kSetBlockedGasAddPolicy</c>). A gas add is blocked when the
		/// target cell holds a DIFFERENT gas that cannot be displaced and none of the left, right
		/// or up neighbours is vacuum or already that gas. Vanilla then deletes the smaller mass,
		/// so a 200 g bubble popping onto a 20 g pocket of another gas loses 40 g.
		///
		/// Covers the <c>ModifyCell</c> path only (<c>SimMessages.ModifyMass</c>, so bubble pops
		/// and a dupe's exhaled breath), and gas only: a blocked liquid add still deletes. With
		/// <see cref="BlockedGasAddPolicy.PromoteAndMix"/> the promotion is real and
		/// permanent -- it is <see cref="PromoteRoom"/>, plus the room's gas moved into the mixture
		/// natively in the same step, so a room promoted this way does not read as empty the way
		/// a bare <see cref="PromoteRoom"/> would. A room is everything connected by open cells,
		/// so one blocked breath can promote a large space.
		///
		/// Kept on the sim, not the world, so it survives a load; not saved, so send it once per
		/// session. Queued like every ext message, but it drains before <c>ModifyCell</c>, so an
		/// add sent in the same frame already sees the new policy.
		/// </summary>
		public static unsafe void SetBlockedGasAddPolicy(BlockedGasAddPolicy policy)
		{
			byte[] payload = BitConverter.GetBytes((int)policy);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetBlockedGasAddPolicy, payload.Length, msg);
			}
		}

		// Not part of Klei's ABI, but not gameplay-hot either: `SIM_DebugRoomOwned` does a full
		// `WaitIdle()` sim-thread rendezvous every call (sim/simdll.cpp), so this is fine for a
		// dupe's 200ms breathing tick but would be the wrong choice for
		// anything polled per-frame.
		[DllImport("SimDLL")]
		private static extern int SIM_DebugRoomOwned(int cellIdx);

		/// <summary>
		/// Whether <paramref name="cell"/>'s room has been promoted (see <see cref="PromoteRoom"/>). <c>false</c> for a cell with no room at all (volume-fractions
		/// never activated, or the cell isn't open) as well as for an ordinary vanilla-owned room --
		/// callers that only care "should I read the facade instead of vanilla for this cell"
		/// want exactly that collapse, not a three-way result.
		/// </summary>
		public static bool IsRoomOwned(int cell)
		{
			return DebugRoomOwnedRaw(cell) == 1;
		}

		/// <summary>
		/// The raw, three-way answer <see cref="IsRoomOwned"/> collapses to a bool: -1 (cell has
		/// no room at all -- volume-fractions never activated for this world, OR this cell was not
		/// open when the room graph was last built -- these two cases are NOT currently
		/// distinguishable from outside the DLL), 0 (a real, recognized room, still vanilla-owned),
		/// or 1 (promoted). Added specifically to diagnose a live report: a room dug out
		/// AFTER volume-fractions already activated elsewhere in the same world is invisible to the
		/// room graph (`BuildRoomGraph` runs once, at first activation, and is never rebuilt), so
		/// `PromoteRoom` on a cell in it silently no-ops (`SetRoomOwned`'s own `room_id &lt; 0`
		/// guard) -- from outside the DLL this looks identical to "nothing has gone wrong yet,"
		/// which is exactly why this diagnostic exists: -1 here, even right after calling
		/// <see cref="PromoteRoom"/>, is the smoking gun for that specific bug.
		/// </summary>
		public static int DebugRoomOwnedRaw(int cell)
		{
			// -1 already means "this cell has no room at all", which is exactly the right answer
			// for a DLL that has no room graph because it has no mixture layer.
			if (unavailable)
			{
				return -1;
			}
			try
			{
				return SIM_DebugRoomOwned(cell);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_DebugRoomOwned");
				return -1;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return -1;
			}
		}

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_GasMassBatchQuery(int* cellIndices, int count, byte* outOwned, ushort* outDominant, float* outMass);

		[DllImport("SimDLL")]
		private static extern float SIM_GasPressure(int cellIdx);

		/// <summary>
		/// <paramref name="cell"/>'s total gas-mixture pressure in Pa (PV=nRT, Dalton's law summed
		/// across every species present -- <c>sim/gas_mixture.h</c>'s own <c>CellPressure</c>,
		/// published here for the first time instead of staying trapped inside the mixing kernel).
		/// Returns <c>false</c> if the cell holds no gas-mixture state at all (mirrors
		/// <see cref="TryGetDominant"/>'s "fall back to vanilla" collapse) -- 0 Pa is a real,
		/// legitimate reading for an occupied-but-cold or massless mixture, so it can't double as
		/// the "no data" sentinel the way it does for a mass total.
		///
		/// Every cell shares the same fixed placeholder volume (<c>gas_mixture.h</c>'s
		/// <c>kCellVolumeM3</c> -- there is no real per-cell volume field yet), which is exactly
		/// what makes this the right read for a compressor/tank mechanic: two cells holding
		/// different moles at comparable temperatures are directly pressure-comparable, so packing
		/// more mass into one fixed-volume cell than a normal room cell would ever hold reads as a
		/// real, visible pressure spike -- concentration standing in for compression.
		/// </summary>
		public static bool TryGetPressure(int cell, out float pressurePa)
		{
			float result;
			if (unavailable)
			{
				pressurePa = 0f;
				return false;
			}
			try
			{
				result = SIM_GasPressure(cell);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_GasPressure");
				pressurePa = 0f;
				return false;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				pressurePa = 0f;
				return false;
			}
			if (result < 0f)
			{
				pressurePa = 0f;
				return false;
			}
			pressurePa = result;
			return true;
		}

		/// <summary>
		/// Real internal volume of ONE gas conduit tile, in m^3: Stationeers' pipe volume, 10 L.
		///
		/// The volume decides whether pressure limits mean anything: 1 kg of Oxygen at 300 K reads
		/// 78 kPa in 1000 L but 7.79 MPa in 10 L, and Stationeers' own gas-pipe burst limit is
		/// 60.8 MPa (see <see cref="PipeNetworkFacade.MaxGasPipePressurePa"/>).
		///
		/// Public because a mod's own container has to equalize against a pipe at the SAME
		/// volume this facade prices it at, and a hand-copied duplicate drifts.
		/// </summary>
		public const float GasConduitVolumeM3 = 0.01f;

		/// <summary>
		/// Real internal volume of ONE liquid conduit tile, in m^3 -- Stationeers' liquid pipe
		/// volume, 20 L. Public for the same reason as <see cref="GasConduitVolumeM3"/>.
		/// </summary>
		public const float LiquidConduitVolumeM3 = 0.02f;

		[DllImport("SimDLL")]
		private static extern unsafe float SIM_ComputeGasPressure(ushort* species, float* massKg, int count, float temperatureK, float volumeM3);

		/// <summary>
		/// Real pressure (Pa) for an arbitrary container that has no cell of its own -- a tank,
		/// a pipe, or any other custom storage a mod builds. Same native formula
		/// <see cref="TryGetPressure"/>/<c>CellPressure</c> use, generalized off any particular
		/// cell (<c>gas::PressureFromMoles</c>/<c>MolesFromSpeciesList</c>,
		/// <c>sim/gas_mixture.h</c>'s <c>SIM_ComputeGasPressure</c> export). This is the ONE
		/// place in the framework that P/Invokes it -- exposed so a mod's own container never has
		/// to hand-copy the P=nRT/V formula and drift from the sim's.
		///
		/// <paramref name="speciesElementIdx"/>/<paramref name="massKg"/> are parallel arrays
		/// (ElementLoader.elements indices, the same convention <see cref="GasComponent.ElementIdx"/>
		/// already uses elsewhere in this facade). Returns <c>false</c> for null/mismatched/empty
		/// arrays, a non-positive volume, or a mass list that sums to zero moles -- 0 Pa is a
		/// real, legitimate answer this can't also mean "no data."
		/// </summary>
		public static unsafe bool TryComputePressure(int[] speciesElementIdx, float[] massKg, float temperatureK, float volumeM3, out float pressurePa)
		{
			pressurePa = 0f;
			if (speciesElementIdx == null || massKg == null || speciesElementIdx.Length == 0 ||
				speciesElementIdx.Length != massKg.Length)
			{
				return false;
			}
			int count = speciesElementIdx.Length;
			ushort[] species = new ushort[count];
			for (int i = 0; i < count; i++)
			{
				species[i] = (ushort)speciesElementIdx[i];
			}
			if (unavailable)
			{
				return false;
			}
			fixed (ushort* speciesPtr = species)
			fixed (float* massPtr = massKg)
			{
				float result;
				try
				{
					result = SIM_ComputeGasPressure(speciesPtr, massPtr, count, temperatureK, volumeM3);
				}
				catch (EntryPointNotFoundException)
				{
					// A Try* with a defined false, unlike the pure functions below -- it already
					// has to answer false for a zero-mole mixture, so it has somewhere to put
					// "no answer" without inventing a pressure.
					MarkUnavailable("SIM_ComputeGasPressure");
					return false;
				}
				catch (DllNotFoundException e)
				{
					ReportMissingLibrary(e);
					return false;
				}
				if (result < 0f)
				{
					return false;
				}
				pressurePa = result;
				return true;
			}
		}

		/// <summary>
		/// Real pressure (Pa) of the gas sitting in a connected pipe segment at
		/// <paramref name="cell"/> -- a thin wrapper over <see cref="TryComputePressure"/> that
		/// reads the pipe's own real mass/element/temperature (vanilla's <c>ConduitFlow</c> --
		/// state that correctly stays managed, matching real vanilla's own design; see
		/// <c>ConduitTemperatureManager</c>'s own header comment: the sim never sees pipe
		/// contents move) and hands it to that one shared native formula.
		///
		/// Vanilla itself has no concept of pipe pressure at all -- <c>ConduitFlow.ConduitContents</c>
		/// carries only element/mass/temperature/disease.
		///
		/// Gas conduits only (<c>Game.Instance.gasConduitFlow</c>), matching the mixture layer's
		/// gas-only scope. Returns
		/// <c>false</c> for an unconnected or empty segment, same "0 Pa is not a usable no-data
		/// sentinel" collapse every other <c>TryGet*</c> here uses.
		/// </summary>
		public static bool TryGetConduitPressure(int cell, out float pressurePa)
		{
			pressurePa = 0f;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}
			ConduitFlow gasFlow = Game.Instance.gasConduitFlow;
			if (gasFlow == null || !gasFlow.HasConduit(cell))
			{
				return false;
			}
			ConduitFlow.ConduitContents contents = gasFlow.GetContents(cell);
			if (contents.mass <= 0f)
			{
				return false;
			}
			Element element = ElementLoader.FindElementByHash(contents.element);
			if (element == null)
			{
				return false;
			}
			int elementIdx = ElementLoader.elements.IndexOf(element);
			return TryComputePressure(new[] { elementIdx }, new[] { contents.mass }, contents.temperature, GasConduitVolumeM3, out pressurePa);
		}

		[DllImport("SimDLL")]
		private static extern float SIM_CalculateCombinedTemperature(float massA, float tempA, float massB, float tempB);

		/// <summary>
		/// The mass-weighted temperature a container ends up at after receiving
		/// <paramref name="massB"/> kg arriving at <paramref name="tempB"/> K, on top of
		/// <paramref name="massA"/> kg already sitting at <paramref name="tempA"/> K -- clamped
		/// to the two input temperatures, never overshooting either one. This is Klei's own real
		/// <c>CalculateCombinedTemperature</c> rule (<c>sim/emitters.h</c>), exposed here so a mod's
		/// own container blends temperature the same way vanilla's own emitters/consumers do,
		/// without hand-copying the formula.
		/// </summary>
		public static float CombineTemperature(float massA, float tempA, float massB, float tempB)
		{
			if (unavailable)
			{
				throw NoExport("CombineTemperature");
			}
			try
			{
				return SIM_CalculateCombinedTemperature(massA, tempA, massB, tempB);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_CalculateCombinedTemperature");
				throw NoExport("CombineTemperature");
			}
		}

		[DllImport("SimDLL")]
		private static extern float SIM_AdiabaticFillTemperature(float gammaMix, float massA, float tempA, float massB, float tempB);

		/// <summary>
		/// Default mixture-wide heat-capacity ratio (Cp/Cv) used by <see cref="AdiabaticFillTemperature"/>
		/// for a caller with no single species in hand: Stationeers' triatomic bucket, into which
		/// nearly every simple gas in its per-species table (O2, N2, CO2, CH4, H2O, N2O, H2) falls.
		///
		/// Prefer the per-species <see cref="MaterialPropertyRegistry.TryGetHeatCapacityRatio"/>
		/// wherever the species is known. This constant exists because a MIXTURE genuinely has no single species to look up and still needs one gamma -- and
		/// because every substance seeded in that registry today lands in this same triatomic
		/// bucket, so the two agree at present anyway (they will not once a monatomic or
		/// polyatomic substance is registered).
		/// </summary>
		public const float DefaultGasHeatCapacityRatio = 1.333f;

		/// <summary>
		/// The real compression-heating temperature a container ends up at after receiving
		/// <paramref name="massB"/> kg arriving at <paramref name="tempB"/> K, on top of
		/// <paramref name="massA"/> kg already sitting at <paramref name="tempA"/> K -- UNLIKE
		/// <see cref="CombineTemperature"/>, this can push the result above BOTH input
		/// temperatures, because the incoming gas is being forced into an existing store rather
		/// than merely mixed at the same pressure. Real open-system tank-filling energy balance
		/// (<c>SIM_AdiabaticFillTemperature</c>, <c>sim/gas_mixture.h</c> -- see its own doc
		/// comment for the full derivation). Reduces to <see cref="CombineTemperature"/>
		/// exactly when <paramref name="gammaMix"/> is 1.0 (a free/open-valve mix, no compression
		/// work) -- pass <see cref="DefaultGasHeatCapacityRatio"/> for an active compressor.
		/// </summary>
		public static float AdiabaticFillTemperature(float gammaMix, float massA, float tempA, float massB, float tempB)
		{
			if (unavailable)
			{
				throw NoExport("AdiabaticFillTemperature");
			}
			try
			{
				return SIM_AdiabaticFillTemperature(gammaMix, massA, tempA, massB, tempB);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_AdiabaticFillTemperature");
				throw NoExport("AdiabaticFillTemperature");
			}
		}

		[DllImport("SimDLL")]
		private static extern float SIM_EqualizeSingleSpeciesMass(float molarMass, float massA, float tempA, float volumeA, float massB, float tempB, float volumeB, float rate);

		/// <summary>
		/// How much mass of ONE shared species should move from container B into container A
		/// this step, to equalize their partial pressures -- positive means A gains (from B),
		/// negative means A loses (to B). Same physics as the native per-cell mixing kernel's
		/// own equalization step (<c>gas::MixPair</c>, <c>sim/gas_mixture.h</c>), generalized off
		/// two cells sharing one fixed volume to two independent containers with their own
		/// volumes/temperatures -- e.g. a tank plumbed to a pipe with no valve between them,
		/// which should behave as ONE connected system sharing pressure, not an active pump
		/// moving mass at a fixed rate. Directionality belongs in explicit valve buildings, as in
		/// Stationeers, never in a storage tank itself.
		///
		/// <paramref name="rate"/> damps the step the same way the native mixing kernel's own
		/// rate parameter does -- <c>MixPair</c> carries a fixed extra *0.5 on top of it, so even
		/// <c>rate=1.0</c> only closes HALF the pressure gap in one call, converging
		/// geometrically over repeated calls rather than snapping to equilibrium instantly (a
		/// caller polling once a second converges over a few real seconds --
		/// verified in <c>vftest.cpp</c>). Pure function -- no simulation state involved at all,
		/// not even the element table (molarMass is a direct parameter), so this needs no native
		/// rendezvous either.
		/// </summary>
		public static float EqualizeSingleSpeciesMass(float molarMass, float massA, float tempA, float volumeA, float massB, float tempB, float volumeB, float rate)
		{
			if (unavailable)
			{
				throw NoExport("EqualizeSingleSpeciesMass");
			}
			try
			{
				return SIM_EqualizeSingleSpeciesMass(molarMass, massA, tempA, volumeA, massB, tempB, volumeB, rate);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_EqualizeSingleSpeciesMass");
				throw NoExport("EqualizeSingleSpeciesMass");
			}
		}

		/// <summary>
		/// The batch counterpart to <see cref="IsRoomOwned"/>/<see cref="TryGetDominant"/>, for
		/// overlay colouring: <c>PropertyTextures.UpdateSolidLiquidGasMass</c> reads
		/// <c>Grid.Element</c>/<c>Grid.Mass</c> for every cell in the active world on an
		/// <c>ISim200ms</c> tick. Doing that one cell at a time through <see cref="IsRoomOwned"/>
		/// would mean one <c>WaitIdle()</c> sim-thread rendezvous PER CELL, tens of thousands of
		/// them every 200ms on a real map -- exactly the per-frame/per-cell misuse
		/// <see cref="IsRoomOwned"/>'s own doc comment already warns against. This does ONE
		/// rendezvous for the whole batch instead.
		///
		/// <paramref name="cells"/>, <paramref name="outOwned"/>, <paramref name="outDominant"/>,
		/// and <paramref name="outMass"/> must all be the same length -- caller-allocated and
		/// reused across calls (e.g. once per texture-chunk-sized batch, not once per call) so a
		/// 200ms-cadence caller doesn't allocate on every tick. No assumption is made about
		/// batch size or cell ordering (not a rect, not a row) -- a caller can match whatever
		/// chunking <c>UpdateTextureThreaded</c> already uses.
		///
		/// For index i: <c>outOwned[i]</c> is true iff that cell's ROOM has been promoted --
		/// not merely "this cell currently holds mixture mass." A promoted room legitimately
		/// sitting at zero real gas still reports owned=true, because that zero is the real
		/// answer a caller must trust over vanilla's stale <c>Grid.Mass</c>. Only where
		/// <c>outOwned[i]</c> is true are <c>outDominant[i]</c>/<c>outMass[i]</c> meaningful;
		/// where it's false the caller's own answer is "fall back to vanilla for this cell,"
		/// same collapse as every other facade method here.
		/// </summary>
		public static unsafe int TryGetBatch(int[] cells, bool[] outOwned, ushort[] outDominant,
			float[] outMass)
		{
			return TryGetBatch(cells, outOwned, outDominant, outMass, cells.Length);
		}

		/// <summary>
		/// As above, but for a caller whose buffers are REUSED and therefore allowed to be larger
		/// than the batch. Only the first <paramref name="count"/> entries are read or written.
		///
		/// This overload exists because the buffers-must-match-exactly rule quietly forced every
		/// caller to allocate a fresh set per call, which is the opposite of what this method's own
		/// doc comment asks for ("caller-allocated and reused across calls"). The overlay texture
		/// hook that finally consumed it was allocating about 110 KB per frame per property as a
		/// result -- see Mod1's OverlayTexturePatch.ScratchBuffers for that measurement.
		/// </summary>
		public static unsafe int TryGetBatch(int[] cells, bool[] outOwned, ushort[] outDominant,
			float[] outMass, int count)
		{
			if (count < 0 || count > cells.Length || count > outOwned.Length
				|| count > outDominant.Length || count > outMass.Length)
			{
				throw new ArgumentException(
					"count must be non-negative and no larger than any of cells/outOwned/"
					+ "outDominant/outMass");
			}
			if (count == 0)
			{
				return 0;
			}

			// Pooled per thread for the same reason the caller pools its own four: this runs once
			// per overlay property per frame, and a fresh byte[] here would put back a fifth of
			// the garbage the caller just stopped producing.
			byte[] ownedBytes = ownedScratch;
			if (ownedBytes == null || ownedBytes.Length < count)
			{
				ownedBytes = new byte[count];
				ownedScratch = ownedBytes;
			}
			else
			{
				// MUST be cleared. `new byte[count]` was zero-filled by the runtime on every call,
				// and the native query is only documented to WRITE the cells it owns -- so a
				// pooled buffer that kept a previous, larger batch's bytes would report cells as
				// owned that this batch never asked about. Pooling the array without this line
				// swaps a garbage problem for a correctness one.
				Array.Clear(ownedBytes, 0, count);
			}
			if (unavailable)
			{
				// Nothing owned, nothing written -- the same answer a world that never activated
				// the mixture layer gets, which every caller already handles.
				for (int i = 0; i < count; i++)
				{
					outOwned[i] = false;
				}
				return 0;
			}
			int written;
			fixed (int* cellsPtr = cells)
			fixed (byte* ownedPtr = ownedBytes)
			fixed (ushort* dominantPtr = outDominant)
			fixed (float* massPtr = outMass)
			{
				try
				{
					written = SIM_GasMassBatchQuery(cellsPtr, count, ownedPtr, dominantPtr, massPtr);
				}
				catch (EntryPointNotFoundException)
				{
					MarkUnavailable("SIM_GasMassBatchQuery");
					written = 0;
				}
				catch (DllNotFoundException e)
				{
					ReportMissingLibrary(e);
					written = 0;
				}
			}
			for (int i = 0; i < count; i++)
			{
				outOwned[i] = ownedBytes[i] == 1;
			}
			return written;
		}

		[ThreadStatic]
		private static byte[] ownedScratch;

		/// <summary>
		/// The total mass, across every species in <paramref name="cell"/>'s gas mixture, that
		/// Klei's own <c>GameTags.Breathable</c> tag would call breathable -- what
		/// <c>GasBreatherFromWorldProvider.GetBreathableCellMass</c> computes for a single vanilla
		/// element, generalized to a multi-species mixture instead of collapsing to whichever
		/// species <see cref="TryGetDominant"/> would report.
		///
		/// This only answers "how much is breathable here". Vanilla breathing reads
		/// <c>Grid.Mass[cell]</c>/<c>Grid.Element[cell]</c>; a mod that wants breathing to follow
		/// the mixture patches <c>GetBreathableCellMass</c> to substitute this answer for a
		/// promoted cell (<see cref="IsRoomOwned"/>) and fall through to vanilla otherwise.
		/// </summary>
		/// <param name="cell">A game cell index, same space as every other per-cell call here.</param>
		/// <param name="isBreathableElement">Callback deciding whether one ElementTable index
		/// counts as breathable. Deliberately not hardcoded to <c>GameTags.Breathable</c> in this
		/// framework layer -- the framework exposes the real mixture data, and the caller
		/// (typically with Klei's own tag) supplies the vanilla-specific semantics, same separation as
		/// every other facade method in this class.</param>
		/// <returns>The summed mass of every species this cell's mixture holds that
		/// <paramref name="isBreathableElement"/> accepts -- zero if the cell holds no
		/// gas-mixture state, or none of its species qualify.</returns>
		public static float GetBreathableMass(int cell, Func<ushort, bool> isBreathableElement)
		{
			GasComponent[] all = TryGetComposition(cell);
			float total = 0f;
			for (int i = 0; i < all.Length; i++)
			{
				if (isBreathableElement(all[i].ElementIdx))
				{
					total += all[i].MassKg;
				}
			}
			return total;
		}

		// Bounds the flood-fill/redistribute below -- protects against a pathological huge
		// network (or a real base-wide pipe grid) from turning one exchange into an unbounded
		// per-tick cost. Far more than any single test scenario needs.
		//
		// `ConduitNetworks` answers network membership from the game's own partition, so these two
		// flood fills are not the usual way a network is resolved -- `PipeNetworkFacade` uses them only as the fallback for the frames between a
		// pipe being placed and `UtilityNetworkManager.Update` rebuilding, plus the equalisers
		// below, plus the rig check that the cache equals the walk. So this cap bounds a rare path
		// rather than every network read; the cached partition has no cap at all, deliberately: a
		// capped walk reports a run of more than 500 tiles as the mass, volume and therefore the
		// PRESSURE of an arbitrary 500-tile subset of itself.
		private const int MaxPipeNetworkTiles = 500;

		/// <summary>
		/// Every gas-conduit cell reachable from <paramref name="startCell"/> by walking
		/// 4-connected neighbors that also have a gas conduit -- a real connected pipe run,
		/// the same shape <see cref="IsRoomOwned"/>'s native room flood-fill uses for open
		/// cells (<c>gas_rooms.h</c>), just walked here in managed code since conduit topology
		/// is <c>Grid.Objects</c>/<c>ObjectLayer.GasConduit</c> data, not anything the native
		/// per-cell world tracks (a pipe is not a cell -- see
		/// <c>GasMixtureFacade.TryGetConduitPressure</c>'s own doc comment). Capped at
		/// <see cref="MaxPipeNetworkTiles"/>; a network larger than that returns exactly that
		/// many cells (an incomplete but still-safe partial fill) rather than growing without
		/// bound.
		/// </summary>
		/// <summary>Neighbour deltas in the order the flood fills walk them: right, left, up, down.</summary>
		private static readonly int[] NeighbourDX = { 1, -1, 0, 0 };

		private static readonly int[] NeighbourDY = { 0, 0, 1, -1 };

		/// <summary>
		/// The connection bit pointing FROM a tile TOWARDS each neighbour, in the same order as
		/// <see cref="NeighbourDX"/>.
		/// </summary>
		private static readonly UtilityConnections[] NeighbourBit =
		{
			UtilityConnections.Right,
			UtilityConnections.Left,
			UtilityConnections.Up,
			UtilityConnections.Down,
		};

		/// <summary>
		/// The bit pointing BACK from the neighbour, same order. A join needs both.
		/// </summary>
		private static readonly UtilityConnections[] ReciprocalBit =
		{
			UtilityConnections.Left,
			UtilityConnections.Right,
			UtilityConnections.Down,
			UtilityConnections.Up,
		};

		/// <summary>
		/// Whether two ADJACENT conduit tiles are actually part of the same network.
		///
		/// Adjacency is not connection, and walking to any adjacent conduit tile is not what ONI
		/// does or what a player sees. Vanilla's own Gas Filter settles it -- it is a 3x1 building whose input (-1,0), output (+1,0) and secondary
		/// output (0,0) sit in a row of three MUTUALLY ADJACENT cells and are three separate
		/// networks, which is the entire mechanism of a filter. Treating adjacency as connection
		/// would merge every pipe that happens to run next to another one.
		///
		/// It matters beyond reporting. <c>PipeNetworkFacade.TryGetNetworkState</c> is built on
		/// these fills, so a machine reading several ports whose pipes run near each other would
		/// see ONE network on all of them, with identical pressures and no differential.
		///
		/// Ground truth is the game's own connection grid,
		/// <c>UtilityNetworkManager.GetConnections(cell, is_physical_building: false)</c>, which
		/// is what <c>SetConnections</c> writes during a stamp and what the player draws by hand.
		/// BOTH tiles must point at each other: ONI's own rebuild treats a one-sided bit as no
		/// connection, and so must this. Falls back to plain adjacency only when the network
		/// manager does not exist yet, which is a state no gameplay code is in.
		/// </summary>
		private static bool ConduitsJoined(int cell, int neighbor, int direction, bool gas)
		{
			IUtilityNetworkMgr manager = Game.Instance != null
				? (gas
					? (IUtilityNetworkMgr)Game.Instance.gasConduitSystem
					: (IUtilityNetworkMgr)Game.Instance.liquidConduitSystem)
				: null;
			if (manager == null)
			{
				return true;
			}
			UtilityConnections from = manager.GetConnections(cell, false);
			UtilityConnections to = manager.GetConnections(neighbor, false);
			return (from & NeighbourBit[direction]) != 0
				&& (to & ReciprocalBit[direction]) != 0;
		}

		public static List<int> FloodFillPipeNetwork(int startCell)
		{
			var result = new List<int>();
			if (!Grid.IsValidCell(startCell) || Grid.Objects[startCell, (int)ObjectLayer.GasConduit] == null)
			{
				return result;
			}

			var visited = new HashSet<int>();
			var queue = new Queue<int>();
			visited.Add(startCell);
			queue.Enqueue(startCell);

			while (queue.Count > 0 && result.Count < MaxPipeNetworkTiles)
			{
				int cell = queue.Dequeue();
				result.Add(cell);

				for (int i = 0; i < 4; i++)
				{
					int neighbor = Grid.OffsetCell(cell, NeighbourDX[i], NeighbourDY[i]);
					if (!Grid.IsValidCell(neighbor) || visited.Contains(neighbor))
					{
						continue;
					}
					if (Grid.Objects[neighbor, (int)ObjectLayer.GasConduit] == null)
					{
						continue;
					}
					if (!ConduitsJoined(cell, neighbor, i, gas: true))
					{
						continue;
					}
					visited.Add(neighbor);
					queue.Enqueue(neighbor);
				}
			}

			return result;
		}

		/// <summary>
		/// STATIONEERS-STYLE NETWORK PRESSURE: vanilla gas conduits are per-TILE objects
		/// (<c>ConduitFlow.RebuildConnections</c> calls <c>soaInfo.AddConduit</c> once per pipe
		/// tile, not once per connected network), so mass only ever moves between immediately
		/// adjacent tiles each tick -- a real, visible "flow travel time" along a pipe run. That
		/// is a vanilla ONI modeling choice, not a law of physics; Stationeers treats an
		/// unbroken pipe run (no valve) as ONE connected network sharing pressure, not a chain of
		/// independent buckets waiting to slowly equalize.
		///
		/// This forces that: floods out from <paramref name="startCell"/>
		/// (<see cref="FloodFillPipeNetwork"/>), sums the real mass every tile in the network
		/// currently holds (in case vanilla's own per-tile flow already left it uneven), and
		/// writes the total back out EVENLY across every tile via <c>ConduitFlow.SetContents</c>
		/// -- so every tile in the run reads the identical, correct, whole-network state
		/// immediately, not "whichever tile mass happens to have reached so far." Call this
		/// after any exchange that touches the network (a pump dispensing, a tank
		/// pulling/pushing) so every other read site (this mod's own tooltip/overlay/debug grid,
		/// and vanilla's own hover text) sees the real, current, network-wide answer without
		/// needing pooled-read logic of their own -- a plain single-tile
		/// <c>ConduitFlow.GetContents</c> already returns the right answer once this has run.
		///
		/// Mixed-species networks (two different elements meeting mid-run with no valve
		/// isolating them) are a known, named simplification: this keeps whichever element it
		/// finds first and folds every other species' mass into that one rather than modeling a
		/// real multi-species pipe mixture -- real vanilla conduits are already
		/// single-element-per-tile (<c>ConduitFlow.AddElement</c> itself refuses a second
		/// element), so a mixed network already can't happen through this mod's own dispense
		/// path; only a save/scenario built by hand with pre-existing mixed contents could hit
		/// this simplification, and even then it disappears (one element `remains) after this
		/// runs once. Disease tracking is dropped for redistributed tiles, a known limitation.
		/// </summary>
		/// <summary>
		/// Every cell <see cref="EqualizePipeNetwork"/> has ever redistributed -- the set a
		/// Harmony patch on vanilla's own <c>ConduitFlow.UpdateConduit</c> checks to skip
		/// vanilla's per-tile mover on pipes this mod manages (see that patch's own doc comment
		/// for why: vanilla's own flow, left running, actively fights this redistribution --
		/// found live, as pipe pressure stuck at exactly half the tank's instead of
		/// converging). Public so a Harmony patch in the flagship mod (a different assembly) can
		/// read it without a second copy of the flood-fill.
		///
		/// Expiry: <see cref="PruneManagedConduitCells"/> removes a cell once its pipe is
		/// actually gone, called automatically at the top of <see cref="EqualizePipeNetwork"/>
		/// so the set self-cleans on the same cadence the mechanic already polls at.
		/// </summary>
		public static readonly HashSet<int> ManagedConduitCells = new HashSet<int>();

		/// <summary>
		/// What the sim should do, of its own accord, to every run under this framework's
		/// managed-pipe regime -- the runs <see cref="EqualizePipeNetwork"/> and
		/// <see cref="EqualizeLiquidNetwork"/> take over. <see cref="ConduitNetworkPolicy.None"/>
		/// (the default) leaves them to Klei's arithmetic, so the framework itself changes
		/// nothing; a consumer opts in at load. Applied through
		/// <see cref="SimConduitNetworks.SetPolicy"/> keyed on each equalise call's start cell, so
		/// it follows the run through every rebuild and costs nothing when it is unchanged.
		///
		/// <see cref="ConduitNetworkPolicy.Phase"/> is only half an opt-in: the sim decides, and
		/// <see cref="SimConduitNetworks.Install"/> is what carries the decisions out.
		/// <see cref="ConduitNetworkPolicy.Mix"/> exchanges heat between neighbouring conduits only,
		/// so it is safe on a run that crosses both rooms of a heat pump; its doc says why it is not
		/// Stationeers' whole-network mix.
		/// </summary>
		public static ConduitNetworkPolicy ManagedRunPolicy = ConduitNetworkPolicy.None;

		/// <summary>The coupling <see cref="ManagedRunPolicy"/> uses when it includes
		/// <see cref="ConduitNetworkPolicy.Mix"/>: in (0, <see cref="SimConduitNetworks.MaxMixFraction"/>],
		/// and a value outside that is refused by <see cref="SimConduitNetworks.SetPolicy"/>, so the
		/// run gets no policy at all.</summary>
		public static float ManagedRunMixFraction = SimConduitNetworks.MaxMixFraction;

		private static void ApplyManagedRunPolicy(int startCell, PipeContentType contentType)
		{
			if (ManagedRunPolicy == ConduitNetworkPolicy.None)
			{
				return;
			}
			SimConduitNetworks.SetPolicy(startCell, contentType, ManagedRunPolicy, ManagedRunMixFraction);
		}

		/// <summary>
		/// Removes every cell from <see cref="ManagedConduitCells"/> that no longer actually has
		/// a gas conduit -- the pipe was deconstructed since this cell was last flagged managed.
		/// A hygiene pass (bounded memory, an accurate set): vanilla's
		/// <c>ConduitFlow.UpdateConduit</c> is only ever invoked for a conduit that still exists,
		/// so a stale entry is dead weight rather than a wrong skip. No separate timer: called from
		/// <see cref="EqualizePipeNetwork"/> itself, so the set self-cleans every time any managed
		/// network actually gets touched.
		/// </summary>
		public static void PruneManagedConduitCells()
		{
			if (ManagedConduitCells.Count == 0)
			{
				return;
			}
			List<int> stale = null;
			foreach (int cell in ManagedConduitCells)
			{
				if (!Grid.IsValidCell(cell) || Grid.Objects[cell, (int)ObjectLayer.GasConduit] == null)
				{
					(stale ?? (stale = new List<int>())).Add(cell);
				}
			}
			if (stale != null)
			{
				for (int i = 0; i < stale.Count; i++)
				{
					ManagedConduitCells.Remove(stale[i]);
				}
			}
		}

		public static void EqualizePipeNetwork(int startCell)
		{
			PruneManagedConduitCells();
			List<int> cells = FloodFillPipeNetwork(startCell);
			if (cells.Count == 0)
			{
				return;
			}

			for (int i = 0; i < cells.Count; i++)
			{
				ManagedConduitCells.Add(cells[i]);
			}
			ApplyManagedRunPolicy(startCell, PipeContentType.Gas);

			ConduitFlow gasFlow = Game.Instance.gasConduitFlow;
			float totalMass = 0f;
			float weightedTempSum = 0f;
			SimHashes element = SimHashes.Vacuum;

			for (int i = 0; i < cells.Count; i++)
			{
				ConduitFlow.ConduitContents contents = gasFlow.GetContents(cells[i]);
				if (contents.mass <= 0f)
				{
					continue;
				}
				totalMass += contents.mass;
				weightedTempSum += contents.mass * contents.temperature;
				if (element == SimHashes.Vacuum)
				{
					element = contents.element;
				}
			}

			if (totalMass <= 0f)
			{
				return;
			}

			float avgTemp = weightedTempSum / totalMass;
			float perTile = totalMass / cells.Count;

			// See NegligibleTileMassKg. An even share this small is float dust, and dust in a
			// conduit tile reads as "occupied" to every vanilla consumer that asks. Park the
			// whole remainder on one tile -- nothing is destroyed -- and leave the others
			// genuinely empty.
			if (perTile < NegligibleTileMassKg)
			{
				gasFlow.SetContents(cells[0],
					new ConduitFlow.ConduitContents(element, totalMass, avgTemp, byte.MaxValue, 0));
				for (int i = 1; i < cells.Count; i++)
				{
					gasFlow.SetContents(cells[i], ConduitFlow.ConduitContents.Empty);
				}

				return;
			}

			var shared = new ConduitFlow.ConduitContents(element, perTile, avgTemp, byte.MaxValue, 0);
			for (int i = 0; i < cells.Count; i++)
			{
				gasFlow.SetContents(cells[i], shared);
			}
		}

		[DllImport("SimDLL")]
		private static extern float SIM_LiquidVolumeFromMass(float massKg, float densityKgM3);

		/// <summary>
		/// Volume (m^3) that <paramref name="massKg"/> of a liquid with density
		/// <paramref name="densityKgM3"/> (kg/m^3 -- <c>Element.density</c>, already real vanilla
		/// per-element data, no new table needed) occupies. 0 for a non-positive density.
		/// See <c>sim/liquid_mixture.h</c>'s <c>VolumeFromMass</c> for the full reasoning: liquid
		/// is tracked by volume, not by the ideal-gas pressure <see cref="TryComputePressure"/>
		/// uses, as in Stationeers' own liquid model.
		/// </summary>
		public static float LiquidVolumeFromMass(float massKg, float densityKgM3)
		{
			if (unavailable)
			{
				throw NoExport("LiquidVolumeFromMass");
			}
			try
			{
				return SIM_LiquidVolumeFromMass(massKg, densityKgM3);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_LiquidVolumeFromMass");
				throw NoExport("LiquidVolumeFromMass");
			}
		}

		/// <summary>
		/// Liquid counterpart to <see cref="TryGetConduitPressure"/>: fill fraction (0..1, can
		/// exceed 1 for an over-capacity segment -- not clamped, same "report the real number"
		/// policy as the pressure reading) of the liquid sitting in a connected pipe segment at
		/// <paramref name="cell"/>, instead of a pressure -- liquids are volume-tracked, not
		/// ideal-gas, per this class's own <c>EqualizeLiquidVolumeMass</c> doc comment. Density
		/// and conduit capacity are caller-supplied rather than fixed constants here (unlike
		/// <see cref="TryGetConduitPressure"/>'s own <see cref="GasConduitVolumeM3"/>): real per-species
		/// density belongs to <see cref="MaterialPropertyRegistry"/>, so the caller passes whatever
		/// value it already uses for its own tank rather than this method inventing a second,
		/// possibly inconsistent one (the overload below resolves it from the registry).
		/// Liquid conduits only (<c>Game.Instance.liquidConduitFlow</c>). Returns <c>false</c> for
		/// an unconnected or empty segment.
		/// </summary>
		public static bool TryGetLiquidConduitFillFraction(int cell, float densityKgM3, float conduitCapacityM3, out float fraction)
		{
			fraction = 0f;
			if (!Grid.IsValidCell(cell) || conduitCapacityM3 <= 0f)
			{
				return false;
			}
			ConduitFlow liquidFlow = Game.Instance.liquidConduitFlow;
			if (liquidFlow == null || !liquidFlow.HasConduit(cell))
			{
				return false;
			}
			ConduitFlow.ConduitContents contents = liquidFlow.GetContents(cell);
			if (contents.mass <= 0f)
			{
				return false;
			}
			fraction = LiquidVolumeFromMass(contents.mass, densityKgM3) / conduitCapacityM3;
			return true;
		}

		/// <summary>
		/// <see cref="TryGetLiquidConduitFillFraction(int,float,float,out float)"/> with the
		/// density resolved per species from <see cref="MaterialPropertyRegistry"/> instead of
		/// supplied by the caller, falling back to <paramref name="fallbackDensityKgM3"/> for a
		/// substance the registry has no record for.
		///
		/// Prefer this overload. A conduit already knows what it is carrying
		/// (<c>ConduitContents.element</c>), so a caller passing one fixed density for every
		/// liquid in the game is discarding information it already had, reading every liquid as
		/// though it were water. The explicit-density overload remains for a caller that genuinely wants to
		/// impose one (a comparison readout, or a container whose contents are not a real ONI
		/// element).
		/// </summary>
		public static bool TryGetLiquidConduitFillFractionForSpecies(int cell, float conduitCapacityM3,
			float fallbackDensityKgM3, out float fraction)
		{
			fraction = 0f;
			if (!Grid.IsValidCell(cell) || conduitCapacityM3 <= 0f)
			{
				return false;
			}
			ConduitFlow liquidFlow = Game.Instance.liquidConduitFlow;
			if (liquidFlow == null || !liquidFlow.HasConduit(cell))
			{
				return false;
			}
			ConduitFlow.ConduitContents contents = liquidFlow.GetContents(cell);
			if (contents.mass <= 0f)
			{
				return false;
			}
			float densityKgM3 = MaterialPropertyRegistry.TryGetLiquidDensityKgPerM3(
				contents.element, out float registryDensityKgM3)
				? registryDensityKgM3
				: fallbackDensityKgM3;
			fraction = LiquidVolumeFromMass(contents.mass, densityKgM3) / conduitCapacityM3;
			return true;
		}

		[DllImport("SimDLL")]
		private static extern float SIM_EqualizeLiquidVolumeMass(float densityKgM3, float massA, float capacityA_m3, float massB, float capacityB_m3, float rate);

		/// <summary>
		/// How much mass of ONE shared liquid species should move from container B into
		/// container A this step, to equalize their FILL FRACTION (volume / capacity) -- the
		/// liquid counterpart to <see cref="EqualizeSingleSpeciesMass"/>, built on
		/// <c>sim/liquid_mixture.h</c>'s <c>EqualizeLiquidVolume</c> instead of
		/// <c>gas::EqualizeSingleSpecies</c>. Same physics motivation as the gas version (a tank
		/// plumbed to a pipe with no valve between them is one connected system, not an active
		/// pump), different underlying model because liquids and gases are not the same kind of
		/// object physically: real Stationeers equalizes liquid networks by volume ratio
		/// (<c>RemoveLiquidToEqualize</c>), never by partial pressure. Positive means A gains
		/// (from B), negative means A loses (to B). Same <c>rate</c>/damping contract as
		/// <see cref="EqualizeSingleSpeciesMass"/> -- carries the same extra *0.5 on top of
		/// <paramref name="rate"/>, converging geometrically over repeated calls.
		/// </summary>
		public static float EqualizeLiquidVolumeMass(float densityKgM3, float massA, float capacityA_m3, float massB, float capacityB_m3, float rate)
		{
			if (unavailable)
			{
				throw NoExport("EqualizeLiquidVolumeMass");
			}
			try
			{
				return SIM_EqualizeLiquidVolumeMass(densityKgM3, massA, capacityA_m3, massB, capacityB_m3, rate);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_EqualizeLiquidVolumeMass");
				throw NoExport("EqualizeLiquidVolumeMass");
			}
		}

		/// <summary>
		/// Liquid counterpart to <see cref="ManagedConduitCells"/> -- a SEPARATE set, not shared
		/// with the gas one, because a single cell can legitimately host both a gas conduit and a
		/// liquid conduit at once (two different <c>ObjectLayer</c>s over the same tile); a
		/// shared set keyed on cell alone would let managing one conduit type wrongly suppress
		/// vanilla's own mover for the OTHER type on the same tile. Pruned by
		/// <see cref="PruneManagedLiquidConduitCells"/>, called automatically by
		/// <see cref="EqualizeLiquidNetwork"/>.
		/// </summary>
		public static readonly HashSet<int> ManagedLiquidConduitCells = new HashSet<int>();

		/// <summary>
		/// Liquid counterpart to <see cref="PruneManagedConduitCells"/> -- removes any cell whose
		/// liquid conduit is gone.
		/// </summary>
		public static void PruneManagedLiquidConduitCells()
		{
			if (ManagedLiquidConduitCells.Count == 0)
			{
				return;
			}
			List<int> stale = null;
			foreach (int cell in ManagedLiquidConduitCells)
			{
				if (!Grid.IsValidCell(cell) || Grid.Objects[cell, (int)ObjectLayer.LiquidConduit] == null)
				{
					(stale ?? (stale = new List<int>())).Add(cell);
				}
			}
			if (stale != null)
			{
				for (int i = 0; i < stale.Count; i++)
				{
					ManagedLiquidConduitCells.Remove(stale[i]);
				}
			}
		}

		/// <summary>
		/// Liquid counterpart to <see cref="FloodFillPipeNetwork"/> -- walks
		/// <c>ObjectLayer.LiquidConduit</c> instead of <c>ObjectLayer.GasConduit</c>. Same
		/// per-tile-conduit shape and same <see cref="MaxPipeNetworkTiles"/> cap.
		/// </summary>
		public static List<int> FloodFillLiquidNetwork(int startCell)
		{
			var result = new List<int>();
			if (!Grid.IsValidCell(startCell) || Grid.Objects[startCell, (int)ObjectLayer.LiquidConduit] == null)
			{
				return result;
			}

			var visited = new HashSet<int>();
			var queue = new Queue<int>();
			visited.Add(startCell);
			queue.Enqueue(startCell);

			while (queue.Count > 0 && result.Count < MaxPipeNetworkTiles)
			{
				int cell = queue.Dequeue();
				result.Add(cell);

				for (int i = 0; i < 4; i++)
				{
					int neighbor = Grid.OffsetCell(cell, NeighbourDX[i], NeighbourDY[i]);
					if (!Grid.IsValidCell(neighbor) || visited.Contains(neighbor))
					{
						continue;
					}
					if (Grid.Objects[neighbor, (int)ObjectLayer.LiquidConduit] == null)
					{
						continue;
					}
					if (!ConduitsJoined(cell, neighbor, i, gas: false))
					{
						continue;
					}
					visited.Add(neighbor);
					queue.Enqueue(neighbor);
				}
			}

			return result;
		}

		/// <summary>
		/// Liquid counterpart to <see cref="EqualizePipeNetwork"/> -- same fix for the same real
		/// bug (vanilla's own per-tile <c>ConduitFlow.UpdateConduit</c> mover fighting a
		/// network-wide redistribution), applied to <c>Game.Instance.liquidConduitFlow</c>
		/// instead of the gas flow. See that method's own doc comment for the full mechanism;
		/// not re-explained here.
		/// </summary>
		/// <summary>
		/// Below this per-tile share, an equalisation writes the network's whole remaining mass
		/// into a SINGLE tile and empties the rest, instead of smearing a sub-milligram share
		/// across every tile.
		///
		/// Without it, a Water Sieve can sit permanently on vanilla's "Pipe Blocked" status with
		/// its output tile holding <c>massKg: 1.027761E-24</c> -- float dust, left by dividing a
		/// nearly-drained network evenly across its cells. `ConduitFlow.IsConduitEmpty` is literally
		/// `contents.mass &lt;= 0f`, and 1e-24 is not &lt;= 0, so
		/// `RequireOutputs` read the pipe as occupied forever and pulled the sieve's
		/// `pipesHaveRoom` Operational Requirement flag false.
		///
		/// The dust is worse than one stuck status item: a conduit tile holds exactly ONE element
		/// (`ConduitFlow.AddElement` returns 0 outright for a different element), so a 1e-24 kg
		/// ghost of the last species to pass through locks that tile against every other species
		/// indefinitely.
		///
		/// CONJURED VALUE -- vanilla has no epsilon of its own to borrow here (`ConduitFlow` carries
		/// MAX_LIQUID_MASS = 10f and MAX_GAS_MASS = 1f and nothing at the bottom of the range).
		/// 1e-6 kg is one milligram: six orders of magnitude under a single full gas tile, eighteen
		/// orders above the dust shown above, and far below anything a player or a test can
		/// see.
		///
		/// Consolidating rather than deleting is deliberate: MATTER IS CONSERVED is a project rule,
		/// and dropping the remainder -- however small -- would be this mod quietly doing the magic
		/// deletion it exists to remove. The surviving tile also keeps the network's mass-weighted
		/// average temperature, so the heat capacity travels with the mass and enthalpy balances
		/// too.
		/// </summary>
		private const float NegligibleTileMassKg = 1e-6f;

		public static void EqualizeLiquidNetwork(int startCell)
		{
			PruneManagedLiquidConduitCells();
			List<int> cells = FloodFillLiquidNetwork(startCell);
			if (cells.Count == 0)
			{
				return;
			}

			for (int i = 0; i < cells.Count; i++)
			{
				ManagedLiquidConduitCells.Add(cells[i]);
			}
			ApplyManagedRunPolicy(startCell, PipeContentType.Liquid);

			ConduitFlow liquidFlow = Game.Instance.liquidConduitFlow;

			float totalMass = 0f;
			float weightedTempSum = 0f;
			SimHashes element = SimHashes.Vacuum;

			for (int i = 0; i < cells.Count; i++)
			{
				ConduitFlow.ConduitContents contents = liquidFlow.GetContents(cells[i]);
				if (contents.mass <= 0f)
				{
					continue;
				}
				totalMass += contents.mass;
				weightedTempSum += contents.mass * contents.temperature;
				if (element == SimHashes.Vacuum)
				{
					element = contents.element;
				}
			}

			if (totalMass <= 0f)
			{
				return;
			}

			float avgTemp = weightedTempSum / totalMass;
			float perTile = totalMass / cells.Count;

			// See NegligibleTileMassKg -- this is the case that found the bug, on this very
			// network: the Water Sieve's output tile left holding 1.027761E-24 kg.
			if (perTile < NegligibleTileMassKg)
			{
				liquidFlow.SetContents(cells[0],
					new ConduitFlow.ConduitContents(element, totalMass, avgTemp, byte.MaxValue, 0));
				for (int i = 1; i < cells.Count; i++)
				{
					liquidFlow.SetContents(cells[i], ConduitFlow.ConduitContents.Empty);
				}

				// Dissolved cargo rides the mass, so it goes where the mass just went. Without this the
				// network's whole liquid ends up on one tile and its dissolved gas stays spread
				// over tiles that are now empty -- where the next thing to touch them releases it.
				DissolvedCargo.ConduitConsolidate(cells, cells[0]);
				return;
			}

			var shared = new ConduitFlow.ConduitContents(element, perTile, avgTemp, byte.MaxValue, 0);
			for (int i = 0; i < cells.Count; i++)
			{
				liquidFlow.SetContents(cells[i], shared);
			}

			// Same reason as the consolidate branch above, and the same rule: equal masses carry
			// equal cargo. This method levels a network in one pass rather than moving tile to
			// tile, so there are no pairwise moves for the dissolved-cargo transfer patches to mirror -- it
			// has to be told. Inert when nothing dissolved is in the network.
			DissolvedCargo.ConduitSpreadEven(cells);

			// INVARIANT CHECK. "Spreading a network's mass evenly over its tiles does not change
			// the total" is a real invariant and cheap to check, and because a silent violation
			// here would surface far downstream as physics. It reports ONCE per session: this
			// runs per network per tick, so a persistent violation would otherwise write
			// thousands of identical lines and bury whatever else the log was going to say.
			float after = 0f;
			for (int i = 0; i < cells.Count; i++)
			{
				after += liquidFlow.GetContents(cells[i]).mass;
			}
			if (System.Math.Abs(after - totalMass) > 1e-3f && !reportedEqualizeLoss)
			{
				reportedEqualizeLoss = true;
				FrameworkLog.Error(string.Format(
					"EqualizeLiquidNetwork LOST MASS IN PLACE: {0} cell(s), "
					+ "{1:F4} kg in, {2:F4} kg out, delta {3:F4} kg, perTile {4:F6}, "
					+ "element {5}, avgTemp {6:F2} K. Reported once per session.",
					cells.Count, totalMass, after, after - totalMass, perTile, element, avgTemp));
			}
		}

		/// <summary>Set once the mass-conservation check above has reported, so a persistent
		/// violation states itself rather than repeating per network per tick.</summary>
		private static bool reportedEqualizeLoss;

		[DllImport("SimDLL")]
		private static extern void SIM_ComputePhaseChangeStep(float massKg, float temperatureK, float thresholdK,
			float latentHeatJPerKg, float specificHeatCapacity, float dtSeconds, float conversionRatePerSecond,
			float minRemainderKg, out float outConvertedMassKg, out float outRemainingTemperatureK);

		/// <summary>
		/// One step's worth of real, latent-heat-conserving phase change for
		/// <paramref name="massKg"/> kg of a single species sitting at <paramref name="temperatureK"/>,
		/// crossing <paramref name="thresholdK"/> -- <c>sim/phase_change.h</c>'s
		/// <c>ComputePhaseChangeStep</c>. See that header's own doc comment for the derivation:
		/// vanilla ONI phase change (<c>TransitionCell</c>) does not conserve energy at all
		/// (neither the native <c>Element</c> struct nor the managed <c>Element</c> class carries
		/// a latent-heat field), so this follows the energy accounting of Stationeers' state
		/// change instead.
		///
		/// Returns <paramref name="convertedMassKg"/>, the mass to move to the OTHER phase this
		/// step (0 if nothing should convert), and <paramref name="remainingTemperatureK"/>, the
		/// SOURCE mass's own temperature after paying (or being warmed by) the latent-heat cost --
		/// always clamped so a single call can never cross back past <paramref name="thresholdK"/>.
		/// Pure function -- no simulation state touched, safe to call every tick for every species
		/// a tank holds.
		/// </summary>
		public static void ComputePhaseChangeStep(float massKg, float temperatureK, float thresholdK,
			float latentHeatJPerKg, float specificHeatCapacity, float dtSeconds, float conversionRatePerSecond,
			float minRemainderKg, out float convertedMassKg, out float remainingTemperatureK)
		{
			if (unavailable)
			{
				throw NoExport("ComputePhaseChangeStep");
			}
			try
			{
				SIM_ComputePhaseChangeStep(massKg, temperatureK, thresholdK, latentHeatJPerKg, specificHeatCapacity,
					dtSeconds, conversionRatePerSecond, minRemainderKg, out convertedMassKg, out remainingTemperatureK);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ComputePhaseChangeStep");
				throw NoExport("ComputePhaseChangeStep");
			}
		}
		// ------------------------------------------------------------------------------------
		// Per-substance material properties: FORWARDERS ONLY.
		//
		// The tables live in MaterialPropertyRegistry, where a third-party mod can read and
		// extend them. These methods forward there because the queries are gas-mixture-adjacent
		// and existing callers use them. New code should call MaterialPropertyRegistry directly.
		// ------------------------------------------------------------------------------------

		/// <summary>
		/// Liquid&lt;-&gt;gas latent heat (J/kg) for <paramref name="id"/>. Forwards to
		/// <see cref="MaterialPropertyRegistry.TryGetLatentHeatOfVaporizationJPerKg"/> -- see that
		/// method for the provenance of the values and for why the mol-to-kg conversion uses the
		/// registry's own molecular mass rather than Klei's <c>Element.molarMass</c>.
		/// </summary>
		public static bool TryGetLatentHeatJPerKg(SimHashes id, out float latentHeatJPerKg)
		{
			return MaterialPropertyRegistry.TryGetLatentHeatOfVaporizationJPerKg(id,
				out latentHeatJPerKg);
		}

		/// <summary>
		/// Melting/freezing (solid&lt;-&gt;liquid) latent heat for <paramref name="id"/>, the
		/// counterpart to <see cref="TryGetLatentHeatJPerKg"/>'s boiling/condensation
		/// (liquid&lt;-&gt;gas) value. A caller running a solid&lt;-&gt;liquid transition must use
		/// THIS one, not the vaporization value, or it overcharges the energy cost fivefold.
		/// Forwards to <see cref="MaterialPropertyRegistry.TryGetLatentHeatOfFusionJPerKg"/>.
		/// </summary>
		public static bool TryGetLatentHeatOfFusionJPerKg(SimHashes id, out float latentHeatJPerKg)
		{
			return MaterialPropertyRegistry.TryGetLatentHeatOfFusionJPerKg(id,
				out latentHeatJPerKg);
		}

		/// <summary>
		/// Sublimation/deposition (solid&lt;-&gt;gas) latent heat for <paramref name="id"/>:
		/// vaporization plus fusion, since the substance crosses both phase boundaries at once.
		/// Forwards to <see cref="MaterialPropertyRegistry.TryGetLatentHeatOfSublimationJPerKg"/>.
		/// </summary>
		public static bool TryGetLatentHeatOfSublimationJPerKg(SimHashes id, out float latentHeatJPerKg)
		{
			return MaterialPropertyRegistry.TryGetLatentHeatOfSublimationJPerKg(id,
				out latentHeatJPerKg);
		}

		/// <summary>
		/// The RAW pressure-dependent evaporation/condensation point (K) at
		/// <paramref name="pressurePa"/>. Forwards to
		/// <see cref="MaterialPropertyRegistry.TryGetEvaporationTemperatureK"/>.
		///
		/// Prefer <see cref="TryGetEvaporationTemperatureClampedK"/> at essentially every call
		/// site -- a power-law fit is only meaningful inside its fitted range, and this one
		/// returns 89.28 K for Water at 1 Pa.
		/// </summary>
		public static bool TryGetEvaporationTemperatureK(SimHashes id, float pressurePa, out float temperatureK)
		{
			return MaterialPropertyRegistry.TryGetEvaporationTemperatureK(id, pressurePa, out temperatureK);
		}

		/// <summary>
		/// The temperature below which <paramref name="id"/> has no liquid phase -- the LOWER
		/// bound of <see cref="TryGetEvaporationTemperatureClampedK"/>. Forwards to
		/// <see cref="MaterialPropertyRegistry.TryGetFreezingTemperatureK"/>.
		/// </summary>
		public static bool TryGetFreezingTemperatureK(SimHashes id, out float temperatureK)
		{
			return MaterialPropertyRegistry.TryGetFreezingTemperatureK(id, out temperatureK);
		}

		/// <summary>
		/// The temperature above which <paramref name="id"/> has no liquid phase -- the UPPER
		/// bound of <see cref="TryGetEvaporationTemperatureClampedK"/>. Forwards to
		/// <see cref="MaterialPropertyRegistry.TryGetCriticalTemperatureK"/>.
		/// </summary>
		public static bool TryGetCriticalTemperatureK(SimHashes id, out float temperatureK)
		{
			return MaterialPropertyRegistry.TryGetCriticalTemperatureK(id, out temperatureK);
		}

		/// <summary>
		/// <see cref="TryGetEvaporationTemperatureK"/> bounded to the range over which a liquid
		/// phase actually exists -- Stationeers' clamped evaporation temperature, the form every
		/// one of its own state-change call sites uses.
		///
		/// ALWAYS PREFER THIS over the raw curve for a phase-change threshold. Concretely, for
		/// Water the raw fit returns 89.28 K at 1 Pa, an unphysical number from evaluating the
		/// curve far outside its valid range. The clamp resolves that same case to water's
		/// freezing point (Klei's 272.5 K): real liquid water pulled down to its freezing point stops boiling and
		/// freezes. This also removes any need for a caller-side "near vacuum" floor pressure.
		///
		/// Applies to the LIQUID&lt;-&gt;GAS transition only -- melting/freezing is a different
		/// boundary and is very nearly pressure-independent. Forwards to
		/// <see cref="MaterialPropertyRegistry.TryGetEvaporationTemperatureClampedK"/>.
		/// </summary>
		public static bool TryGetEvaporationTemperatureClampedK(SimHashes id, float pressurePa, out float temperatureK)
		{
			return MaterialPropertyRegistry.TryGetEvaporationTemperatureClampedK(id, pressurePa, out temperatureK);
		}

		/// <summary>
		/// The saturation (vapor) pressure in pascals at <paramref name="temperatureK"/> -- the
		/// forward direction of the curve <see cref="TryGetEvaporationTemperatureClampedK"/>
		/// inverts. Forwards to
		/// <see cref="MaterialPropertyRegistry.TryGetSaturationPressurePa"/>, which carries the
		/// derivation and the live failure that motivated it.
		///
		/// This is the accessor to reach for when sizing a charge, a tank fill or a set point:
		/// it answers "what pressure does this fluid want to sit at, at this temperature", so a
		/// caller can put a system into a state its own vapor curve agrees with instead of
		/// picking a number and hoping.
		/// </summary>
		public static bool TryGetSaturationPressurePa(SimHashes id, float temperatureK,
			out float pressurePa)
		{
			return MaterialPropertyRegistry.TryGetSaturationPressurePa(id, temperatureK,
				out pressurePa);
		}
	}
}
