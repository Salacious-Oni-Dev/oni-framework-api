using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

// Same split, and the same reason, as ConversionEnthalpy: the arithmetic lives in
// ConversionEnthalpyRule, which has no game types in it and can therefore be executed and
// checked without Unity. This file is the game-facing half for the OTHER recipe engine.
using OutputProfile = OniFramework.ConversionEnthalpyRule.OutputProfile;
using Tier = OniFramework.ConversionEnthalpyRule.Tier;

namespace OniFramework
{
	/// <summary>
	/// The conversion-enthalpy rule applied to <c>EnergyGenerator</c>: what a fuel generator
	/// emits carries the enthalpy of the fuel it burned, instead of being written at a floor.
	///
	/// WHY THIS IS A SECOND CLASS AND NOT A SECOND CASE INSIDE
	/// <see cref="ConversionEnthalpy"/>. ONI has two unrelated recipe engines.
	/// <c>ElementConverter</c> is the one behind 29 buildings and geysers, and is what
	/// <see cref="ConversionEnthalpy"/> fixes. <c>EnergyGenerator</c> is a separate engine,
	/// with its own <c>Formula</c>/<c>InputItem</c>/<c>OutputItem</c> types, behind the fuel
	/// generators. Measured rather than assumed:
	///
	/// <code>
	/// grep -l "ElementConverter" MethaneGeneratorConfig.cs PetroleumGeneratorConfig.cs  ->  (none)
	/// grep -l "EnergyGenerator"  (the 29 ElementConverter configs)                      ->  (none)
	/// </code>
	///
	/// The two sets are disjoint, so the rule that fixed one reaches none of the other. The
	/// ARITHMETIC is shared -- both call <see cref="ConversionEnthalpyRule.Solve"/>, so the two
	/// engines cannot drift apart -- but the reading of the recipe cannot be, because the types
	/// have nothing in common beyond their shape.
	///
	/// WHAT VANILLA DOES. <c>EnergyGenerator.Emit</c>, build 744825:
	///
	/// <code>
	/// float temperature = Mathf.Max(root_pe.Temperature, output.minTemperature);
	/// </code>
	///
	/// The fuel's own sensible heat is discarded at <c>storage.ConsumeIgnoringDisease</c> and
	/// the product is written at the building's body temperature, floored by a constant. Cold
	/// methane in, 383.15 K carbon dioxide out, for free. It is the same defect
	/// <see cref="ConversionEnthalpy"/> exists to answer, in a class that rule cannot see.
	///
	/// HOW BIG IT IS, FROM KLEI'S OWN NUMBERS. Take the Natural Gas Generator at a 300 K
	/// calibration point. Methane's specific heat capacity is 2.191 kJ/(kg K), polluted water's
	/// 4.179, carbon dioxide's 0.846:
	///
	/// <code>
	/// C_in   0.09   * 2.191                                    = 0.1972 kJ/K per second
	/// C_out  0.0675 * 4.179  +  0.0225 * 0.846                 = 0.3011 kJ/K per second
	///
	/// vanilla emits  0.2821 * 313.15  +  0.0190 * 383.15       = 95.61 kW
	/// the fuel brought                 0.1972 * 300            = 59.16 kW
	///                                                            ------
	/// created from nothing at the calibration point              36.45 kW
	/// </code>
	///
	/// **That 36.45 kW is the combustion energy, and it is already in the game.** This is the
	/// measured reason the reworked generators are given no fuel-enthalpy term of their own: the
	/// calibrated reaction enthalpy IS the heat of combustion, derived from Klei's own emission
	/// rates rather than conjured from a table. Adding a second one would count it twice.
	///
	/// For scale against the building's other numbers: it generates 0.8 kW of electricity and
	/// emits a hand-tuned 8 kW of self-heat plus 2 kW of exhaust.
	///
	/// WHAT THIS DOES INSTEAD. Exactly what the converter rule does, and by the same call:
	/// a recipe carries a reaction enthalpy calibrated so that at its calibration temperature it
	/// reproduces vanilla to the float, and away from that point the products carry the fuel's
	/// enthalpy rather than a constant. A generator burning 300 K methane is untouched. One
	/// burning methane at 250 K no longer invents the difference, and one burning it at 400 K no
	/// longer destroys it.
	///
	/// Electricity is not touched. <c>GenerateJoules(WattageRating * dt)</c> is Klei's and stays
	/// Klei's, so this changes no generator's power output -- the same discipline
	/// <see cref="TurbineHeat"/> follows, and for the same reason: a heat-accounting fix that
	/// silently rebalances power is not a heat-accounting fix.
	///
	/// THE STORED OUTPUT, WHICH READING THE C# ALONE DOES NOT SHOW. <c>Emit</c> has two halves,
	/// and only one of them ever reads <c>minTemperature</c>. In the IL of game build 744825,
	/// method <c>EnergyGenerator::Emit</c>:
	///
	/// <code>
	/// IL_0036  callvirt PrimaryElement::get_Temperature()   AddGasChunk    <- store: true
	/// IL_0060  callvirt PrimaryElement::get_Temperature()   AddLiquid      <- store: true
	/// IL_0087  callvirt PrimaryElement::get_Temperature()   SpawnResource  <- store: true
	/// IL_00ca  callvirt PrimaryElement::get_Temperature()
	/// IL_00d5  call     Mathf::Max(float32, float32)                       <- store: false only
	/// </code>
	///
	/// So an output declared <c>store: true</c> is written at the body temperature and its
	/// declared floor is **dead data**. The Natural Gas Generator's carbon dioxide is exactly
	/// that case: it declares 383.15 K and never sees it.
	///
	/// This rule DELIBERATELY DOES NOT START IMPOSING THAT FLOOR. Stored outputs are given the
	/// enthalpy-correct temperature with a zero floor, which leaves vanilla's observable
	/// behaviour where Klei put it and changes only the accounting. Newly honouring a floor that
	/// has never once been applied would be a balance change wearing a correctness fix's
	/// clothes. The floor is still read for the profile, because that is what the calibration is
	/// measured against, and it is reported by <see cref="DescribeDeadFloors"/> so the finding
	/// does not evaporate into this comment.
	///
	/// WHY A TRANSPILER AND A PREFIX RATHER THAN A REIMPLEMENTATION. <c>Emit</c> routes gases,
	/// liquids and solids to four different game APIs and <c>EnergySim200ms</c> carries the
	/// battery-refill logic, the meter, the status items and the delivery pause. Copying either
	/// would mean re-verifying a copy against every game update and diverging silently when it
	/// was not. The transpiler replaces four instructions; the prefix reads storage and writes
	/// nothing.
	///
	/// The prefix is needed because the enthalpy has to be measured BEFORE it is destroyed:
	/// <c>EnergySim200ms</c> calls <c>storage.ConsumeIgnoringDisease</c> and only then calls
	/// <c>Emit</c>, so by the time the temperature is chosen the fuel is already gone.
	///
	/// NO SIMDLL CHANGE. <c>EnergyGenerator</c> is managed, the temperature it picks is managed,
	/// and the rule is arithmetic on numbers the class already holds, so this is framework work,
	/// not simulation work. It lives in the framework rather than in a gameplay mod so that
	/// whatever enforces conservation is reachable by a third-party mod.
	/// </summary>
	public static class GeneratorEnthalpy
	{
		/// <summary>
		/// Calibration temperature used for any generator without an override, in kelvin.
		/// Deliberately the same constant <see cref="ConversionEnthalpy"/> uses: the two rules
		/// answer the same question about different classes, and a colony whose converters and
		/// generators calibrated at different temperatures would be very hard to reason about.
		/// </summary>
		public const float DefaultCalibrationTemperature =
			ConversionEnthalpy.DefaultCalibrationTemperature;

