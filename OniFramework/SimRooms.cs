using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OniFramework
{
	/// <summary>
	/// A WHOLE ROOM, SUMMED BY THE SIM, IN ONE CALL. The managed half of <c>SIM_RoomId</c> and
	/// <c>SIM_RoomAggregate</c> (<c>abi/sim_ext_api.h</c>, <c>sim/gas_rooms.h</c>), and the read
	/// side of a graph the sim has maintained since the mixing kernel first needed it.
	///
	/// <b>What the sim already knew, and could not say.</b> <c>gas_rooms.h</c> keeps, for every
	/// cell, which 4-connected open-cell component it belongs to, how big that component is, and
	/// how much of it is awake — incrementally, because <c>MixRoomPooled</c> skips a sleeping
	/// room as one block. Until these two exports the only thing published out of that graph was
	/// one boolean per cell (<see cref="GasMixtureFacade.IsRoomOwned"/>), which threw the room id
	/// away. So a mod that wanted a room's pressure or its composition had to rebuild the room
	/// itself, from outside, one cell at a time.
	///
	/// <b>It is a better answer, not only a cheaper one.</b> The shape that approximation
	/// actually took in this repo is a radius-3 diamond around a vent — 25 cells, one
	/// <c>IsRoomOwned</c> call each, then a composition and a pressure read per survivor, with an
	/// array allocated per composition read. A diamond is not a room: it leaks through walls, it
	/// truncates a large room, and it takes in cells on the far side of a door. This is the real
	/// component, wall-aware by construction — including the correction recorded in
	/// <c>gas_rooms.h</c>'s own header, that a cell counts as open only if it is BOTH not marked
	/// gas-impermeable AND not currently holding a solid element. Managed code re-deriving rooms
	/// would have to re-derive that fix, or silently reintroduce the hang it was written for.
	///
	/// <b>THE ROOM GRAPH DOES NOT EXIST UNTIL THE MIXTURE LAYER IS ACTIVE.</b> It is built for
	/// mixing, and mixing is activated for a world by the first <c>kInjectGasSpecies</c> that
	/// world ever receives (<see cref="GasMixtureFacade.Inject"/>,
	/// <see cref="GasMixtureFacade.ConvertFromVanilla"/>). Before that, every cell answers
	/// <see cref="NoRoom"/> and <see cref="TryGetAggregate"/> returns false — on a custom SimDLL
	/// that exports these symbols perfectly well. <b>"No room" is therefore not the same claim as
	/// "not available"</b>, and the two are reported separately on purpose: one says this world
	/// has no room graph yet, the other says this DLL cannot build one.
	///
	/// <b>Why this is not part of <see cref="GasMixtureFacade"/>, which owns every other room
	/// read.</b> That class latches ONE flag for all its exports, and its own comment says why
	/// that is safe: "they are all added by the same DLL in the same build". These two are
	/// newer than the gas-mixture exports, so a real install can
	/// have <c>SIM_GasPressure</c> and not <c>SIM_RoomAggregate</c>. Sharing the latch would mean
	/// one missing export switched the whole gas facade off, which is exactly the failure the
	/// latch exists to prevent. Its own latch is the conservative choice, not a stylistic one.
	///
	/// <b>Zero allocation, and that is a contract rather than an implementation detail.</b> The
	/// descriptor is a struct with fixed buffers, filled from a stack buffer; nothing here
	/// allocates on any path, including the failure paths. Allocation, not CPU time, is ONI's
	/// main lag cause (the freeze is Mono GC), and an allocating per-cell loop is precisely what this replaces.
	///
	/// <b>Ask once per room, not once per cell.</b> The sum is O(cells in the room) and is NOT
	/// cached natively. <see cref="RoomId"/> is the cheap call — one barrier and one array read —
	/// so group your cells by it and call <see cref="TryGetAggregate"/> once per distinct id.
	///
	/// <b>A room id is a grouping key for this frame, never a handle.</b> Ids are stable only
	/// between rebuilds of the room graph, and digging or building rebuilds it: rooms merge, split
	/// and are re-numbered. Two cells reporting the same id on the same frame ARE in the same
	/// room; the same id across a dig means nothing.
	///
	/// <b>On a stock SimDLL this reports unavailable rather than throwing.</b> Same latch shape as
	/// every facade written after <see cref="GasMixtureFacade"/>: the first call raises
	/// <see cref="EntryPointNotFoundException"/>, this class catches it, says so once, and every
	/// read afterwards answers "no room" without calling again.
	/// </summary>
	public static class SimRooms
	{
		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern int SIM_RoomId(int cellIdx);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_RoomAggregate(int cellIdx, byte* outAggregate);

		// ------------------------------------------------------------------ layout
		//
		// DECODED BY FIELD OFFSET, NOT BY [StructLayout] MARSHALLING, and for the same reason
		// SimExtPhases and SimExtMessages are: a marshalled struct that drifts from the native
		// header fails by silently reading the wrong four bytes, while an offset table that
		// drifts fails an assertion in the live gate naming the field. The offsets below are
		// OniRoomAggregate in abi/sim_ext_api.h, whose own static_assert pins the total at 140.

		private const int AggregateBytes = 140;
		private const int OffsetRoomId = 0;
		private const int OffsetCellCount = 4;
		private const int OffsetAwakeCount = 8;
		private const int OffsetOwned = 12;
		private const int OffsetMixtureCellCount = 16;
		private const int OffsetSpeciesCount = 20;
		private const int OffsetSpeciesOverflow = 24;
		private const int OffsetMixtureMassKg = 28;
		private const int OffsetVanillaMassKg = 32;
		private const int OffsetMeanPressurePa = 36;
		private const int OffsetTemperatureK = 40;
		private const int OffsetSpecies = 44;
		private const int OffsetMassBySpecies = 76;

		/// <summary>
		/// Composition slots a room's aggregate carries — <c>ONI_ROOM_MAX_SPECIES</c>. It bounds a
		/// ROOM, and is deliberately larger than the sim's own per-CELL bound of 8, so a room
		/// holding more distinct species than any one of its cells does still reports them all.
		/// </summary>
		public const int MaxSpecies = 16;

		/// <summary>
		/// What <see cref="RoomId"/> answers for a cell that is in no room: solid, out of range,
		/// or in a world whose mixture layer has never been activated.
		/// </summary>
		public const int NoRoom = -1;

		// ------------------------------------------------------------------ availability

		private static bool unavailable;
		private static bool warned;

		/// <summary>
		/// Whether this SimDLL publishes the room graph at all. True until a call has actually
		/// failed — an export cannot be probed for without calling it, since these take the worker
		/// barrier and need a world (contrast <see cref="SimExtPhases.Available"/>, which can
		/// answer by asking).
		///
		/// <b>False does not mean "no rooms here".</b> It means this DLL cannot answer. A world
		/// that simply has no room graph yet reports <see cref="NoRoom"/> with
		/// <see cref="Available"/> still true.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		private static void MarkUnavailable(string export)
		{
			unavailable = true;
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL does not export " + export
					+ " -- the room graph is not readable from this DLL (stock SimDLL, or a custom "
					+ "build older than the room aggregate). Every room read answers 'no room' "
					+ "from here on; nothing will throw.");
			}
		}

		private static void ReportMissingLibrary(DllNotFoundException e)
		{
			// DELIBERATELY DOES NOT LATCH, unlike the missing-export case above. Nothing can add
			// an export to a DLL that is already mapped, so EntryPointNotFoundException is a
			// permanent fact -- but a DLL that could not be found may be found on a later call
			// (a load order still settling, a probing path that changes). Latching on it would
			// turn a transient into a permanent, and a session would silently never see the
			// rooms of a DLL it did in fact have.
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL could not be loaded while reading the room graph: "
					+ e.Message + ". Not latching -- a later call may still succeed.");
			}
		}

		// ------------------------------------------------------------------ the aggregate

		/// <summary>
		/// One room, summed: what it holds, in both layers, at the moment it was asked. A value
		/// type with fixed buffers, so obtaining one allocates nothing.
		///
		/// <b>The two mass fields are two LAYERS over one grid, not a value and its fallback.</b>
		/// A room cell can hold multi-gas mixture state, or vanilla's single <c>PhaseEntry</c>
		/// mass, or both. <see cref="MixtureCellCount"/> says how many of <see cref="CellCount"/>
		/// carry any mixture state at all, which is what lets a caller tell "half this room is
		/// outside the mixture layer" from "this room is empty".
		/// </summary>
		public unsafe struct RoomAggregate
		{
			/// <summary>The room's id this frame, or <see cref="NoRoom"/>.</summary>
			public int RoomId;

			/// <summary>Open cells in the room — the sim's own <c>room_size</c>.</summary>
			public int CellCount;

			/// <summary>
			/// Of those, cells not currently gas-sleeping. A room at 0 is one the mixing kernel
			/// skips whole, which is the number that says whether anything is happening in here.
			/// </summary>
			public int AwakeCount;

			/// <summary>
			/// Whether this room has been promoted to the new engine (<c>kPromoteRoom</c>). The
			/// same bit <see cref="GasMixtureFacade.IsRoomOwned"/> reports per cell.
			/// </summary>
			public bool Owned;

			/// <summary>Of <see cref="CellCount"/>, cells holding any mixture state at all.</summary>
			public int MixtureCellCount;

			/// <summary>Entries filled in the composition — at most <see cref="MaxSpecies"/>.</summary>
			public int SpeciesCount;

			/// <summary>
			/// Distinct species that found no slot. Their mass is still inside
			/// <see cref="MixtureMassKg"/>, so a non-zero value here is exactly why the
			/// composition may sum to less than the total — detectable, never lost.
			/// </summary>
			public int SpeciesOverflow;

			/// <summary>Multi-gas mass summed over the room.</summary>
			public float MixtureMassKg;

			/// <summary>Vanilla <c>PhaseEntry</c> mass summed over the room — the other layer.</summary>
			public float VanillaMassKg;

			/// <summary>
			/// Mean pressure over <see cref="MixtureCellCount"/> cells, NOT over
			/// <see cref="CellCount"/>: a cell with no mixture state has no mixture pressure, and
			/// averaging a zero in for it would report a room as emptier the less of it the
			/// mixture layer owns. 0 when <see cref="MixtureCellCount"/> is 0.
			/// </summary>
			public float MeanPressurePa;

			/// <summary>
			/// Mass-weighted mean temperature, weighted by each cell's WHOLE mass (vanilla plus
			/// mixture), because <c>PhaseEntry.temperature</c> is one shared field per cell. 0 for
			/// a room with no mass anywhere.
			/// </summary>
			public float TemperatureK;

			private fixed ushort species[MaxSpecies];
			private fixed float massBySpeciesKg[MaxSpecies];

			/// <summary>
			/// The ElementTable index in composition slot <paramref name="slot"/> — NOT a
			/// <c>SimHashes</c> id; see <c>abi/sim_abi_ext.h</c> for why the sim speaks in table
			/// indices. Returns 0 for a slot past <see cref="SpeciesCount"/>.
			/// </summary>
			public ushort SpeciesAt(int slot)
			{
				if (slot < 0 || slot >= SpeciesCount)
				{
					return 0;
				}
				fixed (ushort* p = species)
				{
					return p[slot];
				}
			}

			/// <summary>
			/// The room's total mass of the species in slot <paramref name="slot"/>. Returns 0 for
			/// a slot past <see cref="SpeciesCount"/>.
			/// </summary>
			public float MassKgAt(int slot)
			{
				if (slot < 0 || slot >= SpeciesCount)
				{
					return 0f;
				}
				fixed (float* p = massBySpeciesKg)
				{
					return p[slot];
				}
			}

			/// <summary>
			/// Fill one composition slot. Internal because a caller has no business writing a
			/// room's contents into an aggregate the sim did not fill: this exists so the decode
			/// above can write the fixed buffers, which nothing outside the struct can reach.
			/// </summary>
			internal void SetSlot(int slot, ushort speciesIdx, float massKg)
			{
				if (slot < 0 || slot >= MaxSpecies)
				{
					return;
				}
				fixed (ushort* s = species)
				{
					s[slot] = speciesIdx;
				}
				fixed (float* m = massBySpeciesKg)
				{
					m[slot] = massKg;
				}
			}

			/// <summary>
			/// The room's total mass of one species, by ElementTable index. False when this room
			/// holds none of it — which, if <see cref="SpeciesOverflow"/> is non-zero, may also
			/// mean it holds some but found no slot to report it in.
			/// </summary>
			public bool TryGetMassKg(ushort speciesIdx, out float massKg)
			{
				for (int i = 0; i < SpeciesCount; i++)
				{
					if (SpeciesAt(i) == speciesIdx)
					{
						massKg = MassKgAt(i);
						return true;
					}
				}
				massKg = 0f;
				return false;
			}

			/// <summary>
			/// Everything on one line, including the composition, for a log a person reads.
			/// </summary>
			public override string ToString()
			{
				if (RoomId == NoRoom)
				{
					return "no room";
				}
				StringBuilder sb = new StringBuilder();
				sb.Append("room ").Append(RoomId).Append(": ").Append(CellCount).Append(" cells (")
					.Append(AwakeCount).Append(" awake, ").Append(MixtureCellCount)
					.Append(" with mixture), ").Append(Owned ? "promoted" : "vanilla-owned")
					.Append(", mixture ").Append(MixtureMassKg.ToString("F4")).Append(" kg, vanilla ")
					.Append(VanillaMassKg.ToString("F4")).Append(" kg, ")
					.Append(MeanPressurePa.ToString("F1")).Append(" Pa mean, ")
					.Append(TemperatureK.ToString("F2")).Append(" K");
				for (int i = 0; i < SpeciesCount; i++)
				{
					sb.Append(i == 0 ? " [" : ", ").Append(SpeciesAt(i)).Append('=')
						.Append(MassKgAt(i).ToString("F4"));
				}
				if (SpeciesCount > 0)
				{
					sb.Append(']');
				}
				if (SpeciesOverflow > 0)
				{
					sb.Append(" (+").Append(SpeciesOverflow).Append(" species with no slot)");
				}
				return sb.ToString();
			}
		}

		// ------------------------------------------------------------------ reads

		/// <summary>
		/// Which room <paramref name="cell"/> is in, or <see cref="NoRoom"/>. The cheap call: one
		/// worker barrier and one array read, no sum. Group cells by this and call
		/// <see cref="TryGetAggregate"/> once per distinct id rather than once per cell.
		///
		/// <b>The id is a grouping key for this frame.</b> Rebuilding the room graph — which
		/// digging and building both do — re-numbers rooms.
		/// </summary>
		public static int RoomId(int cell)
		{
			if (unavailable)
			{
				return NoRoom;
			}
			try
			{
				return SIM_RoomId(cell);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_RoomId");
				return NoRoom;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return NoRoom;
			}
		}

		/// <summary>
		/// Whether <paramref name="cell"/> is in a room at all. Sugar over
		/// <see cref="RoomId"/>; a false answer means "no room here", never "this DLL cannot say"
		/// — ask <see cref="Available"/> for that.
		/// </summary>
		public static bool HasRoom(int cell)
		{
			return RoomId(cell) != NoRoom;
		}

		/// <summary>
		/// The whole room <paramref name="cell"/> belongs to, summed in the sim under one worker
		/// barrier, with no allocation on either side.
		///
		/// False, with <paramref name="aggregate"/> left holding <see cref="NoRoom"/> and zeros,
		/// when this DLL has no room exports, when the world has no mixture layer yet, or when the
		/// cell is solid or out of range. <b>The struct is written on every path</b>, so a caller
		/// that ignores the return value cannot read a plausible room out of it.
		/// </summary>
		public static unsafe bool TryGetAggregate(int cell, out RoomAggregate aggregate)
		{
			aggregate = default(RoomAggregate);
			aggregate.RoomId = NoRoom;
			if (unavailable)
			{
				return false;
			}

			byte* raw = stackalloc byte[AggregateBytes];
			int ok;
			try
			{
				ok = SIM_RoomAggregate(cell, raw);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_RoomAggregate");
				return false;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return false;
			}
			if (ok == 0)
			{
				return false;
			}

			IntPtr p = new IntPtr(raw);
			aggregate.RoomId = Marshal.ReadInt32(p, OffsetRoomId);
			aggregate.CellCount = Marshal.ReadInt32(p, OffsetCellCount);
			aggregate.AwakeCount = Marshal.ReadInt32(p, OffsetAwakeCount);
			aggregate.Owned = Marshal.ReadInt32(p, OffsetOwned) != 0;
			aggregate.MixtureCellCount = Marshal.ReadInt32(p, OffsetMixtureCellCount);
			aggregate.SpeciesCount = Marshal.ReadInt32(p, OffsetSpeciesCount);
			aggregate.SpeciesOverflow = Marshal.ReadInt32(p, OffsetSpeciesOverflow);
			aggregate.MixtureMassKg = ReadFloat(raw, OffsetMixtureMassKg);
			aggregate.VanillaMassKg = ReadFloat(raw, OffsetVanillaMassKg);
			aggregate.MeanPressurePa = ReadFloat(raw, OffsetMeanPressurePa);
			aggregate.TemperatureK = ReadFloat(raw, OffsetTemperatureK);

			// CLAMPED RATHER THAN TRUSTED. speciesCount comes from the DLL and indexes a fixed
			// buffer here; a DLL built against a larger ONI_ROOM_MAX_SPECIES would otherwise walk
			// off the end of it. The clamp is silent on purpose -- the count is reported as what
			// was actually copied, so a caller reading SpeciesCount sees the truth about this
			// struct rather than about the one the DLL filled.
			int count = aggregate.SpeciesCount;
			if (count < 0)
			{
				count = 0;
			}
			if (count > MaxSpecies)
			{
				count = MaxSpecies;
			}
			aggregate.SpeciesCount = count;
			for (int i = 0; i < count; i++)
			{
				aggregate.SetSlot(i, (ushort)Marshal.ReadInt16(p, OffsetSpecies + (i * 2)),
					ReadFloat(raw, OffsetMassBySpecies + (i * 4)));
			}
			return true;
		}

		private static unsafe float ReadFloat(byte* raw, int offset)
		{
			return *(float*)(raw + offset);
		}

		/// <summary>
		/// The room a cell is in, summed, or a default-valued aggregate reporting
		/// <see cref="NoRoom"/>. For call sites that would only have thrown the bool away.
		/// </summary>
		public static RoomAggregate Aggregate(int cell)
		{
			RoomAggregate aggregate;
			TryGetAggregate(cell, out aggregate);
			return aggregate;
		}
	}
}
