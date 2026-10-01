using System;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// WATER THAT LOOKS LIKE IT IS CARRYING SOMETHING -- the managed half of the SimDLL's
	/// <c>kSetDissolvedTint</c>.
	///
	/// <see cref="DissolvedGas"/> has always been invisible in the world. A pond at soda
	/// strength and a pond of plain water render byte-identically, because the liquid property
	/// texture's RGB is a walk of the ELEMENT's own <c>gradientColours</c> and a dissolved amount
	/// is not an element. Turning this on makes the sim blend each liquid cell's rendered colour
	/// part of the way towards the colour of whatever is dissolved in it, so the carbonation is
	/// visible without an overlay, under the waves, the caustics and the refraction, because the
	/// texture it changes is the one <c>Klei/Liquid</c> was already reading.
	///
	/// WHY THE COLOURS COME FROM HERE. A lane is a managed-side assignment -- which gas owns
	/// which lane is content data, and <see cref="DissolvedGas.LaneElement"/> is the only place
	/// that mapping exists -- and the sim has no colour table for elements at all. So this class
	/// reads each lane's element, takes its <c>substance.uiColour</c> and its
	/// <c>molarMass</c>, and sends both.
	///
	/// THE BLEND IS STATIONEERS'. <c>Atmosphere.GetGasColor()</c> sums
	/// each species' colour times its share of the mixture's total QUANTITY, which is moles, and
	/// <c>GetLiquidColor</c> immediately above it does the same for a liquid mixture. That is
	/// what the sim computes, from the reciprocal molar masses this class sends as weights. What
	/// is NOT Stationeers' is the second axis: a
	/// Stationeers atmosphere is one uniform volume whose colour only has to say WHAT is in it,
	/// while a grid cell's colour also has to say HOW MUCH, so concentration drives the distance
	/// travelled towards that colour and the blend drives only its direction.
	///
	/// OFF UNTIL A CALLER ASKS. A SimDLL that is never sent this message renders exactly as it
	/// did before the message existed, which is what lets the offline goldens stay bit-identical
	/// across the version that added it.
	/// </summary>
	public static class LiquidTint
	{
		/// <summary>
		/// The SimDLL's own default full scale: grams of dissolved gas per kilogram of liquid at
		/// which a cell paints at full strength. 10 g/kg -- carbon dioxide saturates water at
		/// about 1.7 g/kg at one atmosphere and 293 K, and a four-atmosphere carbonation vessel
		/// holds its pond near 6, so ordinary carbonated water lands about four fifths up the
		/// ramp with headroom above it.
		/// </summary>
		public const float DefaultFullScaleGramsPerKg = 10f;

		/// <summary>
		/// The SimDLL's own default blend ceiling: how far a cell at full scale moves towards the
		/// dissolved mixture's colour. 0.45 -- below about 0.25 the difference does not survive
		/// the wave and caustic layers drawn over it, and above about 0.6 water starts reading as
		/// a different liquid rather than as water carrying something.
		/// </summary>
		public const float DefaultMaxBlend = 0.45f;

		/// <summary>Whether the last <see cref="Push"/> asked for the tint to be on. Reset by a
		/// <see cref="Disable"/>; this is what was ASKED for, not a readback.</summary>
		public static bool Enabled { get; private set; }

		/// <summary>The full scale the last <see cref="Push"/> sent.</summary>
		public static float FullScaleGramsPerKg { get; private set; }

		/// <summary>The blend ceiling the last <see cref="Push"/> sent.</summary>
		public static float MaxBlend { get; private set; }

		/// <summary>
		/// Turn the tint on and tell the sim what every assigned dissolved lane looks like.
		///
		/// The colour of a lane is its gas's <c>substance.uiColour</c> -- the colour Klei's own
		/// overlays and UI use to tell elements apart -- and not <c>substance.colour</c>, which
		/// is the colour the world renderer draws the element with and is a near-neutral grey for
		/// several gases (carbon dioxide's is literally (30,30,30)). A blend of near-greys
		/// carries no information, and a tint that carries no information is a cost with no
		/// benefit.
		///
		/// The weight of a lane is one over its gas's <c>molarMass</c>, so the sim's blend is by
		/// mole fraction. A lane with no gas assigned sends weight 0 and is ignored by the blend,
		/// though anything sitting in it still counts towards the concentration -- an unnamed gas
		/// is really in the water and should not be able to hide.
		///
		/// Queued, and NOT saved: it describes how to draw a property that is re-registered from
		/// scratch every session, so send it from wherever the rest of a mod's sim configuration
		/// is sent. Inert on a SimDLL without dissolved tint, which ignores an id it does not know.
		/// </summary>
		/// <param name="fullScaleGramsPerKg">Concentration at which a cell paints at full
		/// strength. Anything not positive is replaced by the sim's own default.</param>
		/// <param name="maxBlend">How far a full-strength cell moves towards the mixture colour,
		/// clamped by the sim to 0..1.</param>
		public static unsafe void Push(float fullScaleGramsPerKg = DefaultFullScaleGramsPerKg,
			float maxBlend = DefaultMaxBlend)
		{
			Send(true, fullScaleGramsPerKg, maxBlend);
		}

		/// <summary>
		/// Switch the tint off. Every liquid cell goes back to the colour its element alone gives
		/// it on the next texture sweep; nothing else about the dissolved gas changes, because
		/// this message has never described anything but how to draw it.
		/// </summary>
		public static unsafe void Disable()
		{
			Send(false, FullScaleGramsPerKg > 0f ? FullScaleGramsPerKg : DefaultFullScaleGramsPerKg,
				MaxBlend);
		}

		private static unsafe void Send(bool enabled, float fullScaleGramsPerKg, float maxBlend)
		{
			int lanes = DissolvedGas.Lanes;

			// int32 enabled, float fullScale, float maxBlend, float[lanes], uint32[lanes] --
			// oni_sim::ext::SetDissolvedTintMessage, which is 76 bytes at 8 lanes and is
			// static_assert'd to that size on the sim side.
			byte[] payload = new byte[12 + lanes * 8];
			Buffer.BlockCopy(BitConverter.GetBytes(enabled ? 1 : 0), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(fullScaleGramsPerKg), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(maxBlend), 0, payload, 8, 4);

			int weightsAt = 12;
			int coloursAt = 12 + lanes * 4;
			for (int lane = 0; lane < lanes; lane++)
			{
				float weight = 0f;
				uint colour = 0u;
				SimHashes gas = DissolvedGas.LaneElement(lane);
				if (gas != (SimHashes)0)
				{
					Element element = ElementLoader.FindElementByHash(gas);
					if (element != null)
					{
						if (element.molarMass > 0f)
						{
							weight = 1f / element.molarMass;
						}
						if (element.substance != null)
						{
							Color32 ui = element.substance.uiColour;
							colour = ((uint)ui.r << 16) | ((uint)ui.g << 8) | ui.b;
						}
					}
				}
				Buffer.BlockCopy(BitConverter.GetBytes(weight), 0, payload, weightsAt + lane * 4, 4);
				Buffer.BlockCopy(BitConverter.GetBytes(colour), 0, payload, coloursAt + lane * 4, 4);
			}

			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetDissolvedTint, payload.Length, msg);
			}

			Enabled = enabled;
			FullScaleGramsPerKg = fullScaleGramsPerKg;
			MaxBlend = maxBlend;
		}
	}
}