		private struct OverrideEntry
		{
			public float calibrationK;
			public bool hasCalibration;
			public bool exempt;
			public string reason;
		}

		private struct InputState
		{
			public float heatCapacity;
			public float temperatureK;
			public float dt;
			public int frameStamp;
			public bool valid;
		}

		private static readonly Dictionary<string, OverrideEntry> overrides =
			new Dictionary<string, OverrideEntry>();

		// Keyed by instance id rather than by the component, so a destroyed generator cannot
		// keep itself alive through this table. Entries are overwritten every 200 ms tick and
		// dropped when the generator stops ticking; see PruneSnapshots.
		private static readonly Dictionary<int, InputState> snapshots =
			new Dictionary<int, InputState>();

		private static readonly List<int> pruneScratch = new List<int>();

		private static float defaultCalibrationK = DefaultCalibrationTemperature;

		private static bool installed;

		private static bool warnedAboutStockBugFix;

		private static FieldInfo storageField;

		private static long ticksSincePrune;

		/// <summary>
		/// How many frames an unrewritten snapshot survives before <see cref="PruneSnapshots"/>
		/// treats it as belonging to a generator that no longer ticks. Generously above a
		/// 200 ms cadence at any playable frame rate, so a paused or heavily throttled colony
		/// never drops a live generator's reading.
		/// </summary>
		private const int StaleFrames = 3600;

		// ------------------------------------------------------------------ configuration

		/// <summary>
		/// Calibration temperature applied to every generator without a per-building override,
		/// in kelvin. Read live on each emission, so a change takes effect on the next one.
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
		/// Pin one generator's calibration temperature. Its formula then reproduces vanilla
		/// exactly when the fuel arrives at <paramref name="kelvin"/>.
		/// </summary>
		/// <param name="prefabId">The building's prefab id, e.g. "MethaneGenerator".</param>
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
		/// Leave one generator on vanilla's temperature rule entirely. Its created or destroyed
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

		/// <summary>Drop every override for one generator, returning it to the global default.</summary>
		public static void ClearOverride(string prefabId)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			overrides.Remove(prefabId);
		}

		/// <summary>The calibration temperature in force for a generator, in kelvin.</summary>
		public static float CalibrationTemperatureFor(string prefabId)
		{
			OverrideEntry entry;
			if (prefabId != null && overrides.TryGetValue(prefabId, out entry)
				&& entry.hasCalibration)
			{
				return entry.calibrationK;
			}
			return defaultCalibrationK;
		}

