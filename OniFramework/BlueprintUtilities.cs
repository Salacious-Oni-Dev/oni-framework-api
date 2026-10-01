using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The utility-network half of blueprint validation: is the circuit big enough for what is on
	/// it, is every building that needs a connection actually connected, and is each pipe made of
	/// something consistent with the pipe it is.
	///
	/// WHY THIS EXISTS. It is easy to write a canvas that puts a 1.0-1.3 kW load on plain `Wire`,
	/// which is rated Max1000, and nothing else in the blueprint pipeline notices; only someone
	/// watching the run on screen would. That is the wrong detector: an overloaded
	/// circuit is not a cosmetic complaint, it damages the world the rig is measuring.
	///
	/// `ElectricalUtilityNetwork.UpdateOverloadTime` accumulates `timeOverloaded` for as long as
	/// the circuit's `wattsUsed` exceeds the LOWEST wire rating present on it (plus
	/// `POWER.FLOAT_FUDGE_FACTOR`, 0.5 W), and every 6 s of that deals 1 damage to a RANDOM wire
	/// on the circuit. Wire has 10 hit points and `BaseTimeUntilRepair = -1`, and a blueprint
	/// canvas has no duplicant to repair anything, so about a minute of continuous overload breaks
	/// a wire, the circuit splits, and every building past the break goes unpowered. A rig that
	/// measures heat has no reason to look at a wire, so what it reports afterwards is a colony
	/// that quietly stopped drawing power part way through the window -- numbers that still look
	/// like physics -- the failure mode detectors exist for.
	///
	/// The overload test is CONSUMPTION only. `CircuitManager.Sim200msLast` builds `wattsUsed`
	/// from `EnergyConsumer.WattsUsed` plus battery charging; generated power is not in it. A
	/// canvas of generators with no load -- `generator-enthalpy.yaml`, `turbine-fed.yaml`,
	/// `turbine-heat.yaml` -- therefore cannot overload however much it makes, and those files are
	/// deliberately left on plain Wire.
	///
	/// NOTHING HERE IS A HARDCODED TABLE. Ratings come from the `Wire` component on each def's own
	/// completed prefab, draws from `BuildingDef.EnergyConsumptionWhenActive`, conductivities from
	/// `BuildingDef.ThermalConductivity` and `ElementLoader`. A game update moves these numbers and
	/// this file follows them, which is the whole reason it reads them instead of listing them.
	/// </summary>
	public static class BlueprintUtilities
	{
		/// <summary>
		/// Every circuit's worst-case continuous consumer draw against the weakest wire on it.
		/// Adds one error per overloaded circuit, naming the wire that would carry it.
		///
		/// Set <c>allowCircuitOverload: true</c> on the blueprint to turn these into advisories --
		/// for a rig whose subject IS the overload. Anything else wants the error.
		/// </summary>
		public static void CheckCircuits(RigBlueprint blueprint, List<string> errors, List<string> advice)
		{
			if (blueprint == null || blueprint.buildings == null || blueprint.buildings.Count == 0)
			{
				return;
			}

			// 1. Every wire cell, and the rating of the wire on it. Read off the def's completed
			//    prefab rather than from a table of ids: `WireRefined` is Max2000 and
			//    `HighWattageWire` is Max20000, which is not the ordering the names suggest.
			var ratingAt = new Dictionary<int, float>();
			var wireAt = new Dictionary<int, string>();
			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				if (spec == null || string.IsNullOrEmpty(spec.id))
				{
					continue;
				}
				BuildingDef def = Assets.GetBuildingDef(spec.id);
				if (def == null || def.ObjectLayer != ObjectLayer.Wire)
				{
					continue;
				}
				float rating = RatingOf(def);
				if (rating <= 0f)
				{
					continue;
				}
				int cell = Grid.XYToCell(spec.x, spec.y);
				if (!Grid.IsValidCell(cell))
				{
					continue;
				}
				ratingAt[cell] = rating;
				wireAt[cell] = spec.id;
			}

			// Bridges. A `WireUtilityNetworkLink` joins the two cells at `link1`/`link2` -- which
			// are NOT adjacent to each other -- so a flood over grid neighbours alone would report
			// two circuits where the game sees one, and then under-report the load on both.
			var bridged = new List<KeyValuePair<int, int>>();
			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				if (spec == null || string.IsNullOrEmpty(spec.id))
				{
					continue;
				}
				BuildingDef def = Assets.GetBuildingDef(spec.id);
				if (def == null || def.ObjectLayer != ObjectLayer.WireConnectors || def.BuildingComplete == null)
				{
					continue;
				}
				var link = def.BuildingComplete.GetComponent<WireUtilityNetworkLink>();
				if (link == null)
				{
					continue;
				}
				int a = Grid.OffsetCell(Grid.XYToCell(spec.x, spec.y), link.link1);
				int b = Grid.OffsetCell(Grid.XYToCell(spec.x, spec.y), link.link2);
				if (Grid.IsValidCell(a) && Grid.IsValidCell(b))
				{
					bridged.Add(new KeyValuePair<int, int>(a, b));
					float rating = Wire.GetMaxWattageAsFloat(link.maxWattageRating);
					if (rating > 0f)
					{
						// The bridge is part of the circuit and its own rating counts, so a
						// Max1000 bridge caps a Max2000 run exactly the way the game's own
						// per-rating scan of `wireGroups` does.
						if (!ratingAt.ContainsKey(a) || ratingAt[a] > rating) { ratingAt[a] = rating; wireAt[a] = spec.id; }
						if (!ratingAt.ContainsKey(b) || ratingAt[b] > rating) { ratingAt[b] = rating; wireAt[b] = spec.id; }
					}
				}
			}

			if (ratingAt.Count == 0)
			{
				return;
			}

			// 2. Flood into circuits.
			var circuitOf = new Dictionary<int, int>();
			var adjacency = new Dictionary<int, List<int>>();
			foreach (KeyValuePair<int, int> pair in bridged)
			{
				Link(adjacency, pair.Key, pair.Value);
				Link(adjacency, pair.Value, pair.Key);
			}

			int circuits = 0;
			var stack = new Stack<int>();
			foreach (int start in ratingAt.Keys)
			{
				if (circuitOf.ContainsKey(start))
				{
					continue;
				}
				circuits++;
				circuitOf[start] = circuits;
				stack.Push(start);
				while (stack.Count > 0)
				{
					int cell = stack.Pop();
					Vector2I at = Grid.CellToXY(cell);
					PushNeighbour(ratingAt, circuitOf, stack, circuits, at.x + 1, at.y);
					PushNeighbour(ratingAt, circuitOf, stack, circuits, at.x - 1, at.y);
					PushNeighbour(ratingAt, circuitOf, stack, circuits, at.x, at.y + 1);
					PushNeighbour(ratingAt, circuitOf, stack, circuits, at.x, at.y - 1);
					List<int> jumps;
					if (adjacency.TryGetValue(cell, out jumps))
					{
						for (int j = 0; j < jumps.Count; j++)
						{
							if (ratingAt.ContainsKey(jumps[j]) && !circuitOf.ContainsKey(jumps[j]))
							{
								circuitOf[jumps[j]] = circuits;
								stack.Push(jumps[j]);
							}
						}
					}
				}
			}

			// 3. Bill every consumer to the circuit its POWER INPUT CELL sits on -- not its
			//    origin. `PowerInputOffset` is (0,0) for most buildings but not all, and a
			//    building whose port lands off the wire is not on the circuit at all, which is a
			//    different fault reported separately by CheckConnections.
			var draw = new Dictionary<int, float>();
			var members = new Dictionary<int, List<string>>();
			var weakest = new Dictionary<int, float>();
			var weakestId = new Dictionary<int, string>();
			foreach (KeyValuePair<int, int> entry in circuitOf)
			{
				float rating = ratingAt[entry.Key];
				if (!weakest.ContainsKey(entry.Value) || rating < weakest[entry.Value])
				{
					weakest[entry.Value] = rating;
					weakestId[entry.Value] = wireAt[entry.Key];
				}
			}

			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				if (spec == null || string.IsNullOrEmpty(spec.id))
				{
					continue;
				}
				BuildingDef def = Assets.GetBuildingDef(spec.id);
				if (def == null || def.EnergyConsumptionWhenActive <= 0f)
				{
					continue;
				}
				int port = Grid.OffsetCell(Grid.XYToCell(spec.x, spec.y), def.PowerInputOffset);
				int circuit;
				if (!Grid.IsValidCell(port) || !circuitOf.TryGetValue(port, out circuit))
				{
					continue;
				}
				if (!draw.ContainsKey(circuit))
				{
					draw[circuit] = 0f;
					members[circuit] = new List<string>();
				}
				draw[circuit] += def.EnergyConsumptionWhenActive;
				members[circuit].Add($"{spec.id} {def.EnergyConsumptionWhenActive:F0} W");
			}

			foreach (KeyValuePair<int, float> entry in draw)
			{
				float rating = weakest[entry.Key];
				string id = weakestId[entry.Key];
				string detail = $"circuit {entry.Key} of '{blueprint.name}' draws {entry.Value:F0} W "
					+ $"through {id} (rated {rating:F0} W): "
					+ string.Join(" + ", members[entry.Key].ToArray());
				if (entry.Value <= rating + 0.5f)
				{
					continue;
				}
				string fix = Upgrade(entry.Value);
				string message = detail + ". An overloaded circuit damages a random wire on it "
					+ "every 6 s and there is no duplicant on a canvas to repair one, so the "
					+ "circuit breaks part way through the run and the buildings past the break "
					+ "go unpowered without saying so. "
					+ (fix != null
						? $"Use '{fix}' instead, or split the load across separate circuits."
						: "No stock wire carries this; split the load across separate circuits.")
					+ " Set allowCircuitOverload: true if the overload is the subject of the rig.";
				if (blueprint.allowCircuitOverload)
				{
					advice.Add("ALLOWED OVERLOAD -- " + message);
				}
				else
				{
					errors.Add(message);
				}
			}
		}

		/// <summary>
		/// Buildings that need a connection and do not have one. Advisory rather than fatal: an
		/// unpowered or unpiped building is sometimes exactly what a control arm wants. It is
		/// reported because the alternative is a rig whose subject never became operational --
		/// `RequireOutputs` holds `output_connected` false until a conduit exists at the
		/// building's own `UtilityOutputOffset`, and a building that is not operational never
		/// reaches its own sim tick at all, so it reads `THROTTLED 0.0 W` until someone looks at
		/// the pipe.
		/// </summary>
		public static void CheckConnections(RigBlueprint blueprint, List<string> advice)
		{
			if (blueprint == null || blueprint.buildings == null)
			{
				return;
			}

			// KEYED BY CONDUIT TYPE, NOT BY OBJECT LAYER, and the difference is not cosmetic.
			// A pipe piece lives on `ObjectLayer.LiquidConduit`, but the layer a building's port
			// is looked up on is `Grid.GetObjectLayerForConduitType(Liquid)` --
			// `LiquidConduitConnection`, a different layer entirely. The first version of this
			// check compared the two and reported every correctly-piped turbine on
			// `turbine-heat.yaml` as unconnected. A detector that cries wolf is worse than no
			// detector, so the key is the thing both sides actually agree on: the ConduitType.
			var occupied = new HashSet<long>();
			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				if (spec == null || string.IsNullOrEmpty(spec.id))
				{
					continue;
				}
				BuildingDef def = Assets.GetBuildingDef(spec.id);
				if (def == null)
				{
					continue;
				}
				int cell = Grid.XYToCell(spec.x, spec.y);
				if (!Grid.IsValidCell(cell))
				{
					continue;
				}
				if (def.ObjectLayer == ObjectLayer.Wire)
				{
					occupied.Add(WireKey(cell));
				}
				ConduitType carried = ConduitTypeOf(def.ObjectLayer);
				if (carried != ConduitType.None)
				{
					occupied.Add(ConduitKey(cell, carried));
				}
			}

			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				if (spec == null || string.IsNullOrEmpty(spec.id))
				{
					continue;
				}
				BuildingDef def = Assets.GetBuildingDef(spec.id);
				if (def == null)
				{
					continue;
				}
				int origin = Grid.XYToCell(spec.x, spec.y);
				if (!Grid.IsValidCell(origin))
				{
					continue;
				}

				int powerCell = Grid.OffsetCell(origin,
					Rotatable.GetRotatedCellOffset(def.PowerInputOffset, spec.orientation));
				if (def.RequiresPowerInput && !occupied.Contains(WireKey(powerCell)))
				{
					Vector2I at = Grid.CellToXY(powerCell);
					advice.Add($"'{spec.id}' at ({spec.x},{spec.y}) requires power but there is no "
						+ $"wire at its PowerInputOffset cell ({at.x},{at.y}); it will never be "
						+ "operational, so it draws nothing and heats nothing.");
				}

				AdviseConduit(def.InputConduitType, def.UtilityInputOffset, "input", spec, def, origin, occupied, advice);
				AdviseConduit(def.OutputConduitType, def.UtilityOutputOffset, "output", spec, def, origin, occupied, advice);
			}
		}

		/// <summary>
		/// What each pipe in the blueprint will actually conduct, and what the alternatives would.
		///
		/// THE THREE PIPE TIERS ARE ONE NUMBER, `BuildingDef.ThermalConductivity`, AND IT IS A
		/// MULTIPLIER ON THE BUILD MATERIAL -- not a conductivity by itself:
		///
		///     plain    LiquidConduit / GasConduit                x1        (the def default)
		///     insulated InsulatedLiquidConduit / InsulatedGasConduit x1/32  (0.03125)
		///     radiant  LiquidConduitRadiant / GasConduitRadiant  x2
		///
		/// so the material matters as much as the tier and the two multiply. An Insulated Liquid
		/// Pipe of Igneous Rock (element conductivity 2) conducts 0.0625; the same pipe made of
		/// Tungsten (60) conducts 1.875, which is most of a plain Ceramic pipe -- the tier chosen
		/// for insulation, defeated by the material. Igneous Rock is the standard insulated-pipe
		/// material for exactly this reason: it is `Plumbable`, it is common, and at 2 it is near
		/// the bottom of what an insulated pipe can be built from. Ceramic (0.62) is better where
		/// it can be spared, and a Radiant pipe wants the opposite end -- it is restricted to
		/// refined metals (liquid) or raw metals (gas) so that the x2 has something to multiply.
		///
		/// A pipe is not the only thing on the tile. Its heat exchange is with the CELL it sits
		/// in, so an insulated pipe run through vacuum and an insulated pipe run through a hot
		/// room differ by their surroundings, not by the pipe. Where a rig needs a leg of pipe to
		/// carry a fluid without the surroundings touching it -- every refrigeration and turbine
		/// canvas here -- the insulated pipe is the mechanism and the material is half of it.
		///
		/// Reported, never fatal: which pipe a canvas wants is the rig author's decision, and this
		/// only makes sure the number behind that decision is on the transcript.
		/// </summary>
		public static void ReportConduits(RigBlueprint blueprint, List<string> advice)
		{
			if (blueprint == null || blueprint.buildings == null)
			{
				return;
			}

			var seen = new HashSet<string>();
			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				if (spec == null || string.IsNullOrEmpty(spec.id))
				{
					continue;
				}
				BuildingDef def = Assets.GetBuildingDef(spec.id);
				if (def == null)
				{
					continue;
				}
				if (def.ObjectLayer != ObjectLayer.GasConduit
					&& def.ObjectLayer != ObjectLayer.LiquidConduit)
				{
					continue;
				}

				Element element = MaterialOf(spec, def);
				string key = spec.id + "/" + (element != null ? element.id.ToString() : "?");
				if (!seen.Add(key))
				{
					continue;
				}

				float material = element != null ? element.thermalConductivity : 0f;
				var line = new StringBuilder();
				line.Append($"pipe '{spec.id}' built from ");
				line.Append(element != null ? element.id.ToString() : "an unresolved element");
				line.Append($": def x{def.ThermalConductivity:G4} on material {material:G4} ");
				line.Append($"= {def.ThermalConductivity * material:G4} effective");

				string best, worst;
				if (Extremes(def, out best, out worst))
				{
					line.Append($" (of what this pipe can be built from: least {worst}, most {best})");
				}
				advice.Add(line.ToString());
			}
		}

		private static void AdviseConduit(ConduitType type, CellOffset offset, string which,
			BuildingSpec spec, BuildingDef def, int origin, HashSet<long> occupied, List<string> advice)
		{
			if (type == ConduitType.None)
			{
				return;
			}
			// Rotated, as BuildingDef.MarkArea rotates it: a port offset is declared for the unrotated
			// def, and an R270 LiquidConduitBridge was advised about (13,14)/(15,14) while its pipes
			// sat at (14,13)/(14,15).
			int cell = Grid.OffsetCell(origin, Rotatable.GetRotatedCellOffset(offset, spec.orientation));
			if (!Grid.IsValidCell(cell) || occupied.Contains(ConduitKey(cell, type)))
			{
				return;
			}
			Vector2I at = Grid.CellToXY(cell);
			advice.Add($"'{spec.id}' at ({spec.x},{spec.y}) declares a {type} {which} conduit but "
				+ $"there is no {type} pipe at its Utility{(which == "input" ? "Input" : "Output")}Offset "
				+ $"cell ({at.x},{at.y}). Buildings whose Require{(which == "input" ? "Inputs" : "Outputs")} "
				+ "component gates on that flag never become operational.");
		}

		/// <summary>The rating of the <c>Wire</c> component on a wire def's completed prefab.</summary>
		private static float RatingOf(BuildingDef def)
		{
			if (def == null || def.BuildingComplete == null)
			{
				return 0f;
			}
			var wire = def.BuildingComplete.GetComponent<Wire>();
			return wire != null ? Wire.GetMaxWattageAsFloat(wire.MaxWattageRating) : 0f;
		}

		/// <summary>
		/// The cheapest stock wire that carries <paramref name="watts"/>, found by scanning the
		/// loaded building defs rather than by naming ids -- so a mod that adds a wire is offered
		/// too, and a Klei rebalance is followed rather than contradicted.
		/// </summary>
		private static string Upgrade(float watts)
		{
			string best = null;
			float bestRating = float.MaxValue;
			List<BuildingDef> defs = Assets.BuildingDefs;
			for (int i = 0; i < defs.Count; i++)
			{
				BuildingDef def = defs[i];
				if (def == null || def.ObjectLayer != ObjectLayer.Wire)
				{
					continue;
				}
				float rating = RatingOf(def);
				if (rating >= watts && rating < bestRating)
				{
					bestRating = rating;
					best = def.PrefabID;
				}
			}
			return best;
		}

		/// <summary>
		/// The element a spec will actually be built from: what it names, or the def's own first
		/// default. Mirrors <c>BuildingSpec.ToPrefab</c> exactly, because a report about a
		/// material the builder is not going to use is worse than no report.
		/// </summary>
		private static Element MaterialOf(BuildingSpec spec, BuildingDef def)
		{
			if (spec.element != SimHashes.Void)
			{
				return ElementLoader.FindElementByHash(spec.element);
			}
			if (def.DefaultElements() != null && def.DefaultElements().Count > 0)
			{
				return ElementLoader.GetElement(def.DefaultElements()[0]);
			}
			return null;
		}

		/// <summary>
		/// The least and most conductive material this def can legally be built from.
		///
		/// Walks `def.MaterialCategory` against every loaded element, NOT `def.DefaultElements()`.
		/// `DefaultElements` returns the ONE material the build menu preselects -- for
		/// `InsulatedLiquidConduit` that is SuperInsulator -- so the first version of this reported
		/// "least SuperInsulator, most SuperInsulator" and told the reader nothing. A category
		/// entry may be a composite like `Plumbable&amp;Metal`, which is an OR, so it is split.
		/// </summary>
		private static bool Extremes(BuildingDef def, out string best, out string worst)
		{
			best = null;
			worst = null;
			if (def.MaterialCategory == null || def.MaterialCategory.Length == 0)
			{
				return false;
			}

			var wanted = new List<Tag>();
			for (int i = 0; i < def.MaterialCategory.Length; i++)
			{
				string[] parts = def.MaterialCategory[i].Split('&');
				for (int p = 0; p < parts.Length; p++)
				{
					if (!string.IsNullOrEmpty(parts[p]))
					{
						wanted.Add(new Tag(parts[p]));
					}
				}
			}
			if (wanted.Count == 0)
			{
				return false;
			}

			float hi = float.MinValue;
			float lo = float.MaxValue;
			for (int e = 0; e < ElementLoader.elements.Count; e++)
			{
				Element element = ElementLoader.elements[e];
				if (element == null || element.disabled || !element.IsSolid)
				{
					continue;
				}
				bool matches = false;
				for (int t = 0; t < wanted.Count && !matches; t++)
				{
					matches = element.HasTag(wanted[t]);
				}
				if (!matches)
				{
					continue;
				}
				if (element.thermalConductivity > hi)
				{
					hi = element.thermalConductivity;
					best = $"{element.id} {hi:G4}";
				}
				if (element.thermalConductivity < lo)
				{
					lo = element.thermalConductivity;
					worst = $"{element.id} {lo:G4}";
				}
			}
			return best != null && worst != null;
		}

		/// <summary>The conduit a utility piece on <paramref name="layer"/> carries.</summary>
		private static ConduitType ConduitTypeOf(ObjectLayer layer)
		{
			switch (layer)
			{
				case ObjectLayer.GasConduit: return ConduitType.Gas;
				case ObjectLayer.LiquidConduit: return ConduitType.Liquid;
				case ObjectLayer.SolidConduit: return ConduitType.Solid;
				default: return ConduitType.None;
			}
		}

		private static long WireKey(int cell)
		{
			return ((long)(int)ObjectLayer.Wire << 32) | (uint)cell;
		}

		private static long ConduitKey(int cell, ConduitType type)
		{
			return ((long)((int)type + 64) << 32) | (uint)cell;
		}

		private static void Link(Dictionary<int, List<int>> adjacency, int from, int to)
		{
			List<int> list;
			if (!adjacency.TryGetValue(from, out list))
			{
				list = new List<int>();
				adjacency[from] = list;
			}
			list.Add(to);
		}

		private static void PushNeighbour(Dictionary<int, float> ratingAt,
			Dictionary<int, int> circuitOf, Stack<int> stack, int circuit, int x, int y)
		{
			if (x < 0 || y < 0 || x >= Grid.WidthInCells || y >= Grid.HeightInCells)
			{
				return;
			}
			int cell = Grid.XYToCell(x, y);
			if (ratingAt.ContainsKey(cell) && !circuitOf.ContainsKey(cell))
			{
				circuitOf[cell] = circuit;
				stack.Push(cell);
			}
		}
	}
}
