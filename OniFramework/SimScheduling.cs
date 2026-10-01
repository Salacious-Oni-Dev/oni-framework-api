using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and restores the sim's scheduling counters -- the part of a running simulation
	/// that a save blob does not contain.
	///
	/// <b>The problem this exists for.</b> <see cref="SimRandom"/> closed one half of "a
	/// checkpoint is not complete": the random stream is not in the save format. This closes
	/// the other half, and the gap was found the same way, by measuring rather than by reading
	/// the handler. A checkpoint-replay tool restored a checkpoint and replayed
	/// forward from it, and the replay reproduced the original run for exactly ONE frame and
	/// then diverged -- 26 of 35 replayed ticks wrong, worst 10.537 kg of total mass, on a
	/// 40-tick sweep of the 636x404 cluster corpus. The random stream was <b>not</b> the cause:
	/// on most diverged ticks the live stream state read back identical and the cell arrays
	/// still differed, so the streams were in step and the physics was not.
	///
	/// The cause is that both <c>AllocateCells</c> -- the message a main-game LOAD boots
	/// through -- and <c>Load</c> itself reset SimData scheduling state to its
	/// fresh-allocation values: the substep rotation counter to 0, the pressure direction to
	/// -1, the shuffle direction to -1, the leftover substep time to 0, the
	/// first-physics-substep flag back on, and one frame of physics skipped. A sim that has
	/// been running to tick T holds none of those. They belong to the SimData object rather
	/// than to the world contents, which is why they are not in the save format and never
	/// were. So a restore puts the right world under the wrong schedule, and the first frame
	/// that actually runs physics runs it in a different order.
	///
	/// "A different order" is not cosmetic. The rotation counter picks which of several
	/// equally good cells receives gas displaced by a liquid, and the pressure direction picks
	/// the column order of the gas pressure sweep, the horizontal neighbour each cell pairs
	/// with, and therefore which upward diagonal exists this substep.
	///
	/// <b>Who needs this.</b> Anything that reconstructs a run rather than merely resuming
	/// one: a scrub timeline stepping backwards, a rig replaying to a failure, a bug report of
	/// the form "load this save, wait, watch it go wrong". Measured after this landed, the
	/// same 40-tick sweep goes from 26 diverged ticks to <b>every one of 40 seeks reproducing
	/// the run bit for bit</b>, and an 80-tick sweep does the same across two thinnings of the
	/// checkpoint ring.
	///
	/// <b>A complete checkpoint is six things</b>, not one: the save blob, the random state
	/// (<see cref="SimRandom"/>), this, the unstable-solid countdowns
	/// (<see cref="SimStableTicks"/>), the disease growth remainders
	/// (<see cref="SimDiseaseGrowth"/>) and the component registries
	/// (<see cref="SimRegistryState"/>, which is the one that is not per-cell state at all).
	/// Any five of the six leave a replay that can diverge. For the per-cell arrays the count
	/// still ends at five -- see <see cref="SimGasSleeping"/> for the last one a load resets,
	/// and for why that one does not have to be carried.
	///
	/// <b>This does not change the default.</b> A world that never calls
	/// <see cref="SetState"/> behaves exactly as before -- verified with the sim's differential
	/// suite: 99 scenarios, zero differing lines against a build without it.
	///
	/// <b>States, not settings.</b> Every value the getter returns is a legal value to send
	/// back. There is no validation to do beyond that, and none is done.
	/// </summary>
	public static class SimScheduling
	{
		/// <summary>
		/// The sim's scheduling counters. Mirrors
		/// <c>oni_sim::ext::SetSchedulingStateMessage</c> in <c>abi/sim_abi_ext.h</c> exactly:
		/// 4-byte packing, 20 bytes total. Do not reorder.
		/// </summary>
		[StructLayout(LayoutKind.Sequential, Pack = 4)]
		public struct State
		{
			/// <summary>Frames to publish without stepping physics. A Load sets this to 1.</summary>
			public int SkipPhysicsFrames;

			/// <summary>Negated once per substep. Picks the gas sweep's column order.</summary>
			public int PressureDir;

			/// <summary>The gas shuffle's direction.</summary>
			public int ShuffleDir;

			/// <summary>Frame time left over from the last whole substep.</summary>
			public float SubstepCarry;

			/// <summary>Incremented once per substep. Picks where displaced gas goes.</summary>
			public ushort DisplaceRotation;

			/// <summary>0 or 1. The one-massless-cell-per-region sparing, done once.</summary>
			public byte FirstPhysicsSubstep;

			/// <summary>0 or 1. Whether the volume-fraction mixing pass is running.</summary>
			public byte VolumeFractionsActive;
		}

		[DllImport("SimDLL")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool SIM_DebugSchedulingState(ref State state);

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
		/// The sim's current scheduling counters, or <c>false</c> on a SimDLL without the
		/// export.
		///
		/// Read this at the same moment as the save blob and the random state. All three
		/// together are one checkpoint; any two of them are not.
		/// </summary>
		public static bool TryGetState(out State state)
		{
			state = default(State);
			if (unavailable)
			{
				return false;
			}
			try
			{
				if (!SIM_DebugSchedulingState(ref state))
				{
					// The sim is present but has no world yet. Not a missing export, so this
					// is not latched -- a later call, once a world exists, will succeed.
					return false;
				}
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
			return true;
		}

		/// <summary>
		/// Restores the scheduling counters.
		///
		/// <b>Timing.</b> This is a queued message like every other sim extension: it is
		/// drained at the top of the frame AFTER the one it was sent during, ahead of that
		/// frame's physics. (An earlier version of this text said two frames. Measured since,
		/// by reading the counters back after each frame of a restore: at +0 frames they are
		/// still the Load's own defaults, and at +1 they are the restored values with one
		/// substep already run on top of them.) Send it immediately after the <c>Load</c>, in
		/// the same position as <see cref="SimRandom.SetState"/>.
		///
		/// <b>Send the captured <see cref="State.SkipPhysicsFrames"/> as it was.</b> The Load
		/// asks for one skipped frame of its own, and the frame this message lands on is that
		/// frame: the captured value overwrites the skip, so that frame steps physics and
		/// lands on the tick after the checkpoint's. Re-forcing a skip here instead makes it
		/// skip as well and silently drops one physics frame out of every replay.
		///
		/// Safe to call on a stock SimDLL: it is an unrecognised message id, which the sim
		/// drops.
		/// </summary>
		public static unsafe void SetState(State state)
		{
			int size = Marshal.SizeOf(typeof(State));
			byte[] payload = new byte[size];
			fixed (byte* msg = payload)
			{
				Marshal.StructureToPtr(state, (IntPtr)msg, false);
				global::Sim.SIM_HandleMessage(OniExtMessages.SetSchedulingState, size, msg);
			}
		}
	}
}