		/// <summary>Whether a generator has been exempted from the rule.</summary>
		public static bool IsExempt(string prefabId)
		{
			OverrideEntry entry;
			return prefabId != null && overrides.TryGetValue(prefabId, out entry) && entry.exempt;
		}

		/// <summary>
		/// Every override in force, one line each, for logging and bug reports.
		/// </summary>
		public static IEnumerable<string> DescribeOverrides()
		{
			foreach (KeyValuePair<string, OverrideEntry> pair in overrides)
			{
				OverrideEntry entry = pair.Value;
				string what = entry.exempt
					? "exempt"
					: (entry.hasCalibration
						? "calibrated at " + entry.calibrationK.ToString("0.##") + " K"
						: "no effective override");
				yield return pair.Key + ": " + what + " -- " + entry.reason;
			}
		}

		// ------------------------------------------------------------------ accounting

		/// <summary>
		/// Energy this rule has kept in the game that vanilla would have created or destroyed,
		/// in kilojoules, summed over every emission since load. Positive means vanilla would
		/// have created energy and the rule stopped it.
		/// </summary>
		public static double EnergyWithheldKJ { get; private set; }

		/// <summary>
		/// Energy emitted by generators with no measurable fuel enthalpy, in kilojoules. Counted
		/// rather than corrected, because a formula with no inputs has no enthalpy to carry.
		/// </summary>
		public static double SourceEnergyKJ { get; private set; }

		/// <summary>How many outputs this rule has priced since load.</summary>
		public static long PricedOutputCount { get; private set; }

		/// <summary>
		/// How many outputs were written on the <c>store: true</c> path, where vanilla applies
		/// no floor at all. Separated because that path is the one whose declared
		/// <c>minTemperature</c> is dead data -- see the class remarks.
		/// </summary>
		public static long StoredOutputCount { get; private set; }

		/// <summary>Zero the counters. For A/B measurement across a single session.</summary>
		public static void ResetAccounting()
		{
			EnergyWithheldKJ = 0.0;
			SourceEnergyKJ = 0.0;
			PricedOutputCount = 0L;
			StoredOutputCount = 0L;
		}

		/// <summary>
		/// Every output in the loaded building set that declares a nonzero
		/// <c>minTemperature</c> on the <c>store: true</c> path -- that is, every floor the game
		/// carries in its configs and never applies. One line each.
		///
		/// This exists so the finding in the class remarks is checkable in a running game rather
		/// than only in a comment. It walks <c>Assets.BuildingDefs</c>, so it is meaningful only
		/// after the building database is built.
		/// </summary>
		public static IEnumerable<string> DescribeDeadFloors()
		{
			if (Assets.BuildingDefs == null)
			{
				yield break;
			}
			for (int i = 0; i < Assets.BuildingDefs.Count; i++)
			{
				BuildingDef def = Assets.BuildingDefs[i];
				if (def == null || def.BuildingComplete == null)
				{
					continue;
				}
				EnergyGenerator generator = def.BuildingComplete.GetComponent<EnergyGenerator>();
				if (generator == null || generator.formula.outputs == null)
				{
					continue;
				}
				for (int j = 0; j < generator.formula.outputs.Length; j++)
				{
					EnergyGenerator.OutputItem output = generator.formula.outputs[j];
					if (output.store && output.minTemperature != 0f)
					{
						yield return def.PrefabID + ": " + output.element
							+ " declares minTemperature " + output.minTemperature.ToString("0.##")
							+ " K on the store:true path, where vanilla never applies it";
					}
				}
			}
		}

		// ------------------------------------------------------------------ the recipe, read

		/// <summary>
		/// Build the output profile for one generator over one tick.
		/// </summary>
		/// <param name="generator">The generator running the formula.</param>
		/// <param name="dt">The tick length vanilla is scaling its rates by.</param>
		/// <param name="calibrationK">The generator's calibration temperature.</param>
		public static OutputProfile Profile(EnergyGenerator generator, float dt, float calibrationK)
		{
			OutputProfile profile = default(OutputProfile);
			profile.AllFloorsZero = true;
			if (generator == null || generator.formula.outputs == null)
			{
				return profile;
			}

			EnergyGenerator.OutputItem[] outputs = generator.formula.outputs;
			for (int i = 0; i < outputs.Length; i++)
			{
				EnergyGenerator.OutputItem output = outputs[i];
				Element element = ElementLoader.FindElementByHash(output.element);
				if (element == null)
				{
					continue;
				}

				float mass = output.creationRate * dt;
				float heatCapacity = mass * element.specificHeatCapacity;
				profile.Mass += mass;
				profile.HeatCapacity += heatCapacity;

				// The calibration is measured against what vanilla WOULD emit, so this has to
				// use vanilla's own rule for this output -- which is the floor on the emitted
				// path and no floor at all on the stored one. Using the declared floor for a
				// stored output would calibrate against energy the game never emits.
				float vanillaFloor = output.store ? 0f : output.minTemperature;
				profile.VanillaEnergyAtCalibration +=
					heatCapacity * Mathf.Max(vanillaFloor, calibrationK);
				if (vanillaFloor != 0f)
				{
					profile.AllFloorsZero = false;
				}
			}

			return profile;
		}

