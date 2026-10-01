using System;
using System.Collections.Generic;

namespace OniFramework
{
	/// <summary>
	/// The extended per-substance physical properties vanilla Oxygen Not Included does not have.
	///
	/// WHY THIS TYPE EXISTS:
	///
	///   1. Vanilla ONI carries part of this data, and the gap is exact. Both the native
	///      <c>Element</c> struct (<c>abi/sim_abi.h</c>, generated from the game's own layout) and
	///      the managed <c>Element</c> class carry
	///      <c>specificHeatCapacity</c>, <c>thermalConductivity</c>, <c>molarMass</c>,
	///      <c>lowTemp</c>/<c>highTemp</c> plus their transition targets, <c>maxMass</c>,
	///      <c>viscosity</c>, <c>flow</c>, <c>strength</c> and a <c>materialProperties</c> bitmask.
	///      Neither carries a latent heat, a heat-capacity ratio, a vapor-pressure curve, a
	///      minimum liquid pressure, a critical point, a liquid density, or a working-gas
	///      thermal efficiency. Those seven are what this type adds and nothing else.
	///   2. The replacement SimDLL does not carry it either: its phase-change step takes latent
	///      heat as a caller argument, and this registry is the caller's table.
	///   3. Stationeers models all of it. The seeded values follow Stationeers' atmospherics
	///      (<c>Mole</c> and <c>Chemistry</c>), except the latent heats, which are physical (see
	///      <see cref="MaterialPropertyRegistry"/>).
	///
	/// This layer is deliberately MANAGED-ONLY for now. Nothing in the native sim consumes latent
	/// heat or gamma today -- <c>ComputePhaseChangeStep</c> is a pure function that receives them
	/// as arguments -- so a parallel native per-element table would have zero native consumers and
	/// would cost a full offline-suite re-verification for nothing. The field set below is
	/// deliberately flat and blittable-shaped so that pushing it across as a future extension
	/// message (the next id after <c>kSetInvertedGravityElement</c>) is an addition, not a rewrite.
	/// </summary>
	public sealed class MaterialProperties
	{
		/// <summary>
		/// The substance's real MOLECULAR mass, in g/mol.
		///
		/// This field exists because Klei's own <c>Element.molarMass</c> cannot be trusted for
		/// real per-substance chemistry: it uses ATOMIC masses for diatomic elements. In the shipped
		/// content data (<c>StreamingAssets/elements/*.yaml</c>) Oxygen is <c>15.9994</c> (atomic O, not O2's 31.9988) and Hydrogen is
		/// <c>1.00794</c> (atomic H, not H2's 2.01588), while every molecular compound is correct
		/// (CarbonDioxide <c>44.01</c>, Methane <c>16.044</c>, Water/Steam <c>18.01528</c>).
		/// Stationeers' own table is no help here and is not an alternative: it stores Oxygen at
		/// <c>16.0</c> (the same atomic quirk), but also Water at <c>108.0</c> and Nitrogen at
		/// <c>64.0</c>, which are not molar masses of anything. It is a game-internal quantity
		/// used purely mole-side (<c>GetMass() =&gt; MolarMass() * Quantity</c>), never as a
		/// physical unit conversion.
		///
		/// This is therefore the single molar mass every per-mole-to-per-kilogram derivation in
		/// the registry crosses on: <see cref="MaterialPropertyRegistry.TryGetLiquidDensityKgPerM3"/>
		/// and <see cref="MaterialPropertyRegistry.TryGetLatentHeatOfVaporizationJPerKg"/>. That it independently reproduces real-world liquid densities from Stationeers'
		/// molar volumes is the evidence it is the right one.
		/// </summary>
		public readonly float MolecularMassGPerMol;

		/// <summary>
		/// Latent heat of vaporization, J/mol, at one named reference state and constant in
		/// temperature (the same shape as Stationeers' per-gas latent heat).
		/// A substance with no phase-change model at all (Stationeers' Helium) has 0 here and
		/// never triggers a transition.
		///
		/// THE SEEDED VALUES ARE PHYSICAL, not Stationeers' tuned ones: NIST's h(v) - h(l) at the
		/// normal boiling point (CO2 at its triple point, having no normal boiling point), times
		/// <see cref="MolecularMassGPerMol"/>, so the J/kg every consumer reads is NIST's own figure
		/// whatever molar-mass convention a record uses.
		/// </summary>
		public readonly float LatentHeatOfVaporizationJPerMol;

		/// <summary>
		/// The <c>A</c> coefficient of Stationeers' empirically-fit vapor-pressure power law,
		/// <c>EvaporationPressure(T) = A * T^B</c> in kPa (<c>MoleHelper.EvaporationCoefficientA</c>).
		/// Inverted for the boiling point at a given pressure by
		/// <see cref="MaterialPropertyRegistry.TryGetEvaporationTemperatureK"/>.
		/// </summary>
		public readonly double EvaporationCoefficientA;

		/// <summary>
		/// The <c>B</c> exponent of the same fit (<c>MoleHelper.EvaporationCoefficientB</c>).
		/// </summary>
		public readonly double EvaporationCoefficientB;

		/// <summary>
		/// The lowest pressure (Pa) at which this substance has a liquid phase at all --
		/// Stationeers' per-gas minimum liquid pressure, which it floors at the Armstrong limit,
		/// 6.3 kPa. Doubles as the pressure its triple point is
		/// evaluated at (see <see cref="MaterialPropertyRegistry.TryGetFreezingTemperatureK"/>).
		///
		/// CarbonDioxide's 517 kPa is the standout and is real: CO2 has no liquid phase below its
		/// own triple-point pressure, it sublimes solid-to-gas instead. That is exactly the
		/// constraint any liquid-CO2 process runs into, so it is carried rather than flattened to
		/// the 6.3 kPa default.
		/// </summary>
		public readonly float MinLiquidPressurePa;

		/// <summary>
		/// Whether this substance's GAS phase may condense into a liquid at all --  Stationeers'
		/// <c>MoleHelper.CanCondense(GasType)</c>. It is the first
		/// of the three tests Stationeers' gas-phase state change makes, ahead of the pressure floor
		/// and the saturation temperature, and it is a flat per-substance veto rather than anything
		/// derived.
		///
		/// TRUE FOR EVERY GAS STATIONEERS MODELS EXCEPT HELIUM, and Helium is the reason this is a
		/// stored field rather than an inference. It would be tempting to derive it -- Helium is
		/// also the one gas whose <see cref="LatentHeatOfVaporizationJPerMol"/> is 0, so "no latent
		/// heat means no condensation" reproduces the whole table today. That correspondence is one
		/// data point, not a rule: Stationeers keeps two independent switches
		/// (condensation and latent heat), and a
		/// registering mod must be able to disagree with one without editing the other.
		///
		/// NOR COULD THE PRESSURE FLOOR STAND IN FOR IT. Helium's raw minimum liquid pressure is
		/// <c>0.0</c>, but Stationeers floors every entry at the Armstrong limit, so the value it
		/// actually uses is 6.3 kPa -- a floor every breathable atmosphere clears. Helium is stopped here or it
		/// is not stopped at all.
		///
		/// THE VALUE IS THE FAMILY'S GAS-PHASE ONE, carried unchanged by the family's liquid and
		/// solid keys, exactly as <see cref="WorkingGasThermalEfficiency"/> is. Stationeers gives
		/// every liquid a flat <c>false</c>, which is the structural statement "a liquid is not a
		/// condensing gas" rather than per-substance data, and
		/// <see cref="MaterialPropertyRegistry.TryGetCanCondense"/> reproduces it by asking the
		/// element what phase it is in.
		///
		/// Defaults to <c>true</c>, which is Stationeers' answer for every substance this registry
		/// currently holds. A third-party mod registering a gas that should never condense passes
		/// <c>false</c>.
		/// </summary>
		public readonly bool CanCondense;

		/// <summary>
		/// The pressure (Pa) this substance's critical temperature is obtained by evaluating the
		/// vapor curve at. Stationeers defines every <c>Chemistry.CRITICAL_TEMPERATURE_*_K</c> as
		/// literally <c>MoleHelper.EvaporationTemperature(type, thisPressure)</c>, so no separate critical-temperature table is needed.
		/// 6000 kPa for every substance modeled here; a handful Stationeers models and this does
		/// not use other pressures (NitrousOxide 2000, LiquidAlcohol/HydrochloricAcid 1000,
		/// LiquidSodiumChloride 515), which is why this is a per-substance field rather than a
		/// shared constant.
		/// </summary>
		public readonly float CriticalPointPressurePa;

