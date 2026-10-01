using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// STANDING CONDENSED OR BOILED-OFF MATTER INSIDE A CONDUIT, and the pressure-driven phase
	/// change that produces it.
	///
	/// WHY THIS IS A FRAMEWORK CAPABILITY AND NOT MOD 1'S PRIVATE STATE. ONI's conduit model is
	/// one element, one mass, one temperature per tile: a `ConduitFlow.ConduitContents` literally
	/// cannot represent "mostly steam plus a little condensed water," so ONI has nowhere to put a
	/// two-phase pipe. Stationeers, by contrast, gives an entire network ONE `Atmosphere` holding
	/// gas moles and condensed liquid moles together at a single temperature -- which is what
	/// makes its whole phase-change cooling loop expressible in the first place. This class is
	/// the side-car that closes that gap: a per-tile store of matter the conduit itself cannot
	/// hold. Every mechanic that follows from a two-phase pipe -- condensation valves, purge
	/// valves, evaporator lines, boil-off management -- needs it, so it belongs where a
	/// third-party mod can reach it rather than buried in a gameplay mod.
	///
	/// The split is deliberate: this file answers "what is in the pipe and what does physics do
	/// to it"; a gameplay mod (the flagship thermodynamics mod's <c>PipeMatterState</c>, for
	/// example) answers "and what should that cost the player" -- rupture venting, damage rules,
	/// notification markers -- and forwards its storage calls here.
	///
	/// PERSISTENCE IS DELIBERATELY NOT HERE. The serialized mirror is a `KMonoBehaviour` on the
	/// conduit's own GameObject, and KSerialization keys a component by its type name, so moving
	/// that type between assemblies would silently invalidate every existing save. It stays in
	/// Mod 1 and hooks in through <see cref="MirrorHook"/> instead -- a consumer that wants
	/// persistence registers one; a consumer that does not gets a purely in-memory store and no
	/// error.
	///
	/// KEYED BY CELL AND SPLIT BY CONDUIT TYPE, because a single cell can legitimately host both
	/// a gas conduit and a liquid conduit -- two different `ObjectLayer`s over one tile.
	/// </summary>
	public static class PipeMatterFacade
	{
		private const float GramsPerKilogram = 1000f;

		/// <summary>
		/// The coldest temperature this facade will ever write into a conduit tile or into the
		/// trapped-matter store, in kelvin.
		///
		/// WHY NOT 0 K. Klei's <c>ConduitFlow.GetContents</c> logs <c>"unexpected temperature 0"</c>
		/// whenever <c>contents.mass &gt; 0f &amp;&amp; contents.temperature &lt;= 0f</c>. A
		/// <c>Mathf.Max(t + deltaK, 0f)</c> clamp lets an endothermic bill larger than the tile
		/// can pay land exactly on that floor, leaving real mass sitting at absolute zero; Klei only
		/// logs and hands the value back, so the 0 K would then propagate into every heat capacity,
		/// blend and pressure computed from that tile.
		///
		/// 1 K rather than 0 K, and the value has a provenance rather than being picked: it is the
		/// same floor the sim kernel's own <c>DeliverCellEnergy</c> applies
		/// (<c>if (next &lt;= 1.0f) next = 1.0f;</c> in the replacement SimDLL's
		/// <c>sim/buildings.h</c>), so
		/// the managed and native sides refuse the same temperatures.
		/// </summary>
		public const float MinimumConduitTemperatureK = 1f;

		// How many times a temperature write has been held at the floor, and the joules that
		// could not be delivered because of it. A clamp that silently eats energy in a project
		// whose premise is that energy is conserved is exactly the bug this class exists not to
		// have, so the arithmetic reports itself instead of being invisible. Reported once per
		// session, on the first occurrence, and then counted.
		private static int temperatureFloorHits;
		private static double temperatureFloorUnbilledJoules;

		/// <summary>How often a temperature write has been held at <see cref="MinimumConduitTemperatureK"/>.</summary>
		public static int TemperatureFloorHits => temperatureFloorHits;

		/// <summary>Joules that could not be delivered because a tile was already at the floor.</summary>
		public static double TemperatureFloorUnbilledJoules => temperatureFloorUnbilledJoules;

		private static void NoteTemperatureFloor(int cell, float fromK, float deltaK,
			float unbilledJoules)
		{
			temperatureFloorHits++;
			temperatureFloorUnbilledJoules += unbilledJoules;
			if (temperatureFloorHits == 1)
			{
				Debug.LogWarning(string.Format(
					"[OniFramework] PipeMatterFacade held a conduit tile at the {0:F1} K floor: "
					+ "cell {1} was {2:F2} K and a bill of {3:F2} K would have taken it below "
					+ "absolute zero. {4:F1} J could not be billed there and was passed on. "
					+ "Further occurrences are counted in TemperatureFloorHits rather than logged.",
					MinimumConduitTemperatureK, cell, fromK, deltaK, unbilledJoules));
			}
		}

		/// <summary>
		/// The coldest thing actually present in a tile -- its conduit contents, whatever is
		/// standing in it, or both -- which is the one that reaches
		/// <see cref="MinimumConduitTemperatureK"/> first and therefore the one a bill has to be
		/// tested against. <see cref="float.MaxValue"/> when the tile holds nothing.
		/// </summary>
		private static float ColdestTileTemperatureK(int cell, bool gasConduit, ConduitFlow flow)
		{
			float coldest = float.MaxValue;
			if (flow != null)
			{
				ConduitFlow.ConduitContents contents = flow.GetContents(cell);
				if (contents.mass > 0f && contents.element != SimHashes.Vacuum)
				{
					coldest = contents.temperature;
				}
			}
			if (TryGet(gasConduit, cell, out TrappedMatter matter) && matter.MassKg > 0f
				&& matter.TemperatureK < coldest)
			{
				coldest = matter.TemperatureK;
			}
			return coldest;
		}
		private const float LitresPerCubicMetre = 1000f;

		/// <summary>
		/// Matter sitting in one conduit tile that its `ConduitContents` cannot represent. The
		/// element is a real ONI element (LiquidOxygen, Ice, Steam, ...) so its own phase is read
		/// off `Element.IsLiquid` / `Element.IsGas` / `Element.IsSolid` rather than tracked
		/// separately.
		/// </summary>
		public struct TrappedMatter
		{
			public int ElementIdx;
			public float MassKg;
			public float TemperatureK;
		}

		private static readonly Dictionary<int, TrappedMatter> GasCells =
			new Dictionary<int, TrappedMatter>();

		private static readonly Dictionary<int, TrappedMatter> LiquidCells =
			new Dictionary<int, TrappedMatter>();

		/// <summary>
		/// Called after every mutation, with (isGasConduit, cell), so a consumer can write the
		/// tile's new state through to whatever it persists with. Null by default -- see the
		/// class doc for why persistence deliberately does not live here.
		/// </summary>
		public static Action<bool, int> MirrorHook;

		/// <summary>
		/// Running total of everything ever released into the world by a rupture, per conduit
		/// type. Exists for a test harness: asserting on world mass after a leak is timing-
		/// fragile -- liquid oxygen released at ~85 K into a ~290 K room flashes back to gas
		/// within a second or two, so a check that runs slightly late legitimately finds nothing.
		/// The release itself is the deterministic fact, so that is what gets counted.
		/// </summary>
		public static float ReleasedGasConduitKg { get; private set; }

		public static float ReleasedLiquidConduitKg { get; private set; }

		/// <summary>
		/// Running total of matter that has become SOLID inside a conduit, per conduit type.
		/// Same reasoning as the release counters: solid still HELD is fragile to assert on
		/// because the solid hazard damages hard enough to rupture the pipe and release it within
		/// the same test window. Formation is the fact.
		/// </summary>
		public static float SolidFormedGasConduitKg { get; private set; }

		public static float SolidFormedLiquidConduitKg { get; private set; }

		/// <summary>
		/// Total mass this store has condensed out of a gas network, and boiled out of a liquid
		/// network, since load. Added for the Condensation-Valve/Purge-Valve loop, where the
		/// question a probe actually needs answered is "is the pipe changing phase at all," and
		/// the standing total cannot answer it once a valve is draining as fast as the pipe
		/// converts.
		/// </summary>
		public static float CondensedInGasConduitKg { get; private set; }

		public static float BoiledInLiquidConduitKg { get; private set; }

		/// <summary>
		/// Total mass drained out of pipes by <see cref="TryDrain"/> -- what the valves have
		/// actually harvested, as opposed to what the pipes have produced.
		/// </summary>
		public static float DrainedFromGasConduitKg { get; private set; }

		public static float DrainedFromLiquidConduitKg { get; private set; }

		private static Dictionary<int, TrappedMatter> Store(bool gasConduit)
		{
			return gasConduit ? GasCells : LiquidCells;
		}

		private static void Mirror(bool gasConduit, int cell)
		{
			// The sim's copy first: every mutation of the store comes through here, so
			// this is the one place the native mirror can be kept exact. A no-op on a SimDLL
			// without the export, latched after the first miss.
			SimConduitNetworks.MirrorTrapped(gasConduit, cell);

			Action<bool, int> hook = MirrorHook;
			if (hook == null)
			{
				return;
			}
			try
			{
				hook(gasConduit, cell);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] PipeMatterFacade.MirrorHook threw; the in-memory "
					+ "store is still correct but this tile may not persist: " + e);
			}
		}

		/// <summary>Whatever is standing in this tile, if anything.</summary>
		public static bool TryGet(bool gasConduit, int cell, out TrappedMatter matter)
		{
			return Store(gasConduit).TryGetValue(cell, out matter);
		}

		/// <summary>
		/// Every tile currently holding something, as a snapshot -- a copy, because callers
		/// routinely mutate the store while walking it.
		/// </summary>
		public static IEnumerable<int> TrackedCells(bool gasConduit)
		{
			return new List<int>(Store(gasConduit).Keys);
		}

		/// <summary>
		/// Adds matter to a tile, blending temperature by HEAT CAPACITY rather than by mass --
		/// two masses of the same element blend the same either way, but a mixed tile (condensate
		/// that has partly frozen, say) would land on the wrong temperature under a plain mass
		/// average. Different elements replace rather than mix: a tile holds one trapped species,
		/// matching `ConduitContents`' own single-element shape, and in practice a pipe condenses
		/// one thing.
		/// </summary>
		public static void Add(bool gasConduit, int cell, int elementIdx, float massKg,
			float temperatureK)
		{
			if (massKg <= 0f || elementIdx < 0 || elementIdx >= ElementLoader.elements.Count)
			{
				return;
			}

			Dictionary<int, TrappedMatter> store = Store(gasConduit);
			if (store.TryGetValue(cell, out TrappedMatter existing) && existing.MassKg > 0f)
			{
				// A TILE ALREADY HOLDING SOMETHING KEEPS IT. This used to require the elements to
				// MATCH before merging, and fell through to the outright `store[cell] = new ...`
				// below when they did not -- which silently threw the existing entry away, mass
				// and all.
				//
				// The case is ordinary, not exotic. A tile condenses gas to liquid, its condensate
				// cools past the freezing point and UpdatePhase relabels it solid, and then more
				// gas condenses into the same tile: the store holds SolidOxygen, the arrival is
				// LiquidOxygen, and the solid was deleted. Measured on PipeStressDemo phase 6:
				// 2.400 kg written into the pipe, 2.202 kg standing at the end,
				// 0.05 kg accounted for in the world, and 0.15 kg gone. It stayed invisible for as
				// long as standing condensate was being released back into the pipe every sweep,
				// because nothing survived in the store long enough to change phase under a second
				// arrival.
				//
				// One entry per tile is the store's shape, so two phases in one tile have to
				// become one. They resolve to the phase already standing there, and the arriving
				// mass pays or is paid the latent heat of that transition -- which is the physical
				// answer for a substance meeting its own other phase, and the same billing
				// UpdatePhase uses when the whole tile crosses.
				int intoIdx = existing.ElementIdx;
				float addedMassKg = massKg;
				float addedTemperatureK = temperatureK;

				if (intoIdx != elementIdx)
				{
					Element held = ElementLoader.elements[intoIdx];
					Element arriving = ElementLoader.elements[elementIdx];
					if (IsSameSubstance(held, arriving))
					{
						// Solid gaining liquid RELEASES fusion heat; liquid gaining solid absorbs
						// it. Written as the physical statement rather than as a sign parameter.
						if (MaterialPropertyRegistry.TryGetLatentHeatOfFusionJPerKg(arriving.id,
								out float fusionJPerKg) && fusionJPerKg > 0f)
						{
							float joules = addedMassKg * fusionJPerKg;
							ConduitFlow flow = gasConduit
								? Game.Instance?.gasConduitFlow
								: Game.Instance?.liquidConduitFlow;
							if (flow != null)
							{
								BillLatentHeatToNetwork(cell, gasConduit, flow,
									held.IsSolid ? joules : -joules);
							}
						}
						if (held.IsSolid)
						{
							NoteSolidFormed(gasConduit, addedMassKg);
						}
					}
					else
					{
						// Two genuinely different substances in one tile. The store cannot express
						// that, and this is the one case with no honest answer -- but deleting one
						// of them is not it, so the mass is kept under the element already there
						// and the swap is made LOUD instead of silent. If this ever fires, the
						// store needs to become a list.
						Debug.LogWarning("[OniFramework] PipeMatterFacade: cell " + cell + " holds "
							+ ElementLoader.elements[intoIdx].tag + " and was handed "
							+ ElementLoader.elements[elementIdx].tag + "; keeping "
							+ addedMassKg.ToString("F4") + " kg as the former to conserve mass. "
							+ "A tile that can hold two substances at once needs a list here.");
					}
				}

				float cp = ElementLoader.elements[intoIdx].specificHeatCapacity;
				float hcExisting = existing.MassKg * cp;
				float hcAdded = addedMassKg * cp;
				float total = hcExisting + hcAdded;
				existing.TemperatureK = total > 0f
					? (existing.TemperatureK * hcExisting + addedTemperatureK * hcAdded) / total
					: addedTemperatureK;
				existing.MassKg += addedMassKg;
				store[cell] = existing;
				Mirror(gasConduit, cell);
				return;
			}

			if (ElementLoader.elements[elementIdx].IsSolid)
			{
				// Counts ice that arrives already solid -- a liquid conduit freezing produces it
				// directly rather than going through a liquid-to-solid transition here.
				NoteSolidFormed(gasConduit, massKg);
			}

			store[cell] = new TrappedMatter
			{
				ElementIdx = elementIdx,
				MassKg = massKg,
				TemperatureK = temperatureK
			};
			Mirror(gasConduit, cell);
		}

		/// <summary>
		/// Whether two elements are two phases of one substance, asked of Klei's own transition
		/// table rather than by matching names. Either being the other's low- or high-temperature
		/// transition is enough: the table is what every phase-change site in this project already
		/// navigates by, so nothing here has to know that liquid oxygen is called LiquidOxygen.
		/// </summary>
		private static bool IsSameSubstance(Element a, Element b)
		{
			if (a == null || b == null)
			{
				return false;
			}
			return a.lowTempTransition == b || a.highTempTransition == b
				|| b.lowTempTransition == a || b.highTempTransition == a;
		}

		/// <summary>
		/// Replaces a tile's entry outright. For a caller that has already decided the tile's new
		/// state in full -- a phase transition relabelling the element, say -- where blending
		/// through <see cref="Add"/> would be wrong.
		/// </summary>
		public static void Set(bool gasConduit, int cell, TrappedMatter matter)
		{
			if (matter.MassKg <= 0f)
			{
				RemoveAll(gasConduit, cell, out TrappedMatter _);
				return;
			}
			Store(gasConduit)[cell] = matter;
			Mirror(gasConduit, cell);
		}

		/// <summary>
		/// Takes a tile's entire entry out of the store and hands it to the caller, who then owns
		/// the mass. Returns false, and nothing is removed, when the tile held nothing.
		/// </summary>
		public static bool RemoveAll(bool gasConduit, int cell, out TrappedMatter matter)
		{
			Dictionary<int, TrappedMatter> store = Store(gasConduit);
			if (!store.TryGetValue(cell, out matter))
			{
				return false;
			}
			store.Remove(cell);
			Mirror(gasConduit, cell);
			return true;
		}

		/// <summary>
		/// Takes up to <paramref name="maxMassKg"/> of whatever is standing in a tile -- the
		/// primitive every harvesting device is built on (Stationeers' Condensation Valve draining
		/// liquid out of a gas line, its Purge Valve draining boil-off gas out of a liquid line).
		///
		/// PARTIAL BY DESIGN. Stationeers' condensation valve moves at most
		/// `min(pipe volume, sourceLiquidVolume / 2)` per tick and leaves the rest, which
		/// is why its own guide tells players to build many of them along one line. A drain that
		/// emptied a tile outright would collapse that whole design decision into a single
		/// building, so the cap is the caller's to set and this honours it exactly.
		///
		/// The remainder keeps its temperature: removing mass from a body at uniform temperature
		/// does not change the temperature of what is left.
		/// </summary>
		public static bool TryDrain(bool gasConduit, int cell, float maxMassKg,
			out int elementIdx, out float massKg, out float temperatureK)
		{
			elementIdx = -1;
			massKg = 0f;
			temperatureK = 0f;

			if (maxMassKg <= 0f)
			{
				return false;
			}

			Dictionary<int, TrappedMatter> store = Store(gasConduit);
			if (!store.TryGetValue(cell, out TrappedMatter matter) || matter.MassKg <= 0f)
			{
				return false;
			}

			elementIdx = matter.ElementIdx;
			temperatureK = matter.TemperatureK;
			massKg = Mathf.Min(maxMassKg, matter.MassKg);

			float remainder = matter.MassKg - massKg;
			if (remainder > 0f)
			{
				matter.MassKg = remainder;
				store[cell] = matter;
			}
			else
			{
				store.Remove(cell);
			}
			Mirror(gasConduit, cell);

			if (gasConduit)
			{
				DrainedFromGasConduitKg += massKg;
			}
			else
			{
				DrainedFromLiquidConduitKg += massKg;
			}

			return massKg > 0f;
		}

		/// <summary>
		/// Records a rupture release. The store itself is not touched -- the caller that expelled
		/// the mass into the world owns that -- this only keeps the counter honest.
		/// </summary>
		public static void NoteReleased(bool gasConduit, float massKg)
		{
			if (massKg <= 0f)
			{
				return;
			}
			if (gasConduit)
			{
				ReleasedGasConduitKg += massKg;
			}
			else
			{
				ReleasedLiquidConduitKg += massKg;
			}
		}

		/// <summary>
		/// Records that mass became solid inside a conduit. Public because Mod 1's own
		/// freeze/melt transition happens outside this class and must still be counted.
		/// </summary>
		public static void NoteSolidFormed(bool gasConduit, float massKg)
		{
			if (massKg <= 0f)
			{
				return;
			}
			if (gasConduit)
			{
				SolidFormedGasConduitKg += massKg;
			}
			else
			{
				SolidFormedLiquidConduitKg += massKg;
			}
		}

		/// <summary>
		/// The trapped matter's own heat capacity (J/K) on this tile, or 0 if the tile holds none.
		/// Exists so a caller distributing energy across a whole network can weight this tile
		/// correctly: Stationeers' `Atmosphere` holds the condensed liquid and the gas in ONE body
		/// at ONE temperature, so the condensate is part of what absorbs a latent-heat release,
		/// not a bystander to it. See <see cref="BillLatentHeatToNetwork(int[], bool, ConduitFlow, float)"/>.
		/// </summary>
		public static float HeatCapacityJPerK(bool gasConduit, int cell)
		{
			Dictionary<int, TrappedMatter> store = Store(gasConduit);
			if (!store.TryGetValue(cell, out TrappedMatter matter) || matter.MassKg <= 0f)
			{
				return 0f;
			}

			return matter.MassKg
				* ElementLoader.elements[matter.ElementIdx].specificHeatCapacity
				* GramsPerKilogram;
		}

		/// <summary>
		/// Shifts this tile's trapped matter by <paramref name="deltaK"/>. The caller owns the
		/// energy accounting -- this only applies an already-computed temperature change, which is
		/// why it takes a delta rather than a target: a network-wide distribution produces a
		/// UNIFORM delta, and applying it as a delta is what preserves the per-tile temperature
		/// differences ONI has and Stationeers (one temperature per network) does not.
		/// </summary>
		public static void AddTemperatureK(bool gasConduit, int cell, float deltaK)
		{
			if (deltaK == 0f)
			{
				return;
			}

			Dictionary<int, TrappedMatter> store = Store(gasConduit);
			if (!store.TryGetValue(cell, out TrappedMatter matter) || matter.MassKg <= 0f)
			{
				return;
			}

			// Floored at MinimumConduitTemperatureK, not at 0: standing matter is mass like any
			// other and absolute zero is not a state it can be in. See that constant for the
			// live failure this floor exists to stop.
			matter.TemperatureK =
				Mathf.Max(matter.TemperatureK + deltaK, MinimumConduitTemperatureK);
			store[cell] = matter;
			Mirror(gasConduit, cell);
		}

		/// <summary>
		/// Total trapped LIQUID volume across a run, in litres -- the numerator of Stationeers'
		/// `TotalVolumeLiquids / Volume`. Solid matter is deliberately excluded: Stationeers
		/// prices those two hazards separately and so does this.
		/// </summary>
		public static float TrappedLiquidLitres(bool gasConduit, IEnumerable<int> cells)
		{
			float litres = 0f;
			foreach (int cell in cells)
			{
				if (!TryGet(gasConduit, cell, out TrappedMatter matter) || matter.MassKg <= 0f)
				{
					continue;
				}
				Element element = ElementLoader.elements[matter.ElementIdx];
				if (!element.IsLiquid)
				{
					continue;
				}
				if (MaterialPropertyRegistry.TryGetLiquidDensityKgPerM3(element.id,
					out float density) && density > 0f)
				{
					litres += matter.MassKg / density * LitresPerCubicMetre;
				}
			}
			return litres;
		}

		/// <summary>
		/// Total trapped GAS mass across a run, in kilograms -- the quantity a Purge Valve exists
		/// to remove, and the quantity that sets a liquid line's headspace pressure. The mirror of
		/// <see cref="TrappedLiquidLitres"/>, in mass rather than volume because a gas's volume is
		/// whatever the headspace gives it.
		/// </summary>
		public static float TrappedGasKg(bool gasConduit, IEnumerable<int> cells)
		{
			float kg = 0f;
			foreach (int cell in cells)
			{
				if (!TryGet(gasConduit, cell, out TrappedMatter matter) || matter.MassKg <= 0f)
				{
					continue;
				}
				if (ElementLoader.elements[matter.ElementIdx].IsGas)
				{
					kg += matter.MassKg;
				}
			}
			return kg;
		}

		/// <summary>
		/// Total trapped SOLID matter across a run, in moles -- what Stationeers compares against
		/// `MinFrozenMolesToDamage()`. Uses the registry's MOLECULAR mass, not Klei's
		/// `Element.molarMass`, which is atomic for the diatomics.
		/// </summary>
		public static float TrappedFrozenMoles(bool gasConduit, IEnumerable<int> cells)
		{
			float moles = 0f;
			foreach (int cell in cells)
			{
				if (!TryGet(gasConduit, cell, out TrappedMatter matter) || matter.MassKg <= 0f)
				{
					continue;
				}
				Element element = ElementLoader.elements[matter.ElementIdx];
				if (!element.IsSolid)
				{
					continue;
				}
				if (MaterialPropertyRegistry.TryGetMolecularMassGPerMol(element.id,
					out float molecularMass) && molecularMass > 0f)
				{
					moles += matter.MassKg * GramsPerKilogram / molecularMass;
				}
			}
			return moles;
		}

		/// <summary>
		/// Spreads <paramref name="joules"/> of latent heat across a WHOLE conduit network --
		/// every tile's contents and every tile's standing trapped matter -- in proportion to heat
		/// capacity, which is to say as one uniform temperature rise.
		///
		/// WHY NETWORK-WIDE. A Stationeers pipe network owns exactly one atmosphere, which holds
		/// the gas moles and the condensed liquid moles together at a single temperature. Its state
		/// change therefore bills a transition's latent heat against that whole shared body, and it
		/// only ever reaches the world afterwards, through a separate convection stage.
		///
		/// ONI has no such body. <see cref="GasMixtureFacade.ComputePhaseChangeStep"/> bills the
		/// remainder of the ONE TILE it was called for, and when a converting tile converts
		/// everything there is no remainder at all, so the heat lands on the product itself. That
		/// is a real number, not a rounding artifact: Oxygen's latent heat is 800 J/mol over
		/// 31.9988 g/mol = 25.0 kJ/kg against LiquidOxygen's specific heat of 1.01, so the
		/// condensate came out 24.75 K hotter than the gas that made it, which put ONI's solid
		/// hazard effectively out of reach. The data was never the problem; the DESTINATION was.
		///
		/// Applied as a uniform DELTA rather than a uniform temperature on purpose. Stationeers
		/// homogenises -- one network, one temperature -- but ONI's conduit contents are genuinely
		/// per-tile and carry real gradients the rest of the game depends on. Distributing the
		/// energy in proportion to heat capacity gives every tile the same rise while leaving
		/// those gradients intact.
		///
		/// SIGN CONVENTION: positive <paramref name="joules"/> is heat RELEASED into the network
		/// (condensing, freezing) and negative is heat ABSORBED out of it (evaporating, melting).
		/// Both directions matter and both must be billed. A transition charged in one direction
		/// and free in the other is not an approximation, it is a pump.
		///
		/// Returns false when there was nothing in the network able to absorb the energy, so a
		/// caller can fall back rather than silently lose it.
		/// </summary>
		public static bool BillLatentHeatToNetwork(int[] cells, bool gasConduit, ConduitFlow flow,
			float joules)
		{
			if (cells == null || cells.Length == 0 || joules == 0f || flow == null)
			{
				return false;
			}

			float totalHeatCapacityJPerK = 0f;
			for (int i = 0; i < cells.Length; i++)
			{
				totalHeatCapacityJPerK += NetworkTileHeatCapacityJPerK(cells[i], gasConduit, flow);
			}

			if (totalHeatCapacityJPerK <= 0f)
			{
				return false;
			}

			float deltaK = joules / totalHeatCapacityJPerK;
			if (deltaK == 0f)
			{
				return true;
			}

			for (int i = 0; i < cells.Length; i++)
			{
				int cell = cells[i];
				ConduitFlow.ConduitContents contents = flow.GetContents(cell);
				if (contents.mass > 0f && contents.element != SimHashes.Vacuum)
				{
					float nextK = contents.temperature + deltaK;
					if (nextK < MinimumConduitTemperatureK)
					{
						// No further fallback exists here -- this IS the fallback, and billing the
						// residue to the network again would recurse. So it is counted rather than
						// redistributed, and the count is public.
						float tileHeatCapacityJPerK =
							NetworkTileHeatCapacityJPerK(cell, gasConduit, flow);
						NoteTemperatureFloor(cell, contents.temperature, deltaK,
							(nextK - MinimumConduitTemperatureK) * tileHeatCapacityJPerK);
						nextK = MinimumConduitTemperatureK;
					}
					flow.SetContents(cell, new ConduitFlow.ConduitContents(contents.element,
						contents.mass, nextK, contents.diseaseIdx, contents.diseaseCount));
				}

				AddTemperatureK(gasConduit, cell, deltaK);
			}

			return true;
		}

		/// <summary>
		/// Bills a phase change's latent heat WHERE IT HAPPENED: this tile, and only if this tile
		/// cannot take it, the network as a whole.
		///
		/// THIS IS THE ONE THAT CALL SITES DOING PHASE CHANGE SHOULD USE, and billing the whole
		/// network instead stops a refrigeration loop from moving any heat at all. Measured, with
		/// the loop otherwise working perfectly: 0.2616 kg
		/// boiled in the cold room and 0.2632 kg condensed, and both rooms ended within a quarter
		/// of a kelvin of where they started.
		///
		/// The reason is that a conduit network is not a thermodynamic body. That rig's gas run was
		/// one network of 19 tiles that SPANNED BOTH ROOMS -- 11 in the hot room, 8 in the cold
		/// room's exhaust -- and its liquid run likewise spanned the cold room and the sub-cool leg
		/// in the hot room. Any network that crosses two rooms smears them. Billing across the network
		/// therefore deposited part of the heat
		/// released by condensing in the hot room into the cold room, and took part of the heat
		/// absorbed by boiling in the cold room out of the hot room. The two ends of the cycle were
		/// smeared into each other and cancelled: exactly the amount of cooling the evaporator
		/// produced was handed back by the condenser through the shared pipe run.
		///
		/// Stationeers does not have this problem because its state change bills against the one
		/// atmosphere the gas is actually in -- a pipe segment or a device's internal volume --
		/// never a whole network. A per-tile deposit is ONI's equivalent of that, and it is what
		/// makes a heat pump a heat pump: heat leaves one place and arrives somewhere else.
		///
		/// The fallback matters. When a tile has just condensed everything it held it can have no
		/// heat capacity left, and dropping the joules there would destroy energy in a project
		/// whose whole premise is that it is conserved. In that case, and only then, this falls
		/// back to the network-wide distribution.
		/// </summary>
		public static bool BillLatentHeatToCell(int cell, bool gasConduit, ConduitFlow flow,
			float joules)
		{
			if (flow == null || joules == 0f)
			{
				return false;
			}

			float localHeatCapacityJPerK = NetworkTileHeatCapacityJPerK(cell, gasConduit, flow);
			if (localHeatCapacityJPerK > 0f)
			{
				float deltaK = joules / localHeatCapacityJPerK;

				// A TILE CANNOT PAY A BILL THAT WOULD TAKE IT BELOW ABSOLUTE ZERO, and clamping
				// the write is not the same as refusing the bill. The first version of this did
				// clamp -- `Mathf.Max(t + deltaK, 0f)` -- which left real mass sitting at exactly
				// 0 K (Klei's own `GetContents` shouts `unexpected temperature 0` about it) AND
				// destroyed the joules the clamp swallowed. Both halves are fixed here: the tile
				// is billed only as far as the floor, and the remainder goes on to the network
				// distribution below, which is the escape hatch this method already had for a
				// tile that cannot pay.
				float coldestK = ColdestTileTemperatureK(cell, gasConduit, flow);
				float unbilledJoules = 0f;
				if (coldestK < float.MaxValue
					&& coldestK + deltaK < MinimumConduitTemperatureK)
				{
					float affordableDeltaK = MinimumConduitTemperatureK - coldestK;
					unbilledJoules = joules - affordableDeltaK * localHeatCapacityJPerK;
					NoteTemperatureFloor(cell, coldestK, deltaK, unbilledJoules);
					deltaK = affordableDeltaK;
				}

				if (deltaK != 0f)
				{
					ConduitFlow.ConduitContents contents = flow.GetContents(cell);
					if (contents.mass > 0f && contents.element != SimHashes.Vacuum)
					{
						flow.SetContents(cell, new ConduitFlow.ConduitContents(contents.element,
							contents.mass,
							Mathf.Max(contents.temperature + deltaK, MinimumConduitTemperatureK),
							contents.diseaseIdx, contents.diseaseCount));
					}

					AddTemperatureK(gasConduit, cell, deltaK);
				}

				if (unbilledJoules != 0f)
				{
					return BillLatentHeatToNetwork(cell, gasConduit, flow, unbilledJoules);
				}
				return true;
			}

			return BillLatentHeatToNetwork(cell, gasConduit, flow, joules);
		}

		/// <summary>
		/// <see cref="BillLatentHeatToNetwork(int[], bool, ConduitFlow, float)"/> for a caller
		/// that holds a cell rather than a resolved network. Falls back to billing the single tile
		/// when the network cannot be resolved, so the energy still lands somewhere real.
		/// </summary>
		public static bool BillLatentHeatToNetwork(int cell, bool gasConduit, ConduitFlow flow,
			float joules)
		{
			PipeContentType contentType = gasConduit ? PipeContentType.Gas : PipeContentType.Liquid;
			int[] cells = PipeNetworkFacade.TryGetNetworkState(cell, contentType,
					out PipeNetworkState state) && state.Cells != null && state.Cells.Length > 0
				? state.Cells
				: new int[] { cell };

			return BillLatentHeatToNetwork(cells, gasConduit, flow, joules);
		}

		/// <summary>
		/// One tile's share of the network's heat capacity (J/K): the conduit's own contents plus
		/// whatever is standing in it, because in the body this models those are the same body.
		/// </summary>
		private static float NetworkTileHeatCapacityJPerK(int cell, bool gasConduit, ConduitFlow flow)
		{
			float heatCapacityJPerK = HeatCapacityJPerK(gasConduit, cell);

			ConduitFlow.ConduitContents contents = flow.GetContents(cell);
			if (contents.mass > 0f && contents.element != SimHashes.Vacuum)
			{
				Element element = ElementLoader.FindElementByHash(contents.element);
				if (element != null)
				{
					heatCapacityJPerK +=
						contents.mass * element.specificHeatCapacity * GramsPerKilogram;
				}
			}

			return heatCapacityJPerK;
		}

		/// <summary>
		/// The gas pressure (Pa) in a LIQUID network's headspace -- the volume its liquid does not
		/// occupy, holding whatever gas has boiled off or been deliberately injected as pressurant.
		///
		/// WHY A LIQUID NETWORK NEEDS A PRESSURE AT ALL, and why ONI cannot supply one.
		/// <see cref="PipeNetworkFacade.TryGetNetworkState"/> deliberately leaves
		/// <see cref="PipeNetworkState.PressurePa"/> at zero for a liquid run and reports a fill
		/// fraction instead, because ONI tracks liquids by volume and running the gas law over a
		/// liquid mass is meaningless. That is correct, and it is also exactly why a liquid line
		/// had no boiling point: the vapour curve is a function of pressure, so with no pressure
		/// there is no threshold, and nothing in a liquid pipe could ever boil.
		///
		/// The pressure that actually governs boiling in a real line is the pressure of the gas
		/// ABOVE the liquid, and this store is precisely where that gas lives. So: take the run's
		/// capacity, subtract the volume its liquid contents and any standing condensate occupy,
		/// and apply the gas law to the trapped gas in what is left.
		///
		/// This closes a control loop rather than just filling in a number, and it is the loop the
		/// Purge Valve exists to operate. Liquid boils, the gas it becomes raises the headspace
		/// pressure, the higher pressure raises the boiling point, and the boiling stops. Remove
		/// that gas and the pressure falls, the boiling point falls with it, and the line boils --
		/// and boiling absorbs latent heat, which is the cooling. A liquid line with no pressurant
		/// and no purge valve sits at zero pressure, which the clamp resolves to the fluid's
		/// freezing point, so it flashes to gas until it has pressurised itself. That is the
		/// Stationeers guide's own warning ("ensure the liquid network is pressurised otherwise
		/// this new liquid addition will evaporate and can rapidly cool to freezing") falling out
		/// of the model rather than being special-cased.
		///
		/// Returns false when the run has no headspace or no trapped gas -- a caller should treat
		/// that as zero pressure, not as an error.
		/// </summary>
		public static bool TryGetLiquidHeadspacePressurePa(PipeNetworkState state,
			out float pressurePa)
		{
			pressurePa = 0f;
			if (state == null)
			{
				return false;
			}
			return TryGetLiquidHeadspacePressurePa(state.Cells, state.LiquidVolumeLitres,
				out pressurePa);
		}

		/// <summary>
		/// Same measurement from a <see cref="PipeNetworkReading"/>, for the per-tick callers that
		/// read a network without allocating a state object for it.
		/// </summary>
		public static bool TryGetLiquidHeadspacePressurePa(PipeNetworkReading reading,
			out float pressurePa)
		{
			return TryGetLiquidHeadspacePressurePa(reading.Cells, reading.LiquidVolumeLitres,
				out pressurePa);
		}

		/// <summary>
		/// The measurement itself, in terms of the only two things it needs: the run's tiles and
		/// how much of the run the network's own liquid already occupies. Both callers above hand
		/// those over from whichever shape they happen to hold.
		/// </summary>
		private static bool TryGetLiquidHeadspacePressurePa(int[] cells,
			float liquidVolumeLitres, out float pressurePa)
		{
			pressurePa = 0f;
			if (cells == null || cells.Length == 0)
			{
				return false;
			}

			float capacityM3 = cells.Length * GasMixtureFacade.LiquidConduitVolumeM3;
			float occupiedM3 = liquidVolumeLitres / LitresPerCubicMetre;
			occupiedM3 += TrappedLiquidLitres(gasConduit: false, cells: cells)
				/ LitresPerCubicMetre;

			float headspaceM3 = capacityM3 - occupiedM3;
			if (headspaceM3 <= 0f)
			{
				// A completely liquid-full run has no gas volume at all. Physically this is where
				// a real line would be building hydraulic pressure instead, which is ONI's own
				// overpressure hazard and not this function's business.
				return false;
			}

			var elementIdx = new List<int>();
			var massKg = new List<float>();
			float weightedTemperature = 0f;
			float totalGasKg = 0f;

			foreach (int cell in cells)
			{
				if (!TryGet(false, cell, out TrappedMatter matter) || matter.MassKg <= 0f)
				{
					continue;
				}
				if (!ElementLoader.elements[matter.ElementIdx].IsGas)
				{
					continue;
				}
				elementIdx.Add(matter.ElementIdx);
				massKg.Add(matter.MassKg);
				weightedTemperature += matter.MassKg * matter.TemperatureK;
				totalGasKg += matter.MassKg;
			}

			if (totalGasKg <= 0f)
			{
				return false;
			}

			float temperatureK = weightedTemperature / totalGasKg;
			return GasMixtureFacade.TryComputePressure(elementIdx.ToArray(), massKg.ToArray(),
				temperatureK, headspaceM3, out pressurePa);
		}

		/// <summary>
		/// PRESSURE-DRIVEN PHASE CHANGE for one whole conduit network, in both directions: gas in
		/// a gas line condensing when the line's pressure puts its dew point above the gas's
		/// temperature, and liquid in a liquid line boiling when its headspace pressure puts its
		/// boiling point below the liquid's temperature.
		///
		/// THE GAP THIS FILLS, which is worth stating exactly because the mod shipped for two days
		/// without noticing it. Mod 1's existing condensation path is a Harmony prefix on
		/// `Conduit.OnConduitFrozen`, an event vanilla only ever raises when a conduit's contents
		/// fall below the element's own FLAT `lowTemp`. The pressure-aware vapour curve at that
		/// call site can therefore only ever VETO vanilla's decision -- it can decline to condense
		/// something vanilla thought was freezing. It can never INITIATE a condensation vanilla did
		/// not already propose. But a Stationeers phase-change cooling loop condenses by raising
		/// PRESSURE, at temperatures far above the flat threshold: a pump pushes the line to a few
		/// MPa, the dew point climbs past the working fluid's temperature, and the line condenses
		/// while nowhere near freezing. On an event-driven path that loop is silent. Nothing
		/// condenses, and a Condensation Valve sits on a pipe with nothing in it to harvest.
		///
		/// So this is periodic rather than event-driven, for the same reason
		/// `PipeStressMonitor`'s overpressure check is: a pipe whose pressure has quietly put it
		/// past a threshold generates no event, it simply sits there being past it. The caller
		/// drives this on its own cadence and passes the elapsed time.
		///
		/// THE CONVERSION ITSELF is <see cref="GasMixtureFacade.ComputePhaseChangeStep"/>, the
		/// native form of Stationeers' state change, at Stationeers' own 10 %-per-tick
		/// gradual rate rather than an instant flash. The product leaves at the source's
		/// temperature and the latent heat is then billed across the whole network's shared body
		/// (see <see cref="BillLatentHeatToNetwork(int[], bool, ConduitFlow, float)"/>), which is
		/// the accounting Mod 1's condensation path already settled on after two wrong answers.
		///
		/// OVERLAP WITH THE EVENT PATH is possible and benign: a gas line cold enough to trip
		/// vanilla's flat threshold will have both paths convert on the same tick, each at the
		/// same rate against the same remainder floor, so the effect is a briefly doubled
		/// conversion rate and never a double-charge -- both paths bill exactly the latent heat of
		/// the mass they themselves moved. Collapsing the event path onto this one is a tidy-up
		/// worth doing, not a correctness fix.
		///
		/// Returns the mass converted this call, in kilograms, across the whole network.
		/// </summary>
		/// <param name="cell">Any tile of the network.</param>
		/// <param name="contentType">Which network at that cell.</param>
		/// <param name="tickSeconds">Elapsed simulated time since the caller's last call.</param>
		/// <param name="ratePerSecond">Fraction of the convertible mass per second; Stationeers'
		/// own shape is 0.1.</param>
		/// <param name="minRemainderKg">Do not leave physically meaningless dust behind.</param>
		public static float TickNetworkPhaseChange(int cell, PipeContentType contentType,
			float tickSeconds, float ratePerSecond = 0.1f, float minRemainderKg = 0.01f)
		{
			if (tickSeconds <= 0f)
			{
				return 0f;
			}

			ConduitFlow flow = contentType == PipeContentType.Liquid
				? Game.Instance?.liquidConduitFlow
				: Game.Instance?.gasConduitFlow;
			if (flow == null)
			{
				return 0f;
			}

			// THE SIM DECIDES THIS RUN. Under ConduitNetworkPolicy.Phase the same
			// decisions are made natively on every 200 ms update and carried out by
			// SimConduitNetworks' patch through the same two helpers below; deciding here as well
			// would convert the run twice. Returns 0 because this call converted nothing -- the
			// mass the sim's decisions moved is in SimConduitNetworks.ProposalMassAppliedKg and in
			// this class's own Condensed/Boiled counters.
			if (SimConduitNetworks.IsNativePhaseInForce(cell, contentType))
			{
				return 0f;
			}

			if (!PipeNetworkFacade.TryGetNetworkState(cell, contentType,
				out PipeNetworkState state) || state.Cells == null || state.Cells.Length == 0)
			{
				return 0f;
			}

			bool gasConduit = contentType != PipeContentType.Liquid;

			// The pressure the vapour curve is evaluated at. A gas run has a real gas-law
			// pressure; a liquid run's is its headspace, which may legitimately be zero when the
			// line holds no gas at all -- and zero is a meaningful answer there, not a failure:
			// the clamp resolves it to the fluid's freezing point, so an unpressurised line
			// boils, which is the behaviour Stationeers warns players about.
			float pressurePa;
			if (gasConduit)
			{
				pressurePa = state.PressurePa;
				if (pressurePa <= 0f)
				{
					return 0f;
				}
			}
			else
			{
				TryGetLiquidHeadspacePressurePa(state, out pressurePa);
			}

			float convertedTotalKg = 0f;

			foreach (int c in state.Cells)
			{
				ConduitFlow.ConduitContents contents = flow.GetContents(c);
				if (contents.mass <= 0f || contents.element == SimHashes.Vacuum)
				{
					continue;
				}

				Element element = ElementLoader.FindElementByHash(contents.element);
				if (element == null)
				{
					continue;
				}

				// The element this becomes on the other side of the boundary. Klei stores both
				// directions as indices into the same table, so no name matching and no guessing
				// at a "LiquidX" naming convention.
				Element product = gasConduit ? element.lowTempTransition : element.highTempTransition;
				if (product == null)
				{
					continue;
				}
				if (gasConduit ? !(element.IsGas && product.IsLiquid)
					: !(element.IsLiquid && product.IsGas))
				{
					continue;
				}

				if (!MaterialPropertyRegistry.TryGetEvaporationTemperatureClampedK(contents.element,
					pressurePa, out float boundaryK))
				{
					continue;
				}

				// Condensing needs the gas BELOW its dew point; boiling needs the liquid ABOVE its
				// boiling point. Same boundary, opposite side.
				if (gasConduit ? contents.temperature >= boundaryK
					: contents.temperature <= boundaryK)
				{
					continue;
				}

				if (!MaterialPropertyRegistry.TryGetLatentHeatOfVaporizationJPerKg(contents.element,
					out float latentHeatJPerKg) || latentHeatJPerKg <= 0f)
				{
					continue;
				}

				GasMixtureFacade.ComputePhaseChangeStep(contents.mass, contents.temperature,
					boundaryK, latentHeatJPerKg, element.specificHeatCapacity * GramsPerKilogram,
					tickSeconds, ratePerSecond, minRemainderKg,
					out float convertedMassKg, out float _);

				if (convertedMassKg <= 0f)
				{
					continue;
				}

				convertedTotalKg += ApplyContentsConversion(gasConduit, flow, c, contents, product,
					convertedMassKg, latentHeatJPerKg);
			}

			// The return leg. Without it the liquid branch above is a one-way pump: every tick it
			// boils a little more liquid, absorbs that liquid's full latent heat out of the
			// network, and the gas it produced has no way back. The line would cool without limit
			// and the headspace would fill without limit. Charging and permitting the reverse
			// transition is what closes it, and it is also just the physics -- gas above a liquid
			// at its dew point condenses.
			if (!gasConduit)
			{
				convertedTotalKg += CondenseHeadspaceGas(state, flow, pressurePa, tickSeconds,
					ratePerSecond, minRemainderKg);
			}

			return convertedTotalKg;
		}

		/// <summary>
		/// Carries out one contents conversion -- gas condensing out of a gas run's conduit, or
		/// liquid boiling out of a liquid run's -- that something has already decided: the
		/// managed pass above, or the sim under the PHASE policy
		/// (<see cref="SimConduitNetworks"/>). One implementation, so the two deciders cannot
		/// drift in what a conversion DOES.
		///
		/// The latent heat is billed to <paramref name="c"/>, the tile that converted, per
		/// <see cref="BillLatentHeatToCell"/>'s contract ("where it happened"), not to whichever
		/// anchor cell the caller passed.
		///
		/// Returns the mass converted.
		/// </summary>
		private static float ApplyContentsConversion(bool gasConduit, ConduitFlow flow, int c,
			ConduitFlow.ConduitContents contents, Element product, float convertedMassKg,
			float latentHeatJPerKg)
		{
			// Germs ride the mass they are actually on. Splitting by mass fraction and giving
			// the remainder the integer rounding leftover keeps the total exactly conserved.
			int convertedDisease = 0;
			if (contents.diseaseCount > 0 && contents.mass > 0f)
			{
				convertedDisease = Mathf.Clamp(
					Mathf.RoundToInt(contents.diseaseCount * (convertedMassKg / contents.mass)),
					0, contents.diseaseCount);
			}

			float remainingMassKg = contents.mass - convertedMassKg;
			int productIdx = ElementLoader.elements.IndexOf(product);
			if (productIdx < 0)
			{
				return 0f;
			}

			// Order matters: write the new state FIRST, then bill. The freshly converted mass
			// is part of the mixture that pays for (or is paid by) the transition, the same
			// way Stationeers' newly condensed moles are still inside the Atmosphere that
			// bills for them. The kernel's own `remainingTemperatureK` is deliberately unused:
			// it is single-tile local billing, and this replaces it with the billing below.
			Add(gasConduit, c, productIdx, convertedMassKg, contents.temperature);

			if (remainingMassKg > 0f)
			{
				flow.SetContents(c, new ConduitFlow.ConduitContents(contents.element,
					remainingMassKg, contents.temperature, contents.diseaseIdx,
					contents.diseaseCount - convertedDisease));
			}
			else
			{
				flow.SetContents(c, ConduitFlow.ConduitContents.Empty);
			}

			// Condensing RELEASES latent heat into the network; boiling ABSORBS it. That sign
			// is the entire cooling effect of the loop this exists for, so it is written as
			// the physical statement rather than as a parameter.
			float latentHeatJ = convertedMassKg * latentHeatJPerKg;
			float signedJoules = gasConduit ? latentHeatJ : -latentHeatJ;

			if (!BillLatentHeatToCell(c, gasConduit, flow, signedJoules))
			{
				// Nothing in the network could take it. Cannot happen while the mass just
				// banked is still standing there, but energy must never simply vanish.
				float productHeatCapacity =
					convertedMassKg * product.specificHeatCapacity * GramsPerKilogram;
				if (productHeatCapacity > 0f)
				{
					AddTemperatureK(gasConduit, c, signedJoules / productHeatCapacity);
				}
			}

			if (gasConduit)
			{
				CondensedInGasConduitKg += convertedMassKg;
			}
			else
			{
				BoiledInLiquidConduitKg += convertedMassKg;
			}
			return convertedMassKg;
		}

		/// <summary>
		/// Carries out one phase-change decision the sim made under
		/// <see cref="ConduitNetworkPolicy.Phase"/>, through the same two helpers the managed pass
		/// uses, so a conversion does the same thing whoever decided it. Called by
		/// <see cref="SimConduitNetworks"/>' patch after every conduit update. The fourth kind,
		/// <see cref="SimConduitNetworks.PhaseChangeKind.EvaporateTrapped"/>, has no managed-pass
		/// twin in this class -- its managed counterpart is the housekeeping return in Mod 1's
		/// <c>PipeMatterState</c> -- so it goes through its own helper,
		/// <see cref="ApplyTrappedEvaporation"/>.
		///
		/// REFUSED, returning -1, when the tile no longer holds what the sim decided against: a
		/// different element, or a mass that differs by more than float noise. The sim reads the
		/// contents the game last published and nothing moves them between that update and this
		/// patch, so a refusal means something wrote a conduit behind <c>ConduitFlow.SetContents</c>'
		/// back -- worth counting, never worth converting mass that is not there.
		///
		/// Returns the mass converted (0 when a helper found nothing to do), or -1 when refused.
		/// </summary>
		internal static float ApplyNativePhaseProposal(SimConduitNetworks.PhaseProposal p)
		{
			bool gasConduit = p.ContentType != PipeContentType.Liquid;
			ConduitFlow flow = gasConduit
				? Game.Instance?.gasConduitFlow
				: Game.Instance?.liquidConduitFlow;
			int elementCount = ElementLoader.elements.Count;
			if (flow == null || p.SourceElementIdx >= elementCount || p.ProductElementIdx >= elementCount
				|| !Grid.IsValidCell(p.Cell) || !(p.ConvertedMassKg > 0f))
			{
				return -1f;
			}
			Element source = ElementLoader.elements[p.SourceElementIdx];
			Element product = ElementLoader.elements[p.ProductElementIdx];

			switch (p.Kind)
			{
				case SimConduitNetworks.PhaseChangeKind.CondenseContents:
				case SimConduitNetworks.PhaseChangeKind.BoilContents:
				{
					bool expectGas = p.Kind == SimConduitNetworks.PhaseChangeKind.CondenseContents;
					if (expectGas != gasConduit)
					{
						return -1f;
					}
					ConduitFlow.ConduitContents contents = flow.GetContents(p.Cell);
					if (contents.element != source.id || contents.mass <= 0f
						|| Mathf.Abs(contents.mass - p.SourceMassKg) > StaleToleranceKg(p.SourceMassKg))
					{
						return -1f;
					}
					return ApplyContentsConversion(gasConduit, flow, p.Cell, contents, product,
						Mathf.Min(p.ConvertedMassKg, contents.mass), p.LatentHeatJPerKg);
				}
				case SimConduitNetworks.PhaseChangeKind.CondenseHeadspace:
				{
					if (gasConduit || !TryGet(false, p.Cell, out TrappedMatter matter)
						|| matter.ElementIdx != p.SourceElementIdx || matter.MassKg <= 0f
						|| Mathf.Abs(matter.MassKg - p.SourceMassKg) > StaleToleranceKg(p.SourceMassKg))
					{
						return -1f;
					}
					return ApplyHeadspaceCondensation(flow, p.Cell, product,
						Mathf.Min(p.ConvertedMassKg, matter.MassKg), p.LatentHeatJPerKg);
				}
				case SimConduitNetworks.PhaseChangeKind.EvaporateTrapped:
				{
					if (!gasConduit || !TryGet(true, p.Cell, out TrappedMatter matter)
						|| matter.ElementIdx != p.SourceElementIdx || matter.MassKg <= 0f
						|| Mathf.Abs(matter.MassKg - p.SourceMassKg) > StaleToleranceKg(p.SourceMassKg)
						|| !source.IsLiquid || !product.IsGas)
					{
						return -1f;
					}
					return ApplyTrappedEvaporation(flow, p.Cell, product,
						Mathf.Min(p.ConvertedMassKg, matter.MassKg), p.LatentHeatJPerKg);
				}
				default:
					return -1f;
			}
		}

		/// <summary>
		/// How much more gas a gas conduit tile can take from the liquid standing in it: its
		/// capacity (the flow's own <c>MaxMass</c>, which this project raises for gas) less what it
		/// already carries, or 0 when it carries a DIFFERENT gas -- ConduitContents is
		/// single-element, so the liquid has to stay where it is.
		/// </summary>
		private static float TrappedEvaporationHeadroomKg(ConduitFlow flow, int cell, Element vapour)
		{
			ConduitFlow.ConduitContents contents = flow.GetContents(cell);
			bool tileHasContents = contents.mass > 0f && contents.element != SimHashes.Vacuum;
			if (tileHasContents && contents.element != vapour.id)
			{
				return 0f;
			}
			return SimConduitNetworks.MaxMassOf(flow) - (tileHasContents ? contents.mass : 0f);
		}

		/// <summary>
		/// Carries out one <see cref="SimConduitNetworks.PhaseChangeKind.EvaporateTrapped"/>
		/// decision: liquid standing in a gas conduit going back into the conduit as its own
		/// vapour. The mirror image of <see cref="ApplyHeadspaceCondensation"/>, and the native
		/// replacement for the gas half of Mod 1's <c>PipeMatterState.TickRecovery</c> return,
		/// with two differences from that return, both deliberate. It moves only what the sim's
		/// step decided rather than the whole tile at once, which is what stops a tile the sim
		/// holds on its dew point being emptied and refilled every second. And its latent heat
		/// is billed to THIS tile (<see cref="BillLatentHeatToCell"/>), where the condensing
		/// half already bills it, rather than across the network -- the same one-place rule
		/// <see cref="ApplyContentsConversion"/> follows.
		///
		/// Re-checks the tile's headroom rather than trusting the decider's, because a conduit
		/// that received gas since cannot be overfilled.
		///
		/// Counted in NO existing total. <see cref="CondensedInGasConduitKg"/> stays the gross
		/// mass condensed, as it is when Mod 1's housekeeping returns condensate, so the number
		/// means the same under either decider; the evaporated mass is in
		/// <see cref="SimConduitNetworks.ProposalMassAppliedOfKind"/>.
		///
		/// Returns the mass evaporated.
		/// </summary>
		private static float ApplyTrappedEvaporation(ConduitFlow flow, int cell, Element vapour,
			float convertedMassKg, float latentHeatJPerKg)
		{
			convertedMassKg = Mathf.Min(convertedMassKg,
				TrappedEvaporationHeadroomKg(flow, cell, vapour));
			if (convertedMassKg <= 0f)
			{
				return 0f;
			}

			ConduitFlow.ConduitContents contents = flow.GetContents(cell);
			bool tileHasContents = contents.mass > 0f && contents.element != SimHashes.Vacuum;

			if (!TryDrain(true, cell, convertedMassKg, out int _, out float drainedKg,
				out float drainedTemperatureK) || drainedKg <= 0f)
			{
				return 0f;
			}

			// TryDrain counts what it takes as harvested; this is physics, not a valve.
			DrainedFromGasConduitKg -= drainedKg;

			// Heat-capacity blend, the two sides at their own specific heats: the vapour
			// arrives at the liquid's temperature and then pays for its own transition below.
			float blendedK;
			if (tileHasContents)
			{
				float cpContents = contents.mass * vapour.specificHeatCapacity;
				float cpAdded = drainedKg * vapour.specificHeatCapacity;
				float total = cpContents + cpAdded;
				blendedK = total > 0f
					? (contents.temperature * cpContents + drainedTemperatureK * cpAdded) / total
					: drainedTemperatureK;
			}
			else
			{
				blendedK = drainedTemperatureK;
			}

			flow.SetContents(cell, new ConduitFlow.ConduitContents(vapour.id,
				(tileHasContents ? contents.mass : 0f) + drainedKg,
				Mathf.Max(blendedK, MinimumConduitTemperatureK),
				contents.diseaseIdx, contents.diseaseCount));

			// Evaporating ABSORBS the latent heat, out of the tile it happened in.
			float latentHeatJ = drainedKg * latentHeatJPerKg;
			if (!BillLatentHeatToCell(cell, true, flow, -latentHeatJ))
			{
				float vapourHeatCapacity =
					drainedKg * vapour.specificHeatCapacity * GramsPerKilogram;
				if (vapourHeatCapacity > 0f)
				{
					ConduitFlow.ConduitContents now = flow.GetContents(cell);
					flow.SetContents(cell, new ConduitFlow.ConduitContents(now.element, now.mass,
						Mathf.Max(now.temperature - latentHeatJ / vapourHeatCapacity,
							MinimumConduitTemperatureK),
						now.diseaseIdx, now.diseaseCount));
				}
			}

			return drainedKg;
		}

		private static float StaleToleranceKg(float massKg)
		{
			return 1e-5f + 1e-4f * Mathf.Abs(massKg);
		}

		/// <summary>
		/// Vanilla ONI's own per-tile liquid-conduit mass cap, in kilograms
		/// (<c>Game.OnPrefabInit</c>: <c>new ConduitFlow(ConduitType.Liquid, ..., 10f, 0.75f)</c>).
		/// Unlike the gas cap, the SDK does not raise it, so the vanilla
		/// number is the real one. Written as a named constant because condensing gas back into a
		/// tile must not overfill it.
		/// </summary>
		public const float VanillaLiquidConduitMaxMassKg = 10f;

		/// <summary>
		/// The reverse of the liquid branch of <see cref="TickNetworkPhaseChange"/>: headspace gas
		/// standing in a liquid conduit condensing back into the conduit's own liquid contents
		/// once the run's pressure puts its dew point above the gas's temperature.
		///
		/// This is what makes the Purge Valve a control and not merely a drain. Boil-off raises
		/// headspace pressure, which raises the boiling point, which stops the boiling; if the gas
		/// can also condense back, the line settles at an equilibrium instead of drifting. Pull the
		/// gas out with a purge valve and the equilibrium moves: pressure falls, the boiling point
		/// falls with it, the line boils again, and boiling absorbs latent heat. That is the
		/// cooling, and it is proportional to how hard the valve is purging.
		///
		/// Condensate is returned to the conduit's own contents rather than banked as trapped
		/// liquid, because a liquid conduit can represent liquid perfectly well -- the store exists
		/// for phases a conduit CANNOT hold. Capped at
		/// <see cref="VanillaLiquidConduitMaxMassKg"/> so this can never overfill a tile; gas that
		/// does not fit stays in the headspace, which is the physically sensible outcome anyway.
		/// </summary>
		private static float CondenseHeadspaceGas(PipeNetworkState state, ConduitFlow flow,
			float pressurePa, float tickSeconds, float ratePerSecond, float minRemainderKg)
		{
			if (pressurePa <= 0f)
			{
				return 0f;
			}

			float condensedKg = 0f;

			foreach (int cell in state.Cells)
			{
				if (!TryGet(false, cell, out TrappedMatter matter) || matter.MassKg <= 0f)
				{
					continue;
				}

				Element gas = ElementLoader.elements[matter.ElementIdx];
				if (!gas.IsGas)
				{
					continue;
				}

				Element liquid = gas.lowTempTransition;
				if (liquid == null || !liquid.IsLiquid)
				{
					continue;
				}

				if (!MaterialPropertyRegistry.TryGetEvaporationTemperatureClampedK(gas.id,
					pressurePa, out float boundaryK) || matter.TemperatureK >= boundaryK)
				{
					continue;
				}

				if (!MaterialPropertyRegistry.TryGetLatentHeatOfVaporizationJPerKg(gas.id,
					out float latentHeatJPerKg) || latentHeatJPerKg <= 0f)
				{
					continue;
				}

				if (HeadspaceHeadroomKg(flow, cell, liquid) <= 0f)
				{
					continue;
				}

				GasMixtureFacade.ComputePhaseChangeStep(matter.MassKg, matter.TemperatureK,
					boundaryK, latentHeatJPerKg, gas.specificHeatCapacity * GramsPerKilogram,
					tickSeconds, ratePerSecond, minRemainderKg,
					out float convertedMassKg, out float _);

				condensedKg += ApplyHeadspaceCondensation(flow, cell, liquid, convertedMassKg,
					latentHeatJPerKg);
			}

			return condensedKg;
		}

		/// <summary>
		/// How much more liquid a liquid conduit tile can take from its headspace: its capacity
		/// less what it already carries, or 0 when it already carries a DIFFERENT liquid --
		/// ConduitContents is single-element, so the gas has to stay where it is.
		/// </summary>
		private static float HeadspaceHeadroomKg(ConduitFlow flow, int cell, Element liquid)
		{
			ConduitFlow.ConduitContents contents = flow.GetContents(cell);
			bool tileHasContents = contents.mass > 0f && contents.element != SimHashes.Vacuum;
			if (tileHasContents && contents.element != liquid.id)
			{
				return 0f;
			}
			return VanillaLiquidConduitMaxMassKg - (tileHasContents ? contents.mass : 0f);
		}

		/// <summary>
		/// Carries out one headspace condensation -- standing gas in a liquid conduit going back
		/// into the conduit as liquid -- that something has already decided: the managed pass, or
		/// the sim under the PHASE policy. Re-checks the tile's headroom rather than trusting the
		/// decider's, because a conduit that received liquid since cannot be overfilled.
		/// Returns the mass condensed.
		/// </summary>
		private static float ApplyHeadspaceCondensation(ConduitFlow flow, int cell, Element liquid,
			float convertedMassKg, float latentHeatJPerKg)
		{
			convertedMassKg = Mathf.Min(convertedMassKg, HeadspaceHeadroomKg(flow, cell, liquid));
			if (convertedMassKg <= 0f)
			{
				return 0f;
			}

			ConduitFlow.ConduitContents contents = flow.GetContents(cell);
			bool tileHasContents = contents.mass > 0f && contents.element != SimHashes.Vacuum;

			if (!TryDrain(false, cell, convertedMassKg, out int _, out float drainedKg,
				out float drainedTemperatureK) || drainedKg <= 0f)
			{
				return 0f;
			}

			// TryDrain is the store's own accounting, and it counts what it takes as harvested
			// mass. This is physics rather than a valve, so undo that: the drain counter must
			// only ever report what devices have actually pulled out of pipes.
			DrainedFromLiquidConduitKg -= drainedKg;

			float blendedK;
			if (tileHasContents)
			{
				float cpContents = contents.mass * liquid.specificHeatCapacity;
				float cpAdded = drainedKg * liquid.specificHeatCapacity;
				float total = cpContents + cpAdded;
				blendedK = total > 0f
					? (contents.temperature * cpContents + drainedTemperatureK * cpAdded) / total
					: drainedTemperatureK;
			}
			else
			{
				blendedK = drainedTemperatureK;
			}

			flow.SetContents(cell, new ConduitFlow.ConduitContents(liquid.id,
				(tileHasContents ? contents.mass : 0f) + drainedKg,
				Mathf.Max(blendedK, MinimumConduitTemperatureK),
				contents.diseaseIdx, contents.diseaseCount));

			float latentHeatJ = drainedKg * latentHeatJPerKg;
			if (!BillLatentHeatToCell(cell, false, flow, latentHeatJ))
			{
				float liquidHeatCapacity =
					drainedKg * liquid.specificHeatCapacity * GramsPerKilogram;
				if (liquidHeatCapacity > 0f)
				{
					AddTemperatureK(false, cell, latentHeatJ / liquidHeatCapacity);
				}
			}

			BoiledInLiquidConduitKg -= drainedKg;
			return drainedKg;
		}
	}
}