		/// <summary>
		/// Which tier a generator falls into, decided from its own formula.
		///
		/// The mass test is on the config's rates rather than on the mass actually moved, so a
		/// tick that runs short of fuel does not make a balanced formula look unbalanced.
		/// </summary>
		public static Tier Classify(EnergyGenerator generator, string prefabId,
			float inputHeatCapacity)
		{
			if (generator == null || IsExempt(prefabId))
			{
				return Tier.Exempt;
			}
			// No measurable fuel enthalpy: nothing to carry, nothing to charge a reaction
			// against. Reached when storage holds no matching item with a heat capacity, which
			// is also what a formula with no inputs at all looks like from here.
			if (!(inputHeatCapacity > 0f))
			{
				return Tier.Source;
			}

			float inputMassRate = 0f;
			if (generator.formula.inputs != null)
			{
				for (int i = 0; i < generator.formula.inputs.Length; i++)
				{
					inputMassRate += generator.formula.inputs[i].consumptionRate;
				}
			}
			if (!(inputMassRate > 0f))
			{
				return Tier.Source;
			}

			float outputMassRate = 0f;
			bool allFloorsZero = true;
			if (generator.formula.outputs != null)
			{
				for (int i = 0; i < generator.formula.outputs.Length; i++)
				{
					EnergyGenerator.OutputItem output = generator.formula.outputs[i];
					outputMassRate += output.creationRate;
					if (!output.store && output.minTemperature != 0f)
					{
						allFloorsZero = false;
					}
				}
			}

			bool massConserves =
				ConversionEnthalpyRule.MassConserves(inputMassRate, outputMassRate);

			return (massConserves && allFloorsZero) ? Tier.PhysicalSeparation : Tier.Calibrated;
		}

		/// <summary>
		/// The fuel's heat capacity and temperature for one tick, read from storage before
		/// anything is consumed.
		///
		/// The heat capacity is that of the mass this tick will actually take
		/// (<c>consumptionRate * dt</c>), not of everything in the tank, because that is the
		/// enthalpy the products are receiving. The temperature is the mass-weighted mean over
		/// the stored items that match, which is the same quantity vanilla's own converter
		/// accumulates.
		///
		/// A tagged input can be satisfied by more than one element -- the Petroleum Generator
		/// takes <c>GameTags.CombustibleLiquid</c>, which is petroleum OR crude oil, and their
		/// specific heats differ. The specific heat used is therefore the mass-weighted mean of
		/// what is actually in the tank, not the first match.
		/// </summary>
		/// <param name="generator">The generator about to run its formula.</param>
		/// <param name="dt">The tick length.</param>
		/// <param name="heatCapacity">Fuel heat capacity for this tick, kJ/K.</param>
		/// <param name="temperatureK">Mass-weighted fuel temperature, kelvin.</param>
		/// <returns>False when nothing matching is in storage, in which case both outputs are
		/// zero and the caller should treat the tick as having no fuel enthalpy.</returns>
		public static bool TryReadFuel(EnergyGenerator generator, float dt,
			out float heatCapacity, out float temperatureK)
		{
			heatCapacity = 0f;
			temperatureK = 0f;
			if (generator == null || generator.formula.inputs == null || !(dt > 0f))
			{
				return false;
			}
			Storage storage = StorageOf(generator);
			if (storage == null || storage.items == null)
			{
				return false;
			}

			float totalHeatCapacity = 0f;
			float totalEnergy = 0f;
			EnergyGenerator.InputItem[] inputs = generator.formula.inputs;
			for (int i = 0; i < inputs.Length; i++)
			{
				EnergyGenerator.InputItem input = inputs[i];
				float wanted = input.consumptionRate * dt;
				if (!(wanted > 0f))
				{
					continue;
				}

				// One pass over storage per input, accumulating the mass-weighted specific heat
				// and temperature of everything carrying the tag.
				float mass = 0f;
				float massTimesShc = 0f;
				float massTimesShcTimesT = 0f;
				for (int j = 0; j < storage.items.Count; j++)
				{
					GameObject item = storage.items[j];
					if (item == null || !item.HasTag(input.tag))
					{
						continue;
					}
					PrimaryElement primary = item.GetComponent<PrimaryElement>();
					if (primary == null || primary.Element == null)
					{
						continue;
					}
					float itemHeatCapacity = primary.Mass * primary.Element.specificHeatCapacity;
					mass += primary.Mass;
					massTimesShc += itemHeatCapacity;
					massTimesShcTimesT += itemHeatCapacity * primary.Temperature;
				}

				if (!(mass > 0f) || !(massTimesShc > 0f))
				{
					continue;
				}

				// Scale the tank's heat capacity down to the mass this tick takes. Never take
				// more than the tank holds: a tick short of fuel does not run at all in vanilla,
				// but clamping keeps a partially stocked tank from reporting more enthalpy than
				// it has if that ever changes.
				float taken = wanted < mass ? wanted : mass;
				float shc = massTimesShc / mass;
				float inputTemperature = massTimesShcTimesT / massTimesShc;

				float takenHeatCapacity = taken * shc;
				totalHeatCapacity += takenHeatCapacity;
				totalEnergy += takenHeatCapacity * inputTemperature;
			}

			if (!(totalHeatCapacity > 0f))
			{
				return false;
			}

			heatCapacity = totalHeatCapacity;
			temperatureK = totalEnergy / totalHeatCapacity;
			return true;
		}