		/// <summary>
		/// A hardcoded freezing point (K) for the few substances Stationeers refuses to derive
		/// from its own curve, or <see cref="float.NaN"/> to derive it.
		///
		/// Water is the only substance in this registry that overrides:
		/// <c>Chemistry.TRIPLE_POINT_TEMPERATURE_WATER_K</c> is a literal <c>273.15</c>, not
		/// <c>EvaporationTemperature(Water, 6.3 kPa)</c> -- which would give 270.17 K, close but
		/// deliberately not what Stationeers itself uses. THIS IS A FALLBACK:
		/// Klei's element transition wins wherever the element table has one (water: 272.5 K),
		/// see <see cref="MaterialPropertyRegistry.TryGetFreezingTemperatureK"/>, so the override
		/// only answers before the element table loads. Note that Nitrogen's differently-named
		/// <c>FREEZING_TEMPERATURE_NITROGEN_K</c> is NOT an override despite the name: it is the
		/// same derived <c>EvaporationTemperature(type, 6.3 kPa)</c> construct, which is worth stating because the name invites exactly the wrong guess.
		/// </summary>
		public readonly float FreezingTemperatureOverrideK;

		/// <summary>
		/// Heat-capacity ratio (Cp/Cv, "gamma"), used for adiabatic compression work.
		///
		/// Stationeers buckets this by molecular shape rather than tabulating it per substance:
		/// <c>MonatomicDegreesOfFreedom = 1.666666</c>, <c>DiatomicDegreesOfFreedom = 1.4</c>,
		/// <c>TriatomicDegreesOfFreedom = 1.333333</c>, <c>PolyatomicDegreesOfFreedom = 1.26</c>.
		/// Every substance in this registry lands in the triatomic bucket, so this field carries
		/// no variation TODAY -- it is per-substance because the buckets genuinely differ for
		/// substances a later mod may register (Helium is monatomic; ethanol and the acids are
		/// polyatomic), and because a flat shared constant is precisely what this type exists to
		/// retire.
		/// </summary>
		public readonly float HeatCapacityRatio;

		/// <summary>
		/// Molar volume of the LIQUID phase, in L/mol, or 0 for a substance with no liquid-volume
		/// model -- Stationeers' per-gas molar volume.
		///
		/// Note that Stationeers keys this on the LIQUID member of each family
		/// (<c>Oxygen =&gt; 0.0</c> but <c>LiquidOxygen =&gt; 0.03</c>), with Water the exception
		/// because Stationeers' "Water" IS the liquid and its "Steam" is the gas. ONI's own
		/// naming has the identical shape, Water/Steam quirk included. This registry stores one
		/// record per substance FAMILY, so the value is the family's liquid-phase volume
		/// regardless of which phase was used to look the record up.
		/// </summary>
		public readonly float LiquidMolarVolumeLPerMol;

		/// <summary>
		/// The denominator relating fusion latent heat to vaporization latent heat --
		/// Stationeers derives fusion as vaporization divided by 5.0. Per-substance
		/// for the same reason <see cref="CriticalPointPressurePa"/> is: a registering mod with
		/// real measured data should be able to disagree with the flat 1/5 approximation.
		///
		/// THIS IS THE FALLBACK. <see cref="LatentHeatOfFusionJPerMol"/>
		/// wins wherever it is set; the denominator answers only for a record that leaves it NaN.
		/// </summary>
		public readonly float FusionToVaporizationDenominator;

		/// <summary>
		/// Latent heat of FUSION, J/mol, or <see cref="float.NaN"/> to derive it as
		/// vaporization / <see cref="FusionToVaporizationDenominator"/>.
		///
		/// A separate field because the fusion-to-vaporization ratio is not a constant of
		/// nature: it runs from 0.065 (O2) to 0.585 (CO2) across the seeded families, and no
		/// single denominator gets within a factor of two of both. An explicit 0 means "this
		/// substance has no fusion model" and is not the same as NaN.
		///
		/// Converted to J/kg with <see cref="MolecularMassGPerMol"/>, exactly as vaporization is,
		/// and pushed to the sim as <c>sim.latent_fusion</c> on every phase of the family, where
		/// the planetary latent accumulator bills a freeze or a melt with it.
		/// </summary>
		public readonly float LatentHeatOfFusionJPerMol;

		/// <summary>
		/// How much of an available hot/cold energy difference a Stirling-cycle engine charged with
		/// this substance can actually turn into work -- Stationeers' per-gas thermal efficiency,
		/// which its Stirling engine uses as the working-gas efficiency.
		/// Dimensionless, 0 to 1.
		///
		/// This is a WORKING-GAS property, not a heat-transfer one, and the distinction is the whole
		/// point of the field: it describes the substance sealed inside the machine as its piston
		/// charge, and says nothing about the substance being cooled or the substance carrying heat
		/// in. A monatomic or diatomic gas with a high heat-capacity ratio and a low molar mass
		/// cycles further per stroke, which is why hydrogen and helium lead the table at 0.18 and the
		/// heavy triatomics trail it at 0.08.
		///
		/// THE VALUE STORED HERE IS THE FAMILY'S GAS-PHASE ONE, and the liquid and solid keys of a
		/// family carry it unchanged. That is not a merge of two different numbers: Stationeers gives
		/// every liquid a flat 0.00, which is a STRUCTURAL rule ("a liquid is not a working gas")
		/// rather than per-substance data, and
		/// <see cref="MaterialPropertyRegistry.TryGetWorkingGasThermalEfficiency"/> reproduces it by
		/// asking the element what phase it is in rather than by writing zeros into this table. The
		/// difference matters for a registering mod: a table of zeros would be indistinguishable from
		/// "nobody filled this in".
		///
		/// Defaults to Stationeers' own fallback of 0.03 rather than to zero, so a substance
		/// registered by a third-party mod that never considered Stirling engines is poor rather than
		/// silently unusable.
		/// </summary>
		public readonly float WorkingGasThermalEfficiency;

		/// <summary>
		/// Builds one substance's property record. Every parameter is required except
		/// <paramref name="freezingTemperatureOverrideK"/> (defaults to "derive from the curve")
		/// and <paramref name="fusionToVaporizationDenominator"/> (defaults to Stationeers' 5),
		/// and <paramref name="workingGasThermalEfficiency"/> (defaults to Stationeers'
		/// fallback 0.03), and <paramref name="canCondense"/> (defaults to true, Stationeers'
		/// answer for every gas but Helium). <paramref name="canCondense"/> is LAST rather than
		/// beside <paramref name="minLiquidPressurePa"/> where it belongs by meaning, so that a
		/// caller passing arguments positionally is not silently re-bound by its arrival.
		/// <paramref name="latentHeatOfFusionJPerMol"/> (defaults to NaN, "derive
		/// via the denominator") is after it for the same reason.
		/// </summary>
		public MaterialProperties(
			float molecularMassGPerMol,
			float latentHeatOfVaporizationJPerMol,
			double evaporationCoefficientA,
			double evaporationCoefficientB,
			float minLiquidPressurePa,
			float criticalPointPressurePa,
			float heatCapacityRatio,
			float liquidMolarVolumeLPerMol,
			float freezingTemperatureOverrideK = float.NaN,
			float fusionToVaporizationDenominator = 5f,
			float workingGasThermalEfficiency =
				MaterialPropertyRegistry.DefaultWorkingGasThermalEfficiency,
			bool canCondense = true,
			float latentHeatOfFusionJPerMol = float.NaN)
		{
			MolecularMassGPerMol = molecularMassGPerMol;
			LatentHeatOfVaporizationJPerMol = latentHeatOfVaporizationJPerMol;
			EvaporationCoefficientA = evaporationCoefficientA;
			EvaporationCoefficientB = evaporationCoefficientB;
			MinLiquidPressurePa = minLiquidPressurePa;
			CriticalPointPressurePa = criticalPointPressurePa;
			HeatCapacityRatio = heatCapacityRatio;
			LiquidMolarVolumeLPerMol = liquidMolarVolumeLPerMol;
			FreezingTemperatureOverrideK = freezingTemperatureOverrideK;
			FusionToVaporizationDenominator = fusionToVaporizationDenominator;
			WorkingGasThermalEfficiency = workingGasThermalEfficiency;
			CanCondense = canCondense;
			LatentHeatOfFusionJPerMol = latentHeatOfFusionJPerMol;
		}
	}

