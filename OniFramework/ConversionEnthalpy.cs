using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

// The rule's arithmetic lives in ConversionEnthalpyRule, which has no game types in it so it
// can be executed and checked without Unity. These aliases keep the call sites below reading
// as they did when both halves were one file.
using OutputProfile = OniFramework.ConversionEnthalpyRule.OutputProfile;
using Tier = OniFramework.ConversionEnthalpyRule.Tier;

namespace OniFramework
{
	/// <summary>
	/// The conversion-enthalpy rule: what an <c>ElementConverter</c> recipe emits carries the
	/// energy its inputs brought in, minus a reaction enthalpy that belongs to the recipe.
	///
	/// WHY THIS EXISTS. <c>ElementConverter</c> is the generic recipe engine behind 29 building
	/// configs -- electrolyzer, water sieve, refineries, deodorizers, kilns, distilleries,
	/// generators, and <c>Geyser</c>. Its output temperature is one line
	/// (<c>ElementConverter.ConvertMass</c>, build 744825):
	///
	/// <code>
	/// num13 = (num8 > 0f) ? (num7 / num8) : 0f;              // mass*cp-weighted average INPUT temperature
	/// num16 = Mathf.Max(outputElement.minOutputTemperature, num13);   // OUTPUT temperature
	/// </code>
	///
	/// where <c>num8</c> is the inputs' total heat capacity and <c>num7</c> their total
	/// enthalpy. Two independent non-conservations come out of that line, and neither is a
	/// per-building tuning mistake:
	///
	/// <list type="number">
	/// <item><b>The floor creates energy from nothing.</b> <c>Max(floor, T)</c> heats any
	/// colder input for free. At least 30 output entries across those configs carry a nonzero
	/// floor; the values cluster at 303.15, 343.15, 348.15, 366.5 and 473.15 K.</item>
	/// <item><b>It carries temperature, not enthalpy.</b> The output is written at a
	/// temperature derived from the inputs, but the output element's
	/// <c>specificHeatCapacity</c> is not the input's. Water (cp 4.179) electrolysed into
	/// oxygen (1.005) and hydrogen (2.4) at the same temperature is not the same amount of
	/// energy, so every conversion in the game creates or destroys some even with no
	/// floor.</item>
	/// </list>
	///
	/// WHY THE OBVIOUS FIX IS WRONG. "Carry enthalpy rather than temperature" cannot be applied
	/// literally, and the numbers say so loudly. 1 kg of water at 300 K carries 1253.7 kJ; the
	/// electrolyzer's outputs (0.888 kg oxygen + 0.112 kg hydrogen) have a combined heat
	/// capacity of 1.1612 kJ/K. Giving them the input's whole sensible enthalpy puts them at
	/// <b>1079.6 K</b>. Two reasons:
	///
	/// <list type="number">
	/// <item><b>A chemical conversion changes chemical energy, and vanilla has no term for
	/// it.</b> Electrolysis is endothermic; the missing 855 kJ/s is supposed to end up in the
	/// hydrogen bond, not in the gas temperature. Sensible heat is not the conserved quantity
	/// across a change of element identity.</item>
	/// <item><b>The recipes do not conserve mass either.</b> Only 8 of the 29 configs balance.
	/// The Oil Well Cap turns 1 kg of water into 3.333 kg of crude oil; handing that invented
	/// mass the input's enthalpy drops it from vanilla's 363.15 K to <b>222.6 K</b>, below crude
	/// oil's freezing point, which is the mass error showing up as manufactured cold.</item>
	/// </list>
	///
	/// THE RULE. Give the recipe a reaction enthalpy and let it absorb exactly the discrepancy,
	/// once, at a stated calibration point. Per conversion, with vanilla's own locals:
	///
	/// <code>
	/// C_out        = sum over active outputs of  mass * cp                  (real output heat capacity)
	/// E_van(T_nom) = sum over active outputs of  mass * cp * max(floor, T_nom)
	/// dH           = C_in * T_nom - E_van(T_nom)                            (the reaction enthalpy)
	/// dT           = (C_in * T_in - C_in * T_nom) / C_out
	/// T_out(m)     = max(floor_m, T_nom) + dT
	/// </code>
	///
	/// The total emitted energy is then <c>sum(m*cp*T_out) = E_van + C_out*dT = C_in*T_in - dH</c>
	/// exactly, which is the conservation statement. What the shape buys:
	///
	/// <list type="bullet">
	/// <item>At <c>T_in == T_nom</c>, <c>dT</c> is zero and every output comes out at
	/// <b>precisely vanilla's temperature</b>. The rule is a no-op at its calibration point, so
	/// an existing colony running at nominal temperatures sees nothing change.</item>
	/// <item>A colder input gives a colder output. The floor no longer heats anything for
	/// free -- it sets the recipe's shape, not a free energy source.</item>
	/// <item>The specific-heat mismatch divides out, because <c>dT</c> is scaled by the real
	/// <c>C_out</c> rather than assumed equal to the input's.</item>
	/// <item>Mass non-conservation is absorbed once into <c>dH</c> instead of multiplying the
	/// temperature, which is what stops the Oil Well Cap reading 222.6 K.</item>
	/// <item>Per-output floors keep their relative shape, all shifted by one common
	/// <c>dT</c>. The Polymerizer's steam (473.15 K) stays hotter than its carbon dioxide
	/// (423.15 K) rather than being blended into one number.</item>
	/// </list>
	///
	/// THE ONE EXCEPTION -- PHYSICAL SEPARATIONS. A recipe that conserves mass and carries no
	/// floor on any output is not chemistry at all: it is filtration or separation, and its
	/// reaction enthalpy is genuinely zero. Those get <b>true</b> enthalpy conservation,
	/// <c>T_out = C_in * T_in / C_out</c>, and they are the only recipes whose behaviour
	/// changes at the calibration point. Exactly three of the 29 qualify:
	///
	/// <list type="bullet">
	/// <item><b>Desalinator</b>, 300.00 K -> 312.54 K. Separating a 4.1 cp brine into 4.179 cp
	/// water plus 0.7 cp salt lowers the mixture's heat capacity, so the same energy is a higher
	/// temperature. This is the physically correct answer.</item>
	/// <item><b>Air Deodorizer</b>, 300.00 K -> 285.63 K, the same effect the other way up.</item>
	/// <item><b>Shower</b>, unchanged: water to polluted water is the same specific heat on both
	/// sides, so true conservation and vanilla agree exactly.</item>
	/// </list>
	///
	/// Which tier a recipe falls into is derived from the converter's own arrays at runtime --
	/// mass rates in against mass rates out, and whether every active floor is zero. There is
	/// no per-building table to maintain and nothing to keep in sync with a game update.
	///
	/// SOURCES ARE LEFT ALONE. A recipe with no inputs (<c>DevLifeSupport</c>, <c>Smoker</c>,
	/// and <c>Geyser</c>) has <c>C_in == 0</c>: there is no enthalpy to carry and no reaction to
	/// charge it against. Those keep vanilla's answer exactly. Making a geyser's energy honest
	/// is a resource-economy question, not a thermodynamic one. What this class does do is <b>count</b> them, so the amount
	/// stops being invisible.
	///
	/// WHAT THIS IS NOT. The calibrated <c>dH</c> is not a measured heat of reaction. It is
	/// whatever number reproduces vanilla at <c>T_nom</c>, and it silently contains the
	/// recipe's mass error as well as its chemistry. That is deliberate: it makes the rule
	/// exact and balance-preserving today, and it leaves a single named quantity per recipe for
	/// real thermochemistry to replace later.
	/// <see cref="ConversionEnthalpyRule.EnthalpyKJPerConversion"/> exposes it so a
	/// third-party mod, or the material registry, can read what a recipe is currently
	/// assumed to absorb.
	///
	/// PROVENANCE -- STATIONEERS. The equation above was derived here and then checked against
	/// Stationeers, which turns out to use the same one.
	/// <c>CombustionResult.RunCombustion</c> creates its products carrying
	/// <c>MoleEnergy.Zero</c> and then does
	/// <c>result.TotalEnergy += combustionEnergy + mole.Energy + mole2.Energy</c> -- the
	/// reagents' sensible energy plus the reaction energy, with the product temperature falling
	/// out of the pool rather than being written. Stationeers' enthalpy constants are game-tuned
	/// rather than real (methane 286000 J/mol against a real ~890000).
	///
	/// Two things were taken from that pass and one was deliberately not:
	///
	/// <list type="number">
	/// <item><b>Taken -- the latent-heat term.</b> Stationeers names the phase-change part
	/// separately (<c>combustionEnergy -= IdealGas.Energy(moles, LatentHeatOfVaporization())</c>)
	/// instead of folding it into the reaction constant. See
	/// <see cref="LatentHeatKJPerConversion"/>. It is not a rounding detail: at Stationeers'
	/// tuned latent heat, 444.1 kJ of the Electrolyzer's 855.2 kJ was the latent heat of boiling
	/// its 1 kg of water; at the physical one it is 2256.5 kJ and exceeds the
	/// whole, so the residual goes negative (see <see cref="ResidualEnthalpyKJPerConversion"/>).</item>
	/// <item><b>Taken, opt-in -- the temperature gate.</b> A Stationeers recipe declares a
	/// temperature window its reagents must already be in, and a machine outside it does not run.
	/// See <see cref="TemperatureGate"/>.</item>
	/// <item><b>Not taken -- enthalpy as a material property.</b> Stationeers holds the reaction
	/// energy on the gas, per gas type, rather than on the recipe. That is the
	/// better long-term shape and <b>it is where this should go if real thermochemistry is ever
	/// wanted</b> -- most likely in <see cref="MaterialPropertyRegistry"/>, next to the latent heats already
	/// seeded there. It is not taken now for two reasons: ONI has roughly fifty
	/// fictional materials with no chemistry to draw on, and a per-recipe calibration is what
	/// buys the guarantee that every recipe reproduces vanilla at its design point.
	/// <see cref="ResidualEnthalpyKJPerConversion"/> is the quantity that migration would
	/// replace, and it exists to make that a one-for-one swap rather than a rewrite.</item>
	/// </list>
	///
	/// This class needs no custom SimDLL. It is entirely managed: <c>ElementConverter</c> is a
	/// C# component and the sim never sees a recipe.
	///
	/// The arithmetic itself is in <see cref="ConversionEnthalpyRule"/>, which deliberately
	/// carries no game types so that the rule can be compiled and executed outside the game and
	/// checked against the real recipes. That check has been run over all 29 of them, extracted
	/// mechanically from game build 744825: at the calibration temperature every
	/// calibrated recipe reproduces vanilla to the bit; the emitted energy equals
	/// <c>C_in * T_in - dH</c> across a 100 K to 800 K sweep; the output is monotonic in the
	/// input temperature; and the result does not move when a supply-limited tick scales both
	/// sides down.
	/// </summary>
	public static class ConversionEnthalpy
	{