		// ------------------------------------------------------------------ the patched calls

		/// <summary>
		/// Snapshot the fuel before <c>EnergySim200ms</c> consumes it.
		///
		/// This writes nothing and never refuses the tick. It runs on every 200 ms tick of every
		/// generator, including ticks that will not convert -- which is cheaper than working out
		/// whether the tick will convert, because that decision needs the battery state, the
		/// circuit and <c>IsConvertible</c>, all of which vanilla is about to compute anyway.
		/// </summary>
		public static void EnergySim200msPrefix(EnergyGenerator __instance, float dt)
		{
			try
			{
				if (__instance == null)
				{
					return;
				}
				InputState state = default(InputState);
				state.dt = dt;
				state.frameStamp = Time.frameCount;
				state.valid = TryReadFuel(__instance, dt,
					out state.heatCapacity, out state.temperatureK);
				snapshots[__instance.GetInstanceID()] = state;
				PruneSnapshots();
			}
			catch (Exception e)
			{
				// A generator must never fail to run because the accounting threw. Dropping the
				// snapshot makes this tick fall back to vanilla's temperature, which is the
				// safe answer.
				snapshots.Remove(__instance == null ? 0 : __instance.GetInstanceID());
				Debug.LogWarning("[OniFramework] GeneratorEnthalpy could not read a generator's "
					+ "fuel; this tick falls back to vanilla: " + e);
			}
		}

		/// <summary>
		/// The output temperature for one EMITTED output, replacing the single
		/// <c>Mathf.Max(root_pe.Temperature, output.minTemperature)</c> in
		/// <c>EnergyGenerator.Emit</c>.
		///
		/// The argument order is the stack order at the call site: vanilla pushes the body
		/// temperature first and the floor second, and the transpiler pushes the generator and
		/// the output after them.
		/// </summary>
		/// <param name="bodyTemperatureK">Vanilla's <c>root_pe.Temperature</c>.</param>
		/// <param name="floorK">Vanilla's <c>output.minTemperature</c>.</param>
		/// <param name="generator">The generator emitting.</param>
		/// <param name="output">The output being written.</param>
		public static float OutputTemperature(float bodyTemperatureK, float floorK,
			EnergyGenerator generator, EnergyGenerator.OutputItem output)
		{
			return Resolve(bodyTemperatureK, floorK, generator, output, stored: false);
		}

		/// <summary>
		/// The output temperature for one STORED output, replacing the three
		/// <c>root_pe.Temperature</c> reads on <c>Emit</c>'s <c>store: true</c> path.
		///
		/// The floor is passed as zero, not as <c>output.minTemperature</c>, because vanilla
		/// applies no floor on this path and this rule does not start applying one. See the
		/// class remarks.
		/// </summary>
		/// <param name="rootPe">Vanilla's <c>root_pe</c>, already on the stack.</param>
		/// <param name="generator">The generator emitting.</param>
		/// <param name="output">The output being written.</param>
		public static float StoredTemperature(PrimaryElement rootPe, EnergyGenerator generator,
			EnergyGenerator.OutputItem output)
		{
			float bodyTemperatureK = rootPe == null ? 0f : rootPe.Temperature;
			return Resolve(bodyTemperatureK, 0f, generator, output, stored: true);
		}

		private static float Resolve(float bodyTemperatureK, float floorK,
			EnergyGenerator generator, EnergyGenerator.OutputItem output, bool stored)
		{
			try
			{
				if (generator == null)
				{
					return Mathf.Max(floorK, bodyTemperatureK);
				}

				InputState state;
				if (!snapshots.TryGetValue(generator.GetInstanceID(), out state) || !state.valid)
				{
					// No fuel reading for this tick. Source tier by definition -- vanilla's
					// answer, counted as created rather than corrected.
					float vanilla = Mathf.Max(floorK, bodyTemperatureK);
					AccountSource(vanilla, output, stored);
					return vanilla;
				}

				string prefabId = PrefabIdOf(generator);
				float calibrationK = CalibrationTemperatureFor(prefabId);
				Tier tier = Classify(generator, prefabId, state.heatCapacity);

				// The profile is rebuilt here rather than passed in, because Emit does not
				// receive one. It is built at the same scale vanilla is using -- dt, shared by
				// every output in the formula -- which the prefix recorded from its own
				// parameter rather than deriving, so a tick that ran short of fuel cannot
				// distort it.
				OutputProfile profile = Profile(generator, state.dt, calibrationK);

				float result = ConversionEnthalpyRule.Solve(tier, floorK, state.heatCapacity,
					state.temperatureK, profile, calibrationK);

				Account(tier, floorK, bodyTemperatureK, state.dt, output, result, stored);

				return result;
			}
			catch (Exception e)
			{
				// A generator must never fail to produce because the accounting threw.
				Debug.LogWarning("[OniFramework] GeneratorEnthalpy.Resolve fell back to vanilla "
					+ "for this output: " + e);
				return Mathf.Max(floorK, bodyTemperatureK);
			}
		}

