using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OniFramework
{
	/// <summary>
	/// WHEN A MESSAGE TAKES EFFECT, and the only field on a descriptor that changes the SHAPE of
	/// a caller's code rather than its vocabulary. Mirrors <c>oni_sim::ext::ExtMessageDelivery</c>
	/// in <c>abi/sim_abi_ext.h</c>; the native values are the wire contract and these must not be
	/// renumbered.
	/// </summary>
	public enum ExtMessageDelivery
	{
		/// <summary>
		/// Copied onto the game thread's queue and applied by the NEXT frame's <c>DrainQueue</c>.
		/// <b>A read-back in the same tick sees the value the message replaced, not the one it
		/// sent.</b> The default, and 19 of the 23 ids hold it: these messages change the world,
		/// and a world change that landed mid-frame would be a change the frame the game is
		/// currently holding did not contain.
		/// </summary>
		Queued = 0,

		/// <summary>
		/// Applied inside <c>SIM_HandleMessage</c>, on the calling thread, behind
		/// <c>WaitIdle()</c>, before the call returns. A read-back in the same tick sees the new
		/// value. Four ids: the two registrations, which have to be (registration closes at the
		/// first <c>SIM_AllocateCells</c>, so a queued one would drain after the array it wanted
		/// to declare had been sized), and the two element-attribute writes, which were made
		/// immediate after the tick of latency turned out to be a race against exports that take
		/// no worker barrier.
		/// </summary>
		Immediate = 1,
	}

	/// <summary>
	/// WHAT KIND OF THING AN ID IS. Mirrors <c>oni_sim::ext::ExtMessageClass</c>; the native
	/// values are the wire contract and must not be renumbered.
	///
	/// <b>Published because the count alone misleads.</b> Only <see cref="Parameter"/> and
	/// <see cref="Operation"/> extend the simulation's behaviour — 11 of the 23 ids. The other
	/// twelve are the storage plumbing the parameters are built on, and run-state restoration
	/// that is not an extension point at all.
	/// </summary>
	public enum ExtMessageClass
	{
		/// <summary>
		/// Data a named phase reads while stepping — the shape the extension contract is written
		/// around. 8 of 23.
		/// </summary>
		Parameter = 0,

		/// <summary>
		/// An action applied during the drain. Changes the grid once and is not read again.
		/// 3 of 23.
		/// </summary>
		Operation = 1,

		/// <summary>
		/// The extensible-storage plumbing itself: declare an array, write it, open a read window
		/// or an event stream. 6 of 23. The mechanism the parameters are built on rather than
		/// extensions in their own right.
		/// </summary>
		Store = 2,

		/// <summary>
		/// <b>NOT AN EXTENSION POINT.</b> Restores run state a save blob cannot carry, so that a
		/// replayed run resumes identically. 6 of 23, and the reason "23 extension points"
		/// overstates the surface by more than half.
		/// </summary>
		Checkpoint = 3,
	}

	/// <summary>
	/// THE MESSAGE SURFACE THE SIMDLL ACTUALLY IMPLEMENTS, read from the DLL that is going to
	/// dispatch it. The managed half of <c>SIM_ExtMessageCount</c> /
	/// <c>SIM_ExtMessageDescribe</c> (the SimDLL's EXTENSION-POINTS document, §3.1), and
	/// the sibling of <see cref="SimExtPhases"/> in every respect.
	///
	/// <b>What it is for.</b> A message id is a number a caller puts on a wire. Whether the write
	/// it carries can be read back in the same tick, which phase consumes it, and whether it
	/// extends the simulation at all were, before this, facts recorded only in a markdown table
	/// and in the comments on <c>OniExtMessages</c>. A build that had moved one of them was
	/// indistinguishable from one that had not. This makes all four answerable at runtime,
	/// against the binary that will run the frame.
	///
	/// <b><see cref="Delivery"/> is the field to read first.</b> Class and phase are vocabulary;
	/// delivery changes the shape of the calling code. Nineteen of the twenty-three ids are
	/// <see cref="ExtMessageDelivery.Queued"/>, so a send followed by a read in the same tick
	/// returns the OLD value — a caller that assumes otherwise writes something that works in a
	/// rig with a tick between the two and fails in a mod that does not have one.
	///
	/// <b>Two facades that are useless apart.</b> <see cref="Descriptor.Phase"/> is an index into
	/// <see cref="SimExtPhases"/>'s list, or <see cref="PhaseUnpublished"/>. Knowing that
	/// <c>kSetCellThermalMassBonus</c> is read by phase 4 is worth nothing without the list that
	/// says phase 4 is <c>StepConduction</c> and that <c>DrainQueue</c> runs before it; see
	/// <see cref="PhaseName(Descriptor)"/>, which resolves it for you.
	///
	/// <b>Cheap, lifecycle-free, and callable before a world exists.</b> These and the phase pair
	/// are the only exports in <c>sim_abi_ext.h</c> that read no world state and take no worker
	/// barrier: the table is compile-time. A mod may call this from <c>OnLoad</c>, before
	/// <c>SIM_Initialize</c>, to check that the DLL it loaded still delivers the message it was
	/// built against the way it was built to expect — which is the only point at which it can
	/// still decline to load.
	///
	/// <b>On a stock SimDLL this reports UNAVAILABLE rather than throwing.</b> Klei's DLL does not
	/// export these symbols, so the call raises <see cref="EntryPointNotFoundException"/> — the
	/// same probe-by-calling shape <see cref="SimVersion"/> uses, since an export cannot be tested
	/// for without calling it. <see cref="Available"/> is the first-class answer;
	/// <see cref="Count"/> returns 0 and <see cref="All"/> an empty array. Nothing here throws.
	/// </summary>
	public static class SimExtMessages
	{
		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern int SIM_ExtMessageCount();

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtMessageDescribe(int msgIdx, byte* outDesc);

		// OniExtMessageDesc, abi/sim_ext_api.h, `#pragma pack(push, 4)`, static_assert'ed at 60
		// bytes native-side. Field offsets rather than a [StructLayout] mirror, for the same
		// reason SimExtPhases decodes its descriptor by offset: the layout is an ABI contract
		// stated in one header, and a marshalled struct that drifts from it fails by silently
		// reading the wrong four bytes rather than by not compiling.
		private const int DescBytes = 60;
		private const int DescNameBytes = 40;
		private const int DescOffsetIndex = 40;
		private const int DescOffsetId = 44;
		private const int DescOffsetClass = 48;
		private const int DescOffsetDelivery = 52;
		private const int DescOffsetPhase = 56;

		/// <summary>
		/// <see cref="Descriptor.Phase"/> when no single published phase reads the message:
		/// several do, the reader is the caller's own code, or nothing reads it in a step at all.
		/// <c>oni_sim::ext::kPhaseUnpublished</c>.
		///
		/// Deliberately one value rather than three — <see cref="Descriptor.Class"/> already
		/// separates those cases, and a caller that has to switch on a sentinel to learn something
		/// the neighbouring field already told it is a worse ABI than one that says "ask the docs".
		/// </summary>
		public const int PhaseUnpublished = -1;

		// ------------------------------------------------------------------ state

		private static Descriptor[] cached;
		private static bool absent;

		/// <summary>
		/// What the DLL publishes about one extension message id.
		/// </summary>
		public struct Descriptor
		{
			/// <summary>
			/// The C++ constant's own name, <c>k</c>-prefixed: <c>kSetBuildingExhaust</c>.
			///
			/// <b>Not the spelling of any other copy of the id.</b> The C header
			/// (<c>abi/sim_ext_api.h</c>) publishes the same id as
			/// <c>ONI_MSG_SET_BUILDING_EXHAUST</c> and the managed constant in
			/// <c>OniExtMessages</c> is the unprefixed <c>SetBuildingExhaust</c>. Anything
			/// comparing this against a managed field name has to add the <c>k</c>.
			/// </summary>
			public string Name;

			/// <summary>
			/// Its position in the table, echoed back by the DLL rather than inferred from the
			/// loop counter that fetched it — the same discipline <c>ExtPhaseDesc::index</c> and
			/// <c>ExtEventStreamDesc::declaredIdx</c> hold, because a descriptor copied onto a
			/// wire is no longer beside the counter that produced it.
			///
			/// <b>This is not the id.</b> See <see cref="Id"/>.
			/// </summary>
			public int Index;

			/// <summary>
			/// THE WIRE ID — what <c>SIM_HandleMessage</c> takes, and what the constants in
			/// <c>OniExtMessages</c> hold.
			///
			/// Not the index, and the distinction is load-bearing: an id survives a row being
			/// inserted above it, an index does not. Look messages up by this
			/// (<see cref="TryGetById"/>), never by position.
			/// </summary>
			public int Id;

			/// <summary>What kind of thing the id is — and whether it is an extension point at
			/// all.</summary>
			public ExtMessageClass Class;

			/// <summary>When the message takes effect.</summary>
			public ExtMessageDelivery Delivery;

			/// <summary>
			/// The index, in <see cref="SimExtPhases"/>'s list, of the phase that reads this
			/// message — or <see cref="SimExtMessages.PhaseUnpublished"/>. Resolve it with
			/// <see cref="SimExtMessages.PhaseName(Descriptor)"/>.
			/// </summary>
			public int Phase;

			/// <summary>
			/// True when a read-back in the same tick sees what this message sent. False for the
			/// 19 queued ids, where it sees the value the message replaced.
			/// </summary>
			public bool ReadableSameTick
			{
				get { return Delivery == ExtMessageDelivery.Immediate; }
			}

			/// <summary>
			/// True when this id extends what the simulation DOES —
			/// <see cref="ExtMessageClass.Parameter"/> or <see cref="ExtMessageClass.Operation"/>.
			/// False for the storage plumbing and for the checkpoint restores, which exist so a
			/// replayed run resumes identically and are not an extension point.
			/// </summary>
			public bool IsExtensionPoint
			{
				get
				{
					return Class == ExtMessageClass.Parameter
						|| Class == ExtMessageClass.Operation;
				}
			}

			/// <summary>
			/// True when exactly one published phase reads this message, so
			/// <see cref="Phase"/> indexes <see cref="SimExtPhases"/>.
			/// </summary>
			public bool HasPublishedPhase
			{
				get { return Phase != SimExtMessages.PhaseUnpublished; }
			}

			/// <summary>One line, for a log or a bug report. Self-contained: the phase is the
			/// index, not the name, because resolving the name needs the other table and a
			/// <c>ToString</c> that silently reaches into a second facade is a surprise. Use
			/// <see cref="SimExtMessages.Summary"/> for the resolved form.</summary>
			public override string ToString()
			{
				return Index.ToString("00") + "  " + Name
					+ "  id 0x" + Id.ToString("X8")
					+ " [" + Class + ", " + Delivery
					+ ", " + (HasPublishedPhase ? "read by phase " + Phase : "no published phase")
					+ "]";
			}
		}

		// ------------------------------------------------------------------ availability

		/// <summary>
		/// Whether this SimDLL publishes a message table at all: true for a custom SimDLL new
		/// enough to have the message-surface exports, false for a stock one or an older custom
		/// build.
		///
		/// <b>This answers immediately and truthfully, unlike the registries'
		/// <c>Available</c>.</b> Those cannot probe on demand — calling their exports early is a
		/// lifecycle question, so they report "available" until something has actually failed.
		/// Here there is no lifecycle to disturb: the table is compile-time, takes no barrier and
		/// is legal to read before a world exists, so this property simply asks.
		/// </summary>
		public static bool Available
		{
			get { return Load() != null; }
		}

		/// <summary>
		/// How many extension message ids the DLL publishes, or 0 when there is no message table.
		///
		/// <b>Do not treat this as a version number, and do not read it as "how much this DLL
		/// extends the simulation".</b> Twelve of the twenty-three ids are storage plumbing or
		/// checkpoint restores; count <see cref="Descriptor.IsExtensionPoint"/> if that is the
		/// question. And a count that matches proves nothing about any individual id's delivery
		/// or phase, which is the part a caller's code shape depends on — ask
		/// <see cref="TryGetById"/> about the one id you use.
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
		/// The whole table, in table order, or an empty array when there is no message table.
		///
		/// <b>Enumeration is the point of this class, not a convenience on top of it.</b> A caller
		/// checking that the DLL it loaded still matches the build it was written against has to
		/// walk the list; a caller discovering what this DLL supports has no id to probe with in
		/// the first place. The returned array is a fresh copy each call, so a caller may sort or
		/// keep it.
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
		/// One message by TABLE POSITION. False for an out-of-range index or a DLL with no message
		/// table, leaving <paramref name="descriptor"/> at its default — the same discipline the
		/// native export holds, which leaves a refused caller's buffer untouched rather than
		/// filling it with something plausible.
		///
		/// <b>Most callers want <see cref="TryGetById"/> instead</b>, because the thing a caller
		/// holds is an <c>OniExtMessages</c> constant, which is an id and not a position.
		/// </summary>
		public static bool TryDescribe(int msgIdx, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			Descriptor[] table = Load();
			if (table == null || msgIdx < 0 || msgIdx >= table.Length)
			{
				return false;
			}
			descriptor = table[msgIdx];
			return true;
		}

		/// <summary>
		/// One message BY WIRE ID — the lookup that matches what a caller actually holds. False
		/// when this DLL does not implement that id, which is the answer a mod wants when the
		/// message it depends on has been removed, and is why this is a <c>Try</c> rather than
		/// something that throws.
		/// </summary>
		public static bool TryGetById(int wireId, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			int idx = IndexOfId(wireId);
			return idx >= 0 && TryDescribe(idx, out descriptor);
		}

		/// <summary>
		/// One message by constant name, case-sensitively and <b><c>k</c>-prefixed</b> — the names
		/// are C++ identifiers and the ABI spells them exactly, so this wants
		/// <c>"kSetBuildingExhaust"</c>. False when no message of that name is published.
		/// </summary>
		public static bool TryGetByName(string name, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			int idx = IndexOf(name);
			return idx >= 0 && TryDescribe(idx, out descriptor);
		}

		/// <summary>
		/// The table position of a named message, or -1 when it is not published (including on a
		/// DLL with no message table). See <see cref="Descriptor.Name"/> for the spelling.
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
		/// The table position of a wire id, or -1 when this DLL does not implement it.
		/// </summary>
		public static int IndexOfId(int wireId)
		{
			Descriptor[] table = Load();
			if (table == null)
			{
				return -1;
			}
			for (int i = 0; i < table.Length; i++)
			{
				if (table[i].Id == wireId)
				{
					return i;
				}
			}
			return -1;
		}

		// ------------------------------------------------------------------ the useful questions

		/// <summary>
		/// WHEN THIS ID TAKES EFFECT, keyed on the wire id a caller holds — the one question a
		/// caller actually has, which is "can I read this back in the same tick".
		///
		/// <b>Null means "this DLL does not implement that id", which includes having no message
		/// table at all — and that is deliberately NOT the same answer as
		/// <see cref="ExtMessageDelivery.Queued"/>.</b> Queued is a promise that the write lands
		/// at the next drain; null is no promise of anything, because on a stock SimDLL an
		/// unrecognised id is dropped. Returning the zero-valued enum for both would spell "your
		/// message will arrive one tick late" for a message that will never arrive.
		/// </summary>
		public static ExtMessageDelivery? Delivery(int wireId)
		{
			Descriptor descriptor;
			if (!TryGetById(wireId, out descriptor))
			{
				return null;
			}
			return descriptor.Delivery;
		}

		/// <summary>
		/// The name of the phase that reads a message, resolved through
		/// <see cref="SimExtPhases"/>. Null when the message has no single published phase
		/// (<see cref="PhaseUnpublished"/>), and null when the phase table itself is unavailable
		/// or too short for the index — the two facades ship together, but a caller should not
		/// have to assume that to read this safely.
		/// </summary>
		public static string PhaseName(Descriptor descriptor)
		{
			if (!descriptor.HasPublishedPhase)
			{
				return null;
			}
			SimExtPhases.Descriptor phase;
			if (!SimExtPhases.TryDescribe(descriptor.Phase, out phase))
			{
				return null;
			}
			return phase.Name;
		}

		/// <summary>
		/// <see cref="PhaseName(Descriptor)"/> for a wire id. Null when the id is not published
		/// either.
		/// </summary>
		public static string PhaseName(int wireId)
		{
			Descriptor descriptor;
			if (!TryGetById(wireId, out descriptor))
			{
				return null;
			}
			return PhaseName(descriptor);
		}

		/// <summary>
		/// The whole table as text, one message per line with its phase RESOLVED TO A NAME, or a
		/// single line saying there is none. For a log, a diagnostic route or a bug report — the
		/// cheapest way for a mod author to put the DLL's actual message surface next to the one
		/// they assumed.
		/// </summary>
		public static string Summary()
		{
			Descriptor[] table = Load();
			if (table == null)
			{
				return "no message table: this SimDLL does not export SIM_ExtMessageCount "
					+ "(stock SimDLL, or a custom build older than the published message surface)";
			}

			int queued = 0, extensionPoints = 0;
			for (int i = 0; i < table.Length; i++)
			{
				if (table[i].Delivery == ExtMessageDelivery.Queued)
				{
					queued++;
				}
				if (table[i].IsExtensionPoint)
				{
					extensionPoints++;
				}
			}

			StringBuilder sb = new StringBuilder();
			sb.Append(table.Length).Append(" extension messages (")
				.Append(queued).Append(" queued, ")
				.Append(table.Length - queued).Append(" immediate; ")
				.Append(extensionPoints).Append(" of them extension points, the rest storage "
					+ "plumbing and checkpoint restores):");
			for (int i = 0; i < table.Length; i++)
			{
				string phase = PhaseName(table[i]);
				sb.Append('\n').Append("  ").Append(table[i].ToString());
				if (phase != null)
				{
					sb.Append(" = ").Append(phase);
				}
			}
			return sb.ToString();
		}

		// ------------------------------------------------------------------ the fetch

		/// <summary>
		/// The table, fetched once and cached, or null when this DLL has none.
		///
		/// <b>Caching the whole table is safe here in a way it would not be for the
		/// registries.</b> A DLL cannot change under a running process (the same fact
		/// <see cref="SimVersion"/> caches on), and this table is compile-time: it is generated
		/// from <c>ONI_EXT_MESSAGE_LIST</c> at build time and is not world state, so
		/// <c>SIM_Initialize</c> tearing down and rebuilding the sim — which invalidates every
		/// cell-property index — does not touch it.
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
				count = SIM_ExtMessageCount();
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or one older than the published message surface. A permanent
				// fact about the loaded binary, so it latches: nothing can add an export to a DLL
				// that is already mapped.
				absent = true;
				return null;
			}
			catch (DllNotFoundException e)
			{
				// Deliberately does NOT latch, for the reason SimExtPhases gives at the same
				// line: "the native library was not resolvable at the moment I asked" is not the
				// same fact as "this DLL has no message table", and caching the first as the
				// second would answer UNAVAILABLE for the rest of the session. Retried next call.
				FrameworkLog.Warn("SimExtMessages: could not reach the SimDLL: " + e.Message);
				return null;
			}

			if (count <= 0)
			{
				// Exported but empty. Not reachable from any build we ship -- the table is
				// generated from a macro with 23 rows -- but a zero-length table is not a
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
					ok = SIM_ExtMessageDescribe(i, raw);
				}
				catch (EntryPointNotFoundException)
				{
					// Count without Describe: a build that published half the pair. Refuse the
					// whole table rather than cache a partial one, so a caller never sees a list
					// shorter than Count claimed.
					FrameworkLog.Warn("SimExtMessages: SIM_ExtMessageCount reported " + count
						+ " messages but SIM_ExtMessageDescribe is not exported; treating the "
						+ "message table as unavailable.");
					absent = true;
					return null;
				}
				if (ok == 0)
				{
					FrameworkLog.Warn("SimExtMessages: SIM_ExtMessageDescribe refused index " + i
						+ " of " + count + "; treating the message table as unavailable.");
					absent = true;
					return null;
				}

				IntPtr p = new IntPtr(raw);
				table[i].Name = SimExtRegistry.ReadName(p, 0, DescNameBytes);
				table[i].Index = Marshal.ReadInt32(p, DescOffsetIndex);
				table[i].Id = Marshal.ReadInt32(p, DescOffsetId);
				table[i].Class = (ExtMessageClass)Marshal.ReadInt32(p, DescOffsetClass);
				table[i].Delivery = (ExtMessageDelivery)Marshal.ReadInt32(p, DescOffsetDelivery);
				table[i].Phase = Marshal.ReadInt32(p, DescOffsetPhase);
			}

			cached = table;
			return cached;
		}
	}
}
