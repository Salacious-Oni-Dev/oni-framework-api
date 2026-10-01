using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and restores the per-cell disease growth state -- the fifth thing a
	/// running simulation holds that no save carries.
	///
	/// <b>The problem this exists for.</b> <see cref="SimRandom"/> closed the random stream,
	/// <see cref="SimScheduling"/> the scheduling counters, <see cref="SimStableTicks"/> the
	/// unstable-solid countdowns, and after all three a checkpoint-replay tool reported that every replayed tick reproduced its run bit for bit. It was not measuring
	/// what it claimed. Its digest hashed three of the twelve per-cell arrays
	/// <c>GameDataUpdate</c> publishes -- element, mass and temperature -- and the divergence
	/// was in an array it never looked at.
	///
	/// Widened to all twelve, the same 40-tick sweep on the reference corpus failed <b>26 of
	/// 35 replayed ticks, in the disease arrays alone</b>, with the random stream in step and
	/// the total mass bit-identical, at every seed tried. The corpus carries 3,739 diseased
	/// cells across four disease types, so this was live the whole time.
	///
	/// The cause is that Klei's <c>SaveDisease</c> is eight bytes -- a disease hash and a
	/// count -- so the growth remainder each cell has banked toward its next whole disease
	/// unit, and how long that cell has been infested, are in no save format. Every load
	/// zeroes both. A replay from a restored world therefore grows its disease from zero
	/// while the run it is replaying does not, and the gap widens with replay distance: exact
	/// stops and short replays still passed, because a remainder restarted at zero takes
	/// several ticks to fall a whole unit behind. That is what made it invisible to any check
	/// looking one frame past a checkpoint.
	///
	/// <b>A complete checkpoint is six things</b>: the save blob, the random state
	/// (<see cref="SimRandom"/>), the scheduling counters (<see cref="SimScheduling"/>), the
	/// unstable-solid countdowns (<see cref="SimStableTicks"/>), this, and the component
	/// registries (<see cref="SimRegistryState"/>). This one was called the last of them when
	/// it landed, and it was not: the sixth was hiding behind the fact that nothing publishes
	/// the registries at all. With all six, the same sweeps reproduce every seek bit for bit
	/// over all twelve published arrays and the registry blob, at every seed and interval
	/// tried, including one that thins the checkpoint ring.
	///
	/// <b>Who needs this.</b> The same callers as <see cref="SimStableTicks"/> -- anything
	/// reconstructing a run rather than resuming one. Ordinary gameplay does not: losing the
	/// growth remainders across a load is Klei's own behaviour and this does not change it.
	///
	/// <b>This does not change the default.</b> A world that never calls <see cref="SetState"/>
	/// behaves exactly as before.
	/// </summary>
	public static class SimDiseaseGrowth
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugDiseaseGrowth(float* accum, byte* infest,
			int capacity);

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
		/// The sim's current disease growth state, one entry per <b>padded</b> cell in each
		/// array: <paramref name="accum"/> is the growth remainder banked toward the next whole
		/// disease unit, <paramref name="infest"/> is how long the cell has been infested.
		///
		/// Both come back together because they are one state -- an infestation age without
		/// the remainder that produced it is not a state. The length is whatever the sim
		/// reports rather than anything derived here: padding is the sim's business. Read this
		/// at the same moment as the save blob, the random state, the scheduling counters, the
		/// countdowns and the component registries -- all six together are one checkpoint.
		/// </summary>
		public static unsafe bool TryGetState(out float[] accum, out byte[] infest)
		{
			accum = null;
			infest = null;
			if (unavailable)
			{
				return false;
			}
			int count;
			try
			{
				count = SIM_DebugDiseaseGrowth(null, null, 0);
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
			float[] a = new float[count];
			byte[] f = new byte[count];
			fixed (float* pa = a)
			fixed (byte* pf = f)
			{
				if (SIM_DebugDiseaseGrowth(pa, pf, count) != count)
				{
					return false;
				}
			}
			accum = a;
			infest = f;
			return true;
		}

		/// <summary>
		/// Restores the disease growth state.
		///
		/// <b>Send it after the Load</b>, in the same position as
		/// <see cref="SimRandom.SetState"/>, <see cref="SimScheduling.SetState"/> and
		/// <see cref="SimStableTicks.SetState"/>. It is sized against the world that is loaded
		/// when it drains, not the one that was loaded when it was sent, and the sim rejects
		/// arrays whose length is not that world's padded cell count rather than writing the
		/// part that fits -- half a restored growth field is worse than none, since the half
		/// that did not land is the zeroed state the caller is trying to get out of.
		///
		/// <b>Timing.</b> Queued like every sim extension message: it lands at the top of the
		/// frame after the one it was sent during, ahead of that frame's physics.
		///
		/// Safe to call on a stock SimDLL: it is an unrecognised message id, which the sim
		/// drops. Passing null, empty, or mismatched arrays does nothing.
		/// </summary>
		public static unsafe void SetState(float[] accum, byte[] infest)
		{
			if (accum == null || infest == null || accum.Length == 0 ||
			    accum.Length != infest.Length)
			{
				return;
			}
			// int32 count, then that many floats, then that many bytes -- one buffer, because
			// SIM_HandleMessage takes one pointer and one length and the sim checks the length
			// against the count it was given.
			int count = accum.Length;
			byte[] payload = new byte[4 + count * 4 + count];
			payload[0] = (byte)(count & 0xFF);
			payload[1] = (byte)((count >> 8) & 0xFF);
			payload[2] = (byte)((count >> 16) & 0xFF);
			payload[3] = (byte)((count >> 24) & 0xFF);
			Buffer.BlockCopy(accum, 0, payload, 4, count * 4);
			Buffer.BlockCopy(infest, 0, payload, 4 + count * 4, count);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetDiseaseGrowth, payload.Length, msg);
			}
		}
	}
}
