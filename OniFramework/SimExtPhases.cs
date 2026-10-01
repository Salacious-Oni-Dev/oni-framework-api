using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OniFramework
{
	/// <summary>
	/// How often a phase runs, relative to one call of <c>SIM_Update</c>. Mirrors
	/// <c>oni_sim::ext::ExtPhaseScope</c> in <c>abi/sim_abi_ext.h</c>; the native values are the
	/// wire contract and these must not be renumbered.
	/// </summary>
	public enum ExtPhaseScope
	{
		/// <summary>Once per frame, outside the substep loop entirely -- the drain and the
		/// publish.</summary>
		Frame = 0,

		/// <summary>Once per substep, outside the per-region loop. A frame that runs three
		/// substeps runs this three times, whatever the region count.</summary>
		Substep = 1,

		/// <summary>Once per region per substep. The common case.</summary>
		Region = 2,

		/// <summary>
		/// Sits inside the per-region loop but sweeps THE WHOLE GRID each time, so on a world
		/// with N regions it covers every cell N times per substep.
		///
		/// Called out separately rather than filed under <see cref="Region"/> because the
		/// difference is invisible on a single-region world -- which is every world the offline
		/// suite runs, and most worlds a mod is developed against. A consumer that does per-cell
		/// work keyed to one of these phases pays for it N times over on a real cluster.
		/// </summary>
		GridInRegion = 3,
	}

	/// <summary>
	/// Why a phase might not run on a given frame. A BITMASK, so a phase can carry both --
	/// <see cref="World"/> is deliberately <c>3</c> and therefore implies <see cref="Substep"/>.
	/// Mirrors <c>oni_sim::ext::ExtPhaseGate</c>.
	///
	/// <b>"It did not run" is a normal outcome, not an error.</b> A world with no disease never
	/// runs the disease phases; a world that never sent <c>kInjectGasSpecies</c> never runs
	/// mixing; a paused game runs no substeps at all. Anything that must be true every frame has
	/// to be re-established rather than assumed carried -- see
	/// the SimDLL's EXTENSION-POINTS document, §4.2.
	/// </summary>
	[Flags]
	public enum ExtPhaseGate
	{
		/// <summary>Runs on every frame the sim steps at all.</summary>
		None = 0,

		/// <summary>
		/// Skipped entirely on a frame that runs no substeps -- an ordinary frame, not an error:
		/// a paused game, a frame whose elapsed time did not add up to a whole 0.2 s substep, or
		/// the skipped frame a <c>Load</c> inserts.
		/// </summary>
		Substep = 1,

		/// <summary>
		/// Additionally skipped by a world-state condition. Implies <see cref="Substep"/>.
		/// </summary>
		World = 3,
	}

	/// <summary>
	/// THE ORDER THE SIMULATION FRAME RUNS ITS KERNELS IN, read from the DLL rather than
	/// hardcoded. The managed half of <c>SIM_ExtPhaseCount</c> / <c>SIM_ExtPhaseDescribe</c>
	/// (the SimDLL's EXTENSION-POINTS document, §2.5).
	///
	/// <b>What it is for.</b> Every extension point in the ABI is read by exactly one phase, and
	/// what a mod's write does depends on where that phase sits relative to the writes and reads
	/// around it. Before this, a mod author's only source for that ordering was a table in a
	/// markdown file -- so the ordering was, in practice, hardcoded into every consumer, and a
	/// build of the DLL whose order had moved was indistinguishable from one whose had not. This
	/// makes the question answerable at runtime, by the DLL that will actually run the frame.
	///
	/// <b>What the list guarantees, and what it deliberately does not.</b> Guaranteed: the
	/// relative order of these phases within one substep, and each phase's declared scope and
	/// gate. NOT guaranteed, and each omission is load-bearing: order ACROSS REGIONS (the
	/// reservation that keeps region-parallel stepping open), how many substeps a frame runs,
	/// that a gated phase runs at all, and any ordering WITHIN a phase. The header states all
	/// four; they are repeated here because a caller reading this list is exactly the caller at
	/// risk of assuming the other four.
	///
	/// <b>Cheap, lifecycle-free, and callable before a world exists.</b> These are the only two
	/// exports in <c>sim_abi_ext.h</c> that read no world state and take no worker barrier: the
	/// table is compile-time. So unlike <see cref="SimExtCellProperties"/> -- whose indices are
	/// valid for one sim instance and whose registration window opens and closes on every load
	/// -- nothing here has a lifecycle. A mod may call this from <c>OnLoad</c>, before
	/// <c>SIM_Initialize</c>, to check that the DLL it loaded still orders phases the way the
	/// build it was written against did. That property is deliberate and worth preserving; if a
	/// future change would give this a lifecycle, re-read EXTENSION-POINTS.md §2.5 first.
	///
	/// <b>On a stock SimDLL this reports UNAVAILABLE rather than throwing.</b> Klei's DLL does
	/// not export these symbols, so the call raises <see cref="EntryPointNotFoundException"/>
	/// -- the same probe-by-calling shape <see cref="SimVersion"/> uses, since an export cannot
	/// be tested for without calling it. <see cref="Available"/> is the first-class answer;
	/// <see cref="Count"/> returns 0 and <see cref="All"/> an empty array. Nothing here throws.
	/// </summary>
	public static class SimExtPhases
	{
		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern int SIM_ExtPhaseCount();

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtPhaseDescribe(int phaseIdx, byte* outDesc);

		// ext::ExtPhaseDesc, abi/sim_abi_ext.h, `#pragma pack(push, 4)`, static_assert'ed at 44
		// bytes native-side. Field offsets rather than a [StructLayout] mirror, for the same
		// reason SimExtCellProperties and SimExtFrame decode theirs by offset: the layout is an
		// ABI contract stated in one header, and a marshalled struct that drifts from it fails
		// by silently reading the wrong four bytes rather than by not compiling.
		private const int DescBytes = 44;
		private const int DescNameBytes = 32;
		private const int DescOffsetIndex = 32;
		private const int DescOffsetScope = 36;
		private const int DescOffsetGate = 40;

		// ------------------------------------------------------------------ state

		private static Descriptor[] cached;
		private static bool absent;

		/// <summary>
		/// What the DLL publishes about one phase.
		/// </summary>
		public struct Descriptor
		{
			/// <summary>The kernel's own name, e.g. <c>StepConduction</c>.</summary>
			public string Name;

			/// <summary>
			/// Its position in the frame -- the <c>ExtPhase</c> value. Lower runs first WITHIN A
			/// SUBSTEP; see <see cref="SimExtPhases.RunsBefore"/> for the comparison and for what
			/// it does not mean.
			///
			/// Echoed back by the DLL rather than inferred from the loop counter that fetched it,
			/// for the same reason <c>ExtEventStreamDesc.declaredIdx</c> is: a descriptor copied
			/// onto a wire is no longer beside the counter that produced it.
			/// </summary>
			public int Index;

			/// <summary>How often it runs relative to one frame.</summary>
			public ExtPhaseScope Scope;

			/// <summary>Why it might not run on a given frame.</summary>
			public ExtPhaseGate Gate;

			/// <summary>
			/// True when this phase can be skipped on an otherwise healthy frame -- i.e. when
			/// <see cref="Gate"/> is not <see cref="ExtPhaseGate.None"/>. Only
			/// <c>DrainQueue</c> and <c>Project</c> are ungated.
			/// </summary>
			public bool MayBeSkipped
			{
				get { return Gate != ExtPhaseGate.None; }
			}

			/// <summary>
			/// True when a world-state condition can switch this phase off entirely, on top of
			/// the substep gate -- disease switched off, or the gas mixture never used.
			/// </summary>
			public bool SkippedByWorldState
			{
				get { return (Gate & ExtPhaseGate.World) == ExtPhaseGate.World; }
			}

			/// <summary>One line, for a log or a tooltip.</summary>
			public override string ToString()
			{
				return Index.ToString("00") + "  " + Name + " [" + Scope + ", gate " + Gate + "]";
			}
		}

		// ------------------------------------------------------------------ availability

		/// <summary>
		/// Whether this SimDLL publishes a phase table at all: true for a custom SimDLL new
		/// enough to have the phase exports, false for a stock one or an older custom build.
		///
		/// <b>This answers immediately and truthfully, unlike the sibling registries'
		/// <c>Available</c>.</b> Those cannot probe on demand -- calling their exports early is a
		/// lifecycle question, so they report "available" until something has actually failed.
		/// Here there is no lifecycle to disturb: the table is compile-time, takes no barrier and
		/// is legal to read before a world exists, so this property simply asks.
		/// </summary>
		public static bool Available
		{
			get { return Load() != null; }
		}

		/// <summary>
		/// How many phases the DLL publishes, or 0 when there is no phase table.
		///
		/// <b>Do not treat this as a version number.</b> A count that matches proves nothing
		/// about the ORDER, which is the part an extension point depends on -- two builds can
		/// agree on 21 phases and disagree about which one runs third. Compare names in order,
		/// or ask <see cref="IndexOf"/> for the one phase you care about.
		/// </summary>
		public static int Count
		{
			get
			{
				Descriptor[] table = Load();
				return table == null ? 0 : table.Length;
			}
		}

		// ------------------------------------------------------------------ enumeration

		/// <summary>
		/// The whole table, in frame order, or an empty array when there is no phase table.
		///
		/// <b>Enumeration is the point of this class, not a convenience on top of it.</b> A
		/// caller checking that the DLL it loaded still matches the build it was written against
		/// has to walk the list; a caller discovering what this DLL supports has no name to probe
		/// with in the first place. The returned array is a fresh copy each call, so a caller may
		/// sort or keep it.
		/// </summary>
		public static Descriptor[] All()
		{
			Descriptor[] table = Load();
			if (table == null)
			{
				return new Descriptor[0];
			}
			Descriptor[] copy = new Descriptor[table.Length];
			Array.Copy(table, copy, table.Length);
			return copy;
		}

		/// <summary>
		/// One phase by index. False for an out-of-range index or a DLL with no phase table,
		/// leaving <paramref name="descriptor"/> at its default -- the same discipline the
		/// native export holds, which leaves a refused caller's buffer untouched rather than
		/// filling it with something plausible.
		/// </summary>
		public static bool TryDescribe(int phaseIdx, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			Descriptor[] table = Load();
			if (table == null || phaseIdx < 0 || phaseIdx >= table.Length)
			{
				return false;
			}
			descriptor = table[phaseIdx];
			return true;
		}

		/// <summary>
		/// One phase by kernel name, case-sensitively (the names are C++ identifiers and the ABI
		/// spells them exactly). False when no phase of that name is published -- which is the
		/// answer a mod wants when a phase it depends on has been renamed or removed, and is why
		/// this is a <c>Try</c> rather than something that throws.
		/// </summary>
		public static bool TryGetByName(string name, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			int idx = IndexOf(name);
			return idx >= 0 && TryDescribe(idx, out descriptor);
		}

		/// <summary>
		/// The index of a named phase, or -1 when it is not published (including on a DLL with no
		/// phase table).
		/// </summary>
		public static int IndexOf(string name)
		{
			Descriptor[] table = Load();
			if (table == null || string.IsNullOrEmpty(name))
			{
				return -1;
			}
			for (int i = 0; i < table.Length; i++)
			{
				if (table[i].Name == name)
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Whether <paramref name="earlier"/> runs before <paramref name="later"/> WITHIN ONE
		/// SUBSTEP. False if either name is unpublished, and false for a phase compared with
		/// itself.
		///
		/// <b>Read the qualifier.</b> This orders phases inside a substep and says nothing about
		/// two different substeps, two different regions, or two frames. In particular it does
		/// NOT mean "A's effect is visible to B this frame": a queued message drains once per
		/// frame at <c>DrainQueue</c>, so a write sent while the frame is running lands ahead of
		/// the NEXT frame's physics regardless of which phase sent it.
		/// </summary>
		public static bool RunsBefore(string earlier, string later)
		{
			int a = IndexOf(earlier);
			int b = IndexOf(later);
			return a >= 0 && b >= 0 && a < b;
		}

		/// <summary>
		/// The whole table as text, one phase per line, or a single line saying there is none.
		/// For a log, a diagnostic route or a bug report -- the cheapest way for a mod author to
		/// put the DLL's actual ordering next to the ordering they assumed.
		/// </summary>
		public static string Summary()
		{
			Descriptor[] table = Load();
			if (table == null)
			{
				return "no phase table: this SimDLL does not export SIM_ExtPhaseCount "
					+ "(stock SimDLL, or a custom build older than the published phase list)";
			}
			StringBuilder sb = new StringBuilder();
			sb.Append(table.Length).Append(" phases, in frame order:");
			for (int i = 0; i < table.Length; i++)
			{
				sb.Append('\n').Append("  ").Append(table[i].ToString());
			}
			return sb.ToString();
		}

		// ------------------------------------------------------------------ the fetch

		/// <summary>
		/// The table, fetched once and cached, or null when this DLL has none.
		///
		/// <b>Caching the whole table is safe here in a way it would not be for the
		/// registries.</b> A DLL cannot change under a running process (the same fact
		/// <see cref="SimVersion"/> caches on), and this table is compile-time: it is not
		/// world state, so <c>SIM_Initialize</c> tearing down and rebuilding the sim -- which
		/// invalidates every cell-property index -- does not touch it.
		/// </summary>
		private static unsafe Descriptor[] Load()
		{
			if (cached != null)
			{
				return cached;
			}
			if (absent)
			{
				return null;
			}

			int count;
			try
			{
				count = SIM_ExtPhaseCount();
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or one older than the published phase list. A permanent fact
				// about the loaded binary, so it latches: nothing can add an export to a DLL
				// that is already mapped.
				absent = true;
				return null;
			}
			catch (DllNotFoundException e)
			{
				// Deliberately does NOT latch, and this is the one place this class departs from
				// SimExtCellProperties. "The native library was not resolvable at the moment I
				// asked" is not the same fact as "this DLL has no phase table", and caching the
				// first as the second would answer UNAVAILABLE for the rest of the session --
				// SimVersion makes the same argument about its own null. Retried next call.
				FrameworkLog.Warn("SimExtPhases: could not reach the SimDLL: " + e.Message);
				return null;
			}

			if (count <= 0)
			{
				// Exported but empty. Not reachable from any build we ship -- the table is
				// generated from a macro with 21 rows -- but a zero-length table is not a
				// sensible thing to cache as success, and treating it as absent gives every
				// caller the answer it already handles.
				absent = true;
				return null;
			}

			Descriptor[] table = new Descriptor[count];
			byte* raw = stackalloc byte[DescBytes];
			for (int i = 0; i < count; i++)
			{
				int ok;
				try
				{
					ok = SIM_ExtPhaseDescribe(i, raw);
				}
				catch (EntryPointNotFoundException)
				{
					// Count without Describe: a build that published half the pair. Refuse the
					// whole table rather than cache a partial one, so a caller never sees a list
					// shorter than Count claimed.
					FrameworkLog.Warn("SimExtPhases: SIM_ExtPhaseCount reported " + count
						+ " phases but SIM_ExtPhaseDescribe is not exported; treating the phase "
						+ "table as unavailable.");
					absent = true;
					return null;
				}
				if (ok == 0)
				{
					FrameworkLog.Warn("SimExtPhases: SIM_ExtPhaseDescribe refused index " + i
						+ " of " + count + "; treating the phase table as unavailable.");
					absent = true;
					return null;
				}

				IntPtr p = new IntPtr(raw);
				table[i].Name = SimExtRegistry.ReadName(p, 0, DescNameBytes);
				table[i].Index = Marshal.ReadInt32(p, DescOffsetIndex);
				table[i].Scope = (ExtPhaseScope)Marshal.ReadInt32(p, DescOffsetScope);
				table[i].Gate = (ExtPhaseGate)Marshal.ReadInt32(p, DescOffsetGate);
			}

			cached = table;
			return cached;
		}
	}
}
