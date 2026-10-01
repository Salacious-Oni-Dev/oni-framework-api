using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// HOW MUCH GAS A LIQUID CAN HOLD, and how far a cell is from holding it. This is the data
	/// half of the dissolved-gas equilibrium: <see cref="DissolvedGas"/>
	/// stores dissolved mass per liquid cell, and this says what that mass would settle at.
	///
	/// HENRY'S LAW, in the form the Sander compilation publishes it: the equilibrium concentration
	/// of a gas in a liquid is proportional to that gas's PARTIAL PRESSURE at the interface,
	///
	///     c_eq [mol/m3] = H(T) * p [Pa]
	///
	/// with H in mol m^-3 Pa^-1 and the temperature dependence of the van 't Hoff form
	///
	///     H(T) = H(298.15 K) * exp( B * (1/T - 1/298.15) ).
	///
	/// B is positive for every gas here, so COLD WATER HOLDS MORE. That is not a detail: it is why
	/// a carbonation showcase has to run cold, why a warming pond effervesces, and why a boiling
	/// one degasses entirely.
	///
	/// WHAT THE NUMBERS ARE. <see cref="HenryMolPerM3Pa"/> is H(298.15 K) and
	/// <see cref="VantHoffK"/> is B, both from the Sander (2015) compilation for WATER. They
	/// reproduce the concentrations the scope document quotes, at 1 atm and 25 C:
	///
	///     CO2  1.47 g/kg     O2  0.042 g/kg     Cl2  7.0 g/kg
	///     CH4  0.023 g/kg    H2  0.0016 g/kg
	///
	/// WHICH LIQUIDS DISSOLVE ANYTHING. Water and the three liquids ONI treats as water with
	/// something in it -- Polluted Water, Salt Water and Brine -- with a solvent factor that
	/// scales the capacity. Every other liquid holds nothing until something registers it, which
	/// is the conservative end of the scope document's open question ("liquids other than water
	/// get no solubility at first, or water's values. That is a decision."): a liquid that holds
	/// nothing cannot silently invent dissolved mass, and a caller who knows better can
	/// <see cref="RegisterSolvent"/>.
	///
	/// HOW MUCH ROOM THE DISSOLVED GAS TAKES UP, the second half of this class. A solution is bigger than the solvent that made it: dissolving 0.25 mol of
	/// CO2 into a kilogram of water adds about 8 cm3, because each mole of dissolved CO2 occupies
	/// a PARTIAL MOLAR VOLUME of roughly 33 cm3 between the water molecules. The solution is also
	/// HEAVIER by the gas's own mass, and the two together make it DENSER than the water was --
	/// which is why soda sinks in water. <see cref="SolutionMassKg"/>,
	/// <see cref="SolutionVolumeM3"/> and <see cref="SolutionDensityKgPerM3"/> are those three
	/// numbers, and <see cref="FreeVolumeM3"/> is what a cell has left over.
	///
	/// THESE ARE DERIVED READS. NOTHING HERE WRITES TO THE GRID, and that is deliberate rather
	/// than lazy. <c>Grid.Mass</c> is the liquid mover's own input: <c>physics.h</c> drives flow
	/// off <c>max(mass * 1.01, maxMass)</c> and calls <c>DisplaceLiquid</c> above
	/// <c>1.5 * maxMass</c>, so a carbonated cell whose mass had been raised to 1011 kg would
	/// read as over-full and the mover would shove the surplus out AS WATER. It would also break
	/// the replacement SimDLL's <c>diffsim</c> degeneracy gate, which requires single-phase cells to match Klei bit for bit and which Klei cannot follow
	/// into a solution. So the expansion is computed on demand by whoever needs it, and the grid
	/// keeps saying what Klei would say.
	///
	/// WHAT THIS CLASS DOES NOT DO. It does not move any mass. It answers questions.
	/// <see cref="Bubbles"/> uses it to decide how much a rising bubble trades with the water it
	/// is in; a sensor uses it to report saturation; <see cref="Effervescence"/> sends its data
	/// to the sim, which exchanges gas at a pond's surface.
	/// </summary>
	public static class Solubility
	{
		/// <summary>
		/// The reference temperature every published Henry constant is quoted at. The stock value
		/// of <see cref="SimTunable.FizzReferenceK"/>, the sim's van 't Hoff reference, which this
		/// class reads live so its saturation readings and the sim's fizz agree.
		/// </summary>
		public const float ReferenceTemperatureK = 298.15f;

		private static float LiveReferenceTemperatureK =>
			SimTunables.GetFloat(SimTunable.FizzReferenceK, ReferenceTemperatureK);

		/// <summary>One gas's solubility in water.</summary>
		public struct Entry
		{
			/// <summary>H(298.15 K), mol m^-3 Pa^-1. Sander's H^cp.</summary>
			public float HenryMolPerM3Pa;

			/// <summary>B in H(T) = H(298.15) * exp(B * (1/T - 1/298.15)), kelvin.</summary>
			public float VantHoffK;
		}

		private static readonly Dictionary<SimHashes, Entry> Gases =
			new Dictionary<SimHashes, Entry>
			{
				// Carbon dioxide. The carbonation case, and by far the most soluble of the
				// ordinary gases: about 1.5 g/kg at 1 atm and 25 C, 2.2 g/kg at 10 C.
				{ SimHashes.CarbonDioxide, new Entry { HenryMolPerM3Pa = 3.3e-4f, VantHoffK = 2400f } },

				// Oxygen. Aeration: the reason a pond can hold fish at all, and two orders of
				// magnitude below CO2.
				{ SimHashes.Oxygen, new Entry { HenryMolPerM3Pa = 1.3e-5f, VantHoffK = 1500f } },

				// Chlorine. The most soluble gas in this table, which is why chlorinating water
				// works at all.
				{ SimHashes.ChlorineGas, new Entry { HenryMolPerM3Pa = 9.7e-4f, VantHoffK = 2500f } },

				// Hydrogen. Nearly insoluble; a hydrogen bubble crossing a pond keeps its mass.
				{ SimHashes.Hydrogen, new Entry { HenryMolPerM3Pa = 7.8e-6f, VantHoffK = 500f } },

				// Methane. Also nearly insoluble, and the reason a natural gas line vented under
				// water bubbles out rather than dissolving.
				{ SimHashes.Methane, new Entry { HenryMolPerM3Pa = 1.4e-5f, VantHoffK = 1600f } },
			};

		private static readonly Dictionary<SimHashes, float> Solvents =
			new Dictionary<SimHashes, float>
			{
				{ SimHashes.Water, 1f },

				// Polluted water is water with organic matter in it. Its gas capacity is water's
				// to within far less than this model's own error, so it takes water's values
				// rather than a number invented to look different.
				{ SimHashes.DirtyWater, 1f },

				// Salt water and brine hold LESS gas than fresh water -- dissolved salt takes up
				// room that dissolved gas would otherwise occupy (the Sechenov, or salting-out,
				// effect). Seawater at 35 g/kg salinity holds about 88% of fresh water's CO2;
				// ONI's Brine is roughly twice as salty again, so it gets 0.78. Both numbers are
				// the Sechenov relation evaluated at those salinities, not measurements of ONI.
				{ SimHashes.SaltWater, 0.88f },
				{ SimHashes.Brine, 0.78f },
			};

		// PARTIAL MOLAR VOLUMES of these gases DISSOLVED IN WATER, cubic metres per mole, at
		// about 298 K and the pressures a vessel on this grid reaches. This is the volume a mole
		// of dissolved gas adds to the solution -- NOT the molar volume of the gas, and NOT the
		// molar volume of its liquid phase, both of which are much larger. The literature values
		// (Moore/Battino's tabulation of standard partial molar volumes of gases in water) vary
		// by a few percent over the 273-320 K band this model works in, which is well inside the
		// error of the Henry constants above, so they are stored as constants exactly as
		// Stationeers stores its own per-species molar volumes.
		//
		// A gas with no entry here adds NO volume. That is the conservative end: an unregistered
		// gas cannot silently inflate a pond. A caller who knows better calls
		// `RegisterPartialMolarVolume`.
		private static readonly Dictionary<SimHashes, float> PartialMolarVolumes =
			new Dictionary<SimHashes, float>
			{
				// 33 cm3/mol. The carbonation case: 11 g/kg of CO2 -- soda strength, and about
				// what a 4.6 bar headspace supports at 7 C -- expands the water by 0.82%.
				{ SimHashes.CarbonDioxide, 33e-6f },
				{ SimHashes.Oxygen, 31e-6f },
				{ SimHashes.ChlorineGas, 42e-6f },
				// The smallest of them, and the least soluble, so it never moves a pond.
				{ SimHashes.Hydrogen, 25e-6f },
				{ SimHashes.Methane, 37e-6f },
			};

		// The per-tick memo for `ReadDissolved`. A column walk re-reads the same cells for every
		// bubble above them, and each lane read is a P/Invoke, so the answer is cached for the
		// tick it was read on. Correct by construction rather than by tolerance: `DissolvedGas.Add`
		// is QUEUED and lands on the sim's next tick, so a cell's dissolved record genuinely does
		// not change within one tick.
		private static readonly Dictionary<int, DissolvedReading> dissolvedMemo =
			new Dictionary<int, DissolvedReading>();
		// BOTH stamps, and the memo is dropped when EITHER moves. `SimExtFrame.TickId` is the
		// right clock -- it is the tick a queued `DissolvedGas.Add` lands on -- but it only
		// advances while a frame is being bound, and `DissolvedGas.Available` does not depend on
		// that, so on its own it could sit still forever and serve a stale answer for the rest of
		// the session. `Time.frameCount` alone is not enough either: one render frame can span
		// several 33 ms sim ticks on a slow machine. Together neither failure is reachable.
		private static long dissolvedMemoTick = long.MinValue;
		private static int dissolvedMemoFrame = int.MinValue;

		private struct DissolvedReading
		{
			public float MassKg;
			public float VolumeM3;
		}

		/// <summary>
		/// Adds or replaces one gas's Henry constants. For Mod 2's material property work, and for
		/// any mod adding a gas of its own; a gas with no entry dissolves in nothing.
		/// </summary>
		public static void RegisterGas(SimHashes gas, float henryMolPerM3Pa, float vantHoffK)
		{
			if (gas == (SimHashes)0)
			{
				throw new ArgumentException("not a gas", "gas");
			}
			Gases[gas] = new Entry
			{
				HenryMolPerM3Pa = Mathf.Max(henryMolPerM3Pa, 0f),
				VantHoffK = vantHoffK,
			};
			Effervescence.RefreshIfEnabled();
		}

		/// <summary>
		/// Adds or replaces one liquid's solvent factor: its gas capacity as a multiple of fresh
		/// water's. Zero means it dissolves nothing, which is every liquid this class has not
		/// been told about.
		/// </summary>
		public static void RegisterSolvent(SimHashes liquid, float factor)
		{
			if (liquid == (SimHashes)0)
			{
				throw new ArgumentException("not a liquid", "liquid");
			}
			Solvents[liquid] = Mathf.Max(factor, 0f);
			Effervescence.RefreshIfEnabled();
		}

		/// <summary>One gas's constants, false if it has none.</summary>
		// Every liquid with a nonzero factor, for Effervescence to push to the sim.
		internal static IEnumerable<KeyValuePair<SimHashes, float>> SolventTable()
		{
			foreach (KeyValuePair<SimHashes, float> kv in Solvents)
			{
				if (kv.Value > 0f)
				{
					yield return kv;
				}
			}
		}

		public static bool TryGetGas(SimHashes gas, out Entry entry)
		{
			return Gases.TryGetValue(gas, out entry);
		}

		/// <summary>
		/// How much gas <paramref name="liquid"/> holds as a multiple of fresh water's capacity;
		/// zero for a liquid that dissolves nothing, and for anything that is not a liquid.
		/// </summary>
		public static float SolventFactor(SimHashes liquid)
		{
			float factor;
			return Solvents.TryGetValue(liquid, out factor) ? factor : 0f;
		}

		/// <summary>True if <paramref name="liquid"/> can hold dissolved gas at all.</summary>
		public static bool IsSolvent(SimHashes liquid)
		{
			return SolventFactor(liquid) > 0f;
		}

		/// <summary>
		/// Adds or replaces one gas's PARTIAL MOLAR VOLUME in solution, cubic metres per mole:
		/// how much room a mole of it takes up once dissolved. Zero means it takes up none, which
		/// is every gas this class has not been told about. For Mod 2's material property work
		/// and for any mod adding a gas of its own.
		/// </summary>
		public static void RegisterPartialMolarVolume(SimHashes gas, float m3PerMol)
		{
			if (gas == (SimHashes)0)
			{
				throw new ArgumentException("not a gas", "gas");
			}
			PartialMolarVolumes[gas] = Mathf.Max(m3PerMol, 0f);
			dissolvedMemoTick = long.MinValue;
			dissolvedMemoFrame = int.MinValue;
			Effervescence.RefreshIfEnabled();
		}

		/// <summary>
		/// The volume one mole of dissolved <paramref name="gas"/> adds to the solution holding
		/// it, cubic metres per mole; zero for a gas with no entry.
		/// </summary>
		public static float PartialMolarVolumeM3PerMol(SimHashes gas)
		{
			float m3PerMol;
			return PartialMolarVolumes.TryGetValue(gas, out m3PerMol) ? m3PerMol : 0f;
		}

		/// <summary>
		/// Kilograms of gas dissolved in <paramref name="cell"/>, every lane summed. Zero for a
		/// cell that is not liquid and on a SimDLL without the dissolved-mass property.
		/// </summary>
		public static float DissolvedMassKg(int cell)
		{
			DissolvedReading reading = ReadDissolved(cell);
			return reading.MassKg;
		}

		/// <summary>
		/// The volume that dissolved gas ADDS to <paramref name="cell"/>'s liquid, cubic metres:
		/// each lane's moles times its <see cref="PartialMolarVolumeM3PerMol"/>. Zero for a cell
		/// that is not liquid, and for one whose dissolved gases have no registered volume.
		/// </summary>
		public static float DissolvedVolumeM3(int cell)
		{
			DissolvedReading reading = ReadDissolved(cell);
			return reading.VolumeM3;
		}

		/// <summary>
		/// Everything <paramref name="cell"/> actually holds, in kilograms: the liquid the grid
		/// knows about PLUS the gas dissolved in it. This is the mass that presses down on what
		/// is below it, and it is what <see cref="Bubbles.PressureAtPa"/> sums a column of.
		/// <c>Grid.Mass</c> alone understates it by the dissolved fraction, which is about 1% at
		/// soda strength.
		/// </summary>
		public static float SolutionMassKg(int cell)
		{
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return 0f;
			}
			float massKg = Grid.Mass[cell];
			return massKg > 0f ? massKg + DissolvedMassKg(cell) : 0f;
		}

		/// <summary>
		/// The volume <paramref name="cell"/>'s solution occupies, cubic metres: the liquid's own
		/// volume (its mass over its pure density) plus <see cref="DissolvedVolumeM3"/>. A cell
		/// holding 500 kg of water occupies half a tile; a FULL cell carbonated to soda strength
		/// occupies slightly MORE than one, and that overfill is real -- it is what squeezes the
		/// gas above it (<see cref="FreeVolumeM3"/>).
		/// </summary>
		public static float SolutionVolumeM3(int cell)
		{
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return 0f;
			}
			Element element = Grid.Element[cell];
			float massKg = Grid.Mass[cell];
			if (element == null || !(massKg > 0f))
			{
				return 0f;
			}
			float rho = Bubbles.LiquidDensityKgPerM3(element);
			float volumeM3 = rho > 0f ? massKg / rho : 0f;
			return volumeM3 + DissolvedVolumeM3(cell);
		}

		/// <summary>
		/// The density of what is in <paramref name="cell"/>, kg/m3:
		/// <see cref="SolutionMassKg"/> over <see cref="SolutionVolumeM3"/>. Carbonated water is
		/// DENSER than plain water -- the gas adds 1.1% of the mass and 0.82% of the volume at
		/// soda strength, so the density rises about 0.27% -- which is why a bubble climbing
		/// through it rises a little faster. Falls back to the liquid's pure density for a cell
		/// holding nothing dissolved, and returns zero for a cell that is not liquid.
		/// </summary>
		public static float SolutionDensityKgPerM3(int cell)
		{
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return 0f;
			}
			float volumeM3 = SolutionVolumeM3(cell);
			if (!(volumeM3 > 0f))
			{
				Element fallback = Grid.Element[cell];
				return fallback != null ? Bubbles.LiquidDensityKgPerM3(fallback) : 0f;
			}
			return SolutionMassKg(cell) / volumeM3;
		}

		/// <summary>
		/// How much of <paramref name="cell"/>'s cubic metre its solution does NOT occupy.
		/// POSITIVE for a partly filled cell -- 0.5 for a cell holding 500 kg of water -- and
		/// NEGATIVE for a full cell whose dissolved gas has expanded it past a tile. It is
		/// deliberately not clamped at zero: the negative case is the whole point, because that
		/// overfill is volume the gas above has lost.
		///
		/// A cell that is not liquid has all of its volume free, which is what lets a caller sum
		/// this over a region without testing each cell.
		/// </summary>
		public static float FreeVolumeM3(int cell)
		{
			if (!Grid.IsValidCell(cell))
			{
				return 0f;
			}
			if (!Grid.IsLiquid(cell))
			{
				return Grid.Solid[cell] ? 0f : PipeNetworkFacade.LiveCellVolumeM3;
			}
			return PipeNetworkFacade.LiveCellVolumeM3 - SolutionVolumeM3(cell);
		}

		// The dissolved half of a cell, mass and added volume in one pass over the lanes, memoed
		// for the tick. Only lanes with an assigned element are read.
		private static DissolvedReading ReadDissolved(int cell)
		{
			DissolvedReading reading = default(DissolvedReading);
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell) || !DissolvedGas.Available)
			{
				return reading;
			}
			long tick = SimExtFrame.Available ? SimExtFrame.TickId : 0L;
			int frame = Time.frameCount;
			if (tick != dissolvedMemoTick || frame != dissolvedMemoFrame)
			{
				dissolvedMemo.Clear();
				dissolvedMemoTick = tick;
				dissolvedMemoFrame = frame;
			}
			else if (dissolvedMemo.TryGetValue(cell, out reading))
			{
				return reading;
			}
			for (int lane = 0; lane < DissolvedGas.Lanes; lane++)
			{
				SimHashes gas = DissolvedGas.LaneElement(lane);
				if (gas == (SimHashes)0)
				{
					continue;
				}
				float kg = DissolvedGas.ReadLane(cell, lane);
				if (!(kg > 0f))
				{
					continue;
				}
				reading.MassKg += kg;
				float m3PerMol = PartialMolarVolumeM3PerMol(gas);
				if (!(m3PerMol > 0f))
				{
					continue;
				}
				float molarMassKgPerMol = AtmosphereFacade.MolarMassGPerMol(
					(ushort)ElementLoader.GetElementIndex(gas)) * 0.001f;
				if (molarMassKgPerMol > 0f)
				{
					reading.VolumeM3 += kg / molarMassKgPerMol * m3PerMol;
				}
			}
			dissolvedMemo[cell] = reading;
			return reading;
		}

		/// <summary>
		/// H(T) for <paramref name="gas"/> in <paramref name="liquid"/>, mol m^-3 Pa^-1, with the
		/// solvent factor already applied. Zero if either side has no entry.
		/// </summary>
		public static float HenryMolPerM3Pa(SimHashes gas, SimHashes liquid, float temperatureK)
		{
			Entry entry;
			if (!Gases.TryGetValue(gas, out entry) || entry.HenryMolPerM3Pa <= 0f)
			{
				return 0f;
			}
			float factor = SolventFactor(liquid);
			if (factor <= 0f || !(temperatureK > 0f))
			{
				return 0f;
			}
			float scaled = entry.HenryMolPerM3Pa * factor;
			if (entry.VantHoffK == 0f)
			{
				return scaled;
			}
			return scaled * Mathf.Exp(entry.VantHoffK
				* (1f / temperatureK - 1f / LiveReferenceTemperatureK));
		}

		/// <summary>
		/// The equilibrium concentration of <paramref name="gas"/> in <paramref name="liquid"/>,
		/// as KILOGRAMS OF GAS PER KILOGRAM OF LIQUID, at <paramref name="partialPressurePa"/>.
		///
		/// <paramref name="liquidDensityKgPerM3"/> converts Henry's volumetric answer into the
		/// per-mass one every caller here actually wants: a cell's dissolved record is a mass and
		/// the liquid it rides is a mass. Pass the cell's own mass, which for ONI's one-cubic-metre
		/// cells IS its density.
		/// </summary>
		public static float EquilibriumKgPerKg(SimHashes gas, SimHashes liquid, float temperatureK,
			float partialPressurePa, float liquidDensityKgPerM3)
		{
			if (!(partialPressurePa > 0f) || !(liquidDensityKgPerM3 > 0f))
			{
				return 0f;
			}
			float henry = HenryMolPerM3Pa(gas, liquid, temperatureK);
			if (henry <= 0f)
			{
				return 0f;
			}
			float molarMassGPerMol = AtmosphereFacade.MolarMassGPerMol(
				(ushort)ElementLoader.GetElementIndex(gas));
			if (!(molarMassGPerMol > 0f))
			{
				return 0f;
			}
			// mol/m3 * kg/mol = kg/m3, over the liquid's own kg/m3 = kg gas per kg liquid.
			return henry * partialPressurePa * molarMassGPerMol * 0.001f / liquidDensityKgPerM3;
		}

		/// <summary>
		/// What one liquid CELL would hold at equilibrium, in kilograms, given the partial
		/// pressure of <paramref name="gas"/> at it. Zero for a cell that is not liquid, or whose
		/// liquid dissolves nothing.
		///
		/// HENRY'S LAW IS PER UNIT VOLUME, so this is <c>H(T) * p * M</c> over the volume the
		/// cell's solution actually occupies (<see cref="SolutionVolumeM3"/>) -- NOT over a whole
		/// tile. Passing the cell's mass in as its density and multiplying by that same mass
		/// cancels, and tells a cell holding ONE KILOGRAM of water that it can hold as much
		/// dissolved gas as a full one -- a factor of a thousand at the shallow edge of a pond.
		/// </summary>
		public static float EquilibriumKg(int cell, SimHashes gas, float partialPressurePa)
		{
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell) || !(partialPressurePa > 0f))
			{
				return 0f;
			}
			Element element = Grid.Element[cell];
			float massKg = Grid.Mass[cell];
			if (element == null || !(massKg > 0f))
			{
				return 0f;
			}
			float henry = HenryMolPerM3Pa(gas, element.id, Grid.Temperature[cell]);
			if (!(henry > 0f))
			{
				return 0f;
			}
			float molarMassGPerMol = AtmosphereFacade.MolarMassGPerMol(
				(ushort)ElementLoader.GetElementIndex(gas));
			if (!(molarMassGPerMol > 0f))
			{
				return 0f;
			}
			// mol/(m3 Pa) * Pa * kg/mol * m3 = kg.
			return henry * partialPressurePa * molarMassGPerMol * 0.001f * SolutionVolumeM3(cell);
		}

		/// <summary>
		/// Kilograms of <paramref name="gas"/> dissolved per kilogram of liquid at
		/// <paramref name="cell"/>. Takes the sim's barrier, the same as
		/// <see cref="DissolvedGas.Read"/>.
		/// </summary>
		public static float ConcentrationKgPerKg(int cell, SimHashes gas)
		{
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return 0f;
			}
			float massKg = Grid.Mass[cell];
			return massKg > 0f ? DissolvedGas.Read(cell, gas) / massKg : 0f;
		}

		/// <summary>
		/// The same, in the unit a carbonation reads in: GRAMS PER KILOGRAM. Soda is about
		/// 8 g/kg; water in equilibrium with ordinary air is about 0.0005.
		/// </summary>
		public static float ConcentrationGramsPerKg(int cell, SimHashes gas)
		{
			return ConcentrationKgPerKg(cell, gas) * 1000f;
		}

		/// <summary>
		/// How saturated <paramref name="cell"/> is with <paramref name="gas"/>: 1 means it holds
		/// exactly what the pressure at that cell supports, above 1 is supersaturated (which is
		/// what fizzes when the pressure drops), and 0 means either empty or a pressure of zero.
		///
		/// THE PRESSURE USED IS THE LOCAL ONE -- <see cref="Bubbles.PressureAtPa"/>, the gas above
		/// the surface plus the water column -- taken as though the gas in question held all of
		/// it. For a pond being carbonated with pure CO2 that is exactly right. For a pond under
		/// ordinary air it OVERSTATES the pressure available to any one gas, and the honest
		/// reading there is against that gas's own partial pressure, which is
		/// <see cref="EquilibriumKg"/> with a partial pressure the caller computes.
		/// </summary>
		public static float SaturationFraction(int cell, SimHashes gas)
		{
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return 0f;
			}
			float pressurePa = Bubbles.PressureAtPa(Grid.CellToPosCCC(cell, Grid.SceneLayer.Front));
			float equilibriumKg = EquilibriumKg(cell, gas, pressurePa);
			if (!(equilibriumKg > 0f))
			{
				return 0f;
			}
			return DissolvedGas.Read(cell, gas) / equilibriumKg;
		}
	}
}
