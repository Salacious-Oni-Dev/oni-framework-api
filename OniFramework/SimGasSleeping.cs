using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads the volume-fractions mixing layer's per-cell sleep bits.
	///
	/// <b>Read-only, and that is the finding.</b> This looks exactly like the gap
	/// <see cref="SimStableTicks"/> and <see cref="SimDiseaseGrowth"/> had to close: <c>World::gas_sleeping_</c> is per-cell state
	/// that no save blob carries, every allocation resets it to all-awake, and every load goes
	/// through an allocation -- so a restored world has forgotten which cells the mixing pass
	/// had parked. It was the last such array left after the countdowns, and the obvious
	/// expectation was a fifth piece of a checkpoint.
	///
	/// It is not, and there is deliberately no setter here to make one. A pair of cells is
	/// skipped only when <b>both</b> ends are asleep, and both only sleep after five
	/// consecutive ticks in which every transfer between them fell under the mixing kernel's
	/// minimum-transfer floor. A sub-floor transfer is never applied, so nothing accumulates
	/// behind the floor, and nothing can write into such a pair without waking one end first.
	/// Skipping the pair is therefore exactly equal to running it, and forgetting the bits
	/// costs a few ticks of skipped optimisation and nothing else.
	///
	/// <b>Measured, not argued.</b> A checkpoint-replay run moved 479.420492 kg across 612 cells and
	/// three species into the mixture layer and ran a 120-tick scrub sweep with the mixture in
	/// its digest -- the mixing kernels read <c>PhaseEntry.temperature</c> and write only their
	/// own arrays, none of which <c>GameDataUpdate</c> carries, so a mixture divergence is
	/// byte-identical in every published array and the digest had to be extended before the
	/// test meant anything. 255,287 cells were asleep at the end of the run
	/// and 256,012 at the peak, so the sweep genuinely exercised sleeping rather than a world
	/// with nothing to forget, and all 113 replayed ticks reproduced the run bit for bit.
	///
	/// <b>So what is this for.</b> Keeping that conclusion checkable by looking instead of by
	/// reasoning. A caller that ever does find a mixture divergence across a load can read the
	/// array rather than infer it, and this is the export that lets it.
	/// </summary>
	public static class SimGasSleeping
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugGasSleeping(byte* buffer, int capacity);

		private static bool unavailable;

		/// <summary>
		/// Whether the export is present -- i.e. whether this is a custom SimDLL new enough to
		/// have it. False only after a call has actually failed, since the export cannot be
		/// probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// The mixing layer's sleep bits, one byte per <b>padded</b> cell: 1 where a cell is
		/// parked, 0 where it is awake. False on a SimDLL without the export, and on one with
		/// no allocated world.
		///
		/// The length is whatever the sim reports rather than anything derived here, the same
		/// contract <see cref="SimStableTicks.TryGetState"/> uses: padding is the sim's
		/// business. Every cell reads as awake on a world that has never activated volume
		/// fractions, which is every vanilla world.
		/// </summary>
		public static unsafe bool TryGetState(out byte[] sleeping)
		{
			sleeping = null;
			if (unavailable)
			{
				return false;
			}
			int count;
			try
			{
				count = SIM_DebugGasSleeping(null, 0);
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or a custom one older than this export. Latched so a caller
				// polling this does not throw repeatedly.
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
				// The sim is present but has no allocated world. Not a missing export, so this
				// is not latched.
				return false;
			}
			byte[] buffer = new byte[count];
			fixed (byte* p = buffer)
			{
				if (SIM_DebugGasSleeping(p, count) != count)
				{
					return false;
				}
			}
			sleeping = buffer;
			return true;
		}

		/// <summary>
		/// How many cells are parked right now, or -1 when the sim cannot say. A convenience
		/// over <see cref="TryGetState"/> for the common case of wanting the count rather than
		/// the map.
		/// </summary>
		public static int SleepingCount()
		{
			byte[] sleeping;
			if (!TryGetState(out sleeping))
			{
				return -1;
			}
			int n = 0;
			for (int i = 0; i < sleeping.Length; ++i)
			{
				if (sleeping[i] != 0)
				{
					++n;
				}
			}
			return n;
		}
	}
}