		/// <summary>
		/// The input temperature at which a calibrated recipe reproduces vanilla exactly, in
		/// kelvin. 300 K -- near ONI's starting-biome ambient, so the recipes that matter early
		/// are unchanged for a normal colony and only off-design operation moves.
		///
		/// This is a convention, not a measured constant. It is the zero point of a linear
		/// shift, so moving it changes which input temperature is the no-op, and nothing else.
		/// </summary>
		public const float DefaultCalibrationTemperature = 300f;

		private struct OverrideEntry
		{
			public float calibrationK;
			public bool hasCalibration;
			public bool exempt;
			public string reason;
		}

		private static readonly Dictionary<string, OverrideEntry> overrides =
			new Dictionary<string, OverrideEntry>();

		private static float defaultCalibrationK = DefaultCalibrationTemperature;

		private static bool installed;

		// ------------------------------------------------------------------ configuration

		/// <summary>
		/// Calibration temperature applied to every recipe without a per-building override, in
		/// kelvin. Read live on each conversion, so a change takes effect on the next one.
		/// Values at or below absolute zero are rejected.
		/// </summary>
		public static float GlobalCalibrationTemperature
		{
			get { return defaultCalibrationK; }
			set
			{
				if (!(value > 0f))
				{
					throw new ArgumentOutOfRangeException(
						"value",
						"A calibration temperature must be above absolute zero; got " + value + " K.");
				}
				defaultCalibrationK = value;
			}
		}

