using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads the replacement SimDLL's energy conservation ledger (its LEDGERS document) from
	/// managed code.
	///
	/// Without an energy ledger, "heat is not deleted" is an assertion nothing can contradict.
	/// This is the managed half of the instrument that can.
	///
	/// Exposed in the framework rather than kept inside a gameplay mod because a third-party mod that
	/// adds a heat source should be able to ask whether its own colony still balances, and to
	/// see how much of the heat came from the power grid rather than merely that heat appeared.
	///
	/// <b>Kilojoules throughout.</b> ONI's <c>Element.specificHeatCapacity</c> is kJ/(kg·K), so
	/// every figure the sim keeps is kJ.
	///
	/// Only present with the custom SimDLL. On a stock one the export does not exist and
	/// <see cref="TryRead"/> returns <c>false</c> rather than throwing, so a mod can degrade
	/// instead of faulting.
	/// </summary>
	public static class EnergyLedgerFacade
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugEnergyLedger(double* outFields, int count);

		/// <summary>
		/// A snapshot of the ledger. The four reservoirs are instantaneous totals; the buckets
		/// are cumulative since the world was allocated.
		/// </summary>
		public struct Snapshot
		{
			/// <summary>Cells: mass × specificHeatCapacity × temperature, over game cells.</summary>
			public double GridKJ;
			/// <summary>Building structure temperatures.</summary>
			public double BuildingsKJ;
			/// <summary>Conduit (pipe) contents.</summary>
			public double ConduitsKJ;
			/// <summary>Element chunks -- matter the game holds outside the grid.</summary>
			public double ChunksKJ;
			/// <summary>Vanilla self-heat: <c>operating_kilowatts × dt</c>, cumulative.</summary>
			public double BuildingOperatingKJ;
			/// <summary>
			/// Non-conservation inside Klei's own building↔cell exchange, where the two sides
			/// are clamped independently. Measured at the rounding floor in every offline
			/// scenario, but published rather than hidden.
			/// </summary>
			public double BuildingExchangeKJ;
			/// <summary>Energy carried in or out by registering, replacing or dropping a building.</summary>
			public double BuildingRegisteredKJ;
			/// <summary>
			/// <c>ModifyBuildingEnergy</c> -- Klei's <c>ProduceEnergy</c> and the conduit energy
			/// sink. Heat the game accounted somewhere else, kept apart from new energy.
			/// </summary>
			public double BuildingEnergyMsgKJ;
			/// <summary>
			/// <see cref="PowerHeat"/>'s derived power → heat rule, and only that. This is the
			/// number that answers "how much of this colony's heat came from the power grid".
			/// </summary>
			public double BuildingWasteHeatKJ;
			/// <summary>
			/// How many buildings are registered with the sim's heat-exchange list. A census,
			/// not an energy, and published because a <see cref="BuildingsKJ"/> of 0 is
			/// otherwise ambiguous: it means either that no building is registered or that
			/// they are all registered with no heat capacity, which are different faults.
			/// </summary>
			public double RegisteredBuildingCount;
			/// <summary>
			/// The sim's own accumulated substep time, in seconds -- charged once per substep,
			/// not once per region. Not an energy. It exists so a caller can compare the clock
			/// the sim charges on against the clock it measures with, instead of assuming the
			/// two agree.
			/// </summary>
			public double SimSeconds;
			/// <summary>
			/// <c>ModifyCellEnergy</c> — the only channel that puts heat straight into a grid
			/// cell, and the one Klei's <c>StructureTemperatureComponents.ExhaustHeat</c> uses.
			/// What actually landed, measured across the temperature write alone.
			/// </summary>
			public double CellEnergyMsgKJ;
			/// <summary>
			/// What the same message asked for and did not get: vacuum, under 0.001 kg, a cell
			/// already at or above the building's overheat temperature, or the 10000 K clamp --
			/// and, with <see cref="SimTunable.CellEnergyCarry"/> off, the float rounding of the
			/// temperature write, in either sign (see <see cref="CellEnergyCarriedKJ"/>).
			///
			/// <para>Deliberately NOT part of <see cref="AccountedInflowKJ"/>. This is the
			/// deletion itself, made countable; adding it would let the loss cancel out and the
			/// ledger would report conservation exactly where there is none.</para>
			/// </summary>
			public double CellEnergyRefusedKJ;
			/// <summary>
			/// A building's exhaust heat, moved into the sim as a rate by
			/// <see cref="ExhaustHeat"/>. New energy the building's operation invents, exactly
			/// like its self-heat, so it counts as inflow.
			/// </summary>
			public double BuildingExhaustKJ;
			/// <summary>
			/// How much of <see cref="BuildingExhaustKJ"/> could not be delivered to the cells
			/// and went into the building's own body instead — precisely what vanilla destroys.
			///
			/// <para>NOT part of <see cref="AccountedInflowKJ"/>: it is already inside
			/// <see cref="BuildingExhaustKJ"/>, and adding it would double-count.</para>
			/// </summary>
			public double BuildingExhaustBouncedKJ;
			/// <summary>
			/// Energy radiated out of building bodies to the environment
			/// (<see cref="Radiation"/>). An <b>outflow</b> — a named boundary crossing with a
			/// receiver, not a deletion — so it is subtracted from the accounted inflow.
			/// </summary>
			public double RadiatedKJ;

			// ---------------------------------------------------------------- fields 16..35
			//
			// The whole published set is exposed, not only the fields one caller wanted: a
			// surplus that can be localised to a chunk cannot be ATTRIBUTED without the buckets
			// that describe what happens to a chunk, and reasoning about the unread half of an
			// instrument is how a ledger gets misread.
			//
			// AccountedInflowKJ and AnchorKJ deliberately DO NOT change. Three probes already
			// assert against those two numbers; folding twenty new buckets into them would
			// silently move every one of those baselines. The new buckets are summed by
			// ExtendedInflowKJ instead, so the older figure keeps meaning exactly what it
			// meant.

			/// <summary>Energy carried in or out by registering or dropping an element chunk.</summary>
			public double ChunkRegisteredKJ;
			/// <summary><c>ModifyChunkEnergy</c> -- heat the game hands a chunk directly.</summary>
			public double ChunkEnergyMsgKJ;
			/// <summary>
			/// The chunk temperature adjuster: a fictitious second body a chunk exchanges
			/// against (a suit in a dock, food in a fridge). Its own side of the solve is
			/// discarded, so nothing pays for what the chunk gains -- a boundary crossing with
			/// a named source.
			/// </summary>
			public double ChunkAdjusterKJ;
			/// <summary>
			/// Non-conservation inside Klei's own chunk↔cell exchange. The counterpart of
			/// <see cref="BuildingExchangeKJ"/>, and the same cause:
			/// <c>CalculateTemperatureExchange</c> clamps each side to the pair's equilibrium
			/// independently, so whenever either clamp bites the two bodies disagree about how
			/// much moved. Positive is energy the exchange invented.
			///
			/// Unlike the building bucket this one is <b>not</b> at the rounding floor when the
			/// pair is badly mismatched -- a heavy chunk sitting in a light gas cell is the
			/// worst case, and it is also a completely ordinary thing for a building to be.
			/// </summary>
			public double ChunkExchangeKJ;
			/// <summary>Chunk energy a message offered that the chunk would not take.</summary>
			public double ChunkEnergyRefusedKJ;
			/// <summary><c>ModifyCell</c>: energy carried by a cell's contents being replaced.</summary>
			public double CellModifiedKJ;
			/// <summary>Energy moved by liquid displacement.</summary>
			public double DisplacedKJ;
			/// <summary>The 1 K floor in cell-to-cell conduction.</summary>
			public double ConductionClampKJ;
			/// <summary>Energy in cells the sim cleared.</summary>
			public double ClearedKJ;
			/// <summary>Energy in mass deleted by the wisp rule.</summary>
			public double WispKJ;
			/// <summary>Energy in mass deleted by the thin-liquid rule.</summary>
			public double ThinLiquidKJ;
			/// <summary>Energy in mass deleted as unstable.</summary>
			public double UnstableKJ;
			/// <summary>Energy removed by element consumers.</summary>
			public double ConsumedEnergyKJ;
			/// <summary>Energy added by element emitters.</summary>
			public double EmittedEnergyKJ;
			/// <summary>Energy added by component emitters.</summary>
			public double ComponentEmittedEnergyKJ;
			/// <summary>Non-conservation in the fluid movers.</summary>
			public double MoverKJ;
			/// <summary>Energy present at world initialisation, including the vacuum rect.</summary>
			public double WorldInitKJ;
			/// <summary>Energy carried by sublimated mass.</summary>
			public double SublimatedEnergyKJ;
			/// <summary>Latent heat exchanged by phase change.</summary>
			public double PhaseChangeKJ;
			/// <summary>Energy carried by ore transition.</summary>
			public double TransitionOreKJ;

			// ---------------------------------------------------------------- fields 36..38
			//
			// The planetary buckets. Guarded separately
			// from fields 16..35 by <see cref="HasPlanetaryBuckets"/>, because raising the older
			// gate would switch off twenty working buckets on every DLL that has not caught up.

			/// <summary>
			/// Sunlight a building body or a grid cell absorbed from the world's star
			/// (<see cref="PlanetaryEnvironment"/>). An <b>inflow</b>, and
			/// <see cref="RadiatedKJ"/>'s twin across the same boundary -- the difference
			/// between the two is a world's net radiative budget, which is the one number a
			/// Dynamic Planet rig actually wants.
			/// </summary>
			public double AbsorbedKJ;

			/// <summary>
			/// Heat that arrived inside the matter the planetary surface boundary supplied, plus
			/// what the boundary moved holding a sky-exposed cell at the planet's temperature.
			/// An <b>inflow</b>. Its mass twin is charged at the same call site.
			/// </summary>
			public double AtmosphereBoundaryKJ;

			/// <summary>
			/// What the GROUND shed into its own sky: a sky-exposed grid cell radiating by the
			/// world's own <see cref="WorldEnvironment.SolarAbsorptivity"/>, which by Kirchhoff
			/// is its emissivity too. An <b>outflow</b>, and <see cref="AbsorbedKJ"/>'s twin.
			///
			/// Deliberately NOT the same bucket as <see cref="RadiatedKJ"/>, which is what the
			/// colony's BUILDINGS shed. The finite-reservoir policy
			/// (<see cref="PlanetaryEnvironment.ReservoirHeatCapacityKJPerK"/>) warms a planet by
			/// the colony's waste heat, and a bare planet radiating its own daylight back at its
			/// own sky is not that -- one bucket for both would have an unvisited Mars heat
			/// itself in proportion to how much of it the player had uncovered.
			/// </summary>
			public double CellRadiatedKJ;

			/// <summary>
			/// SURFACE UPTAKE (field 39): the grid's own change where a liquid's free
			/// surface dissolved gas out of the cell above -- that vanilla gas cell's
			/// <c>m c T</c> leaving, plus the heat the gas carried above the liquid's temperature
			/// landing in the liquid. Signed as the grid sees it (it charges negative); gas taken
			/// from the mixture layer charges only the liquid's heat. See
			/// <see cref="Effervescence.SetSurfaceExchange"/>. Zero unless
			/// <see cref="HasUptakeBucket"/>.
			/// </summary>
			public double DissolvedSurfaceKJ;
			/// <summary>
			/// Field 40: the energy the cells' rounding carries hold right now, with the SimDLL's
			/// <see cref="SimTunable.CellEnergyCarry"/> row on -- paid by <c>ModifyCellEnergy</c>,
			/// not yet in any cell's temperature. A stock, not a flow, and in neither
			/// <see cref="TotalKJ"/> nor an inflow: <see cref="CellEnergyMsgKJ"/> is what reached
			/// the grid, so a payment splits into that, <see cref="CellEnergyRefusedKJ"/> and the
			/// change in this. Zero with the row off. Zero unless <see cref="HasCarryBucket"/>.
			/// </summary>
			public double CellEnergyCarriedKJ;

			/// <summary>
			/// How many fields the DLL actually published into this snapshot. Fields past this
			/// index are left at zero and mean "not reported", which is not the same as "zero
			/// kilojoules" -- see <see cref="HasExtendedBuckets"/>.
			/// </summary>
			public int PublishedFieldCount;

			/// <summary>
			/// Whether fields 16..35 are real. False against an older custom SimDLL, where the
			/// extended buckets read zero because nothing filled them.
			/// </summary>
			public bool HasExtendedBuckets
			{
				get { return PublishedFieldCount >= ExtendedFieldCount; }
			}

			/// <summary>
			/// Whether fields 36..38 -- the three planetary buckets -- are real. False against a
			/// SimDLL that publishes fewer fields, where they read zero because nothing filled them.
			/// </summary>
			public bool HasPlanetaryBuckets
			{
				get { return PublishedFieldCount >= PlanetaryFieldCount; }
			}

			/// <summary>Whether field 39, <see cref="DissolvedSurfaceKJ"/>, is real. False against
			/// a SimDLL that publishes fewer fields.</summary>
			public bool HasUptakeBucket
			{
				get { return PublishedFieldCount >= UptakeFieldCount; }
			}

			/// <summary>Whether field 40, <see cref="CellEnergyCarriedKJ"/>, is real. False
			/// against a simulation library that does not publish it.</summary>
			public bool HasCarryBucket
			{
				get { return PublishedFieldCount >= CarryFieldCount; }
			}

			/// <summary>The four reservoirs summed -- the quantity that must not move on its own.</summary>
			public double TotalKJ
			{
				get { return GridKJ + BuildingsKJ + ConduitsKJ + ChunksKJ; }
			}

			/// <summary>Everything that entered from outside, as far as the ledger is charged.</summary>
			public double AccountedInflowKJ
			{
				get
				{
					return BuildingOperatingKJ + BuildingExchangeKJ + BuildingRegisteredKJ
						+ BuildingEnergyMsgKJ + BuildingWasteHeatKJ + CellEnergyMsgKJ
						+ BuildingExhaustKJ - RadiatedKJ;
				}
			}

			/// <summary>
			/// <see cref="AccountedInflowKJ"/> plus every bucket in fields 16..35. Zero of the
			/// older figure's meaning is changed by this; it is a second, wider reading for a
			/// caller that wants the whole published set rather than the building sites alone.
			/// Meaningless unless <see cref="HasExtendedBuckets"/>.
			/// </summary>
			public double ExtendedInflowKJ
			{
				get
				{
					return AccountedInflowKJ + ChunkRegisteredKJ + ChunkEnergyMsgKJ
						+ ChunkAdjusterKJ + ChunkExchangeKJ + CellModifiedKJ + DisplacedKJ
						+ ConductionClampKJ + ClearedKJ + WispKJ + ThinLiquidKJ + UnstableKJ
						+ ConsumedEnergyKJ + EmittedEnergyKJ + ComponentEmittedEnergyKJ
						+ MoverKJ + WorldInitKJ + SublimatedEnergyKJ + PhaseChangeKJ
						+ TransitionOreKJ + AbsorbedKJ + AtmosphereBoundaryKJ
						- CellRadiatedKJ + DissolvedSurfaceKJ;
				}
			}

			/// <summary>
			/// <c>Total - AccountedInflow</c>. The figure that should stay put; a difference
			/// between two of these is the drift.
			///
			/// It is <b>not</b> zero-drift yet and must not be read as if it were: only the
			/// building sites are charged. Seven call sites are still unaccounted -- element
			/// chunks, <c>ModifyCell</c>, phase change, element consumers/emitters, vacuum-rect
			/// init, <c>ModifyEnergy</c> on a cell, mass deletion and sublimation -- so a real
			/// colony will drift for reasons that have nothing to do with any one mod. The replacement
			/// SimDLL's LEDGERS document has the measured table.
			/// </summary>
			public double AnchorKJ
			{
				get { return TotalKJ - AccountedInflowKJ; }
			}
		}

		// The set this reader has always required. Kept as the MINIMUM rather than raised to
		// the full 36, because three probes already call TryRead against a deployed DLL and a
		// stricter test would turn them all off at once the moment the DLL lagged the mod.
		private const int FieldCount = 16;

		// Everything SIM_DebugEnergyLedger publishes today. Append only, never reorder -- the
		// other half of this contract is `kEnergyLedgerFields` in driver/src/diffsim.cpp.
		internal const int ExtendedFieldCount = 36;

		// And the planetary buckets, three of them. A SEPARATE gate rather than
		// a raised one: the twenty buckets above have three callers already, and folding the
		// planetary set into their gate would turn all of them off against any DLL that had not
		// caught up -- the same reasoning that made FieldCount a minimum.
		//
		// Raised 38 -> 39 in the same commit as the bucket rather than given a third constant of
		// its own, because nothing had shipped against the 38 form: 0.5 and 0.6 are four days
		// apart and `HasPlanetaryBuckets` has no caller outside this repository.
		internal const int PlanetaryFieldCount = 39;

		// The surface-uptake bucket, its own gate for the same reason.
		internal const int UptakeFieldCount = 40;

		// The rounding-carry stock, its own gate for the same reason.
		internal const int CarryFieldCount = 41;
		private static bool unavailable;

		/// <summary>
		/// Reads the ledger. Returns <c>false</c> when the custom SimDLL is not installed (the
		/// export is missing), when the world is not allocated yet, or when the DLL is older
		/// than this build and publishes fewer fields than expected -- in every case leaving
		/// <paramref name="snapshot"/> at its default rather than reporting partial numbers as
		/// if they were whole.
		/// </summary>
		public static unsafe bool TryRead(out Snapshot snapshot)
		{
			snapshot = default(Snapshot);
			if (unavailable)
			{
				return false;
			}
			// Sized to the WIDEST set this reader knows about, not to the older gate: a buffer
			// that stopped at 36 would make the DLL's 38 unreadable however many it published.
			double* fields = stackalloc double[CarryFieldCount];
			for (int i = 0; i < CarryFieldCount; i++)
			{
				fields[i] = 0.0;
			}
			int published;
			try
			{
				published = SIM_DebugEnergyLedger(fields, CarryFieldCount);
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL. Latch it so the next call is a bool test rather than another
				// throw -- this is read on a timer by anything that watches conservation.
				unavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return false;
			}
			if (published < FieldCount)
			{
				return false;
			}
			snapshot.GridKJ = fields[0];
			snapshot.BuildingsKJ = fields[1];
			snapshot.ConduitsKJ = fields[2];
			snapshot.ChunksKJ = fields[3];
			snapshot.BuildingOperatingKJ = fields[4];
			snapshot.BuildingExchangeKJ = fields[5];
			snapshot.BuildingRegisteredKJ = fields[6];
			snapshot.BuildingEnergyMsgKJ = fields[7];
			snapshot.BuildingWasteHeatKJ = fields[8];
			snapshot.RegisteredBuildingCount = fields[9];
			snapshot.SimSeconds = fields[10];
			snapshot.CellEnergyMsgKJ = fields[11];
			snapshot.CellEnergyRefusedKJ = fields[12];
			snapshot.BuildingExhaustKJ = fields[13];
			snapshot.BuildingExhaustBouncedKJ = fields[14];
			snapshot.RadiatedKJ = fields[15];
			snapshot.PublishedFieldCount = published;
			// Guarded as a block: a DLL that publishes 16 leaves these at zero AND leaves
			// HasExtendedBuckets false, so a caller cannot read "not reported" as "nothing
			// happened". That distinction is the whole reason PublishedFieldCount is carried.
			if (published >= ExtendedFieldCount)
			{
				snapshot.ChunkRegisteredKJ = fields[16];
				snapshot.ChunkEnergyMsgKJ = fields[17];
				snapshot.ChunkAdjusterKJ = fields[18];
				snapshot.ChunkExchangeKJ = fields[19];
				snapshot.ChunkEnergyRefusedKJ = fields[20];
				snapshot.CellModifiedKJ = fields[21];
				snapshot.DisplacedKJ = fields[22];
				snapshot.ConductionClampKJ = fields[23];
				snapshot.ClearedKJ = fields[24];
				snapshot.WispKJ = fields[25];
				snapshot.ThinLiquidKJ = fields[26];
				snapshot.UnstableKJ = fields[27];
				snapshot.ConsumedEnergyKJ = fields[28];
				snapshot.EmittedEnergyKJ = fields[29];
				snapshot.ComponentEmittedEnergyKJ = fields[30];
				snapshot.MoverKJ = fields[31];
				snapshot.WorldInitKJ = fields[32];
				snapshot.SublimatedEnergyKJ = fields[33];
				snapshot.PhaseChangeKJ = fields[34];
				snapshot.TransitionOreKJ = fields[35];
			}
			if (published >= PlanetaryFieldCount)
			{
				snapshot.AbsorbedKJ = fields[36];
				snapshot.AtmosphereBoundaryKJ = fields[37];
				snapshot.CellRadiatedKJ = fields[38];
			}
			if (published >= UptakeFieldCount)
			{
				snapshot.DissolvedSurfaceKJ = fields[39];
			}
			if (published >= CarryFieldCount)
			{
				snapshot.CellEnergyCarriedKJ = fields[40];
			}
			return true;
		}

		/// <summary>
		/// Whether the custom SimDLL's ledger export has been found. <c>false</c> only after a
		/// <see cref="TryRead"/> has actually failed to bind -- it is not known before the
		/// first call.
		/// </summary>
		public static bool IsAvailable
		{
			get { return !unavailable; }
		}
	}
}
