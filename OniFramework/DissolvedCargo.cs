using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// DISSOLVED GAS THAT SURVIVES PIPES, STORAGE AND BOTTLES: the managed half of
	/// <see cref="DissolvedGas"/>.
	///
	/// <b>WHY A SIDE-TABLE AT ALL.</b> The sim carries dissolved gas in a cell property flagged
	/// <see cref="CellPropertyTransport.FollowsLiquidMass"/>, and moves it with the liquid through
	/// every liquid kernel. That carriage ends at the edge of the grid. Everything past it is
	/// managed code, and ONI's two managed carriers cannot hold a second substance:
	/// <c>ConduitFlow.ConduitContents</c> is one element, one mass, one temperature plus a disease
	/// count, and a <c>PrimaryElement</c> in a <c>Storage</c> is the same. So a pump that lifts
	/// carbonated water out of a pond has, without this class, exactly two possible behaviours --
	/// lose the gas at the pump, or refuse to model it. This is the third: a record that rides
	/// beside the mass, through the same transfers, by the same proportional rule the sim uses.
	///
	/// <b>THE RULE IS ONE LINE.</b> When mass moves, its dissolved gas moves in the same
	/// proportion; when mass merges, the payloads add. That is precisely what the sim does on the
	/// grid, and precisely what vanilla already does for disease inside <c>UpdateConduit</c>
	/// (<c>(int)(num3 / mass * diseaseCount)</c>). Nothing here invents a second physics: it
	/// extends an existing one past a boundary that only exists because of how ONI stores things.
	///
	/// <b>ABSOLUTE KILOGRAMS, NOT A CONCENTRATION</b>. A
	/// concentration would have been cheaper -- a split needs no write at all, because the
	/// concentration of what stays behind is unchanged -- but managed carriers sit OUTSIDE the
	/// native ledger that <see cref="LiquidPayload"/> closes on every tick, so this class has to
	/// keep its own. Under a concentration, "gas in pipes" is a sum of c*mass, and any mass the
	/// game drops quietly takes its gas with it and nothing can tell. Under kilograms, a carrier
	/// left holding gas at zero mass is a visible, assertable defect.
	///
	/// <b>THE STORE IS KEYED TWO WAYS, because the game has two kinds of carrier.</b>
	/// <list type="bullet">
	/// <item>A conduit tile is keyed by CELL, in a flat per-cell array read on the pipe's own
	/// update path. Liquid conduits only: a gas conduit has no liquid to dissolve anything into.
	/// </item>
	/// <item>An item -- an element chunk, a bottle, whatever a <c>Storage</c> is holding -- is
	/// keyed by its own GameObject, in <see cref="DissolvedCargoComponent"/>. Storage moves
	/// objects, so a payload attached to the object needs no transfer rule at all; only a SPLIT
	/// and an ABSORB need one, which is the same two sites vanilla patches for disease.</item>
	/// </list>
	///
	/// <b>THREADING.</b> <c>ConduitFlow.UpdateConduit</c> runs on WORKER THREADS, one
	/// <c>UpdateNetworkTask</c> per network. Two facts make the per-cell store safe there without
	/// a lock: a network owns a disjoint set of cells, so two threads never touch one cell; and a
	/// cell's lane array is allocated by a single reference store, which is atomic. What is NOT
	/// safe from a worker is Unity itself -- <c>Grid.Objects</c> and <c>GetComponent</c> -- so the
	/// serialized mirror is never written there. A worker marks the cell dirty and
	/// <see cref="FlushMirrors"/> does the component work on the game thread.
	///
	/// <b>WHERE IT LIVES, AND WHY HERE RATHER THAN IN A GAMEPLAY MOD.</b> KSerialization keys a
	/// component by type name, so a serialized component can never move between assemblies
	/// without invalidating every save that holds it. The native flag this carries is generic on
	/// purpose, so that a third-party payload gets the same ride; if the carriage lived in a
	/// gameplay mod, every other mod's payload would die at the pipe mouth. So the component
	/// lives here, and it is the one place it can ever live.
	/// </summary>
	public static class DissolvedCargo
	{
		/// <summary>Lanes per carrier: the same lane table as <see cref="DissolvedGas"/>, because
		/// a lane number is what both the save and the sim store.</summary>
		public const int Lanes = DissolvedGas.Lanes;

		/// <summary>
		/// How long a payload may sit in <see cref="pending"/> waiting for the chunk its liquid
		/// became, in seconds of real time, before it is degassed at the building instead.
		///
		/// It exists because the two halves of "a pump took your water" arrive separately: the
		/// sim's consumed record is drained during <see cref="SimExtFrame.Bound"/>, and the
		/// <c>ElementConsumer</c>'s own callback stores the mass on its own schedule. Normally the
		/// wait is a frame. A building that is destroyed, disabled or filtered between the two
		/// never produces the chunk at all, and the gas must not sit here forever: it goes back
		/// into the world, which is the one thing that is always true of matter.
		/// </summary>
		public const float PendingTimeoutSeconds = 10f;

		// ---- the conduit store -------------------------------------------------------------
		//
		// lanesByCell[cell] is null until that tile first holds something. The outer array is one
		// reference per cell and is allocated once per world; a lane array is 8 floats and is
		// allocated only for tiles that actually carry gas, which is the rare case.
		private static float[][] lanesByCell;
		private static byte[] mirrorDirty;
		private static int cellCount;

		// ---- the pending store -------------------------------------------------------------
		//
		// Payload a consumer took, waiting for the chunk its liquid turned into. Keyed by the
		// consumer's GameObject instance id; game thread only.
		private struct Pending
		{
			public GameObject Building;
			public Storage Storage;
			public SimHashes Element;
			public float[] Lanes;
			public float SinceRealtime;
		}

		private static readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
		private static readonly List<int> pendingSweep = new List<int>();

		// Scratch for CollectCarriers, reused rather than allocated: OnFrameBound runs every frame
		// and this class is not allowed to make the frame's garbage worse than the carriage it adds.
		private static readonly List<PrimaryElement> carriers = new List<PrimaryElement>();
		private static readonly List<DissolvedCargoComponent> carrierCargo =
			new List<DissolvedCargoComponent>();

		private static bool installed;

		/// <summary>Kilograms handed to a carrier by a consumer taking liquid off the grid, since
		/// load. The way gas gets in.</summary>
		public static double CarriedInKg { get; private set; }

		/// <summary>Kilograms a carrier gave back to the world as gas, since load: a pipe
		/// deconstructed, an item destroyed, a consumer that never produced a chunk, or a transfer
		/// to something this class does not model.</summary>
		public static double DegassedKg { get; private set; }

		/// <summary>Kilograms that reached a carrier and are still in one. Recomputed by
		/// <see cref="TotalCarriedKg"/>, not accumulated.</summary>
		public static double InCarriersKg { get { return TotalCarriedKg(); } }

		/// <summary>Payloads that timed out in <see cref="pending"/> rather than finding their
		/// chunk. A non-zero count is a consumer this class does not follow, not a leak: the gas
		/// is in <see cref="DegassedKg"/>.</summary>
		public static long PendingTimeouts { get; private set; }

		/// <summary>Payload records dropped because the sim named a consumer no longer in the
		/// game. Counted, degassed if its cell is known, and otherwise the one place a kilogram
		/// can be lost -- which is why it is a counter rather than a silent branch.</summary>
		public static long OrphanedConsumers { get; private set; }

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool Installed { get { return installed; } }

		// ---- the audit ---------------------------------------------------------------------

		/// <summary>
		/// What one sweep of <see cref="Audit"/> found.
		///
		/// THE NUMBER THAT MATTERS IS <see cref="OrphanCellKg"/>. Everything else is context for
		/// it.
		/// </summary>
		/// <summary>
		/// Declares that the caller is moving liquid through <c>ConduitFlow</c> ITSELF and will
		/// account for the dissolved cargo ITSELF, so the framework's own
		/// <c>ConduitFlow.RemoveElement</c>/<c>AddElement</c> patches must keep their hands off
		/// for the duration.
		///
		/// WHY THIS EXISTS. Those patches are the framework's catch-all: any mass that enters or
		/// leaves a pipe by a route this class does not otherwise model has its cargo lifted into a
		/// parcel, and <c>DissolvedCargoPatches.SettleParcel</c> hands that parcel to the
		/// <c>ConduitConsumer</c> that took the liquid -- or, finding none, RELEASES IT TO THE
		/// WORLD, which is correct for liquid disappearing into a building nobody models. It is
		/// exactly wrong for a caller that models the building itself. Such a caller is shadowed
		/// TWICE: the patch lifts share S of the tile's cargo into a parcel that is then degassed,
		/// and the caller's own <see cref="ConduitToItem"/> takes S of what is left.
		///
		/// For example, a tank that drinks from its inlet with a direct
		/// <c>liquidFlow.RemoveElement</c> and is not a <c>ConduitConsumer</c> would, without this
		/// scope, send about 15% of everything a pump lifted back to the room as gas.
		///
		/// USE IT AROUND THE WHOLE TRANSFER, not just the <c>ConduitFlow</c> call: the caller's
		/// own cargo move belongs inside the scope too, so that one transfer has exactly one
		/// shadow. Re-entrant (depth-counted) and <c>[ThreadStatic]</c>, so a conduit network's
		/// own worker thread cannot suppress another's.
		///
		/// <code>
		/// using (DissolvedCargo.OwnMove())
		/// {
		///     ConduitFlow.ConduitContents got = flow.RemoveElement(cell, kg);
		///     DissolvedCargo.ConduitToItem(cell, gameObject, got.mass / massBefore);
		/// }
		/// </code>
		/// </summary>
		public static OwnMoveScope OwnMove()
		{
			ownMoveDepth++;
			return new OwnMoveScope(true);
		}

		/// <summary>
		/// True while a <see cref="OwnMove"/> scope is open on this thread. The framework's
		/// <c>ConduitFlow</c> patches read this and do nothing.
		/// </summary>
		public static bool InOwnMove { get { return ownMoveDepth > 0; } }

		[ThreadStatic] private static int ownMoveDepth;

		/// <summary>
		/// The handle returned by <see cref="OwnMove"/>. A struct rather than a class so the
		/// <c>using</c> costs no allocation, and it carries a flag so that a
		/// <c>default(OwnMoveScope)</c> -- which never incremented the counter -- cannot
		/// decrement it on disposal.
		/// </summary>
		public struct OwnMoveScope : System.IDisposable
		{
			private bool open;

			internal OwnMoveScope(bool open)
			{
				this.open = open;
			}

			public void Dispose()
			{
				if (!open)
				{
					return;
				}
				open = false;
				if (ownMoveDepth > 0)
				{
					ownMoveDepth--;
				}
			}
		}

		/// <summary>
		/// Claims the dissolved cargo of anything a <c>Storage</c> CONSUMES inside the scope, so
		/// that the caller can follow it onto whatever the consumption produced instead of
		/// letting it go back to the room.
		///
		/// WHY THIS EXISTS. <c>Storage.ConsumeAndGetDisease</c> takes mass out of a stored chunk
		/// by assigning <c>PrimaryElement.Units</c> -- no split, no merge, no message -- and
		/// destroys the chunk when it reaches zero. That is how a fabricator eats its ingredients,
		/// how a duplicant eats, and how the Soda Fountain drinks, and left alone every one of them
		/// would delete the dissolved gas the mass was carrying: the cargo rides on a
		/// <c>DissolvedCargoComponent</c> attached to a GameObject that is about to be destroyed.
		/// The framework takes the proportional share off the chunk as it shrinks
		/// (<c>DissolvedCargoPatches</c>) and, with no claim open, RELEASES IT AS GAS at the
		/// storage's own cell -- the same answer every other unmodelled exit gets.
		///
		/// A CLAIM CHANGES WHERE IT GOES, NOT WHETHER IT SURVIVES. Inside the scope the released
		/// lanes are collected here instead; <see cref="ConsumedCargoClaim.TakeAll"/> hands them
		/// to the caller, and anything the caller does NOT take is degassed at
		/// <paramref name="fallbackCell"/> when the scope closes. There is no path on which the
		/// mass simply stops existing.
		///
		/// <code>
		/// using (DissolvedCargo.ConsumedCargoClaim claim =
		///     DissolvedCargo.ClaimConsumedCargo(cell, temperatureK))
		/// {
		///     storage.ConsumeAndGetDisease(GameTags.Water, 5f, out _, out _, out _);
		///     float[] carried = claim.TakeAll();   // now the caller's problem, and its credit
		/// }
		/// </code>
		///
		/// Re-entrant and <c>[ThreadStatic]</c>, like <see cref="OwnMove"/>. A nested claim takes
		/// precedence until it closes.
		/// </summary>
		public static ConsumedCargoClaim ClaimConsumedCargo(int fallbackCell,
			float fallbackTemperatureK)
		{
			ConsumedCargoClaim claim = new ConsumedCargoClaim(fallbackCell, fallbackTemperatureK,
				activeClaim);
			activeClaim = claim;
			return claim;
		}

		[ThreadStatic] private static ConsumedCargoClaim activeClaim;

		/// <summary>
		/// Offers consumed cargo to an open claim. Returns false when there is none, and the
		/// caller must then dispose of it some other way -- which for the patches means degassing
		/// it. Internal: the offer side belongs to the framework, the claim side to the consumer.
		/// </summary>
		internal static bool OfferConsumedCargo(float[] lanes)
		{
			ConsumedCargoClaim claim = activeClaim;
			if (claim == null || lanes == null)
			{
				return false;
			}
			claim.Collect(lanes);
			return true;
		}

		/// <summary>The handle returned by <see cref="ClaimConsumedCargo"/>.</summary>
		public sealed class ConsumedCargoClaim : System.IDisposable
		{
			private readonly int fallbackCell;
			private readonly float fallbackTemperatureK;
			private readonly ConsumedCargoClaim previous;
			private float[] lanes;
			private bool open;

			internal ConsumedCargoClaim(int fallbackCell, float fallbackTemperatureK,
				ConsumedCargoClaim previous)
			{
				this.fallbackCell = fallbackCell;
				this.fallbackTemperatureK = fallbackTemperatureK;
				this.previous = previous;
				open = true;
			}

			internal void Collect(float[] taken)
			{
				for (int lane = 0; lane < Lanes; lane++)
				{
					if (!(taken[lane] > 0f))
					{
						continue;
					}
					if (lanes == null)
					{
						lanes = new float[Lanes];
					}
					lanes[lane] += taken[lane];
				}
			}

			/// <summary>Kilograms collected so far, all lanes.</summary>
			public float Total()
			{
				return lanes == null ? 0f : LaneSum(lanes);
			}

			/// <summary>Kilograms of one gas collected so far.</summary>
			public float Gas(SimHashes gas)
			{
				int lane = DissolvedGas.LaneOf(gas);
				return lanes == null || lane < 0 ? 0f : lanes[lane];
			}

			/// <summary>
			/// Hands ONE gas's collected mass to the caller and zeroes that lane, leaving the
			/// rest of the claim to its fallback. For a consumer that can account for some of
			/// what it drank and not the rest.
			/// </summary>
			public float TakeGas(SimHashes gas)
			{
				int lane = DissolvedGas.LaneOf(gas);
				if (lanes == null || lane < 0)
				{
					return 0f;
				}
				float kg = lanes[lane];
				lanes[lane] = 0f;
				return kg;
			}

			/// <summary>
			/// Hands everything collected so far to the caller and empties the claim, so that
			/// closing the scope releases nothing. Null if nothing was collected.
			/// </summary>
			public float[] TakeAll()
			{
				float[] taken = lanes;
				lanes = null;
				return taken;
			}

			public void Dispose()
			{
				if (!open)
				{
					return;
				}
				open = false;
				activeClaim = previous;
				float[] remaining = lanes;
				lanes = null;
				if (remaining != null && LaneSum(remaining) > 0f)
				{
					// Nobody took it. It is still real, so it goes back to the world.
					Degas(remaining, fallbackCell, fallbackTemperatureK);
				}
			}
		}

		public struct AuditResult
		{
			/// <summary>Conduit tiles holding any cargo.</summary>
			public int CellsWithCargo;

			/// <summary>Tiles holding cargo with no conduit or no liquid under it.</summary>
			public int OrphanCells;

			/// <summary>Kilograms on those tiles: gas being carried by nothing.</summary>
			public double OrphanCellKg;

			/// <summary>Items holding any cargo.</summary>
			public int ItemsWithCargo;

			/// <summary>Items holding cargo with no mass left to hold it.</summary>
			public int OrphanItems;

			/// <summary>Kilograms on those items.</summary>
			public double OrphanItemKg;

			/// <summary>The highest cargo-to-liquid ratio on any tile, and where. A tile whose
			/// water left without its cargo shows up here before it shows up anywhere else.
			/// </summary>
			public float WorstConcentrationKgPerKg;

			public int WorstConcentrationCell;

			/// <summary>Conduit tiles plus items. Not pending parcels -- those have no carrier
			/// yet by definition and are <see cref="PendingKg"/>'s business.</summary>
			public double CarriedKg;

			/// <summary>Nothing is being carried by nothing.</summary>
			public bool Clean { get { return OrphanCells == 0 && OrphanItems == 0; } }
		}

		/// <summary>
		/// Run the sweep. OFF BY DEFAULT: set this, or pass <c>--e1-audit</c>.
		///
		/// WHY THIS EXISTS, in one sentence: this class has to shadow every way ONI moves liquid
		/// mass, no compiler will say when one is missed, and a missed mover -- a network levelled
		/// without its cargo, an endpoint bypassing vanilla's transfer, a destroyed pipe -- always
		/// has the SAME observable signature, which this looks for directly: CARGO SITTING WHERE
		/// THERE IS NO LIQUID TO CARRY IT.
		/// </summary>
		public static bool AuditEnabled { get; set; }

		/// <summary>Frames between sweeps while <see cref="AuditEnabled"/>. 60 is about 1 Hz.
		/// </summary>
		public static int AuditIntervalFrames { get; set; }

		/// <summary>Sweeps that found an orphan, since load.</summary>
		public static long AuditFailures { get; private set; }

		/// <summary>The most recent sweep's result, clean or not.</summary>
		public static AuditResult LastAudit { get; private set; }

		private static int auditFrames;

		private static int auditLogBudget = 16;

		/// <summary>
		/// Sweep now and return what was found, whether or not <see cref="AuditEnabled"/>. A rig
		/// calls this directly at a point where it knows what the answer should be; the periodic
		/// sweep exists to catch the mover nobody thought to write an assertion for.
		/// </summary>
		public static AuditResult Audit()
		{
			AuditResult r = new AuditResult();
			ConduitFlow flow = Game.Instance != null ? Game.Instance.liquidConduitFlow : null;
			if (lanesByCell != null)
			{
				for (int cell = 0; cell < lanesByCell.Length; cell++)
				{
					float[] lanes = lanesByCell[cell];
					if (lanes == null)
					{
						continue;
					}
					float total = 0f;
					for (int lane = 0; lane < Lanes; lane++)
					{
						total += lanes[lane];
					}
					if (!(total > 0f))
					{
						continue;
					}
					r.CellsWithCargo++;
					r.CarriedKg += total;

					float mass = 0f;
					bool hasConduit = false;
					if (flow != null && Grid.IsValidCell(cell))
					{
						hasConduit = flow.HasConduit(cell);
						if (hasConduit)
						{
							mass = flow.GetContents(cell).mass;
						}
					}
					if (!hasConduit || !(mass > 0f))
					{
						r.OrphanCells++;
						r.OrphanCellKg += total;
						if (auditLogBudget > 0)
						{
							auditLogBudget--;
							FrameworkLog.Warn("DissolvedCargo audit: cell " + cell + " holds "
								+ (total * 1000f).ToString("F3") + " g with "
								+ (hasConduit ? "an empty conduit" : "no conduit")
								+ " -- cargo carried by nothing");
						}
						continue;
					}
					float concentration = total / mass;
					if (concentration > r.WorstConcentrationKgPerKg)
					{
						r.WorstConcentrationKgPerKg = concentration;
						r.WorstConcentrationCell = cell;
					}
				}
			}
			DissolvedCargoComponent.AuditItems(ref r, ref auditLogBudget);
			LastAudit = r;
			if (!r.Clean)
			{
				AuditFailures++;
			}
			return r;
		}


		/// <summary>
		/// Start carrying. Installs <see cref="DissolvedGas"/> (hence <see cref="LiquidPayload"/>
		/// and <see cref="SimExtFrame"/>), subscribes to the consumed stream, and patches the
		/// carriers. Idempotent. Inert on a SimDLL with no dissolved-gas property: there is then
		/// nothing that can reach a carrier in the first place.
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
			DissolvedGas.Install(harmony);
			DissolvedGas.Consumed += OnConsumed;
			SimExtFrame.Bound += OnFrameBound;
			DissolvedCargoPatches.Install(harmony);
			if (AuditIntervalFrames <= 0)
			{
				AuditIntervalFrames = 60;
			}
			if (Array.IndexOf(Environment.GetCommandLineArgs(), "--e1-audit") >= 0)
			{
				AuditEnabled = true;
				FrameworkLog.Info("DissolvedCargo: --e1-audit, sweeping every "
					+ AuditIntervalFrames + " frames");
			}
			installed = true;
		}

		/// <summary>Forget every carrier and zero the counters. For a rig, and for a world
		/// change.</summary>
		public static void Reset()
		{
			lanesByCell = null;
			mirrorDirty = null;
			cellCount = 0;
			pending.Clear();
			tickets.Clear();
			CarriedInKg = 0.0;
			DegassedKg = 0.0;
			ownMoveDepth = 0;
			activeClaim = null;
			PendingTimeouts = 0;
			OrphanedConsumers = 0;
			UnclaimedTicketKg = 0.0;
			CreditWithdrawnKg = 0.0;
			ReconcileShortKg = 0.0;
			ProbeWithdrawnKg = 0.0;
			publishRequestedTick = long.MinValue;
			DissolvedCargoComponent.ResetCleanupLogBudget();
			ticketLogBudget = 40;
			auditLogBudget = 16;
			auditFrames = 0;
			AuditFailures = 0;
			LastAudit = new AuditResult();
		}

		// ---- conduit accessors -------------------------------------------------------------

		/// <summary>Kilograms of <paramref name="lane"/> in the liquid conduit at
		/// <paramref name="cell"/>.</summary>
		public static float ConduitLane(int cell, int lane)
		{
			float[] lanes = LanesAt(cell, false);
			return lanes == null || lane < 0 || lane >= Lanes ? 0f : lanes[lane];
		}

		/// <summary>Kilograms of <paramref name="gas"/> in the liquid conduit at
		/// <paramref name="cell"/>.</summary>
		public static float ConduitGas(int cell, SimHashes gas)
		{
			return ConduitLane(cell, DissolvedGas.LaneOf(gas));
		}

		/// <summary>Every lane summed, for the liquid conduit at <paramref name="cell"/>.</summary>
		public static float ConduitTotal(int cell)
		{
			float[] lanes = LanesAt(cell, false);
			if (lanes == null)
			{
				return 0f;
			}
			float total = 0f;
			for (int i = 0; i < Lanes; i++)
			{
				total += lanes[i];
			}
			return total;
		}

		/// <summary>
		/// Put <paramref name="kg"/> of <paramref name="lane"/> into the liquid conduit at
		/// <paramref name="cell"/>, or take it out with a negative amount. The caller owns the
		/// matter: this moves a record, it does not create gas.
		/// </summary>
		public static void ConduitAdd(int cell, int lane, float kg)
		{
			if (lane < 0 || lane >= Lanes || !(kg > 0f) && !(kg < 0f) || float.IsNaN(kg))
			{
				return;
			}
			float[] lanes = LanesAt(cell, true);
			if (lanes == null)
			{
				return;
			}
			lanes[lane] = Mathf.Max(0f, lanes[lane] + kg);
			MarkDirty(cell);
		}

		/// <summary>
		/// Move <paramref name="share"/> (0 to 1) of every lane at <paramref name="fromCell"/>
		/// into <paramref name="toCell"/>. THE ONE TRANSFER RULE, used by every conduit site.
		/// Returns the kilograms moved, summed over lanes.
		/// </summary>
		public static float ConduitMoveShare(int fromCell, int toCell, float share)
		{
			if (!(share > 0f) || fromCell == toCell)
			{
				return 0f;
			}
			float[] src = LanesAt(fromCell, false);
			if (src == null)
			{
				return 0f;
			}
			if (share > 1f)
			{
				share = 1f;
			}
			float[] dst = null;
			float moved = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				float take = src[lane] * share;
				if (!(take > 0f))
				{
					continue;
				}
				if (dst == null)
				{
					dst = LanesAt(toCell, true);
					if (dst == null)
					{
						return 0f;
					}
				}
				src[lane] -= take;
				dst[lane] += take;
				moved += take;
			}
			if (moved > 0f)
			{
				MarkDirty(fromCell);
				MarkDirty(toCell);
			}
			return moved;
		}

		/// <summary>
		/// Take every lane out of <paramref name="cell"/> and return it, or null if there was
		/// nothing. The caller must put it somewhere; <see cref="ConduitDegas"/> is the answer
		/// when there is nowhere.
		/// </summary>
		public static float[] ConduitTakeAll(int cell)
		{
			float[] lanes = LanesAt(cell, false);
			if (lanes == null)
			{
				return null;
			}
			float[] taken = null;
			for (int lane = 0; lane < Lanes; lane++)
			{
				if (!(lanes[lane] > 0f))
				{
					continue;
				}
				if (taken == null)
				{
					taken = new float[Lanes];
				}
				taken[lane] = lanes[lane];
				lanes[lane] = 0f;
			}
			if (taken != null)
			{
				MarkDirty(cell);
			}
			return taken;
		}

		/// <summary>
		/// Take <paramref name="share"/> of every lane out of <paramref name="cell"/> and return
		/// it, or null if there was nothing. The partial twin of <see cref="ConduitTakeAll"/>, for
		/// a caller that drained part of a tile rather than all of it -- a reservoir sipping from
		/// the pipe it is plumbed into. The caller must put what it gets somewhere.
		/// </summary>
		public static float[] ConduitTakeShare(int cell, float share)
		{
			if (!(share > 0f))
			{
				return null;
			}
			if (share >= 1f)
			{
				return ConduitTakeAll(cell);
			}
			float[] lanes = LanesAt(cell, false);
			if (lanes == null)
			{
				return null;
			}
			float[] taken = null;
			for (int lane = 0; lane < Lanes; lane++)
			{
				float take = lanes[lane] * share;
				if (!(take > 0f))
				{
					continue;
				}
				if (taken == null)
				{
					taken = new float[Lanes];
				}
				taken[lane] = take;
				lanes[lane] -= take;
			}
			if (taken != null)
			{
				MarkDirty(cell);
			}
			return taken;
		}

		/// <summary>
		/// Pool every lane held by <paramref name="cells"/> and hand each cell back an equal
		/// share.
		///
		/// FOR A NETWORK-WIDE REDISTRIBUTION THAT REWRITES TILES RATHER THAN MOVING BETWEEN THEM.
		/// <see cref="ConduitMoveShare"/> answers "this much went from here to there"; it cannot
		/// answer <c>GasMixtureFacade.EqualizeLiquidNetwork</c>, which sets every tile in a network
		/// to the same mass in one pass and so has no pairwise moves to mirror. Without this the
		/// mass is levelled and the cargo is not, and two tiles that now hold identical water hold
		/// wildly different amounts of gas -- a concentration the physics never produced.
		///
		/// Equal shares, not mass-weighted, BECAUSE THE CALLER JUST MADE THE MASSES EQUAL. A
		/// caller that levels to something other than a flat per-tile mass wants
		/// <see cref="ConduitMoveShare"/> per pair instead.
		///
		/// Returns the kilograms pooled. Game thread or the network's own worker; the cells must
		/// all belong to one network, which is what makes that safe (see the class remarks).
		/// </summary>
		public static float ConduitSpreadEven(IList<int> cells)
		{
			if (cells == null || cells.Count == 0)
			{
				return 0f;
			}
			float[] pooled = null;
			float total = 0f;
			for (int i = 0; i < cells.Count; i++)
			{
				float[] lanes = LanesAt(cells[i], false);
				if (lanes == null)
				{
					continue;
				}
				for (int lane = 0; lane < Lanes; lane++)
				{
					if (!(lanes[lane] > 0f))
					{
						continue;
					}
					if (pooled == null)
					{
						pooled = new float[Lanes];
					}
					pooled[lane] += lanes[lane];
					total += lanes[lane];
					lanes[lane] = 0f;
				}
				MarkDirty(cells[i]);
			}
			if (pooled == null)
			{
				return 0f;
			}
			float per = 1f / cells.Count;
			for (int i = 0; i < cells.Count; i++)
			{
				float[] lanes = LanesAt(cells[i], true);
				if (lanes == null)
				{
					// Nowhere to put this cell's share and no way to give it back: release it at
					// the cell rather than lose it. Only reachable on an invalid cell, which a
					// network should not contain.
					float[] orphan = new float[Lanes];
					for (int lane = 0; lane < Lanes; lane++)
					{
						orphan[lane] = pooled[lane] * per;
					}
					Degas(orphan, cells[i], GridTemperatureAt(cells[i]));
					continue;
				}
				for (int lane = 0; lane < Lanes; lane++)
				{
					if (pooled[lane] > 0f)
					{
						lanes[lane] += pooled[lane] * per;
					}
				}
				MarkDirty(cells[i]);
			}
			return total;
		}

		/// <summary>
		/// Pool every lane held by <paramref name="cells"/> onto <paramref name="intoCell"/>.
		/// The twin of <see cref="ConduitSpreadEven"/> for the branch where a caller consolidates
		/// a network's whole mass onto one tile instead of levelling it. Returns the kilograms
		/// moved.
		/// </summary>
		public static float ConduitConsolidate(IList<int> cells, int intoCell)
		{
			if (cells == null || cells.Count == 0)
			{
				return 0f;
			}
			float moved = 0f;
			for (int i = 0; i < cells.Count; i++)
			{
				if (cells[i] == intoCell)
				{
					continue;
				}
				moved += ConduitMoveShare(cells[i], intoCell, 1f);
			}
			return moved;
		}

		// The temperature to release at when a cell has to give its cargo back and there is no
		// carrier left to ask. The grid's own reading, which is what the liquid that was there
		// had.
		private static float GridTemperatureAt(int cell)
		{
			return Grid.IsValidCell(cell) ? Grid.Temperature[cell] : 293.15f;
		}

		/// <summary>
		/// Give everything at <paramref name="cell"/> back to the world as gas, through
		/// <see cref="DissolvedGas"/>'s own release path -- bubbles if there is liquid to rise
		/// through, otherwise straight into the nearest cell that is not solid. Game thread only.
		/// Returns the kilograms released.
		/// </summary>
		public static float ConduitDegas(int cell, float temperatureK)
		{
			float[] taken = ConduitTakeAll(cell);
			return Degas(taken, cell, temperatureK);
		}

		// ---- item accessors ----------------------------------------------------------------

		/// <summary>Kilograms of <paramref name="lane"/> carried by <paramref name="item"/>.
		/// </summary>
		public static float ItemLane(GameObject item, int lane)
		{
			DissolvedCargoComponent cargo = CargoOf(item, false);
			return cargo == null ? 0f : cargo.Lane(lane);
		}

		/// <summary>Kilograms of <paramref name="gas"/> carried by <paramref name="item"/>.
		/// </summary>
		public static float ItemGas(GameObject item, SimHashes gas)
		{
			return ItemLane(item, DissolvedGas.LaneOf(gas));
		}

		/// <summary>Every lane summed, for <paramref name="item"/>.</summary>
		public static float ItemTotal(GameObject item)
		{
			DissolvedCargoComponent cargo = CargoOf(item, false);
			return cargo == null ? 0f : cargo.Total();
		}

		/// <summary>
		/// Put <paramref name="kg"/> of <paramref name="lane"/> onto <paramref name="item"/>. The
		/// item keeps it through storage, hauling and a save, and gives it back when its mass
		/// leaves.
		/// </summary>
		public static void ItemAdd(GameObject item, int lane, float kg)
		{
			if (lane < 0 || lane >= Lanes || float.IsNaN(kg) || kg == 0f)
			{
				return;
			}
			// A negative amount takes gas OFF the item, and must not create a component to do it.
			DissolvedCargoComponent cargo = CargoOf(item, kg > 0f);
			if (cargo != null)
			{
				cargo.Add(lane, kg);
			}
		}

		/// <summary>
		/// Move <paramref name="share"/> of every lane from <paramref name="from"/> to
		/// <paramref name="to"/>. The item-side twin of <see cref="ConduitMoveShare"/>.
		/// </summary>
		public static float ItemMoveShare(GameObject from, GameObject to, float share)
		{
			if (!(share > 0f) || from == null || to == null || from == to)
			{
				return 0f;
			}
			DissolvedCargoComponent src = CargoOf(from, false);
			if (src == null || !(src.Total() > 0f))
			{
				return 0f;
			}
			if (share > 1f)
			{
				share = 1f;
			}
			DissolvedCargoComponent dst = null;
			float moved = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				float take = src.Lane(lane) * share;
				if (!(take > 0f))
				{
					continue;
				}
				if (dst == null)
				{
					dst = CargoOf(to, true);
					if (dst == null)
					{
						return 0f;
					}
				}
				src.Add(lane, -take);
				dst.Add(lane, take);
				moved += take;
			}
			return moved;
		}

		/// <summary>
		/// Move <paramref name="share"/> of every lane from the conduit at
		/// <paramref name="fromCell"/> onto <paramref name="to"/>. The crossing between the two
		/// halves of the store, for a building that drinks out of the pipe it is plumbed into
		/// rather than through a <c>ConduitConsumer</c> the carriage already patches. Returns the
		/// kilograms moved.
		/// </summary>
		public static float ConduitToItem(int fromCell, GameObject to, float share)
		{
			if (to == null)
			{
				return 0f;
			}
			float[] taken = ConduitTakeShare(fromCell, share);
			if (taken == null)
			{
				return 0f;
			}
			DissolvedCargoComponent dst = CargoOf(to, true);
			float moved = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				if (!(taken[lane] > 0f))
				{
					continue;
				}
				moved += taken[lane];
				if (dst != null)
				{
					dst.Add(lane, taken[lane]);
				}
			}
			if (dst == null && moved > 0f)
			{
				// The destination cannot hold cargo. Put it back rather than swallow it: the
				// liquid went there, and a carrier that cannot take the gas is a release, not a
				// deletion.
				Degas(taken, fromCell, GridTemperatureAt(fromCell));
			}
			return moved;
		}

		/// <summary>
		/// Move <paramref name="share"/> of every lane from <paramref name="from"/> onto the
		/// conduit at <paramref name="toCell"/>. The reverse crossing of
		/// <see cref="ConduitToItem"/>, for a building pushing its own liquid back into a pipe.
		/// Returns the kilograms moved.
		/// </summary>
		public static float ItemToConduit(GameObject from, int toCell, float share)
		{
			if (from == null || !(share > 0f) || !Grid.IsValidCell(toCell))
			{
				return 0f;
			}
			DissolvedCargoComponent src = CargoOf(from, false);
			if (src == null || !(src.Total() > 0f))
			{
				return 0f;
			}
			if (share > 1f)
			{
				share = 1f;
			}
			float moved = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				float take = src.Lane(lane) * share;
				if (!(take > 0f))
				{
					continue;
				}
				src.Add(lane, -take);
				ConduitAdd(toCell, lane, take);
				moved += take;
			}
			return moved;
		}

		/// <summary>Everything <paramref name="item"/> carries, taken off it, or null.</summary>
		public static float[] ItemTakeAll(GameObject item)
		{
			DissolvedCargoComponent cargo = CargoOf(item, false);
			return cargo == null ? null : cargo.TakeAll();
		}

		/// <summary>
		/// Give everything <paramref name="item"/> carries back to the world as gas, at the item's
		/// own cell. What a destroyed or consumed item does with its payload.
		/// </summary>
		public static float ItemDegas(GameObject item, float temperatureK)
		{
			if (item == null)
			{
				return 0f;
			}
			float[] taken = ItemTakeAll(item);
			if (taken == null)
			{
				return 0f;
			}
			int cell = Grid.PosToCell(item.transform.GetPosition());
			return Degas(taken, cell, temperatureK);
		}

		// ---- taking liquid off the grid, for a carrier the sim cannot name -------------------
		//
		// THE GAP THIS CLOSES. Everything above assumes the payload ARRIVED -- that some consumer
		// took liquid off the grid and the sim told us, naming it. The sim names exactly two
		// kinds (`LiquidPayloadConsumerKind`): an `ElementConsumer` by its sim handle, and a
		// `MassConsumption` message by its callback index. A building that takes liquid by
		// writing the cell directly -- `kRemoveVanillaMass`, as a mod's own pump intake may -- is
		// NEITHER, so its dissolved gas would be left behind on a cell that had just lost the
		// water holding it. A mod that replaces vanilla's conduit endpoints with components that
		// call `ConduitFlow` and the cell arrays directly bypasses the vanilla endpoints this
		// class patches.
		//
		// THE ANSWER IS VANILLA'S OWN MESSAGE, NOT A NEW ONE. `SimMessages.ConsumeMass` already
		// removes a named element from a cell and already carries a callback index, and
		// `ApplyMassConsumption` already routes the cell's payload to that index
		// (`PayloadBeginConsume`/`PayloadEndConsume`, kPayloadConsumerMassConsumption). So there
		// is no new ABI here and no second removal path: the caller sends the message the game
		// already has, and leaves a TICKET saying which carrier the payload belongs to. The
		// removal and the payload split are then ONE atomic thing inside the sim, which a
		// managed-side "read the lanes, subtract them, then remove the mass" could never be --
		// those are two queued messages and anything between them sees a concentration that
		// never existed.
		//
		// THE TICKET'S LIFETIME IS ONE FRAME. `ApplyMassConsumption` pushes the payload record
		// and the mass-consumed callback into the SAME published frame, and the payload stream is
		// drained in `SimExtFrame`'s `PrepareGameData` postfix, which runs before `Game.Update`
		// reads the callbacks. So the payload is routed first and the ticket is dropped there;
		// the callback drops it too, for the case where the consume found no dissolved gas at all
		// and so produced no record. Either way it is gone before vanilla can hand the same
		// callback index to somebody else.
		private struct Ticket
		{
			public int ConduitCell;
			public GameObject Item;
			// What the carrier was credited at SEND time, per lane, and which lanes the sim has since answered for. Null when the
			// send credited nothing. See `Consume`.
			public float[] Credited;
			public bool[] Settled;
			// Opened by ProbeUnclaimedCredit, so its settlement is counted apart from the
			// ordinary traffic a running pump puts through the same counters.
			public bool Probe;
		}

		private static readonly Dictionary<int, Ticket> tickets = new Dictionary<int, Ticket>();

		private static Action<global::Sim.MassConsumedCallback, object> ticketCallback;

		// Budgeted the same way the carrier-cleanup line is, and for the same reason: the ticket
		// round trip is the one link in this class with no other witness. A ticket that is opened and
		// never claimed looks, from every counter, exactly like a consume that took no payload.
		private static int ticketLogBudget = 40;

		/// <summary>Tickets outstanding right now: a consume was sent and its payload has not come
		/// back yet. Normally 0 or a handful; a number that only grows is a frame that stopped
		/// being published. For rigs.</summary>
		public static int OpenTickets { get { return tickets.Count; } }

		/// <summary>Kilograms a ticket could not be honoured for, because the carrier it named was
		/// gone by the time the payload arrived. Released, not lost.</summary>
		public static double UnclaimedTicketKg { get; private set; }

		/// <summary>
		/// Kilograms carriers have been CREDITED at send time and the sim has not yet answered
		/// for. For that interval the same gas is counted twice -- on the carrier and still in the
		/// grid cell the consume has not drained yet -- so a world ledger that sums both subtracts
		/// this. Normally one tick of intake; zero whenever no ticket is open.
		/// </summary>
		public static double OutstandingCreditKg
		{
			get
			{
				double total = 0.0;
				foreach (KeyValuePair<int, Ticket> kv in tickets)
				{
					Ticket t = kv.Value;
					if (t.Credited == null)
					{
						continue;
					}
					for (int lane = 0; lane < Lanes; lane++)
					{
						if (!t.Settled[lane])
						{
							total += t.Credited[lane];
						}
					}
				}
				return total;
			}
		}

		/// <summary>Kilograms credited at send time and later taken back, because the sim's
		/// answer for that lane was smaller, or never came (an unclaimed ticket), or a save
		/// needed the grid and the carriers to agree. Gas that never existed twice.</summary>
		public static double CreditWithdrawnKg { get; private set; }

		/// <summary>
		/// Kilograms a reconciliation needed to take back off a carrier and could NOT, because
		/// the carrier no longer held them -- the credited gas had already flowed on down the pipe,
		/// or the carrier was destroyed and degassed it. Every gram here is gas counted twice, so
		/// this is the one number that must stay near zero. Negative deltas are small (an estimate
		/// one tick stale against the sim's own answer), so a large value is a defect.
		/// </summary>
		public static double ReconcileShortKg { get; private set; }

		/// <summary>Kilograms withdrawn from tickets opened by <see cref="ProbeUnclaimedCredit"/>,
		/// and the probe tickets still open. A rig's control reads these rather than the global
		/// counters, which a running pump moves too.</summary>
		public static double ProbeWithdrawnKg { get; private set; }

		/// <summary>See <see cref="ProbeWithdrawnKg"/>.</summary>
		public static int OpenProbeTickets
		{
			get
			{
				int n = 0;
				foreach (KeyValuePair<int, Ticket> kv in tickets)
				{
					if (kv.Value.Probe)
					{
						n++;
					}
				}
				return n;
			}
		}

		/// <summary>
		/// Take <paramref name="kg"/> of <paramref name="element"/> out of GRID cell
		/// <paramref name="fromCell"/> and put the dissolved gas that came with it onto the liquid
		/// conduit at <paramref name="toCell"/>.
		///
		/// For a building that moves liquid from the world into a pipe by itself, with no
		/// <c>ElementConsumer</c> and no <c>ConduitDispenser</c> -- the case this class's two patch
		/// families cannot see. The removal is vanilla's own <c>SimMessages.ConsumeMass</c> over a
		/// single cell, so the mass leaves exactly the way vanilla's own consumers make it leave;
		/// this adds only the ticket that says where the gas goes.
		///
		/// Queued, like every sim message: the mass leaves on the next tick and the gas lands on
		/// the conduit when that tick is published. Returns false, sending nothing, if the
		/// carriage is not installed or the cells are not valid -- a caller that gets false and
		/// still wants the mass gone must remove it itself, and its dissolved gas will be released
		/// by the sim rather than carried.
		///
		/// Game thread only.
		/// </summary>
		public static bool ConsumeToConduit(int fromCell, SimHashes element, float kg, int toCell)
		{
			if (!Grid.IsValidCell(toCell))
			{
				return false;
			}
			return Consume(fromCell, element, kg,
				new Ticket { ConduitCell = toCell, Item = null });
		}

		/// <summary>
		/// <see cref="ConsumeToConduit"/> for an item carrier: the gas lands on
		/// <paramref name="to"/>'s own <see cref="DissolvedCargoComponent"/> instead of on a
		/// conduit tile. For a building that lifts liquid off the grid into a store of its own
		/// rather than into a pipe.
		/// </summary>
		public static bool ConsumeToItem(int fromCell, SimHashes element, float kg, GameObject to)
		{
			if (to == null)
			{
				return false;
			}
			return Consume(fromCell, element, kg, new Ticket { ConduitCell = -1, Item = to });
		}

		private static bool Consume(int fromCell, SimHashes element, float kg, Ticket ticket)
		{
			if (!installed || !(kg > 0f) || float.IsNaN(kg) || !Grid.IsValidCell(fromCell)
				|| Game.Instance == null)
			{
				return false;
			}
			if (ticketCallback == null)
			{
				ticketCallback = OnMassConsumed;
			}
			var handle = Game.Instance.massConsumedCallbackManager.Add(ticketCallback, null,
				"DissolvedCargo");
			// CREDIT NOW, RECONCILE LATER. The water this
			// consume removes reaches its carrier synchronously -- the caller pushes it through
			// `ConduitFlow.AddElement` in the same call -- while the gas it holds comes back on the
			// sim's payload record a tick or two later. A carrier that waits for the record makes
			// every destination filled from the head of a column receive its first kilograms lean
			// and keep them that way (a Soda Fountain reads about 4.9 g/kg against 6.0 delivered).
			//
			// So the carrier is credited here with what the source cell holds in the share of its
			// mass this consume takes, and the record, when it lands, applies only the DIFFERENCE.
			// The sim stays the authority on how much gas actually left the cell. The hazard is
			// that a credit nothing ever settles is matter creation, so every path that ends a ticket settles its credit: a lane's
			// record reconciles it, the mass-consumed callback withdraws whatever no record
			// answered for, and a save withdraws every open credit before anything is written.
			ticket.Credited = CreditAtSend(fromCell, element, kg, ticket);
			ticket.Settled = ticket.Credited != null ? new bool[Lanes] : null;
			tickets[handle.index] = ticket;
			if (ticketLogBudget > 0)
			{
				ticketLogBudget--;
				FrameworkLog.Info("DissolvedCargo: ticket " + handle.index + " opened for "
					+ (kg * 1000f).ToString("F3") + " g of " + element + " at cell " + fromCell
					+ " -> " + (ticket.Item != null ? "item " + ticket.Item.name
						: "conduit cell " + ticket.ConduitCell));
			}
			// RADIUS 1, NOT 0, AND THE DIFFERENCE IS EVERYTHING. `ApplyMassConsumption` takes the
			// flood branch whenever height is 0, and this line first shipped saying "a flood of
			// depth 0 visits the origin alone". IT VISITS NOTHING. `Flood` (sim/emitters.h)
			// enqueues the origin at depth 0 and then tests `if (n.depth >= max_depth) continue;`
			// -- so at max_depth 0 the origin is skipped before it is ever read, the drain removes
			// nothing, and no payload record is produced. Measured (run 10:51): 42
			// tickets opened, not one claimed, while the pipe filled with water the grid never
			// lost. That is MATTER CREATION, and it was live on the ordinary-play path.
			//
			// max_depth 1 admits the origin (0 >= 1 is false) and rejects its neighbours at depth
			// 1, which is exactly the single cell this wants. It is also vanilla's own convention
			// for a one-cell consume -- `Moppable` (one tile), `SteamTurbine` and `Turbine` (their
			// own cell), `BeeForageStates` (one cell) all pass 1, and no vanilla caller passes 0.
			SimMessages.ConsumeMass(fromCell, element, kg, 1, handle.index);
			return true;
		}

		// Vanilla releases the handle immediately before calling this, so the index is already
		// free to be handed out again -- which is exactly why the ticket has to go now.
		private static void OnMassConsumed(global::Sim.MassConsumedCallback cb, object data)
		{
			Ticket ticket;
			if (tickets.TryGetValue(cb.callbackIdx, out ticket) && ticket.Credited != null)
			{
				// THE UNCLAIMED-TICKET CONTROL. Every payload record for this consume was routed
				// earlier in this same frame (the stream drains in `PrepareGameData`, before
				// `Game.Update` reads the callbacks), so a lane still unsettled here is a lane the
				// sim took NO gas from -- the cell held none, held a different liquid, or the
				// consume found nothing. Its credit was never real and comes back off the carrier.
				for (int lane = 0; lane < Lanes; lane++)
				{
					if (!ticket.Settled[lane] && ticket.Credited[lane] > 0f)
					{
						float taken = Withdraw(ticket, lane, ticket.Credited[lane]);
						if (ticket.Probe)
						{
							ProbeWithdrawnKg += taken;
						}
						ticket.Credited[lane] = 0f;
						ticket.Settled[lane] = true;
					}
				}
			}
			tickets.Remove(cb.callbackIdx);
		}

		// The source cell's dissolved gas in the share of its mass `kg` is, per lane; null if
		// that is nothing. An ESTIMATE: it is read from the frame the game is holding, one tick
		// before the consume lands, and the sim's own record corrects it.
		private static float[] CreditAtSend(int fromCell, SimHashes element, float kg, Ticket ticket)
		{
			if (!Grid.IsValidCell(fromCell) || !Grid.IsLiquid(fromCell))
			{
				return null;
			}
			Element e = Grid.Element[fromCell];
			float mass = Grid.Mass[fromCell];
			if (e == null || e.id != element || !(mass > 0f))
			{
				return null;
			}
			float share = Mathf.Min(kg / mass, 1f);
			float[] credit = null;
			for (int lane = 0; lane < Lanes; lane++)
			{
				float have = SourceLane(fromCell, lane);
				if (!(have > 0f))
				{
					continue;
				}
				if (credit == null)
				{
					credit = new float[Lanes];
				}
				credit[lane] = have * share;
			}
			if (credit == null)
			{
				return null;
			}
			for (int lane = 0; lane < Lanes; lane++)
			{
				if (credit[lane] > 0f)
				{
					CarriedInKg += credit[lane];
					Deposit(ticket, lane, credit[lane]);
				}
			}
			return credit;
		}

		// One lane of a grid cell's dissolved gas, from the frame the sim published this tick
		// when it can be, and through the barrier otherwise. A pump calls `Consume` every tick,
		// and a worker barrier per lane per pump per tick would stall the game thread on the
		// ordinary-play path; the published copy costs a pointer read. It is requested here the
		// first time it is missing and re-requested at most once a tick until it appears -- the
		// request is queued, and a world reload clears it.
		private static long publishRequestedTick = long.MinValue;

		private static float SourceLane(int cell, int lane)
		{
			SimExtFrame.PublishedProperty published;
			float value;
			if (SimExtFrame.TryGetProperty(DissolvedGas.PropertyName, out published)
				&& published.IsCurrent
				&& published.TryReadFloat(SimExtFrame.PaddedCell(cell), out value, lane))
			{
				return value;
			}
			if (DissolvedGas.Available && SimExtFrame.TickId != publishRequestedTick)
			{
				publishRequestedTick = SimExtFrame.TickId;
				SimExtCellProperties.Publish(DissolvedGas.PropertyIndex, true);
			}
			return DissolvedGas.ReadLane(cell, lane);
		}

		// Put `kg` of `lane` on the ticket's carrier. A carrier that is already gone has nothing
		// to credit, so the credit is skipped -- the record, when it comes, then finds the carrier
		// gone too and releases the real amount through the ordinary unclaimed path.
		private static void Deposit(Ticket ticket, int lane, float kg)
		{
			if (ticket.Item != null)
			{
				ItemAdd(ticket.Item, lane, kg);
				return;
			}
			if (ticket.ConduitCell >= 0 && Grid.IsValidCell(ticket.ConduitCell))
			{
				ConduitAdd(ticket.ConduitCell, lane, kg);
			}
		}

		// Take `kg` of `lane` back off the ticket's carrier, as much as it still holds; what it
		// does not hold is counted in ReconcileShortKg.
		private static float Withdraw(Ticket ticket, int lane, float kg)
		{
			if (!(kg > 0f))
			{
				return 0f;
			}
			float have = 0f;
			bool item = ticket.Item != null;
			bool conduit = !item && ticket.ConduitCell >= 0 && Grid.IsValidCell(ticket.ConduitCell);
			if (item)
			{
				have = ItemLane(ticket.Item, lane);
			}
			else if (conduit)
			{
				have = ConduitLane(ticket.ConduitCell, lane);
			}
			float take = Mathf.Min(have, kg);
			if (take > 0f)
			{
				if (item)
				{
					ItemAdd(ticket.Item, lane, -take);
				}
				else
				{
					ConduitAdd(ticket.ConduitCell, lane, -take);
				}
			}
			CarriedInKg -= take;
			CreditWithdrawnKg += take;
			if (kg - take > 0f)
			{
				ReconcileShortKg += kg - take;
			}
			return take;
		}

		/// <summary>
		/// THE UNCLAIMED-TICKET POSITIVE CONTROL, for rigs. Opens a ticket exactly as
		/// <see cref="ConsumeToConduit"/> does -- the conduit at <paramref name="toCell"/> is
		/// credited at once with the gas <paramref name="kg"/> of <paramref name="fromCell"/>'s
		/// liquid holds -- but sends the consume for an element the cell cannot hold
		/// (<c>Unobtanium</c>, a solid). The sim removes nothing, publishes no payload record, and
		/// answers with the mass-consumed callback alone. A carriage that settles its credits
		/// correctly takes the whole credit back off the conduit when that callback arrives
		/// (<see cref="CreditWithdrawnKg"/> rises by it, <see cref="ReconcileShortKg"/> does not);
		/// one that does not has created that much gas. Returns the kilograms credited, 0 if
		/// nothing could be.
		/// </summary>
		public static float ProbeUnclaimedCredit(int fromCell, float kg, int toCell)
		{
			if (!installed || !(kg > 0f) || !Grid.IsValidCell(fromCell) || !Grid.IsValidCell(toCell)
				|| !Grid.IsLiquid(fromCell) || Game.Instance == null)
			{
				return 0f;
			}
			Element e = Grid.Element[fromCell];
			if (e == null)
			{
				return 0f;
			}
			if (ticketCallback == null)
			{
				ticketCallback = OnMassConsumed;
			}
			var handle = Game.Instance.massConsumedCallbackManager.Add(ticketCallback, null,
				"DissolvedCargo.ProbeUnclaimedCredit");
			var ticket = new Ticket { ConduitCell = toCell, Item = null, Probe = true };
			ticket.Credited = CreditAtSend(fromCell, e.id, kg, ticket);
			ticket.Settled = ticket.Credited != null ? new bool[Lanes] : null;
			tickets[handle.index] = ticket;
			SimMessages.ConsumeMass(fromCell, SimHashes.Unobtanium, kg, 1, handle.index);
			return ticket.Credited != null ? LaneSum(ticket.Credited) : 0f;
		}

		/// <summary>
		/// Take every open ticket's unsettled credit back off its carrier. Called before a save
		/// is written: the consumes behind those tickets are still queued, so the grid being saved
		/// STILL HOLDS that gas, and a carrier saved holding it too would load with it twice. The
		/// game carries on afterwards, the consumes land, and their records -- now reconciling
		/// against a credit of zero -- add the full real amount, exactly as before Option 1.
		/// </summary>
		internal static void WithdrawOpenCredits()
		{
			foreach (KeyValuePair<int, Ticket> kv in tickets)
			{
				Ticket t = kv.Value;
				if (t.Credited == null)
				{
					continue;
				}
				for (int lane = 0; lane < Lanes; lane++)
				{
					if (!t.Settled[lane] && t.Credited[lane] > 0f)
					{
						Withdraw(t, lane, t.Credited[lane]);
						t.Credited[lane] = 0f;
					}
				}
			}
		}

		// A payload record naming a `MassConsumption` callback index. Returns true when a ticket
		// claimed it.
		private static bool RouteTicket(int id, int lane, float kg, float temperatureK)
		{
			Ticket ticket;
			if (!tickets.TryGetValue(id, out ticket))
			{
				if (ticketLogBudget > 0)
				{
					ticketLogBudget--;
					FrameworkLog.Info("DissolvedCargo: payload record for callback " + id
						+ " carrying " + (kg * 1000f).ToString("F3")
						+ " g matched NO open ticket (" + tickets.Count + " open)");
				}
				return false;
			}
			if (ticketLogBudget > 0)
			{
				ticketLogBudget--;
				FrameworkLog.Info("DissolvedCargo: ticket " + id + " claimed "
					+ (kg * 1000f).ToString("F3") + " g on lane " + lane + " -> "
					+ (ticket.Item != null ? "item " + ticket.Item.name
						: "conduit cell " + ticket.ConduitCell));
			}
			// NOT REMOVED HERE. One consume produces ONE RECORD PER LANE, so a ticket has to
			// survive its own first record; the mass-consumed callback, which fires later in this
			// same frame and exactly once, is what drops it.
			if (ticket.Credited != null && !ticket.Settled[lane])
			{
				// RECONCILE. The carrier already holds `Credited[lane]` from the send; the sim
				// says `kg` actually left the cell. Only the difference moves.
				float credited = ticket.Credited[lane];
				ticket.Settled[lane] = true;
				if (kg < credited)
				{
					Withdraw(ticket, lane, credited - kg);
					return true;
				}
				kg -= credited;
				if (!(kg > 0f))
				{
					return true;
				}
			}
			if (ticket.Item != null)
			{
				CarriedInKg += kg;
				ItemAdd(ticket.Item, lane, kg);
				return true;
			}
			if (ticket.ConduitCell >= 0 && Grid.IsValidCell(ticket.ConduitCell))
			{
				CarriedInKg += kg;
				ConduitAdd(ticket.ConduitCell, lane, kg);
				return true;
			}
			// The carrier the ticket named is gone. The matter still has to go somewhere.
			UnclaimedTicketKg += kg;
			float[] one = new float[Lanes];
			one[lane] = kg;
			Degas(one, ticket.ConduitCell, temperatureK);
			return true;
		}

		// ---- totals and bookkeeping --------------------------------------------------------

		/// <summary>
		/// Kilograms in every carrier this class knows about: every conduit tile, plus every item
		/// that has a live <see cref="DissolvedCargoComponent"/>. Walks the world, so it is a
		/// probe and a rig assertion, not something to call per frame.
		/// </summary>
		public static double TotalCarriedKg()
		{
			double total = 0.0;
			if (lanesByCell != null)
			{
				for (int cell = 0; cell < lanesByCell.Length; cell++)
				{
					float[] lanes = lanesByCell[cell];
					if (lanes == null)
					{
						continue;
					}
					for (int lane = 0; lane < Lanes; lane++)
					{
						total += lanes[lane];
					}
				}
			}
			total += DissolvedCargoComponent.TotalOnItemsKg();
			foreach (KeyValuePair<int, Pending> entry in pending)
			{
				float[] lanes = entry.Value.Lanes;
				for (int lane = 0; lane < Lanes; lane++)
				{
					total += lanes[lane];
				}
			}
			return total;
		}

		/// <summary>Kilograms waiting for the chunk their liquid became. Part of
		/// <see cref="TotalCarriedKg"/>, broken out so a rig can tell "in transit" from
		/// "arrived".</summary>
		public static double PendingKg()
		{
			double total = 0.0;
			foreach (KeyValuePair<int, Pending> entry in pending)
			{
				float[] lanes = entry.Value.Lanes;
				for (int lane = 0; lane < Lanes; lane++)
				{
					total += lanes[lane];
				}
			}
			return total;
		}

		// ---- internals ---------------------------------------------------------------------

		private static float[] LanesAt(int cell, bool create)
		{
			float[][] table = lanesByCell;
			if (table == null || cellCount != Grid.CellCount)
			{
				if (!create)
				{
					return table != null && cellCount == Grid.CellCount && Grid.IsValidCell(cell)
						? table[cell] : null;
				}
				table = Allocate();
				if (table == null)
				{
					return null;
				}
			}
			if (!Grid.IsValidCell(cell))
			{
				return null;
			}
			float[] lanes = table[cell];
			if (lanes == null && create)
			{
				// A single reference store, so a worker thread owning this cell's network cannot
				// race another worker: networks are disjoint, and no two own one cell.
				lanes = new float[Lanes];
				table[cell] = lanes;
			}
			return lanes;
		}

		private static float[][] Allocate()
		{
			int count = Grid.CellCount;
			if (count <= 0)
			{
				return null;
			}
			// A world change resizes the grid; anything held for the old one is not addressable
			// in the new one, so it is dropped rather than carried across.
			lanesByCell = new float[count][];
			mirrorDirty = new byte[count];
			cellCount = count;
			return lanesByCell;
		}

		private static void MarkDirty(int cell)
		{
			byte[] dirty = mirrorDirty;
			if (dirty != null && cell >= 0 && cell < dirty.Length)
			{
				dirty[cell] = 1;
			}
		}

		/// <summary>
		/// Write every changed conduit tile through to its serialized mirror. GAME THREAD ONLY --
		/// it reads <c>Grid.Objects</c> and calls <c>GetComponent</c>, neither of which a worker
		/// may touch, which is the whole reason the dirty flag exists.
		/// </summary>
		internal static void FlushMirrors()
		{
			byte[] dirty = mirrorDirty;
			float[][] table = lanesByCell;
			if (dirty == null || table == null)
			{
				return;
			}
			for (int cell = 0; cell < dirty.Length; cell++)
			{
				if (dirty[cell] == 0)
				{
					continue;
				}
				dirty[cell] = 0;
				DissolvedCargoComponent cargo = ConduitCargoAt(cell, false);
				if (cargo != null)
				{
					cargo.Mirror(table[cell]);
				}
			}
		}

		/// <summary>The mirror component on the liquid conduit at <paramref name="cell"/>, if the
		/// tile has a conduit building at all.</summary>
		internal static DissolvedCargoComponent ConduitCargoAt(int cell, bool create)
		{
			if (!Grid.IsValidCell(cell))
			{
				return null;
			}
			GameObject go = Grid.Objects[cell, (int)ObjectLayer.LiquidConduit];
			return go == null ? null : CargoOf(go, create);
		}

		private static DissolvedCargoComponent CargoOf(GameObject go, bool create)
		{
			if (go == null)
			{
				return null;
			}
			DissolvedCargoComponent cargo = go.GetComponent<DissolvedCargoComponent>();
			if (cargo == null && create)
			{
				// Only reached for an object whose prefab the attachment pass did not cover --
				// something another mod spawns, say. Adding it here still serializes, because
				// KSerialization walks the live components of the object it is saving.
				cargo = go.AddComponent<DissolvedCargoComponent>();
			}
			return cargo;
		}

		/// <summary>
		/// Give <paramref name="lanes"/> back to the world as gas at <paramref name="cell"/>, and
		/// count it. THE ONE EXIT for a payload with no carrier left, whichever site found it:
		/// an unclaimed parcel, a timed-out consumer, a deconstructed pipe, a destroyed item.
		/// The array is emptied.
		/// </summary>
		internal static float ReleaseLanes(float[] lanes, int cell, float temperatureK)
		{
			return Degas(lanes, cell, temperatureK);
		}

		/// <summary>Kilograms across every lane of a lane array. Null reads as zero.</summary>
		internal static float LaneSum(float[] lanes)
		{
			if (lanes == null)
			{
				return 0f;
			}
			float total = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				total += lanes[lane];
			}
			return total;
		}

		private static float Degas(float[] lanes, int cell, float temperatureK)
		{
			if (lanes == null)
			{
				return 0f;
			}
			float released = 0f;
			for (int lane = 0; lane < Lanes; lane++)
			{
				float kg = lanes[lane];
				if (!(kg > 0f))
				{
					continue;
				}
				lanes[lane] = 0f;
				DissolvedGas.ReleaseToWorld(cell, lane, kg, temperatureK);
				released += kg;
			}
			DegassedKg += released;
			return released;
		}

		/// <summary>
		/// The sim says a consumer took liquid off the grid and this much dissolved gas went with
		/// it (the record NAMES the consumer). Park it until the chunk its liquid became exists, then attach it there.
		/// </summary>
		private static void OnConsumed(LiquidPayloadConsumerKind kind, int id, SimHashes gas,
			float kg, float temperatureK)
		{
			int lane = DissolvedGas.LaneOf(gas);
			if (lane < 0 || !(kg > 0f))
			{
				return;
			}
			if (kind == LiquidPayloadConsumerKind.MassConsumption
				&& RouteTicket(id, lane, kg, temperatureK))
			{
				return;
			}
			ElementConsumer consumer = kind == LiquidPayloadConsumerKind.ElementConsumer
				? ElementConsumerLookup.ByHandle(id) : null;
			if (consumer == null || consumer.storage == null)
			{
				// Either a MassConsumption record whose callback index no ticket claimed -- the
				// game's own consumers send that message too, and they have no dissolved-gas
				// carrier to offer -- or an ElementConsumer the game has since destroyed. Neither
				// has a carrier to hand it to; the matter goes back to the world if the sim told
				// us where, and is counted either way.
				OrphanedConsumers++;
				float[] one = new float[Lanes];
				one[lane] = kg;
				Degas(one, consumer != null ? Grid.PosToCell(consumer.transform.GetPosition()) : -1,
					temperatureK);
				return;
			}

			CarriedInKg += kg;
			int key = consumer.gameObject.GetInstanceID();
			Pending p;
			if (!pending.TryGetValue(key, out p))
			{
				p = new Pending
				{
					Building = consumer.gameObject,
					Storage = consumer.storage,
					Element = consumer.elementToConsume,
					Lanes = new float[Lanes],
				};
			}
			p.Lanes[lane] += kg;
			p.SinceRealtime = Time.realtimeSinceStartup;
			pending[key] = p;
		}

		/// <summary>
		/// Once a frame, on the game thread: hand parked payloads to the chunks their liquid
		/// became, and give up on the ones that have waited too long.
		/// </summary>
		private static void OnFrameBound()
		{
			// BEFORE THE EARLY-OUT BELOW, which returns whenever nothing is parked -- and a world
			// that is quietly orphaning cargo is exactly a world with nothing parked.
			if (AuditEnabled)
			{
				auditFrames++;
				if (auditFrames >= (AuditIntervalFrames > 0 ? AuditIntervalFrames : 60))
				{
					auditFrames = 0;
					Audit();
				}
			}
			if (pending.Count == 0)
			{
				return;
			}
			pendingSweep.Clear();
			foreach (KeyValuePair<int, Pending> entry in pending)
			{
				pendingSweep.Add(entry.Key);
			}
			float now = Time.realtimeSinceStartup;
			for (int i = 0; i < pendingSweep.Count; i++)
			{
				Pending p;
				if (!pending.TryGetValue(pendingSweep[i], out p))
				{
					continue;
				}
				float carried = CollectCarriers(p);
				if (carried > 0f)
				{
					// Resolve every carrier's component BEFORE handing anything over, so a chunk
					// that cannot take cargo does not silently swallow its share of the payload:
					// the share is over the mass that can actually hold it, not over all of it.
					carrierCargo.Clear();
					float assignable = 0f;
					for (int c = 0; c < carriers.Count; c++)
					{
						DissolvedCargoComponent cargo = CargoOf(carriers[c].gameObject, true);
						carrierCargo.Add(cargo);
						if (cargo != null)
						{
							assignable += carriers[c].Mass;
						}
					}
					if (assignable > 0f)
					{
						for (int c = 0; c < carriers.Count; c++)
						{
							if (carrierCargo[c] == null)
							{
								continue;
							}
							float share = carriers[c].Mass / assignable;
							for (int lane = 0; lane < Lanes; lane++)
							{
								if (p.Lanes[lane] > 0f)
								{
									carrierCargo[c].Add(lane, p.Lanes[lane] * share);
								}
							}
						}
						pending.Remove(pendingSweep[i]);
						continue;
					}
				}
				if (now - p.SinceRealtime < PendingTimeoutSeconds && p.Building != null)
				{
					continue;
				}
				PendingTimeouts++;
				int cell = p.Building != null
					? Grid.PosToCell(p.Building.transform.GetPosition()) : -1;
				Degas(p.Lanes, cell, GetTemperatureNear(p, cell));
				pending.Remove(pendingSweep[i]);
			}
		}

		/// <summary>
		/// The chunks in a parked payload's storage that should carry it, into
		/// <c>carriers</c>; returns their total mass, or 0 if there are none.
		/// </summary>
		/// <remarks>
		/// THIS USED TO ASK FOR ONE CHUNK BY ELEMENT AND THAT WAS WRONG. <c>Pending.Element</c> is
		/// the consumer's own <c>elementToConsume</c>, and for a pump that field is
		/// <c>SimHashes.Vacuum</c> -- vanilla's "any element" SENTINEL, not an element.
		/// <c>LiquidPumpConfig.DoPostConfigureComplete</c> sets
		/// <c>configuration = Configuration.AllLiquid</c> and never assigns
		/// <c>elementToConsume</c>, so it keeps the field's declared default, and
		/// <c>ElementConsumer.AddMassInternal</c> reads the sentinel as "store whatever the sim
		/// removed": <c>if (elementToConsume == SimHashes.Vacuum || elementToConsume == element.id)</c>.
		/// Asking <c>Storage.FindPrimaryElement(Vacuum)</c> for that consumer matches nothing, so
		/// every payload it parked would wait out <see cref="PendingTimeoutSeconds"/> and be
		/// degassed: the intake ledger balances to the gram while the pipe carries no gas at all.
		/// A ledger that counts at the door says nothing about what arrived.
		///
		/// So: the named element when the consumer names one, otherwise every liquid chunk the
		/// storage holds, with the payload split across them by mass -- the same proportional share
		/// every other hand-off in this class uses, and the right answer for a pump that consumed
		/// two different liquids, which an AllLiquid consumer beside two ponds will.
		/// </remarks>
		private static float CollectCarriers(Pending p)
		{
			carriers.Clear();
			if (p.Storage == null)
			{
				return 0f;
			}
			if (p.Element != SimHashes.Vacuum)
			{
				PrimaryElement one = p.Storage.FindPrimaryElement(p.Element);
				if (one == null || !(one.Mass > 0f))
				{
					return 0f;
				}
				carriers.Add(one);
				return one.Mass;
			}
			float total = 0f;
			List<GameObject> items = p.Storage.items;
			for (int i = 0; i < items.Count; i++)
			{
				if (items[i] == null)
				{
					continue;
				}
				PrimaryElement e = items[i].GetComponent<PrimaryElement>();
				// Liquid only, whatever the consumer's configuration is: dissolved gas lives in
				// liquid, so a gas chunk in the same storage is not a carrier for it.
				if (e == null || !(e.Mass > 0f) || e.Element == null || !e.Element.IsLiquid)
				{
					continue;
				}
				carriers.Add(e);
				total += e.Mass;
			}
			return total;
		}

		private static float GetTemperatureNear(Pending p, int cell)
		{
			float mass = CollectCarriers(p);
			if (mass > 0f)
			{
				// Mass-weighted, because with the Vacuum sentinel there can be more than one chunk
				// and the payload was split across all of them.
				double sum = 0.0;
				for (int i = 0; i < carriers.Count; i++)
				{
					sum += (double)carriers[i].Temperature * (double)carriers[i].Mass;
				}
				float weighted = (float)(sum / (double)mass);
				if (weighted > 0f)
				{
					return weighted;
				}
			}
			return Grid.IsValidCell(cell) && Grid.Temperature[cell] > 0f
				? Grid.Temperature[cell] : 293.15f;
		}
	}

	/// <summary>
	/// <c>ElementConsumer.handleInstanceMap</c>, read by reflection: vanilla's own private map
	/// from the SIM HANDLE the sim reports to the managed consumer that owns it.
	///
	/// This is what makes this class cost no SimDLL work at all. The sim already publishes a consumed record
	/// naming the consumer by its sim handle (<see cref="LiquidPayloadConsumed"/>), and the game
	/// already keeps the only map that can resolve one -- it uses it itself, three lines later, to
	/// deliver the mass. Reading it is strictly cheaper and strictly more correct than keeping a
	/// second map of our own, which could only ever go stale against this one.
	/// </summary>
	internal static class ElementConsumerLookup
	{
		private static Dictionary<int, ElementConsumer> map;
		private static bool looked;

		internal static ElementConsumer ByHandle(int handle)
		{
			if (!looked)
			{
				looked = true;
				map = AccessTools.StaticFieldRefAccess<Dictionary<int, ElementConsumer>>(
					typeof(ElementConsumer), "handleInstanceMap");
				if (map == null)
				{
					FrameworkLog.Warn("DissolvedCargo: ElementConsumer.handleInstanceMap is not "
						+ "readable, so gas a pump takes cannot be handed to the pump. It will be "
						+ "released at the consumer instead.");
				}
			}
			ElementConsumer consumer;
			return map != null && map.TryGetValue(handle, out consumer) ? consumer : null;
		}
	}
}
