using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace OniFramework
{
	/// <summary>Why a liquid's payload left the grid. Mirrors
	/// <c>oni_sim::ext::LiquidPayloadReleaseReason</c>.</summary>
	public enum LiquidPayloadReleaseReason
	{
		/// <summary>The cell was cleared under its liquid: a tiny cell evaporated, or a
		/// displaced merge emptied it.</summary>
		Cleared = 0,

		/// <summary>The liquid froze or boiled.</summary>
		PhaseChange = 1,

		/// <summary>The liquid off-gassed part of itself.</summary>
		OffGas = 2,

		/// <summary>The liquid left the grid as a falling particle.</summary>
		Falling = 3,

		/// <summary>A sub-gram wisp evaporated or merged away.</summary>
		Wisp = 4,

		/// <summary>A message replaced the liquid with another element.</summary>
		Replaced = 5,

		/// <summary>A message removed liquid mass, or a consumer with no callback took it.</summary>
		Removed = 6,

		/// <summary>Vanilla deleted the liquid (a blocked add).</summary>
		Deleted = 7,

		/// <summary>The sim emptied a massless cell.</summary>
		Massless = 8,

		/// <summary>The LIQUID did not leave: the gas did. The cell held more dissolved gas than
		/// the pressure on it can keep in solution, and fizzed (<see cref="Effervescence"/>). The
		/// cell named is still liquid.</summary>
		Effervescence = 9,

		/// <summary>The LIQUID did not leave, and no bubble formed: the gas crossed the liquid's
		/// free surface, because the top cell held more than the PARTIAL pressure of that gas
		/// above keeps in solution (<see cref="Effervescence.SetSurfaceExchange"/>).
		/// The cell named is the top liquid cell; the gas goes into the cell above it.</summary>
		Surface = 10,

		/// <summary>NOT a release: the liquid's free surface DISSOLVED gas out of the cell above
		/// it (<see cref="Effervescence.SetSurfaceExchange"/>). The record's
		/// <c>Amount</c> is NEGATIVE, minus the kilograms absorbed, and the sim has already moved
		/// the gas: a handler counts it and puts nothing anywhere. <c>TemperatureK</c> is the gas
		/// cell's. The cell named is the top liquid cell.</summary>
		Absorbed = 11,
	}

	/// <summary>Who took liquid, and its payload, off the grid. Mirrors
	/// <c>oni_sim::ext::LiquidPayloadConsumerKind</c>.</summary>
	public enum LiquidPayloadConsumerKind
	{
		/// <summary>An <c>ElementConsumer</c>; the record's id is its sim handle.</summary>
		ElementConsumer = 0,

		/// <summary>A <c>MassConsumption</c> message; the record's id is its callback index.</summary>
		MassConsumption = 1,
	}

	/// <summary>One record on <c>sim.liquid_payload_released</c>: payload whose liquid left
	/// the grid with nobody to hand it to. 24 bytes.</summary>
	public struct LiquidPayloadReleased
	{
		/// <summary>The GAME cell the liquid was in.</summary>
		public int Cell;

		/// <summary>The property it was, as registered.</summary>
		public int PropertyIdx;

		/// <summary>The component (lane) of that property.</summary>
		public int Component;

		/// <summary>How much, in the property's own unit.</summary>
		public float Amount;

		/// <summary>The liquid's temperature as it left.</summary>
		public float TemperatureK;

		/// <summary>Why it left.</summary>
		public LiquidPayloadReleaseReason Reason;
	}

	/// <summary>One record on <c>sim.liquid_payload_consumed</c>: payload a consumer took with
	/// its liquid. 24 bytes.</summary>
	public struct LiquidPayloadConsumed
	{
		/// <summary>Which kind of consumer; says what <see cref="Id"/> means.</summary>
		public LiquidPayloadConsumerKind Kind;

		/// <summary>The consumer: an ElementConsumer's sim handle, or a MassConsumption
		/// message's callback index.</summary>
		public int Id;

		/// <summary>The property it was, as registered.</summary>
		public int PropertyIdx;

		/// <summary>The component (lane) of that property.</summary>
		public int Component;

		/// <summary>How much, in the property's own unit, summed over every cell this consumer
		/// took from this tick.</summary>
		public float Amount;

		/// <summary>The temperature of what the consumer took.</summary>
		public float TemperatureK;
	}

	/// <summary>
	/// THE EXITS for every cell property flagged
	/// <see cref="CellPropertyTransport.FollowsLiquidMass"/>. Such a property is an amount carried by
	/// liquid, and the sim moves it with the liquid through every liquid kernel. When the liquid
	/// leaves the grid, its share leaves on one of two streams:
	/// <list type="bullet">
	/// <item><see cref="ReleasedStream"/>: nobody took it. The liquid boiled, froze, was
	/// replaced, fell, evaporated or was removed by a message. The record names the cell.</item>
	/// <item><see cref="ConsumedStream"/>: a consumer took it -- an <c>ElementConsumer</c> or a
	/// <c>MassConsumption</c> -- and the record names the consumer, beside the
	/// <c>ConsumedMassInfo</c> the game already gets for the liquid itself.</item>
	/// </list>
	/// A record neither stream took (nobody subscribed, or the per-frame cap) is not lost: it is
	/// added to <see cref="TryGetUnreported"/>'s running total. So for any carried property:
	/// <code>
	///     on the grid + released + consumed + unreported == everything ever added
	/// </code>
	/// which vftest checks on every tick.
	///
	/// A third-party mod gets this for its own payload by registering an F32 property and
	/// calling <see cref="SimExtCellProperties.SetTransport"/>; <see cref="DissolvedGas"/> is the
	/// first-party user. <see cref="Install"/> subscribes both streams and raises
	/// <see cref="Released"/> and <see cref="Consumed"/> once per record, for every property,
	/// during <see cref="SimExtFrame.Bound"/>.
	/// </summary>
	public static class LiquidPayload
	{
		/// <summary><c>ext::kStreamLiquidPayloadReleased</c>.</summary>
		public const string ReleasedStream = "sim.liquid_payload_released";

		/// <summary><c>ext::kStreamLiquidPayloadConsumed</c>.</summary>
		public const string ConsumedStream = "sim.liquid_payload_consumed";

		/// <summary>Bytes per record, on both streams.</summary>
		public const int RecordBytes = 24;

		/// <summary>
		/// One released record, for any carried property. Raised on the game thread, inside
		/// <see cref="SimExtFrame.Bound"/>; filter on <see cref="LiquidPayloadReleased.PropertyIdx"/>.
		/// </summary>
		public static event Action<LiquidPayloadReleased> Released;

		/// <summary>One consumed record, for any carried property. Same thread and moment as
		/// <see cref="Released"/>.</summary>
		public static event Action<LiquidPayloadConsumed> Consumed;

		/// <summary>Records lost to a stream's per-frame byte cap since load. Their amounts are
		/// on <see cref="TryGetUnreported"/>, so nothing is lost, but no event was raised for
		/// them.</summary>
		public static long DroppedRecords { get; private set; }

		[DllImport("SimDLL")]
		private static extern int SIM_ExtLiquidPayloadUnreported(int propertyIdx, int component,
			out double amount);

		private static bool installed;
		private static bool unavailable;
		private static int releasedIdx = -1;
		private static int consumedIdx = -1;
		private static long subscribedAtTick = -1;
		private static bool strideWarned;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool Installed
		{
			get { return installed; }
		}

		/// <summary>
		/// Whether this SimDLL declares the two streams. False on a stock SimDLL or an older
		/// replacement, and then nothing here does anything.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable && Resolve(); }
		}

		/// <summary>
		/// How much of one component of a carried property left the grid in a record nobody
		/// took, since the SimDLL was initialised. False on a SimDLL without the export.
		/// Takes the sim's worker barrier: read it for a ledger, not every tick.
		/// </summary>
		public static bool TryGetUnreported(int propertyIdx, int component, out double amount)
		{
			amount = 0.0;
			if (unavailable)
			{
				return false;
			}
			try
			{
				return SIM_ExtLiquidPayloadUnreported(propertyIdx, component, out amount) != 0;
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
		}

		/// <summary>
		/// Install the per-tick drain: <see cref="SimExtFrame.Install"/>, then subscribe both
		/// streams and decode them on every bound frame. Idempotent, so several mods can ask.
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
			SimExtFrame.Install(harmony);
			SimExtFrame.Bound += OnBound;
			installed = true;
		}

		private static bool Resolve()
		{
			if (releasedIdx >= 0 && consumedIdx >= 0)
			{
				return true;
			}
			releasedIdx = SimExtEventStreams.Find(ReleasedStream);
			consumedIdx = SimExtEventStreams.Find(ConsumedStream);
			return releasedIdx >= 0 && consumedIdx >= 0;
		}

		private static void OnBound()
		{
			if (unavailable || !Resolve())
			{
				return;
			}
			SimExtFrame.PublishedStream released;
			SimExtFrame.PublishedStream consumed;
			bool haveReleased = SimExtFrame.TryGetStream(ReleasedStream, out released);
			bool haveConsumed = SimExtFrame.TryGetStream(ConsumedStream, out consumed);
			// Subscription is queued and lands two binds later (sent in tick N, drained in the
			// frame kicked at N+1, published at N+2). A world the sim re-initialised has
			// forgotten it, so a stream still missing well after the last request is asked for
			// again. Subscribing twice is harmless.
			if (!haveReleased || !haveConsumed)
			{
				if (subscribedAtTick < 0 || SimExtFrame.TickId - subscribedAtTick > 4)
				{
					SimExtEventStreams.Subscribe(releasedIdx, true);
					SimExtEventStreams.Subscribe(consumedIdx, true);
					subscribedAtTick = SimExtFrame.TickId;
				}
			}
			if (haveReleased)
			{
				DrainReleased(released);
			}
			if (haveConsumed)
			{
				DrainConsumed(consumed);
			}
		}

		private static bool Readable(SimExtFrame.PublishedStream stream, string name)
		{
			if (!stream.IsCurrent || stream.Count <= 0)
			{
				return false;
			}
			if (stream.Stride != RecordBytes)
			{
				// The record shape changed under this build. Decoding anyway would hand out
				// plausible nonsense. Said once, not every tick.
				if (strideWarned)
				{
					return false;
				}
				strideWarned = true;
				FrameworkLog.Error("LiquidPayload: " + name + " has stride " + stream.Stride
					+ ", expected " + RecordBytes + ". Not decoding.");
				return false;
			}
			DroppedRecords += stream.Dropped;
			return true;
		}

		private static void DrainReleased(SimExtFrame.PublishedStream stream)
		{
			Action<LiquidPayloadReleased> handler = Released;
			if (handler == null || !Readable(stream, ReleasedStream))
			{
				return;
			}
			for (int i = 0; i < stream.Count; i++)
			{
				int o = i * RecordBytes;
				LiquidPayloadReleased r = default(LiquidPayloadReleased);
				r.Cell = Marshal.ReadInt32(stream.Data, o);
				r.PropertyIdx = Marshal.ReadInt32(stream.Data, o + 4);
				r.Component = Marshal.ReadInt32(stream.Data, o + 8);
				r.Amount = ReadFloat(stream.Data, o + 12);
				r.TemperatureK = ReadFloat(stream.Data, o + 16);
				r.Reason = (LiquidPayloadReleaseReason)Marshal.ReadInt32(stream.Data, o + 20);
				try
				{
					handler(r);
				}
				catch (Exception e)
				{
					// One handler's bug must not cost the records after it: their liquid has
					// already left the grid, and this is the only place they are ever seen.
					FrameworkLog.Error("LiquidPayload: a Released handler threw: " + e);
				}
			}
		}

		private static void DrainConsumed(SimExtFrame.PublishedStream stream)
		{
			Action<LiquidPayloadConsumed> handler = Consumed;
			if (handler == null || !Readable(stream, ConsumedStream))
			{
				return;
			}
			for (int i = 0; i < stream.Count; i++)
			{
				int o = i * RecordBytes;
				LiquidPayloadConsumed r = default(LiquidPayloadConsumed);
				r.Kind = (LiquidPayloadConsumerKind)Marshal.ReadInt32(stream.Data, o);
				r.Id = Marshal.ReadInt32(stream.Data, o + 4);
				r.PropertyIdx = Marshal.ReadInt32(stream.Data, o + 8);
				r.Component = Marshal.ReadInt32(stream.Data, o + 12);
				r.Amount = ReadFloat(stream.Data, o + 16);
				r.TemperatureK = ReadFloat(stream.Data, o + 20);
				try
				{
					handler(r);
				}
				catch (Exception e)
				{
					FrameworkLog.Error("LiquidPayload: a Consumed handler threw: " + e);
				}
			}
		}

		private static float ReadFloat(IntPtr data, int offset)
		{
			return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(data, offset)), 0);
		}
	}
}
