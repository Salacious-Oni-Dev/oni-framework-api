using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// A GRID RAYCAST, ANSWERED BY THE SIM. The managed half of <c>SIM_QueryRaycast</c>
	/// (<c>abi/sim_ext_api.h</c>, <c>sim/raycast.h</c>): the first cell on a straight line that
	/// you say stops it, the cell just before that, and how much of a radiation-like quantity
	/// survives the cells on the way.
	///
	/// <b>It is the radiation field's own walk.</b> The sim answers with Klei's
	/// <c>RadiationAbsorptionAlongLine</c> cell walk plus a stop condition, so what a sensor
	/// "sees" and what radiation reaches never disagree about which cells lie between two
	/// points. Both endpoints are visited: a ray cast from inside a tile starts inside it, and
	/// then <see cref="Hit.LastClearCell"/> is <see cref="NoCell"/>.
	///
	/// <b>What stops it.</b> A cell stops the ray when its element's phase is in
	/// <c>stopOn</c>, OR it carries any bit of <c>stopOnProperties</c> — unless it carries any
	/// bit of <c>ignoreProperties</c>, which wins over both (the cell still attenuates). The
	/// third mask is line of sight: a Glass Tile is a SOLID cell that vanilla marks
	/// <see cref="Sim.Cell.Properties.Transparent"/>, so "blocked by walls, not by windows" is
	/// <c>stopOn: Phase.Solid, ignoreProperties: Transparent</c>. A closed door needs nothing
	/// extra: the game swaps its cells to the door's solid material, and back when it opens.
	///
	/// <b>Transmission is radiation's, not light's.</b> It is the product of
	/// <c>1 - absorption</c> over the visited cells, using the radiation tuning the game set —
	/// the only per-material attenuation the sim owns. With nothing to stop the ray it is
	/// exactly the number the radiation field would use for that line.
	///
	/// <b>Batch rays.</b> Each ray is O(length) in native code; the barrier and the crossing
	/// are the fixed cost. <see cref="CastBatch"/> answers any number under one of each, into
	/// arrays the caller owns. Nothing here allocates on any path.
	///
	/// <b>Cells are game cells.</b> The walk is a straight line across the whole grid and does
	/// not know where one asteroid ends; a ray between two worlds crosses the void between them.
	///
	/// <b>On a stock SimDLL this reports unavailable rather than throwing</b>, with the same
	/// latch every facade since <see cref="GasMixtureFacade"/> uses: the first call raises
	/// <see cref="EntryPointNotFoundException"/>, this class says so once, and every cast
	/// afterwards returns false without calling again. Its own latch, because this export
	/// is newer than the others and a real install can have every older one without it.
	/// </summary>
	public static class SimRaycast
	{
		/// <summary>Phases a ray can be told to stop on. The bit is <c>1 &lt;&lt; phase</c>,
		/// ONI_RAYCAST_PHASE_* in <c>abi/sim_ext_api.h</c>; the phase is the cell element's.</summary>
		[Flags]
		public enum Phase : uint
		{
			None = 0,
			Vacuum = 0x1,
			Gas = 0x2,
			Liquid = 0x4,
			Solid = 0x8,
		}

		public const int NoCell = -1;

		// ------------------------------------------------------------------ layout
		//
		// Explicit offsets and sizes, the managed copy of OniRaycastQuery and OniRaycastResult,
		// whose own static asserts in abi/sim_ext_api.h pin them at 20 and 16 bytes. Both are
		// blittable -- four-byte fields at fixed offsets -- so an array of either is handed to the
		// sim pinned, with no marshalling copy.

		/// <summary>One ray to cast. Build it with <see cref="Query(int, int, Phase, Sim.Cell.Properties, Sim.Cell.Properties)"/>.</summary>
		[StructLayout(LayoutKind.Explicit, Size = 20)]
		public struct Query
		{
			[FieldOffset(0)] public int StartCell;
			[FieldOffset(4)] public int EndCell;
			[FieldOffset(8)] public uint PhaseMask;
			[FieldOffset(12)] public uint PropertyMask;
			[FieldOffset(16)] public uint IgnorePropertyMask;

			public Query(int startCell, int endCell, Phase stopOn,
				Sim.Cell.Properties stopOnProperties = 0, Sim.Cell.Properties ignoreProperties = 0)
			{
				StartCell = startCell;
				EndCell = endCell;
				PhaseMask = (uint)stopOn;
				PropertyMask = (uint)stopOnProperties;
				IgnorePropertyMask = (uint)ignoreProperties;
			}
		}

		/// <summary>One ray's answer. A cell field is <see cref="NoCell"/> when there is none.</summary>
		[StructLayout(LayoutKind.Explicit, Size = 16)]
		public struct Hit
		{
			/// <summary>The first cell from the start that stopped the ray, or NoCell.</summary>
			[FieldOffset(0)] public int HitCell;

			/// <summary>The cell just before HitCell on the start side; the end cell when nothing
			/// stopped the ray; NoCell when the start cell itself stopped it.</summary>
			[FieldOffset(4)] public int LastClearCell;

			/// <summary>Cells from the start up to and including HitCell, or the whole line.
			/// Zero only for a query the sim refused (a cell outside the grid).</summary>
			[FieldOffset(8)] public int CellsVisited;

			/// <summary>Radiation transmission over those cells, in [0, 1].</summary>
			[FieldOffset(12)] public float Transmission;

			public bool Stopped
			{
				get { return HitCell != NoCell; }
			}

			public bool Valid
			{
				get { return CellsVisited > 0; }
			}

			public override string ToString()
			{
				if (!Valid)
				{
					return "refused";
				}
				return (Stopped ? "stopped at " + HitCell : "clear") + ", last clear "
					+ LastClearCell + ", " + CellsVisited + " cells, transmission "
					+ Transmission.ToString("F6");
			}
		}

		private static readonly Hit Refused = new Hit
		{
			HitCell = NoCell,
			LastClearCell = NoCell,
			CellsVisited = 0,
			Transmission = 0f,
		};

		// ------------------------------------------------------------------ export

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_QueryRaycast(Query* queries, Hit* results, int count);

		// ------------------------------------------------------------------ availability

		private static bool unavailable;
		private static bool warned;

		public static bool Available
		{
			get { return !unavailable; }
		}

		private static void MarkUnavailable()
		{
			unavailable = true;
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL does not export SIM_QueryRaycast -- grid raycasts are not "
					+ "available from this DLL (stock SimDLL, or a custom build without it). "
					+ "Every cast returns false from here on; nothing will throw.");
			}
		}

		private static void ReportMissingLibrary(DllNotFoundException e)
		{
			// Not latched, for the reason SimRooms gives: a DLL that could not be found may be
			// found later, while a missing export in a mapped DLL is permanent.
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL could not be loaded for a grid raycast: " + e.Message
					+ ". Not latching -- a later call may still succeed.");
			}
		}

		// ------------------------------------------------------------------ casts

		/// <summary>Casts one ray. Returns false -- with <paramref name="hit"/> written as a
		/// refusal -- when the DLL cannot cast, or when either cell is outside the grid.</summary>
		public static unsafe bool TryCast(int startCell, int endCell, Phase stopOn,
			out Hit hit, Sim.Cell.Properties stopOnProperties = 0,
			Sim.Cell.Properties ignoreProperties = 0)
		{
			hit = Refused;
			if (unavailable)
			{
				return false;
			}
			Query q = new Query(startCell, endCell, stopOn, stopOnProperties, ignoreProperties);
			Hit h = Refused;
			int answered;
			try
			{
				answered = SIM_QueryRaycast(&q, &h, 1);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable();
				return false;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return false;
			}
			if (answered != 1)
			{
				return false;
			}
			hit = h;
			return true;
		}

		/// <summary>Casts <paramref name="count"/> rays under one barrier into
		/// <paramref name="results"/>, which the caller owns and must hold at least that many.
		/// Every result is written, a refused query included (see <see cref="Hit.Valid"/>).
		/// Returns how many queries were valid, or -1 when the DLL cannot cast or the arguments
		/// do not fit -- in which case nothing was written.</summary>
		public static unsafe int CastBatch(Query[] queries, Hit[] results, int count)
		{
			if (unavailable || queries == null || results == null || count < 0
				|| count > queries.Length || count > results.Length)
			{
				return -1;
			}
			if (count == 0)
			{
				return 0;
			}
			try
			{
				fixed (Query* q = queries)
				fixed (Hit* r = results)
				{
					return SIM_QueryRaycast(q, r, count);
				}
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable();
				return -1;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return -1;
			}
		}
	}
}