		/// <summary>True once <see cref="Install"/> has patched the game.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		/// <summary>
		/// Pin one building's calibration temperature. The recipe then reproduces vanilla
		/// exactly when its weighted average input arrives at <paramref name="kelvin"/>.
		/// </summary>
		/// <param name="prefabId">The building's prefab id, e.g. "Electrolyzer".</param>
		/// <param name="kelvin">Calibration temperature, above absolute zero.</param>
		/// <param name="reason">Why, for <see cref="DescribeOverrides"/>. Required, so an
		/// override is never anonymous in a bug report.</param>
		public static void SetCalibrationTemperature(string prefabId, float kelvin, string reason)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			if (string.IsNullOrEmpty(reason))
			{
				throw new ArgumentNullException("reason");
			}
			if (!(kelvin > 0f))
			{
				throw new ArgumentOutOfRangeException(
					"kelvin",
					"A calibration temperature must be above absolute zero; got " + kelvin + " K.");
			}

			OverrideEntry entry;
			overrides.TryGetValue(prefabId, out entry);
			entry.calibrationK = kelvin;
			entry.hasCalibration = true;
			entry.reason = reason;
			overrides[prefabId] = entry;
		}

		/// <summary>
		/// Leave one building on vanilla's temperature rule entirely. Its created or destroyed
		/// energy is not counted either, because an exempt building is one this rule has
		/// declared it does not model.
		/// </summary>
		public static void Exempt(string prefabId, string reason)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			if (string.IsNullOrEmpty(reason))
			{
				throw new ArgumentNullException("reason");
			}