	/// <summary>
	/// The public material-property registry: the one place extended physical properties for a
	/// substance are looked up, and the one place a third-party mod can add or replace them.
	///
	/// <see cref="GasMixtureFacade"/>'s property query methods forward here rather than owning
	/// tables of their own. New code should call this class directly.
	/// </summary>
	public static class MaterialPropertyRegistry
	{
		private static readonly Dictionary<SimHashes, MaterialProperties> Entries =
			new Dictionary<SimHashes, MaterialProperties>();

		/// <summary>
		/// Grams per kilogram. Every per-mole quantity in this registry has to cross into the
		/// per-kilogram world the sim actually tracks mass in, and that crossing is where a 1000x
		/// error hides. The rule: convert where OUR J/mol or g/mol data meets a kg-based mass, and never when
		/// handing a number back to one of Klei's own kernels (those are already internally
		/// consistent in their own per-gram units).
		/// </summary>
		private const float GramsPerKilogram = 1000f;

		/// <summary>Litres per cubic metre -- L/mol into kg/m3 for the liquid-density derivation.</summary>
		private const float LitresPerCubicMetre = 1000f;

		static MaterialPropertyRegistry()
		{
			SeedStationeersDefaults();
		}

		/// <summary>
		/// The substances seeded by default. Every value follows Stationeers' atmospherics
		/// (<c>Mole</c> and <c>Chemistry</c>) EXCEPT THE LATENT HEATS, which are physical:
		/// vaporization from NIST's reference equations of state, fusion from published tables,
		/// each cited beside its value below. Stationeers' own are 5-26x smaller and were tuned
		/// for its gameplay, not measured.
		///
		/// COVERAGE, and why it stops where it does:
		///
		/// Each family is registered under ALL of its phase keys -- gas, liquid and solid -- so a
		/// property lookup answers the same way whichever phase of a substance happens to be
		/// asking.
		///
		/// PHASE ALIASES ARE DELIBERATE AND THEY CHANGE PHYSICS. Two consequences, both intended:
		///
		///   * Liquid keys make boiling PRESSURE-DEPENDENT, and symmetric.
		///     <see cref="PhaseVessel.TransitionThresholdK"/> keys the vapor curve on the element
		///     being transitioned, so with only the gas key registered, Oxygen-&gt;LiquidOxygen
		///     condensation would use the curve while LiquidOxygen-&gt;Oxygen boiling fell back to
		///     vanilla's flat <c>highTemp</c>: one boundary, two thresholds depending on which way
		///     it was crossed. Registering the liquid key keeps the boundary symmetric.
		///   * Liquid keys give real liquid DENSITIES. <c>LiquidCarbonDioxide</c> now resolves to
		///     ~1100 kg/m3 instead of the flat 1000 kg/m3 fallback that was only ever correct for
		///     water.
		///
		/// Solid keys are registered for completeness and are inert by construction: fusion and
		/// sublimation thresholds are pressure-independent in both this project and Stationeers
		/// (Stationeers gates freeze and melt on a flat <c>FreezingTemperature()</c>), so no
		/// solid key can reach the vapor curve. What they do buy is a latent-heat lookup that
		/// succeeds from the solid side directly instead of relying on the counterpart-element
		/// fallback.
		///
		/// NITROGEN IS NOT SEEDED HERE BECAUSE ONI HAS NO NITROGEN: there is no
		/// <c>SimHashes.Nitrogen</c>. A mod that adds the element registers its family itself.
		/// </summary>
		private static void SeedStationeersDefaults()
		{
			// Water family. The one substance with a hardcoded freezing point (Stationeers'
			// literal 273.15 triple point, a fallback behind the element table's 272.5 K) and the one whose liquid member is the
			// family's base name rather than a "Liquid"-prefixed alias.
			RegisterFamily(
				new MaterialProperties(
					molecularMassGPerMol: 18.01528f,
					latentHeatOfVaporizationJPerMol: 40651f,   // NIST: 2256.5 kJ/kg at 373.12 K
					evaporationCoefficientA: 3.8782059839e-19,
					evaporationCoefficientB: 7.90030107708,
					minLiquidPressurePa: 6300f,
					criticalPointPressurePa: 6000000f,
					heatCapacityRatio: 1.333333f,
					liquidMolarVolumeLPerMol: 0.018f,
					freezingTemperatureOverrideK: 273.15f,
					workingGasThermalEfficiency: 0.05f,   // Stationeers' Steam
					latentHeatOfFusionJPerMol: 6009f),   // 333.55 kJ/kg, IAPWS via the CRC
				SimHashes.Water, SimHashes.Steam, SimHashes.Ice);

			// Polluted Water (Klei's element id is DirtyWater), registered because it
			// is a genuinely BETTER phase-change coolant than clean water and ONI already ships
			// the property that makes it one -- this only gives the vapour curve something to read.
			//
			// From the game's own elements/liquid.yaml: DirtyWater lowTemp 252.5 K, highTemp
			// 392.5 K, against Water's 273.15 K and 372.5 K. That is a 140 K liquid range against
			// water's 99 K -- it freezes 20.65 K colder and boils 20 K hotter. In a phase-change
			// cooling loop that margin is the whole ballgame: an evaporator line works by boiling,
			// boiling cools the line, and a line that cools past its own freezing point freezes
			// solid, ruptures, and dumps its contents into the room it was supposed to be cooling.
			//
			// Curve coefficients are the water family's. Not laziness: it is the same solvent with
			// a little dissolved matter, Klei models it with the same 4.179 specific heat, and
			// inventing a second fit would be inventing data. What IS different is taken from
			// Klei's own numbers -- molar mass 20 g/mol rather than 18.015, and the freezing point
			// override, which is the property being reached for.
			//
			// ONE ASYMMETRY WORTH KNOWING and deliberately not papered over: DirtyWater's
			// highTempTransitionTarget is Steam, and Steam's lowTempTransition is Water. So a
			// closed loop run on polluted water slowly purifies itself into clean water, which is
			// vanilla's own behaviour (boiling is how ONI cleans water) and is left alone here.
			// The coolant advantage is real while it is liquid, which is the regime a loop
			// actually runs in.
			RegisterFamily(
				new MaterialProperties(
					molecularMassGPerMol: 20f,
					latentHeatOfVaporizationJPerMol: 45130f,   // water's 2256.5 kJ/kg PER KG, at M = 20
					evaporationCoefficientA: 3.8782059839e-19,
					evaporationCoefficientB: 7.90030107708,
					minLiquidPressurePa: 6300f,
					criticalPointPressurePa: 6000000f,
					heatCapacityRatio: 1.333333f,
					liquidMolarVolumeLPerMol: 0.018f,
					freezingTemperatureOverrideK: 252.5f,
					workingGasThermalEfficiency: 0.05f,   // Stationeers' Pollutant
					latentHeatOfFusionJPerMol: 6671f),   // water's 333.55 kJ/kg per kg
				SimHashes.DirtyWater, SimHashes.DirtyIce);

			RegisterFamily(
				new MaterialProperties(
					molecularMassGPerMol: 31.9988f,
					latentHeatOfVaporizationJPerMol: 6817.5f,   // NIST: 213.1 kJ/kg at 90.19 K
					evaporationCoefficientA: 2.6854996004e-11,
					evaporationCoefficientB: 6.49214937325,
					minLiquidPressurePa: 6300f,
					criticalPointPressurePa: 6000000f,
					heatCapacityRatio: 1.333333f,
					liquidMolarVolumeLPerMol: 0.03f,
					workingGasThermalEfficiency: 0.12f,   // Stationeers' Oxygen
					latentHeatOfFusionJPerMol: 444f),   // CRC: 0.444 kJ/mol
				SimHashes.Oxygen, SimHashes.LiquidOxygen, SimHashes.SolidOxygen);

			RegisterFamily(
				new MaterialProperties(
					molecularMassGPerMol: 44.01f,
					latentHeatOfVaporizationJPerMol: 15420f,   // NIST: 350.4 kJ/kg at the 216.59 K triple point
					evaporationCoefficientA: 1.579573e-26,
					evaporationCoefficientB: 12.195837931,
					minLiquidPressurePa: 517000f,
					criticalPointPressurePa: 6000000f,
					heatCapacityRatio: 1.333333f,
					liquidMolarVolumeLPerMol: 0.04f,
					workingGasThermalEfficiency: 0.08f,   // Stationeers' value for carbon dioxide
					latentHeatOfFusionJPerMol: 9019f),   // 9.019 kJ/mol at the triple point
				SimHashes.CarbonDioxide, SimHashes.LiquidCarbonDioxide, SimHashes.SolidCarbonDioxide);

			RegisterFamily(
				new MaterialProperties(
					molecularMassGPerMol: 2.01588f,
					latentHeatOfVaporizationJPerMol: 904.5f,   // NIST: 448.7 kJ/kg at 20.37 K
					evaporationCoefficientA: 3.18041e-05,
					evaporationCoefficientB: 4.4843872973,
					minLiquidPressurePa: 6300f,
					criticalPointPressurePa: 6000000f,
					heatCapacityRatio: 1.333333f,
					liquidMolarVolumeLPerMol: 0.028f,
					workingGasThermalEfficiency: 0.18f,   // Stationeers' Hydrogen
					latentHeatOfFusionJPerMol: 117f),   // CRC: 0.117 kJ/mol
				SimHashes.Hydrogen, SimHashes.LiquidHydrogen, SimHashes.SolidHydrogen);

			// Methane completes the set of ONI gases that have a
			// full solid/liquid/gas ladder AND a Stationeers counterpart.
			RegisterFamily(
				new MaterialProperties(
					molecularMassGPerMol: 16.043f,
					latentHeatOfVaporizationJPerMol: 8195f,   // NIST: 510.8 kJ/kg at 111.67 K
					evaporationCoefficientA: 5.863496734e-15,
					evaporationCoefficientB: 7.8643601035,
					minLiquidPressurePa: 6300f,
					criticalPointPressurePa: 6000000f,
					heatCapacityRatio: 1.333333f,
					liquidMolarVolumeLPerMol: 0.04f,
					workingGasThermalEfficiency: 0.15f,   // Stationeers' Methane
					latentHeatOfFusionJPerMol: 939.2f),   // NIST: 0.9392 kJ/mol (Vogt and Pitzer)
				SimHashes.Methane, SimHashes.LiquidMethane, SimHashes.SolidMethane);
		}

