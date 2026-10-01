using System;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// How breathable a cell's oxygen content is, graded by OXYGEN PARTIAL PRESSURE rather than by
	/// mass. See <see cref="AtmosphereFacade"/> for why that distinction is the whole point.
	/// </summary>
	public enum OxygenGrade
	{
		/// <summary>Below <see cref="AtmosphereFacade.CriticalOxygenPartialPressurePa"/> -- not breathable at all.</summary>
		None = 0,

		/// <summary>Between the critical and low thresholds. Breathable, but actively dangerous.</summary>
		Critical = 1,

		/// <summary>Between the low and safe thresholds. Breathable, with a warning.</summary>
		Low = 2,

		/// <summary>At or above <see cref="AtmosphereFacade.BreathableOxygenPartialPressurePa"/>.</summary>
		Safe = 3
	}

	/// <summary>
	/// How survivable a cell's air TEMPERATURE is to breathe, which is a separate question from
	/// whether the occupant is comfortable standing in it.
	/// </summary>
	public enum AirTemperatureGrade
	{
		/// <summary>Below <see cref="AtmosphereFacade.FrigidAirK"/>. Damaging, red warning.</summary>
		Frigid = 0,

		/// <summary>Between frigid and comfortable. Yellow warning, no damage.</summary>
		Cold = 1,

		/// <summary>Inside the comfortable band. No warning.</summary>
		Comfortable = 2,

		/// <summary>Above <see cref="AtmosphereFacade.ComfortableAirMaximumK"/>. Yellow warning AND damaging.</summary>
		Hot = 3
	}

	/// <summary>
	/// How much of a cell's air is something that should not be inhaled, graded by MOLE FRACTION.
	/// Named for carbon dioxide because that is the case with published human thresholds, but the
	/// same grades are applied to the toxic fraction (Pollutant and friends) as well.
	/// </summary>
	public enum ContaminantGrade
	{
		/// <summary>Below every threshold.</summary>
		Clear = 0,

		/// <summary>At or above <see cref="AtmosphereFacade.HeadacheContaminantFraction"/>.</summary>
		Headache = 1,

		/// <summary>At or above <see cref="AtmosphereFacade.ImpairedContaminantFraction"/>.</summary>
		Impaired = 2,

		/// <summary>At or above <see cref="AtmosphereFacade.SuffocatingContaminantFraction"/> -- suffocating regardless of how much oxygen is also present.</summary>
		Suffocating = 3
	}

	/// <summary>
	/// One cell's air, graded on every axis that decides whether breathing it is survivable. Every
	/// field is a real measurement, not a verdict, so a consumer that disagrees with this
	/// framework's thresholds can re-grade from the same numbers.
	/// </summary>
	public struct AirAssessment
	{
		/// <summary>The cell this describes.</summary>
		public int Cell;

		/// <summary>
		/// True when the cell holds real multi-species mixture state (<see cref="GasMixtureFacade.IsRoomOwned"/>),
		/// false when this was derived from vanilla's single-element <c>Grid</c> fields. Both
		/// answers are usable; this says which one it is.
		/// </summary>
		public bool IsMixture;

		/// <summary>Total absolute pressure, Pa. Zero for a solid or vacuum cell.</summary>
		public float TotalPressurePa;

		/// <summary>Oxygen's own partial pressure, Pa. This is what <see cref="OxygenGrade"/> grades.</summary>
		public float OxygenPartialPressurePa;

		/// <summary>Oxygen's mole fraction, 0-1. The number a combustion model wants.</summary>
		public float OxygenMoleFraction;

		/// <summary>Carbon dioxide's mole fraction, 0-1.</summary>
		public float CarbonDioxideMoleFraction;

		/// <summary>
		/// Combined mole fraction of every species the element table marks toxic
		/// (<c>Element.toxicity &gt; 0</c>), which is how Pollutant, contaminated oxygen and any
		/// third-party toxic gas all get counted without this facade holding a list of names.
		/// </summary>
		public float ToxicMoleFraction;

		/// <summary>Air temperature, K.</summary>
		public float TemperatureK;

		/// <summary>Total gas mass in the cell, kg.</summary>
		public float TotalMassKg;

		/// <summary>Grade of <see cref="OxygenPartialPressurePa"/>.</summary>
		public OxygenGrade Oxygen;

		/// <summary>Grade of <see cref="TemperatureK"/>.</summary>
		public AirTemperatureGrade Temperature;

		/// <summary>Grade of <see cref="CarbonDioxideMoleFraction"/>.</summary>
		public ContaminantGrade CarbonDioxide;

		/// <summary>Grade of <see cref="ToxicMoleFraction"/>.</summary>
		public ContaminantGrade Toxic;

		/// <summary>
		/// Whether breathing this air causes damage right now, on any axis: no usable oxygen,
		/// a suffocating contaminant fraction, or a temperature outside the survivable band.
		/// </summary>
		public bool IsHarmful
		{
			get
			{
				return Oxygen == OxygenGrade.None
					|| CarbonDioxide == ContaminantGrade.Suffocating
					|| Toxic == ContaminantGrade.Suffocating
					|| Temperature == AirTemperatureGrade.Frigid
					|| Temperature == AirTemperatureGrade.Hot;
			}
		}

		/// <summary>
		/// Whether this air is safe on every axis. Deliberately not the negation of
		/// <see cref="IsHarmful"/>: air can be warning-worthy without being damaging.
		/// </summary>
		public bool IsFullySafe
		{
			get
			{
				return Oxygen == OxygenGrade.Safe
					&& Temperature == AirTemperatureGrade.Comfortable
					&& CarbonDioxide == ContaminantGrade.Clear
					&& Toxic == ContaminantGrade.Clear;
			}
		}
	}

	/// <summary>
	/// STUB. What a combustion model would need from a cell, measured and handed over without any
	/// ignition being modelled. See <see cref="AtmosphereFacade.EvaluateCombustionRisk"/>.
	/// </summary>
	public struct CombustionRisk
	{
		/// <summary>The cell this describes.</summary>
		public int Cell;

		/// <summary>Oxygen mole fraction, 0-1.</summary>
		public float OxygenMoleFraction;

		/// <summary>
		/// Combined mole fraction of every species this facade currently treats as fuel
		/// (Hydrogen, Methane), 0-1.
		/// </summary>
		public float FuelMoleFraction;

		/// <summary>The single fuel species holding the most moles, or 0 when there is no fuel.</summary>
		public ushort DominantFuelIdx;

		/// <summary>Air temperature, K.</summary>
		public float TemperatureK;

		/// <summary>
		/// True when the oxygen fraction is at or above <see cref="AtmosphereFacade.OxygenEnrichedMoleFraction"/>.
		/// Real-world oxygen-enriched atmospheres ignite materials that are inert in ordinary air;
		/// this is the flag a future combustion model reads, and the one that makes an all-oxygen
		/// colony a hazard rather than an optimisation.
		/// </summary>
		public bool IsOxygenEnriched;

		/// <summary>
		/// True when fuel and oxygen are both present in quantities that could sustain a flame.
		/// Coarse on purpose: this is a stub, and a real model wants per-fuel flammability limits.
		/// </summary>
		public bool IsFlammableMixture;
	}

	/// <summary>
	/// AIR, as a thing with a COMPOSITION -- and the physiology that follows from it.
	///
	/// WHAT VANILLA ONI DOES, and why it cannot express this. A vanilla cell holds exactly one
	/// element. "Breathable" is a tag on that element, and how much of it there is is a mass in kg
	/// (<c>GasBreatherFromWorldProvider.GetBreathableCellMass</c>: it reads
	/// <c>Grid.Element[cell].HasTag(Breathable)</c> and returns <c>Grid.Mass[cell]</c>). There are
	/// two tiers, both mass thresholds on the breather. So in vanilla a room is breathable because
	/// it is FULL OF OXYGEN, and any inert gas mixed into it is not diluting the oxygen -- it has
	/// displaced the oxygen entirely, because a cell cannot hold both.
	///
	/// That is the limitation this facade exists to remove. Real breathing does not care about the
	/// mass of oxygen in a room; it cares about oxygen's PARTIAL PRESSURE, which is what actually
	/// drives gas across a lung membrane. A 100 kPa room that is 25% oxygen and a 25 kPa room that
	/// is 100% oxygen are the same breath. Once a cell can hold a real mixture (the custom SimDLL's
	/// gas-mixture state, reached through <see cref="GasMixtureFacade"/>), partial pressure is
	/// computable and the vanilla mass test becomes the special case it always was.
	///
	/// WHERE THE THRESHOLDS COME FROM. Stationeers' own published breathing model, which is itself
	/// grounded in real physiology, and which is reused here rather than re-derived:
	///
	///   - 16 kPa oxygen partial pressure is safe. Below it, a warning; below 12 kPa, a severe
	///     warning; below 5 kPa there is not enough oxygen to breathe at all. Real physiology puts
	///     the hypoxia threshold at 16 kPa (sea-level air is 21 kPa) and unconsciousness near
	///     5 kPa, so these are not arbitrary game numbers.
	///   - The inert fraction does not matter. Nitrogen, argon and carbon dioxide below its own
	///     threshold are all simply not oxygen; they dilute but do not poison. This is why
	///     "breathable air" is 75/25 nitrogen/oxygen rather than pure oxygen, and why filling a
	///     colony with pure oxygen is a fire hazard rather than an upgrade.
	///   - Carbon dioxide is graded on its own fraction, not on displacement: 0.5% causes
	///     headaches, 1% causes cognitive impairment, and about 7% suffocates EVEN WITH ENOUGH
	///     OXYGEN PRESENT. That last clause is the part vanilla cannot express at all, because a
	///     vanilla cell holding CO2 holds no oxygen by construction.
	///   - Air outside 0-50 C damages the lungs; below -10 C is the severe case.
	///
	/// WHAT THIS IS NOT. It grades air. It does not apply effects, damage anyone, or patch the
	/// game -- a facade that reached into duplicant health would be gameplay living in the
	/// framework. A gameplay mod turns these grades into status items and effects; a third-party mod could grade the same
	/// air and do something completely different with the answer, which is the point.
	///
	/// EVERY CELL ANSWERS. A cell with no mixture state falls back to vanilla's own single-element
	/// fields and comes back with <see cref="AirAssessment.IsMixture"/> false. A caller never has
	/// to ask "is this cell promoted?" before it can ask "is this air breathable?".
	/// </summary>
	public static class AtmosphereFacade
	{
		/// <summary>
		/// Oxygen partial pressure at or above which air is simply breathable, Pa.
		/// </summary>
		public const float BreathableOxygenPartialPressurePa = 16000f;

		/// <summary>
		/// Oxygen partial pressure below which the warning becomes severe, Pa. Between this and
		/// <see cref="BreathableOxygenPartialPressurePa"/> the air is breathable but low.
		/// </summary>
		public const float LowOxygenPartialPressurePa = 12000f;

		/// <summary>
		/// Oxygen partial pressure below which air cannot be breathed at all, Pa. Between this and
		/// <see cref="LowOxygenPartialPressurePa"/> the air is breathable but critical.
		/// </summary>
		public const float CriticalOxygenPartialPressurePa = 5000f;

		/// <summary>Coldest air that can be breathed without lung damage, K (0 C).</summary>
		public const float ComfortableAirMinimumK = 273.15f;

		/// <summary>Hottest air that can be breathed without lung damage, K (50 C).</summary>
		public const float ComfortableAirMaximumK = 323.15f;

		/// <summary>Below this the cold warning becomes severe, K (-10 C).</summary>
		public const float FrigidAirK = 263.15f;

		/// <summary>Contaminant mole fraction at which headaches begin (0.5%).</summary>
		public const float HeadacheContaminantFraction = 0.005f;

		/// <summary>Contaminant mole fraction at which cognitive impairment begins (1%).</summary>
		public const float ImpairedContaminantFraction = 0.01f;

		/// <summary>
		/// Contaminant mole fraction at which the air suffocates regardless of its oxygen content
		/// (7%).
		/// </summary>
		public const float SuffocatingContaminantFraction = 0.07f;

		/// <summary>
		/// Oxygen mole fraction at or above which an atmosphere counts as oxygen-enriched, and
		/// materials that are inert in ordinary air become ignitable. The real-world figure
		/// (23.5% by volume) rather than a tuned one; ordinary air is 21%.
		/// </summary>
		public const float OxygenEnrichedMoleFraction = 0.235f;

		/// <summary>
		/// Mole fraction of fuel below which a mixture cannot sustain a flame however much oxygen
		/// is present. Methane's own lower flammability limit (5% by volume), used as the single
		/// coarse limit for every fuel until a real combustion model replaces this stub with
		/// per-fuel limits.
		/// </summary>
		public const float LowerFlammabilityMoleFraction = 0.05f;

		/// <summary>
		/// The species this stub currently counts as fuel. Deliberately short: it is the set ONI
		/// already ships that would actually burn, and extending it is a combustion model's job,
		/// not this facade's.
		/// </summary>
		private static readonly SimHashes[] FuelSpecies =
		{
			SimHashes.Hydrogen,
			SimHashes.Methane
		};

		/// <summary>
		/// One species' partial pressure in <paramref name="cell"/>, Pa.
		///
		/// Computed the same way the sim computes total pressure -- the same native ideal-gas call
		/// with only that species' mass in it -- rather than as (mole fraction x total pressure).
		/// The two are algebraically identical for an ideal gas, and going through the sim means
		/// this cannot drift from the pressure the rest of the game reports.
		///
		/// Returns <c>false</c> for an invalid cell, a cell holding no gas, or a species that is
		/// not present: 0 Pa is a real answer and cannot double as "no data", the same collapse
		/// every other <c>TryGet*</c> in this framework avoids.
		/// </summary>
		public static bool TryGetPartialPressurePa(int cell, ushort elementIdx, out float pressurePa)
		{
			pressurePa = 0f;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}

			float massKg;
			float temperatureK = Grid.Temperature[cell];
			if (GasMixtureFacade.IsRoomOwned(cell))
			{
				massKg = 0f;
				GasMixtureFacade.GasComponent[] composition = GasMixtureFacade.TryGetComposition(cell);
				for (int i = 0; i < composition.Length; i++)
				{
					if (composition[i].ElementIdx == elementIdx)
					{
						massKg = composition[i].MassKg;
						break;
					}
				}
			}
			else
			{
				Element element = Grid.Element[cell];
				if (element == null || !element.IsGas || element.idx != elementIdx)
				{
					return false;
				}
				massKg = Grid.Mass[cell];
			}

			if (massKg <= 0f || temperatureK <= 0f)
			{
				return false;
			}

			return GasMixtureFacade.TryComputePressure(new[] { (int)elementIdx }, new[] { massKg },
				temperatureK, PipeNetworkFacade.LiveCellVolumeM3, out pressurePa);
		}

		/// <summary>
		/// One species' mole fraction in <paramref name="cell"/>, 0-1.
		///
		/// Moles come from mass / molar mass, and the molar mass comes from
		/// <see cref="MaterialPropertyRegistry"/> first, falling back to the element table. That
		/// order is deliberate and matters: ONI's own element data stores ATOMIC mass for its
		/// diatomic gases (Oxygen is 15.9994, Hydrogen 1.00794) but real MOLECULAR mass for
		/// everything else (CarbonDioxide 44.01), so mole fractions computed from the element
		/// table alone are wrong by a factor of two between those two groups. The property
		/// registry holds real molecular masses throughout, and is the same table the native sim
		/// is given, so this agrees with the pressure the sim reports.
		/// </summary>
		public static bool TryGetMoleFraction(int cell, ushort elementIdx, out float fraction)
		{
			fraction = 0f;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}

			float speciesMoles = 0f;
			float totalMoles = 0f;
			if (GasMixtureFacade.IsRoomOwned(cell))
			{
				GasMixtureFacade.GasComponent[] composition = GasMixtureFacade.TryGetComposition(cell);
				for (int i = 0; i < composition.Length; i++)
				{
					float moles = MolesOf(composition[i].ElementIdx, composition[i].MassKg);
					totalMoles += moles;
					if (composition[i].ElementIdx == elementIdx)
					{
						speciesMoles = moles;
					}
				}
			}
			else
			{
				Element element = Grid.Element[cell];
				if (element == null || !element.IsGas)
				{
					return false;
				}
				totalMoles = MolesOf(element.idx, Grid.Mass[cell]);
				speciesMoles = element.idx == elementIdx ? totalMoles : 0f;
			}

			if (totalMoles <= 0f)
			{
				return false;
			}

			fraction = speciesMoles / totalMoles;
			return true;
		}

		/// <summary>
		/// Grades <paramref name="cell"/>'s air on every axis at once -- one composition read
		/// instead of one per question, which matters because this runs per duplicant per
		/// breathing tick.
		///
		/// A cell that holds no gas at all comes back with every grade at its worst
		/// (<see cref="OxygenGrade.None"/>, temperature graded from whatever the cell's own
		/// temperature is) rather than with a "no data" flag: vacuum is a real, and fatal, answer
		/// to "can this be breathed".
		/// </summary>
		public static AirAssessment Assess(int cell)
		{
			AirAssessment assessment = default(AirAssessment);
			assessment.Cell = cell;
			if (!Grid.IsValidCell(cell))
			{
				assessment.Oxygen = OxygenGrade.None;
				assessment.Temperature = AirTemperatureGrade.Comfortable;
				return assessment;
			}

			assessment.TemperatureK = Grid.Temperature[cell];
			assessment.IsMixture = GasMixtureFacade.IsRoomOwned(cell);

			ushort oxygenIdx = IndexOf(SimHashes.Oxygen);
			ushort carbonDioxideIdx = IndexOf(SimHashes.CarbonDioxide);

			float totalMoles = 0f;
			float oxygenMoles = 0f;
			float carbonDioxideMoles = 0f;
			float toxicMoles = 0f;
			float totalMassKg = 0f;

			if (assessment.IsMixture)
			{
				GasMixtureFacade.GasComponent[] composition = GasMixtureFacade.TryGetComposition(cell);
				int[] speciesIdx = new int[composition.Length];
				float[] speciesMass = new float[composition.Length];
				for (int i = 0; i < composition.Length; i++)
				{
					ushort idx = composition[i].ElementIdx;
					float massKg = composition[i].MassKg;
					speciesIdx[i] = idx;
					speciesMass[i] = massKg;
					totalMassKg += massKg;

					float moles = MolesOf(idx, massKg);
					totalMoles += moles;
					if (IsOxygenBearing(idx, oxygenIdx))
					{
						oxygenMoles += moles;
					}
					if (idx == carbonDioxideIdx)
					{
						carbonDioxideMoles += moles;
					}
					if (IsToxic(idx))
					{
						toxicMoles += moles;
					}
				}

				if (composition.Length > 0 && assessment.TemperatureK > 0f)
				{
					GasMixtureFacade.TryComputePressure(speciesIdx, speciesMass,
						assessment.TemperatureK, PipeNetworkFacade.LiveCellVolumeM3,
						out assessment.TotalPressurePa);
				}
			}
			else
			{
				Element element = Grid.Element[cell];
				if (element != null && element.IsGas)
				{
					totalMassKg = Grid.Mass[cell];
					float moles = MolesOf(element.idx, totalMassKg);
					totalMoles = moles;
					if (IsOxygenBearing(element.idx, oxygenIdx))
					{
						oxygenMoles = moles;
					}
					if (element.idx == carbonDioxideIdx)
					{
						carbonDioxideMoles = moles;
					}
					if (IsToxic(element.idx))
					{
						toxicMoles = moles;
					}

					if (totalMassKg > 0f && assessment.TemperatureK > 0f)
					{
						GasMixtureFacade.TryComputePressure(new[] { (int)element.idx },
							new[] { totalMassKg }, assessment.TemperatureK,
							PipeNetworkFacade.LiveCellVolumeM3, out assessment.TotalPressurePa);
					}
				}
			}

			assessment.TotalMassKg = totalMassKg;
			if (totalMoles > 0f)
			{
				assessment.OxygenMoleFraction = oxygenMoles / totalMoles;
				assessment.CarbonDioxideMoleFraction = carbonDioxideMoles / totalMoles;
				assessment.ToxicMoleFraction = toxicMoles / totalMoles;
			}

			// Oxygen's partial pressure straight from the sim's own gas law, for the same reason
			// TryGetPartialPressurePa does it that way: (fraction x total) would be algebraically
			// identical but would drift the moment anything about the pressure model changed.
			if (oxygenMoles > 0f && assessment.TemperatureK > 0f)
			{
				float oxygenMassKg = oxygenMoles * MolarMassGPerMol(oxygenIdx) / 1000f;
				GasMixtureFacade.TryComputePressure(new[] { (int)oxygenIdx },
					new[] { oxygenMassKg }, assessment.TemperatureK,
					PipeNetworkFacade.LiveCellVolumeM3, out assessment.OxygenPartialPressurePa);
			}

			assessment.Oxygen = GradeOxygen(assessment.OxygenPartialPressurePa);
			assessment.Temperature = GradeTemperature(assessment.TemperatureK);
			assessment.CarbonDioxide = GradeContaminant(assessment.CarbonDioxideMoleFraction);
			assessment.Toxic = GradeContaminant(assessment.ToxicMoleFraction);
			return assessment;
		}

		/// <summary>
		/// THE DRAFT at a cell: the pressure gradient the simulation's own gas transport is
		/// acting on, in pascals per tile, pointing the way air is being pushed.
		///
		/// <paramref name="eastwardPa"/> is positive when the air at this cell is being driven
		/// east (its western neighbour is at higher pressure than its eastern one);
		/// <paramref name="northwardPa"/> likewise for up. The magnitude is a real pressure
		/// difference, not a normalised score, so a caller can compare two drafts or set a
		/// threshold in units the rest of the game already reports.
		///
		/// WHY THIS IS A READ AND NOT A MECHANISM. The custom SimDLL already moves gas between
		/// adjacent cells down each species' own partial-pressure gradient every mixing tick
		/// (<c>MixPair</c>, sim/gas_mixture.h, driven by <c>TickRoomPooledMixing</c>) -- Dalton's
		/// law, per species, which is the transport model. Nothing needs to invent a draft; what
		/// creates one is a machine that lowers the pressure at one end of a room and raises it
		/// at the other. This exposes the resulting gradient so a mod can SEE the draft, aim a
		/// sensor at it, or decide a room is stagnant, without knowing which buildings made it.
		///
		/// Returns false when the cell is not a valid gas cell, in which case both outputs are
		/// zero. A neighbour that has no pressure (a solid tile, the map edge) contributes its
		/// own cell's pressure instead, so a draft measured against a wall reads as zero across
		/// that axis rather than as a huge gradient into rock.
		/// </summary>
		public static bool TryGetDraft(int cell, out float eastwardPa, out float northwardPa)
		{
			eastwardPa = 0f;
			northwardPa = 0f;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}
			if (!TryCellPressurePa(cell, out float here))
			{
				return false;
			}

			float west = NeighbourPressurePa(Grid.CellLeft(cell), here);
			float east = NeighbourPressurePa(Grid.CellRight(cell), here);
			float south = NeighbourPressurePa(Grid.CellBelow(cell), here);
			float north = NeighbourPressurePa(Grid.CellAbove(cell), here);

			eastwardPa = (west - east) * 0.5f;
			northwardPa = (south - north) * 0.5f;
			return true;
		}

		private static float NeighbourPressurePa(int cell, float fallbackPa)
		{
			return TryCellPressurePa(cell, out float pressurePa) ? pressurePa : fallbackPa;
		}

		/// <summary>
		/// One cell's total gas pressure, mixture-aware: the mixture layer's own answer when the
		/// cell is promoted, and the ideal-gas pressure of vanilla's single element when it is
		/// not, so a draft can be measured across the boundary between a promoted room and an
		/// ordinary one.
		/// </summary>
		private static bool TryCellPressurePa(int cell, out float pressurePa)
		{
			pressurePa = 0f;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}
			if (GasMixtureFacade.IsRoomOwned(cell))
			{
				return GasMixtureFacade.TryGetPressure(cell, out pressurePa);
			}
			Element element = Grid.Element[cell];
			if (element == null || !element.IsGas)
			{
				return false;
			}
			float massKg = Grid.Mass[cell];
			float temperatureK = Grid.Temperature[cell];
			if (massKg <= 0f || temperatureK <= 0f)
			{
				return false;
			}
			return GasMixtureFacade.TryComputePressure(new[] { (int)element.idx },
				new[] { massKg }, temperatureK, PipeNetworkFacade.LiveCellVolumeM3, out pressurePa);
		}

		/// <summary>
		/// Pressure that ONE MOLE of gas exerts in the given volume at the given temperature,
		/// asked of the SIMULATION rather than computed from a hand-typed gas constant
		/// (native-sim-first discipline).
		///
		/// The species handed to the sim is arbitrary because an ideal gas does not care --
		/// P = nRT/V -- and oxygen is used simply because it is guaranteed to exist in every
		/// build. The point of routing through <see cref="GasMixtureFacade.TryComputePressure"/>
		/// is that this number cannot drift away from the pressures the rest of the game reports.
		///
		/// Public because every device that has to convert between a pressure SETPOINT and an
		/// amount of gas needs it, and two copies of this would be two things to keep in step.
		/// </summary>
		public static float PressurePerMolePa(float temperatureK, float volumeM3)
		{
			if (temperatureK <= 0f || volumeM3 <= 0f)
			{
				return 0f;
			}
			ushort oxygenIdx = IndexOf(SimHashes.Oxygen);
			if (oxygenIdx == 0)
			{
				return 0f;
			}
			float oneMoleKg = MolarMassGPerMol(oxygenIdx) / 1000f;
			if (oneMoleKg <= 0f)
			{
				return 0f;
			}
			if (!GasMixtureFacade.TryComputePressure(new[] { (int)oxygenIdx },
					new[] { oneMoleKg }, temperatureK, volumeM3, out float pressurePa))
			{
				return 0f;
			}
			return pressurePa;
		}

		/// <summary>
		/// Moles of gas a pressure of <paramref name="pressurePa"/> represents in the given
		/// volume at the given temperature -- Stationeers' own <c>IdealGas.Quantity</c>, which
		/// every setpoint-seeking vent and mixer in that game sizes its transfers with. Negative
		/// or zero pressure returns zero moles rather than a negative transfer.
		/// </summary>
		public static float MolesFromPressurePa(float pressurePa, float volumeM3,
			float temperatureK)
		{
			if (pressurePa <= 0f)
			{
				return 0f;
			}
			float perMole = PressurePerMolePa(temperatureK, volumeM3);
			return perMole > 0f ? pressurePa / perMole : 0f;
		}

		/// <summary>Grades an oxygen partial pressure on its own, for a caller that already has one.</summary>
		public static OxygenGrade GradeOxygen(float oxygenPartialPressurePa)
		{
			if (oxygenPartialPressurePa >= BreathableOxygenPartialPressurePa)
			{
				return OxygenGrade.Safe;
			}
			if (oxygenPartialPressurePa >= LowOxygenPartialPressurePa)
			{
				return OxygenGrade.Low;
			}
			if (oxygenPartialPressurePa >= CriticalOxygenPartialPressurePa)
			{
				return OxygenGrade.Critical;
			}
			return OxygenGrade.None;
		}

		/// <summary>Grades an air temperature on its own, for a caller that already has one.</summary>
		public static AirTemperatureGrade GradeTemperature(float temperatureK)
		{
			if (temperatureK > ComfortableAirMaximumK)
			{
				return AirTemperatureGrade.Hot;
			}
			if (temperatureK < FrigidAirK)
			{
				return AirTemperatureGrade.Frigid;
			}
			if (temperatureK < ComfortableAirMinimumK)
			{
				return AirTemperatureGrade.Cold;
			}
			return AirTemperatureGrade.Comfortable;
		}

		/// <summary>Grades a contaminant mole fraction on its own, for a caller that already has one.</summary>
		public static ContaminantGrade GradeContaminant(float moleFraction)
		{
			if (moleFraction >= SuffocatingContaminantFraction)
			{
				return ContaminantGrade.Suffocating;
			}
			if (moleFraction >= ImpairedContaminantFraction)
			{
				return ContaminantGrade.Impaired;
			}
			if (moleFraction >= HeadacheContaminantFraction)
			{
				return ContaminantGrade.Headache;
			}
			return ContaminantGrade.Clear;
		}

		/// <summary>
		/// STUB, on purpose, and the hook a combustion model is meant to land on.
		///
		/// It measures what a fire needs -- an oxidiser fraction, a fuel fraction and a
		/// temperature -- and reports whether those are simultaneously present. It does NOT model
		/// ignition, flame propagation, pressure rise, or consumption of the reactants, and
		/// nothing in this framework calls it: the point of shipping it now is that the
		/// measurement side is settled and agreed with the rest of the atmosphere model, so
		/// combustion becomes "decide what happens", not "first work out what the air is".
		///
		/// WHAT A REAL MODEL WILL HAVE TO ADD, named here rather than discovered later:
		/// per-fuel upper and lower flammability limits (this uses methane's lower limit for
		/// every fuel), autoignition temperatures, an ignition SOURCE model, and the reaction
		/// itself -- fuel + oxygen consumed, products and heat produced, all of which the
		/// framework's existing conversion-enthalpy machinery already knows how to bill.
		/// </summary>
		public static CombustionRisk EvaluateCombustionRisk(int cell)
		{
			CombustionRisk risk = default(CombustionRisk);
			risk.Cell = cell;
			if (!Grid.IsValidCell(cell))
			{
				return risk;
			}

			risk.TemperatureK = Grid.Temperature[cell];

			ushort oxygenIdx = IndexOf(SimHashes.Oxygen);
			float totalMoles = 0f;
			float oxygenMoles = 0f;
			float fuelMoles = 0f;
			float bestFuelMoles = 0f;

			if (GasMixtureFacade.IsRoomOwned(cell))
			{
				GasMixtureFacade.GasComponent[] composition = GasMixtureFacade.TryGetComposition(cell);
				for (int i = 0; i < composition.Length; i++)
				{
					ushort idx = composition[i].ElementIdx;
					float moles = MolesOf(idx, composition[i].MassKg);
					totalMoles += moles;
					if (idx == oxygenIdx)
					{
						oxygenMoles += moles;
					}
					else if (IsFuel(idx))
					{
						fuelMoles += moles;
						if (moles > bestFuelMoles)
						{
							bestFuelMoles = moles;
							risk.DominantFuelIdx = idx;
						}
					}
				}
			}
			else
			{
				Element element = Grid.Element[cell];
				if (element != null && element.IsGas)
				{
					totalMoles = MolesOf(element.idx, Grid.Mass[cell]);
					if (element.idx == oxygenIdx)
					{
						oxygenMoles = totalMoles;
					}
					else if (IsFuel(element.idx))
					{
						fuelMoles = totalMoles;
						risk.DominantFuelIdx = element.idx;
					}
				}
			}

			if (totalMoles > 0f)
			{
				risk.OxygenMoleFraction = oxygenMoles / totalMoles;
				risk.FuelMoleFraction = fuelMoles / totalMoles;
			}

			risk.IsOxygenEnriched = risk.OxygenMoleFraction >= OxygenEnrichedMoleFraction;
			risk.IsFlammableMixture = risk.FuelMoleFraction >= LowerFlammabilityMoleFraction
				&& risk.OxygenMoleFraction > 0f;
			return risk;
		}

		/// <summary>
		/// Moles in <paramref name="massKg"/> of the species at <paramref name="elementIdx"/>.
		/// Zero for an unknown species or a non-positive mass, which callers sum harmlessly.
		/// </summary>
		private static float MolesOf(ushort elementIdx, float massKg)
		{
			if (massKg <= 0f)
			{
				return 0f;
			}
			float molarMass = MolarMassGPerMol(elementIdx);
			if (molarMass <= 0f)
			{
				return 0f;
			}
			return massKg * 1000f / molarMass;
		}

		/// <summary>
		/// Molar mass in g/mol for a species, in the convention the SIMULATION uses -- real
		/// molecular mass -- rather than the convention ONI's own element table uses.
		///
		/// Public because anything that needs to reason in MOLES has to resolve molar mass the
		/// same way this file does or it will silently disagree with the breathing check. ONI
		/// stores ATOMIC mass for its diatomic gases (Oxygen 15.9994, Hydrogen 1.00794) and real
		/// molecular mass for everything else (CarbonDioxide 44.01), so a mole count taken from
		/// the element table alone is wrong by a factor of two between those two groups.
		/// <see cref="MaterialPropertyRegistry"/> is real throughout and is the same table the
		/// native sim's gas law is handed, so it is consulted first; the element table is only a
		/// fallback for a species the registry does not carry.
		///
		/// See <see cref="TryGetMoleFraction"/> for the same reasoning in context.
		/// </summary>
		public static float MolarMassGPerMol(ushort elementIdx)
		{
			Element element = ElementAt(elementIdx);
			if (element == null)
			{
				return 0f;
			}
			if (MaterialPropertyRegistry.TryGetMolecularMassGPerMol(element.id, out float registryMass)
				&& registryMass > 0f)
			{
				return registryMass;
			}
			return element.molarMass;
		}

		/// <summary>
		/// Whether a species contributes to the oxygen a breather can actually use.
		///
		/// Oxygen itself, plus anything ONI tags <c>Breathable</c> -- which in a stock game means
		/// Polluted Oxygen, a gas duplicants have always breathed perfectly well. Grading purely
		/// on the Oxygen species would have said a room full of Polluted Oxygen contains no
		/// oxygen at all and suffocated everyone in it, which is a regression against vanilla
		/// rather than a stricter physical model.
		///
		/// This is a stopgap with a known end date. Polluted Oxygen is really an oxygen/pollutant
		/// MIXTURE, and converting it into one is already queued (it was blocked on the Pollutant
		/// element and the mixture API, both of which now exist). Once it is a mixture, its
		/// oxygen content is counted by the Oxygen branch on its own merits and this clause stops
		/// mattering -- at which point a partly-spent Polluted Oxygen room will correctly read as
		/// having LESS oxygen than a fresh one, which today it cannot.
		/// </summary>
		private static bool IsOxygenBearing(ushort elementIdx, ushort oxygenIdx)
		{
			if (elementIdx == oxygenIdx)
			{
				return true;
			}
			Element element = ElementAt(elementIdx);
			return element != null && element.IsGas && element.HasTag(GameTags.Breathable);
		}

		/// <summary>
		/// Whether a species counts towards the harmful fraction graded by
		/// <see cref="ContaminantGrade"/>.
		///
		/// The obvious test -- <c>Element.toxicity &gt; 0</c> -- is not enough on its own, because
		/// ONI's own toxicity table is sparser and stranger than it looks: read out of
		/// <c>gas.yaml</c>, the ONLY gases with non-zero toxicity are CarbonDioxide (0.0001) and
		/// ContaminatedOxygen (0.01). Chlorine is zero. So the naive test both DOUBLE-COUNTS
		/// carbon dioxide, which already has its own axis and its own thresholds, and marks
		/// Polluted Oxygen as a suffocant at 7% -- a gas duplicants breathe in vanilla.
		///
		/// Both exclusions are therefore about not inventing harm the simulation cannot justify:
		/// carbon dioxide is graded once, on its own axis, and a gas ONI itself tags
		/// <c>Breathable</c> is not simultaneously a suffocant. Mod 2's Pollutant (toxicity 0.5,
		/// not breathable) is exactly what this is meant to catch, along with any third-party
		/// toxic gas registered the same way.
		/// </summary>
		private static bool IsToxic(ushort elementIdx)
		{
			Element element = ElementAt(elementIdx);
			if (element == null || element.toxicity <= 0f)
			{
				return false;
			}
			if (element.id == SimHashes.CarbonDioxide)
			{
				return false;
			}
			return !element.HasTag(GameTags.Breathable);
		}

		private static bool IsFuel(ushort elementIdx)
		{
			Element element = ElementAt(elementIdx);
			if (element == null)
			{
				return false;
			}
			for (int i = 0; i < FuelSpecies.Length; i++)
			{
				if (element.id == FuelSpecies[i])
				{
					return true;
				}
			}
			return false;
		}

		private static Element ElementAt(ushort elementIdx)
		{
			if (ElementLoader.elements == null || elementIdx >= ElementLoader.elements.Count)
			{
				return null;
			}
			return ElementLoader.elements[elementIdx];
		}

		/// <summary>
		/// Element-table index for a hash, or 0 when the element does not exist in this game build.
		/// Zero is Vacuum, which no composition entry ever names, so an absent species simply never
		/// matches -- the behaviour a caller wants for an element a DLC or another mod supplies.
		/// </summary>
		private static ushort IndexOf(SimHashes id)
		{
			Element element = ElementLoader.FindElementByHash(id);
			return element != null ? element.idx : (ushort)0;
		}
	}
}
