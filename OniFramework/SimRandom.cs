using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and pins the custom SimDLL's random stream.
	///
	/// <b>The problem this exists for.</b> A main-game LOAD boots through the sim's
	/// <c>AllocateCells</c> message, and that path seeds the random stream from the wall clock
	/// (<c>time(NULL)</c>). That is Klei's own behaviour, faithfully reproduced in the
	/// replacement SimDLL and deliberately not "fixed" there -- a save loaded twice is meant
	/// to shuffle its gas differently. The consequence is that <b>a loaded world is not
	/// reproducible across runs</b>: measured on a 636x404 cluster, two runs of an identical
	/// ten-tick command diverged in 1,897 element cells, 40,151 mass cells and 50,189
	/// temperature cells. Runs finishing inside the same wall-clock second agreed; runs
	/// straddling a second boundary did not.
	///
	/// The divergence starts wherever gas moves, because the gas shuffle is what draws from the
	/// stream. Anything that does not touch a gas is unaffected.
	///
	/// <b>Who needs this.</b> Not only a visualizer:
	/// <list type="bullet">
	/// <item>A test rig whose result depends on gas is not reproducible across game loads,
	/// which sits badly with a "two consecutive green runs" acceptance rule -- that rule
	/// assumes a determinism a loaded world does not have. Pin the state at rig arm time and
	/// the rig becomes repeatable.</item>
	/// <item>A checkpoint is not complete without it. The sim's save blob does NOT carry the
	/// stream position (<c>rng_</c> is not in its save format), so restoring a blob and
	/// re-simulating draws from a different point than the original run did.</item>
	/// <item>A bug report of the form "load this save, wait, watch it go wrong" is reproducible
	/// only if the stream is.</item>
	/// </list>
	///
	/// <b>This does not change the default.</b> A world that never calls
	/// <see cref="SetState"/> behaves exactly as before, which is the same
	/// byte-identical-until-sent guarantee every extension message makes -- verified with the
	/// sim's differential suite: 99 scenarios, zero differing lines against a build without it.
	///
	/// <b>Not a seed, a state.</b> The value is written into the LCG verbatim; there is no
	/// seed-to-state transform. So a value from <see cref="TryGetState"/> handed back to
	/// <see cref="SetState"/> reproduces the stream exactly. Every <see cref="uint"/> is legal,
	/// zero included.
	///
	/// Only meaningful with the custom SimDLL. On a stock one <see cref="TryGetState"/> returns
	/// <c>false</c> rather than throwing, and <see cref="SetState"/> is a message the sim
	/// ignores.
	/// </summary>
	public static class SimRandom
	{
		[DllImport("SimDLL")]
		private static extern uint SIM_DebugRandomState();

		private static bool unavailable;

		/// <summary>
		/// Whether the getter export is present -- i.e. whether this is the custom SimDLL.
		/// False only after a call has actually failed, since the export cannot be probed
		/// without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		/// <summary>
		/// The sim's current random state, or <c>false</c> on a stock SimDLL.
		///
		/// Read this after a load and keep it beside the save if a run needs to be repeatable
		/// later. Note that the state ADVANCES as the sim draws from it, so a value read after
		/// N frames returns to that point, not to the start.
		/// </summary>
		public static bool TryGetState(out uint state)
		{
			state = 0u;
			if (unavailable)
			{
				return false;
			}
			try
			{
				state = SIM_DebugRandomState();
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL. Latched so a caller polling this does not throw repeatedly.
				unavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return false;
			}
			return true;
		}

		/// <summary>
		/// Pins the random stream to an exact state.
		///
		/// <b>Timing matters.</b> This is a queued message like every other sim extension, so
		/// it lands at the top of the NEXT sim frame, ahead of that frame's physics. Send it
		/// before the first frame whose behaviour must be reproducible. Sending it mid-run is
		/// legal and simply re-points the stream from that frame onward.
		///
		/// Safe to call on a stock SimDLL: it is an unrecognised message id, which the sim
		/// drops.
		/// </summary>
		public static unsafe void SetState(uint state)
		{
			byte[] payload = BitConverter.GetBytes(state);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetRandomState, payload.Length, msg);
			}
		}
	}
}