		/// <summary>
		/// Whether <see cref="PushMolecularMassesToSim"/> has run for this game session. Also the
		/// "is the sim reachable" gate: before the first push, a late <see cref="Register"/> has
		/// nowhere to send its value and simply doesn't try.
		/// </summary>
		private static bool pushedToSim;

		/// <summary>
		/// Sends every registered substance's <see cref="MaterialProperties.MolecularMassGPerMol"/>
		/// down to the custom SimDLL, making this registry the authority on how many moles a
		/// kilogram of a gas is worth and therefore on every pressure the sim reports.
		///
		/// WHY THIS EXISTS. Klei's own element table stores the ATOMIC mass for the diatomic
		/// gases -- Oxygen 15.9994 rather than O2's 31.9988, Hydrogen 1.00794 rather than H2's
		/// 2.01588, and a Chlorine figure (34.453) that is a typo of atomic Cl on top of being
		/// atomic (<c>StreamingAssets/elements/gas.yaml</c>). Uncorrected, 6 kg of Oxygen in a
		/// 120 L pipe network at 293.2 K reads 7.62 MPa where the real answer is 3.81 MPa: exactly
		/// 2x, on every diatomic, everywhere a gas pressure is reported.
		///
		/// The native side seeds its own corrections for those four elements
		/// (<c>ElementTable::SeedDefaultMolecularMasses</c>, sim/world.h), so the SimDLL is right
		/// standalone with no message at all -- this push is what makes the correction part of the
		/// public API rather than a hardcoded native table. A third-party mod that registers a new gas here
		/// gets a correct native pressure for it without touching the SimDLL.
		///
		/// Only values strictly greater than zero are sent. That is a deliberate guard, not
		/// defensiveness: the native message treats a non-positive mass as "remove this override",
		/// so a record left with an unset molecular mass would silently REVERT Oxygen to Klei's
		/// wrong 15.9994 rather than leaving it alone.
		///
		/// Safe to call more than once -- the native side replaces an override rather than
		/// accumulating -- and safe to call before any element is registered.
		///
		/// This message is IMMEDIATE, not deferred: the value lands before the call returns,
		/// because the store it writes is read by exports that take no worker barrier
		/// (<c>SIM_ExtElementAttribute</c>).
		/// </summary>
		public static void PushMolecularMassesToSim()
		{
			foreach (KeyValuePair<SimHashes, MaterialProperties> entry in Entries)
			{
				SendMolecularMass(entry.Key, entry.Value);
			}
			PushPhaseCurvesToSim();
			pushedToSim = true;
		}

		private static bool pushAtEveryOpening;

		/// <summary>
		/// Pushes everything <see cref="PushMolecularMassesToSim"/> pushes at EVERY
		/// <see cref="SimExtRegistrar.Opening"/>: after <c>SIM_Initialize</c> and the element
		/// table, before the world is allocated -- and so before a save's <c>Load</c> and before
		/// worldgen's settle frames. Call once, from <c>UserMod2.OnLoad</c>. Idempotent, and it
		/// installs <see cref="SimExtRegistrar"/> itself.
		///
		/// <b>WHY <c>Game.OnSpawn</c> IS TOO LATE.</b> The sim's <c>Load</c> runs Klei's load-time state transition (<c>DoLoadTimeStateTransition</c>):
		/// every cell more than 3 K out of its element's range takes one transition as it loads.
		/// The sim gates that transition with the same phase rules the running frame uses -- the
		/// condensation rule (<c>sim.can_condense</c>, <c>sim.min_liquid_pressure</c>) and the
		/// pressure-dependent boil (<c>sim.phase_curve</c>) -- but those are element attributes,
		/// they are not saved, and <c>SIM_Initialize</c> discards them. A mod that pushes them
		/// only from <c>Game.OnSpawn</c> reaches the sim one <c>Load</c> too late, and every cell
		/// its rules were holding (CO2 kept gaseous under a floor pressure, a pressurised liquid
		/// kept from boiling) takes Klei's ungated transition on every load.
		///
		/// Harmless alongside an existing <c>OnSpawn</c> push: the native side replaces an entry
		/// rather than accumulating one.
		/// </summary>
		/// <param name="harmony">The calling mod's Harmony instance, for
		/// <see cref="SimExtRegistrar.Install"/>.</param>
		public static void PushToSimAtEveryOpening(HarmonyLib.Harmony harmony)
		{
			SimExtRegistrar.Install(harmony);
			if (pushAtEveryOpening)
			{
				return;
			}
			pushAtEveryOpening = true;
			SimExtRegistrar.Opening += PushMolecularMassesToSim;
		}

		/// <summary>
		/// Pushes every registered material's vapour curve (<c>sim.phase_curve</c>) and liquid
		/// density (<c>sim.liquid_density</c>) to the sim, for the conduit-run phase change the sim
		/// decides under <see cref="ConduitNetworkPolicy.Phase"/>. Called by
		/// <see cref="PushMolecularMassesToSim"/>, so every consumer that already pushes the
		/// molecular masses gets these too.
		///
		/// THE CURVE IS PUSHED PRE-RESOLVED, not as raw fit coefficients plus pressures: the
		/// freezing and critical temperatures go across already evaluated by
		/// <see cref="TryGetFreezingTemperatureK"/> and <see cref="TryGetCriticalTemperatureK"/>,
		/// so the sim's clamp is this registry's clamp and nothing is derived twice. An element
		/// missing any one of the five is not pushed at all -- the sim then never changes its
		/// phase in a conduit, which is exactly what the managed pass does for the same record
		/// (every one of those Try* calls is a <c>continue</c> there).
		///
		/// Silently does nothing on a SimDLL without the attributes.
		/// </summary>
		public static void PushPhaseCurvesToSim()
		{
			foreach (KeyValuePair<SimHashes, MaterialProperties> entry in Entries)
			{
				SendPhaseData(entry.Key, entry.Value);
			}
		}