		private static void Account(Tier tier, float floorK, float bodyTemperatureK, float dt,
			EnergyGenerator.OutputItem output, float result, bool stored)
		{
			Element element = ElementLoader.FindElementByHash(output.element);
			if (element == null)
			{
				return;
			}
			float heatCapacity = output.creationRate * dt * element.specificHeatCapacity;
			float vanillaTemperature = Mathf.Max(floorK, bodyTemperatureK);

			if (tier == Tier.Source)
			{
				SourceEnergyKJ += (double)heatCapacity * result;
			}
			else if (tier != Tier.Exempt)
			{
				EnergyWithheldKJ +=
					(double)heatCapacity * vanillaTemperature - (double)heatCapacity * result;
			}
			PricedOutputCount++;
			if (stored)
			{
				StoredOutputCount++;
			}
		}

		private static void AccountSource(float vanillaTemperature,
			EnergyGenerator.OutputItem output, bool stored)
		{
			Element element = ElementLoader.FindElementByHash(output.element);
			if (element != null)
			{
				// No dt is knowable without a fuel reading, so this counts the per-second rate.
				// It is a tally of emissions this rule could not price, not an energy balance.
				SourceEnergyKJ +=
					(double)output.creationRate * element.specificHeatCapacity * vanillaTemperature;
			}
			PricedOutputCount++;
			if (stored)
			{
				StoredOutputCount++;
			}
		}

		/// <summary>
		/// Drop snapshots for generators that have stopped ticking -- deconstructed, or on a
		/// world that has been unloaded.
		///
		/// Staleness is decided on a frame stamp rather than by resolving the instance id back
		/// to an object. Every live generator rewrites its own entry every 200 ms, so an entry
		/// that has not moved in <see cref="StaleFrames"/> frames belongs to something that is
		/// no longer running, and no reverse lookup is needed to know it. The id is stored
		/// rather than the component for the same reason: a dictionary keyed on the object
		/// would keep the managed wrapper of every generator ever built alive for the session.
		///
		/// Swept every 600 calls rather than every call, because the table holds one small
		/// struct per active generator and is almost always already correct.
		/// </summary>
		private static void PruneSnapshots()
		{
			ticksSincePrune++;
			if (ticksSincePrune < 600L)
			{
				return;
			}
			ticksSincePrune = 0L;

			int now = Time.frameCount;
			pruneScratch.Clear();
			foreach (KeyValuePair<int, InputState> pair in snapshots)
			{
				if (now - pair.Value.frameStamp < StaleFrames)
				{
					continue;
				}
				pruneScratch.Add(pair.Key);
			}
			for (int i = 0; i < pruneScratch.Count; i++)
			{
				snapshots.Remove(pruneScratch[i]);
			}
			pruneScratch.Clear();
		}

		/// <summary>
		/// The generator's own <c>storage</c> field. It is <c>[MyCmpAdd] private</c>, so the
		/// component on the same GameObject is the one the formula draws from, but reading the
		/// field rather than a sibling <c>GetComponent</c> keeps this honest if that ever stops
		/// being true.
		/// </summary>
		private static Storage StorageOf(EnergyGenerator generator)
		{
			if (storageField == null)
			{
				storageField = AccessTools.Field(typeof(EnergyGenerator), "storage");
				if (storageField == null)
				{
					throw new InvalidOperationException(
						"GeneratorEnthalpy could not find EnergyGenerator.storage. The game build "
						+ "has moved; re-check the member against the current game assembly.");
				}
			}
			return storageField.GetValue(generator) as Storage;
		}

		private static string PrefabIdOf(EnergyGenerator generator)
		{
			KPrefabID prefab = generator.GetComponent<KPrefabID>();
			return prefab == null ? null : prefab.PrefabTag.Name;
		}

		// ------------------------------------------------------------------ install

		/// <summary>
		/// Apply the rule. Idempotent -- a second call with the same or a different Harmony
		/// instance is a no-op, so several mods can each ask for it without double-patching.
		/// </summary>
		/// <param name="harmony">The calling mod's Harmony instance, normally the one handed
		/// to <c>UserMod2.OnLoad</c>.</param>
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

