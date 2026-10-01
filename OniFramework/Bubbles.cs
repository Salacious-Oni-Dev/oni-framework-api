using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// BUBBLE PHYSICS for vanilla's <c>BubbleManager</c>: rise speed from the bubble's real size,
	/// expansion as the water above it thins, and heat exchange with the liquid it passes
	/// through.
	///
	/// WHAT VANILLA DOES. Every bubble in the game goes through
	/// <c>BubbleManager.SpawnBubble</c>: a breath under water, a converter's output under liquid,
	/// Flatulence, a boiling Tepidizer, a pufferfish, the DLC5 vent. <c>Sim33ms</c> then moves each
	/// bubble at its archetype's velocity, and no caller ever passes one, so every bubble rises at
	/// <c>DEFAULT_VELOCITY</c>: one tile a second whatever it is made of and however large it is.
	/// A bubble's temperature is frozen at spawn. It pops into the gas above the liquid through
	/// <c>AddRemoveSubstance</c>.
	///
	/// WHAT THIS CHANGES, as one prefix on <c>Sim33ms</c>. Nothing is replaced: vanilla still
	/// moves, fades, pops and deposits every bubble. The prefix runs first and, for each bubble
	/// rising at the default velocity through a liquid that is not its own element:
	/// <list type="bullet">
	/// <item><b>Heat.</b> The bubble relaxes toward the temperature it would share with its
	/// liquid cell, with a time constant set by its size. The heat it gains is taken from that
	/// cell with <c>SimMessages.ModifyEnergy</c>, so none is created or deleted. See
	/// <see cref="HeatTransferCoefficientWPerM2K"/>.</item>
	/// <item><b>Size.</b> Volume from the ideal gas law at the local pressure, which is the gas
	/// pressure at the liquid's surface plus the weight of every kilogram of liquid above the
	/// bubble (<see cref="PressureAtPa"/>). A bubble grows as it climbs.</item>
	/// <item><b>Speed.</b> Terminal velocity from that size (<see cref="RiseVelocityMPerS"/>).
	/// The prefix moves the bubble by the difference between that speed and vanilla's one tile a
	/// second, and vanilla's own step then adds the one tile a second back. It also scales the
	/// fade-out to the same speed, because vanilla fades a bubble over the one tile below the
	/// surface on the assumption that one tile takes one second.</item>
	/// <item><b>Render size.</b> The size level is set from volume, not mass. The thresholds are
	/// vanilla's 0.1 and 0.3 kg of carbon dioxide at one atmosphere and 20 °C, so a CO2 bubble at
	/// the surface draws as it always did, and a hydrogen bubble of the same mass draws larger.</item>
	/// </list>
	///
	/// WHAT IS LEFT ALONE. A bucket a caller gave its own velocity keeps it. Nothing is added to
	/// the save, because every quantity used is one vanilla already stores per bubble (position,
	/// mass, temperature, size level, alpha). Removing the mod leaves ordinary vanilla bubbles.
	///
	/// A DROPLET DENSER THAN ITS HOST LIQUID (vanilla's lubricant, in a lighter liquid) has no
	/// rise speed. It is released into the sim at once, and the sim's own liquid displacement
	/// already sorts liquids by density.
	///
	/// THE PRESSURE MODEL:
	/// <list type="bullet">
	/// <item>the liquid column is weighed as a SOLUTION (<see cref="Solubility.SolutionMassKg"/>),
	/// so gas dissolved in the water above a bubble presses down on it;</item>
	/// <item>a gas cell's pressure is taken over the volume the gas HAS
	/// (<see cref="GasVolumeM3"/>), which includes whatever of the tile below the liquid has left
	/// empty -- so surface pressure varies CONTINUOUSLY with the water level instead of stepping
	/// when a cell tips over, and a carbonated cell that has expanded past a tile squeezes the
	/// headspace above it;</item>
	/// <item>a bubble's buoyancy is against the solution's density
	/// (<see cref="Solubility.SolutionDensityKgPerM3"/>), not the pure liquid's.</item>
	/// </list>
	///
	/// WHAT IS NOT MODELLED, on purpose and for now. The P·dV work a growing bubble does on the
	/// liquid, which is a few joules per bubble. The in-flight bubble's heat, like its mass, is
	/// invisible to the native energy ledger until it pops. <see cref="ExchangedKJ"/> counts it
	/// on this side.
	///
	/// Managed only. No SimDLL change is needed, and this works on a stock DLL.
	/// </summary>
	public static class Bubbles
	{
		// ------------------------------------------------------------------ constants

		/// <summary>Standard gravity, m/s². The stock value of <see cref="SimTunable.StandardGravity"/>,
		/// which this class reads live.</summary>
		public const float GravityMPerS2 = 9.80665f;

		// THE SIM'S LIVE VALUES for the rows this class mirrors. Every constant above and below that
		// has a SimTunable row is that row's STOCK value, kept as the fallback for a SimDLL without
		// the table; the arithmetic here reads these, so a tuned sim and these readings stay one
		// rule (SimTunables; the sim's FizzSurfacePressure and FizzPooledPressure read the same
		// rows). SizeLevelVolumesM3 alone keeps the stock constant: it converts Klei's bubble
		// sprite thresholds, which no sim row moves.
		private static float LiveGravity =>
			SimTunables.GetFloat(SimTunable.StandardGravity, GravityMPerS2);
		private static float LiveGasConstant =>
			SimTunables.GetFloat(SimTunable.GasConstantR, GasConstant);
		private static float LiveMinimumGasVolumeM3 =>
			SimTunables.GetFloat(SimTunable.FizzMinimumGasVolumeM3, MinimumGasVolumeM3);
		private static int LiveSurfaceColumnCells =>
			SimTunables.GetInt(SimTunable.FizzSurfaceColumnCells, SurfaceColumnCells);
		private static float LiveFallbackSolventDensityKgM3 =>
			SimTunables.GetFloat(SimTunable.FizzFallbackSolventDensityKgM3, 1000f);

		/// <summary>
		/// The gas constant, J/(mol·K). The same value Stationeers' <c>AtmosphereHelper</c> and
		/// this project's gas mixture use. The stock value of <see cref="SimTunable.GasConstantR"/>,
		/// which this class reads live.
		/// </summary>
		public const float GasConstant = 8.3144f;

		/// <summary>
		/// The floor under <see cref="GasVolumeM3"/>, cubic metres. A liquid cell expanded past a
		/// full tile eats into the gas volume above it, and without a floor a sufficiently
		/// carbonated pond would divide by zero and hand back an infinite pressure.
		///
		/// This is Stationeers' <c>Chemistry.MinimumGasVolume</c>, 0.1 litres, in cubic metres --
		/// their floor under <c>Atmosphere.GetGasVolume()</c>, kept at its own value rather than
		/// rescaled, because it serves the same purpose against the same arithmetic. At this
		/// volume the pressure is ten thousand times the one-tile reading, which is far past
		/// anything a vessel reaches: the floor is a guard, not a regime.
		///
		/// The stock value of <see cref="SimTunable.FizzMinimumGasVolumeM3"/>, which this class
		/// reads live.
		/// </summary>
		public const float MinimumGasVolumeM3 = 1e-4f;

		/// <summary>
		/// The largest bubble diameter the speed model will use, in metres: one tile. A spherical
		/// cap bubble wider than the cell it rises through cannot be drawn on this grid, and near
		/// a vacuum surface the ideal gas law would otherwise grow it without bound. At this size
		/// Davies-Taylor gives about 2.2 m/s, which is the fastest any bubble rises.
		/// </summary>
		public const float MaxDiameterM = 1f;

		/// <summary>
		/// The pressure floor used in the gas law, Pa. A numerical guard for a bubble just
		/// under a liquid surface that is exposed to vacuum; <see cref="MaxDiameterM"/> is what
		/// actually bounds the result.
		/// </summary>
		public const float MinPressurePa = 100f;

		/// <summary>
		/// Surface tension of the host liquid, N/m. Water's value (0.072), used for every liquid,
		/// because no ONI element carries a surface tension. It only matters for bubbles under
		/// about 2 cm across, which is under about 10 mg of CO2; every vanilla source spawns
		/// grams or more. Per-liquid values are not modelled.
		/// </summary>
		public static float SurfaceTensionNPerM = 0.072f;

		/// <summary>
		/// Dynamic viscosity of the host liquid, Pa·s. Water's value (1.0e-3), used for every
		/// liquid for the same reason as <see cref="SurfaceTensionNPerM"/>. ONI's
		/// <c>Element.viscosity</c> is a flow-rate tuning, not a physical unit, so it cannot be
		/// used here. Only bubbles under about half a millimetre are in the Stokes regime at all.
		/// </summary>
		public static float LiquidViscosityPaS = 1.0e-3f;

		/// <summary>
		/// The bubble-to-liquid heat transfer coefficient, W/(m²·K). 100 is Stationeers' own flat
		/// convection coefficient (<c>AtmosphereHelper.GetConvectionHeat</c>: <c>100 * area * ΔT</c>),
		/// taken as is. With it the time constant is <c>ρ_g·c_g·d / (6h)</c>, about a second for
		/// a 20 g CO2 breath bubble, so a bubble reaches the water's temperature within a tile or
		/// two of climbing.
		/// </summary>
		public static float HeatTransferCoefficientWPerM2K = 100f;

		/// <summary>
		/// The bubble-to-liquid MASS transfer coefficient k_L, m/s. A rising
		/// bubble does not only trade heat with the water it climbs through, it trades matter.
		/// Gas crosses the interface toward Henry's-law equilibrium (<see cref="Solubility"/>),
		/// so a bubble in undersaturated water SHRINKS -- that is dissolution, and it is how a
		/// pond gets carbonated -- and a bubble crossing supersaturated water GROWS, which is
		/// sparging: bubble an insoluble gas through loaded water and it carries the dissolved
		/// load out with it.
		///
		/// 1e-4 m/s is the standard order for a gas bubble rising freely in water (Sherwood
		/// correlations put a millimetre-scale bubble at 1-4e-4; larger, wobbling bubbles sit
		/// lower per unit area). The scope document's own worked case uses the same number: a
		/// 27 cm CO2 breath bubble crossing four tiles in 3.5 s loses about 1% of its mass, so a
		/// single breath still surfaces, and it takes a deliberate plume of small bubbles to
		/// carbonate anything. Set it to zero to switch the exchange off entirely.
		/// </summary>
		public static float MassTransferCoefficientMPerS = 1.0e-4f;

		/// <summary>
		/// THE DIAMETER THE MASS EXCHANGE TREATS A BUBBLE'S GAS AS BEING DISPERSED INTO, metres.
		/// Zero -- the default -- uses the bubble's own geometric sphere, which is what it is
		/// drawn as.
		///
		/// WHY A SECOND DIAMETER EXISTS. `BubbleManager` draws and moves a handful of large
		/// bubbles: `MaxSplitBubbles` is 16, so a vent releasing 100 g makes sixteen spheres
		/// about 12 cm across. Real gas released through a sparger does not do that. It breaks
		/// into millimetre bubbles, and the whole point of a sparger is that it does, because
		/// for a FIXED MASS of gas the interfacial area goes as 1/d:
		///
		///     a = 6 m / (rho * d)
		///
		/// so the same 100 g dispersed at 3 mm instead of 12 cm has forty times the surface to
		/// dissolve through. Measured, first CARBONATION run: with the geometric
		/// diameter the whole three-vent plume carried about 0.45 m2 of interface and moved
		/// 2.2 g in 6 s, which would carbonate the rig's 28 tonnes of water in about a week of
		/// simulated time. That is not the physics being wrong, it is the RENDERING being
		/// coarse, and paying for it by drawing ten thousand bubbles at 33 ms is not available.
		///
		/// So the visual model and the transfer model are allowed to disagree about size, and
		/// only here: rise speed, growth, heat and what is released at the surface all still use
		/// the real sphere. A caller that sets this is saying "the gas my source releases is
		/// dispersed this finely", which is a property of the source, not of the renderer.
		/// </summary>
		public static float InterfacialDiameterM;

		/// <summary>
		/// The two render size thresholds, as volumes in m³: vanilla's
		/// <c>InstanceData.MassTresholds</c> of 0.1 and 0.3 kg, converted to the volume those
		/// masses of carbon dioxide take at 101325 Pa and 293.15 K.
		/// </summary>
		public static readonly float[] SizeLevelVolumesM3 =
		{
			ReferenceCarbonDioxideVolume(0.1f),
			ReferenceCarbonDioxideVolume(0.3f),
		};

		private static float ReferenceCarbonDioxideVolume(float kg)
		{
			return kg * GasConstant * 293.15f / (101325f * 0.04401f);
		}

		/// <summary>
		/// The energy source id the heat exchange is sent under. Vanilla has no bubble id; the
		/// Conduit Temperature Manager id is the closest one, since that is also heat exchanged
		/// between a moving parcel of fluid and a grid cell. The replacement SimDLL only carries the
		/// id (<c>ApplyCellEnergy</c> never reads it); a real vanilla value is used rather than a
		/// new one so that the game's own library receives an id it knows.
		/// </summary>
		private const SimMessages.EnergySourceID ExchangeSourceId =
			SimMessages.EnergySourceID.ConduitTemperatureManager;

		/// <summary>The ceiling passed with each exchange: the sim's own maximum, so it never
		/// refuses heat on temperature grounds.</summary>
		private const float ExchangeMaxTemperatureK = 10000f;

		// ------------------------------------------------------------------ state

		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		/// <summary>
		/// Heat moved from liquid cells into bubbles since load, kJ. Negative when the bubbles
		/// were the hotter side. Each kilojoule here was also removed from, or added to, a
		/// liquid cell through <c>ModifyEnergy</c>, so the two sides sum to zero.
		/// </summary>
		public static double ExchangedKJ { get; private set; }

		/// <summary>Bubble updates this class has governed since load. Zero while bubbles are
		/// visibly rising means the patch applied and is never reached.</summary>
		public static long GovernedUpdates { get; private set; }

		/// <summary>Dense droplets handed straight to the sim because they could not rise.</summary>
		public static long SunkDroplets { get; private set; }

		/// <summary>
		/// Mass that left bubbles and entered the water as dissolved gas since load, kg. Every
		/// kilogram here was taken off a bubble's own mass, so it is not released again when that
		/// bubble surfaces: the two sides are one transfer.
		/// </summary>
		public static double DissolvedKg { get; private set; }

		/// <summary>
		/// Mass that left the water and entered bubbles since load, kg -- sparging, and the
		/// bubble half of effervescence. The mirror of <see cref="DissolvedKg"/>.
		/// </summary>
		public static double StrippedKg { get; private set; }

		/// <summary>
		/// Net enthalpy the bubbles gave the water AS MASS since load, kJ, counted above 0 K:
		/// each dissolving kilogram at the bubble's temperature, less each stripped kilogram at
		/// the water's (which is the temperature it joins the bubble at). With
		/// <see cref="ExchangedKJ"/> it closes a bubble's books: what a bubble brought in is what
		/// it popped with, less the heat it gained (<see cref="ExchangedKJ"/>), plus this.
		/// </summary>
		public static double DissolvedEnthalpyKJ { get; private set; }

		/// <summary>
		/// The part of <see cref="DissolvedEnthalpyKJ"/> paid to water cells as HEAT since load,
		/// kJ: a dissolving mass's <c>m c (t_bubble - T_cell)</c>, sent through
		/// <c>ModifyEnergy</c> with the heat exchange's own payments. The rest,
		/// <c>m c T_cell</c>, stays with the dissolved gas at its water's temperature -- a
		/// dissolved lane carries no heat of its own, so it holds the gas at the cell's.
		/// </summary>
		public static double DissolutionHeatKJ { get; private set; }

		/// <summary>
		/// Exchanges the sim would not take, because <c>DissolvedGas.Add</c> refused the cell.
		/// Non-zero means mass was NOT moved; the bubble keeps it, so nothing is lost.
		/// </summary>
		public static long ExchangeRefusals { get; private set; }

		/// <summary>Reset the running totals.</summary>
		public static void ResetAccounting()
		{
			ExchangedKJ = 0.0;
			GovernedUpdates = 0;
			SunkDroplets = 0;
			DissolvedKg = 0.0;
			StrippedKg = 0.0;
			DissolvedEnthalpyKJ = 0.0;
			DissolutionHeatKJ = 0.0;
			ExchangeRefusals = 0;
		}

		/// <summary>
		/// Raised once for every bubble <c>BubbleManager</c> released into the sim this tick,
		/// with its state at the moment it popped. The mass and temperature are what vanilla's
		/// <c>AddRemoveSubstance</c> was handed. Only tracked while something is subscribed.
		/// </summary>
		public static event Action<BubbleState> Popped;

		/// <summary>One bubble, as read from <c>BubbleManager</c>.</summary>
		public struct BubbleState
		{
			public SimHashes Element;
			public int WorldIdx;
			public Vector2 Position;
			public float MassKg;
			public float TemperatureK;
			/// <summary>The render size level, 0 to 2. Set from volume while this class governs
			/// the bubble, from mass by vanilla otherwise.</summary>
			public byte SizeLevel;
		}

		// ------------------------------------------------------------------ reflection

		private static readonly FieldInfo BubblesField =
			AccessTools.Field(typeof(BubbleManager), "bubbles");
		private static readonly FieldInfo ArchetypesField =
			AccessTools.Field(typeof(BubbleManager), "archetypes");
		private static readonly Type InstanceDataType =
			AccessTools.Inner(typeof(BubbleManager), "InstanceData");
		private static readonly Type ArchetypeType =
			AccessTools.Inner(typeof(BubbleManager), "Archetype");
		private static readonly Type WorldArchetypeType =
			AccessTools.Inner(typeof(BubbleManager), "WorldArchetype");

		/// <summary>
		/// One vanilla bucket's per-bubble lists, read once and kept for the bucket's lifetime.
		/// The lists are <c>readonly</c> fields of a bucket that is never replaced, and a
		/// bucket's archetype never changes, so none of this goes stale.
		/// </summary>
		private sealed class Bucket
		{
			public SimHashes Element;
			public int WorldIdx;
			public bool Governed;
			public List<Vector2> Position;
			public List<float> Temperature;
			public List<float> Mass;
			public List<byte> SizeLevel;
			public List<float> Alpha;
			public List<int> Frame;
		}

		private static readonly ConditionalWeakTable<object, Bucket> Buckets =
			new ConditionalWeakTable<object, Bucket>();

		private static Bucket BucketOf(object worldArchetype, object instanceData,
			IDictionary archetypes)
		{
			if (Buckets.TryGetValue(instanceData, out Bucket bucket))
			{
				return bucket;
			}
			object archetypeId = AccessTools.Field(WorldArchetypeType, "archetype")
				.GetValue(worldArchetype);
			if (archetypeId == null || !archetypes.Contains(archetypeId))
			{
				return null;
			}
			object archetype = archetypes[archetypeId];
			Vector2 velocity = (Vector2)AccessTools.Field(ArchetypeType, "velocity").GetValue(archetype);
			bucket = new Bucket
			{
				Element = (SimHashes)AccessTools.Field(ArchetypeType, "element").GetValue(archetype),
				WorldIdx = (int)AccessTools.Field(WorldArchetypeType, "worldIdx").GetValue(worldArchetype),
				// Only the default. A caller that chose its own velocity chose it on purpose.
				Governed = velocity == Vector2.up,
				Position = (List<Vector2>)AccessTools.Field(InstanceDataType, "position").GetValue(instanceData),
				Temperature = (List<float>)AccessTools.Field(InstanceDataType, "temperature").GetValue(instanceData),
				Mass = (List<float>)AccessTools.Field(InstanceDataType, "mass").GetValue(instanceData),
				SizeLevel = (List<byte>)AccessTools.Field(InstanceDataType, "sizeLevel").GetValue(instanceData),
				Alpha = (List<float>)AccessTools.Field(InstanceDataType, "alpha").GetValue(instanceData),
				Frame = (List<int>)AccessTools.Field(InstanceDataType, "frame").GetValue(instanceData),
			};
			Buckets.Add(instanceData, bucket);
			return bucket;
		}

		/// <summary>Every live bucket of the manager, or none if there is no manager.</summary>
		private static IEnumerable<Bucket> LiveBuckets(BubbleManager manager)
		{
			if (manager == null)
			{
				yield break;
			}
			IDictionary bubbles = (IDictionary)BubblesField.GetValue(manager);
			IDictionary archetypes = (IDictionary)ArchetypesField.GetValue(manager);
			if (bubbles == null || archetypes == null)
			{
				yield break;
			}
			foreach (DictionaryEntry entry in bubbles)
			{
				Bucket bucket = BucketOf(entry.Key, entry.Value, archetypes);
				if (bucket != null)
				{
					yield return bucket;
				}
			}
		}

		// ------------------------------------------------------------------ physics (public)

		/// <summary>
		/// Density of <paramref name="element"/> as the contents of a bubble, kg/m³. A gas by the
		/// ideal gas law at <paramref name="pressurePa"/> and <paramref name="temperatureK"/>,
		/// with real molecular mass (<see cref="AtmosphereFacade.MolarMassGPerMol"/>, not Klei's
		/// atomic mass for the diatomic gases). A liquid droplet at its liquid density.
		/// </summary>
		public static float DensityKgPerM3(SimHashes element, float temperatureK, float pressurePa)
		{
			Element e = ElementLoader.FindElementByHash(element);
			if (e == null)
			{
				return 0f;
			}
			if (e.IsLiquid)
			{
				return LiquidDensityKgPerM3(e);
			}
			float molarKgPerMol = AtmosphereFacade.MolarMassGPerMol(ElementLoader.GetElementIndex(element)) * 0.001f;
			float t = Mathf.Max(temperatureK, 1f);
			return Mathf.Max(pressurePa, MinPressurePa) * molarKgPerMol / (LiveGasConstant * t);
		}

		/// <summary>
		/// Diameter of a spherical bubble of <paramref name="massKg"/> of
		/// <paramref name="element"/>, in metres, capped at <see cref="MaxDiameterM"/>.
		/// </summary>
		public static float DiameterM(SimHashes element, float massKg, float temperatureK,
			float pressurePa)
		{
			return DiameterFromVolume(VolumeM3(element, massKg, temperatureK, pressurePa));
		}

		/// <summary>Volume of the bubble in m³, uncapped.</summary>
		public static float VolumeM3(SimHashes element, float massKg, float temperatureK,
			float pressurePa)
		{
			float rho = DensityKgPerM3(element, temperatureK, pressurePa);
			return rho > 0f ? massKg / rho : 0f;
		}

		/// <summary>
		/// Terminal rise speed, m/s (the grid is one metre a tile, so this is also tiles a
		/// second), of a bubble or droplet of <paramref name="element"/> in
		/// <paramref name="liquid"/>. Zero if it is not lighter than the liquid.
		///
		/// THE MODEL. Two limits, and the bubble takes the slower:
		/// <list type="bullet">
		/// <item>Stokes, for the smallest bubbles: <c>v = Δρ·g·d² / (18μ)</c>.</item>
		/// <item>Mendelson's wave analogy, <c>v = √(2σ/(ρ_l·d) + 0.71²·g·d·Δρ/ρ_l)</c>, for
		/// everything else. Its large-bubble limit is the Davies-Taylor spherical-cap law,
		/// <c>v = 0.71·√(g·d·Δρ/ρ_l)</c>, which is the regime every vanilla bubble source is in.
		/// The surface-tension term only matters below a couple of centimetres.</item>
		/// </list>
		///
		/// WHAT DENSITY CHANGES. For a gas in a liquid, Δρ/ρ_l is almost exactly one, so the
		/// gas's density barely changes the speed directly. What it changes is the size: at equal
		/// mass hydrogen takes about 22 times the volume of carbon dioxide, so its bubble is about
		/// 2.8 times wider and rises about 1.7 times faster. For a liquid droplet in a liquid the
		/// density difference is the whole story.
		/// </summary>
		public static float RiseVelocityMPerS(SimHashes element, SimHashes liquid, float massKg,
			float temperatureK, float pressurePa)
		{
			Element host = ElementLoader.FindElementByHash(liquid);
			if (host == null || !host.IsLiquid)
			{
				return 0f;
			}
			float rhoBubble = DensityKgPerM3(element, temperatureK, pressurePa);
			if (rhoBubble <= 0f)
			{
				return 0f;
			}
			return VelocityFrom(DiameterFromVolume(massKg / rhoBubble), rhoBubble,
				LiquidDensityKgPerM3(host));
		}

		/// <summary>
		/// Pressure at <paramref name="position"/>, Pa: the gas pressure at the top of the liquid
		/// column above it, plus the weight of every kilogram between the two (one tile is one
		/// square metre, so kilograms times g is pascals). The share of the position's own cell
		/// above it is counted by height.
		///
		/// THE COLUMN IS WEIGHED AS A SOLUTION, not as the grid's liquid alone
		/// (<see cref="Solubility.SolutionMassKg"/>). Gas dissolved in the water above a bubble
		/// is mass that presses down on it, and counting only <c>Grid.Mass</c> understated the
		/// column by the dissolved fraction -- about 1% at soda strength.
		///
		/// The surface gas pressure is the gas column above the surface, pooled
		/// (<see cref="CapSurfacePressurePa"/>), each cell read as vanilla's single-element view
		/// of it with real molar mass, over the volume the gas actually has
		/// (<see cref="GasVolumeM3"/>). In a room the mixture layer owns, that view can lag the
		/// mixture; at a tile of depth the water's own ~9.8 kPa already dominates the difference.
		/// A column capped by a solid has no surface gas, and counts only the liquid.
		/// </summary>
		public static float PressureAtPa(Vector2 position)
		{
			int cell = Grid.PosToCell(position);
			if (!Grid.IsValidCell(cell))
			{
				return 0f;
			}
			float columnKg = 0f;
			if (Grid.IsLiquid(cell))
			{
				float heightInCell = position.y - Mathf.Floor(position.y);
				columnKg += Solubility.SolutionMassKg(cell) * Mathf.Clamp01(1f - heightInCell);
			}
			else
			{
				return SurfacePressurePa(cell);
			}
			int c = Grid.CellAbove(cell);
			while (Grid.IsValidCell(c) && Grid.IsLiquid(c))
			{
				columnKg += Solubility.SolutionMassKg(c);
				c = Grid.CellAbove(c);
			}
			float surfacePa = Grid.IsValidCell(c) ? CapSurfacePressurePa(c) : 0f;
			return surfacePa + columnKg * LiveGravity;
		}

		/// <summary>
		/// How many cells up from a liquid surface <see cref="SurfaceColumnPressurePa"/> pools,
		/// the cap included. The stock value of <see cref="SimTunable.FizzSurfaceColumnCells"/>,
		/// which this class reads live, so the managed pool and the sim's stay one column.
		/// </summary>
		public const int SurfaceColumnCells = 8;

		/// <summary>
		/// The gas pressure on a liquid surface whose cap is <paramref name="capCell"/>, Pa, read
		/// as the gas column above it POOLED: from the cap upward, up to
		/// <see cref="SurfaceColumnCells"/> cells and stopping at the first solid or liquid,
		/// every cell's moles-times-temperature and volume summed, and one ideal-gas pressure
		/// read off the sums, <c>R * sum(n T) / sum(V)</c>. Vacuum adds volume and no moles.
		///
		/// WHY NOT THE CAP CELL ALONE. ONI moves gas one cell at a time, so the cell falling
		/// water has just vacated can hold a few GRAMS while the cells right above it hold a
		/// full atmosphere. Read alone, that pocket puts a pond's surface at a few hundred pascals
		/// and fizzes water nowhere near saturated. The physical gas it stands for is the
		/// headspace's.
		///
		/// This is Stationeers' <c>Room.CacheRoomData</c>, which sums a room's
		/// <c>GasMixture</c> and <c>Volume</c> and reads <c>IdealGas.Pressure</c> once, on a
		/// bounded column instead of a flood-filled room. It is the sim's
		/// <c>FizzSurfacePressure</c> rule for rule, so effervescence and bubbles read the same
		/// surface.
		/// </summary>
		public static float SurfaceColumnPressurePa(int capCell)
		{
			double molesK = 0.0;
			double volumeM3 = 0.0;
			int c = capCell;
			int cells = LiveSurfaceColumnCells;
			for (int k = 0; k < cells && Grid.IsValidCell(c); k++)
			{
				if (!GasContents(c, out double n, out double v))
				{
					break;
				}
				molesK += n;
				volumeM3 += v;
				c = Grid.CellAbove(c);
			}
			return PooledPressurePa(molesK, volumeM3);
		}

		/// <summary>
		/// The surface pressure over a liquid column whose cap is <paramref name="capCell"/>, Pa.
		/// Zero under a solid. A cap holding gas reads its pooled column
		/// (<see cref="SurfaceColumnPressurePa"/>). A cap holding NONE -- a gap the liquid kernel
		/// leaves for a substep when water moves a whole cell -- takes the highest of that pooled
		/// column and the cells left and right of it, and reads 0 only with no gas at any of
		/// them, which is a real vacuum surface. The sim's <c>FizzClassifyCap</c>, rule for rule.
		/// </summary>
		public static float CapSurfacePressurePa(int capCell)
		{
			if (!Grid.IsValidCell(capCell))
			{
				return 0f;
			}
			Element element = Grid.Element[capCell];
			if (element == null || element.IsSolid)
			{
				return 0f;
			}
			float column = SurfaceColumnPressurePa(capCell);
			if (SurfacePressurePa(capCell) > 0f)
			{
				return column;
			}
			float best = column;
			int left = Grid.CellLeft(capCell);
			if (Grid.IsValidCell(left))
			{
				best = Mathf.Max(best, SurfacePressurePa(left));
			}
			int right = Grid.CellRight(capCell);
			if (Grid.IsValidCell(right))
			{
				best = Mathf.Max(best, SurfacePressurePa(right));
			}
			return best;
		}

		/// <summary>
		/// What <paramref name="cell"/> adds to a pooled gas reading: moles times kelvin, and
		/// the volume it offers (<see cref="GasVolumeM3"/>'s own tile plus the free volume of the
		/// liquid under it, unfloored). False for a solid, a liquid or an invalid cell, which a
		/// pooled reading stops at. The sim's <c>FizzGasContents</c>.
		/// </summary>
		private static bool GasContents(int cell, out double molesK, out double volumeM3)
		{
			molesK = 0.0;
			volumeM3 = 0.0;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}
			Element element = Grid.Element[cell];
			if (element == null || !(element.IsGas || element.IsVacuum))
			{
				return false;
			}
			float temperatureK = Mathf.Max(Grid.Temperature[cell], 1f);
			float massKg = Grid.Mass[cell];
			if (element.IsGas && massKg > 0f)
			{
				molesK += MolesK(ElementLoader.GetElementIndex(element.id), massKg, temperatureK);
			}
			// A PROMOTED ROOM keeps its gas in the mixture layer and its cells read vacuum, so a
			// vacuum cell's composition is read too -- only a vacuum one, because
			// SIM_GasComposition takes the sim's barrier and this runs per bubble per frame. The
			// sim's FizzAddCell reads the mixture of every cell.
			if (element.IsVacuum && GasMixtureFacade.Available)
			{
				int count = GasMixtureFacade.ReadComposition(cell, compositionSpecies,
					compositionMassKg);
				for (int i = 0; i < count; i++)
				{
					molesK += MolesK(compositionSpecies[i], compositionMassKg[i], temperatureK);
				}
			}
			double volume = PipeNetworkFacade.LiveCellVolumeM3;
			int below = Grid.CellBelow(cell);
			if (Grid.IsValidCell(below) && Grid.IsLiquid(below))
			{
				volume += Solubility.FreeVolumeM3(below);
			}
			volumeM3 = volume;
			return true;
		}

		private static readonly ushort[] compositionSpecies =
			new ushort[GasMixtureFacade.MaxSpeciesPerCell];
		private static readonly float[] compositionMassKg =
			new float[GasMixtureFacade.MaxSpeciesPerCell];

		private static double MolesK(int elementIdx, float massKg, float temperatureK)
		{
			if (elementIdx < 0 || !(massKg > 0f))
			{
				return 0.0;
			}
			float molarKgPerMol = AtmosphereFacade.MolarMassGPerMol((ushort)elementIdx) * 0.001f;
			return molarKgPerMol > 0f ? (double)massKg / molarKgPerMol * temperatureK : 0.0;
		}

		/// <summary>
		/// Ideal-gas pressure of pooled moles-kelvin over a pooled volume, Pa, the volume floored
		/// at <see cref="MinimumGasVolumeM3"/>. Double throughout, as the sim's
		/// <c>FizzPooledPressure</c> is, so a uniform column pools to exactly its one cell.
		/// </summary>
		private static float PooledPressurePa(double molesK, double volumeM3)
		{
			if (!(molesK > 0.0))
			{
				return 0f;
			}
			double floor = LiveMinimumGasVolumeM3;
			double v = volumeM3 > floor ? volumeM3 : floor;
			return (float)(molesK * LiveGasConstant / v);
		}

		/// <summary>
		/// The volume the gas in <paramref name="cell"/> actually occupies, cubic metres: its own
		/// tile, PLUS whatever of the tile below it the liquid there has left empty
		/// (<see cref="Solubility.FreeVolumeM3"/>). Zero for a cell holding no gas.
		///
		/// WHY THE CELL BELOW IS PART OF IT. ONI's grid will not put gas and liquid in one cell,
		/// so a liquid cell holding 500 kg of water is half a cubic metre of water and half a
		/// cubic metre of VACUUM as far as the sim is concerned -- and the gas sitting on top of
		/// it physically extends into that half. Reading its pressure over one tile made the
		/// surface pressure a STEP FUNCTION of the water level: the level could rise most of a
		/// tile with no change at all, then jump when the cell tipped over. Counting the free
		/// volume below makes it continuous, which is what a partially filled cell has always
		/// physically meant.
		///
		/// <b>THIS IS A READING, NOT A REPRESENTATION. NO GAS IS PUT IN THE LIQUID CELL.</b> The
		/// mass in the numerator is still <c>Grid.Mass</c> of the GAS cell and nothing else; the
		/// grid still holds one element per cell, no message is sent, and a cell cannot hold two
		/// phases. Only the DENOMINATOR differs, because the numerator was never the part that was
		/// wrong.
		///
		/// THE CONSEQUENCE, stated rather than hidden: over a partially filled cell this class's
		/// pressure DISAGREES with the per-cell mass reading vanilla's own gas physics and
		/// UI use, by up to a factor of two. Ours is the physical number and theirs is the
		/// bookkeeping one; nothing reconciles them, and nothing can until a cell can hold both
		/// phases.
		///
		/// IT LOOKS ONLY DOWN. A partially filled liquid cell beside or above a gas cell adds
		/// nothing. Deliberate: the case this exists for is a liquid SURFACE, and a surface is
		/// below the gas that rests on it. A general free-volume flood fill would be a
		/// per-query region walk for a term that is zero everywhere else.
		///
		/// AND THE SAME TERM RUNS THE OTHER WAY. <see cref="Solubility.FreeVolumeM3"/> goes
		/// NEGATIVE for a full cell whose dissolved gas has expanded it past a tile, so
		/// carbonating the water under a headspace squeezes that headspace and raises its
		/// pressure -- which raises the Henry ceiling, which is a real positive feedback and the
		/// reason the expansion is worth computing at all.
		///
		/// This is Stationeers' <c>Atmosphere.GetGasVolume()</c>, which is
		/// <c>max(Volume - TotalVolumeLiquids, GetMinimumGasVolume(Mode))</c>, on a one-cubic-metre
		/// cell instead of a room: same shape, same floor, same reason for the floor.
		/// </summary>
		public static float GasVolumeM3(int cell)
		{
			if (!Grid.IsValidCell(cell))
			{
				return 0f;
			}
			float volumeM3 = PipeNetworkFacade.LiveCellVolumeM3;
			int below = Grid.CellBelow(cell);
			if (Grid.IsValidCell(below) && Grid.IsLiquid(below))
			{
				volumeM3 += Solubility.FreeVolumeM3(below);
			}
			return Mathf.Max(volumeM3, LiveMinimumGasVolumeM3);
		}

		// ------------------------------------------------------------------ bubbles (public)

		/// <summary>
		/// Spawn a bubble through vanilla's <c>BubbleManager</c>, at the default velocity, so
		/// this class governs it. Returns false if there is no manager (no running world).
		/// </summary>
		public static bool Spawn(SimHashes element, Vector2 position, float massKg,
			float temperatureK, byte diseaseIdx = byte.MaxValue, int diseaseCount = 0)
		{
			if (BubbleManager.instance == null)
			{
				return false;
			}
			BubbleManager.instance.SpawnBubble(element, position, massKg, temperatureK,
				new BubbleManager.Disease { Idx = diseaseIdx, Count = diseaseCount });
			return true;
		}

		/// <summary>
		/// The most bubbles <see cref="SpawnSplit"/> makes from one call. Past this each bubble
		/// gets larger instead: a gas vent under Mod 1's 8 kg pipe cap can hand over kilograms in
		/// one 200 ms update, and a few hundred bubbles a second from one tile buys nothing
		/// visible.
		/// </summary>
		public const int MaxSplitBubbles = 16;

		/// <summary>
		/// Release <paramref name="massKg"/> of gas as several bubbles, none larger than
		/// <paramref name="maxVolumeM3"/> at the pressure where it is released (default: vanilla's
		/// largest render size, <see cref="SizeLevelVolumesM3"/>), up to
		/// <see cref="MaxSplitBubbles"/>. For a source that emits more than one bubble's worth at
		/// a time -- an underwater gas vent -- rather than one giant bubble.
		///
		/// Mass and disease are split exactly: the bubbles' masses sum to
		/// <paramref name="massKg"/>, and their disease counts to <paramref name="diseaseCount"/>
		/// (the remainder of the integer division rides on the last bubble). Bubbles are spread
		/// evenly across the middle of the tile, left to right, with no random offset, so the
		/// same call always makes the same bubbles. Returns how many were spawned; 0 if there is
		/// no running world or nothing to spawn.
		/// </summary>
		public static int SpawnSplit(SimHashes element, Vector2 position, float massKg,
			float temperatureK, byte diseaseIdx = byte.MaxValue, int diseaseCount = 0,
			float maxVolumeM3 = -1f)
		{
			if (BubbleManager.instance == null || !(massKg > 0f))
			{
				return 0;
			}
			if (!(maxVolumeM3 > 0f))
			{
				maxVolumeM3 = SizeLevelVolumesM3[SizeLevelVolumesM3.Length - 1];
			}
			float rho = DensityKgPerM3(element, temperatureK, PressureAtPa(position));
			float maxBubbleKg = rho > 0f ? rho * maxVolumeM3 : massKg;
			int count = maxBubbleKg > 0f ? Mathf.CeilToInt(massKg / maxBubbleKg) : 1;
			count = Mathf.Clamp(count, 1, MaxSplitBubbles);
			float each = massKg / count;
			int diseaseEach = diseaseCount / count;
			float spent = 0f;
			for (int i = 0; i < count; i++)
			{
				bool last = i == count - 1;
				float kg = last ? massKg - spent : each;
				spent += kg;
				int germs = last ? diseaseCount - diseaseEach * (count - 1) : diseaseEach;
				float dx = count == 1 ? 0f : (i / (float)(count - 1) - 0.5f) * 0.6f;
				BubbleManager.instance.SpawnBubble(element, position + new Vector2(dx, 0f), kg,
					temperatureK, new BubbleManager.Disease { Idx = diseaseIdx, Count = germs });
			}
			return count;
		}

		/// <summary>
		/// Every bubble currently in flight, across all worlds, appended to
		/// <paramref name="results"/> after clearing it.
		/// </summary>
		public static void GetInFlight(List<BubbleState> results)
		{
			if (results == null)
			{
				throw new ArgumentNullException("results");
			}
			results.Clear();
			foreach (Bucket bucket in LiveBuckets(BubbleManager.instance))
			{
				for (int i = 0; i < bucket.Frame.Count; i++)
				{
					if (bucket.Frame[i] == -1)
					{
						continue;
					}
					results.Add(new BubbleState
					{
						Element = bucket.Element,
						WorldIdx = bucket.WorldIdx,
						Position = bucket.Position[i],
						MassKg = bucket.Mass[i],
						TemperatureK = bucket.Temperature[i],
						SizeLevel = bucket.SizeLevel[i],
					});
				}
			}
		}

		/// <summary>
		/// Kilograms of <paramref name="element"/> in flight, in one world or (with -1) all of
		/// them. This mass is in no grid cell until the bubble pops, so a ledger that weighs
		/// cells must add it.
		/// </summary>
		public static float InFlightMassKg(SimHashes element, int worldIdx = -1)
		{
			float kg = 0f;
			foreach (Bucket bucket in LiveBuckets(BubbleManager.instance))
			{
				if (bucket.Element != element || (worldIdx >= 0 && bucket.WorldIdx != worldIdx))
				{
					continue;
				}
				for (int i = 0; i < bucket.Frame.Count; i++)
				{
					if (bucket.Frame[i] != -1)
					{
						kg += bucket.Mass[i];
					}
				}
			}
			return kg;
		}

		// ------------------------------------------------------------------ installation

		/// <summary>
		/// Patch <c>BubbleManager.Sim33ms</c>. Idempotent. Throws if a member this depends on
		/// has moved, rather than leaving bubbles on vanilla physics with no symptom.
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
			MethodInfo sim33ms = AccessTools.Method(typeof(BubbleManager), "Sim33ms",
				new Type[] { typeof(float) });
			if (sim33ms == null || BubblesField == null || ArchetypesField == null
				|| InstanceDataType == null || ArchetypeType == null || WorldArchetypeType == null)
			{
				throw new InvalidOperationException(
					"Bubbles.Install could not find BubbleManager.Sim33ms or one of the private "
					+ "members it reads (bubbles, archetypes, InstanceData, Archetype, "
					+ "WorldArchetype). The game build has moved; re-check against the current "
					+ "game assembly.");
			}
			foreach (string field in new[] { "position", "temperature", "mass", "sizeLevel",
				"alpha", "frame" })
			{
				if (AccessTools.Field(InstanceDataType, field) == null)
				{
					throw new InvalidOperationException(
						"Bubbles.Install could not find BubbleManager.InstanceData." + field + ".");
				}
			}
			harmony.Patch(sim33ms,
				prefix: new HarmonyMethod(AccessTools.Method(typeof(Bubbles), "Sim33msPrefix")),
				postfix: new HarmonyMethod(AccessTools.Method(typeof(Bubbles), "Sim33msPostfix")));
			installed = true;
		}

		// ------------------------------------------------------------------ the patch
		//
		// Not attributed: OniFramework must never patch anything merely by being loaded.

		private static readonly Dictionary<int, float> PendingKJ = new Dictionary<int, float>();

		/// <summary>
		/// Dissolved mass this tick's bubbles have already sent, keyed by cell and lane, so that
		/// two bubbles in one cell do not both act on the same pre-tick reading. Cleared with
		/// <see cref="PendingKJ"/> at the end of every tick. See <see cref="Exchange"/>.
		/// </summary>
		private static readonly Dictionary<long, float> PendingDissolvedKg =
			new Dictionary<long, float>();

		private static readonly List<KeyValuePair<Bucket, int>> Watched =
			new List<KeyValuePair<Bucket, int>>();

		private static void Sim33msPrefix(BubbleManager __instance, float dt)
		{
			Watched.Clear();
			try
			{
				bool watch = Popped != null;
				foreach (Bucket bucket in LiveBuckets(__instance))
				{
					for (int i = 0; i < bucket.Frame.Count; i++)
					{
						if (bucket.Frame[i] == -1)
						{
							continue;
						}
						if (watch)
						{
							Watched.Add(new KeyValuePair<Bucket, int>(bucket, i));
						}
						if (bucket.Governed)
						{
							Govern(bucket, i, dt);
						}
					}
				}
				foreach (KeyValuePair<int, float> pending in PendingKJ)
				{
					if (pending.Value != 0f)
					{
						SimMessages.ModifyEnergy(pending.Key, pending.Value,
							ExchangeMaxTemperatureK, ExchangeSourceId);
					}
				}
			}
			catch (Exception e)
			{
				// A bubble must never fail to rise because the physics threw. Vanilla's step
				// still runs after this, so the fallback is vanilla's own bubble.
				Debug.LogWarning("[OniFramework] Bubbles.Sim33msPrefix skipped a tick after an "
					+ "exception: " + e);
			}
			finally
			{
				PendingKJ.Clear();
				PendingDissolvedKg.Clear();
			}
		}

		private static void Sim33msPostfix()
		{
			if (Watched.Count == 0)
			{
				return;
			}
			try
			{
				Action<BubbleState> handler = Popped;
				foreach (KeyValuePair<Bucket, int> watched in Watched)
				{
					Bucket bucket = watched.Key;
					int i = watched.Value;
					if (handler == null || bucket.Frame[i] != -1)
					{
						continue;
					}
					handler(new BubbleState
					{
						Element = bucket.Element,
						WorldIdx = bucket.WorldIdx,
						Position = bucket.Position[i],
						MassKg = bucket.Mass[i],
						TemperatureK = bucket.Temperature[i],
						SizeLevel = bucket.SizeLevel[i],
					});
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] Bubbles.Sim33msPostfix: a Popped handler threw: "
					+ e);
			}
			finally
			{
				Watched.Clear();
			}
		}

		/// <summary>One bubble, one tick: heat, size, speed, fade. See the class comment.</summary>
		private static void Govern(Bucket bucket, int i, float dt)
		{
			Vector2 position = bucket.Position[i];
			int cell = Grid.PosToCell(position);
			if (!Grid.IsValidCell(cell))
			{
				return;
			}
			Element host = Grid.Element[cell];
			if (host == null || !host.IsLiquid || host.id == bucket.Element)
			{
				// Out of the liquid, or in its own element: vanilla pops it this tick.
				return;
			}
			Element self = ElementLoader.FindElementByHash(bucket.Element);
			if (self == null)
			{
				return;
			}
			GovernedUpdates++;

			float massKg = bucket.Mass[i];
			float pressurePa = PressureAtPa(position);
			// The density of what the bubble is climbing through, gas dissolved in it included:
			// carbonated water is about 0.27% denser than plain water at soda strength, so a
			// bubble in it rises very slightly faster. Falls back to the pure liquid for a cell
			// holding nothing dissolved.
			float rhoHost = Solubility.SolutionDensityKgPerM3(cell);
			if (!(rhoHost > 0f))
			{
				rhoHost = LiquidDensityKgPerM3(host);
			}

			// ---- heat. Relax toward the temperature the bubble and its cell would share, so a
			// thin liquid cell cannot be driven past equilibrium by a large bubble.
			float t = bucket.Temperature[i];
			float bubbleCapacity = massKg * self.specificHeatCapacity;
			float cellCapacity = Grid.Mass[cell] * host.specificHeatCapacity;
			if (bubbleCapacity > 0f && cellCapacity > 0f && dt > 0f
				&& HeatTransferCoefficientWPerM2K > 0f)
			{
				float rho = DensityKgPerM3(bucket.Element, t, pressurePa);
				float d = rho > 0f ? DiameterFromVolume(massKg / rho) : MaxDiameterM;
				// tau = m c / (h A) with m = rho V, A = pi d^2, V = pi d^3 / 6.
				float tau = rho * self.specificHeatCapacity * 1000f * d
					/ (6f * HeatTransferCoefficientWPerM2K);
				float shared = (bubbleCapacity * t + cellCapacity * Grid.Temperature[cell])
					/ (bubbleCapacity + cellCapacity);
				float fraction = tau > 0f ? 1f - Mathf.Exp(-dt / tau) : 1f;
				float next = t + (shared - t) * fraction;
				float gainedKJ = bubbleCapacity * (next - t);
				if (gainedKJ != 0f && !float.IsNaN(gainedKJ))
				{
					bucket.Temperature[i] = next;
					t = next;
					PendingKJ.TryGetValue(cell, out float owed);
					PendingKJ[cell] = owed - gainedKJ;
					ExchangedKJ += gainedKJ;
				}
			}

			// ---- mass. Trade gas with the water at Henry's-law equilibrium. Runs
			// after the heat step on purpose -- solubility is strongly temperature dependent, so
			// the bubble exchanges matter at the temperature it now has, not the one it arrived
			// with -- and before the size step, so what is drawn this frame is the bubble that
			// is left.
			massKg = Exchange(bucket, i, cell, host, self, massKg, ref t, pressurePa, dt);
			if (!(massKg > 0f))
			{
				// It dissolved completely. Invisible hands it to vanilla's release path this
				// tick, which adds its remaining mass -- nothing -- to the cell above.
				bucket.Mass[i] = 0f;
				bucket.Alpha[i] = 0f;
				return;
			}

			// ---- size and speed, at the bubble's new temperature.
			float rhoBubble = DensityKgPerM3(bucket.Element, t, pressurePa);
			float volume = rhoBubble > 0f ? massKg / rhoBubble : 0f;
			bucket.SizeLevel[i] = SizeLevelFor(volume);
			float v = rhoBubble > 0f
				? VelocityFrom(DiameterFromVolume(volume), rhoBubble, rhoHost)
				: 0f;
			if (v <= 0f)
			{
				// Denser than its host: it cannot rise. Invisible means vanilla releases it into
				// the sim this tick, and the sim's liquid displacement sorts it by density.
				bucket.Alpha[i] = 0f;
				SunkDroplets++;
				return;
			}

			// ---- move. Vanilla adds DEFAULT_VELOCITY * dt (one tile a second) after this.
			bucket.Position[i] = new Vector2(position.x, position.y + (v - 1f) * dt);

			// ---- fade. Vanilla fades a bubble at |velocity| = 1 a second over the tile below
			// the surface; at speed v that tile takes 1/v seconds. -1 means not fading yet.
			float alpha = bucket.Alpha[i];
			if (alpha != -1f)
			{
				bucket.Alpha[i] = Mathf.Clamp01(alpha + (1f - v) * dt);
			}
		}

		/// <summary>
		/// ONE BUBBLE TRADES MATTER WITH ITS CELL. Returns the bubble's mass after the trade and
		/// writes it back into the bucket.
		///
		/// The driving force is the distance from Henry's-law equilibrium. The bubble is pure --
		/// <c>BubbleManager</c> has one element per archetype -- so the partial pressure at the
		/// interface is the whole local pressure, gas column plus water column
		/// (<see cref="PressureAtPa"/>). Against that,
		///
		///     dm = k_L * A * rho_liquid * (c_eq - c) * dt
		///
		/// with A the bubble's surface area, c the cell's present concentration in kg of gas per
		/// kg of liquid, and c_eq what that pressure and temperature support.
		///
		/// THREE CLAMPS, each closing a way to invent or destroy mass:
		/// <list type="bullet">
		/// <item>never past equilibrium in one tick, so a large bubble in a thin cell cannot
		/// overshoot and oscillate;</item>
		/// <item>never more than the bubble has to give;</item>
		/// <item>never more than the cell has to give back, counting what other bubbles have
		/// already taken from it this same tick.</item>
		/// </list>
		///
		/// THE ONE-TICK LAG. <c>DissolvedGas.Add</c> is queued and lands on the sim's next tick,
		/// so <c>DissolvedGas.Read</c> here is one tick behind what this class has already sent.
		/// <see cref="PendingDissolvedKg"/> carries the current tick's own sends so that several
		/// bubbles in one cell do not each see the same stale concentration; the remaining lag is
		/// one 33 ms tick of a transfer that takes seconds, and it is self-correcting because the
		/// next tick sees the real number.
		///
		/// SENSIBLE HEAT TRAVELS WITH THE MASS. A dissolved lane carries no heat of its own, so a
		/// dissolved gas is at its water's temperature. A kilogram that dissolves out of a
		/// bubble hotter than the water therefore pays <c>m c (t - T_cell)</c> to the cell, in
		/// the same batched <c>ModifyEnergy</c> as the heat exchange (colder pays a negative
		/// amount), and a kilogram stripped into a bubble joins it at the water's temperature,
		/// so the bubble's temperature is blended. Without this, a hot bubble's excess would go with
		/// its dissolving mass into a lane that holds no heat and be deleted. Counted in
		/// <see cref="DissolvedEnthalpyKJ"/> and <see cref="DissolutionHeatKJ"/>.
		///
		/// NO HEAT OF SOLUTION. Dissolving a gas releases its enthalpy of solution (about
		/// 20 kJ/mol for CO2) and stripping it costs the same back. That is real and it is not
		/// modelled here: this transfer is isothermal. At the masses involved -- tens of grams
		/// into a tonne of water -- it is a few millikelvin, and the alternative is a second
		/// energy path to keep balanced against the one above it.
		/// </summary>
		private static float Exchange(Bucket bucket, int i, int cell, Element host, Element self,
			float massKg, ref float temperatureK, float pressurePa, float dt)
		{
			if (!(MassTransferCoefficientMPerS > 0f) || !(dt > 0f) || !(pressurePa > 0f)
				|| host == null || !DissolvedGas.Available)
			{
				return massKg;
			}
			float liquidKg = Grid.Mass[cell];
			if (!(liquidKg > 0f))
			{
				return massKg;
			}
			SimHashes gas = bucket.Element;
			// kg of gas per kg of liquid, at this cell's own temperature -- not the bubble's.
			float equilibrium = Solubility.EquilibriumKgPerKg(gas, host.id,
				Grid.Temperature[cell], pressurePa, liquidKg);
			if (!(equilibrium > 0f))
			{
				// This liquid dissolves nothing, or this gas does not dissolve. Either way the
				// bubble crosses it unchanged, which is the right answer for hydrogen in oil.
				return massKg;
			}

			long key = ((long)cell << 8) | (uint)Mathf.Max(DissolvedGas.LaneOf(gas), 0);
			float pending;
			PendingDissolvedKg.TryGetValue(key, out pending);
			float presentKg = Mathf.Max(DissolvedGas.Read(cell, gas) + pending, 0f);
			// What this cell holds at equilibrium, over the volume its solution occupies rather
			// than over a whole tile: a half-full cell holds half as much. See
			// Solubility.EquilibriumKg.
			float capacityKg = Solubility.EquilibriumKg(cell, gas, pressurePa);
			if (!(capacityKg > 0f))
			{
				return massKg;
			}

			float rhoBubble = DensityKgPerM3(gas, temperatureK, pressurePa);
			if (!(rhoBubble > 0f))
			{
				return massKg;
			}
			// The interface. Either the drawn sphere, or -- for gas released through something
			// that disperses it -- the specific surface of that dispersion, 6m/(rho*d). See
			// InterfacialDiameterM.
			float areaM2;
			if (InterfacialDiameterM > 0f)
			{
				areaM2 = 6f * massKg / (rhoBubble * InterfacialDiameterM);
			}
			else
			{
				float diameterM = DiameterFromVolume(massKg / rhoBubble);
				areaM2 = Mathf.PI * diameterM * diameterM;
			}
			float gradient = (capacityKg - presentKg) / liquidKg;
			float transferKg = MassTransferCoefficientMPerS * areaM2 * liquidKg * gradient * dt;

			if (transferKg > 0f)
			{
				// Into the water. Not past equilibrium, and not more than the bubble holds.
				transferKg = Mathf.Min(transferKg, capacityKg - presentKg);
				transferKg = Mathf.Min(transferKg, massKg);
			}
			else
			{
				// Out of the water. Not past equilibrium, and not more than the cell holds --
				// and of what it holds, only HALF in one tick. `presentKg` is a tick stale (the
				// add that would correct it is still queued), and the sim refuses a whole
				// message that would take a lane below zero rather than clamping it, which
				// would leave the bubble holding mass the water never gave up. Half of a
				// reading that moves by a fraction of a percent per tick is not a limit any
				// real exchange reaches; it is a floor under an arithmetic race.
				transferKg = -Mathf.Min(-transferKg, presentKg - capacityKg);
				transferKg = -Mathf.Min(-transferKg, presentKg * 0.5f);
			}
			if (transferKg == 0f || float.IsNaN(transferKg) || float.IsInfinity(transferKg))
			{
				return massKg;
			}

			if (!DissolvedGas.Add(cell, gas, transferKg))
			{
				// The sim would not take it -- no lane, no property, or the cell stopped being
				// liquid between the checks. The bubble keeps the mass.
				ExchangeRefusals++;
				return massKg;
			}
			PendingDissolvedKg[key] = pending + transferKg;

			float next = Mathf.Max(massKg - transferKg, 0f);
			bucket.Mass[i] = next;
			float c = self.specificHeatCapacity;
			float cellK = Grid.Temperature[cell];
			if (transferKg > 0f)
			{
				DissolvedKg += transferKg;
				DissolvedEnthalpyKJ += (double)transferKg * c * temperatureK;
				// The mass leaves at the bubble's temperature and is held at the water's: the
				// difference is heat, and it goes to the water.
				float payKJ = transferKg * c * (temperatureK - cellK);
				if (payKJ != 0f && !float.IsNaN(payKJ))
				{
					PendingKJ.TryGetValue(cell, out float owed);
					PendingKJ[cell] = owed + payKJ;
					DissolutionHeatKJ += payKJ;
				}
			}
			else
			{
				float strippedKg = -transferKg;
				StrippedKg += strippedKg;
				DissolvedEnthalpyKJ -= (double)strippedKg * c * cellK;
				// Stripped gas joins at the water's temperature: blend, same heat capacity.
				if (next > 0f)
				{
					temperatureK = (massKg * temperatureK + strippedKg * cellK) / next;
					bucket.Temperature[i] = temperatureK;
				}
			}
			return next;
		}

		// ------------------------------------------------------------------ helpers

		private static float VelocityFrom(float diameterM, float rhoBubble, float rhoHost)
		{
			float delta = rhoHost - rhoBubble;
			if (delta <= 0f || diameterM <= 0f || rhoHost <= 0f)
			{
				return 0f;
			}
			float g = LiveGravity;
			float stokes = delta * g * diameterM * diameterM
				/ (18f * Mathf.Max(LiquidViscosityPaS, 1e-6f));
			float wave = Mathf.Sqrt(2f * SurfaceTensionNPerM / (rhoHost * diameterM)
				+ 0.71f * 0.71f * g * diameterM * delta / rhoHost);
			return Mathf.Min(stokes, wave);
		}

		private static float DiameterFromVolume(float volumeM3)
		{
			if (volumeM3 <= 0f)
			{
				return 0f;
			}
			return Mathf.Min(Mathf.Pow(6f * volumeM3 / Mathf.PI, 1f / 3f), MaxDiameterM);
		}

		private static byte SizeLevelFor(float volumeM3)
		{
			for (int level = 0; level < SizeLevelVolumesM3.Length; level++)
			{
				if (volumeM3 <= SizeLevelVolumesM3[level])
				{
					return (byte)level;
				}
			}
			return (byte)SizeLevelVolumesM3.Length;
		}

		/// <summary>
		/// A liquid's PURE density: <see cref="MaterialPropertyRegistry"/> first, else the
		/// element's default cell mass (ONI's full cell is one cubic metre), else water's 1000.
		/// This is the solvent alone. For the density of what a cell is actually holding, gas
		/// dissolved in it included, use <see cref="Solubility.SolutionDensityKgPerM3"/>.
		/// </summary>
		public static float LiquidDensityKgPerM3(Element liquid)
		{
			if (MaterialPropertyRegistry.TryGetLiquidDensityKgPerM3(liquid.id, out float rho)
				&& rho > 0f)
			{
				return rho;
			}
			float defaultMass = liquid.defaultValues.mass;
			return defaultMass > 0f ? defaultMass : LiveFallbackSolventDensityKgM3;
		}

		/// <summary>
		/// The gas pressure of one non-liquid cell alone, Pa, over the volume the gas HAS, not
		/// the tile it is indexed by (<see cref="GasVolumeM3"/>); zero for a solid or vacuum. The
		/// pressure at a position inside gas. A liquid SURFACE reads its column instead
		/// (<see cref="CapSurfacePressurePa"/>).
		/// </summary>
		private static float SurfacePressurePa(int cell)
		{
			if (!GasContents(cell, out double molesK, out double volumeM3))
			{
				return 0f;
			}
			return PooledPressurePa(molesK, volumeM3);
		}
	}
}
