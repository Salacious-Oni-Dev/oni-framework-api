using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and restores the sim's copy of the game's visibility mask -- the ninth thing a
	/// checkpoint needs.
	///
	/// <b>The problem this exists for.</b> The sim keeps the mask the game sends with every frame
	/// three buffers deep, as the game's own library does: a frame reads the mask the game sent two frames earlier,
	/// and falling liquid (and a solid emitter's ore drop) refuses a cell nobody can see. Every
	/// allocate and load empties all three, as a new world starts on vanilla, and the game refills
	/// them by itself. A caller reconstructing a run, rather than resuming one, needs them back:
	/// without them the first frames after a restore refuse the falling liquid the run handed
	/// over, and the replay diverges from the run it replays.
	///
	/// <b>A complete checkpoint is nine things</b>, listed once in
	/// the SimDLL's <c>abi/sim_abi_ext.h</c> at <c>kSetExtCellState</c>. See
	/// <see cref="SimCellRadiation"/>, the other immediate one.
	///
	/// <b>This does not change the default.</b> A world that never calls
	/// <see cref="SetState"/> behaves exactly as before.
	/// </summary>
	public static class SimVisibilityState
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugVisibilityState(byte* output, int capacity);

		private static bool unavailable;

		/// <summary>
		/// Whether the getter export is present -- i.e. whether this is a replacement SimDLL
		/// that has it. False only after a call has actually failed, since the
		/// export cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// The three buffers and which one is the sim side, as one opaque payload: pass it to
		/// <see cref="SetState"/> unchanged. Read it at the same moment as the save blob and the
		/// other checkpoint components.
		/// </summary>
		public static unsafe bool TryGetState(out byte[] state)
		{
			state = null;
			if (unavailable)
			{
				return false;
			}
			int length;
			try
			{
				length = SIM_DebugVisibilityState(null, 0);
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
			if (length <= 0)
			{
				// The sim is present but has no allocated world. Not a missing export, so this
				// is not latched.
				return false;
			}
			byte[] bytes = new byte[length];
			fixed (byte* p = bytes)
			{
				if (SIM_DebugVisibilityState(p, length) != length)
				{
					return false;
				}
			}
			state = bytes;
			return true;
		}

		/// <summary>
		/// Restores the visibility mask.
		///
		/// <b>Send it after the Load, before the next frame.</b> It is applied
		/// <b>immediately</b>, inside the call: the next frame's call writes the game's new mask
		/// into one of these buffers, and a queued restore would land after that and overwrite
		/// it. The first frame after the Load then puts the buffers exactly where the captured
		/// run had them. The sim rejects a payload whose cell count is not the loaded world's,
		/// whole rather than in part.
		///
		/// Safe to call on a stock SimDLL: it is an unrecognised message id, which the sim
		/// drops. Passing null or empty does nothing.
		/// </summary>
		public static unsafe void SetState(byte[] state)
		{
			if (state == null || state.Length == 0)
			{
				return;
			}
			fixed (byte* msg = state)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetVisibilityState, state.Length, msg);
			}
		}
	}
}