		/// <summary>
		/// One element's share of <see cref="PushPhaseCurvesToSim"/>: the vapour curve, the liquid
		/// density, and the two attributes the sim's cell condensation
		/// rule reads. Every write is skipped silently when the SimDLL does not have the attribute
		/// or the registry cannot supply the value, so a stock SimDLL and an under-described
		/// element both end at "no change from vanilla".
		///
		/// THE TWO CONDENSATION ATTRIBUTES ARE WRITTEN AGAINST THE GAS MEMBER OF A FAMILY ONLY.
		/// The native rule (<c>CondensationRuleOf</c>, sim/physics.h) consults them only after it
		/// has established that the source element is a gas whose low-temperature target is a
		/// liquid, so an entry on a liquid or a solid key is an entry nothing reads -- and it would
		/// not be inert, because the sim's test for "has the managed side described this element"
		/// is the presence of a <c>sim.can_condense</c> entry. Writing one everywhere would make
		/// that question unanswerable.
		///
		/// The phase comes from <c>ElementLoader</c>, so before the element table loads this
		/// writes no condensation rule at all. That is the correct failure: an element the sim has
		/// not been told about keeps Klei's fixed threshold. <see cref="PushMolecularMassesToSim"/> runs
		/// from <c>Game.OnSpawn</c>, long after the table is built.
		/// </summary>
		private static void SendPhaseData(SimHashes id, MaterialProperties p)
		{
			if (p == null)
			{
				return;
			}

			int curve = SimExtElementAttributes.PhaseCurveIndex;
			if (curve >= 0 &&
				p.EvaporationCoefficientA > 0.0 && p.EvaporationCoefficientB != 0.0 &&
				TryGetFreezingTemperatureK(id, out float freezingK) &&
				TryGetCriticalTemperatureK(id, out float criticalK) &&
				TryGetLatentHeatOfVaporizationJPerKg(id, out float latentJPerKg) &&
				latentJPerKg > 0f)
			{
				SimExtElementAttributes.SetFloat(curve, id, (float)p.EvaporationCoefficientA, 0);
				SimExtElementAttributes.SetFloat(curve, id, (float)p.EvaporationCoefficientB, 1);
				SimExtElementAttributes.SetFloat(curve, id, freezingK, 2);
				SimExtElementAttributes.SetFloat(curve, id, criticalK, 3);
				SimExtElementAttributes.SetFloat(curve, id, latentJPerKg, 4);
			}

			int density = SimExtElementAttributes.LiquidDensityIndex;
			if (density >= 0 && TryGetLiquidDensityKgPerM3(id, out float densityKgPerM3) &&
				densityKgPerM3 > 0f)
			{
				SimExtElementAttributes.SetFloat(density, id, densityKgPerM3);
			}

			// The enthalpy of fusion, on EVERY member of the family,
			// the same way the curve goes. The sim decides which member to read -- the liquid for a
			// freeze or a melt, the gas for a direct gas <-> solid step -- so this side does not
			// have to know the phase. Read only by the planetary latent accumulator; -1 on a
			// SimDLL without the attribute, which then counts condensation and boiling alone.
			int fusion = SimExtElementAttributes.LatentFusionIndex;
			if (fusion >= 0 && TryGetLatentHeatOfFusionJPerKg(id, out float fusionJPerKg) &&
				fusionJPerKg > 0f)
			{
				SimExtElementAttributes.SetFloat(fusion, id, fusionJPerKg);
			}

			if (!IsGasPhase(id))
			{
				return;
			}

			// The floor goes across in PASCALS, which is what the record already holds and what
			// the sim's own gas pressures are in -- Stationeers' table is in kPa and this registry
			// converted it once, at the point the numbers were read, rather than at every use.
			int floor = SimExtElementAttributes.MinLiquidPressureIndex;
			if (floor >= 0 && p.MinLiquidPressurePa > 0f)
			{
				SimExtElementAttributes.SetFloat(floor, id, p.MinLiquidPressurePa);
			}

			// WRITTEN LAST ON PURPOSE. This is the entry the sim treats as "the managed side has
			// described this element", and the moment it lands the element stops using Klei's
			// threshold -- so it must not land before the floor and the curve it is going to be
			// evaluated with. The writes are immediate and on this thread, so the ordering is real
			// rather than decorative.
			int condense = SimExtElementAttributes.CanCondenseIndex;
			if (condense >= 0)
			{
				SimExtElementAttributes.SetFloat(condense, id, p.CanCondense ? 1f : 0f);
			}
		}

		/// <summary>
		/// Whether <paramref name="id"/> is a gas right now, according to Klei's own element
		/// table. False -- not "unknown" -- before the table loads, because every caller here
		/// wants "may I treat this as a gas", and "I cannot tell" must answer that with no.
		/// </summary>
		private static bool IsGasPhase(SimHashes id)
		{
			if (ElementLoader.elements == null)
			{
				return false;
			}
			Element element = ElementLoader.FindElementByHash(id);
			return element != null && element.IsGas;
		}

