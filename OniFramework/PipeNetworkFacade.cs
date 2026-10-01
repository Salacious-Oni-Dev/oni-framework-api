using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Which conduit layer a network reading came from. Vanilla ONI keeps gas and liquid
	/// conduits on separate object layers with separate <c>ConduitFlow</c> instances, unlike
	/// Stationeers where one <c>Atmosphere</c> holds both phases at once -- see
	/// <see cref="PipeNetworkFacade"/>'s own class doc for what that costs us.
	/// </summary>
	public enum PipeContentType
	{
		Gas,
		Liquid,
	}

	/// <summary>
	/// How bad a network's condition is, in Stationeers' own two tiers.
	///
	/// <see cref="Stressed"/> is Stationeers' <c>Thing.Stressed</c>: the quantity has passed
	/// <see cref="PipeNetworkFacade.StressedRatio"/> (0.8) of its limit but the network is not
	/// yet taking damage -- a warning. <see cref="Critical"/> is the point where
	/// <c>AtmosphericsNetwork.ApplyPressureDamageToWeakest</c> would start applying real damage.
	/// </summary>
	public enum PipeStressLevel
	{
		None = 0,
		Stressed = 1,
		Critical = 2,
	}

	/// <summary>
	/// What is wrong with a network. Mirrors Stationeers' <c>PipeBurst</c> flags
	/// (<c>Pressure</c>/<c>Liquid</c>/<c>Solid</c>) rather than inventing a new taxonomy, plus
	/// one mirror case ONI's split conduit layers make possible that Stationeers has no name
	/// for (<see cref="Vaporization"/>).
	/// </summary>
	public enum PipeStressKind
	{
		None = 0,

		/// <summary>Stationeers <c>PipeBurst.Pressure</c>: differential pressure against the
		/// surrounding world atmosphere is approaching or past the pipe's rating. For a liquid
		/// network this is hydraulic overfill instead -- see
		/// <see cref="PipeNetworkFacade.TryEvaluateStress"/>.</summary>
		Overpressure = 1,

		/// <summary>Stationeers <c>PipeBurst.Liquid</c> / <c>CondensationInGasPipe</c>: a GAS
		/// network cold enough for its own contents to condense at its own pressure.</summary>
		Condensation = 2,

		/// <summary>Stationeers <c>PipeBurst.Solid</c>: contents at or below their freezing
		/// point, which in Stationeers plugs the network with solid and damages the weakest
		/// member.</summary>
		Freezing = 3,

		/// <summary>The mirror of <see cref="Condensation"/> for a LIQUID network -- contents
		/// hot enough to boil inside a pipe that cannot hold gas. Stationeers has no equivalent
		/// burst flag because one Stationeers network holds both phases at once; ONI's liquid
		/// conduits physically cannot, so the case is real here and needs its own name.</summary>
		Vaporization = 4,

		/// <summary>Stationeers <c>PipeBurst.Liquid</c> in its literal form: a GAS network with
		/// liquid ALREADY STANDING in it, measured as the volume fraction
		/// <c>AtmosphericsNetwork.EvaluateIncorrectMatterState</c> tests against
		/// <see cref="PipeNetworkFacade.LiquidInGasPipeDamageRatio"/>.
		///
		/// DISTINCT FROM <see cref="Condensation"/>, and the distinction is the whole reason
		/// this value exists. Condensation is an APPROACH warning derived from the vapour
		/// curve -- the contents are still gas, and they are on course to condense. This is the
		/// AFTERMATH: the gas has condensed, the liquid is sitting in the pipe, and the pipe is
		/// being damaged for holding it. A run can be in the second state without being in the
		/// first, which is exactly the hole this closed: a pipe whose contents had
		/// ENTIRELY become standing condensate has no gas left for the vapour curve to have an
		/// opinion about, so it reported perfectly healthy while holding kilograms of liquid
		/// oxygen one top-up short of rupturing.</summary>
		StandingLiquid = 5,

		/// <summary>Stationeers <c>PipeBurst.Solid</c> in its literal form: solid matter ALREADY
		/// STANDING in a network of either type, measured in moles against
		/// <see cref="PipeNetworkFacade.MinFrozenMolesToDamage"/>. The same approach-versus-
		/// aftermath distinction <see cref="StandingLiquid"/> draws against
		/// <see cref="Condensation"/>, drawn here against <see cref="Freezing"/>.</summary>
		StandingSolid = 6,
	}

	/// <summary>
	/// One species' share of a network's contents. <see cref="Moles"/> and
	/// <see cref="MoleFraction"/> are what Stationeers' own tooltip prints per gas
	/// (<c>AtmosphericsManager.DisplayGas</c>: name, percent of total moles, then the raw mole
	/// count).
	/// </summary>
	public readonly struct PipeSpecies
	{
		/// <summary>Index into <c>ElementLoader.elements</c> -- the same convention
		/// <c>GasMixtureFacade.GasComponent.ElementIdx</c> already uses.</summary>
		public readonly int ElementIdx;

		public readonly float MassKg;

		/// <summary>Moles, from this substance's MOLECULAR mass
		/// (<c>MaterialPropertyRegistry.TryGetMolecularMassGPerMol</c>), NOT from Klei's
		/// <c>Element.molarMass</c> -- see <see cref="PipeNetworkFacade"/>'s class doc for why
		/// those two disagree for diatomics.</summary>
		public readonly float Moles;

		/// <summary>0..1 share of the network's total moles.</summary>
		public readonly float MoleFraction;

		public PipeSpecies(int elementIdx, float massKg, float moles, float moleFraction)
		{
			ElementIdx = elementIdx;
			MassKg = massKg;
			Moles = moles;
			MoleFraction = moleFraction;
		}
	}

	/// <summary>
	/// A whole connected pipe run's aggregate state -- the unit Stationeers reports at
	/// (its <c>Atmosphere</c> belongs to the <c>PipeNetwork</c>, never to one pipe), rather than
	/// the single tile vanilla ONI's <c>ConduitFlow.GetContents</c> answers for.
	/// </summary>
	public sealed class PipeNetworkState
	{
		public PipeContentType ContentType;

		/// <summary>Every conduit tile in the connected run, in ASCENDING CELL ORDER.
		///
		/// The run comes from <see cref="ConduitNetworks"/>, one array per network rather than one
		/// per asker, so no order can be relative to the caller. See that class for why ascending was the order
		/// chosen.
		///
		/// THE ARRAY IS BORROWED. It is the cache's own, valid until the next topology change,
		/// and writing to it corrupts every other reader of the same network. It is handed out by
		/// reference because copying it per call is precisely the allocation that was removed.
		/// </summary>
		public int[] Cells = Array.Empty<int>();

		/// <summary>Total internal volume of the run, in litres -- tiles times
		/// <c>GasMixtureFacade.GasConduitVolumeM3</c>/<c>LiquidConduitVolumeM3</c>. This is
		/// Stationeers' <c>Atmosphere.Volume</c>, the "Volume:" tooltip line.</summary>
		public float VolumeLitres;

		public float TotalMassKg;

		/// <summary>Mass-weighted mean of every tile's temperature. Stationeers' networks share
		/// ONE temperature by construction; ONI's per-tile conduits do not, so this collapses
		/// them the same way <c>GasMixtureFacade.CombineTemperature</c> collapses two
		/// containers.</summary>
		public float TemperatureK;

		/// <summary>Real gas pressure of the whole run (Pa), via the native shared formula.
		/// Always 0 for a liquid network -- ONI liquids are volume-tracked, not ideal-gas (see
		/// <c>GasMixtureFacade.EqualizeLiquidVolumeMass</c>).</summary>
		public float PressurePa;

		public float TotalMoles;

		/// <summary>Liquid networks only: occupied volume / capacity, unclamped, so an
		/// over-capacity run reports above 1 rather than silently pinning.</summary>
		public float FillFraction;

		/// <summary>Liquid networks only: occupied volume in litres -- the counterpart of
		/// Stationeers' "Liquids Volume:" line.</summary>
		public float LiquidVolumeLitres;

		public PipeSpecies[] Contents = Array.Empty<PipeSpecies>();
	}

	/// <summary>
	/// The same aggregate as <see cref="PipeNetworkState"/>, as a VALUE, for the components that
	/// read a network every tick.
	///
	/// A struct rather than a class because the point of it is that reading a network costs no
	/// allocation: <see cref="PipeNetworkFacade.TryReadNetwork"/> borrows the cells from
	/// <see cref="ConduitNetworks"/>, sums into shared scratch, and fills this out on the
	/// caller's stack. Nine per-tick components on one pipe loop used to allocate six objects
	/// each, sixty times a minute, for numbers they read once and dropped.
	///
	/// <see cref="PipeNetworkState.Contents"/> has no counterpart here on purpose -- the
	/// composition is the one part that cannot be a fixed-size value, and almost no caller reads
	/// it. Those that do call <see cref="PipeNetworkFacade.ReadNetworkComposition"/> with their
	/// own buffers.
	/// </summary>
	public struct PipeNetworkReading
	{
		public PipeContentType ContentType;

		/// <summary>The game's own <c>UtilityNetwork.id</c> for this run, or -1 if it could not
		/// be resolved. STABLE ONLY WITHIN ONE <see cref="ConduitNetworks.Version"/>: a topology
		/// change renumbers every network from zero, so this identifies a run within a tick and
		/// must not be stored across a build or a deconstruct.</summary>
		public int NetworkId;

		/// <summary>Every conduit tile in the run, ascending, BORROWED from the cache on the same
		/// terms as <see cref="PipeNetworkState.Cells"/> -- do not write to it.</summary>
		public int[] Cells;

		public int CellCount;

		/// <summary>Number of distinct elements found across the run's tiles. Read
		/// <see cref="PipeNetworkFacade.ReadNetworkComposition"/> to find out which.</summary>
		public int SpeciesCount;

		public float VolumeLitres;
		public float TotalMassKg;
		public float TotalMoles;
		public float TemperatureK;

		/// <summary>Gas runs only; always 0 for a liquid run, exactly as
		/// <see cref="PipeNetworkState.PressurePa"/> is.</summary>
		public float PressurePa;

		/// <summary>Liquid runs only.</summary>
		public float LiquidVolumeLitres;

		/// <summary>Liquid runs only; unclamped, so an over-capacity run reads above 1.</summary>
		public float FillFraction;
	}

	/// <summary>
	/// The answer to "is this network in trouble, and which tile is worst." Everything needed to
	/// render a warning is here so no caller re-derives a threshold.
	/// </summary>
	public readonly struct PipeStressReport
	{
		public readonly PipeStressLevel Level;
		public readonly PipeStressKind Kind;

		/// <summary>The single tile to point a notification at -- Stationeers' own
		/// <c>_weakestMember</c> concept. See <see cref="PipeNetworkFacade.TryEvaluateStress"/>
		/// for how it is picked per kind.</summary>
		public readonly int WorstCell;

		/// <summary>The species driving a thermal warning; -1 for pressure/fill warnings.</summary>
		public readonly int ElementIdx;

		/// <summary>The measured quantity, in whatever unit <see cref="Kind"/> implies: Pa for
		/// <see cref="PipeStressKind.Overpressure"/> on a gas network, a 0..1+ fill fraction for
		/// overpressure on a liquid network, K for the thermal kinds.</summary>
		public readonly float Value;

		/// <summary>The limit <see cref="Value"/> was compared against, same unit.</summary>
		public readonly float Limit;

		/// <summary>How close to failure, 0..1+ (1 = at the damage threshold). Comparable across
		/// kinds, so a caller can rank several networks without knowing the units.</summary>
		public readonly float Severity;

		public PipeStressReport(PipeStressLevel level, PipeStressKind kind, int worstCell,
			int elementIdx, float value, float limit, float severity)
		{
			Level = level;
			Kind = kind;
			WorstCell = worstCell;
			ElementIdx = elementIdx;
			Value = value;
			Limit = limit;
			Severity = severity;
		}
	}

	/// <summary>
	/// Whole-pipe-network readings and Stationeers' pipe-stress model, exposed as API. It backs
	/// a Stationeers-style pipe tooltip and overpressure/thermal notifications, but nothing here
	/// is specific to one mod: any mod wanting "what is actually in this pipe run, and is it
	/// about to fail" gets the same answer from the same numbers.
	///
	/// NO NEW PHYSICS LIVES HERE, deliberately. Every
	/// quantity is either read from vanilla's own <c>ConduitFlow</c>, computed by the native
	/// shared formula already exported for it (<c>GasMixtureFacade.TryComputePressure</c> ->
	/// <c>SIM_ComputeGasPressure</c>), or looked up in
	/// <see cref="MaterialPropertyRegistry"/>. What is new is the AGGREGATION (per-tile conduits
	/// collapsed into the whole-network unit Stationeers reports at) and the THRESHOLDS, which
	/// are Stationeers' own constants.
	///
	/// THE THRESHOLDS follow Stationeers' pipe limits and network evaluation: a pipe is Stressed when the DIFFERENTIAL pressure against its own surrounding world
	/// atmosphere exceeds <see cref="StressedRatio"/> of its rating, and takes damage past the
	/// rating itself.
	///
	/// TWO HONEST DEVIATIONS, both forced by ONI's own conduit model rather than chosen:
	/// 1. Stationeers counts actual condensed/frozen MOLES sitting in the pipe
	///    (<c>GasMixture.CheckForFreezing</c>, <c>TotalVolumeLiquids</c>) because one Stationeers
	///    network holds gas and liquid at once. An ONI gas conduit physically cannot hold a
	///    liquid (<c>ConduitFlow.AddElement</c> refuses a second element, and the two phases live
	///    on different object layers entirely), so there are no condensed moles to count. This
	///    detects the CONDITION instead -- network temperature against the real clamped Antoine
	///    curve at the network's own pressure, the same
	///    <c>MaterialPropertyRegistry.TryGetEvaporationTemperatureClampedK</c> that pipe phase
	///    change runs on. The tiering band (<see cref="ThermalStressMarginK"/>) is ours,
	///    not Stationeers', because there is no mole count to take 5% of.
	/// 2. A liquid network has no ideal-gas pressure in ONI's model at all, so its
	///    <see cref="PipeStressKind.Overpressure"/> case is hydraulic OVERFILL measured against
	///    the same <see cref="StressedRatio"/>. Stationeers' own liquid handling is the same idea
	///    (<c>AtmosphereHelper.DrainLiquids</c> refuses to fill a network past a minimum gas
	///    headspace), just expressed as a ratio rather than a pressure.
	///
	/// ONE SOURCE OF TRUTH FOR MOLES: <see cref="PipeSpecies.Moles"/> comes from the registry's
	/// MOLECULAR mass, and <see cref="PipeNetworkState.PressurePa"/> comes from the native kernel,
	/// which divides by a corrected molecular mass (<c>ElementTable::MolecularMassOf</c>) that
	/// <see cref="MaterialPropertyRegistry.PushMolecularMassesToSim"/> feeds from this same
	/// registry. Klei's <c>Element.molarMass</c> is ATOMIC for the diatomics (Oxygen 15.9994,
	/// Hydrogen 1.00794), so dividing by it would make every diatomic pressure exactly 2x higher
	/// than the printed mole count implies.
	/// </summary>
	public static class PipeNetworkFacade
	{
		/// <summary>
		/// Stationeers <c>Chemistry.Limits.MAXPressureGasPipe</c> = 60794.99816894531 kPa,
		/// converted to Pa. The rating a normal or insulated gas pipe bursts at
		/// (<c>Pipe.MaxPressure</c>).
		/// </summary>
		public const float MaxGasPipePressurePa = 60794998.17f;

		/// <summary>
		/// Stationeers <c>Chemistry.Limits.MAXPressureLiquidPipe</c> = 6079.499816894531 kPa,
		/// in Pa. Kept for completeness and for any caller modelling a liquid line as a real
		/// pressure vessel; this facade's own liquid path uses fill fraction instead (see the
		/// class doc's deviation 2).
		/// </summary>
		public const float MaxLiquidPipePressurePa = 6079499.82f;

		/// <summary>
		/// Stationeers <c>Thing.StressedRatio</c> = 0.8f. The single fraction that separates
		/// "warn the player" from "start doing damage," used for every stress kind here so the
		/// two tiers mean the same thing everywhere.
		/// </summary>
		public const float StressedRatio = 0.8f;

		/// <summary>
		/// FLOOR of the band, in K: how close a network's temperature has to get to a phase
		/// boundary before it counts as <see cref="PipeStressLevel.Stressed"/>. Crossing the
		/// boundary is <see cref="PipeStressLevel.Critical"/>. The band actually used is
		/// <see cref="ThermalStressMarginFor"/>, which never returns less than this.
		///
		/// OURS, NOT STATIONEERS' -- see the class doc's deviation 1. Stationeers tiers thermal
		/// damage off a mole count (<c>0.05 * VolumeLitres</c> of frozen/condensed matter) that
		/// an ONI conduit cannot physically accumulate, so there is nothing to take a fraction
		/// of. 5 K is a deliberately small floor: large enough to give the player a tick or two
		/// of warning before a pipe full of gas starts condensing, small enough that an ordinary
		/// well-run line never sits permanently in it.
		/// </summary>
		public const float ThermalStressMarginK = 5.0f;

		/// <summary>
		/// Fraction of the phase-boundary temperature that the warning band scales with, above
		/// the <see cref="ThermalStressMarginK"/> floor.
		///
		/// WHY PROPORTIONAL. A flat 5 K is not the same warning at every
		/// temperature: on a ~135 K oxygen line it is 3.7% of the boundary, but on a ~373 K steam
		/// line it is 1.25% -- so the hotter line got a third of the relative notice for the same
		/// physical risk, even though its contents swing further per unit of heat gained or lost.
		/// A fraction fixes that: 5% gives oxygen ~6.8 K and steam ~18.7 K, and the floor keeps
		/// cryogenic contents from getting a uselessly narrow band.
		///
		/// This matters more than a tiering nicety, and that is the reason it changed. ONI VOIDS a
		/// pipe once its contents fall below <c>element.lowTemp - 3 K</c> (native
		/// <c>sim/conduits.h</c>), so the useful part of a condensation warning is the band ABOVE
		/// the boundary -- the part where the player can still act. Widening that band is the
		/// cheapest real improvement available to it.
		///
		/// OURS, like the floor. Stationeers has no equivalent to copy.
		/// </summary>
		public const float ThermalStressMarginFraction = 0.05f;

		/// <summary>
		/// Volume of one world cell in m^3, for pricing the ambient atmosphere a pipe is
		/// surrounded by. Matches <c>gas_mixture.h</c>'s own <c>kCellVolumeM3</c>, which is what
		/// the native per-cell pressure export already divides by -- so an ambient pressure
		/// derived here and one read from <c>GasMixtureFacade.TryGetPressure</c> are directly
		/// comparable rather than differing by a hidden scale factor.
		///
		/// The stock value of <see cref="SimTunable.CellVolumeM3"/>. The framework's own
		/// arithmetic reads the live row (<c>LiveCellVolumeM3</c>), so a tuned cell volume moves
		/// both sides of that comparison together.
		/// </summary>
		public const float CellVolumeM3 = 1.0f;

		// The sim's live cell volume and pipe-liquid density fallback (SimTunables), with the
		// stock constants as the fallback for a SimDLL without the table.
		internal static float LiveCellVolumeM3 =>
			SimTunables.GetFloat(SimTunable.CellVolumeM3, CellVolumeM3);
		internal static float LiveFallbackLiquidDensityKgM3 =>
			SimTunables.GetFloat(SimTunable.FallbackConduitLiquidDensityKgM3,
				FallbackLiquidDensityKgM3);

		/// <summary>
		/// Volume fraction of a GAS network that has to be occupied by condensed liquid before
		/// Stationeers starts damaging the pipe: <c>ratio &gt; 0.02</c> in
		/// <c>AtmosphericsNetwork.EvaluateIncorrectMatterState</c>. Liquid and All networks are
		/// explicitly no-ops there -- this hazard is gas-pipes-only, because a liquid line is
		/// supposed to be full of liquid.
		/// </summary>
		public const float LiquidInGasPipeDamageRatio = 0.02f;

		/// <summary>
		/// Stationeers' own damage magnitude for liquid in a gas pipe:
		/// <c>clamp(log10(ratio * 100) - 0.8, 0.2, 10)</c>, verbatim from
		/// <c>EvaluateIncorrectMatterState</c>. Returns 0 below
		/// <see cref="LiquidInGasPipeDamageRatio"/>, where Stationeers does not damage at all.
		///
		/// The shape is worth keeping rather than replacing with something linear: it is
		/// logarithmic, so a pipe just over the threshold is nicked (0.2) while one badly full of
		/// condensate is punished hard (up to 10), and the caller does not have to invent a curve.
		///
		/// WHY THIS EXISTS AT ALL. A mod that intercepts vanilla's freezing-pipe handler and
		/// condenses the contents instead of deleting them must not drop the damage with it:
		/// Stationeers damages a pipe for solid contents (<c>PipeBurst.Solid</c>) AND for liquid in
		/// a gas pipe (<c>PipeBurst.Liquid</c>), on top of the pressure case.
		/// </summary>
		public static float LiquidInGasPipeDamage(float liquidVolumeRatio)
		{
			if (liquidVolumeRatio <= LiquidInGasPipeDamageRatio)
			{
				return 0f;
			}
			return Mathf.Clamp(Mathf.Log10(liquidVolumeRatio * 100f) - 0.8f, 0.2f, 10f);
		}

		/// <summary>
		/// Stationeers' <c>MinFrozenMolesToDamage()</c>: <c>0.05 * Atmosphere.Volume</c>, with the
		/// volume in LITRES and the result read as MOLES. Below this much frozen
		/// matter a pipe is not damaged at all, so it doubles as the "is there enough solid in
		/// here to matter" gate.
		/// </summary>
		public static float MinFrozenMolesToDamage(float volumeLitres)
		{
			return 0.05f * volumeLitres;
		}

		/// <summary>
		/// Stationeers' damage magnitude for FROZEN contents:
		/// <c>clamp(log2(frozenMoles / minMoles), 0.5, 10)</c>, verbatim from
		/// <c>AtmosphericsNetwork.EvaluateIncorrectMatterState</c>. Returns 0 below
		/// <see cref="MinFrozenMolesToDamage"/>.
		///
		/// Note the two deliberate differences from <see cref="LiquidInGasPipeDamage"/>, both
		/// Stationeers' own: this is log2 rather than log10 and its floor is 0.5 rather than 0.2,
		/// so solid matter ramps up roughly three times faster per doubling and starts off worse.
		/// A pipe full of ice is a more urgent problem than a pipe with some condensate in it,
		/// and the curve says so.
		///
		/// Unlike the liquid rule, this one is NOT gas-networks-only in Stationeers -- a liquid
		/// line whose contents freeze is damaged by exactly this path.
		/// </summary>
		public static float FrozenContentsDamage(float frozenMoles, float volumeLitres)
		{
			float minMoles = MinFrozenMolesToDamage(volumeLitres);
			if (minMoles <= 0f || frozenMoles <= minMoles)
			{
				return 0f;
			}
			return Mathf.Clamp(Mathf.Log(frozenMoles / minMoles, 2f), 0.5f, 10f);
		}

		/// <summary>
		/// Stationeers' damage magnitude for the PRESSURE branch:
		/// <c>clamp(log2(delta / MaxPressure), 0.2, 10)</c>, verbatim from
		/// <c>AtmosphericsNetwork.ApplyPressureDamageToWeakest</c>, where it is followed by
		/// <c>weakest.DamageRecord |= PipeBurst.Pressure</c>. Returns 0 at or below the rating,
		/// which is exactly where Stationeers does no damage at all -- below the rating a pipe is
		/// merely <c>Stressed</c>, and Stationeers signals that with positional audio rather than
		/// with hit points.
		///
		/// TAKES A RATIO, NOT TWO PRESSURES, and that is deliberate. Stationeers divides a
		/// differential pressure by the member's rating; this project's
		/// <see cref="PipeStressReport.Severity"/> is already that quotient for a gas run
		/// (<c>|networkPressure - ambientPressure| / MaxGasPipePressurePa</c>) and is documented
		/// as "normalised across units precisely so that comparison is meaningful." Feeding the
		/// ratio straight in keeps one number flowing from the evaluation to the damage instead
		/// of reconstructing the division at the call site.
		///
		/// LIQUID NETWORKS USE THE SAME CURVE ON A DIFFERENT RATIO. Stationeers' pressure branch is phase-agnostic -- it walks every member of
		/// every network and compares that member's <c>MaxPressure</c> -- but an ONI liquid
		/// conduit has no ideal-gas pressure to compare (see this class's deviation 2), so its
		/// severity is hydraulic <see cref="PipeNetworkState.FillFraction"/> against 1.0 instead.
		/// The honest statement of the difference: Stationeers asks "how far past its rating is
		/// this pressure vessel," and for a liquid line this asks "how far past full is it."
		/// Both answer the same question about the same failure and both feed the same curve, but
		/// only the gas case is a literal port.
		///
		/// The 0.2 floor matters more here than the shape does. A gas conduit's
		/// <c>Def.HitPoints</c> is 10 (<c>GasConduitConfig</c>), so Stationeers' 0.2..10 range
		/// lands almost exactly on one ONI conduit's whole health bar: a run barely over its
		/// rating loses 1 HP per evaluation and has ten seconds of grace, while one at eight
		/// times its rating loses 3 and has three. That spread is the reason the curve was worth
		/// porting rather than replacing with a linear ramp.
		/// </summary>
		public static float OverpressureDamage(float severityRatio)
		{
			if (severityRatio <= 1f)
			{
				return 0f;
			}
			return Mathf.Clamp(Mathf.Log(severityRatio, 2f), 0.2f, 10f);
		}

		private const float GramsPerKilogram = 1000f;
		private const float LitresPerCubicMetre = 1000f;

		/// <summary>
		/// <summary>Scratch for the per-species sum inside a network read. Cleared per call and
		/// never handed out, so the aggregate walk costs no allocation of its own. Single-threaded
		/// by construction -- every caller is a game-thread component.</summary>
		private static readonly Dictionary<int, float> MassScratch = new Dictionary<int, float>();

		/// <summary>Parallel scratch the species dictionary is flattened into before it goes to
		/// <c>GasMixtureFacade.TryComputePressure</c>, which takes arrays. Grown on demand and
		/// kept, so a colony settles onto one allocation apiece for the life of the session.</summary>
		private static int[] SpeciesIdxScratch = new int[16];
		private static float[] SpeciesMassScratch = new float[16];

		private static void EnsureSpeciesScratch(int count)
		{
			if (SpeciesIdxScratch.Length >= count)
			{
				return;
			}
			int size = SpeciesIdxScratch.Length;
			while (size < count)
			{
				size *= 2;
			}
			SpeciesIdxScratch = new int[size];
			SpeciesMassScratch = new float[size];
		}

		/// <summary>
		/// The cells of the run containing <paramref name="cell"/>, from
		/// <see cref="ConduitNetworks"/> when the game has partitioned the colony, and from the
		/// old flood fill when it has not yet (a pipe placed this frame, before
		/// <c>UtilityNetworkManager.Update</c> has run).
		///
		/// The cached answer is the CACHE'S OWN ARRAY and must not be written to -- see
		/// <see cref="ConduitNetworks.TryGetCells"/>. The fallback answer is a fresh array; a
		/// caller cannot tell which it got, so neither may be mutated.
		/// </summary>
		private static int[] ResolveNetworkCells(int cell, PipeContentType contentType)
		{
			int[] cached;
			if (ConduitNetworks.TryGetCells(cell, contentType, out cached))
			{
				return cached;
			}

			List<int> walked = contentType == PipeContentType.Liquid
				? GasMixtureFacade.FloodFillLiquidNetwork(cell)
				: GasMixtureFacade.FloodFillPipeNetwork(cell);
			return walked.Count == 0 ? null : walked.ToArray();
		}

		/// <summary>
		/// The tile of <paramref name="cells"/> spatially furthest from <paramref name="fromCell"/>,
		/// or <c>Grid.InvalidCell</c> when the run holds nothing else.
		///
		/// WHY THIS EXISTS. A discharge pump must not drop its output beside itself: ONI fixes a
		/// run's flow direction when the network is built and a machine has no say in it, so
		/// discharging at the port works or does not work depending on which end the game decided
		/// was downstream. The failure was measured -- a Carbon Skimmer made 310 kg of polluted
		/// water, the pipe held 2.26 kg of it at the machine's end, and the reservoir eight tiles
		/// away read empty.
		///
		/// The callers that need this used to get it for free by walking the flood fill BACKWARDS,
		/// because a breadth-first walk from the port put the graph-farthest tile last. The cached
		/// partition has no per-caller origin and so no such order (see
		/// <see cref="ConduitNetworks"/>), which turns an incidental property into something that
		/// has to be asked for. Asking for it is also more honest: this measures MANHATTAN
		/// distance on the grid, not distance along the pipe, so a run that doubles back on itself
		/// answers differently than the old walk did. Every caller wants "far from the machine" in
		/// the physical sense the bug above was about, and every one of them falls back to trying
		/// the other tiles if the far one refuses the offer.
		///
		/// Ties go to the lowest cell index, so the answer does not depend on array order.
		/// </summary>
		public static int FarthestNetworkCell(int[] cells, int fromCell)
		{
			if (cells == null || !Grid.IsValidCell(fromCell))
			{
				return Grid.InvalidCell;
			}

			Vector2I from = Grid.CellToXY(fromCell);
			int best = Grid.InvalidCell;
			int bestDistance = -1;
			for (int i = 0; i < cells.Length; i++)
			{
				int candidate = cells[i];
				if (candidate == fromCell || !Grid.IsValidCell(candidate))
				{
					continue;
				}
				Vector2I at = Grid.CellToXY(candidate);
				int distance = Math.Abs(at.x - from.x) + Math.Abs(at.y - from.y);
				if (distance > bestDistance || (distance == bestDistance && candidate < best))
				{
					bestDistance = distance;
					best = candidate;
				}
			}
			return best;
		}

		/// <summary>
		/// THE CELLS ONLY, WITHOUT READING A SINGLE TILE'S CONTENTS. For the many callers whose
		/// whole question is "which tiles are in this run" -- find the first tile holding
		/// something, offer mass to a tile that is not the port, bill a set of tiles -- and which
		/// were paying for a full aggregate they then ignored.
		///
		/// Allocates NOTHING in the steady state. Compare
		/// <see cref="TryGetNetworkState(int, PipeContentType, out PipeNetworkState)"/>, which
		/// additionally walks every tile summing mass and species and returns a fresh state
		/// object.
		///
		/// THE ARRAY IS BORROWED, NOT GIVEN. It belongs to <see cref="ConduitNetworks"/>, it is
		/// valid until the next topology change, and writing to it corrupts every other reader of
		/// that network.
		/// </summary>
		public static bool TryGetNetworkCells(int cell, PipeContentType contentType,
			out int[] cells)
		{
			cells = null;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}

			ConduitFlow flow = contentType == PipeContentType.Liquid
				? Game.Instance?.liquidConduitFlow
				: Game.Instance?.gasConduitFlow;
			if (flow == null || !flow.HasConduit(cell))
			{
				return false;
			}

			int[] resolved = ResolveNetworkCells(cell, contentType);
			if (resolved == null || resolved.Length == 0)
			{
				return false;
			}

			cells = resolved;
			return true;
		}

		/// <summary>
		/// The aggregate a per-tick component actually reads, as a STRUCT, with no allocation
		/// anywhere on the path.
		///
		/// Same numbers as <see cref="PipeNetworkState"/> and computed by the same arithmetic --
		/// mass summed per species over the run, temperature mass-weighted, gas pressure through
		/// <c>GasMixtureFacade.TryComputePressure</c>, liquid fill through Mod 2's densities --
		/// with one thing left out on purpose: the per-species breakdown. A caller that needs the
		/// composition asks for it separately through
		/// <see cref="ReadNetworkComposition"/> and supplies its own buffers, exactly as
		/// <c>GasMixtureFacade.ReadComposition</c> does for a cell, so the common case does not
		/// pay for an array it will not read.
		///
		/// <see cref="PipeNetworkReading.Cells"/> is borrowed on the same terms as
		/// <see cref="TryGetNetworkCells"/>.
		/// </summary>
		public static bool TryReadNetwork(int cell, PipeContentType contentType,
			out PipeNetworkReading reading)
		{
			reading = default(PipeNetworkReading);

			ConduitFlow flow = contentType == PipeContentType.Liquid
				? Game.Instance?.liquidConduitFlow
				: Game.Instance?.gasConduitFlow;
			if (flow == null)
			{
				return false;
			}

			int[] cells;
			if (!TryGetNetworkCells(cell, contentType, out cells))
			{
				return false;
			}

			float tileVolumeM3 = contentType == PipeContentType.Liquid
				? GasMixtureFacade.LiquidConduitVolumeM3
				: GasMixtureFacade.GasConduitVolumeM3;

			Dictionary<int, float> massByElement = MassScratch;
			massByElement.Clear();
			float totalMassKg = 0f;
			float weightedTemperature = 0f;

			for (int i = 0; i < cells.Length; i++)
			{
				ConduitFlow.ConduitContents contents = flow.GetContents(cells[i]);
				if (contents.mass <= 0f)
				{
					continue;
				}
				Element element = ElementLoader.FindElementByHash(contents.element);
				if (element == null)
				{
					continue;
				}
				int idx = ElementLoader.elements.IndexOf(element);
				if (idx < 0)
				{
					continue;
				}
				float existing;
				massByElement.TryGetValue(idx, out existing);
				massByElement[idx] = existing + contents.mass;
				totalMassKg += contents.mass;
				weightedTemperature += contents.mass * contents.temperature;
			}

			reading.ContentType = contentType;
			reading.NetworkId = ConduitNetworks.NetworkIdOf(cell, contentType);
			reading.Cells = cells;
			reading.CellCount = cells.Length;
			reading.VolumeLitres = cells.Length * tileVolumeM3 * LitresPerCubicMetre;
			reading.TotalMassKg = totalMassKg;
			reading.TemperatureK = totalMassKg > 0f ? weightedTemperature / totalMassKg : 0f;
			reading.SpeciesCount = massByElement.Count;

			if (massByElement.Count == 0)
			{
				return true;
			}

			EnsureSpeciesScratch(massByElement.Count);
			int[] speciesIdx = SpeciesIdxScratch;
			float[] speciesMass = SpeciesMassScratch;
			int n = 0;
			float totalMoles = 0f;
			foreach (KeyValuePair<int, float> entry in massByElement)
			{
				speciesIdx[n] = entry.Key;
				speciesMass[n] = entry.Value;
				totalMoles += MolesOf(entry.Key, entry.Value);
				n++;
			}
			reading.TotalMoles = totalMoles;

			if (contentType == PipeContentType.Gas)
			{
				// TryComputePressure reads only the first SpeciesCount entries it is told about,
				// so the scratch arrays being longer than the species count is not a problem --
				// but they are shared, so the count travels with them everywhere below.
				float pressurePa;
				if (TryComputeScratchPressure(speciesIdx, speciesMass, n, reading.TemperatureK,
					cells.Length * tileVolumeM3, out pressurePa))
				{
					reading.PressurePa = pressurePa;
				}
			}
			else
			{
				float occupiedM3 = 0f;
				for (int k = 0; k < n; k++)
				{
					occupiedM3 += GasMixtureFacade.LiquidVolumeFromMass(speciesMass[k],
						LiquidDensityOf(speciesIdx[k]));
				}
				reading.LiquidVolumeLitres = occupiedM3 * LitresPerCubicMetre;
				float capacityM3 = cells.Length * tileVolumeM3;
				reading.FillFraction = capacityM3 > 0f ? occupiedM3 / capacityM3 : 0f;
			}

			return true;
		}

		private static int[] ExactIdxScratch = new int[0];
		private static float[] ExactMassScratch = new float[0];

		/// <summary>
		/// <c>GasMixtureFacade.TryComputePressure</c> takes two arrays and uses their LENGTH as
		/// the species count, which the shared scratch buffers deliberately do not respect -- they
		/// are grown to a power of two and reused. This trims to exactly the live count, into two
		/// more reused buffers, rather than making the shared ones exact-sized and reallocating on
		/// every colony whose species count wobbles by one.
		/// </summary>
		private static bool TryComputeScratchPressure(int[] speciesIdx, float[] speciesMass,
			int count, float temperatureK, float volumeM3, out float pressurePa)
		{
			pressurePa = 0f;
			if (count <= 0)
			{
				return false;
			}
			if (ExactIdxScratch.Length != count)
			{
				ExactIdxScratch = new int[count];
				ExactMassScratch = new float[count];
			}
			Array.Copy(speciesIdx, ExactIdxScratch, count);
			Array.Copy(speciesMass, ExactMassScratch, count);
			return GasMixtureFacade.TryComputePressure(ExactIdxScratch, ExactMassScratch,
				temperatureK, volumeM3, out pressurePa);
		}

		/// <summary>
		/// The per-species breakdown of a run, into CALLER-OWNED buffers. Returns the number of
		/// species written, or 0 when there is no run, no contents, or the buffers are too small.
		///
		/// Counterpart to <c>GasMixtureFacade.ReadComposition</c> and refuses on the same terms:
		/// a buffer that cannot hold the answer is a refusal, not a truncation, because a
		/// truncated composition is a wrong mole fraction rather than a short list.
		/// <paramref name="moles"/> may be null when the caller only wants mass.
		/// </summary>
		public static int ReadNetworkComposition(int cell, PipeContentType contentType,
			int[] elementIdx, float[] massKg, float[] moles)
		{
			if (elementIdx == null || massKg == null)
			{
				return 0;
			}

			ConduitFlow flow = contentType == PipeContentType.Liquid
				? Game.Instance?.liquidConduitFlow
				: Game.Instance?.gasConduitFlow;
			if (flow == null)
			{
				return 0;
			}

			int[] cells;
			if (!TryGetNetworkCells(cell, contentType, out cells))
			{
				return 0;
			}

			Dictionary<int, float> massByElement = MassScratch;
			massByElement.Clear();
			for (int i = 0; i < cells.Length; i++)
			{
				ConduitFlow.ConduitContents contents = flow.GetContents(cells[i]);
				if (contents.mass <= 0f)
				{
					continue;
				}
				Element element = ElementLoader.FindElementByHash(contents.element);
				if (element == null)
				{
					continue;
				}
				int idx = ElementLoader.elements.IndexOf(element);
				if (idx < 0)
				{
					continue;
				}
				float existing;
				massByElement.TryGetValue(idx, out existing);
				massByElement[idx] = existing + contents.mass;
			}

			int count = massByElement.Count;
			if (count == 0 || count > elementIdx.Length || count > massKg.Length
				|| (moles != null && count > moles.Length))
			{
				return 0;
			}

			int n = 0;
			foreach (KeyValuePair<int, float> entry in massByElement)
			{
				elementIdx[n] = entry.Key;
				massKg[n] = entry.Value;
				if (moles != null)
				{
					moles[n] = MolesOf(entry.Key, entry.Value);
				}
				n++;
			}
			return n;
		}

		/// Aggregate state of the whole connected conduit run containing
		/// <paramref name="cell"/>. Returns <c>false</c> when there is no conduit of that type
		/// at the cell; returns <c>true</c> with an EMPTY <see cref="PipeNetworkState.Contents"/>
		/// for a real but empty run, because "this pipe exists and holds nothing" is a genuinely
		/// different answer from "there is no pipe here" and the caller usually wants to say so.
		/// </summary>
		public static bool TryGetNetworkState(int cell, PipeContentType contentType,
			out PipeNetworkState state)
		{
			state = null;
			if (!Grid.IsValidCell(cell))
			{
				return false;
			}

			ConduitFlow flow = contentType == PipeContentType.Liquid
				? Game.Instance?.liquidConduitFlow
				: Game.Instance?.gasConduitFlow;
			if (flow == null || !flow.HasConduit(cell))
			{
				return false;
			}

			int[] cells = ResolveNetworkCells(cell, contentType);
			if (cells == null || cells.Length == 0)
			{
				return false;
			}

			float tileVolumeM3 = contentType == PipeContentType.Liquid
				? GasMixtureFacade.LiquidConduitVolumeM3
				: GasMixtureFacade.GasConduitVolumeM3;

			// Sum mass per species across every tile. A vanilla conduit is single-element per
			// tile, but a RUN can still hold two different elements on two different tiles (they
			// only merge once something equalizes it), so this is a real multi-species answer,
			// not a formality.
			Dictionary<int, float> massByElement = MassScratch;
			massByElement.Clear();
			float totalMassKg = 0f;
			float weightedTemperature = 0f;

			foreach (int c in cells)
			{
				ConduitFlow.ConduitContents contents = flow.GetContents(c);
				if (contents.mass <= 0f)
				{
					continue;
				}
				Element element = ElementLoader.FindElementByHash(contents.element);
				if (element == null)
				{
					continue;
				}
				int idx = ElementLoader.elements.IndexOf(element);
				if (idx < 0)
				{
					continue;
				}
				massByElement.TryGetValue(idx, out float existing);
				massByElement[idx] = existing + contents.mass;
				totalMassKg += contents.mass;
				weightedTemperature += contents.mass * contents.temperature;
			}

			state = new PipeNetworkState
			{
				ContentType = contentType,
				Cells = cells,
				VolumeLitres = cells.Length * tileVolumeM3 * LitresPerCubicMetre,
				TotalMassKg = totalMassKg,
				TemperatureK = totalMassKg > 0f ? weightedTemperature / totalMassKg : 0f,
			};

			if (massByElement.Count == 0)
			{
				return true;
			}

			// Moles first, because the mole fractions Stationeers prints are shares of the
			// TOTAL, so nothing can be finalised until every species has been counted.
			var elementIdx = new int[massByElement.Count];
			var massKg = new float[massByElement.Count];
			var moles = new float[massByElement.Count];
			float totalMoles = 0f;
			int i = 0;
			foreach (KeyValuePair<int, float> entry in massByElement)
			{
				elementIdx[i] = entry.Key;
				massKg[i] = entry.Value;
				moles[i] = MolesOf(entry.Key, entry.Value);
				totalMoles += moles[i];
				i++;
			}

			var contentsOut = new PipeSpecies[massByElement.Count];
			for (int k = 0; k < contentsOut.Length; k++)
			{
				contentsOut[k] = new PipeSpecies(elementIdx[k], massKg[k], moles[k],
					totalMoles > 0f ? moles[k] / totalMoles : 0f);
			}
			state.Contents = contentsOut;
			state.TotalMoles = totalMoles;

			if (contentType == PipeContentType.Gas)
			{
				float networkVolumeM3 = cells.Length * tileVolumeM3;
				if (GasMixtureFacade.TryComputePressure(elementIdx, massKg, state.TemperatureK,
					networkVolumeM3, out float pressurePa))
				{
					state.PressurePa = pressurePa;
				}
			}
			else
			{
				// Volume-tracked, not ideal-gas: each species occupies its own real density's
				// worth of the run's capacity. Density comes from Mod 2's registry, falling back
				// to water-equivalent for an unregistered substance the same way
				// LiquidMixtureTankComponent already does -- vanilla ONI's Element has no
				// density field at all to fall through to.
				float occupiedM3 = 0f;
				for (int k = 0; k < contentsOut.Length; k++)
				{
					occupiedM3 += GasMixtureFacade.LiquidVolumeFromMass(contentsOut[k].MassKg,
						LiquidDensityOf(contentsOut[k].ElementIdx));
				}
				state.LiquidVolumeLitres = occupiedM3 * LitresPerCubicMetre;
				float capacityM3 = cells.Length * tileVolumeM3;
				state.FillFraction = capacityM3 > 0f ? occupiedM3 / capacityM3 : 0f;
			}

			return true;
		}

		/// <summary>
		/// Stationeers' pipe-stress evaluation against an already-read
		/// <see cref="PipeNetworkState"/>. Returns <c>false</c> when the network is fine, so a
		/// caller can treat "no report" as "clear this warning."
		///
		/// Only the WORST condition is reported -- Stationeers accumulates several
		/// <c>PipeBurst</c> flags at once, but a notification list wants one reason, and a
		/// network that is simultaneously overpressured and freezing has one thing the player
		/// should be told first. Kinds are ranked by <see cref="PipeStressReport.Severity"/>,
		/// which is normalised across units precisely so that comparison is meaningful.
		///
		/// Worst-tile selection, per kind (Stationeers' <c>_weakestMember</c> role):
		/// - Overpressure on a gas run: the tile whose own surrounding cell gives the largest
		///   differential, which is exactly the per-structure loop
		///   <c>ScanStructuresAndEvaluate</c> runs. A pipe crossing vacuum is under more strain
		///   than the identical pipe in a pressurised room, and this finds it.
		/// - Overfill on a liquid run: the run is uniform, so the first tile stands in.
		/// - Thermal: the tile sitting in the coldest (freezing/condensation) or hottest
		///   (vaporization) surroundings -- where the network is losing the fight first.
		/// </summary>
		public static bool TryEvaluateStress(PipeNetworkState state, out PipeStressReport report)
		{
			report = default;
			// CELLS, not contents. This used to bail on an empty Contents array, which quietly
			// made the whole evaluation unreachable for the one network state that most needs it:
			// a pipe whose contents have entirely condensed has no flowing species left, and it
			// is precisely then that it is full of standing liquid and closest to rupturing. The
			// tiers below each guard their own inputs -- the thermal one on a real temperature,
			// the pressure ones on a real pressure or fill -- so an empty run simply scores zero
			// on all of them instead of being refused a look.
			if (state == null || state.Cells == null || state.Cells.Length == 0)
			{
				return false;
			}

			PipeStressReport best = default;
			bool haveBest = false;

			if (state.ContentType == PipeContentType.Gas)
			{
				if (TryEvaluateGasOverpressure(state, out PipeStressReport pressureReport))
				{
					best = pressureReport;
					haveBest = true;
				}
			}
			else if (TryEvaluateLiquidOverfill(state, out PipeStressReport fillReport))
			{
				best = fillReport;
				haveBest = true;
			}

			if (TryEvaluateThermal(state, out PipeStressReport thermalReport) &&
				(!haveBest || thermalReport.Severity > best.Severity))
			{
				best = thermalReport;
				haveBest = true;
			}

			if (TryEvaluateMatterState(state, out PipeStressReport matterReport) &&
				(!haveBest || matterReport.Severity > best.Severity))
			{
				best = matterReport;
				haveBest = true;
			}

			if (!haveBest || best.Level == PipeStressLevel.None)
			{
				return false;
			}
			report = best;
			return true;
		}

		/// <summary>
		/// Convenience wrapper: read a network and evaluate it in one call, for a caller that
		/// only wants the warning. Returns <c>false</c> if there is no such network OR if it is
		/// healthy.
		/// </summary>
		public static bool TryEvaluateStress(int cell, PipeContentType contentType,
			out PipeStressReport report)
		{
			report = default;
			return TryGetNetworkState(cell, contentType, out PipeNetworkState state) &&
				TryEvaluateStress(state, out report);
		}

		private static bool TryEvaluateGasOverpressure(PipeNetworkState state,
			out PipeStressReport report)
		{
			report = default;

			int worstCell = state.Cells[0];
			float worstDelta = -1f;
			foreach (int c in state.Cells)
			{
				// A PIPE BURIED IN A SOLID TILE IS NOT EXPOSED TO ANYTHING, and scoring it as if
				// it were was a real defect -- user-reported as "the vent pipe blew again, same
				// spot," and it was the same spot every time for a reason.
				//
				// AmbientPressurePa returns 0 for any cell that is not holding gas, which is
				// correct for vacuum and wrong for solid. A conduit crossing a wall (an ONI Window
				// Tile or Insulation Tile makes its cell solid) therefore scored the maximum
				// possible differential -- full network pressure against nothing -- so it was
				// permanently the network's weakest member and always the tile that burst, however
				// modest the pressure elsewhere.
				//
				// Physically it is the reverse: a pipe embedded in a tile is SUPPORTED by it.
				// Stationeers' rule is about a pipe's differential against the atmosphere it is
				// exposed to, and a buried pipe is exposed to none. Solid cells are skipped for
				// the weakest-member pick, with a fallback below for the degenerate case where a
				// run is buried along its whole length and something still has to be nominated.
				if (Grid.Solid[c])
				{
					continue;
				}

				float delta = Mathf.Abs(state.PressurePa - AmbientPressurePa(c));
				if (delta > worstDelta)
				{
					worstDelta = delta;
					worstCell = c;
				}
			}

			if (worstDelta < 0f)
			{
				// Every tile of the run is buried. Nothing is exposed, so nothing is preferentially
				// weak; fall back to scoring the run's first tile so a genuinely over-rated network
				// is still reported rather than silently exempt.
				worstCell = state.Cells[0];
				worstDelta = Mathf.Abs(state.PressurePa - AmbientPressurePa(worstCell));
			}
			if (worstDelta < 0f)
			{
				return false;
			}

			float severity = worstDelta / MaxGasPipePressurePa;
			PipeStressLevel level = LevelFor(severity);
			if (level == PipeStressLevel.None)
			{
				return false;
			}
			report = new PipeStressReport(level, PipeStressKind.Overpressure, worstCell, -1,
				worstDelta, MaxGasPipePressurePa, severity);
			return true;
		}

		private static bool TryEvaluateLiquidOverfill(PipeNetworkState state,
			out PipeStressReport report)
		{
			report = default;
			float severity = state.FillFraction;
			PipeStressLevel level = LevelFor(severity);
			if (level == PipeStressLevel.None)
			{
				return false;
			}
			report = new PipeStressReport(level, PipeStressKind.Overpressure, state.Cells[0], -1,
				state.FillFraction, 1f, severity);
			return true;
		}

		private static bool TryEvaluateThermal(PipeNetworkState state, out PipeStressReport report)
		{
			report = default;
			if (state.TemperatureK <= 0f)
			{
				return false;
			}

			bool haveWorst = false;
			PipeStressKind worstKind = PipeStressKind.None;
			int worstElement = -1;
			float worstThreshold = 0f;
			float worstSeverity = 0f;

			foreach (PipeSpecies species in state.Contents)
			{
				Element element = ElementLoader.elements[species.ElementIdx];
				if (element == null)
				{
					continue;
				}

				// Freezing first: it is the hard floor for both phases, and Stationeers gates it
				// on a FLAT threshold rather than the vapor curve -- an easy distinction to miss.
				if (MaterialPropertyRegistry.TryGetFreezingTemperatureK(element.id,
					out float freezingK))
				{
					float severity = ThermalSeverity(freezingK - state.TemperatureK, freezingK);
					if (severity > worstSeverity)
					{
						worstSeverity = severity;
						worstKind = PipeStressKind.Freezing;
						worstElement = species.ElementIdx;
						worstThreshold = freezingK;
						haveWorst = true;
					}
				}

				// Phase boundary at the network's OWN pressure, via the clamped Antoine curve --
				// the raw curve is only meaningful inside its fitted range, which is why
				// Stationeers itself never calls anything but the clamped form.
				if (MaterialPropertyRegistry.TryGetEvaporationTemperatureClampedK(element.id,
					state.PressurePa, out float boundaryK))
				{
					float margin = state.ContentType == PipeContentType.Gas
						? boundaryK - state.TemperatureK   // gas too cold -> condenses
						: state.TemperatureK - boundaryK;  // liquid too hot -> boils
					float severity = ThermalSeverity(margin, boundaryK);
					if (severity > worstSeverity)
					{
						worstSeverity = severity;
						worstKind = state.ContentType == PipeContentType.Gas
							? PipeStressKind.Condensation
							: PipeStressKind.Vaporization;
						worstElement = species.ElementIdx;
						worstThreshold = boundaryK;
						haveWorst = true;
					}
				}
			}

			if (!haveWorst)
			{
				return false;
			}
			PipeStressLevel level = LevelFor(worstSeverity);
			if (level == PipeStressLevel.None)
			{
				return false;
			}

			bool wantCold = worstKind != PipeStressKind.Vaporization;
			int worstCell = state.Cells[0];
			float extreme = Grid.Temperature[worstCell];
			foreach (int c in state.Cells)
			{
				float ambient = Grid.Temperature[c];
				if (wantCold ? ambient < extreme : ambient > extreme)
				{
					extreme = ambient;
					worstCell = c;
				}
			}

			report = new PipeStressReport(level, worstKind, worstCell, worstElement,
				state.TemperatureK, worstThreshold, worstSeverity);
			return true;
		}

		/// <summary>
		/// The hazard measured on what the network is ACTUALLY HOLDING rather than on where its
		/// contents sit against a phase boundary -- Stationeers'
		/// <c>AtmosphericsNetwork.EvaluateIncorrectMatterState</c>, which is a scan of standing
		/// matter and not a temperature comparison at all.
		///
		/// WHY THIS EXISTS SEPARATELY FROM <see cref="TryEvaluateThermal"/>. The thermal
		/// tier reads <see cref="PipeNetworkState.Contents"/>, which is the FLOWING contents --
		/// what <c>ConduitFlow</c> holds. Condensate does not live there: it is banked as standing
		/// matter (<see cref="PipeMatterFacade"/>) the moment it forms. So a pipe part-way through
		/// condensing warns correctly, because there is still gas in it to be below its dew point,
		/// and a pipe that has finished condensing reports HEALTHY, because there is no gas left
		/// to have an opinion about. The live case: twelve tiles holding 2.400 kg of liquid oxygen
		/// at 0.01875 of the 0.02 damage ratio -- one small top-up short of rupturing -- and a
		/// stress level of None.
		///
		/// It is also the tier that finally makes the WARNING and the DAMAGE read the same
		/// quantity. <c>EvaluateMatterStateDamage</c> has always priced these two hazards off
		/// standing matter; until now nothing warned about the thing that was being charged for.
		///
		/// THE TWO BRANCHES ARE STATIONEERS' OWN, with its own scoping:
		///   - LIQUID, gas networks only. A liquid line is supposed to be full of liquid, so
		///     Stationeers makes Liquid and All networks explicit no-ops.
		///   - SOLID, every network type. Ice plugs a pipe whatever the pipe was carrying.
		/// The worse of the two is reported, not both, for the same reason
		/// <see cref="TryEvaluateStress"/> reports one kind overall: a notification list wants
		/// one reason, and a pipe full of ice should not be told twice that it is also full of
		/// liquid.
		///
		/// SEVERITY IS THE RATIO TO THE DAMAGE THRESHOLD, so 1.0 means "damage starts here" in
		/// exactly the sense every other kind's severity means it, and
		/// <see cref="StressedRatio"/> gives the warning band its usual 80 % lead-in. A run at
		/// 0.01875 of the 0.02 ratio scores 0.94 and warns as <see cref="PipeStressLevel.Stressed"/>
		/// without being damaged, which is the correct reading of a pipe that is one top-up from
		/// failing.
		///
		/// The worst tile is the one holding the most standing matter -- the physical analogue of
		/// the thermal tier's "where the network is losing the fight first", and the tile a player
		/// following the notification most wants to be shown.
		/// </summary>
		private static bool TryEvaluateMatterState(PipeNetworkState state,
			out PipeStressReport report)
		{
			report = default;
			if (state.Cells == null || state.Cells.Length == 0)
			{
				return false;
			}

			bool gasConduit = state.ContentType != PipeContentType.Liquid;
			float volumeLitres = state.Cells.Length
				* (gasConduit ? GasMixtureFacade.GasConduitVolumeM3
					: GasMixtureFacade.LiquidConduitVolumeM3)
				* LitresPerCubicMetre;
			if (volumeLitres <= 0f)
			{
				return false;
			}

			PipeStressKind kind = PipeStressKind.None;
			float severity = 0f;
			float value = 0f;
			float limit = 0f;

			float frozenMoles = PipeMatterFacade.TrappedFrozenMoles(gasConduit, state.Cells);
			float minFrozenMoles = MinFrozenMolesToDamage(volumeLitres);
			if (frozenMoles > 0f && minFrozenMoles > 0f)
			{
				severity = frozenMoles / minFrozenMoles;
				kind = PipeStressKind.StandingSolid;
				value = frozenMoles;
				limit = minFrozenMoles;
			}

			if (gasConduit)
			{
				float liquidLitres = PipeMatterFacade.TrappedLiquidLitres(gasConduit, state.Cells);
				float liquidSeverity =
					liquidLitres / volumeLitres / LiquidInGasPipeDamageRatio;
				if (liquidSeverity > severity)
				{
					severity = liquidSeverity;
					kind = PipeStressKind.StandingLiquid;
					value = liquidLitres;
					limit = volumeLitres * LiquidInGasPipeDamageRatio;
				}
			}

			PipeStressLevel level = LevelFor(severity);
			if (kind == PipeStressKind.None || level == PipeStressLevel.None)
			{
				return false;
			}

			// Whichever tile is holding the most of it. Reported as the standing element rather
			// than as one of the network's flowing species, because on the case this tier exists
			// for there are no flowing species left at all.
			int worstCell = state.Cells[0];
			int worstElement = -1;
			float worstMassKg = -1f;
			foreach (int c in state.Cells)
			{
				if (!PipeMatterFacade.TryGet(gasConduit, c,
						out PipeMatterFacade.TrappedMatter matter) ||
					matter.MassKg <= worstMassKg)
				{
					continue;
				}
				worstMassKg = matter.MassKg;
				worstCell = c;
				worstElement = matter.ElementIdx;
			}

			report = new PipeStressReport(level, kind, worstCell, worstElement, value, limit,
				severity);
			return true;
		}

		/// <summary>
		/// The warning band for a boundary at <paramref name="boundaryK"/>: the
		/// <see cref="ThermalStressMarginFraction"/> of it, but never less than the
		/// <see cref="ThermalStressMarginK"/> floor. Public because a caller solving for a
		/// temperature inside or outside the band (the pipe-stress harness does exactly this)
		/// must use the same number the tiering does, not a copy of it that can drift.
		/// </summary>
		public static float ThermalStressMarginFor(float boundaryK)
		{
			float proportional = Mathf.Abs(boundaryK) * ThermalStressMarginFraction;
			return Mathf.Max(ThermalStressMarginK, proportional);
		}

		/// <summary>
		/// Maps a temperature margin (K past, or short of, a phase boundary) onto the same
		/// 0..1+ severity scale the pressure path uses, so the two are rankable and share one
		/// tiering rule. At the boundary exactly, severity is 1 (Critical);
		/// <see cref="ThermalStressMarginFor"/> away from it on the safe side, severity is
		/// <see cref="StressedRatio"/> (the bottom of the warning band). Further away than that
		/// falls off linearly toward 0 and never warns.
		///
		/// Takes the boundary as well as the margin because the band is now
		/// proportional to it -- see <see cref="ThermalStressMarginFraction"/> for why a flat
		/// band was the wrong shape.
		/// </summary>
		private static float ThermalSeverity(float marginK, float boundaryK)
		{
			if (marginK >= 0f)
			{
				return 1f;
			}
			float band = ThermalStressMarginFor(boundaryK);
			float distance = -marginK;
			if (distance >= band)
			{
				// Keep decaying rather than snapping to 0, so callers ranking several healthy
				// networks still get a meaningful ordering.
				return StressedRatio * band / (distance + band);
			}
			return 1f - (1f - StressedRatio) * (distance / band);
		}

		private static PipeStressLevel LevelFor(float severity)
		{
			if (severity >= 1f)
			{
				return PipeStressLevel.Critical;
			}
			if (severity >= StressedRatio)
			{
				return PipeStressLevel.Stressed;
			}
			return PipeStressLevel.None;
		}

		/// <summary>
		/// Pressure of the world cell a conduit tile sits in -- the "external" half of
		/// Stationeers' differential. Prefers this project's own gas-mixture layer when the cell
		/// has been promoted to it (a real multi-species pressure); otherwise derives one from
		/// vanilla's single-element cell mass through the same native formula, so both paths
		/// produce the same kind of number. A solid or empty cell is 0 Pa, which is correct:
		/// a pipe buried in rock or crossing vacuum genuinely has nothing pushing back.
		/// </summary>
		public static float AmbientPressurePa(int cell)
		{
			if (!Grid.IsValidCell(cell))
			{
				return 0f;
			}
			if (GasMixtureFacade.TryGetPressure(cell, out float mixturePressurePa))
			{
				return mixturePressurePa;
			}
			Element element = Grid.Element[cell];
			float massKg = Grid.Mass[cell];
			if (element == null || !element.IsGas || massKg <= 0f)
			{
				return 0f;
			}
			int idx = ElementLoader.elements.IndexOf(element);
			if (idx < 0)
			{
				return 0f;
			}
			return GasMixtureFacade.TryComputePressure(new[] { idx }, new[] { massKg },
				Grid.Temperature[cell], LiveCellVolumeM3, out float pressurePa)
				? pressurePa
				: 0f;
		}

		/// <summary>
		/// Moles of <paramref name="massKg"/> of the substance at
		/// <paramref name="elementIdx"/>, from Mod 2's registry MOLECULAR mass, falling back to
		/// Klei's own <c>Element.molarMass</c> for an unregistered substance. The fallback is
		/// knowingly wrong by a factor of 2 for an unregistered DIATOMIC (Klei stores atomic
		/// mass for those) -- it is a last resort so an unregistered species still prints
		/// something, not a supported path. Register the substance instead.
		/// </summary>
		public static float MolesOf(int elementIdx, float massKg)
		{
			if (massKg <= 0f || elementIdx < 0 || elementIdx >= ElementLoader.elements.Count)
			{
				return 0f;
			}
			Element element = ElementLoader.elements[elementIdx];
			if (element == null)
			{
				return 0f;
			}
			if (MaterialPropertyRegistry.TryGetMolecularMassGPerMol(element.id,
				out float molecularMassGPerMol) && molecularMassGPerMol > 0f)
			{
				return massKg * GramsPerKilogram / molecularMassGPerMol;
			}
			return element.molarMass > 0f ? massKg * GramsPerKilogram / element.molarMass : 0f;
		}

		private static float LiquidDensityOf(int elementIdx)
		{
			if (elementIdx < 0 || elementIdx >= ElementLoader.elements.Count)
			{
				return LiveFallbackLiquidDensityKgM3;
			}
			Element element = ElementLoader.elements[elementIdx];
			if (element == null)
			{
				return LiveFallbackLiquidDensityKgM3;
			}
			return MaterialPropertyRegistry.TryGetLiquidDensityKgPerM3(element.id,
				out float densityKgM3) && densityKgM3 > 0f
				? densityKgM3
				: LiveFallbackLiquidDensityKgM3;
		}

		/// <summary>
		/// Water-equivalent density for a liquid with no registry record. Vanilla ONI's
		/// <c>Element</c> has no density field at all, so there is nothing to fall through to;
		/// this is the same fallback <c>LiquidMixtureTankComponent</c> already uses, hoisted so
		/// the two agree. The stock value of
		/// <see cref="SimTunable.FallbackConduitLiquidDensityKgM3"/>, the sim's fallback for the
		/// same pipe contents, which this class reads live.
		/// </summary>
		public const float FallbackLiquidDensityKgM3 = 1000f;
	}
}
