using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>Which phase boundary a vanilla element transition crosses.</summary>
	/// <remarks>
	/// Not cosmetic: each boundary has its OWN threshold rule and its OWN latent-heat cost, and
	/// Stationeers keeps them strictly separate -- its state change gates boiling and
	/// condensation on the pressure-dependent vapor curve, while its freeze and melt passes gate
	/// on a flat freezing temperature, and the two charge the latent heat of fusion and of
	/// vaporization respectively.
	/// </remarks>
	public enum PhaseTransitionKind
	{
		None,
		/// <summary>liquid &lt;-&gt; gas. Pressure-dependent threshold (the vapor curve).</summary>
		Vaporization,
		/// <summary>solid &lt;-&gt; liquid. Effectively pressure-independent threshold.</summary>
		Fusion,
		/// <summary>solid &lt;-&gt; gas. Crosses both boundaries, so it costs both latent heats.</summary>
		Sublimation,
	}

	/// <summary>
	/// A ONE-TEMPERATURE VESSEL WITH REAL PHASE CHANGE: a list of species (element index -> kg)
	/// sharing one temperature inside a fixed volume, whose contents boil, condense, melt and
	/// freeze with their latent heat paid from the whole vessel's heat capacity. Stationeers'
	/// <c>Atmosphere</c> inside a <c>StateChangeDevice</c> or a tank, for any building that holds
	/// matter off the grid.
	///
	/// WHO USES IT. The flagship thermodynamics mod's gas mixture tank and its Evaporation and
	/// Condensation Chambers (modelled on Stationeers' <c>StateChangeDevice</c>) all run it from
	/// here, and so can a third-party mod building its own boiler, still or condenser.
	///
	/// ONE TICK, <see cref="ApplyPhaseChangeStep"/>: scan the species for one whose temperature
	/// has crossed the threshold of a real vanilla transition, convert the energy-affordable
	/// share (<see cref="GasMixtureFacade.ComputePhaseChangeStep"/>, Stationeers' 10 % per second)
	/// to the vanilla transition target in the same lists, and pay the latent heat against the
	/// vessel's TOTAL heat capacity, clamped at the threshold. At most one species per tick --
	/// vanilla's own one-transition-per-cell-per-substep rule (<c>sim/physics.h</c>).
	///
	/// THE STORAGE STAYS THE HOST'S. A <see cref="PhaseVessel"/> wraps the host's own two lists by
	/// reference, so a building keeps serializing them with <c>[Serialize]</c> exactly as before
	/// and no framework type ever enters a save. The temperature is a value, so the host copies
	/// <see cref="TemperatureK"/> into its own serialized field before a save
	/// (<c>[OnSerializing]</c>), and builds the vessel after load (OnSpawn), when the
	/// deserializer has put its lists in place.
	///
	/// UNITS. <c>Element.specificHeatCapacity</c> is Klei's raw J/(g K); every heat capacity here
	/// is J/K (x1000 at the point it meets a J/kg latent heat), and the one place that must NOT
	/// be scaled is a heat capacity handed back to Klei's own chunk kernel.
	/// </summary>
	public sealed class PhaseVessel
	{
		/// <summary>
		/// Stationeers' own 10 %-per-tick shape (it converts 10% of the computed quantity per
		/// tick) -- a gradual conversion, not an instant flash.
		/// </summary>
		public const float PhaseChangeRatePerSecond = 0.1f;

		/// <summary>
		/// "Don't leave physically meaningless dust behind": the same role as Stationeers'
		/// <c>MINIMUM_WORLD_VALID_TOTAL_MOLES</c>, in kg. A first practical floor, not a
		/// physically derived constant -- the same category of choice as <c>gas_mixture.h</c>'s
		/// own <c>kMinTransferMass</c>.
		/// </summary>
		public const float PhaseChangeMinRemainderKg = 0.01f;

		/// <summary>
		/// <c>Element.specificHeatCapacity</c> is Klei's raw J/(g K) (Water = 4.179), so a heat
		/// capacity that meets a J/kg latent heat needs this factor; without it the converted mass
		/// is undercounted exactly 1000x.
		/// </summary>
		private const float GramsPerKilogram = 1000f;

		private const float LitresPerCubicMetre = 1000f;

		/// <param name="elementIdx">The host's species list (element index), used by reference.</param>
		/// <param name="massKg">The host's mass list, kg, parallel to <paramref name="elementIdx"/>.</param>
		/// <param name="temperatureK">The shared temperature, K.</param>
		/// <param name="volumeLitres">The vessel's whole volume, L; liquid displaces gas in it.</param>
		/// <param name="minimumGasVolumeLitres">Floor on the gas space, L, so a vessel full of
		/// liquid never divides by zero (Stationeers floors it at 0.1 L).</param>
		public PhaseVessel(List<int> elementIdx, List<float> massKg, float temperatureK,
			float volumeLitres, float minimumGasVolumeLitres)
		{
			ElementIdx = elementIdx;
			MassKg = massKg;
			TemperatureK = temperatureK;
			VolumeLitres = volumeLitres;
			MinimumGasVolumeLitres = minimumGasVolumeLitres;
		}

		/// <summary>The species, by element index. The host's own list.</summary>
		public List<int> ElementIdx { get; }

		/// <summary>Each species' mass, kg, parallel to <see cref="ElementIdx"/>.</summary>
		public List<float> MassKg { get; }

		/// <summary>The one temperature every species shares, K.</summary>
		public float TemperatureK { get; set; }

		public float VolumeLitres { get; }

		public float MinimumGasVolumeLitres { get; }

		/// <summary>Everything in the vessel, m*c, J/K.</summary>
		public float HeatCapacityJPerK()
		{
			return HeatCapacityJPerK(ElementIdx, MassKg);
		}

		/// <summary>The volume the liquids take, L, at their registered densities.</summary>
		public float LiquidLitres()
		{
			float litres = 0f;
			for (int i = 0; i < ElementIdx.Count; i++)
			{
				Element element = ElementLoader.elements[ElementIdx[i]];
				if (element.IsLiquid
					&& MaterialPropertyRegistry.TryGetLiquidDensityKgPerM3(element.id,
						out float density) && density > 0f)
				{
					litres += MassKg[i] / density * LitresPerCubicMetre;
				}
			}
			return litres;
		}

		/// <summary>What the liquid leaves for gas, L: Stationeers' <c>Volume - liquids</c>,
		/// floored.</summary>
		public float GasVolumeLitres()
		{
			return Mathf.Max(VolumeLitres - LiquidLitres(), MinimumGasVolumeLitres);
		}

		public float GasMassKg()
		{
			return PhaseMassKg(gas: true);
		}

		public float LiquidMassKg()
		{
			return PhaseMassKg(gas: false);
		}

		public float TotalMassKg()
		{
			float kg = 0f;
			for (int i = 0; i < MassKg.Count; i++)
			{
				kg += MassKg[i];
			}
			return kg;
		}

		/// <summary>Sensible heat of everything in the vessel, m*c*T, J.</summary>
		public float SensibleHeatJ()
		{
			return HeatCapacityJPerK() * TemperatureK;
		}

		/// <summary>The gas species' amount, mol.</summary>
		public float GasMoles()
		{
			float moles = 0f;
			for (int i = 0; i < ElementIdx.Count; i++)
			{
				if (ElementLoader.elements[ElementIdx[i]].IsGas)
				{
					moles += PipeNetworkFacade.MolesOf(ElementIdx[i], MassKg[i]);
				}
			}
			return moles;
		}

		/// <summary>
		/// The gas pressure over <see cref="GasVolumeLitres"/>, Pa (the gas-only pressure
		/// Stationeers reports), through the one native P = nRT/V
		/// (<see cref="GasMixtureFacade.TryComputePressure"/>) the framework uses everywhere. 0 with no gas.
		/// </summary>
		public float PressurePa()
		{
			if (GasMoles() <= 0f || TemperatureK <= 0f)
			{
				return 0f;
			}

			var idx = new List<int>();
			var mass = new List<float>();
			for (int i = 0; i < ElementIdx.Count; i++)
			{
				if (ElementLoader.elements[ElementIdx[i]].IsGas && MassKg[i] > 0f)
				{
					idx.Add(ElementIdx[i]);
					mass.Add(MassKg[i]);
				}
			}
			return GasMixtureFacade.TryComputePressure(idx.ToArray(), mass.ToArray(),
				TemperatureK, GasVolumeLitres() / LitresPerCubicMetre,
				out float pressurePa) ? pressurePa : 0f;
		}

		/// <summary>
		/// Adds matter at its own temperature and mixes calorimetrically. Returns the sensible
		/// heat it carried in, m*c*T in J, for a host's energy ledger.
		/// </summary>
		/// <remarks>Not an adiabatic fill: a host that compresses gas in bills that work itself,
		/// and adding the adiabatic temperature rise here as well would charge it twice.</remarks>
		public float Add(int elementIdx, float massKg, float temperatureK)
		{
			if (massKg <= 0f)
			{
				return 0f;
			}
			Element element = ElementLoader.elements[elementIdx];
			float incomingHeatCapacity = massKg * element.specificHeatCapacity * GramsPerKilogram;
			float existingHeatCapacity = HeatCapacityJPerK();
			float total = existingHeatCapacity + incomingHeatCapacity;
			if (total > 0f)
			{
				TemperatureK = (existingHeatCapacity * TemperatureK
					+ incomingHeatCapacity * temperatureK) / total;
			}

			int i = ElementIdx.IndexOf(elementIdx);
			if (i >= 0)
			{
				MassKg[i] += massKg;
			}
			else
			{
				ElementIdx.Add(elementIdx);
				MassKg.Add(massKg);
			}
			return incomingHeatCapacity * temperatureK;
		}

		/// <summary>
		/// Takes <paramref name="massKg"/> out of <paramref name="slot"/> at the vessel's
		/// temperature, dropping the slot when it empties. Returns the sensible heat it carried
		/// out, J.
		/// </summary>
		/// <remarks>Emptying a slot shifts every later slot down, so a caller walking the slots
		/// while removing walks them from the last one back.</remarks>
		public float Remove(int slot, float massKg)
		{
			if (massKg <= 0f)
			{
				return 0f;
			}
			Element element = ElementLoader.elements[ElementIdx[slot]];
			float sensibleJ = massKg * element.specificHeatCapacity * GramsPerKilogram * TemperatureK;
			MassKg[slot] -= massKg;
			if (MassKg[slot] <= 0f)
			{
				MassKg.RemoveAt(slot);
				ElementIdx.RemoveAt(slot);
			}
			return sensibleJ;
		}

		/// <summary>
		/// Puts <paramref name="joules"/> into the vessel as one temperature change (negative
		/// takes heat out). False, and nothing changes, when the vessel is empty: an empty vessel
		/// has nowhere to put energy.
		/// </summary>
		public bool AddHeat(float joules)
		{
			float heatCapacity = HeatCapacityJPerK();
			if (heatCapacity <= 0f)
			{
				return false;
			}
			TemperatureK += joules / heatCapacity;
			return true;
		}

		/// <summary>
		/// One tick of real phase change in this vessel -- <see cref="ApplyPhaseChangeStep"/> over
		/// its own lists and temperature.
		/// </summary>
		/// <param name="vaporPressurePa">The pressure the vapor curve is read at. A host decides
		/// which: the tank passes its vapor-only partial pressure, the chambers their whole
		/// <see cref="PressurePa"/>.</param>
		public bool StepPhaseChange(float vaporPressurePa, float dtSeconds,
			out PhaseTransitionKind kind, out bool rising, out float convertedKg,
			out float latentHeatJ)
		{
			float temperatureK = TemperatureK;
			bool converted = ApplyPhaseChangeStep(ElementIdx, MassKg, ref temperatureK,
				vaporPressurePa, dtSeconds, out kind, out rising, out convertedKg, out latentHeatJ);
			TemperatureK = temperatureK;
			return converted;
		}

		// ------------------------------------------------------------------------------------
		// The phase-change rule itself, over any one-temperature pair of species lists.
		// ------------------------------------------------------------------------------------

		/// <summary>m*c over a pair of species lists, J/K.</summary>
		public static float HeatCapacityJPerK(List<int> elementIdx, List<float> massKg)
		{
			float heatCapacity = 0f;
			for (int i = 0; i < elementIdx.Count; i++)
			{
				heatCapacity += massKg[i]
					* ElementLoader.elements[elementIdx[i]].specificHeatCapacity
					* GramsPerKilogram;
			}
			return heatCapacity;
		}

		/// <summary>
		/// One tick of phase change over a one-temperature vessel's species lists: converts at
		/// most one species, pays the latent heat against the whole vessel's heat capacity
		/// (clamped at the threshold), and relabels the converted mass in place -- a phase change
		/// moves matter nowhere. Returns true when something converted, with the boundary it
		/// crossed, the direction, the mass and the latent heat it took (+, boiling or melting)
		/// or released (-, condensing or freezing), J.
		///
		/// Each candidate is classified by the boundary it crosses so that boiling/condensation,
		/// melting/freezing and sublimation each get their own threshold rule and latent heat.
		/// One pressure-dependent vapor threshold for every transition would move a liquid's
		/// freezing point with its vapor pressure.
		///
		/// Species with no latent heat registered never phase-change: inert rather than a guessed
		/// value, the same "fall back to doing nothing" contract as the facades' TryGet* methods.
		/// </summary>
		public static bool ApplyPhaseChangeStep(List<int> elementIdx, List<float> massKgList,
			ref float temperatureK, float vaporPressurePa, float dtSeconds,
			out PhaseTransitionKind convertedKind, out bool convertedRising, out float convertedKg,
			out float latentHeatJ)
		{
			convertedKind = PhaseTransitionKind.None;
			convertedRising = false;
			convertedKg = 0f;
			latentHeatJ = 0f;

			for (int i = 0; i < elementIdx.Count; i++)
			{
				float massKg = massKgList[i];
				if (massKg <= 0f)
				{
					continue;
				}
				Element element = ElementLoader.elements[elementIdx[i]];

				if (!TrySelectTransition(element, temperatureK, vaporPressurePa, out bool rising,
					out float thresholdK, out Element targetElement, out PhaseTransitionKind kind))
				{
					continue;
				}

				if (!TryGetTransitionLatentHeatJPerKg(kind, element, targetElement,
					out float latentHeatJPerKg))
				{
					continue;
				}

				int targetIdx = ElementLoader.elements.IndexOf(targetElement);
				if (targetIdx < 0)
				{
					continue;
				}

				GasMixtureFacade.ComputePhaseChangeStep(massKg, temperatureK, thresholdK,
					latentHeatJPerKg, element.specificHeatCapacity * GramsPerKilogram, dtSeconds,
					PhaseChangeRatePerSecond, PhaseChangeMinRemainderKg,
					out float convertedMassKg, out float _);
				if (convertedMassKg <= 0f)
				{
					continue;
				}

				float totalHeatCapacity = HeatCapacityJPerK(elementIdx, massKgList);
				if (totalHeatCapacity > 0f)
				{
					float energyForConversionJ = convertedMassKg * latentHeatJPerKg;
					float deltaTempK = energyForConversionJ / totalHeatCapacity;
					temperatureK = rising ? temperatureK - deltaTempK : temperatureK + deltaTempK;
					temperatureK = rising ? Mathf.Max(temperatureK, thresholdK) : Mathf.Min(temperatureK, thresholdK);
				}

				massKgList[i] -= convertedMassKg;
				int targetSlot = elementIdx.IndexOf(targetIdx);
				if (targetSlot >= 0)
				{
					massKgList[targetSlot] += convertedMassKg;
				}
				else
				{
					elementIdx.Add(targetIdx);
					massKgList.Add(convertedMassKg);
				}
				if (massKgList[i] <= 0f)
				{
					massKgList.RemoveAt(i);
					elementIdx.RemoveAt(i);
				}

				convertedKind = kind;
				convertedRising = rising;
				convertedKg = convertedMassKg;
				latentHeatJ = rising ? convertedMassKg * latentHeatJPerKg : -convertedMassKg * latentHeatJPerKg;

				// One transition per tick -- real vanilla's own rule (sim/physics.h).
				return true;
			}
			return false;
		}

		/// <summary>
		/// Picks the one transition (if any) <paramref name="element"/> should undergo at
		/// <paramref name="temperatureK"/>. The high-temperature side is considered first,
		/// matching vanilla's <c>DoStateTransition</c> ordering.
		///
		/// The threshold depends on which boundary is being crossed, which depends on the
		/// transition TARGET -- so the target is resolved and classified BEFORE a threshold is
		/// computed. One threshold computed up front and reused for both branches is what let the
		/// liquid-&gt;gas vapor curve leak into the liquid-&gt;solid freezing check.
		/// </summary>
		public static bool TrySelectTransition(Element element, float temperatureK,
			float vaporPressurePa, out bool rising, out float thresholdK, out Element targetElement,
			out PhaseTransitionKind kind)
		{
			rising = false;
			thresholdK = 0f;
			targetElement = null;
			kind = PhaseTransitionKind.None;

			if (HasRealTransition(element, element.highTempTransitionTarget, element.highTempTransition))
			{
				Element candidate = element.highTempTransition;
				PhaseTransitionKind candidateKind = ClassifyTransition(element, candidate);
				if (candidateKind != PhaseTransitionKind.None)
				{
					float candidateThresholdK = TransitionThresholdK(candidateKind, element, vaporPressurePa, rising: true);
					if (temperatureK > candidateThresholdK)
					{
						rising = true;
						thresholdK = candidateThresholdK;
						targetElement = candidate;
						kind = candidateKind;
						return true;
					}
				}
			}

			if (HasRealTransition(element, element.lowTempTransitionTarget, element.lowTempTransition))
			{
				Element candidate = element.lowTempTransition;
				PhaseTransitionKind candidateKind = ClassifyTransition(element, candidate);
				if (candidateKind != PhaseTransitionKind.None)
				{
					float candidateThresholdK = TransitionThresholdK(candidateKind, element, vaporPressurePa, rising: false);
					if (temperatureK < candidateThresholdK)
					{
						rising = false;
						thresholdK = candidateThresholdK;
						targetElement = candidate;
						kind = candidateKind;
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// The temperature at which <paramref name="kind"/>'s boundary sits for this species right
		/// now.
		///
		/// Only <see cref="PhaseTransitionKind.Vaporization"/> is pressure-dependent, and it uses
		/// the CLAMPED vapor curve (<see cref="GasMixtureFacade.TryGetEvaporationTemperatureClampedK"/>),
		/// never the raw one -- see that method for the unphysical 89.28 K the raw curve gives.
		/// Fusion and sublimation use vanilla's fixed <c>highTemp</c>/
		/// <c>lowTemp</c>, which is correct rather than a compromise: a real melting point barely
		/// moves with pressure, and Stationeers gates its freeze/melt on a flat temperature for
		/// that reason. A vaporizing species with no curve data also falls back to the vanilla
		/// constant.
		/// </summary>
		public static float TransitionThresholdK(PhaseTransitionKind kind, Element element,
			float vaporPressurePa, bool rising)
		{
			if (kind == PhaseTransitionKind.Vaporization &&
				GasMixtureFacade.TryGetEvaporationTemperatureClampedK(element.id, vaporPressurePa,
					out float curveThresholdK))
			{
				return curveThresholdK;
			}
			return rising ? element.highTemp : element.lowTemp;
		}

		/// <summary>
		/// The latent-heat cost (J/kg) of <paramref name="kind"/>'s boundary, tried against the
		/// source element first and the target second. The table is keyed by the species that
		/// carries the data, so a transition from a phase with no entry of its own still resolves
		/// through its counterpart; Water-&gt;Ice and Ice-&gt;Water charge the same fusion value.
		/// </summary>
		public static bool TryGetTransitionLatentHeatJPerKg(PhaseTransitionKind kind, Element from,
			Element to, out float latentHeatJPerKg)
		{
			return TryGetLatentHeatFor(kind, from, out latentHeatJPerKg) ||
				TryGetLatentHeatFor(kind, to, out latentHeatJPerKg);
		}

		/// <summary>The boundary a transition from <paramref name="from"/> to
		/// <paramref name="to"/> crosses.</summary>
		public static PhaseTransitionKind ClassifyTransition(Element from, Element to)
		{
			if (from == null || to == null)
			{
				return PhaseTransitionKind.None;
			}
			if ((from.IsLiquid && to.IsGas) || (from.IsGas && to.IsLiquid))
			{
				return PhaseTransitionKind.Vaporization;
			}
			if ((from.IsLiquid && to.IsSolid) || (from.IsSolid && to.IsLiquid))
			{
				return PhaseTransitionKind.Fusion;
			}
			if ((from.IsSolid && to.IsGas) || (from.IsGas && to.IsSolid))
			{
				return PhaseTransitionKind.Sublimation;
			}
			return PhaseTransitionKind.None;
		}

		private float PhaseMassKg(bool gas)
		{
			float kg = 0f;
			for (int i = 0; i < ElementIdx.Count; i++)
			{
				Element element = ElementLoader.elements[ElementIdx[i]];
				if (gas ? element.IsGas : element.IsLiquid)
				{
					kg += MassKg[i];
				}
			}
			return kg;
		}

		/// <summary>
		/// Mirrors Klei's <c>Element.HasTransitionUp</c> exactly -- 0 and
		/// Unobtanium are vanilla's "no real transition" sentinels, and a target resolving back to
		/// itself is not a transition either. No managed <c>HasTransitionDown</c> exists
		/// (<c>DoStateTransition</c>'s low side is native-only), so the same shape serves both.
		/// </summary>
		private static bool HasRealTransition(Element element, SimHashes targetHash, Element target)
		{
			return targetHash != (SimHashes)0 && targetHash != SimHashes.Unobtanium &&
				target != null && target != element;
		}

		private static bool TryGetLatentHeatFor(PhaseTransitionKind kind, Element element,
			out float latentHeatJPerKg)
		{
			if (element != null)
			{
				switch (kind)
				{
					case PhaseTransitionKind.Vaporization:
						return GasMixtureFacade.TryGetLatentHeatJPerKg(element.id,
							out latentHeatJPerKg);
					case PhaseTransitionKind.Fusion:
						return GasMixtureFacade.TryGetLatentHeatOfFusionJPerKg(element.id,
							out latentHeatJPerKg);
					case PhaseTransitionKind.Sublimation:
						return GasMixtureFacade.TryGetLatentHeatOfSublimationJPerKg(element.id,
							out latentHeatJPerKg);
				}
			}
			latentHeatJPerKg = 0f;
			return false;
		}
	}
}