			MethodInfo energySim = AccessTools.Method(typeof(EnergyGenerator), "EnergySim200ms");
			MethodInfo emit = AccessTools.Method(typeof(EnergyGenerator), "Emit");
			if (energySim == null || emit == null)
			{
				throw new InvalidOperationException(
					"GeneratorEnthalpy.Install could not find EnergyGenerator.EnergySim200ms() or "
					+ "EnergyGenerator.Emit(). The game build has moved and the rule would "
					+ "silently do nothing, so this throws instead. Re-check both members against "
					+ "the current game assembly before shipping.");
			}

			harmony.Patch(energySim,
				new HarmonyMethod(AccessTools.Method(typeof(GeneratorEnthalpy),
					"EnergySim200msPrefix")));
			harmony.Patch(emit, null, null,
				new HarmonyMethod(AccessTools.Method(typeof(GeneratorEnthalpy), "EmitTranspiler")));

			// WarnIfStockBugFixPresent is NOT called here -- see that method's own doc comment.
			// This runs at the calling mod's OnLoad, which is patch-order-sequenced by
			// mods.json's list order, not load-order sequenced against every OTHER mod. With Stock
			// Bug Fix listed AFTER the calling mod (the ordinary case -- nothing sorts this list by
			// name or dependency), its DLL is not yet loaded into the AppDomain when this runs, so
			// AccessTools.TypeByName would find nothing and the warning would silently never fire.

