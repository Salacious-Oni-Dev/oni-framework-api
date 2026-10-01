// GENERATED from the SimDLL's sim/tunables.def by its tools/gen_tunables_cs.py. DO NOT
// EDIT: change the row there and regenerate. A member's value is its row's position, which
// is the tunable's id on the wire (kSetTunable) and in every SIM_ExtTunable* export. Rows
// are append-only, so an id never changes meaning. SimTunables is the API that uses these.
namespace OniFramework
{
	/// <summary>
	/// One of the SimDLL's live-tunable numbers. Read and set through <see cref="SimTunables"/>.
	/// 132 rows, generated from the SimDLL's own table.
	/// </summary>
	public enum SimTunable
	{
		/// <summary>Seconds of simulated time per substep (0.2). Every rate in the sim is per substep.</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		SubstepSeconds = 0,
		/// <summary>Cell pairs closer than this in temperature (K) exchange no heat (1.0).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		MinConductionDelta = 1,
		/// <summary>log2 of the conduction quiet-tile edge in cells (4 = 16x16). Perf only.</summary>
		/// <remarks>int; group Heat; origin OURS.</remarks>
		ConductionTileShift = 2,
		/// <summary>Building-to-building heat transfer scale (0.005). Its cell form is derived.</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		BuildingTransferRate = 3,
		/// <summary>Ceiling for building, conduit and chunk temperatures (10000 K).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		MaxTemperature = 4,
		/// <summary>Ceiling a ModifyCell or cell add clamps its temperature to (10000 K).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		CellModMaxTemperature = 5,
		/// <summary>Pipe-contents to pipe-wall contact area (50).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		ConduitContactArea = 6,
		/// <summary>Pipe-contents to pipe-wall transfer scale (0.001).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		ConduitTransferRate = 7,
		/// <summary>A pipe side with no more heat capacity than this exchanges nothing (1e-4).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		MinConduitHeatCapacity = 8,
		/// <summary>Smallest pipe-to-cell energy transfer that is applied (1e-6).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		MinConduitEnergy = 9,
		/// <summary>Element-chunk (debris) to cell transfer scale (0.001).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		ChunkTransferRate = 10,
		/// <summary>K past lowTemp/highTemp before a cell changes state (3.0).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		TransitionMargin = 11,
		/// <summary>K a cell's temperature is pulled back across the line when it changes state (1.5).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		TransitionOvershoot = 12,
		/// <summary>K past a pipe's contents' transition before it is flagged (3.0).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		ConduitTransitionMargin = 13,
		/// <summary>K past a debris chunk's transition before it is reported (3.0).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		ChunkTransitionMargin = 14,
		/// <summary>m/s^2, for hydrostatic pressure under liquid (9.80665).</summary>
		/// <remarks>float; group Phase; origin PHYS.</remarks>
		StandardGravity = 15,
		/// <summary>Sublimation stops once the receiving gas cell holds this much (1.8 kg).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		SublimationCellCapKg = 16,
		/// <summary>Liquid off-gassing stops once the gas cell above holds this much (1.8 kg).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		OffGasCellCapKg = 17,
		/// <summary>Most a liquid cell off-gasses in one substep (1.0 kg).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		OffGasAmountCapKg = 18,
		/// <summary>A liquid left with less than this after off-gassing is used up whole (0.01 kg).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		OffGasResidualKg = 19,
		/// <summary>A freeze at or under this share of the solid's default mass becomes ore (0.8).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		SmallFreezeOreRatio = 20,
		/// <summary>Mass one partial melt takes off a solid (5 kg).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		PartialMeltKg = 21,
		/// <summary>K around a solid's highTemp that gates a partial melt and sets its melt temperature (3).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		PartialMeltMargin = 22,
		/// <summary>K above its lowTemp the heating gas keeps after paying for a partial melt (6).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		PartialMeltGasMargin = 23,
		/// <summary>K outside its liquid range a falling-liquid spawn is still accepted (3).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		FallingLiquidTemperatureWindow = 24,
		/// <summary>Largest share of a gas cell the pressure sweeps move in one step (0.125).</summary>
		/// <remarks>float; group Gas; origin KLEI.</remarks>
		GasPressureCap = 25,
		/// <summary>A gas cell under this mass is evaporated as a wisp (0.001 kg).</summary>
		/// <remarks>float; group Gas; origin KLEI.</remarks>
		GasWispMassKg = 26,
		/// <summary>A random draw at or under this skips the gas shuffle (0.9).</summary>
		/// <remarks>float; group Gas; origin KLEI.</remarks>
		GasShuffleSkipGate = 27,
		/// <summary>A random draw at or under this skips one shuffle candidate (0.5).</summary>
		/// <remarks>float; group Gas; origin KLEI.</remarks>
		GasShuffleCandidateCoin = 28,
		/// <summary>Chance a gas cell swaps with a same-state cell below (0.99).</summary>
		/// <remarks>float; group Gas; origin KLEI.</remarks>
		GasDisplacementProbability = 29,
		/// <summary>Chance a liquid cell swaps with a same-state cell below (0.30).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidDisplacementProbability = 30,
		/// <summary>Share of a level difference a liquid moves sideways per step (0.25).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidHorizontalRate = 31,
		/// <summary>How much heavier a liquid cell must be than its neighbour to push (1.01).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidPressureRatio = 32,
		/// <summary>An upward liquid flow at or under this is dropped (0.01 kg).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidMinUpwardFlow = 33,
		/// <summary>Share of the excess a liquid pours down into a fuller cell (0.5).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidPourShare = 34,
		/// <summary>Share of the excess an over-full liquid pushes up (0.5).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidUpwardShare = 35,
		/// <summary>An upward push from a cell this many times over its ceiling ignores viscosity (2.0).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidViscosityBypassRatio = 36,
		/// <summary>A liquid cell under this mass is handled as a thin film in post-process (0.01 kg).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		ThinLiquidKg = 37,
		/// <summary>Least liquid a displaced liquid cell must hold to be moved (0.01 kg).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		CellModMinLiquidKg = 38,
		/// <summary>A cell at or under this mass counts as empty (FLT_MIN).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		CellModEmptyMass = 39,
		/// <summary>Scale of a fresh unstable-solid countdown draw (bits of 3/32767).</summary>
		/// <remarks>float; group Rng; origin KLEI.</remarks>
		StableTicksRerollScale = 40,
		/// <summary>Least substeps an unstable solid waits before falling (3).</summary>
		/// <remarks>int; group Rng; origin KLEI.</remarks>
		StableTicksRerollBase = 41,
		/// <summary>Scale turning a 15-bit rand() into [0, 1] (the float nearest 1/32767).</summary>
		/// <remarks>float; group Rng; origin KLEI.</remarks>
		RandomScale = 42,
		/// <summary>Share a sealed room's gases move toward its pooled mix per tick (0.2).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		RoomMixingRate = 43,
		/// <summary>Unchanged ticks before a room's pooled mixing sleeps (5).</summary>
		/// <remarks>int; group Mixture; origin OURS.</remarks>
		RoomMixingSleepThreshold = 44,
		/// <summary>Room pooled mixing runs every Nth tick; 0 never (1).</summary>
		/// <remarks>int; group Mixture; origin OURS.</remarks>
		RoomMixingEveryNTicks = 45,
		/// <summary>A mixing transfer at or under this (kg) is skipped as equilibrated (1e-6).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		MinTransferMass = 46,
		/// <summary>Volume of one cell for ideal-gas pressure (1 m^3).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		CellVolumeM3 = 47,
		/// <summary>Ideal gas constant, J/(mol K) (8.3144, Stationeers' value).</summary>
		/// <remarks>float; group Mixture; origin PHYS.</remarks>
		GasConstantR = 48,
		/// <summary>Solid share at which a mixed cell projects as solid (0.55).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		SolidEnter = 49,
		/// <summary>Solid share under which a solid-projected cell stops being solid (0.45).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		SolidExit = 50,
		/// <summary>Cells a dirty rectangle is widened by before projection. A FLOOR: one more than the furthest kernel write, so it may be raised, never lowered.</summary>
		/// <remarks>int; group Mixture; origin OURS.</remarks>
		ProjectReach = 51,
		/// <summary>Gas mass (kg) at which a building's exhaust is fully blocked (1.5).</summary>
		/// <remarks>float; group Building; origin KLEI.</remarks>
		ExhaustMaxPressure = 52,
		/// <summary>Stefan-Boltzmann constant in kW/(m^2 K^4).</summary>
		/// <remarks>double; group Building; origin PHYS.</remarks>
		StefanBoltzmannKW = 53,
		/// <summary>Pressure under which a building cannot convect (6300 Pa).</summary>
		/// <remarks>double; group Building; origin STN.</remarks>
		ArmstrongLimitPa = 54,
		/// <summary>Gas pressure at which convection is full strength (101325 Pa).</summary>
		/// <remarks>float; group Building; origin STN.</remarks>
		ConvectionOneAtmospherePa = 55,
		/// <summary>Liquid volume share at which convection is full strength (0.001).</summary>
		/// <remarks>float; group Building; origin STN.</remarks>
		ConvectionLiquidVolumeRatioForFull = 56,
		/// <summary>Building-to-surroundings convection coefficient (100 W/m^2K).</summary>
		/// <remarks>float; group Building; origin STN.</remarks>
		BuildingConvectionWPerM2K = 57,
		/// <summary>CEILING on the cells a building convects across (4).</summary>
		/// <remarks>int; group Building; origin OURS.</remarks>
		MaxBuildingConvectionReach = 58,
		/// <summary>CEILING on a world boundary's exchange rate (0.5).</summary>
		/// <remarks>float; group Building; origin OURS.</remarks>
		MaxWorldBoundaryRate = 59,
		/// <summary>CEILING on a pipe network's per-step mixing fraction (0.25).</summary>
		/// <remarks>float; group Building; origin OURS.</remarks>
		MaxConduitMixFraction = 60,
		/// <summary>Liquid density assumed for a cell's element that has none (1000 kg/m^3).</summary>
		/// <remarks>float; group Building; origin OURS.</remarks>
		FallbackCellLiquidDensityKgM3 = 61,
		/// <summary>Liquid density assumed for pipe contents that have none (1000 kg/m^3).</summary>
		/// <remarks>float; group Building; origin OURS.</remarks>
		FallbackConduitLiquidDensityKgM3 = 62,
		/// <summary>Mass of a world-border cell (9999 kg).</summary>
		/// <remarks>float; group Building; origin KLEI.</remarks>
		WorldBorderMassKg = 63,
		/// <summary>Temperature of a world-border cell (0 K).</summary>
		/// <remarks>float; group Building; origin KLEI.</remarks>
		WorldBorderTemperatureK = 64,
		/// <summary>Base of the per-substep germ half-life decay (2).</summary>
		/// <remarks>float; group Disease; origin KLEI.</remarks>
		DiseaseDecayBase = 65,
		/// <summary>Share of a germ-count difference that spreads per step (0.125).</summary>
		/// <remarks>float; group Disease; origin KLEI.</remarks>
		DiseaseDiffusionShare = 66,
		/// <summary>CEILING on a cell's infestation age counter (254; a uint8, 255 is reserved).</summary>
		/// <remarks>int; group Disease; origin KLEI.</remarks>
		MaxInfestationTicks = 67,
		/// <summary>Floor a cell's radiation is clamped to (0).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		MinRadiation = 68,
		/// <summary>CEILING a cell's radiation is clamped to (9e6).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		MaxRadiation = 69,
		/// <summary>Occlusion cutoff and final floor of the radiation field (0.01).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationEpsilon = 70,
		/// <summary>Rads a cell gains per germ of radiation sickness (0.001).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationGermEmissionScale = 71,
		/// <summary>A directional emitter's cell under this level does not spread (0.01).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationSpreadGate = 72,
		/// <summary>Emitter falloff under which noise is added (0.25).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationNoiseFalloffGate = 73,
		/// <summary>Bias subtracted from emitter noise (0.125).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationNoiseBias = 74,
		/// <summary>Scale turning a 15-bit rand() into emitter noise (bits of 1/(4*32767)).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationNoiseScale = 75,
		/// <summary>Stencil weight at (-2,-2) (0.10).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil00 = 76,
		/// <summary>Stencil weight at (-1,-2) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil01 = 77,
		/// <summary>Stencil weight at (0,-2) (0.25).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil02 = 78,
		/// <summary>Stencil weight at (1,-2) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil03 = 79,
		/// <summary>Stencil weight at (2,-2) (0.10).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil04 = 80,
		/// <summary>Stencil weight at (-2,-1) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil05 = 81,
		/// <summary>Stencil weight at (-1,-1) (0.50).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil06 = 82,
		/// <summary>Stencil weight at (0,-1) (0.75).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil07 = 83,
		/// <summary>Stencil weight at (1,-1) (0.50).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil08 = 84,
		/// <summary>Stencil weight at (2,-1) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil09 = 85,
		/// <summary>Stencil weight at (-2,0) (0.25).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil10 = 86,
		/// <summary>Stencil weight at (-1,0) (0.75).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil11 = 87,
		/// <summary>Stencil weight at (0,0) (1.00).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil12 = 88,
		/// <summary>Stencil weight at (1,0) (0.75).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil13 = 89,
		/// <summary>Stencil weight at (2,0) (0.25).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil14 = 90,
		/// <summary>Stencil weight at (-2,1) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil15 = 91,
		/// <summary>Stencil weight at (-1,1) (0.50).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil16 = 92,
		/// <summary>Stencil weight at (0,1) (0.75).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil17 = 93,
		/// <summary>Stencil weight at (1,1) (0.50).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil18 = 94,
		/// <summary>Stencil weight at (2,1) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil19 = 95,
		/// <summary>Stencil weight at (-2,2) (0.10).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil20 = 96,
		/// <summary>Stencil weight at (-1,2) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil21 = 97,
		/// <summary>Stencil weight at (0,2) (0.25).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil22 = 98,
		/// <summary>Stencil weight at (1,2) (0.15).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil23 = 99,
		/// <summary>Stencil weight at (2,2) (0.10).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationStencil24 = 100,
		/// <summary>Liquid fill alpha: mass multiplier before the curve (0.001).</summary>
		/// <remarks>float; group Texture; origin KLEI.</remarks>
		FillAlphaScale = 101,
		/// <summary>Liquid fill alpha curve exponent (0.45).</summary>
		/// <remarks>float; group Texture; origin KLEI.</remarks>
		FillAlphaExponent = 102,
		/// <summary>Liquid fill gradient: mass divisor before the curve (1000).</summary>
		/// <remarks>float; group Texture; origin KLEI.</remarks>
		FillGradientReference = 103,
		/// <summary>Liquid fill gradient curve exponent (0.45).</summary>
		/// <remarks>float; group Texture; origin KLEI.</remarks>
		FillGradientExponent = 104,
		/// <summary>Dissolved-gas tint curve exponent (0.45).</summary>
		/// <remarks>float; group Texture; origin OURS.</remarks>
		DissolvedTintExponent = 105,
		/// <summary>K beyond an element's range its temperature colour ramp extends (3).</summary>
		/// <remarks>float; group Texture; origin KLEI.</remarks>
		TemperatureRampMargin = 106,
		/// <summary>Henry's-law reference temperature (298.15 K).</summary>
		/// <remarks>float; group Fizz; origin PHYS.</remarks>
		FizzReferenceK = 107,
		/// <summary>Smallest gas volume a bubble pressure is computed over (1e-4 m^3).</summary>
		/// <remarks>float; group Fizz; origin OURS.</remarks>
		FizzMinimumGasVolumeM3 = 108,
		/// <summary>Gas cells above a liquid surface pooled into its surface pressure (8).</summary>
		/// <remarks>int; group Fizz; origin OURS.</remarks>
		FizzSurfaceColumnCells = 109,
		/// <summary>Gas a cell always keeps when a liquid dissolves from it (0.002 kg).</summary>
		/// <remarks>float; group Fizz; origin OURS.</remarks>
		FizzUptakeKeepKg = 110,
		/// <summary>Largest share of a gas cell dissolved in one step (0.5).</summary>
		/// <remarks>float; group Fizz; origin OURS.</remarks>
		FizzUptakeMaxShare = 111,
		/// <summary>Solvent density assumed for an element with no default mass (1000 kg/m^3).</summary>
		/// <remarks>float; group Fizz; origin OURS.</remarks>
		FizzFallbackSolventDensityKgM3 = 112,
		/// <summary>A liquid over this many times its maxMass displaces the liquid above (1.5).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		OverfullDisplaceRatio = 113,
		/// <summary>How much heavier than the cell above an over-full liquid must be (1.01).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		OverfullHeavierRatio = 114,
		/// <summary>Share of an over-full cell moved up into the cleared cell (bits of 1/2.01).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		OverfullShare = 115,
		/// <summary>Resistance a pressure break starts from before any wall (1).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		PressureBreakBaseResistance = 116,
		/// <summary>Scale on each wall cell's strength term (0.25).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		PressureBreakStrengthScale = 117,
		/// <summary>Resistance a natural (unbuilt) wall cell adds (bits 0x3dccccd0, ~0.1).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		PressureBreakUnbuiltFloor = 118,
		/// <summary>Share of a gas cell `DoGasPressureDisplacement` moves in (0.125).</summary>
		/// <remarks>float; group Gas; origin KLEI.</remarks>
		GasPressureDisplacementShare = 119,
		/// <summary>Share of a liquid's surplus `DoLiquidPressureDisplacement` moves (0.125).</summary>
		/// <remarks>float; group Liquid; origin KLEI.</remarks>
		LiquidPressureDisplacementShare = 120,
		/// <summary>Floor a conducting cell's temperature is clamped to after each pair (1 K).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		ConductionMinTemperature = 121,
		/// <summary>Ceiling a conducting cell's temperature is clamped to after each pair (10000 K).</summary>
		/// <remarks>float; group Heat; origin KLEI.</remarks>
		ConductionMaxTemperature = 122,
		/// <summary>A cell at or under this mass refuses ModifyCellEnergy/exhaust (0.001 kg).</summary>
		/// <remarks>float; group Building; origin KLEI.</remarks>
		CellEnergyMinMassKg = 123,
		/// <summary>Floor ModifyCellEnergy/exhaust clamps an out-of-range result to (1 K).</summary>
		/// <remarks>float; group Building; origin KLEI.</remarks>
		CellEnergyMinTemperature = 124,
		/// <summary>Rads a cell loses per tick when have/linger underflows to 0 (1).</summary>
		/// <remarks>float; group Radiation; origin KLEI.</remarks>
		RadiationDecayStep = 125,
		/// <summary>Share of the full equalising transfer MixPair/EqualizeSingleSpecies move (0.5).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		GasEqualizeDamping = 126,
		/// <summary>Share of the full equalising transfer EqualizeLiquidVolume moves (0.5).</summary>
		/// <remarks>float; group Mixture; origin OURS.</remarks>
		LiquidEqualizeDamping = 127,
		/// <summary>Share of a source's germs a liquid pressure displacement carries with its slice (0.125).</summary>
		/// <remarks>float; group Disease; origin KLEI.</remarks>
		LiquidDisplacementGermShare = 128,
		/// <summary>Share of a source's germs a gas pressure displacement carries with its slice (0.125).</summary>
		/// <remarks>float; group Disease; origin KLEI.</remarks>
		GasDisplacementGermShare = 129,
		/// <summary>A state change's ore share is spawned only above this mass (0.001 kg).</summary>
		/// <remarks>float; group Phase; origin KLEI.</remarks>
		TransitionOreMinKg = 130,
		/// <summary>1 = ModifyCellEnergy carries each cell's float-rounding remainder into its next payment (0).</summary>
		/// <remarks>int; group Building; origin OURS.</remarks>
		CellEnergyCarry = 131,
	}
}
