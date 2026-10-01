using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// THE PER-TICK READ WINDOW onto the two extension registries that publish with the frame:
	/// subscribed cell properties (<see cref="SimExtCellProperties.Publish"/>) and subscribed
	/// event streams (<see cref="SimExtEventStreams.Subscribe"/>).
	///
	/// <b>Why this exists rather than another export.</b> Every per-call route into the SimDLL
	/// opens with a worker-thread barrier. That is cheap for a probe reading one cell and wrong
	/// for anything reading a REGION every frame: the cost is paid per call and the barrier is a
	/// stall hazard mid-frame. The sim already knows what exists and how long it is, so it
	/// publishes a descriptor table with the frame -- exactly the way Klei's own arrays are
	/// published -- and a caller binds once per tick and reads through a span.
	///
	/// <b>THE RULE THAT BITES, and it is Klei's own rule for Grid.mass and every array beside
	/// it:</b>
	/// <code>
	///     THE POINTERS ARE VALID FOR THE TICK AND NOT ACROSS IT.
	/// </code>
	/// The sim publishes two frames alternately, so a pointer held past the tick that produced
	/// it is a use-after-free waiting for the frame after next. Every descriptor this class
	/// hands out carries the <see cref="TickId"/> it was bound on and an
	/// <see cref="PublishedProperty.IsCurrent"/> flag; check it, or copy what you need with
	/// <see cref="PublishedProperty.CopyBytes"/>, and never cache a <c>Data</c> pointer in a
	/// field.
	///
	/// <b>NOT INSTALLED BY DEFAULT.</b> OniFramework ships no Harmony patches of its own -- see
	/// mod.yaml -- so a consumer opts in with one line in its <c>UserMod2.OnLoad</c>:
	/// <code>
	/// public override void OnLoad(Harmony harmony)
	/// {
	///     base.OnLoad(harmony);
	///     OniFramework.SimExtFrame.Install(harmony);
	/// }
	/// </code>
	/// Installing costs one postfix on <c>Sim.HandleMessage</c>, which the game calls a handful
	/// of times per tick, and the postfix returns immediately for every message that is not the
	/// frame. With nothing subscribed the two exports return empty tables, so an installed
	/// binder that nobody uses costs two calls a tick and no copies.
	///
	/// <b>Where the frame pointer comes from.</b> <c>Game.StepTheSim</c> is private and returns
	/// the pointer to its private caller, so there is nothing to read after the fact. The
	/// pointer IS the return of <c>Sim.HandleMessage(SimMessageHashes.PrepareGameData, ...)</c>,
	/// which is public -- so binding there catches the exact frame the game is about to bind its
	/// own arrays to, on the game thread, once per tick.
	/// </summary>
	public static class SimExtFrame
	{
		// Descriptor layouts, from abi/sim_abi_ext.h. Read field by field rather than through a
		// [StructLayout] type: the native structs are `#pragma pack(4)` with a trailing 8-byte
		// pointer and a 48-byte char array, and spelling the offsets out here is both shorter
		// and harder to get subtly wrong than describing that to the marshaller.
		private const int PropertyDescriptorBytes = 80;
		private const int PropertyOffsetType = 48;
		private const int PropertyOffsetPersist = 52;
		private const int PropertyOffsetArity = 56;
		private const int PropertyOffsetStride = 60;
		private const int PropertyOffsetCellCount = 64;
		private const int PropertyOffsetByteCount = 68;
		private const int PropertyOffsetData = 72;

		private const int StreamDescriptorBytes = 72;
		private const int StreamOffsetStride = 48;
		private const int StreamOffsetCount = 52;
		private const int StreamOffsetByteCount = 56;
		private const int StreamOffsetDropped = 60;
		private const int StreamOffsetData = 64;

		/// <summary>
		/// One subscribed cell property as published for this tick. A snapshot of the
		/// descriptor, not of the data: <see cref="Data"/> points into the sim's published frame
		/// and is valid only while <see cref="IsCurrent"/> is true.
		/// </summary>
		public struct PublishedProperty
		{
			/// <summary>The registered <c>&lt;owner&gt;.&lt;property&gt;</c> name. Match on
			/// this -- a position in the table is not a property index.</summary>
			public string Name;

			/// <summary>The scalar each component holds.</summary>
			public ExtScalarType Type;

			/// <summary>What a load owes this property.</summary>
			public ExtPersistence Persist;

			/// <summary>Components per cell. Layout is cell-major.</summary>
			public int Arity;

			/// <summary>Bytes per component, implied by <see cref="Type"/>.</summary>
			public int Stride;

			/// <summary>PADDED cells -- the allocation's count, not the game's grid. See
			/// <see cref="SimExtFrame.PaddedCell"/>.</summary>
			public int CellCount;

			/// <summary><c>CellCount * Arity * Stride</c>. Build a span from this; derive
			/// nothing.</summary>
			public int ByteCount;

			/// <summary>The published bytes. VALID FOR THIS TICK ONLY.</summary>
			public IntPtr Data;

			/// <summary>The tick this descriptor was bound on.</summary>
			public long Tick;

			/// <summary>Whether <see cref="Data"/> still belongs to the tick in progress. False
			/// means the pointer is stale and must not be read.</summary>
			public bool IsCurrent
			{
				get { return Data != IntPtr.Zero && Tick == TickId; }
			}

			/// <summary>
			/// Copy the whole published array into <paramref name="destination"/>, which must be
			/// at least <see cref="ByteCount"/> long. Returns false if the descriptor is stale or
			/// the buffer is too small. This is how a caller keeps values past the tick.
			/// </summary>
			public bool CopyBytes(byte[] destination)
			{
				if (!IsCurrent || destination == null || destination.Length < ByteCount)
				{
					return false;
				}
				Marshal.Copy(Data, destination, 0, ByteCount);
				return true;
			}

			/// <summary>
			/// One component of one PADDED cell, as a float. Returns false if the descriptor is
			/// stale, the property is not <see cref="ExtScalarType.Float32"/>, or the indices are
			/// out of range. Convert a game cell with <see cref="SimExtFrame.PaddedCell"/> first.
			/// </summary>
			public bool TryReadFloat(int paddedCell, out float value, int component = 0)
			{
				value = 0f;
				if (!IsCurrent || Type != ExtScalarType.Float32)
				{
					return false;
				}
				if (paddedCell < 0 || paddedCell >= CellCount || component < 0 || component >= Arity)
				{
					return false;
				}
				int offset = (paddedCell * Arity + component) * Stride;
				int bits = Marshal.ReadInt32(Data, offset);
				value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
				return true;
			}
		}

		/// <summary>
		/// One subscribed event stream as published for this tick. Same lifetime rule as
		/// <see cref="PublishedProperty"/>.
		/// </summary>
		public struct PublishedStream
		{
			/// <summary>The declared stream name. Match on this -- a position in the table is
			/// not a stream index.</summary>
			public string Name;

			/// <summary>Bytes per record, fixed at declaration.</summary>
			public int Stride;

			/// <summary>Records this frame.</summary>
			public int Count;

			/// <summary><c>Count * Stride</c>.</summary>
			public int ByteCount;

			/// <summary>
			/// Records LOST this frame to the stream's per-frame byte cap. Non-zero means what
			/// you are reading is a prefix of what happened -- handing a truncated list back as
			/// if it were complete is exactly what this field exists to prevent.
			/// </summary>
			public int Dropped;

			/// <summary>The published records. VALID FOR THIS TICK ONLY.</summary>
			public IntPtr Data;

			/// <summary>The tick this descriptor was bound on.</summary>
			public long Tick;

			/// <summary>Whether <see cref="Data"/> still belongs to the tick in progress.</summary>
			public bool IsCurrent
			{
				get { return Data != IntPtr.Zero && Tick == TickId; }
			}

			/// <summary>
			/// Copy this frame's records into <paramref name="destination"/>, which must be at
			/// least <see cref="ByteCount"/> long. Returns false if the descriptor is stale or
			/// the buffer is too small.
			/// </summary>
			public bool CopyBytes(byte[] destination)
			{
				if (!IsCurrent || destination == null || destination.Length < ByteCount)
				{
					return false;
				}
				Marshal.Copy(Data, destination, 0, ByteCount);
				return true;
			}
		}

		[DllImport("SimDLL")]
		private static extern IntPtr SIM_ExtPublishedProperties(IntPtr frame, out int count);

		[DllImport("SimDLL")]
		private static extern IntPtr SIM_ExtPublishedEvents(IntPtr frame, out int count);

		private static readonly List<PublishedProperty> properties = new List<PublishedProperty>();
		private static readonly List<PublishedStream> streams = new List<PublishedStream>();
		private static bool installed;
		private static bool unavailable;

		/// <summary>
		/// How many frames have been bound. Increments once per published frame and never
		/// resets, so it doubles as the staleness token every descriptor carries. 0 means
		/// nothing has been bound yet -- the binder is not installed, or no frame has been
		/// published since it was.
		/// </summary>
		public static long TickId { get; private set; }

		/// <summary>
		/// Raised at the end of every bind, once both descriptor tables are readable and before
		/// the game does anything else with the frame. This is the moment a consumer that reads
		/// a published property every tick should do it -- inside the handler the descriptors are
		/// current by construction, and after it they are only current until the next frame.
		///
		/// A handler that throws is caught and logged rather than allowed to take down the
		/// message the game is in the middle of sending.
		/// </summary>
		public static event System.Action Bound;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool Installed
		{
			get { return installed; }
		}

		/// <summary>
		/// Whether the publish-side exports are present. False only after a call has actually
		/// failed, since an export cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// Every subscribed cell property published on the last bound frame, in registration
		/// order. A position in this list is NOT a property index; match on
		/// <see cref="PublishedProperty.Name"/>.
		/// </summary>
		public static IList<PublishedProperty> Properties
		{
			get { return properties.AsReadOnly(); }
		}

		/// <summary>
		/// Every subscribed event stream published on the last bound frame, in declaration
		/// order. A position in this list is NOT a stream index; match on
		/// <see cref="PublishedStream.Name"/>.
		/// </summary>
		public static IList<PublishedStream> Streams
		{
			get { return streams.AsReadOnly(); }
		}

		/// <summary>
		/// The published descriptor for <paramref name="name"/>, if that property is subscribed
		/// and appeared in the last bound frame. Check
		/// <see cref="PublishedProperty.IsCurrent"/> before reading its data.
		/// </summary>
		public static bool TryGetProperty(string name, out PublishedProperty property)
		{
			for (int i = 0; i < properties.Count; i++)
			{
				if (string.Equals(properties[i].Name, name, StringComparison.Ordinal))
				{
					property = properties[i];
					return true;
				}
			}
			property = default(PublishedProperty);
			return false;
		}

		/// <summary>
		/// The published descriptor for <paramref name="name"/>, if that stream is subscribed
		/// and appeared in the last bound frame. Check <see cref="PublishedStream.IsCurrent"/>
		/// before reading its data.
		/// </summary>
		public static bool TryGetStream(string name, out PublishedStream stream)
		{
			for (int i = 0; i < streams.Count; i++)
			{
				if (string.Equals(streams[i].Name, name, StringComparison.Ordinal))
				{
					stream = streams[i];
					return true;
				}
			}
			stream = default(PublishedStream);
			return false;
		}

		/// <summary>
		/// The padded-grid index of a game cell. Extension storage is sized by the sim's
		/// allocation, which carries a one-cell border on every side, so a published array is
		/// <c>(width + 2) * (height + 2)</c> long and game cell <c>(x, y)</c> sits at
		/// <c>(y + 1) * (width + 2) + (x + 1)</c>. Reading a published array with a game cell
		/// index does not fail -- it silently returns a neighbour's value -- which is why this
		/// conversion is spelled out rather than left to each caller.
		/// </summary>
		public static int PaddedCell(int gameCell)
		{
			int width = Grid.WidthInCells;
			if (width <= 0 || gameCell < 0)
			{
				return -1;
			}
			int x = gameCell % width;
			int y = gameCell / width;
			return (y + 1) * (width + 2) + (x + 1);
		}

		/// <summary>
		/// Install the per-tick binder. Idempotent -- a second call with the same or a different
		/// Harmony instance is a no-op, so several mods can each ask for it.
		/// </summary>
		/// <param name="harmony">The calling mod's Harmony instance, normally the one handed to
		/// <c>UserMod2.OnLoad</c>.</param>
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

			MethodInfo handleMessage = AccessTools.Method(typeof(global::Sim), "HandleMessage",
				new Type[] { typeof(SimMessageHashes), typeof(int), typeof(byte[]) });
			if (handleMessage == null)
			{
				throw new InvalidOperationException(
					"SimExtFrame.Install could not find Sim.HandleMessage(SimMessageHashes, int, byte[]). "
					+ "The game build has moved and the binder would silently never fire, so this "
					+ "throws instead. Re-check the member against the current game assembly before shipping.");
			}
			harmony.Patch(handleMessage, null, new HarmonyMethod(
				AccessTools.Method(typeof(SimExtFrame), "HandleMessagePostfix")));

			installed = true;
		}

		// Not attributed. OniFramework must never patch anything merely by being loaded, and an
		// attributed class would be swept up by a consumer's Harmony.PatchAll(assembly).
		// Install() wires this by hand instead -- the same idiom PowerHeat uses.
		//
		// Fires for every message the game sends through the managed Sim.HandleMessage wrapper,
		// which is a handful per tick (most sim messages go through SimMessages' own direct
		// P/Invoke), and returns immediately for all but the frame.
		private static void HandleMessagePostfix(SimMessageHashes sim_msg_id, IntPtr __result)
		{
			if (sim_msg_id != SimMessageHashes.PrepareGameData || __result == IntPtr.Zero)
			{
				return;
			}
			Bind(__result);
		}

		/// <summary>
		/// Rebind both descriptor tables against <paramref name="frame"/>, the pointer
		/// <c>PrepareGameData</c> just returned. Public so a harness that drives the sim itself
		/// -- rather than letting the game step it -- can bind without installing the patch;
		/// ordinary mods should call <see cref="Install"/> and never this.
		/// </summary>
		public static void Bind(IntPtr frame)
		{
			TickId++;
			properties.Clear();
			streams.Clear();

			if (unavailable || frame == IntPtr.Zero)
			{
				return;
			}

			try
			{
				int count;
				IntPtr table = SIM_ExtPublishedProperties(frame, out count);
				// Null with count 0 is a REFUSAL -- a stale or unrecognised frame -- and not the
				// same fact as a live frame with nothing subscribed, which comes back non-null.
				// Both leave the list empty here; the distinction matters to the sim, not to a
				// reader who has just been told there is nothing to read.
				if (table != IntPtr.Zero && count > 0)
				{
					for (int i = 0; i < count; i++)
					{
						IntPtr descriptor = new IntPtr(table.ToInt64() + i * PropertyDescriptorBytes);
						PublishedProperty p = default(PublishedProperty);
						p.Name = SimExtRegistry.ReadName(descriptor, 0);
						p.Type = (ExtScalarType)Marshal.ReadInt32(descriptor, PropertyOffsetType);
						p.Persist = (ExtPersistence)Marshal.ReadInt32(descriptor, PropertyOffsetPersist);
						p.Arity = Marshal.ReadInt32(descriptor, PropertyOffsetArity);
						p.Stride = Marshal.ReadInt32(descriptor, PropertyOffsetStride);
						p.CellCount = Marshal.ReadInt32(descriptor, PropertyOffsetCellCount);
						p.ByteCount = Marshal.ReadInt32(descriptor, PropertyOffsetByteCount);
						p.Data = Marshal.ReadIntPtr(descriptor, PropertyOffsetData);
						p.Tick = TickId;
						properties.Add(p);
					}
				}

				IntPtr streamTable = SIM_ExtPublishedEvents(frame, out count);
				if (streamTable != IntPtr.Zero && count > 0)
				{
					for (int i = 0; i < count; i++)
					{
						IntPtr descriptor = new IntPtr(streamTable.ToInt64() + i * StreamDescriptorBytes);
						PublishedStream s = default(PublishedStream);
						s.Name = SimExtRegistry.ReadName(descriptor, 0);
						s.Stride = Marshal.ReadInt32(descriptor, StreamOffsetStride);
						s.Count = Marshal.ReadInt32(descriptor, StreamOffsetCount);
						s.ByteCount = Marshal.ReadInt32(descriptor, StreamOffsetByteCount);
						s.Dropped = Marshal.ReadInt32(descriptor, StreamOffsetDropped);
						s.Data = Marshal.ReadIntPtr(descriptor, StreamOffsetData);
						s.Tick = TickId;
						streams.Add(s);
					}
				}
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or a custom one too old to have it. Latched so the
				// binder stops paying for two failed calls every tick.
				unavailable = true;
				Debug.Log("[OniFramework] SimExtFrame: this SimDLL publishes no extension tables; "
					+ "the per-tick binder is now inert.");
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
			}

			System.Action bound = Bound;
			if (bound != null)
			{
				try
				{
					bound();
				}
				catch (Exception e)
				{
					// A subscriber's bug must not propagate out of Sim.HandleMessage: the game is
					// mid-message and the exception would surface as a sim failure rather than as
					// the mod's own.
					FrameworkLog.Error("SimExtFrame: a Bound handler threw: " + e);
				}
			}
		}
	}
}
