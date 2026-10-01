using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and restores the per-cell radiation field -- the eighth thing a checkpoint needs.
	///
	/// <b>The problem this exists for.</b> A save does carry radiation, but <c>Load</c> does not
	/// give all of it back: like Klei's own <c>Load</c>, it clears the radiation of every Vacuum
	/// and Void cell, and a running world has radiation in Vacuum. Losing it across a load is
	/// Klei's behaviour and this does not change it. A caller reconstructing a run, rather than
	/// resuming one, needs it back: without it, a checkpoint-replay tool failed 2 of 4 exact stops and 17 of 26 replayed ticks on the reference
	/// corpus, in the radiation array alone, with the random stream in step and the mass
	/// bit-identical.
	///
	/// <b>A complete checkpoint is nine things</b>, listed once in
	/// the SimDLL's <c>abi/sim_abi_ext.h</c> at <c>kSetExtCellState</c>. See
	/// <see cref="SimDiseaseGrowth"/> for the pattern this follows.
	///
	/// <b>This does not change the default.</b> A world that never calls
	/// <see cref="SetState"/> behaves exactly as before.
	/// </summary>
	public static class SimCellRadiation
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugCellRadiation(float* output, int capacity);

		private static bool unavailable;

		/// <summary>
		/// Whether the getter export is present -- i.e. whether this is a custom SimDLL that
		/// has it. False only after a call has actually failed, since the
		/// export cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// The sim's radiation field, one entry per <b>padded</b> cell. The length is whatever
		/// the sim reports. Read it at the same moment as the save blob and the other checkpoint
		/// components.
		/// </summary>
		public static unsafe bool TryGetState(out float[] radiation)
		{
			radiation = null;
			if (unavailable)
			{
				return false;
			}
			int count;
			try
			{
				count = SIM_DebugCellRadiation(null, 0);
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
			if (count <= 0)
			{
				// The sim is present but has no allocated world. Not a missing export, so this
				// is not latched.
				return false;
			}
			float[] values = new float[count];
			fixed (float* p = values)
			{
				if (SIM_DebugCellRadiation(p, count) != count)
				{
					return false;
				}
			}
			radiation = values;
			return true;
		}

		/// <summary>
		/// Restores the radiation field.
		///
		/// <b>Send it after the Load.</b> It is applied <b>immediately</b>, inside the call,
		/// unlike the other checkpoint components: the game reads radiation back every frame, and
		/// the first frame after a Load publishes before a queued message would land. The sim
		/// rejects an array whose length is not the loaded world's padded cell count rather than
		/// writing the part that fits.
		///
		/// Safe to call on a stock SimDLL: it is an unrecognised message id, which the sim
		/// drops. Passing null or empty does nothing.
		/// </summary>
		public static unsafe void SetState(float[] radiation)
		{
			if (radiation == null || radiation.Length == 0)
			{
				return;
			}
			int count = radiation.Length;
			byte[] payload = new byte[4 + count * 4];
			payload[0] = (byte)(count & 0xFF);
			payload[1] = (byte)((count >> 8) & 0xFF);
			payload[2] = (byte)((count >> 16) & 0xFF);
			payload[3] = (byte)((count >> 24) & 0xFF);
			Buffer.BlockCopy(radiation, 0, payload, 4, count * 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetCellRadiation, payload.Length, msg);
			}
		}
	}
}
