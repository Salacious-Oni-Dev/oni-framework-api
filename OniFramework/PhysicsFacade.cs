using System;

namespace OniFramework
{
	/// <summary>
	/// Managed half of the framework's direct physics-kernel extensions -- as opposed to
	/// <see cref="GasMixtureFacade"/>, which is specifically the volume-fractions gas-mixture
	/// layer. First (and so far only) member: <see cref="SetInvertedGravityElement"/>.
	/// </summary>
	public static class PhysicsFacade
	{
		/// <summary>
		/// Flips the vertical sign of <paramref name="elementIdx"/>'s own liquid turn in the
		/// native sim's StepFlow kernel (oni_sim::ext::kSetInvertedGravityElement,
		/// abi/sim_abi_ext.h) -- that element falls up and pools under solid ceilings instead
		/// of on floors. This is a real change to the physics kernel itself, not a managed-side
		/// per-frame cell swap layered on top of an untouched vanilla sim.
		///
		/// <paramref name="elementIdx"/> is an ElementTable index (e.g.
		/// <c>ElementLoader.elements.IndexOf(...)</c>), the same convention every other message
		/// in this framework uses -- not a <c>SimHashes</c> id. Pass -1 to clear the effect and
		/// restore ordinary gravity for every element; only one element can be inverted at a
		/// time, so calling this again replaces whichever element was inverted before rather
		/// than adding a second one.
		///
		/// Only ever sends a message and reads nothing back, so like
		/// <see cref="GasMixtureFacade.Inject"/> this reuses Klei's own
		/// <c>Sim.SIM_HandleMessage</c> P/Invoke rather than needing its own SimDLL binding.
		/// </summary>
		public static unsafe void SetInvertedGravityElement(int elementIdx)
		{
			byte[] payload = new byte[4];
			Buffer.BlockCopy(BitConverter.GetBytes(elementIdx), 0, payload, 0, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetInvertedGravityElement, payload.Length, msg);
			}
		}
	}
}