		/// <summary>
		/// One <c>ext::kSetMolecularMass</c> ("ONI7", abi/sim_abi_ext.h) message. The payload is
		/// keyed on the SimHashes id -- Klei's <c>Hash.SDBMLower</c> of the element name, which is
		/// exactly what the native <c>Element::id</c> field carries -- and NOT on an element-table
		/// index, so the override survives the sim's element table being reloaded; <c>vftest</c>
		/// asserts that it does.
		/// </summary>
		private static unsafe void SendMolecularMass(SimHashes id, MaterialProperties properties)
		{
			if (properties == null || properties.MolecularMassGPerMol <= 0f)
			{
				return;
			}

			// THE ATTRIBUTE ROUTE FIRST. The molecular mass is an ordinary
			// registered per-element attribute (`sim.molecular_mass`) rather than a hardcoded
			// side table, and going through the registry is what gives this value a READ-BACK
			// path: SimExtElementAttributes.TryGetMolecularMass can answer "what molar mass is
			// the sim actually using", which a write-only message cannot. Same store either way -- the legacy id below is a convenience
			// alias for exactly this attribute.
			int attribute = SimExtElementAttributes.MolecularMassIndex;
			if (attribute >= 0)
			{
				SimExtElementAttributes.SetFloat(attribute, id, properties.MolecularMassGPerMol);
				return;
			}

			// THE LEGACY ROUTE, and it is not dead code: a custom SimDLL older than stage 5 has
			// kSetMolecularMass and no attribute registry, so resolving the attribute returns -1
			// there and the write has to go the old way or not at all. On a stock SimDLL both
			// routes are dropped as unknown ids, which is the intended silent degradation.
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes((int)id), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(properties.MolecularMassGPerMol), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetMolecularMass, payload.Length, msg);
			}
		}

		/// <summary>
		/// The molecular mass the SIM is using for <paramref name="id"/> right now, in g/mol,
		/// which is not necessarily what this registry holds: the sim seeds its own diatomic
		/// corrections before any mod says anything, another mod may have overridden the value,
		/// and a stock SimDLL holds nothing at all.
		///
		/// Returns false when the sim has no override for that element -- in which case it falls
		/// back to Klei's own <c>Element.molarMass</c>, which for the diatomic gases is the
		/// ATOMIC figure and therefore half the truth.
		///
		/// Lets a push made through <see cref="PushMolecularMassesToSim"/> be verified rather than
		/// assumed, and a pressure that looks wrong be traced back to the number it was computed
		/// from.
		/// </summary>
		public static bool TryGetSimMolecularMass(SimHashes id, out float gramsPerMole)
		{
			return SimExtElementAttributes.TryGetMolecularMass(id, out gramsPerMole);
		}

		/// <summary>
		/// Registers (or replaces) the property record for <paramref name="id"/>. This is the
		/// third-party entry point: a mod adding a new element, or disagreeing with a seeded
		/// value, calls this. Replacement is deliberate and silent -- last writer wins -- because
		/// a mod whose whole purpose is better material data must be able to override a default
		/// without the framework arbitrating between mods.
		/// </summary>
		public static void Register(SimHashes id, MaterialProperties properties)
		{
			if (properties == null)
			{
				throw new ArgumentNullException(nameof(properties));
			}
			Entries[id] = properties;
			// A registration that happens AFTER the bulk push (a third-party mod loading late, or
			// a mod overriding a seeded value at runtime) has to reach the sim too, otherwise the
			// registry and the SimDLL disagree about what a kilogram of that gas weighs in moles.
			// Before the first push there is no live sim to send to, so this stays quiet.
			if (pushedToSim)
			{
				SendMolecularMass(id, properties);
				// And the phase data with it. The molecular mass alone would give a mod registering
				// after Game.OnSpawn a corrected pressure and no vapour curve -- a half-described
				// element, which the sim reads as an element with no phase model at all.
				SendPhaseData(id, properties);
			}
		}

		/// <summary>
		/// Registers one record under several element ids at once -- the normal shape for a
		/// substance family whose phases are separate ONI elements (Water/Steam, or a caller
		/// opting its own liquid and solid aliases in).
		/// </summary>
		public static void RegisterFamily(MaterialProperties properties, params SimHashes[] ids)
		{
			if (properties == null)
			{
				throw new ArgumentNullException(nameof(properties));
			}
			if (ids == null)
			{
				throw new ArgumentNullException(nameof(ids));
			}
			for (int i = 0; i < ids.Length; i++)
			{
				Entries[ids[i]] = properties;
				// Same late-registration push as Register above -- every phase key of a family
				// carries the same molecule, so each one gets the same corrected molecular mass.
				if (pushedToSim)
				{
					SendMolecularMass(ids[i], properties);
					SendPhaseData(ids[i], properties);
				}
			}
		}

		/// <summary>
		/// Removes <paramref name="id"/>'s record, returning <c>true</c> if there was one. A
		/// substance with no record is inert rather than defaulted: every query below returns
		/// <c>false</c> and its caller falls back to whatever vanilla behavior it had before.
		/// </summary>
		public static bool Unregister(SimHashes id)
		{
			return Entries.Remove(id);
		}

		/// <summary>Whether <paramref name="id"/> has a property record.</summary>
		public static bool IsRegistered(SimHashes id)
		{
			return Entries.ContainsKey(id);
		}

		/// <summary>
		/// The full property record for <paramref name="id"/>, or <c>false</c> if it has none.
		/// Prefer the derived accessors below where one exists -- they carry the unit conversions
		/// and the clamping rules, which are easy to get wrong when open-coded at a call site.
		/// </summary>
		public static bool TryGet(SimHashes id, out MaterialProperties properties)
		{
			return Entries.TryGetValue(id, out properties);
		}

		/// <summary>Every element id with a property record, in no particular order.</summary>
		public static IEnumerable<SimHashes> RegisteredElements
		{
			get { return Entries.Keys; }
		}

		/// <summary>How many element ids currently have a record.</summary>
		public static int Count
		{
			get { return Entries.Count; }
		}

		/// <summary>
		/// The substance's real molecular mass (g/mol) -- see
		/// <see cref="MaterialProperties.MolecularMassGPerMol"/> for why this exists alongside
		/// Klei's own <c>Element.molarMass</c> rather than deferring to it.
		/// </summary>
		public static bool TryGetMolecularMassGPerMol(SimHashes id, out float molecularMassGPerMol)
		{
			if (Entries.TryGetValue(id, out MaterialProperties p) && p.MolecularMassGPerMol > 0f)
			{
				molecularMassGPerMol = p.MolecularMassGPerMol;
				return true;
			}
			molecularMassGPerMol = 0f;
			return false;
		}

		/// <summary>
		/// The heat-capacity ratio (Cp/Cv) for <paramref name="id"/>, for adiabatic compression
		/// work. Replaces the flat shared constant Mod 1 used before this registry existed; see
		/// <see cref="MaterialProperties.HeatCapacityRatio"/> for why the seeded values currently
		/// agree with that constant anyway.
		/// </summary>
		public static bool TryGetHeatCapacityRatio(SimHashes id, out float heatCapacityRatio)
		{
			if (Entries.TryGetValue(id, out MaterialProperties p) && p.HeatCapacityRatio > 0f)
			{
				heatCapacityRatio = p.HeatCapacityRatio;
				return true;
			}
			heatCapacityRatio = 0f;
			return false;
		}

		/// <summary>
		/// Stationeers' fallback working-gas efficiency, for any gas its table does not list. Public because it is the value a third-party mod's
		/// substance silently receives, and a number that decides how good somebody's machine is
		/// should not be a private literal.
		/// </summary>
		public const float DefaultWorkingGasThermalEfficiency = 0.03f;

		/// <summary>
		/// The fraction of an available hot/cold energy difference a Stirling-cycle engine
		/// charged with <paramref name="id"/> can turn into work.
		///
		/// TWO GATES, and both are Stationeers' own rather than this project's:
		///
		///   * <b>Only a gas is a working gas.</b> Stationeers gives every liquid a flat 0.00
		///     thermal efficiency, so a machine charged with a liquid produces
		///     nothing at all. Reproduced here by asking the live element what state it is in
		///     (<c>Element.IsGas</c>) rather than by seeding zeros, because this registry stores
		///     one record per substance FAMILY and a family's liquid key would otherwise have to
		///     contradict its gas key. A solid charge is refused for the same reason.
		///   * <b>An unregistered substance is poor, not unusable.</b> It gets
		///     <see cref="DefaultWorkingGasThermalEfficiency"/>, matching Stationeers' default
		///     arm, and the method still returns true. A caller that needs to know whether real
		///     data existed should ask <see cref="IsRegistered"/>.
		///
		/// Returns false, with zero, only when the substance cannot be a working gas at all --
		/// which is the answer a machine wants, since it is also the answer that makes its power
		/// output zero.
		///
		/// SAFE BEFORE THE ELEMENT TABLE LOADS. If <c>ElementLoader</c> has not populated yet the
		/// phase gate cannot be evaluated, and the registry answers from its table alone rather
		/// than refusing -- a load-order accident must not silently turn every engine off.
		/// </summary>
		public static bool TryGetWorkingGasThermalEfficiency(SimHashes id, out float efficiency)
		{
			Element element = ElementLoader.elements == null
				? null
				: ElementLoader.FindElementByHash(id);
			if (element != null && !element.IsGas)
			{
				efficiency = 0f;
				return false;
			}

			efficiency = Entries.TryGetValue(id, out MaterialProperties p)
				? p.WorkingGasThermalEfficiency
				: DefaultWorkingGasThermalEfficiency;
			return efficiency > 0f;
		}

		/// <summary>
		/// Density of this substance's LIQUID phase, kg/m3, derived as
		/// <c>MolecularMassGPerMol / LiquidMolarVolumeLPerMol</c> (g/mol divided by L/mol is g/L,
		/// which is already kg/m3 -- the two 1000s cancel, which is why no conversion constant
		/// appears in the arithmetic).
		///
		/// This derivation is worth spelling out because it validates: using the substance's real
		/// molecular mass rather than Klei's atomic-for-diatomics <c>Element.molarMass</c>,
		/// Stationeers' own liquid molar volumes reproduce real-world liquid densities across the
		/// whole seeded set -- H2O 1001 (real 1000), CO2 1100 (1101), H2 72 (71), O2 1066 (1141),
		/// CH4 401 (422). Two independently-sourced tables agreeing to within a few percent is
		/// considerably stronger evidence than either one alone, and it replaces a flat 1000 kg/m3
		/// placeholder that was correct only for water.
		///
		/// Returns <c>false</c> for a substance with no liquid-volume model (Stationeers stores 0
		/// for those), leaving the caller on whatever fallback it used before.
		/// </summary>
		public static bool TryGetLiquidDensityKgPerM3(SimHashes id, out float densityKgPerM3)
		{
			if (Entries.TryGetValue(id, out MaterialProperties p) &&
				p.LiquidMolarVolumeLPerMol > 0f && p.MolecularMassGPerMol > 0f)
			{
				densityKgPerM3 = p.MolecularMassGPerMol / p.LiquidMolarVolumeLPerMol;
				return true;
			}
			densityKgPerM3 = 0f;
			return false;
		}

		/// <summary>
		/// The lowest pressure at which <paramref name="id"/> has a liquid phase at all, in Pa --
		/// Stationeers' minimum liquid pressure, already floored at the Armstrong limit of 6.3 kPa. Below this the substance sublimes rather
		/// than condensing, and the sim refuses the transition however cold the cell gets.
		///
		/// This is a FAMILY property and answers for every phase key, because it is also the
		/// pressure the family's triple point is evaluated at -- see
		/// <see cref="TryGetFreezingTemperatureK"/>, which reads the same number from the solid
		/// side. Use <see cref="TryGetCanCondense"/> for the question that is phase-specific.
		/// </summary>
		public static bool TryGetMinLiquidPressurePa(SimHashes id, out float pressurePa)
		{
			if (Entries.TryGetValue(id, out MaterialProperties p) && p.MinLiquidPressurePa > 0f)
			{
				pressurePa = p.MinLiquidPressurePa;
				return true;
			}
			pressurePa = 0f;
			return false;
		}

		/// <summary>
		/// Whether <paramref name="id"/> may condense out of the gas phase -- Stationeers'
		/// <c>MoleHelper.CanCondense(GasType)</c>, the first test its gas-phase state change makes.
		///
		/// Returns false for anything that is not currently a gas, which is the phase half of the
		/// rule rather than a lookup failure: Stationeers answers <c>false</c> for every liquid in
		/// its table, and this reproduces that by asking the element table what phase the element
		/// is in instead of storing a row of falses that would be indistinguishable from an
		/// unfilled record. Same shape, and for the same reason, as
		/// <see cref="TryGetWorkingGasThermalEfficiency"/>.
		///
		/// Note the asymmetry with the sim-side <see cref="SimExtElementAttributes.TryGetCanCondense"/>:
		/// there the return value means "the sim has been told about this element" and the out
		/// parameter carries the verdict, because the sim genuinely distinguishes an element
		/// nobody described from one described as never condensing. Here the record either exists
		/// or it does not, and <paramref name="canCondense"/> is the answer.
		/// </summary>
		public static bool TryGetCanCondense(SimHashes id, out bool canCondense)
		{
			canCondense = false;
			if (!IsGasPhase(id))
			{
				return false;
			}
			if (!Entries.TryGetValue(id, out MaterialProperties p))
			{
				return false;
			}
			canCondense = p.CanCondense;
			return true;
		}

		/// <summary>
		/// What the SIM is holding for <paramref name="id"/>'s condensation rule, as opposed to
		/// what this registry believes: a straight pass-through to
		/// <see cref="SimExtElementAttributes.TryGetCanCondense"/>, kept here so a caller that
		/// pushed through <see cref="PushMolecularMassesToSim"/> can verify the push landed
		/// without reaching past the registry to the attribute layer. The counterpart to
		/// <see cref="TryGetSimMolecularMass"/>, and there for the same reason: a write-only route
		/// cannot be verified.
		/// </summary>
		public static bool TryGetSimCanCondense(SimHashes id, out bool canCondense)
		{
			return SimExtElementAttributes.TryGetCanCondense(id, out canCondense);
		}

		/// <summary>
		/// Latent heat of vaporization in J/kg for <paramref name="id"/>, converted from the
		/// stored J/mol via this substance's own
		/// <see cref="MaterialProperties.MolecularMassGPerMol"/>.
		///
		/// WHICH MOLAR MASS, because the obvious answers are both wrong. Klei's
		/// <c>Element.molarMass</c> is ATOMIC rather than molecular for the diatomics, so O2 and H2
		/// would come out at twice the J/kg their own J/mol figure supports. Stationeers' molar
		/// masses are no better: it stores Water at <c>108.0</c> g/mol and Nitrogen at <c>64.0</c>, neither of which is a real molar mass
		/// of anything. Its whole table is a game-internal quantity used mole-side
		/// (<c>GetMass() =&gt; MolarMass() * Quantity</c>), never as a physical unit conversion,
		/// and it is unusable as one -- Water at 108 g/mol over its own 0.018 L/mol liquid molar
		/// volume implies a 6000 kg/m3 liquid.
		///
		/// The registry's own is the right one: the same
		/// <see cref="MaterialProperties.MolecularMassGPerMol"/> that
		/// <see cref="TryGetLiquidDensityKgPerM3"/> uses, which is the value that reproduces
		/// real-world liquid densities from Stationeers' own molar volumes. One molar mass serves
		/// both derivations.
		///
		/// The seeded J/mol values are PHYSICAL, stored as NIST's kJ/kg times
		/// this same molecular mass, so what this returns for a seeded family is NIST's figure:
		/// water 2256.5 kJ/kg, CO2 350.4, where Stationeers' numbers gave 444.1 and 13.6.
		/// </summary>
		public static bool TryGetLatentHeatOfVaporizationJPerKg(SimHashes id,
			out float latentHeatJPerKg)
		{
			if (Entries.TryGetValue(id, out MaterialProperties p) &&
				p.LatentHeatOfVaporizationJPerMol > 0f && p.MolecularMassGPerMol > 0f)
			{
				latentHeatJPerKg =
					p.LatentHeatOfVaporizationJPerMol / p.MolecularMassGPerMol * GramsPerKilogram;
				return true;
			}
			latentHeatJPerKg = 0f;
			return false;
		}

		/// <summary>
		/// Latent heat of FUSION (solid&lt;-&gt;liquid) in J/kg. The record's explicit
		/// <see cref="MaterialProperties.LatentHeatOfFusionJPerMol"/> when it has one (every seeded
		/// family); otherwise Stationeers' derived
		/// <c>LatentHeatOfVaporization / 5</c>, using this substance's own
		/// <see cref="MaterialProperties.FusionToVaporizationDenominator"/>. An explicit value of
		/// 0 or less answers false: that record has said it has no fusion model, and the
		/// denominator is not consulted behind its back.
		/// </summary>
		public static bool TryGetLatentHeatOfFusionJPerKg(SimHashes id, out float latentHeatJPerKg)
		{
			if (Entries.TryGetValue(id, out MaterialProperties explicitRecord) &&
				!float.IsNaN(explicitRecord.LatentHeatOfFusionJPerMol))
			{
				if (explicitRecord.LatentHeatOfFusionJPerMol > 0f &&
					explicitRecord.MolecularMassGPerMol > 0f)
				{
					latentHeatJPerKg = explicitRecord.LatentHeatOfFusionJPerMol /
						explicitRecord.MolecularMassGPerMol * GramsPerKilogram;
					return true;
				}
				latentHeatJPerKg = 0f;
				return false;
			}
			if (Entries.TryGetValue(id, out MaterialProperties p) &&
				p.FusionToVaporizationDenominator > 0f &&
				TryGetLatentHeatOfVaporizationJPerKg(id, out float vaporizationJPerKg))
			{
				latentHeatJPerKg = vaporizationJPerKg / p.FusionToVaporizationDenominator;
				return true;
			}
			latentHeatJPerKg = 0f;
			return false;
		}

		/// <summary>
		/// Latent heat of SUBLIMATION (solid&lt;-&gt;gas) in J/kg -- both costs in series, which is
		/// what Stationeers charges for the same transition.
		/// </summary>
		public static bool TryGetLatentHeatOfSublimationJPerKg(SimHashes id,
			out float latentHeatJPerKg)
		{
			if (TryGetLatentHeatOfVaporizationJPerKg(id, out float vaporizationJPerKg) &&
				TryGetLatentHeatOfFusionJPerKg(id, out float fusionJPerKg))
			{
				latentHeatJPerKg = vaporizationJPerKg + fusionJPerKg;
				return true;
			}
			latentHeatJPerKg = 0f;
			return false;
		}

		/// <summary>
		/// The RAW pressure-dependent evaporation/condensation point (K) --
		/// <c>(P / A) ^ (1 / B)</c> with P in kPa.
		///
		/// Prefer <see cref="TryGetEvaporationTemperatureClampedK"/> at essentially every call
		/// site. A power-law fit is only meaningful inside the range it was fitted over, and
		/// Stationeers itself never calls the raw form at a state-change site; every state change
		/// uses the clamped one. The raw form at 1 Pa gives an unphysical 89.28 K boiling point
		/// for water.
		/// </summary>
		public static bool TryGetEvaporationTemperatureK(SimHashes id, float pressurePa, out float temperatureK)
		{
			temperatureK = 0f;
			if (pressurePa <= 0f || !Entries.TryGetValue(id, out MaterialProperties p) ||
				p.EvaporationCoefficientA <= 0.0 || p.EvaporationCoefficientB == 0.0)
			{
				return false;
			}
			double pressureKPa = pressurePa / 1000.0;
			temperatureK = (float)Math.Pow(pressureKPa / p.EvaporationCoefficientA, 1.0 / p.EvaporationCoefficientB);
			return true;
		}

		/// <summary>
		/// The saturation (vapor) pressure in pascals at <paramref name="temperatureK"/> --
		/// <c>A * T^B</c>, with the kPa result the curve is fitted in converted to pascals.
		///
		/// This is the FORWARD direction of the same curve
		/// <see cref="TryGetEvaporationTemperatureK"/> inverts, and it is Stationeers' own
		/// primitive: <c>MoleHelper.EvaporationPressure(type, temperature)</c>. It answers "what
		/// pressure does this fluid want to sit at", which a hand-tuned constant cannot.
		///
		/// WHY A LOOP NEEDS IT. Charging a gas line by MASS without it is easy to get badly wrong:
		/// 1 kg of steam in a 10 L tile at 310 K is about 14 MPa, past Water's 6 MPa critical
		/// point, so the dew point clamps to the critical temperature of 643.7 K and the line sits
		/// 333 K BELOW its own dew point. `ComputePhaseChangeStep` is energy-limited
		/// rather than purely rate-limited -- the mass it converts is bounded by what the latent
		/// heat can warm back to the boundary -- so at that much subcooling the bound is the whole
		/// tile, and the entire charge condenses in a single 200 ms tick. The resulting liquid,
		/// standing inside a GAS network, trips the liquid-in-a-gas-pipe damage rule and ruptures
		/// the run. A loop must be charged to a state its own vapor curve agrees
		/// with, and that requires asking the curve which pressure that is.
		///
		/// Returns false for an unregistered material or a non-positive temperature, the same
		/// contract as every other accessor here. NOT clamped: unlike the temperature direction,
		/// where an unclamped answer gives an unphysical 89.28 K boiling point, evaluating A * T^B is monotonic and well behaved across the whole range, and a
		/// caller that needs to respect the critical point can compare against
		/// <see cref="TryGetCriticalTemperatureK"/> -- which is itself defined in terms of this
		/// same curve.
		/// </summary>
		public static bool TryGetSaturationPressurePa(SimHashes id, float temperatureK,
			out float pressurePa)
		{
			pressurePa = 0f;
			if (temperatureK <= 0f || !Entries.TryGetValue(id, out MaterialProperties p) ||
				p.EvaporationCoefficientA <= 0.0 || p.EvaporationCoefficientB == 0.0)
			{
				return false;
			}
			double pressureKPa = p.EvaporationCoefficientA
				* Math.Pow(temperatureK, p.EvaporationCoefficientB);
			pressurePa = (float)(pressureKPa * 1000.0);
			return true;
		}

		/// <summary>
		/// The temperature (K) below which <paramref name="id"/> has no liquid phase, for a
		/// registered element.
		///
		/// KLEI'S OWN ELEMENT DATA FIRST. When the loaded element table gives this
		/// substance a liquid-to-solid transition, the answer is that transition's temperature --
		/// the liquid's <c>lowTemp</c>, which in Klei's data is also its solid's <c>highTemp</c> --
		/// so a pipe, the sim's conduit-run phase decider (the value goes across in
		/// <see cref="PushPhaseCurvesToSim"/>) and the world's own cells all freeze a substance at
		/// ONE temperature. The Stationeers derivation below disagrees with it: oxygen would
		/// freeze at 56.42 K in a pipe and 54.36 K in a cell, methane at 81.53 K against 90.55 K, water at 273.15 K against 272.5 K. Klei's
		/// numbers are the measured triple points (54.36 K is oxygen's); the derived ones are an
		/// artefact of Stationeers evaluating every curve at its own 6.3 kPa floor.
		///
		/// Otherwise -- a substance with no solid in the element table, such as a mod-registered
		/// element that declares itself as its own freezing target, or any call made before
		/// <c>ElementLoader</c> has loaded -- its <see cref="MaterialProperties.FreezingTemperatureOverrideK"/>
		/// if it has one, else the vapor curve evaluated at its own
		/// <see cref="MaterialProperties.MinLiquidPressurePa"/>. Stationeers' own debug command
		/// names that construct outright ("TriplePoint: {EvaporationTemperature(type,
		/// MinLiquidPressure())}K at {MinLiquidPressure()}kPa").
		/// </summary>
		public static bool TryGetFreezingTemperatureK(SimHashes id, out float temperatureK)
		{
			temperatureK = 0f;
			if (!Entries.TryGetValue(id, out MaterialProperties p))
			{
				return false;
			}
			if (TryGetElementFreezingTemperatureK(id, out temperatureK))
			{
				return true;
			}
			if (!float.IsNaN(p.FreezingTemperatureOverrideK))
			{
				temperatureK = p.FreezingTemperatureOverrideK;
				return true;
			}
			return TryGetEvaporationTemperatureK(id, p.MinLiquidPressurePa, out temperatureK);
		}

		/// <summary>
		/// The liquid-to-solid transition temperature Klei's loaded element table gives the
		/// substance <paramref name="id"/> belongs to, reached from any of its phases: a liquid's
		/// own <c>lowTemp</c>, a solid's <c>highTemp</c>, a gas's liquid's <c>lowTemp</c>. False
		/// when the table is not loaded yet or the substance has no liquid/solid pair in it.
		/// </summary>
		private static bool TryGetElementFreezingTemperatureK(SimHashes id, out float temperatureK)
		{
			temperatureK = 0f;
			if (ElementLoader.elements == null || ElementLoader.elements.Count == 0)
			{
				return false;
			}
			Element element = ElementLoader.FindElementByHash(id);
			if (element == null)
			{
				return false;
			}
			if (element.IsSolid)
			{
				Element liquid = element.highTempTransition;
				if (liquid == null || !liquid.IsLiquid || element.highTemp <= 0f)
				{
					return false;
				}
				temperatureK = element.highTemp;
				return true;
			}
			Element liquidPhase = element.IsGas ? element.lowTempTransition : element;
			if (liquidPhase == null || !liquidPhase.IsLiquid)
			{
				return false;
			}
			Element solid = liquidPhase.lowTempTransition;
			if (solid == null || !solid.IsSolid || liquidPhase.lowTemp <= 0f)
			{
				return false;
			}
			temperatureK = liquidPhase.lowTemp;
			return true;
		}

		/// <summary>
		/// The critical temperature (K) -- the vapor curve evaluated at this substance's
		/// <see cref="MaterialProperties.CriticalPointPressurePa"/>, which is how Stationeers
		/// defines every one of its own <c>CRITICAL_TEMPERATURE_*_K</c> constants.
		/// </summary>
		public static bool TryGetCriticalTemperatureK(SimHashes id, out float temperatureK)
		{
			temperatureK = 0f;
			return Entries.TryGetValue(id, out MaterialProperties p) &&
				TryGetEvaporationTemperatureK(id, p.CriticalPointPressurePa, out temperatureK);
		}

		/// <summary>
		/// The evaporation/condensation point (K) at <paramref name="pressurePa"/>, CLAMPED to
		/// this substance's freezing and critical temperatures -- Stationeers'
		/// <c>EvaporationTemperatureClamped</c>, which is the form every one of its own state-change
		/// call sites uses.
		///
		/// A pressure the curve cannot evaluate at all (zero, negative, or a substance with no
		/// coefficients) resolves to the freezing point rather than failing: a liquid at
		/// effectively no pressure boils as readily as it physically can, and that floor is its
		/// freezing point, not an arbitrary constant such as a 1 Pa floor.
		/// </summary>
		public static bool TryGetEvaporationTemperatureClampedK(SimHashes id, float pressurePa,
			out float temperatureK)
		{
			temperatureK = 0f;
			if (!TryGetFreezingTemperatureK(id, out float freezingK) ||
				!TryGetCriticalTemperatureK(id, out float criticalK))
			{
				return false;
			}
			if (!TryGetEvaporationTemperatureK(id, pressurePa, out float rawK))
			{
				temperatureK = freezingK;
				return true;
			}
			temperatureK = (float)Math.Min(Math.Max(rawK, freezingK), criticalK);
			return true;
		}
	}
}
