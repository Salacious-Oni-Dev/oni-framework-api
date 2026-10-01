using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and restores the per-cell countdown that decides when an unstable solid falls --
	/// the last piece of a running simulation that no save carries.
	///
	/// <b>The problem this exists for.</b> <see cref="SimRandom"/> closed the random stream and
	/// <see cref="SimScheduling"/> closed the scheduling counters, and a checkpoint-replay tool
	/// still could not reproduce every replayed tick. Which ticks failed
	/// looked at first like a property of how often it checkpointed. It is not: it is a
	/// property of the world. At one checkpoint every 10 ticks over a 40-tick sweep, three
	/// random seeds reproduced all 40 seeks and two others left 9 replayed ticks wrong, on the
	/// same binary against the same DLL.
	///
	/// The cause is the sim's stable-ticks array, one byte per cell, holding in its low five bits how
	/// many substeps an unstable solid has left before it falls. The save format has no field
	/// for it -- a blob carries cells, disease, backwall, gas and room promotion -- and every
	/// allocation resets the whole array to the "reroll" sentinel, which every load goes
	/// through. So a restored world has forgotten when each grain of sand was going to fall,
	/// and the sim rerolls each countdown it reaches.
	///
	/// That reroll is <b>the only draw the unstable path makes on the random stream</b>, which
	/// is why the symptom is two things at once: sand that falls on the wrong tick, and a
	/// random stream that ends up in the wrong place even though it was restored correctly.
	/// Measured on the 636x404 cluster corpus, one replayed frame past a checkpoint: 2,234 of
	/// 256,944 cells differ and the total mass is off by 20.392861 kg in two cells of about
	/// 10 kg -- one grain of sand, one tick early. With this restored as well, every seek of
	/// the sweep reproduces the run bit for bit at every checkpoint interval tried, and at
	/// every seed tried.
	///
	/// <b>A complete checkpoint is six things</b>: the save blob, the random state
	/// (<see cref="SimRandom"/>), the scheduling counters (<see cref="SimScheduling"/>), this,
	/// the disease growth remainders (<see cref="SimDiseaseGrowth"/>) and the component
	/// registries (<see cref="SimRegistryState"/>). Any five of them leave a replay that can
	/// diverge. This one was twice believed to be the last and was not, each time because the
	/// test that said so was too narrow: first hashing three of the twelve arrays
	/// GameDataUpdate publishes, then hashing only what it publishes at all -- the registries
	/// are published nowhere.
	///
	/// The sixth is <b>not</b> a per-cell array, and the per-cell enumeration that ended at
	/// five still holds: the mixing layer's sleep bits are the last per-cell array a load
	/// resets, and <see cref="SimGasSleeping"/> records why forgetting those costs nothing.
	///
	/// <b>Who needs this.</b> The same callers as <see cref="SimScheduling"/> -- anything that
	/// reconstructs a run rather than resuming one. Ordinary gameplay does not: losing the
	/// countdowns across a load is Klei's behaviour and this does not change it.
	///
	/// <b>This does not change the default.</b> A world that never calls <see cref="SetState"/>
	/// behaves exactly as before -- verified by running the sim's differential suite with the
	/// build that has this message against the build that does not: 98 scenarios identical,
	/// zero divergences, three runs.
	/// </summary>
	public static class SimStableTicks
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugStableTicks(byte* buffer, int capacity);

		private static bool unavailable;

		/// <summary>
		/// Whether the getter export is present -- i.e. whether this is a custom SimDLL new
		/// enough to have it. False only after a call has actually failed, since the export
		/// cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// The sim's current unstable-solid countdowns, one byte per <b>padded</b> cell, or
		/// <c>false</c> on a SimDLL without the export or on one with no world yet.
		///
		/// The length is whatever the sim reports rather than anything derived here: padding
		/// is the sim's business. Read this at the same moment as the save blob, the random
		/// state and the scheduling counters -- all four together are one checkpoint.
		/// </summary>
		public static unsafe bool TryGetState(out byte[] ticks)
		{
			ticks = null;
			if (unavailable)
			{
				return false;
			}
			int count;
			try
			{
				count = SIM_DebugStableTicks(null, 0);
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or a custom one older than this message. Latched so a
				// caller polling this does not throw repeatedly.
				unavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return false;
			}
			if (count <= 0)
			{
				// The sim is present but has no allocated world. Not a missing export, so
				// this is not latched.
				return false;
			}
			byte[] buffer = new byte[count];
			fixed (byte* p = buffer)
			{
				if (SIM_DebugStableTicks(p, count) != count)
				{
					return false;
				}
			}
			ticks = buffer;
			return true;
		}

		/// <summary>
		/// Restores the unstable-solid countdowns.
		///
		/// <b>Send it after the Load</b>, in the same position as
		/// <see cref="SimRandom.SetState"/> and <see cref="SimScheduling.SetState"/>. It is
		/// sized against the world that is loaded when it drains, not the one that was loaded
		/// when it was sent, and the sim rejects an array whose length is not that world's
		/// padded cell count rather than writing the part that fits.
		///
		/// <b>Timing.</b> Queued like every sim extension message: it lands at the top of the
		/// frame after the one it was sent during, ahead of that frame's physics -- which is
		/// the first frame that runs under the restored state.
		///
		/// Safe to call on a stock SimDLL: it is an unrecognised message id, which the sim
		/// drops. Passing null or an empty array does nothing.
		/// </summary>
		public static unsafe void SetState(byte[] ticks)
		{
			if (ticks == null || ticks.Length == 0)
			{
				return;
			}
			// The only variable-length message in the extension set: a 4-byte count followed
			// by that many bytes, built into one buffer because SIM_HandleMessage takes one
			// pointer and one length.
			byte[] payload = new byte[4 + ticks.Length];
			payload[0] = (byte)(ticks.Length & 0xFF);
			payload[1] = (byte)((ticks.Length >> 8) & 0xFF);
			payload[2] = (byte)((ticks.Length >> 16) & 0xFF);
			payload[3] = (byte)((ticks.Length >> 24) & 0xFF);
			Buffer.BlockCopy(ticks, 0, payload, 4, ticks.Length);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetStableTicks, payload.Length, msg);
			}
		}
	}
}
