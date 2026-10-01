using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OniFramework
{
	/// <summary>
	/// WHERE THE SIMULATION FRAME ACTUALLY GOES, in a colony somebody actually built, as data.
	/// The managed half of <c>SIM_DebugSetProfiler</c> / <c>SIM_DebugProfileSummary</c> /
	/// <c>SIM_DebugProfile</c>.
	///
	/// <b>Why this exists when the sim already had a profiler.</b> It had two readers and
	/// neither one is a program. The replacement SimDLL's offline <c>bench</c> times the same kernels
	/// OFFLINE, against a recorded corpus, on scenarios somebody wrote -- it never runs the
	/// game's own frame and never sees a real colony. The backtick key times them in the real
	/// game and then prints the table as ENGLISH into <c>Player.log</c> and
	/// <c>sim_profile.log</c>, where comparing two builds means reading two log files by eye.
	/// This is the third reader: the same table, as numbers, while the game runs.
	/// <see cref="DebugInspectorServer"/>'s <c>/profile</c> route is the first consumer, but the
	/// general case is any mod: a mod asking whether its own additions cost a frame must be able to ask without patching the SimDLL.
	///
	/// <b>TWO CLOCKS, AND ONLY ONE OF THEM NEEDS ARMING.</b> This is the single most important
	/// thing to know before reading a row. <see cref="Slot.Milliseconds"/> and
	/// <see cref="Slot.Calls"/> accumulate only while the profiler is armed -- disarmed, the sim
	/// reads no timer at all, which is the property that lets the instrumentation exist in a
	/// shipping build. The four CENSUS numbers (<see cref="Slot.Examined"/>,
	/// <see cref="Slot.Skipped"/>, <see cref="Slot.Changes"/>, <see cref="Slot.Invocations"/>)
	/// are not gated and count on every frame of every run. So a row can honestly carry millions
	/// of examined cells and 0.0 ms, and <b>a zero duration means "nobody armed it" and never
	/// "this kernel did no work"</b> -- <see cref="Slot.Invocations"/> is the field that answers
	/// the second question.
	///
	/// <b>Nothing here has a window of its own.</b> Both halves are cumulative -- the timings
	/// since the last arm, the census since the last arm or since the process started, whichever
	/// is later. Reading does not zero and cannot: a read that zeroed would let two readers, or
	/// a reader and the backtick key, silently destroy each other's measurement. A caller that
	/// wants a per-interval figure takes two reads and subtracts, which is what
	/// <see cref="Delta"/> is for.
	///
	/// <b>Nothing is cached.</b> Unlike <see cref="SimExtPhases"/>, whose table is compile-time,
	/// every number here is live and changes every frame. Only the ABSENCE of the exports is
	/// latched, because nothing can add an export to a DLL that is already mapped.
	///
	/// <b>On a stock SimDLL this reports UNAVAILABLE rather than throwing</b>, the same
	/// probe-by-calling shape <see cref="SimVersion"/> and <see cref="SimExtPhases"/> use.
	/// </summary>
	public static class SimProfile
	{
		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern int SIM_DebugSetProfiler(int enabled);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugProfileSummary(byte* outSummary);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugProfile(byte* outRows, int max);

		// OniProfileSlot and OniProfileSummary, abi/sim_ext_api.h, `#pragma pack(push, 4)`,
		// ONI_SIM_STATIC_ASSERT'ed at 96 and 48 bytes native-side. Field offsets rather than a
		// [StructLayout] mirror, for the reason every sibling facade decodes by offset: the
		// layout is an ABI contract stated in one header, and a marshalled struct that drifts
		// from it fails by silently reading the wrong eight bytes rather than by not compiling.
		private const int SlotBytes = 96;
		private const int SlotNameBytes = 32;
		private const int SlotOffsetMs = 32;
		private const int SlotOffsetCalls = 40;
		private const int SlotOffsetExamined = 48;
		private const int SlotOffsetSkipped = 56;
		private const int SlotOffsetChanges = 64;
		private const int SlotOffsetInvocations = 72;
		private const int SlotOffsetBudget = 80;
		private const int SlotOffsetCellSweep = 88;
		private const int SlotOffsetWithinBudget = 92;

		private const int SummaryBytes = 48;
		private const int SummaryOffsetEnabled = 0;
		private const int SummaryOffsetFrames = 4;
		private const int SummaryOffsetRegionCells = 8;
		private const int SummaryOffsetRegionCellsIncl = 16;
		private const int SummaryOffsetGridCells = 24;
		private const int SummaryOffsetRegions = 32;
		private const int SummaryOffsetOutside = 40;

		// A sanity ceiling on the slot count the DLL reports, so a garbage return from a DLL
		// that exports these names and means something else by them cannot ask this side to
		// allocate an arbitrary buffer. The real table is 18 rows.
		private const int MaxSlots = 256;

		// ------------------------------------------------------------------ state

		private static bool absent;

		// ------------------------------------------------------------------ the table

		/// <summary>
		/// One timed kernel. Read <see cref="SimProfile"/>'s "two clocks" note before comparing
		/// any two fields on this row: the first two accumulate only while armed, the rest
		/// always.
		/// </summary>
		public struct Slot
		{
			/// <summary>The kernel's own name, e.g. <c>StepConduction</c> -- the same name
			/// <c>bench</c>'s offline table and the DLL's log report use.</summary>
			public string Name;

			/// <summary>Cumulative milliseconds since the last arm; 0.0 while disarmed.</summary>
			public double Milliseconds;

			/// <summary>Timed entries since the last arm; 0 while disarmed.</summary>
			public long Calls;

			/// <summary>
			/// Cells this sweep's own loop stepped over. Counted per ROW from inside the loop
			/// rather than computed from the loop bounds, deliberately: a number derived from the
			/// same rectangle the loop is driven by would agree with that loop even when the loop
			/// is wrong, which is the exact defect this counter exists to catch.
			/// </summary>
			public long Examined;

			/// <summary>
			/// Cells inside the rectangle the kernel skipped in a RUN via an internal early-out.
			/// Only conduction's quiet-tile test does this today, so
			/// <c>Examined - Skipped</c> is the number of cells the sweep actually stood on.
			/// </summary>
			public long Skipped;

			/// <summary>
			/// State changes announced. A COUNT OF CHANGES, NOT OF DISTINCT CELLS -- a gas sweep
			/// can announce the same cell as a source and again as a destination in one
			/// invocation, so this may legitimately exceed <see cref="Examined"/>.
			/// <c>Changes / Examined</c> is a work density, not a fraction.
			/// </summary>
			public long Changes;

			/// <summary>Times the kernel was entered: once per region per substep.</summary>
			public long Invocations;

			/// <summary>
			/// Cells one invocation of this kernel may step over, by its declared bound; 0 on
			/// rows where <see cref="IsCellSweep"/> is false.
			/// </summary>
			public long BudgetPerInvocation;

			/// <summary>
			/// Whether the census numbers on this row are in a unit that compares. A cell sweep
			/// walks a rectangle of the grid; the rest walk LISTS -- the message queue, the
			/// registered buildings, the element chunks, the radiation emitters -- where a ratio
			/// against the grid says nothing at all.
			/// </summary>
			public bool IsCellSweep;

			/// <summary>
			/// <c>Examined &lt;= BudgetPerInvocation * Invocations</c>: the one assertion the
			/// census exists for, computed in the DLL because the rule that picks each kernel's
			/// denominator is the DLL's. A kernel that regresses to walking the whole grid trips
			/// its own bound and names itself here. True on rows where it means nothing.
			/// </summary>
			public bool WithinBudget;

			/// <summary>Milliseconds per frame, given the summary's frame count.</summary>
			public double MillisecondsPerFrame(int frames)
			{
				return frames > 0 ? Milliseconds / frames : 0.0;
			}

			/// <summary>
			/// Cells examined as a percentage of what this kernel's bound allows. Above 100 %
			/// means the sweep exceeded its declared bound -- see <see cref="WithinBudget"/>.
			/// 0 on a row that is not a cell sweep.
			/// </summary>
			public double BudgetPercent
			{
				get
				{
					long allowed = BudgetPerInvocation * Invocations;
					return allowed > 0 ? 100.0 * Examined / allowed : 0.0;
				}
			}
		}

		/// <summary>
		/// The table's header row: what the per-slot numbers are to be read against.
		/// </summary>
		public struct Summary
		{
			/// <summary>Whether the millisecond half is armed.</summary>
			public bool Enabled;

			/// <summary>
			/// Frames since the last arm -- the denominator for <see cref="Slot.Milliseconds"/>.
			/// One per <c>PrepareGameData</c>, NOT one per substep: a kernel runs once per region
			/// per substep, so its call count is the wrong denominator for anything a frame
			/// budget is compared against.
			/// </summary>
			public int Frames;

			/// <summary>Sum of the active rectangles' areas -- what a correct sweep may look at
			/// once per invocation. A sum, not a count of distinct cells: rectangles may
			/// overlap, so it is an upper bound.</summary>
			public long RegionCells;

			/// <summary>The same rectangles grown one cell on their far edges, which is what
			/// <c>StepPostProcess</c> sweeps. The game sends <c>maxY = height - 1</c>, so this is
			/// the only budget that includes the world's top row.</summary>
			public long RegionCellsInclusive;

			/// <summary>The PADDED grid: what a sweep that ignored its regions would look at
			/// instead. The gap between this and <see cref="RegionCells"/> is the defect class
			/// the census was built for.</summary>
			public long GridCells;

			/// <summary>Active regions -- one per discovered world, so 1 in the base game and
			/// one per asteroid in a Spaced Out cluster.</summary>
			public long Regions;

			/// <summary>
			/// Changes announced while no sweep was running: a queued <c>ModifyCell</c>, an
			/// emitter, a building. Its own field rather than folded into a kernel's, because
			/// charging them to whichever kernel ran last is the sort of quiet misattribution
			/// that makes a profile agree with itself and lie.
			/// </summary>
			public long OutsideAnySweep;
		}

		// ------------------------------------------------------------------ availability

		/// <summary>
		/// Whether this SimDLL publishes a live profile at all: true for a custom SimDLL new
		/// enough to have the profile exports, false for a stock one or an older custom
		/// build. Probes by calling, since an export cannot be tested for without calling it;
		/// the probe is <see cref="SIM_DebugProfile"/>'s sizing call, which touches no world
		/// state and is legal before a world exists.
		/// </summary>
		public static bool Available
		{
			get { return SlotCount() > 0; }
		}

		/// <summary>
		/// Whether the millisecond half is currently armed. False on a DLL with no live profile.
		/// </summary>
		public static bool IsArmed
		{
			get
			{
				Summary summary;
				return TryReadSummary(out summary) && summary.Enabled;
			}
		}

		// ------------------------------------------------------------------ arming

		/// <summary>
		/// Arms or disarms the millisecond half, returning the PREVIOUS setting so a caller can
		/// put it back the way it found it. False on a DLL with no live profile, which is also
		/// what a previously-disarmed profiler returns -- check <see cref="Available"/> first if
		/// the difference matters.
		///
		/// <b>Arming zeroes both halves; arming an ALREADY-armed profiler does nothing.</b> That
		/// asymmetry is deliberate on the native side: zeroing here would silently discard a
		/// window somebody else armed -- another mod, or the player's backtick key -- halfway
		/// through it, with no way for either to notice. A caller that genuinely wants a fresh
		/// window disarms first.
		///
		/// <b>Disarming does not write the log report the backtick key writes.</b> A caller
		/// reading <see cref="TryRead"/> already has every number that report would print.
		/// </summary>
		public static bool SetArmed(bool armed)
		{
			if (absent)
			{
				return false;
			}
			try
			{
				return SIM_DebugSetProfiler(armed ? 1 : 0) != 0;
			}
			catch (EntryPointNotFoundException)
			{
				absent = true;
				return false;
			}
			catch (DllNotFoundException e)
			{
				// Deliberately does NOT latch: "the native library was not resolvable at the
				// moment I asked" is a different fact from "this DLL has no live profile", and
				// caching the first as the second would answer UNAVAILABLE for the rest of the
				// session. SimExtPhases makes the same distinction.
				FrameworkLog.Warn("SimProfile: could not reach the SimDLL: " + e.Message);
				return false;
			}
		}

		// ------------------------------------------------------------------ reading

		/// <summary>
		/// The header row alone, for a caller that only wants the frame count or the geometry.
		/// False on a DLL with no live profile, leaving <paramref name="summary"/> at its
		/// default -- the same discipline the native export holds, which leaves a refused
		/// caller's buffer untouched rather than filling it with something plausible.
		/// </summary>
		public static unsafe bool TryReadSummary(out Summary summary)
		{
			summary = default(Summary);
			if (absent)
			{
				return false;
			}
			byte* raw = stackalloc byte[SummaryBytes];
			int ok;
			try
			{
				ok = SIM_DebugProfileSummary(raw);
			}
			catch (EntryPointNotFoundException)
			{
				absent = true;
				return false;
			}
			catch (DllNotFoundException e)
			{
				FrameworkLog.Warn("SimProfile: could not reach the SimDLL: " + e.Message);
				return false;
			}
			if (ok == 0)
			{
				return false;
			}
			IntPtr p = new IntPtr(raw);
			summary.Enabled = Marshal.ReadInt32(p, SummaryOffsetEnabled) != 0;
			summary.Frames = Marshal.ReadInt32(p, SummaryOffsetFrames);
			summary.RegionCells = Marshal.ReadInt64(p, SummaryOffsetRegionCells);
			summary.RegionCellsInclusive = Marshal.ReadInt64(p, SummaryOffsetRegionCellsIncl);
			summary.GridCells = Marshal.ReadInt64(p, SummaryOffsetGridCells);
			summary.Regions = Marshal.ReadInt64(p, SummaryOffsetRegions);
			summary.OutsideAnySweep = Marshal.ReadInt64(p, SummaryOffsetOutside);
			return true;
		}

		/// <summary>
		/// The whole table plus its header row, read under ONE worker barrier per export call.
		/// False on a DLL with no live profile, leaving both outputs at their defaults.
		///
		/// <b>Two calls, not one per row.</b> The native side takes the sim's idle barrier on
		/// entry, so N per-row reads would be N chances to block on an in-flight frame; the bulk
		/// shape is the same one <c>SIM_GasMassBatchQuery</c> and <c>SIM_DebugCellFlow</c> hold,
		/// because each native read may wait on the sim's worker.
		/// </summary>
		public static unsafe bool TryRead(out Summary summary, out Slot[] slots)
		{
			slots = new Slot[0];
			if (!TryReadSummary(out summary))
			{
				return false;
			}

			int count = SlotCount();
			if (count <= 0)
			{
				return false;
			}

			byte* raw = stackalloc byte[SlotBytes * count];
			int total;
			try
			{
				total = SIM_DebugProfile(raw, count);
			}
			catch (EntryPointNotFoundException)
			{
				// Sizing without filling: a build that published half the pair. Refuse the whole
				// table rather than hand back a partial one.
				absent = true;
				return false;
			}
			catch (DllNotFoundException e)
			{
				FrameworkLog.Warn("SimProfile: could not reach the SimDLL: " + e.Message);
				return false;
			}
			if (total <= 0)
			{
				return false;
			}
			// The export returns the TOTAL, which may exceed what was asked for. It cannot here
			// -- the buffer was sized by the immediately preceding sizing call -- but the clamp
			// is what makes that an assumption this code does not depend on.
			int filled = total < count ? total : count;

			Slot[] table = new Slot[filled];
			for (int i = 0; i < filled; i++)
			{
				IntPtr p = new IntPtr(raw + i * SlotBytes);
				table[i].Name = SimExtRegistry.ReadName(p, 0, SlotNameBytes);
				table[i].Milliseconds =
					BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, SlotOffsetMs));
				table[i].Calls = Marshal.ReadInt64(p, SlotOffsetCalls);
				table[i].Examined = Marshal.ReadInt64(p, SlotOffsetExamined);
				table[i].Skipped = Marshal.ReadInt64(p, SlotOffsetSkipped);
				table[i].Changes = Marshal.ReadInt64(p, SlotOffsetChanges);
				table[i].Invocations = Marshal.ReadInt64(p, SlotOffsetInvocations);
				table[i].BudgetPerInvocation = Marshal.ReadInt64(p, SlotOffsetBudget);
				table[i].IsCellSweep = Marshal.ReadInt32(p, SlotOffsetCellSweep) != 0;
				table[i].WithinBudget = Marshal.ReadInt32(p, SlotOffsetWithinBudget) != 0;
			}
			slots = table;
			return true;
		}

		/// <summary>
		/// The difference between two reads, row by row, which is how a per-interval figure is
		/// taken from two cumulative ones. Rows are matched by NAME rather than by position, so
		/// a table that gained a row between the two samples subtracts correctly instead of
		/// silently comparing two different kernels; a row present in <paramref name="later"/>
		/// and absent from <paramref name="earlier"/> is returned whole.
		///
		/// <b>A re-arm between the two samples makes the result meaningless</b>, and this cannot
		/// detect it -- arming zeroes, so the later read can be smaller than the earlier one and
		/// the difference comes out negative. Compare the two summaries' <c>Frames</c> first: a
		/// later frame count that is not greater than the earlier one is the signal.
		/// </summary>
		public static Slot[] Delta(Slot[] earlier, Slot[] later)
		{
			if (later == null)
			{
				return new Slot[0];
			}
			Slot[] result = new Slot[later.Length];
			for (int i = 0; i < later.Length; i++)
			{
				result[i] = later[i];
				if (earlier == null)
				{
					continue;
				}
				for (int j = 0; j < earlier.Length; j++)
				{
					if (earlier[j].Name != later[i].Name)
					{
						continue;
					}
					result[i].Milliseconds -= earlier[j].Milliseconds;
					result[i].Calls -= earlier[j].Calls;
					result[i].Examined -= earlier[j].Examined;
					result[i].Skipped -= earlier[j].Skipped;
					result[i].Changes -= earlier[j].Changes;
					result[i].Invocations -= earlier[j].Invocations;
					break;
				}
			}
			return result;
		}

		/// <summary>
		/// The whole table as text, one kernel per line, or a single line saying there is none.
		/// The same shape the DLL writes into <c>sim_profile.log</c>, so the two can be put side
		/// by side. For a log, a diagnostic route or a bug report.
		/// </summary>
		public static string Report()
		{
			Summary summary;
			Slot[] slots;
			if (!TryRead(out summary, out slots))
			{
				return "no live profile: this SimDLL does not export SIM_DebugProfile "
					+ "(stock SimDLL, or a custom build older than the published profile)";
			}

			StringBuilder sb = new StringBuilder();
			sb.Append(summary.Enabled ? "profiler ARMED" : "profiler DISARMED (timings are 0)");
			sb.Append(", ").Append(summary.Frames).Append(" frames since arming");
			for (int i = 0; i < slots.Length; i++)
			{
				Slot s = slots[i];
				if (s.Calls == 0 && s.Invocations == 0)
				{
					continue;
				}
				sb.Append('\n').Append("  ").Append(s.Name.PadRight(26));
				sb.Append(s.MillisecondsPerFrame(summary.Frames).ToString("F3")).Append(" ms/frame");
				if (s.IsCellSweep && s.Invocations > 0)
				{
					sb.Append("  examined ").Append(s.Examined);
					sb.Append(" (").Append(s.BudgetPercent.ToString("F1")).Append("% of bound)");
					if (!s.WithinBudget)
					{
						sb.Append("  ** OVER ITS DECLARED BOUND **");
					}
				}
			}
			if (summary.OutsideAnySweep > 0)
			{
				sb.Append('\n').Append("  ").Append("(outside any sweep)".PadRight(26));
				sb.Append(summary.OutsideAnySweep).Append(" changes");
			}
			return sb.ToString();
		}

		// ------------------------------------------------------------------ the fetch

		/// <summary>
		/// How many rows the DLL's table has, or 0 when it has none. The sizing call: a null
		/// buffer with a zero maximum, which is the discipline every buffer-filling export here
		/// holds.
		/// </summary>
		private static unsafe int SlotCount()
		{
			if (absent)
			{
				return 0;
			}
			int count;
			try
			{
				count = SIM_DebugProfile(null, 0);
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or one older than the published profile. A permanent fact about
				// the loaded binary, so it latches: nothing can add an export to a mapped DLL.
				absent = true;
				return 0;
			}
			catch (DllNotFoundException e)
			{
				FrameworkLog.Warn("SimProfile: could not reach the SimDLL: " + e.Message);
				return 0;
			}
			if (count <= 0 || count > MaxSlots)
			{
				return 0;
			}
			return count;
		}
	}
}
