using System;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// GAS DISSOLVED IN LIQUID. Every liquid cell carries up to <see cref="Lanes"/> dissolved gases, in kg, in the
	/// first-party cell property <see cref="PropertyName"/>. The sim moves them with the liquid
	/// through every liquid kernel (<see cref="CellPropertyTransport.FollowsLiquidMass"/>), saves
	/// them with the world, and hands them back when the liquid leaves the grid.
	///
	/// <b>LANES.</b> A lane is one gas. Which gas owns which lane is fixed here, in
	/// <see cref="LaneElement"/>, and is part of the save format: a lane number is what the save
	/// stores, so a gas never changes lane. Lanes 5 to 7 are unassigned and reserved.
	///
	/// <b>WHERE RELEASED GAS GOES.</b> <see cref="Install"/> routes every released record for
	/// this property back into the world as gas, so matter is conserved:
	/// <list type="bullet">
	/// <item>still in liquid -- a cell a message emptied a share of -- it rises as bubbles through
	/// <see cref="Bubbles.SpawnSplit"/>, and <see cref="Bubbles"/> carries them up;</item>
	/// <item>in a cell that FROZE, with liquid still beside it, it goes into that liquid as
	/// bubbles -- ice excludes dissolved gas, and pushes it into the water that has not frozen
	/// yet (<see cref="FrozenOutKg"/>);</item>
	/// <item>in a cell that is otherwise no longer liquid -- the water boiled to steam, or froze
	/// with nothing liquid beside it -- it goes into the nearest cell that is not solid, starting
	/// with that cell and then the one above, through <c>SimMessages.AddRemoveSubstance</c>. A
	/// boiled cell's gas leaving with its steam is the physical answer, not a fallback.</item>
	/// <item>and a cell that FIZZED is still liquid, so it is the first case: bubbles
	/// (<see cref="Effervescence"/>).</item>
	/// </list>
	///
	/// <b>WHERE CONSUMED GAS GOES.</b> Gas a pump or a drinking duplicant took with its water is
	/// raised on <see cref="Consumed"/> and counted in <see cref="ConsumedKg"/>, and travels with
	/// the water into the pipe and storage through <see cref="DissolvedCargo"/>.
	///
	/// <b>IT SPREADS THROUGH STILL WATER.</b> Transport alone moves an amount only where its
	/// water goes, so a pond carbonated by one column of bubbles would read exactly 0.000 g/kg
	/// one tile away. The sim runs a mixing sweep beside the liquid kernels that evens the
	/// CONCENTRATION out between neighbouring cells of the same liquid, at
	/// <see cref="MixingShare"/> of the gap per pair per substep. It is isotropic, so it leaves a
	/// pond UNIFORM rather than stratified -- carbonated water really is denser and really does
	/// settle downwards, and that part is not modelled.
	///
	/// Gas enters by <see cref="Add"/>, and from the gas above a free surface through
	/// <see cref="Effervescence.SetSurfaceExchange"/>.
	/// </summary>
	public static class DissolvedGas
	{
		/// <summary><c>ext::kDissolvedMassProperty</c>: F32, kg, one component per lane.</summary>
		public const string PropertyName = "sim.dissolved_mass";

		/// <summary><c>ext::kDissolvedGasLanes</c>.</summary>
		public const int Lanes = 8;

		// THE LANE TABLE. Append only: a lane's gas is written into every save that holds any of
		// it. `(SimHashes)0` is an unassigned lane.
		private static readonly SimHashes[] laneElements =
		{
			SimHashes.CarbonDioxide,
			SimHashes.Oxygen,
			SimHashes.ChlorineGas,
			SimHashes.Hydrogen,
			SimHashes.Methane,
			(SimHashes)0,
			(SimHashes)0,
			(SimHashes)0,
		};

		/// <summary>Kilograms released from liquid back into the world as gas since load.</summary>
		public static double ReleasedKg { get; private set; }

		/// <summary>Kilograms taken off the grid by consumers, with their liquid, since load.</summary>
		public static double ConsumedKg { get; private set; }

		/// <summary>Released kilograms on a lane with no gas assigned: only possible if something
		/// wrote the property directly. Counted, and not put anywhere.</summary>
		public static double UnroutedKg { get; private set; }

		/// <summary>Releases that found no <c>BubbleManager</c> and went straight into their cell
		/// as gas instead.</summary>
		public static long FallbackReleases { get; private set; }

		/// <summary>
		/// Gas a consumer took with its liquid: the consumer's kind and id (see
		/// <see cref="LiquidPayloadConsumed"/>), the gas, the kg and the temperature. Raised on
		/// the game thread during <see cref="SimExtFrame.Bound"/>.
		/// </summary>
		public static event Action<LiquidPayloadConsumerKind, int, SimHashes, float, float> Consumed;

		private static int propertyIdx = -1;
		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool Installed
		{
			get { return installed; }
		}

		/// <summary>The registered index of <see cref="PropertyName"/>, or -1 on a SimDLL that
		/// has none (a stock SimDLL).</summary>
		public static int PropertyIndex
		{
			get
			{
				if (propertyIdx < 0)
				{
					propertyIdx = SimExtCellProperties.Find(PropertyName);
				}
				return propertyIdx;
			}
		}

		/// <summary>Whether this SimDLL carries dissolved gas at all.</summary>
		public static bool Available
		{
			get { return PropertyIndex >= 0; }
		}

		/// <summary>The gas that owns <paramref name="lane"/>, or <c>(SimHashes)0</c> for an
		/// unassigned or out-of-range lane.</summary>
		public static SimHashes LaneElement(int lane)
		{
			return lane >= 0 && lane < laneElements.Length ? laneElements[lane] : (SimHashes)0;
		}

		/// <summary>The lane <paramref name="gas"/> dissolves into, or -1 if it has none.</summary>
		public static int LaneOf(SimHashes gas)
		{
			if (gas == (SimHashes)0)
			{
				return -1;
			}
			for (int i = 0; i < laneElements.Length; i++)
			{
				if (laneElements[i] == gas)
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Kilograms of <paramref name="gas"/> dissolved in <paramref name="cell"/>'s liquid; 0
		/// for a gas with no lane or a SimDLL without the property. Takes the sim's worker
		/// barrier: fine for a hover card or a probe, wrong for a sweep (publish the property and
		/// read it through <see cref="SimExtFrame"/> instead).
		/// </summary>
		public static float Read(int cell, SimHashes gas)
		{
			return ReadLane(cell, LaneOf(gas));
		}

		/// <summary><see cref="Read"/> by lane.</summary>
		public static float ReadLane(int cell, int lane)
		{
			float kg;
			if (lane < 0 || PropertyIndex < 0
				|| !SimExtCellProperties.TryReadFloat(PropertyIndex, cell, out kg, lane))
			{
				return 0f;
			}
			return kg;
		}

		/// <summary>
		/// Every lane of <paramref name="cell"/> into <paramref name="kgByLane"/>, which must hold
		/// <see cref="Lanes"/> values; returns the total. Same barrier as <see cref="Read"/>.
		/// </summary>
		public static float Composition(int cell, float[] kgByLane)
		{
			if (kgByLane == null || kgByLane.Length < Lanes)
			{
				throw new ArgumentException("needs room for " + Lanes + " lanes", "kgByLane");
			}
			float total = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				kgByLane[lane] = ReadLane(cell, lane);
				total += kgByLane[lane];
			}
			return total;
		}

		/// <summary>
		/// Dissolve <paramref name="kg"/> of <paramref name="gas"/> into <paramref name="cell"/>'s
		/// liquid, or take it out with a negative amount. The caller owns the matter: gas put in
		/// here must have come out of somewhere, and gas taken out must go somewhere.
		///
		/// Returns false, sending nothing, for a gas with no lane, a SimDLL without the property,
		/// or a cell that is not liquid now. The sim refuses, whole, an amount that is not finite
		/// or that would take the lane below zero -- on <c>sim.message_refused</c>, so a caller
		/// that must know whether an extraction landed subscribes to that. Queued: it lands on
		/// the next tick.
		/// </summary>
		public static bool Add(int cell, SimHashes gas, float kg)
		{
			int lane = LaneOf(gas);
			if (lane < 0 || PropertyIndex < 0 || !Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return false;
			}
			SimExtCellProperties.AddFloat(PropertyIndex, cell, kg, lane);
			return true;
		}

		/// <summary>
		/// How fast dissolved gas EVENS OUT through water that is not moving: the fraction of a
		/// neighbouring pair's concentration gap the sim's mixing sweep closes per pair per
		/// substep. <see cref="SimExtCellProperties.DefaultMixingShare"/> (0.125) unless set,
		/// and setting it to 0 turns the mixing off, so a dissolved amount only ever goes where
		/// its water goes.
		///
		/// Out of range is clamped by the sim, not refused. The write is QUEUED and lands next
		/// tick, and it is not saved, so a mod that wants a rate other than the default sets it
		/// once per session. Reading gives back what was last set THROUGH THIS PROPERTY -- the
		/// sim publishes no read-back for it -- so a mod that never set it reads the default the
		/// sim applied. Inert, and reads the default, on a SimDLL without the property.
		/// </summary>
		public static float MixingShare
		{
			get { return mixingShare; }
			set
			{
				float clamped = Mathf.Clamp01(value);
				mixingShare = clamped;
				if (PropertyIndex >= 0)
				{
					SimExtCellProperties.SetMixing(PropertyIndex, clamped);
				}
			}
		}

		private static float mixingShare = SimExtCellProperties.DefaultMixingShare;

		/// <summary>
		/// Kilograms of <paramref name="gas"/> that left liquid in a record nobody drained, since
		/// the SimDLL was initialised. Non-zero means something ran without <see cref="Install"/>
		/// or a stream hit its per-frame cap. Takes the sim's barrier.
		/// </summary>
		public static double UnreportedKg(SimHashes gas)
		{
			int lane = LaneOf(gas);
			double kg;
			if (lane < 0 || PropertyIndex < 0 || !LiquidPayload.TryGetUnreported(PropertyIndex, lane, out kg))
			{
				return 0.0;
			}
			return kg;
		}

		/// <summary>
		/// Drain the liquid-payload streams every tick and put released gas back into the world
		/// (see the class summary). Installs <see cref="LiquidPayload"/>, which installs
		/// <see cref="SimExtFrame"/>. Idempotent. Inert on a SimDLL without the property.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (installed)
			{
				return;
			}
			LiquidPayload.Install(harmony);
			LiquidPayload.Released += OnReleased;
			LiquidPayload.Consumed += OnConsumed;
			installed = true;
		}

		/// <summary>Zero the counters. For rigs.</summary>
		public static void ResetAccounting()
		{
			ReleasedKg = 0.0;
			ConsumedKg = 0.0;
			UnroutedKg = 0.0;
			FallbackReleases = 0;
			FrozenOutKg = 0.0;
		}

		private static void OnReleased(LiquidPayloadReleased r)
		{
			if (r.PropertyIdx != PropertyIndex)
			{
				return;
			}
			if (r.Reason == LiquidPayloadReleaseReason.Absorbed)
			{
				// Gas went INTO the liquid through its surface; the sim already took it out of
				// the cell above. Count it, and do not route a negative amount anywhere.
				Effervescence.NoteAbsorbed(r.Component, -r.Amount);
				return;
			}
			if (r.Reason == LiquidPayloadReleaseReason.Effervescence)
			{
				Effervescence.NoteFizzed(r.Amount);
			}
			else if (r.Reason == LiquidPayloadReleaseReason.Surface && Grid.IsValidCell(r.Cell))
			{
				// Across the surface, not as a bubble: straight into the gas above. If that cell
				// has since filled with liquid, the one release path makes it a bubble there.
				Effervescence.NoteOutgassed(r.Amount);
				ReleaseToWorld(Grid.CellAbove(r.Cell), r.Component, r.Amount, r.TemperatureK);
				return;
			}
			else if (r.Reason == LiquidPayloadReleaseReason.PhaseChange && Grid.IsValidCell(r.Cell)
				&& Grid.Solid[r.Cell])
			{
				// FROZEN. Ice has no room for dissolved gas, so freezing water pushes it out ahead
				// of the freezing front into the water that is still liquid -- which is where the
				// bubbles in an ice cube come from. If a neighbour is still liquid the gas goes
				// into it as bubbles; only a cell frozen on every side puts it straight into the
				// nearest open cell, as before.
				int into = LiquidNeighbour(r.Cell);
				if (into >= 0)
				{
					FrozenOutKg += r.Amount;
					ReleaseToWorld(into, r.Component, r.Amount, r.TemperatureK);
					return;
				}
			}
			ReleaseToWorld(r.Cell, r.Component, r.Amount, r.TemperatureK);
		}

		/// <summary>
		/// Kilograms of dissolved gas that freezing pushed out of a cell and into a neighbouring
		/// liquid as bubbles, since the SimDLL was initialised. Part of <see cref="ReleasedKg"/>,
		/// not in addition to it.
		/// </summary>
		public static double FrozenOutKg { get; private set; }

		// Above first -- the gas is buoyant and a bubble released there rises straight out --
		// then the sides, then below.
		private static int LiquidNeighbour(int cell)
		{
			int[] around = { Grid.CellAbove(cell), Grid.CellLeft(cell), Grid.CellRight(cell),
				Grid.CellBelow(cell) };
			foreach (int c in around)
			{
				if (Grid.IsValidCell(c) && Grid.IsLiquid(c))
				{
					return c;
				}
			}
			return -1;
		}

		/// <summary>
		/// Put <paramref name="kg"/> of the gas on <paramref name="lane"/> back into the world at
		/// <paramref name="cell"/>: bubbles if there is liquid for them to rise through, otherwise
		/// straight into the nearest cell that is not solid.
		///
		/// THE ONE RELEASE PATH, and it is shared on purpose. The sim's own released stream comes
		/// through here, and so does every managed carrier that loses its liquid -- a pipe
		/// deconstructed, a bottle destroyed, a parcel no building claimed
		/// (<see cref="DissolvedCargo"/>). One path means one set of rules about where gas may be
		/// put, and one counter that a rig can close its ledger against.
		///
		/// Game thread only. An unassigned lane or an invalid cell is counted in
		/// <see cref="UnroutedKg"/> rather than placed: there is no gas it could be, or nowhere
		/// for it to go.
		/// </summary>
		public static void ReleaseToWorld(int cell, int lane, float kg, float temperatureK)
		{
			if (!(kg > 0f))
			{
				return;
			}
			SimHashes gas = LaneElement(lane);
			if (gas == (SimHashes)0 || !Grid.IsValidCell(cell))
			{
				UnroutedKg += kg;
				return;
			}
			ReleasedKg += kg;
			if (Grid.IsLiquid(cell))
			{
				Vector2 at = Grid.CellToPosCCC(cell, Grid.SceneLayer.Front);
				if (Bubbles.SpawnSplit(gas, at, kg, temperatureK) > 0)
				{
					return;
				}
				FallbackReleases++;
			}
			SimMessages.AddRemoveSubstance(OpenCellNear(cell), gas,
				CellEventLogger.Instance.ExhaustSimUpdate, kg, temperatureK,
				byte.MaxValue, 0);
		}

		// The cell itself unless it is solid, then above, left, right, below. A tile that
		// replaced its water has nowhere to hold the gas, and vanilla's own bubble pop puts gas
		// into a solid cell only because it never meets one.
		private static int OpenCellNear(int cell)
		{
			if (!Grid.Solid[cell])
			{
				return cell;
			}
			int[] around = { Grid.CellAbove(cell), Grid.CellLeft(cell), Grid.CellRight(cell),
				Grid.CellBelow(cell) };
			foreach (int c in around)
			{
				if (Grid.IsValidCell(c) && !Grid.Solid[c])
				{
					return c;
				}
			}
			return cell;
		}

		private static void OnConsumed(LiquidPayloadConsumed r)
		{
			if (r.PropertyIdx != PropertyIndex || !(r.Amount > 0f))
			{
				return;
			}
			SimHashes gas = LaneElement(r.Component);
			ConsumedKg += r.Amount;
			var handler = Consumed;
			if (handler != null)
			{
				handler(r.Kind, r.Id, gas, r.Amount, r.TemperatureK);
			}
		}
	}
}
