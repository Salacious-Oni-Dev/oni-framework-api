using System;

namespace OniFramework
{
	/// <summary>
	/// The arithmetic of the conversion-enthalpy rule, with no game types in it.
	///
	/// Split out of <see cref="ConversionEnthalpy"/> deliberately. Everything here is a pure
	/// function of plain numbers, so it can be compiled and executed without Unity, without
	/// Assembly-CSharp and without a running colony -- which is the only way the rule's central
	/// claim ("at the calibration temperature this reproduces vanilla exactly, and away from it
	/// the emitted energy balances to the last float") can be checked rather than asserted.
	/// <see cref="ConversionEnthalpy"/> is the game-facing half: it reads a converter, decides a
	/// tier, and calls in here.
	///
	/// See <see cref="ConversionEnthalpy"/> for why the rule has this shape, what it replaces,
	/// and why the obvious "carry enthalpy" fix is wrong.
	/// </summary>
	public static class ConversionEnthalpyRule
	{
		/// <summary>How a recipe is treated, decided from its own arrays.</summary>
		public enum Tier
		{
			/// <summary>
			/// No inputs at all, so there is no enthalpy to carry. Vanilla's temperature is
			/// used unchanged and the emitted energy is counted as created.
			/// </summary>
			Source,

			/// <summary>
			/// Mass conserves and every active output floor is zero: filtration or separation,
			/// no chemistry. True enthalpy conservation, reaction enthalpy exactly zero.
			/// </summary>
			PhysicalSeparation,

			/// <summary>
			/// Everything else. Reaction enthalpy calibrated so the recipe reproduces vanilla
			/// at its calibration temperature.
			/// </summary>
			Calibrated,

			/// <summary>
			/// Deliberately exempt, or degenerate (no active output with heat capacity).
			/// Vanilla's temperature is used unchanged and nothing is counted.
			/// </summary>
			Exempt
		}

		/// <summary>
		/// The output side of one recipe, reduced to the numbers the rule needs.
		/// </summary>
		public struct OutputProfile
		{
			/// <summary>Total heat capacity of the active outputs, kJ/K.</summary>
			public float HeatCapacity;

			/// <summary>
			/// What vanilla would emit at the calibration temperature, kJ. This is the
			/// recipe's design-point energy and the quantity the reaction enthalpy is measured
			/// against.
			/// </summary>
			public float VanillaEnergyAtCalibration;

			/// <summary>Total mass of the active outputs, kg.</summary>
			public float Mass;

			/// <summary>True if every active output carries a zero floor.</summary>
			public bool AllFloorsZero;
		}

		/// <summary>
		/// The temperature one output should be written at, in kelvin.
		/// </summary>
		/// <param name="tier">Which rule applies.</param>
		/// <param name="floorK">This output's <c>minOutputTemperature</c>.</param>
		/// <param name="inputHeatCapacity">Vanilla's <c>num8</c>: the inputs' total heat
		/// capacity for this conversion, kJ/K.</param>
		/// <param name="inputTemperature">Vanilla's <c>num13</c>: the mass*cp-weighted average
		/// input temperature, kelvin.</param>
		/// <param name="profile">The output side of the same conversion.</param>
		/// <param name="calibrationK">The recipe's calibration temperature.</param>
		public static float Solve(Tier tier, float floorK, float inputHeatCapacity,
			float inputTemperature, OutputProfile profile, float calibrationK)
		{
			// Degenerate output side: nothing to divide by, so there is no rule to apply and
			// vanilla's answer is the only defined one.
			if (!(profile.HeatCapacity > 0f))
			{
				return Max(floorK, inputTemperature);
			}

			switch (tier)
			{
				case Tier.PhysicalSeparation:
					// Reaction enthalpy is exactly zero, so the outputs share the inputs'
					// enthalpy outright. Every floor is zero in this tier by construction, so
					// there is no per-output shape to preserve and all outputs land together.
					return inputHeatCapacity * inputTemperature / profile.HeatCapacity;

				case Tier.Calibrated:
				{
					// dH = C_in * T_nom - E_van(T_nom), and the emitted energy is
					// C_in * T_in - dH. Expressed as a shift off the design point, that is a
					// single dT shared by every output, which is what keeps their relative
					// floors intact.
					float deltaT =
						inputHeatCapacity * (inputTemperature - calibrationK) / profile.HeatCapacity;
					return Max(floorK, calibrationK) + deltaT;
				}

				default:
					// Source and Exempt: vanilla, unchanged.
					return Max(floorK, inputTemperature);
			}
		}

		/// <summary>
		/// The reaction enthalpy a recipe is currently assumed to absorb, in kilojoules per
		/// conversion. Positive is endothermic -- energy leaves the sensible pool. Zero for a
		/// physical separation by definition, and meaningless for a source, which returns zero
		/// as well.
		///
		/// Exposed because it is the single number a real thermochemistry table would replace.
		/// </summary>
		public static float EnthalpyKJPerConversion(Tier tier, float inputHeatCapacity,
			OutputProfile profile, float calibrationK)
		{
			if (tier != Tier.Calibrated)
			{
				return 0f;
			}
			return inputHeatCapacity * calibrationK - profile.VanillaEnergyAtCalibration;
		}

		/// <summary>
		/// Whether a recipe's mass rates balance, to the tolerance the rule uses when deciding
		/// whether it is a physical separation.
		///
		/// Relative, because the rates are authored as decimal literals: the Electrolyzer's
		/// 0.888 + 0.11199999 is exactly 1 kg in intent and 0.99999999 in float.
		/// </summary>
		public static bool MassConserves(float inputMassRate, float outputMassRate)
		{
			float scale = inputMassRate > 1e-6f ? inputMassRate : 1e-6f;
			float difference = outputMassRate - inputMassRate;
			if (difference < 0f)
			{
				difference = -difference;
			}
			return difference <= 1e-4f * scale;
		}

		/// <summary>
		/// <c>Mathf.Max</c> without the Unity dependency, so this file compiles and runs
		/// standalone. Same semantics for the finite values a recipe can produce.
		/// </summary>
		private static float Max(float a, float b)
		{
			return a > b ? a : b;
		}
	}
}