			OverrideEntry entry;
			overrides.TryGetValue(prefabId, out entry);
			entry.exempt = true;
			entry.reason = reason;
			overrides[prefabId] = entry;
		}

		/// <summary>Drop every override for one building, returning it to the global default.</summary>
		public static void ClearOverride(string prefabId)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			overrides.Remove(prefabId);
		}

		/// <summary>The calibration temperature in force for a building, in kelvin.</summary>
		public static float CalibrationTemperatureFor(string prefabId)
		{
			OverrideEntry entry;
			if (prefabId != null && overrides.TryGetValue(prefabId, out entry) && entry.hasCalibration)
			{
				return entry.calibrationK;
			}
			return defaultCalibrationK;
		}

		/// <summary>Whether a building has been exempted from the rule.</summary>
		public static bool IsExempt(string prefabId)
		{
			OverrideEntry entry;
			return prefabId != null && overrides.TryGetValue(prefabId, out entry) && entry.exempt;
		}

		/// <summary>
		/// Every override currently in force, one readable line each, for a diagnostic dump.
		/// </summary>
		public static IEnumerable<string> DescribeOverrides()
		{
			foreach (KeyValuePair<string, OverrideEntry> pair in overrides)
			{
				OverrideEntry e = pair.Value;
				string what = e.exempt
					? "exempt"
					: (e.hasCalibration ? ("calibrated at " + e.calibrationK.ToString("0.##") + " K") : "no effect");
				yield return pair.Key + ": " + what + " (" + (e.reason ?? "no reason given") + ")";
			}
		}

		// ------------------------------------------------------------------ the rule

		// ------------------------------------------------------------------ reading a converter

		/// <summary>
		/// Build the output profile for one converter at a given scale. <paramref name="scale"/>
		/// converts a config's per-second mass rate into the mass actually emitted by this
		/// conversion -- vanilla's <c>OutputMultiplier * num * num2</c>.
		/// </summary>
		public static OutputProfile Profile(ElementConverter converter, float scale, float calibrationK)
		{
			OutputProfile profile = default(OutputProfile);
			profile.AllFloorsZero = true;
			if (converter == null || converter.outputElements == null)
			{
				return profile;
			}

			for (int i = 0; i < converter.outputElements.Length; i++)
			{
				ElementConverter.OutputElement output = converter.outputElements[i];
				if (!output.IsActive)
				{
					continue;
				}
				// An output pinned to the building's own temperature does not draw from the
				// input pool -- vanilla's ternary never reaches the floor for it -- so it must
				// not appear in C_out either, or the pool would be shared with something that
				// is not taking from it.
				if (output.useEntityTemperature)
				{
					continue;
				}

				Element element = ElementLoader.FindElementByHash(output.elementHash);
				if (element == null)
				{
					continue;
				}

				float mass = output.massGenerationRate * scale;
				float heatCapacity = mass * element.specificHeatCapacity;
				profile.Mass += mass;
				profile.HeatCapacity += heatCapacity;
				profile.VanillaEnergyAtCalibration +=
					heatCapacity * Mathf.Max(output.minOutputTemperature, calibrationK);
				if (output.minOutputTemperature != 0f)
				{
					profile.AllFloorsZero = false;
				}
			}

			return profile;
		}

		/// <summary>
		/// Which tier a converter falls into, decided from its own arrays.
		///
		/// The mass test is on the config's rates rather than on the mass actually moved,
		/// because a supply-limited tick scales both sides by the same factor and would
		/// otherwise make a balanced recipe look unbalanced at the moment its input runs out.
		/// </summary>
		public static Tier Classify(ElementConverter converter, string prefabId,
			float inputHeatCapacity)
		{
			if (converter == null || IsExempt(prefabId))
			{
				return Tier.Exempt;
			}
			// No input enthalpy: nothing to carry, nothing to charge a reaction against.
			if (!(inputHeatCapacity > 0f))
			{
				return Tier.Source;
			}

			float inputMassRate = 0f;
			if (converter.consumedElements != null)
			{
				for (int i = 0; i < converter.consumedElements.Length; i++)
				{
					ElementConverter.ConsumedElement consumed = converter.consumedElements[i];
					if (consumed.IsActive)
					{
						inputMassRate += consumed.MassConsumptionRate;
					}
				}
			}
			if (!(inputMassRate > 0f))
			{
				return Tier.Source;
			}

			float outputMassRate = 0f;
			bool allFloorsZero = true;
			if (converter.outputElements != null)
			{
				for (int i = 0; i < converter.outputElements.Length; i++)
				{
					ElementConverter.OutputElement output = converter.outputElements[i];
					if (!output.IsActive || output.useEntityTemperature)
					{
						continue;
					}
					outputMassRate += output.massGenerationRate * converter.OutputMultiplier;
					if (output.minOutputTemperature != 0f)
					{
						allFloorsZero = false;
					}
				}
			}

			// A physical separation conserves mass and imposes no floor.
			bool massConserves =
				ConversionEnthalpyRule.MassConserves(inputMassRate, outputMassRate);

			return (massConserves && allFloorsZero) ? Tier.PhysicalSeparation : Tier.Calibrated;
		}

		// ------------------------------------------------------------------ accounting

		/// <summary>
		/// Energy this rule has kept in the game that vanilla would have created or destroyed,
		/// in kilojoules, summed over every conversion since load. Positive means vanilla would
		/// have created energy and the rule stopped it.
		/// </summary>
		public static double EnergyWithheldKJ { get; private set; }

		/// <summary>
		/// Energy emitted by recipes with no inputs, in kilojoules, summed since load. This is
		/// energy the rule <b>did not</b> stop -- geysers and the two input-free converters --
		/// and it is counted separately precisely because it is the part still unaccounted for.
		/// </summary>
		public static double SourceEnergyKJ { get; private set; }

		/// <summary>
		/// Output entries the rule has priced since load.
		///
		/// One conversion writes one entry per active output, so a two-output recipe such as the
		/// Electrolyzer advances this by two each tick. It counts priced writes, not conversions,
		/// and it is named for what it counts because the earlier name was read as ticks.
		/// </summary>
		public static long PricedOutputCount { get; private set; }

		/// <summary>
		/// Conversions the temperature gate has refused since load. Always zero while
		/// <see cref="TemperatureGate"/> is off.
		/// </summary>
		public static long GateBlockedCount { get; private set; }

		/// <summary>
		/// Conversions the temperature gate examined and allowed since load.
		///
		/// Counted only while <see cref="TemperatureGate"/> is on, because with the gate off
		/// <see cref="AllowsConversion"/> returns before it looks at anything. So the gate-off
		/// arm leaves this at zero and no refusal ratio can be formed from it, which is the
		/// honest answer: with the gate off there were no decisions to take a ratio of.
		/// </summary>
		public static long GateAllowedCount { get; private set; }

		/// <summary>
		/// What the temperature gate did to one recipe: how many conversions it let through and
		/// how many it refused.
		///
		/// The pair is what makes a refusal readable. A converter blocked on every tick of the
		/// window is a building that has stopped working; one blocked on a third of them is a
		/// building that is throttled by how fast its inputs reheat. The single global
		/// <see cref="GateBlockedCount"/> cannot tell those apart.
		/// </summary>
		public struct GateTally
		{
			/// <summary>Conversions the gate allowed for this recipe.</summary>
			public long Allowed;

			/// <summary>Conversions the gate refused for this recipe.</summary>
			public long Blocked;

			/// <summary>Conversions the gate examined for this recipe.</summary>
			public long Examined
			{
				get { return Allowed + Blocked; }
			}

			/// <summary>
			/// The fraction of examined conversions the gate refused, 0 to 1. Zero when the gate
			/// never examined this recipe.
			/// </summary>
			public double BlockedFraction
			{
				get { return Examined > 0L ? (double)Blocked / Examined : 0.0; }
			}
		}

		private static readonly Dictionary<string, GateTally> gateTallies =
			new Dictionary<string, GateTally>();

		/// <summary>
		/// A snapshot of <see cref="GateTally"/> per prefab id since the last
		/// <see cref="ResetAccounting"/>. A copy, so the caller may hold it while the game runs.
		/// Empty while <see cref="TemperatureGate"/> is off.
		/// </summary>
		public static Dictionary<string, GateTally> GateTallies()
		{
			return new Dictionary<string, GateTally>(gateTallies);
		}

		private static void TallyGate(string prefabId, bool allowed)
		{
			if (prefabId == null)
			{
				prefabId = "(no KPrefabID)";
			}
			GateTally tally;
			gateTallies.TryGetValue(prefabId, out tally);
			if (allowed)
			{
				tally.Allowed++;
			}
			else
			{
				tally.Blocked++;
			}
			gateTallies[prefabId] = tally;
		}

		/// <summary>Reset the running totals. Does not touch overrides.</summary>
		public static void ResetAccounting()
		{
			EnergyWithheldKJ = 0.0;
			SourceEnergyKJ = 0.0;
			PricedOutputCount = 0;
			GateBlockedCount = 0;
			GateAllowedCount = 0;
			gateTallies.Clear();
		}

		// ------------------------------------------------------------------ the latent term
		//
		// Stationeers names the phase-change part of a reaction's energy rather than folding it
		// into the reaction constant:
		//
		//     if (fuel.MatterState() == Liquid)
		//         combustionEnergy -= IdealGas.Energy(moles, fuel.LatentHeatOfVaporization());
		//
		// (CombustionResult.RunCombustion). Our calibrated dH silently contains the same
		// quantity, and it is not a small part of it. With Stationeers' tuned 8000 J/mol it was
		// 444.1 kJ of the Electrolyzer's 855.2 kJ per conversion, 52 % of the total.
		//
		// So the number is decomposed rather than changed. The total dH -- and therefore every
		// output temperature and the whole of the verified arithmetic -- is untouched; what is
		// new is that the physical part of it can be read separately from the tuned remainder.
		// The latent heats come from MaterialPropertyRegistry, which holds PHYSICAL values.
		//
		// AND THAT MAKES THE ELECTROLYZER'S RESIDUAL NEGATIVE, ON PURPOSE. Water's real 2256.5
		// kJ/kg names 2256.5 kJ of latent heat inside a total of 855.2 kJ, so
		// ResidualEnthalpyKJPerConversion returns about -1401 kJ. It is display only -- the total
		// dH, and so every output temperature, is unchanged, and the only reader is
		// ConversionEnthalpyProbe -- and it is the honest answer: vanilla's 855.2 kJ was never
		// physical (real electrolysis costs ~15.9 MJ per kg of water), and a negative remainder is
		// the calibration saying so. It is documented rather than clamped, because a clamp would
		// hide exactly the number a real thermochemistry pass needs to see.

		/// <summary>
		/// The part of a recipe's reaction enthalpy that is the latent heat of boiling its
		/// liquid inputs, in kilojoules per conversion. Positive, and always a component of
		/// <see cref="ConversionEnthalpyRule.EnthalpyKJPerConversion"/> rather than an addition
		/// to it.
		///
		/// Counted only where the recipe actually changes phase -- a liquid input with at least
		/// one gas output -- and only for inputs whose tag resolves to a real element with a
		/// registered latent heat. A recipe consuming a category tag ("Filter", "BuildingWood")
		/// or an element the registry has no data for contributes nothing here, and its enthalpy
		/// stays wholly calibrated. That is the intended degradation, not a gap to close by
		/// guessing.
		/// </summary>
		public static float LatentHeatKJPerConversion(ElementConverter converter, float scale)
		{
			if (converter == null || converter.consumedElements == null
				|| converter.outputElements == null)
			{
				return 0f;
			}

			bool anyGasOutput = false;
			for (int i = 0; i < converter.outputElements.Length; i++)
			{
				ElementConverter.OutputElement output = converter.outputElements[i];
				if (!output.IsActive)
				{
					continue;
				}
				Element element = ElementLoader.FindElementByHash(output.elementHash);
				if (element != null && element.IsGas)
				{
					anyGasOutput = true;
					break;
				}
			}
			if (!anyGasOutput)
			{
				return 0f;
			}

			float total = 0f;
			for (int i = 0; i < converter.consumedElements.Length; i++)
			{
				ElementConverter.ConsumedElement consumed = converter.consumedElements[i];
				if (!consumed.IsActive)
				{
					continue;
				}
				Element element = ElementLoader.GetElement(consumed.Tag);
				if (element == null || !element.IsLiquid)
				{
					continue;
				}
				float latentJPerKg;
				if (!MaterialPropertyRegistry.TryGetLatentHeatOfVaporizationJPerKg(
					element.id, out latentJPerKg))
				{
					continue;
				}
				// The registry is in joules per kilogram and the whole ledger is kilojoules.
				total += consumed.MassConsumptionRate * scale * latentJPerKg * 0.001f;
			}
			return total;
		}

		/// <summary>
		/// The remainder of the reaction enthalpy once the latent term is named: the part that
		/// is still a tuned constant rather than a physical quantity. CAN BE NEGATIVE: with
		/// physical latent heats the Electrolyzer's is about -1401 kJ, because its calibrated
		/// total is smaller than the real latent heat of the water it splits (see the note above
		/// <see cref="LatentHeatKJPerConversion"/>). Not clamped.
		///
		/// This is the number a real thermochemistry pass would replace. Doing that properly
		/// means moving the enthalpy off the recipe and onto the material, the way Stationeers
		/// holds it (a flat per-gas table times a per-oxidiser multiplier) -- at which point this method and the calibration behind it both go away.
		/// That migration is deliberately not taken now: it needs enthalpies for the roughly
		/// fifty fictional ONI materials that have no real chemistry, and it would give up the
		/// guarantee that every recipe reproduces vanilla at its calibration point.
		/// </summary>
		public static float ResidualEnthalpyKJPerConversion(Tier tier,
			ElementConverter converter, float inputHeatCapacity, OutputProfile profile,
			float calibrationK, float scale)
		{
			float total = ConversionEnthalpyRule.EnthalpyKJPerConversion(
				tier, inputHeatCapacity, profile, calibrationK);
			return total - LatentHeatKJPerConversion(converter, scale);
		}

		// ------------------------------------------------------------------ the temperature gate
		//
		// Stationeers does not heat a product to a floor. Its recipes declare a temperature
		// WINDOW the reagents must already be inside -- Reagents.Recipe carries a
		// `Temperature Temperature` requirement alongside a `float Energy` that is spent as
		// electricity -- and a machine outside that window simply does not run.
		//
		// Read against ONI, that says minOutputTemperature is the wrong shape: "the Polymerizer
		// emits steam at 473.15 K" ought to be "the Polymerizer needs 473.15 K to run". Under
		// that reading the floor stops being an energy source at the root rather than being
		// compensated for afterwards.
		//
		// It is off by default and gated behind this flag, because turning it on materially
		// changes when twenty buildings work: a cold Electrolyzer stops producing rather than
		// producing cold gas. It is exposed so the change can be A/B'd in a live colony before
		// anyone argues about the balance.

		/// <summary>
		/// Whether a recipe refuses to run when its inputs are colder than the highest floor its
		/// outputs declare. <b>Off by default.</b>
		///
		/// With this off, the rule compensates for the floor after the fact, which is the
		/// committed and verified behaviour. With it on, the floor becomes an entry requirement
		/// in the Stationeers sense and the conversion is skipped entirely -- no inputs
		/// consumed, no outputs produced.
		///
		/// Recipes whose active outputs all carry a zero floor are never gated, so the physical
		/// separations are unaffected either way.
		/// </summary>
		public static bool TemperatureGate { get; set; }

		/// <summary>
		/// The temperature a recipe must reach for the gate to let it run, in kelvin: the
		/// highest floor across its active outputs. Zero means the recipe is never gated.
		/// </summary>
		public static float GateFloorK(ElementConverter converter)
		{
			if (converter == null || converter.outputElements == null)
			{
				return 0f;
			}
			float floor = 0f;
			for (int i = 0; i < converter.outputElements.Length; i++)
			{
				ElementConverter.OutputElement output = converter.outputElements[i];
				if (!output.IsActive || output.useEntityTemperature)
				{
					continue;
				}
				if (output.minOutputTemperature > floor)
				{
					floor = output.minOutputTemperature;
				}
			}
			return floor;
		}

		/// <summary>
		/// The mass*cp-weighted temperature of everything in storage that this recipe consumes,
		/// in kelvin, and false if there is nothing to weigh.
		///
		/// This mirrors vanilla's own accumulation -- <c>num12 = mass * specificHeatCapacity</c>,
		/// <c>num7 += num12 * Temperature</c> -- but over each item's <b>whole</b> mass rather
		/// than over the throttled amount the tick would take. The gate asks whether the
		/// material is hot enough, and that does not depend on how much of it this particular
		/// tick would consume. It reads storage and writes nothing.
		/// </summary>
		public static bool TryGetInputTemperature(ElementConverter converter,
			out float temperatureK)
		{
			temperatureK = 0f;
			if (converter == null || converter.consumedElements == null)
			{
				return false;
			}
			Storage storage = StorageOf(converter);
			if (storage == null || storage.items == null)
			{
				return false;
			}

			float heatCapacity = 0f;
			float energy = 0f;
			for (int i = 0; i < converter.consumedElements.Length; i++)
			{
				ElementConverter.ConsumedElement consumed = converter.consumedElements[i];
				if (!consumed.IsActive)
				{
					continue;
				}
				for (int j = 0; j < storage.items.Count; j++)
				{
					GameObject item = storage.items[j];
					if (item == null || !item.HasTag(consumed.Tag))
					{
						continue;
					}
					PrimaryElement primary = item.GetComponent<PrimaryElement>();
					if (primary == null || primary.Element == null)
					{
						continue;
					}
					float hc = primary.Mass * primary.Element.specificHeatCapacity;
					heatCapacity += hc;
					energy += hc * primary.Temperature;
				}
			}

			if (!(heatCapacity > 0f))
			{
				return false;
			}
			temperatureK = energy / heatCapacity;
			return true;
		}

		/// <summary>
		/// Whether the gate is in a position to refuse this recipe at all: it is switched on,
		/// the recipe is not exempt, and at least one active output declares a nonzero floor.
		///
		/// This is deliberately separate from <see cref="AllowsConversion"/>, which answers
		/// whether a particular tick may proceed. A recipe that is not gated is not "allowed" in
		/// any interesting sense -- the gate never looked at it -- and counting those as
		/// allowances would dilute every refusal ratio with the recipes the gate cannot touch.
		/// </summary>
		public static bool IsGated(ElementConverter converter)
		{
			if (!TemperatureGate || converter == null)
			{
				return false;
			}
			if (IsExempt(PrefabIdOf(converter)))
			{
				return false;
			}
			return GateFloorK(converter) > 0f;
		}

		/// <summary>
		/// Whether the gate lets this conversion proceed. Always true while
		/// <see cref="TemperatureGate"/> is off, for an ungated recipe, and for a recipe with no
		/// weighable input -- a source has nothing to be too cold.
		/// </summary>
		public static bool AllowsConversion(ElementConverter converter)
		{
			if (!IsGated(converter))
			{
				return true;
			}
			float inputK;
			if (!TryGetInputTemperature(converter, out inputK))
			{
				return true;
			}
			return inputK >= GateFloorK(converter);
		}

		/// <summary>
		/// Harmony prefix on <c>ConvertMass</c>. Returning false skips the whole method, which
		/// is what "the recipe does not run" has to mean: vanilla consumes its inputs inside
		/// that method, so refusing any later would burn the material and emit nothing.
		/// </summary>
		public static bool ConvertMassPrefix(ElementConverter __instance)
		{
			try
			{
				if (!IsGated(__instance))
				{
					return true;
				}
				bool allowed = AllowsConversion(__instance);
				if (allowed)
				{
					GateAllowedCount++;
				}
				else
				{
					GateBlockedCount++;
				}
				TallyGate(PrefabIdOf(__instance), allowed);
				return allowed;
			}
			catch (Exception e)
			{
				// A gate that throws must not stop the game converting. Fall through to vanilla.
				Debug.LogWarning("[OniFramework] ConversionEnthalpy gate fell through to vanilla: "
					+ e);
				return true;
			}
		}

		// ------------------------------------------------------------------ the patched call
		//
		// This replaces the single `Mathf.Max(outputElement.minOutputTemperature, num13)` call
		// inside ElementConverter.ConvertMass. The two originals stay on the stack and the
		// transpiler pushes the four extra arguments after them, so the argument order here is
		// the stack order there.

		/// <summary>
		/// The output temperature for one output of one conversion. Called from inside
		/// <c>ElementConverter.ConvertMass</c> in place of its <c>Mathf.Max</c>.
		/// </summary>
		/// <param name="floorK">Vanilla's <c>outputElement.minOutputTemperature</c>.</param>
		/// <param name="inputTemperature">Vanilla's <c>num13</c>.</param>
		/// <param name="converter">The converter running the recipe.</param>
		/// <param name="inputHeatCapacity">Vanilla's <c>num8</c>.</param>
		/// <param name="outputMass">Vanilla's <c>num15</c> for this output -- the mass actually
		/// being emitted, from which the conversion's scale is recovered.</param>
		/// <param name="output">The output entry being written.</param>
		public static float OutputTemperature(float floorK, float inputTemperature,
			ElementConverter converter, float inputHeatCapacity, float outputMass,
			ElementConverter.OutputElement output)
		{
			try
			{
				if (converter == null)
				{
					return Mathf.Max(floorK, inputTemperature);
				}

				// Recover the conversion's scale from this output's own mass. Vanilla computes
				// num15 = massGenerationRate * OutputMultiplier * num * num2, so dividing it
				// back out gives the factor every other output shares -- which is why no extra
				// local has to be lifted out of the method for the speed multiplier or the
				// supply-limited fraction.
				float perUnit = output.massGenerationRate * converter.OutputMultiplier;
				if (!(perUnit > 0f) || !(outputMass > 0f))
				{
					return Mathf.Max(floorK, inputTemperature);
				}
				float scale = outputMass / perUnit;

				string prefabId = PrefabIdOf(converter);
				float calibrationK = CalibrationTemperatureFor(prefabId);
				Tier tier = Classify(converter, prefabId, inputHeatCapacity);
				OutputProfile profile = Profile(converter, scale, calibrationK);

				float result = ConversionEnthalpyRule.Solve(tier, floorK, inputHeatCapacity,
					inputTemperature, profile, calibrationK);

				Account(tier, floorK, inputTemperature, outputMass, output, result);

				return result;
			}
			catch (Exception e)
			{
				// A recipe must never fail to produce because the accounting threw. Vanilla's
				// answer is the safe fallback and the exception is reported once per occurrence
				// rather than swallowed.
				Debug.LogWarning("[OniFramework] ConversionEnthalpy.OutputTemperature fell back to "
					+ "vanilla for this output: " + e);
				return Mathf.Max(floorK, inputTemperature);
			}
		}

		private static void Account(Tier tier, float floorK, float inputTemperature,
			float outputMass, ElementConverter.OutputElement output, float result)
		{
			Element element = ElementLoader.FindElementByHash(output.elementHash);
			if (element == null)
			{
				return;
			}
			float heatCapacity = outputMass * element.specificHeatCapacity;
			float vanillaTemperature = Mathf.Max(floorK, inputTemperature);
			double vanillaEnergy = (double)heatCapacity * vanillaTemperature;
			double actualEnergy = (double)heatCapacity * result;

			if (tier == Tier.Source)
			{
				// No inputs, so all of it is created. Counted, not corrected.
				SourceEnergyKJ += actualEnergy;
			}
			else if (tier != Tier.Exempt)
			{
				EnergyWithheldKJ += vanillaEnergy - actualEnergy;
			}
			PricedOutputCount++;
		}

		private static FieldInfo storageField;

		/// <summary>
		/// The converter's own <c>storage</c> field, not a sibling <c>GetComponent</c>.
		/// <c>ElementConverter.SetStorage</c> exists and is used, so the component on the same
		/// GameObject is not always the one the recipe actually draws from -- and the gate has to
		/// weigh what the conversion would consume, not what happens to be attached.
		/// </summary>
		private static Storage StorageOf(ElementConverter converter)
		{
			if (storageField == null)
			{
				storageField = AccessTools.Field(typeof(ElementConverter), "storage");
				if (storageField == null)
				{
					throw new InvalidOperationException(
						"ConversionEnthalpy could not find ElementConverter.storage. The game build "
						+ "has moved; re-check the member against the current game assembly.");
				}
			}
			return storageField.GetValue(converter) as Storage;
		}

		private static string PrefabIdOf(ElementConverter converter)
		{
			KPrefabID prefab = converter.GetComponent<KPrefabID>();
			return prefab == null ? null : prefab.PrefabTag.Name;
		}

		// ------------------------------------------------------------------ installation

		/// <summary>
		/// Patch <c>ElementConverter.ConvertMass</c>. Idempotent.
		///
		/// One transpiler, replacing one call, plus a prefix that is inert unless
		/// <see cref="TemperatureGate"/> is on. Everything else in the method -- disease
		/// carrying, bubble spawning, storage routing, the report manager -- is left exactly as
		/// Klei wrote it, which is the whole reason this is a transpiler rather than a
		/// reimplementation: a copy of a 150-line method would have to be re-verified against
		/// every game update, and would silently diverge when it was not.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (installed)
			{
				return;
			}

			MethodInfo convertMass = AccessTools.Method(typeof(ElementConverter), "ConvertMass");
			if (convertMass == null)
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy.Install could not find ElementConverter.ConvertMass(). The "
					+ "game build has moved and the rule would silently do nothing, so this throws "
					+ "instead. Re-check the member against the current game assembly before shipping.");
			}

			// The prefix is the temperature gate and does nothing at all while TemperatureGate
			// is off, which is the default. It is installed unconditionally so the flag can be
			// turned on and off in a live colony without re-patching.
			harmony.Patch(convertMass,
				new HarmonyMethod(AccessTools.Method(typeof(ConversionEnthalpy), "ConvertMassPrefix")),
				null,
				new HarmonyMethod(AccessTools.Method(typeof(ConversionEnthalpy), "ConvertMassTranspiler")));

			installed = true;
		}

		/// <summary>
		/// Replace the one <c>Mathf.Max(float, float)</c> in <c>ConvertMass</c> with a call to
		/// <see cref="OutputTemperature"/>, pushing the four extra arguments the rule needs.
		///
		/// Three locals have to be identified, and all three are anchored on instruction shapes
		/// rather than on local indices, which reorder whenever anything in the method changes.
		/// Verified against the IL of build 744825, where they resolve to locals 7, 27 and 29:
		///
		/// <list type="bullet">
		/// <item><c>num8</c>, the inputs' heat capacity: the divisor of the one division in the
		/// method whose two operands are both locals and whose result is stored straight into a
		/// local. There are four <c>div</c> instructions in <c>ConvertMass</c> and only this
		/// shape picks out <c>num7 / num8</c> -- <c>num5 / num3</c> feeds an <c>add</c>, and the
		/// other two divide something that is not a local.</item>
		/// <item><c>outputElement</c>: the local loaded to read
		/// <c>OutputElement.accumulator</c>. There are two <c>Accumulators.Accumulate</c> calls
		/// in the method; the input-side one reads <c>ConsumedElement.Accumulator</c>, a
		/// different field on a different type, so the field identity separates them.</item>
		/// <item><c>num15</c>, this output's mass: the second argument of that same call.</item>
		/// </list>
		///
		/// Every anchor is required to match exactly once. If one is missing, or matches twice,
		/// this throws rather than returning the method unchanged -- a silently unpatched recipe
		/// engine looks exactly like a working one, and that is the failure worth being loud
		/// about.
		///
		/// The pushed loads are clones of the instructions already in the method, operand and
		/// all, rather than freshly built ones. A local referenced by <c>ldloc.s</c> keeps
		/// whatever operand form the original body used, so there is no chance of emitting a
		/// shape the runtime reads differently.
		/// </summary>
		public static IEnumerable<CodeInstruction> ConvertMassTranspiler(
			IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);

			// ---- num8: the divisor of "num13 = num7 / num8"
			CodeInstruction divisorLoad = null;
			int divisorMatches = 0;
			for (int i = 2; i + 1 < code.Count; i++)
			{
				if (code[i].opcode != OpCodes.Div)
				{
					continue;
				}
				if (!IsLoadLocal(code[i - 2]) || !IsLoadLocal(code[i - 1]))
				{
					continue;
				}
				if (!IsStoreLocal(code[i + 1]))
				{
					continue;
				}
				divisorMatches++;
				divisorLoad = code[i - 1];
			}
			if (divisorMatches != 1)
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy expected exactly one 'local / local -> local' division in "
					+ "ElementConverter.ConvertMass, which is where the inputs' heat capacity lives, "
					+ "and found " + divisorMatches + ". The method has been rewritten; re-derive the "
					+ "anchors from the current game assembly before shipping.");
			}

			// ---- outputElement and num15: the output-side Accumulate call
			MethodInfo accumulate = AccessTools.Method(
				typeof(Accumulators), "Accumulate",
				new Type[] { typeof(HandleVector<int>.Handle), typeof(float) });
			FieldInfo accumulatorField =
				AccessTools.Field(typeof(ElementConverter.OutputElement), "accumulator");
			if (accumulate == null || accumulatorField == null)
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy could not resolve Accumulators.Accumulate or "
					+ "ElementConverter.OutputElement.accumulator. The game build has moved; "
					+ "re-derive the anchors from the current game assembly.");
			}

			CodeInstruction outputLoad = null;
			CodeInstruction massLoad = null;
			int accumulateMatches = 0;
			for (int i = 3; i < code.Count; i++)
			{
				if (!code[i].Calls(accumulate))
				{
					continue;
				}
				// ... ldloc outputElement ; ldfld accumulator ; ldloc num15 ; callvirt Accumulate
				if (!IsLoadLocal(code[i - 1]))
				{
					continue;
				}
				if (code[i - 2].opcode != OpCodes.Ldfld
					|| !accumulatorField.Equals(code[i - 2].operand))
				{
					continue;
				}
				if (!IsLoadLocal(code[i - 3]) && !IsLoadLocalAddress(code[i - 3]))
				{
					continue;
				}
				accumulateMatches++;
				massLoad = code[i - 1];
				outputLoad = code[i - 3];
			}
			if (accumulateMatches != 1)
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy expected exactly one output-side "
					+ "'accumulators.Accumulate(outputElement.accumulator, num15)' call in "
					+ "ElementConverter.ConvertMass, which is where the output entry and its mass are "
					+ "identified, and found " + accumulateMatches + ". The method has been "
					+ "rewritten; re-derive the anchors from the current game assembly before shipping.");
			}
			if (IsLoadLocalAddress(outputLoad))
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy found the output element loaded by address rather than by "
					+ "value in ElementConverter.ConvertMass. OutputTemperature takes it by value, "
					+ "so pushing that operand would put a managed pointer where a struct is "
					+ "expected. Re-derive the anchors from the current game assembly.");
			}

			// ---- the replacement
			MethodInfo mathfMax = AccessTools.Method(
				typeof(Mathf), "Max", new Type[] { typeof(float), typeof(float) });
			MethodInfo replacement = AccessTools.Method(
				typeof(ConversionEnthalpy), "OutputTemperature");
			if (mathfMax == null || replacement == null)
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy could not resolve Mathf.Max(float, float) or its own "
					+ "OutputTemperature. This is a build problem, not a game one.");
			}

			List<CodeInstruction> output = new List<CodeInstruction>(code.Count + 8);
			int replaced = 0;
			for (int i = 0; i < code.Count; i++)
			{
				if (code[i].Calls(mathfMax))
				{
					// The two vanilla arguments are already on the stack; push the rest in the
					// order OutputTemperature declares them, then call it instead of Mathf.Max.
					// The replaced call's labels and exception blocks move to the first pushed
					// instruction so nothing branching here lands in the middle of the sequence.
					CodeInstruction self = new CodeInstruction(OpCodes.Ldarg_0);
					self.labels = code[i].labels;
					self.blocks = code[i].blocks;
					output.Add(self);
					output.Add(new CodeInstruction(divisorLoad.opcode, divisorLoad.operand));
					output.Add(new CodeInstruction(massLoad.opcode, massLoad.operand));
					output.Add(new CodeInstruction(outputLoad.opcode, outputLoad.operand));
					output.Add(new CodeInstruction(OpCodes.Call, replacement));
					replaced++;
					continue;
				}
				output.Add(code[i]);
			}

			if (replaced != 1)
			{
				throw new InvalidOperationException(
					"ConversionEnthalpy expected exactly one Mathf.Max(float, float) call in "
					+ "ElementConverter.ConvertMass and found " + replaced + ". The method has been "
					+ "rewritten; re-derive the anchors from the current game assembly before shipping.");
			}

			return output;
		}

		private static bool IsLoadLocal(CodeInstruction instruction)
		{
			OpCode op = instruction.opcode;
			return op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc_0
				|| op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3;
		}

		private static bool IsStoreLocal(CodeInstruction instruction)
		{
			OpCode op = instruction.opcode;
			return op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0
				|| op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;
		}

		private static bool IsLoadLocalAddress(CodeInstruction instruction)
		{
			OpCode op = instruction.opcode;
			return op == OpCodes.Ldloca || op == OpCodes.Ldloca_S;
		}
	}
}