			installed = true;
		}

		/// <summary>
		/// Compatibility with the Stock Bug Fix mod: mutual detection, log only, and this
		/// framework's own transpiler is not skipped. Stock Bug Fix ships an
		/// individually togglable option, "Minimum Output Temperatures" ("Corrects the minimum
		/// output exhaust temperatures of many generators" -- its own options-screen string,
		/// read off its compiled strings table), whose transpiler rewrites the exact same four
		/// IL sites <see cref="EmitTranspiler"/> does (<c>PrimaryElement.get_Temperature()</c>,
		/// <c>Mathf.Max</c>, <c>OutputItem.emitOffset</c>/<c>minTemperature</c>) to impose the
		/// floor this rule deliberately does not (see this class's own doc comment for why).
		/// Harmony composes transpilers sequentially, so whichever of the two runs second may
		/// find a different IL shape than it expects and silently no-op on some of the four
		/// sites -- nondeterministic per Harmony's own load-order sort, not a crash.
		///
		/// Not resolved by skipping either transpiler: Stock Bug Fix's fix is deliberate and
		/// broadly scoped ("many generators"), and the player already has a per-fix toggle in
		/// Stock Bug Fix's own options screen to remove it if it conflicts -- an escape hatch
		/// this framework did not have to build. So this only makes the conflict legible: log
		/// once, naming both sides and where the toggle lives. Detected by type name rather
		/// than a hard reference, the same reason every other soft-dependency check in this
		/// codebase is: Stock Bug Fix does not have to be installed, or even exist, for this
		/// framework to load.
		///
		/// CALL THIS FROM <c>Game.OnSpawn</c> (or later), NOT FROM <see cref="Install"/>. Every
		/// mod's Harmony patching runs at that mod's own <c>OnLoad</c>, sequenced by
		/// <c>mods.json</c>'s list order -- an order nothing in this codebase controls and a
		/// player can reorder freely. With Stock Bug Fix listed after the calling mod, its DLL is
		/// not yet loaded into the AppDomain when <see cref="Install"/> runs, so a check there
		/// finds nothing and never warns, even though Stock Bug Fix IS present and IS about to
		/// collide. <c>Game.OnSpawn</c> runs after every mod's <c>OnLoad</c> has completed
		/// regardless of list order, so that is the only place this check can give a reliable
		/// answer.
		/// Idempotent -- safe to call once per <c>OnSpawn</c> even across multiple save loads in
		/// one process.
		/// </summary>
		public static void WarnIfStockBugFixPresent()
		{
			if (warnedAboutStockBugFix)
			{
				return;
			}
			try
			{
				if (AccessTools.TypeByName("PeterHan.StockBugFix.EnergyGenerator_Emit_Patch") != null)
				{
					Debug.LogWarning("[OniFramework] Stock Bug Fix is installed and its "
						+ "'Minimum Output Temperatures' option rewrites the same generator "
						+ "output-temperature code GeneratorEnthalpy does. The two disagree on "
						+ "purpose (GeneratorEnthalpy gives stored outputs the fuel's real "
						+ "enthalpy with no floor; Stock Bug Fix imposes Klei's declared "
						+ "minTemperature floor) and which one wins on a given generator is not "
						+ "guaranteed. If a generator's exhaust temperature looks wrong, disable "
						+ "'Minimum Output Temperatures' in Stock Bug Fix's own mod options -- "
						+ "see oni-mod-compat decision 0003 for the full analysis.");
					warnedAboutStockBugFix = true;
				}
			}
			catch
			{
				// A detection failure must not stop this rule from installing over a real
				// conflict just because it could not also warn about one.
			}
		}

		/// <summary>
		/// Replace the four temperature reads in <c>EnergyGenerator.Emit</c>.
		///
		/// Unlike <see cref="ConversionEnthalpy.ConvertMassTranspiler"/>, nothing here has to be
		/// anchored on a local: every value the rule needs is a parameter of <c>Emit</c> itself
		/// (<c>ldarg.0</c> the generator, <c>ldarg.1</c> the output, <c>ldarg.3</c> the
		/// <c>PrimaryElement</c>), so the substitution is positional and cannot be broken by the
		/// locals reordering.
		///
		/// Two distinct substitutions, because <c>Emit</c> has two halves:
		///
		/// <list type="bullet">
		/// <item>The one <c>Mathf.Max(float, float)</c> becomes
		/// <see cref="OutputTemperature"/>, which receives vanilla's two operands plus the
		/// generator and the output.</item>
		/// <item>The three <c>PrimaryElement::get_Temperature()</c> calls that are NOT the one
		/// feeding that <c>Mathf.Max</c> become <see cref="StoredTemperature"/>.</item>
		/// </list>
		///
		/// The fourth <c>get_Temperature</c> is identified by what follows it rather than by
		/// its index, so a reordering of the branches cannot silently convert a stored output
		/// into an emitted one. Verified against build 744825, where the five instructions sit
		/// at IL_0036, IL_0060, IL_0087, IL_00ca and IL_00d5.
		/// </summary>
		public static IEnumerable<CodeInstruction> EmitTranspiler(
			IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);

			MethodInfo getTemperature = AccessTools.PropertyGetter(
				typeof(PrimaryElement), "Temperature");
			MethodInfo mathfMax = AccessTools.Method(
				typeof(Mathf), "Max", new Type[] { typeof(float), typeof(float) });
			MethodInfo emitted = AccessTools.Method(
				typeof(GeneratorEnthalpy), "OutputTemperature");
			MethodInfo stored = AccessTools.Method(
				typeof(GeneratorEnthalpy), "StoredTemperature");
			FieldInfo minTemperature = AccessTools.Field(
				typeof(EnergyGenerator.OutputItem), "minTemperature");

			if (getTemperature == null || mathfMax == null || emitted == null || stored == null
				|| minTemperature == null)
			{
				throw new InvalidOperationException(
					"GeneratorEnthalpy could not resolve PrimaryElement.Temperature, "
					+ "Mathf.Max(float, float), EnergyGenerator.OutputItem.minTemperature or its "
					+ "own replacements. This is a build problem, not a game one.");
			}

			List<CodeInstruction> output = new List<CodeInstruction>(code.Count + 12);
			int storedReplaced = 0;
			int emittedReplaced = 0;

			for (int i = 0; i < code.Count; i++)
			{
				if (code[i].Calls(getTemperature) && !FeedsTheFloorMax(code, i, minTemperature,
					mathfMax))
				{
					// The PrimaryElement is already on the stack; push the generator and the
					// output after it and call our own reader instead of the property.
					CodeInstruction self = new CodeInstruction(OpCodes.Ldarg_0);
					self.labels = code[i].labels;
					self.blocks = code[i].blocks;
					output.Add(self);
					output.Add(new CodeInstruction(OpCodes.Ldarg_1));
					output.Add(new CodeInstruction(OpCodes.Call, stored));
					storedReplaced++;
					continue;
				}

				if (code[i].Calls(mathfMax))
				{
					// Vanilla's two operands are on the stack in the order OutputTemperature
					// declares them; push the generator and the output and call it instead.
					CodeInstruction self = new CodeInstruction(OpCodes.Ldarg_0);
					self.labels = code[i].labels;
					self.blocks = code[i].blocks;
					output.Add(self);
					output.Add(new CodeInstruction(OpCodes.Ldarg_1));
					output.Add(new CodeInstruction(OpCodes.Call, emitted));
					emittedReplaced++;
					continue;
				}

				output.Add(code[i]);
			}

			if (storedReplaced != 3 || emittedReplaced != 1)
			{
				throw new InvalidOperationException(
					"GeneratorEnthalpy expected three stored-path temperature reads and one "
					+ "Mathf.Max in EnergyGenerator.Emit and found " + storedReplaced + " and "
					+ emittedReplaced + ". The method has been rewritten; re-derive the anchors "
					+ "from the current game assembly before shipping.");
			}

			return output;
		}

		/// <summary>
		/// Whether the <c>get_Temperature</c> at <paramref name="index"/> is the one feeding the
		/// emitted path's <c>Mathf.Max(root_pe.Temperature, output.minTemperature)</c>.
		///
		/// Matched on the three instructions that follow it -- load the output, load
		/// <c>minTemperature</c>, call <c>Mathf.Max</c> -- rather than on a position, so the
		/// emitted and stored halves stay correctly distinguished if the branches move.
		/// </summary>
		private static bool FeedsTheFloorMax(List<CodeInstruction> code, int index,
			FieldInfo minTemperature, MethodInfo mathfMax)
		{
			if (index + 3 >= code.Count)
			{
				return false;
			}
			if (code[index + 1].opcode != OpCodes.Ldarg_1)
			{
				return false;
			}
			if (!code[index + 2].LoadsField(minTemperature))
			{
				return false;
			}
			return code[index + 3].Calls(mathfMax);
		}
	}
}
