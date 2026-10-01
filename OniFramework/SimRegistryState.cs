using System;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads and restores the sim's four component registries -- buildings, element chunks,
	/// the element and disease flow components, and the radiation emitters -- as one opaque
	/// blob. The sixth thing a running simulation holds that no save carries.
	///
	/// <b>The problem this exists for.</b> <c>AllocateCells</c> clears all four registries
	/// outright, and no save blob has ever carried any of them. In ordinary play that is
	/// correct and invisible, because the game re-registers every handle it holds after a
	/// load. Anything <i>else</i> driving the sim cannot: it is not holding those handles.
	/// For such a caller a restored world is a world with no buildings, no element chunks, no
	/// emitters and no consumers in it.
	///
	/// <b>Re-sending the original registration messages is not the same thing.</b> That was
	/// measured, and it fixes only tick zero. A
	/// registration is a handle on a body that then evolves every substep -- a chunk's and a
	/// building's temperature, an emitter's elapsed time and blocked state, a consumer's
	/// sampling offset, a radiation emitter's timers -- so replaying the recorded
	/// registrations restores the topology at the values it had when it was first registered.
	/// A checkpoint is then restored with a registry stale by however many ticks old it is.
	/// The measurement that gives it away: shortening the checkpoint interval made it
	/// <b>worse</b>, 3 of 17 replayed ticks reproducing at interval 2 against 9 at interval
	/// 10, because the fault scales with a checkpoint's age and not with replay length.
	///
	/// Exact stops kept passing throughout, which is why this hid: a stop restores and
	/// publishes without stepping physics, so a stale registry never gets to act.
	///
	/// <b>A complete checkpoint is six things</b>: the save blob, the random state
	/// (<see cref="SimRandom"/>), the scheduling counters (<see cref="SimScheduling"/>), the
	/// unstable-solid countdowns (<see cref="SimStableTicks"/>), the disease growth
	/// remainders (<see cref="SimDiseaseGrowth"/>), and this.
	///
	/// <b>The blob is opaque and that is deliberate.</b> The sim writes it and the sim parses
	/// it; nothing here or in any caller looks inside. The alternative shape -- rebuilding the
	/// registries with the vanilla <c>SetElementChunkData</c> and <c>ModifyBuildingHeatExchange</c>
	/// messages -- would have required every caller to recompute chunk heat capacities and
	/// building registration fields out of the element table, mirroring the sim's own
	/// bookkeeping, and would still have left the flow accumulators and the radiation timers
	/// uncarried. The blob carries its own magic, version and lengths, and the sim validates
	/// all of them before it moves anything into place.
	///
	/// <b>Who needs this.</b> The same callers as <see cref="SimStableTicks"/> and
	/// <see cref="SimDiseaseGrowth"/> -- anything reconstructing a run rather than resuming
	/// one: a scrub timeline, a replay harness, a deterministic regression check. Ordinary
	/// gameplay does not, because the game re-registers everything itself.
	///
	/// <b>This does not change the default.</b> A world that never calls
	/// <see cref="SetState"/> behaves exactly as before -- measured, not assumed: the same
	/// corpus through <c>diffsim</c> against a build without this gives 98 scenarios
	/// identical for all 50 ticks, zero divergences, and byte-identical crossload blobs.
	/// </summary>
	public static class SimRegistryState
	{
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugRegistryState(byte* outBlob, int capacity);

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
		/// The sim's current component registries, serialized.
		///
		/// The length is <b>not</b> derivable from the world's dimensions -- it depends on how
		/// much the game has registered -- so the sizing call is the only way to learn it, and
		/// the sim builds the blob for both calls. Read this at the same moment as the save
		/// blob and the other four pieces: all six together are one checkpoint.
		///
		/// Returns false with a null blob on a SimDLL without the export, and on a sim with no
		/// allocated world.
		/// </summary>
		public static unsafe bool TryGetState(out byte[] blob)
		{
			blob = null;
			if (unavailable)
			{
				return false;
			}
			int count;
			try
			{
				count = SIM_DebugRegistryState(null, 0);
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
			byte[] b = new byte[count];
			fixed (byte* p = b)
			{
				if (SIM_DebugRegistryState(p, count) != count)
				{
					return false;
				}
			}
			blob = b;
			return true;
		}

		/// <summary>
		/// Restores the component registries from a blob <see cref="TryGetState"/> produced.
		///
		/// <b>Send it after the Load</b>, in the same position as
		/// <see cref="SimRandom.SetState"/> and the other three. It replaces all four
		/// registries wholesale, so it belongs on a world that has just been reallocated: on
		/// one whose registries are already populated it is a change rather than a restore.
		///
		/// <b>Do not combine it with re-sending the original registration messages.</b> Both
		/// arrive as queued messages on the same frame and the sim drains the component
		/// registrations <i>after</i> this one, so the replayed registrations would pile a
		/// second copy of everything on top of what this restored.
		///
		/// <b>Timing.</b> Queued like every sim extension message: it lands at the top of the
		/// frame after the one it was sent during, ahead of that frame's physics.
		///
		/// Applied whole or not at all: a blob that is truncated, or from a build whose
		/// structures changed, is rejected and logged rather than leaving half a colony
		/// registered. Safe to call on a stock SimDLL -- an unrecognised message id, which the
		/// sim drops. Passing null or empty does nothing.
		/// </summary>
		public static unsafe void SetState(byte[] blob)
		{
			if (blob == null || blob.Length == 0)
			{
				return;
			}
			// The payload IS the blob: no header in front of it, because it carries its own
			// magic, version and lengths.
			fixed (byte* msg = blob)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetRegistryState, blob.Length, msg);
			}
		}
	}
}
