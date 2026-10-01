using System;

namespace OniFramework
{
	/// <summary>
	/// The Stirling-cycle power arithmetic, with no game types in it.
	///
	/// This is the same split <see cref="ConversionEnthalpyRule"/> uses and it exists for the same
	/// reason: the recipe READING is game-specific and belongs with the building, but the
	/// arithmetic is not, and arithmetic that lives inside a <c>KMonoBehaviour</c> can only be
	/// exercised by launching Oxygen Not Included. Everything here is a pure function of floats,
	/// so a third-party mod can predict this engine's output without owning one, and this
	/// project's probes can compute a closed-form expectation BEFORE they look at the live
	/// machine -- which is the only shape of test that can fail honestly.
	///
	/// PORTED FROM, not invented: Stationeers' Stirling engine. Its tick reduces to one line:
	///
	/// <code>
	/// EnergyAsPower = Min(MaxPower, |E_hot - E_cold|
	///                   * _workingGasEfficiency
	///                   * _machineEnvironmentEfficiency
	///                   * MachinePressureDifferentialEfficiency);
	/// _hotSideAtmosphere.GasMixture.RemoveEnergy(EnergyAsPower);
	/// </code>
	///
	/// THE SECOND LINE IS AS LOAD-BEARING AS THE FIRST, and it is why this class was ported at all.
	/// Exactly the energy that left as electricity is removed from the working fluid -- one call,
	/// for that amount, and nothing else is deleted. Every joule the engine did NOT convert is
	/// still in the gas and leaves again through the exchangers: no heat is deleted.
	///
	/// WHAT IS DELIBERATELY NOT PORTED. Stationeers' <c>MachineEfficiency</c> is an
	/// <c>AnimationCurve</c> evaluated against world temperature, and in the shipped game it is
	/// FLAT 1.0 from 0 K to 3000 K -- so it is ported as the constant it measurably is
	/// (<see cref="MachineEnvironmentEfficiency"/>) rather than as a fake curve that would imply a
	/// variation nobody can observe. Its explosion behaviour, its RPM model and its canister
	/// venting are gameplay, not cycle arithmetic, and are not here.
	/// </summary>
	public static class StirlingCycleRule
	{
		/// <summary>
		/// Stationeers' <c>MachineEfficiency.Evaluate(worldTemperature)</c>, ported as a constant
		/// because the shipped curve is flat 1.0 across its whole 0 K - 3000 K domain.
		///
		/// Kept as a named constant rather than dropped from the product entirely so that the
		/// third factor of "efficiency is a product of three factors, each &lt;= 1" is visible in
		/// the code, and so that a later mod that wants a real environment curve has an obvious
		/// place to put it. Multiplying by it costs one instruction and documents the port.
		/// </summary>
		public const float MachineEnvironmentEfficiency = 1f;

		/// <summary>
		/// Stationeers' <c>IdealPressureDifferential</c>, 3000 kPa: the hot-to-cold pressure
		/// difference at or above which the engine is at its best. Expressed here in PASCALS
		/// (3 MPa) because every pressure this project handles on the ONI side is in Pa, and a
		/// silent kPa/Pa mix is exactly the class of unit bug this codebase has already shipped
		/// twice.
		/// </summary>
		public const float IdealPressureDifferentialPa = 3000000f;

		/// <summary>
		/// The efficiency contributed by the pressure differential across the machine:
		/// <c>Clamp01(dP / IdealPressureDifferential)</c>, with <c>dP</c> floored at zero so a
		/// backwards differential contributes nothing rather than negative power.
		///
		/// <paramref name="hotSidePressurePa"/> and <paramref name="coldSidePressurePa"/> are the
		/// pressures of the working charge on each side, not of the fluids being exchanged with.
		/// </summary>
		public static float PressureDifferentialEfficiency(float hotSidePressurePa,
			float coldSidePressurePa)
		{
			float dP = hotSidePressurePa - coldSidePressurePa;
			if (!(dP > 0f))
			{
				// Covers NaN as well as a zero or reversed differential: !(NaN > 0) is true.
				return 0f;
			}
			float e = dP / IdealPressureDifferentialPa;
			return e > 1f ? 1f : e;
		}

		/// <summary>
		/// The electrical power the cycle produces, in the same energy unit
		/// <paramref name="hotSideEnergy"/> and <paramref name="coldSideEnergy"/> are expressed in.
		///
		/// Unit-agnostic on purpose. Stationeers passes <c>MoleEnergy</c>; this project's caller
		/// passes kilojoules and divides by the tick length to reach kilowatts. What matters is
		/// that both sides use the SAME unit, because the quantity that drives the engine is their
		/// DIFFERENCE -- and that a difference is what drives it is the property that makes the
		/// machine impossible to cheat with a hot source alone.
		///
		/// <paramref name="maxPower"/> is the machine's own cap, applied last, exactly as
		/// Stationeers applies <c>RocketMath.Min(MaxPower, ...)</c>. Pass
		/// <see cref="float.PositiveInfinity"/> for an uncapped machine.
		/// </summary>
		public static float Power(float hotSideEnergy, float coldSideEnergy,
			float workingGasEfficiency, float pressureDifferentialEfficiency, float maxPower)
		{
			float difference = Math.Abs(hotSideEnergy - coldSideEnergy);
			if (!(difference > 0f) || !(workingGasEfficiency > 0f) ||
				!(pressureDifferentialEfficiency > 0f))
			{
				return 0f;
			}

			float power = difference
				* workingGasEfficiency
				* MachineEnvironmentEfficiency
				* pressureDifferentialEfficiency;

			return power > maxPower ? maxPower : power;
		}

		/// <summary>
		/// The thermal energy a mass of substance holds relative to absolute zero:
		/// <c>mass * specificHeatCapacity * temperature</c>.
		///
		/// The reference measures both sides with <c>GasMixture.TotalEnergy</c>, which is this
		/// same product summed over a mixture. Absolute zero is the right reference point and not
		/// an arbitrary one: the engine consumes a DIFFERENCE of two of these, so any shared
		/// offset cancels -- but only if both sides use the same one, and "the same one" is
		/// cheapest to guarantee by using none.
		/// </summary>
		public static float ThermalEnergy(float massKg, float specificHeatCapacityKJPerKgK,
			float temperatureK)
		{
			return massKg * specificHeatCapacityKJPerKgK * temperatureK;
		}

		/// <summary>
		/// The temperature a body lands at after <paramref name="energyKJ"/> is taken out of it,
		/// floored at absolute zero.
		///
		/// The caller is expected never to reach that floor -- the engine can only remove what it
		/// converted, which is a fraction of a difference and therefore always less than the hot
		/// side holds -- so the floor is a guard against a caller's arithmetic error rather than
		/// part of the model, and a heat capacity of zero returns the temperature unchanged rather
		/// than dividing by it.
		/// </summary>
		public static float TemperatureAfterRemoval(float temperatureK, float heatCapacityKJPerK,
			float energyKJ)
		{
			if (!(heatCapacityKJPerK > 0f))
			{
				return temperatureK;
			}
			float result = temperatureK - energyKJ / heatCapacityKJPerK;
			return result < 0f ? 0f : result;
		}
	}
}
