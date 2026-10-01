using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// One record on the <c>sim.message_refused</c> stream: a message the SimDLL threw away,
	/// and why. Mirrors <c>oni_sim::ext::ExtRefusedMessage</c>, 16 bytes.
	/// </summary>
	public struct ExtRefusedMessage
	{
		/// <summary>The message id as sent -- a <c>SimMessageHashes</c> value, or one of the
		/// framework's own <see cref="OniExtMessages"/> ids.</summary>
		public int MessageId;

		/// <summary>Why it was thrown away.</summary>
		public ExtRefusalReason Reason;

		/// <summary>Bytes that arrived.</summary>
		public int PayloadBytes;

		/// <summary>Bytes the handler needed; 0 when the reason is not about size.</summary>
		public int ExpectedBytes;

		/// <summary>A log-ready sentence naming the id in both decimal and the "ONIx" form the
		/// extension ids are chosen to be recognisable in.</summary>
		public override string ToString()
		{
			string id = MessageId.ToString();
			if ((MessageId & unchecked((int)0xFFFFFF00)) == 0x4F4E4900)
			{
				id += " (\"ONI" + (char)(MessageId & 0xFF) + "\")";
			}
			switch (Reason)
			{
				case ExtRefusalReason.ShortPayload:
					return "message " + id + " refused: payload was " + PayloadBytes
						+ " bytes, the handler needed " + ExpectedBytes;
				case ExtRefusalReason.UnknownMessage:
					return "message " + id + " refused: this SimDLL does not handle that id";
				case ExtRefusalReason.BadTarget:
					return "message " + id + " refused: unregistered property index, "
						+ "out-of-range cell, or a component outside the property's arity (an "
						+ "amount add or a transport change also refuses a property that is not "
						+ "F32, and an add a non-finite amount or a sum below zero)";
				default:
					return "message " + id + " refused: unknown reason " + (int)Reason;
			}
		}
	}

	/// <summary>
	/// REGISTRY 3 OF THREE: per-frame event streams. The sim's own <c>GameDataUpdate</c> carries
	/// ten hardcoded per-frame event lists -- dig info, spawned ore, mass consumed, and so on --
	/// filled by the kernels and read by the game before the next frame. That mechanism works
	/// and cannot grow: <c>GameDataUpdate</c>'s layout is a contract, so an eleventh event type
	/// has nowhere to go. A registered stream is the same shape with the hardcoding removed.
	///
	/// <b>Only NATIVE code declares a stream, and that is the one place the three registries
	/// deliberately do not match.</b> A cell property is storage, so the mod that owns the data
	/// declares it. An event stream is something the SIMULATION observed and is reporting, so it
	/// is declared by whatever produces it -- and everything that produces one is a kernel inside
	/// the SimDLL. There is no register call here and there should not be one: a mod pushing
	/// records in so the sim can hand them back is a loopback with no consumer.
	///
	/// <b>The surface a mod gets is DISCOVER, SUBSCRIBE, READ:</b> <see cref="Find"/>,
	/// <see cref="Subscribe"/>, then <see cref="SimExtFrame"/> for this tick's records.
	///
	/// <b>Subscription is the COLLECTION gate, not a publish filter.</b> An unsubscribed stream
	/// does not buffer a single record, so a stream nobody reads costs one predicted branch at
	/// each emit site and no memory at all. That is stronger than a published cell property's
	/// opt-in, where the data exists either way and only the copy is optional.
	/// </summary>
	public static class SimExtEventStreams
	{
		/// <summary>
		/// <c>ext::kStreamMessageRefused</c> -- the first declared stream, and the reason the
		/// mechanism was built. The SimDLL drops a malformed message in silence at more than
		/// fifty separate sites, so a mod cannot tell a write that landed from a write that was
		/// thrown away, and the symptom is a value that never changes. Subscribe to this while
		/// developing and every refused message becomes a log line.
		/// </summary>
		public const string MessageRefusedStream = "sim.message_refused";

		/// <summary>Bytes per record on <see cref="MessageRefusedStream"/>.</summary>
		public const int RefusedRecordBytes = 16;

		[DllImport("SimDLL", CharSet = CharSet.Ansi)]
		private static extern int SIM_ExtEventStreamIndex(string name);

		[DllImport("SimDLL")]
		private static extern int SIM_ExtEventStreamCount();

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtEventStreamDescribe(int streamIdx, byte* outDesc);

		private static bool unavailable;

		/// <summary>
		/// Whether the stream-discovery export is present. False only after a call has actually
		/// failed, since an export cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// The index of a declared stream, or -1 if this SimDLL declares no such stream.
		///
		/// Stable for the life of the DLL instance -- streams are declared once at
		/// initialisation and never added, removed or reordered -- so resolve a name once rather
		/// than every tick.
		/// </summary>
		public static int Find(string name)
		{
			if (unavailable || string.IsNullOrEmpty(name))
			{
				return -1;
			}
			try
			{
				return SIM_ExtEventStreamIndex(name);
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return -1;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return -1;
			}
		}

		/// <summary>
		/// Start or stop collecting <paramref name="streamIdx"/>. QUEUED: a subscription sent
		/// during tick N first collects on tick N+1 and first appears in the table tick N+1
		/// publishes, so a caller that subscribes and reads immediately gets nothing and has not
		/// hit a bug.
		///
		/// Unsubscribing drops the buffer as well as stopping collection.
		/// </summary>
		public static unsafe void Subscribe(int streamIdx, bool enable)
		{
			if (streamIdx < 0)
			{
				return;
			}
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(streamIdx), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(enable ? 1 : 0), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SubscribeEventStream, payload.Length, msg);
			}
		}

		// `ExtEventStreamDesc`, in `abi/sim_abi_ext.h`. Offsets rather than a
		// [StructLayout] mirror, for the reason SimExtCellProperties.Descriptor gives: the
		// struct is the sim's and a managed copy of it is a second place for the layout to
		// drift. The static_assert on the native side pins the size; DescBytes is the same
		// number, spelled here once.
		private const int DescBytes = 60;
		private const int DescOffsetStride = 48;
		private const int DescOffsetSubscribed = 52;
		private const int DescOffsetDeclaredIdx = 56;

		/// <summary>
		/// One declared stream's registration, as <c>SIM_ExtEventStreamDescribe</c> reports it.
		///
		/// The record LAYOUT is deliberately not here. <see cref="Stride"/> says how big a
		/// record is, never what is in it -- a stream is a contract between its producer and a
		/// consumer that knows the struct (<see cref="ExtRefusedMessage"/> for the first
		/// stream). A generic client can enumerate, subscribe, count and copy bytes without
		/// decoding any of it.
		/// </summary>
		public struct Descriptor
		{
			/// <summary>The declared name, e.g. <c>sim.message_refused</c>.</summary>
			public string Name;

			/// <summary>Bytes per record, fixed at declaration.</summary>
			public int Stride;

			/// <summary>
			/// Whether this stream is currently collecting. Survives an Allocate and a load: a
			/// subscription is a statement about what the caller wants to watch.
			///
			/// QUEUED, so this reads false immediately after <see cref="Subscribe"/> and true
			/// on the next tick -- and it flips one frame BEFORE the stream appears in
			/// <see cref="SimExtFrame.Streams"/>, because the drain that sets it runs inside a
			/// frame whose published table was already built. Seeing true does not mean the
			/// bytes are there yet.
			/// </summary>
			public bool Subscribed;

			/// <summary>
			/// The index to pass to <see cref="Subscribe"/>. Carried rather than re-derived:
			/// a descriptor that has been copied out of the table or sorted by name is no
			/// longer beside the loop counter that produced it.
			/// </summary>
			public int DeclaredIndex;
		}

		/// <summary>
		/// How many event streams this SimDLL declares, so they can be ENUMERATED and not only
		/// probed with <see cref="Find"/> by a name the caller already had.
		///
		/// Takes no barrier: the declaration set is written once at initialisation and never
		/// changes.
		/// </summary>
		public static int Count()
		{
			if (unavailable)
			{
				return 0;
			}
			try
			{
				return SIM_ExtEventStreamCount();
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return 0;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return 0;
			}
		}

		/// <summary>
		/// Fill <paramref name="descriptor"/> for one stream index. False for an out-of-range
		/// index or a SimDLL without the export, leaving the descriptor at its default.
		///
		/// CHEAP BUT NOT FREE: unlike <see cref="Count"/> this takes the sim's worker barrier,
		/// because <see cref="Descriptor.Subscribed"/> is written inside the frame's message
		/// drain and a barrier-free read of it reports the previous frame's answer. Fetch it
		/// when discovering or when a subscription changes -- not every tick. The per-tick path
		/// is <see cref="SimExtFrame.Streams"/>, which takes no barrier.
		/// </summary>
		public static unsafe bool TryDescribe(int streamIdx, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			if (unavailable || streamIdx < 0)
			{
				return false;
			}
			byte* raw = stackalloc byte[DescBytes];
			try
			{
				if (SIM_ExtEventStreamDescribe(streamIdx, raw) == 0)
				{
					return false;
				}
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return false;
			}

			IntPtr p = new IntPtr(raw);
			descriptor.Name = SimExtRegistry.ReadName(p, 0);
			descriptor.Stride = Marshal.ReadInt32(p, DescOffsetStride);
			descriptor.Subscribed = Marshal.ReadInt32(p, DescOffsetSubscribed) != 0;
			descriptor.DeclaredIndex = Marshal.ReadInt32(p, DescOffsetDeclaredIdx);
			return true;
		}

		/// <summary>
		/// Every stream this SimDLL declares, in declaration order. Empty when the SimDLL is
		/// older than stage 4b, which a caller should report as "this build cannot enumerate
		/// streams" rather than as "there are none".
		/// </summary>
		public static List<Descriptor> Describe()
		{
			var streams = new List<Descriptor>();
			int n = Count();
			for (int i = 0; i < n; i++)
			{
				Descriptor d;
				if (TryDescribe(i, out d))
				{
					streams.Add(d);
				}
			}
			return streams;
		}

		/// <summary>
		/// Subscribe to (or unsubscribe from) <see cref="MessageRefusedStream"/>. Returns false
		/// when this SimDLL does not declare it, so a caller can say "diagnostics unavailable"
		/// rather than wondering why nothing ever arrives.
		/// </summary>
		public static bool SubscribeToRefusals(bool enable)
		{
			int idx = Find(MessageRefusedStream);
			if (idx < 0)
			{
				return false;
			}
			Subscribe(idx, enable);
			return true;
		}

		/// <summary>
		/// Decode this tick's <see cref="MessageRefusedStream"/> records, or an empty list when
		/// the stream is not subscribed, nothing was refused, or the frame has moved on.
		///
		/// <b>Read it during the tick it belongs to.</b> The records live in the published frame
		/// and the pointer is valid for that tick only, which is why this decodes into managed
		/// structs immediately -- a refusal list is small and a copy is the right trade. See
		/// <see cref="SimExtFrame"/> for the lifetime rule in full.
		///
		/// A non-zero <see cref="SimExtFrame.PublishedStream.Dropped"/> on the stream means the
		/// list is a PREFIX of what happened, not the whole of it.
		/// </summary>
		public static List<ExtRefusedMessage> ReadRefusals()
		{
			List<ExtRefusedMessage> records = new List<ExtRefusedMessage>();
			SimExtFrame.PublishedStream stream;
			if (!SimExtFrame.TryGetStream(MessageRefusedStream, out stream) || !stream.IsCurrent)
			{
				return records;
			}
			if (stream.Stride != RefusedRecordBytes)
			{
				// A stride that is not what this build expects means the record shape changed
				// under us. Decoding anyway would produce plausible nonsense, so refuse.
				UnityEngine.Debug.LogWarning("[OniFramework] SimExtEventStreams: "
					+ MessageRefusedStream + " has stride " + stream.Stride + ", expected "
					+ RefusedRecordBytes + ". The record shape has changed; not decoding.");
				return records;
			}
			for (int i = 0; i < stream.Count; i++)
			{
				int offset = i * RefusedRecordBytes;
				ExtRefusedMessage record = default(ExtRefusedMessage);
				record.MessageId = Marshal.ReadInt32(stream.Data, offset);
				record.Reason = (ExtRefusalReason)Marshal.ReadInt32(stream.Data, offset + 4);
				record.PayloadBytes = Marshal.ReadInt32(stream.Data, offset + 8);
				record.ExpectedBytes = Marshal.ReadInt32(stream.Data, offset + 12);
				records.Add(record);
			}
			return records;
		}
	}
}
