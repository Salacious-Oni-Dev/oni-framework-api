using System;
using System.Collections.Generic;
using Klei.AI;
using TemplateClasses;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The half of a <see cref="RigBlueprint"/> that needs a live game: buildings, the things
	/// already inside them, what is already in the pipes, and the multi-species half of any gas
	/// mixture. Everything that can be true before the sim exists is done by
	/// <see cref="BlueprintWorld"/> instead, because a cell painted into the canvas is simply true
	/// at frame zero rather than modified into place afterwards.
	///
	/// Buildings go through Klei's own <c>TemplateLoader.Stamp</c>. That is not laziness about the
	/// placement code -- it is the only way to get orientation, prefilled <c>Storage</c>, utility
	/// connections and facades applied the way vanilla applies them, and to keep getting them right
	/// when Klei changes that method. The path was proven in this project by
	/// <c>TemplateStampCompressorDemo</c>, whose class comment records the five findings that make
	/// it work, including the one that matters most here: a template <c>Prefab</c>'s location is
	/// the SAME origin cell <c>BuildingDef.Build</c> takes, because
	/// <c>TemplateLoader.PlaceBuilding</c>'s own <c>(WidthInCells - 1) / 2</c> offset and
	/// <c>BuildingDef.GetBuildingCell</c>'s cancel exactly.
	///
	/// <see cref="ValidateAgainstGame"/> is the part worth reading. A five-wide Gas Reservoir does
	/// not occupy the cell you name -- it spans two cells either side of it -- and a building
	/// stamped into solid terrain is placed anyway, silently, ending up behind the tiles rather
	/// than in a room -- a defect only a screenshot shows. It is a load-time error naming the building and the
	/// cells, because <c>BuildingDef.PlacementOffsets</c> is the authoritative answer to "what does
	/// this building actually cover" and nothing else is.
	/// </summary>
	public static class BlueprintBuilder
	{
		/// <summary>
		/// Appliers for <see cref="BuildingSpec.settings"/> ids beyond the three
		/// <c>TemplateLoader</c> itself understands. Keyed by setting id; the framework ships none,
		/// because guessing at building internals produces defects. A mod registers what its own buildings need, and an id with no
		/// applier is reported rather than silently dropped -- which is exactly what Klei's own
		/// <c>other_values</c> switch does to anything outside its three cases.
		/// </summary>
		public static readonly Dictionary<string, System.Action<GameObject, float>> SettingAppliers
			= new Dictionary<string, System.Action<GameObject, float>>();

		/// <summary>
		/// Everything checkable only once the game's building defs and grid exist: every building
		/// id resolves, every footprint is inside the world, no two buildings overlap, and nothing
		/// is about to be stamped into solid terrain.
		///
		/// Run this after <see cref="BlueprintWorld"/> has built the canvas and before any building
		/// is placed. Returns true when <paramref name="errors"/> comes back empty.
		/// </summary>
		public static bool ValidateAgainstGame(RigBlueprint blueprint, out List<string> errors)
		{
			errors = new List<string>();
			if (blueprint == null)
			{
				errors.Add("null blueprint");
				return false;
			}
			if (blueprint.buildings == null || blueprint.buildings.Count == 0)
			{
				return true;
			}

			// Which building claimed which cell ON WHICH OBJECT LAYER, so an overlap can name
			// both sides rather than just saying that one exists.
			//
			// THE LAYER IS PART OF THE KEY BECAUSE ONI'S OWN MODEL PUTS IT THERE.
			// `Grid.Objects[cell, (int)objectLayer]` holds one object per cell PER LAYER, so a
			// wire and a machine occupying the same cell is not an overlap -- it is how every
			// real colony is wired. A generator's power port sits inside its own footprint
			// (PetroleumGenerator's PowerOutputOffset is (1,0), MethaneGenerator's is (0,0)), so
			// a blueprint that cannot run a `Wire` across a generator cannot connect one at all.
			//
			// Keyed on a packed long rather than a tuple, to stay on the language level the rest
			// of this assembly compiles at.
			var claimedBy = new Dictionary<long, string>();

			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec building = blueprint.buildings[i];
				if (building == null || string.IsNullOrEmpty(building.id))
				{
					continue;
				}

				string what = $"buildings[{i}] '{building.id}'";
				BuildingDef def = Assets.GetBuildingDef(building.id);
				if (def == null)
				{
					errors.Add($"{what}: no such building def");
					continue;
				}

				int origin = Grid.XYToCell(building.x, building.y);
				if (!Grid.IsValidCell(origin))
				{
					errors.Add($"{what}: origin ({building.x},{building.y}) is not a valid cell");
					continue;
				}

				List<int> footprint = Footprint(def, origin, building.orientation);
				if (footprint.Count == 0)
				{
					errors.Add($"{what}: resolved to an empty footprint");
					continue;
				}

				// Asked once per building rather than once per cell: a 5x3 reservoir would
				// otherwise repeat the same component lookup fifteen times to reach the same answer.
				bool ignoresSolid = ReplacesItsOwnCell(def) || RunsThroughSolid(def);

				for (int c = 0; c < footprint.Count; c++)
				{
					int cell = footprint[c];
					Vector2I at = Grid.CellToXY(cell);

					long claim = ((long)(int)def.ObjectLayer << 32) | (uint)cell;
					string other;
					if (claimedBy.TryGetValue(claim, out other))
					{
						errors.Add($"{what}: overlaps {other} at ({at.x},{at.y}) on object layer "
							+ $"{def.ObjectLayer}");
					}
					else
					{
						claimedBy[claim] = what;
					}

					// The reservoir defect, as a check. A building stamped into solid terrain is
					// placed regardless and ends up buried, which reads on screen as a
					// mis-positioned rig rather than as a blueprint that painted over its own
					// footprint.
					//
					// EXCEPT FOR TILES AND UTILITIES, WHICH ARE MEANT TO BE IN SOLID GROUND. A
					// foundation tile overwrites the rock it is placed on rather than being buried by
					// it (ReplacesItsOwnCell), and a pipe or wire lives on a layer terrain does not
					// reach at all (RunsThroughSolid). Flagging those was a false positive that cost
					// AIRLOOP its first two canvas runs: 129 errors, then 15, every one of them an
					// InsulationTile sitting on the wall cell it was there to become or a LiquidConduit
					// running inside the insulated floor the rig deliberately routed it through.
					if (Grid.Solid[cell] && !ignoresSolid)
					{
						errors.Add($"{what}: cell ({at.x},{at.y}) is solid "
							+ $"{Grid.Element[cell].id}; the building would be stamped in behind it. "
							+ $"Its footprint is {def.WidthInCells}x{def.HeightInCells} centred on "
							+ $"({building.x},{building.y}), so it spans "
							+ $"{DescribeFootprint(footprint)}.");
					}
				}
			}

			// THE UTILITY NETWORKS, which the footprint pass above cannot see. A circuit whose
			// load exceeds its wire is a hard error -- the game will break that wire mid-run and
			// the rig will keep reporting -- while a missing connection or a pipe's material is
			// reported and left to the author. See BlueprintUtilities for the whole argument and
			// for the run that made it necessary.
			var advice = new List<string>();
			BlueprintUtilities.CheckCircuits(blueprint, errors, advice);
			BlueprintUtilities.CheckConnections(blueprint, advice);
			BlueprintUtilities.ReportConduits(blueprint, advice);

			// Logged ONCE per blueprint. Validation deliberately runs twice on the way to a
			// world -- the harness checks before it commits, `Build` checks again before it
			// stamps -- and the same advisory printed twice reads as two circuits rather than
			// one. Same idiom as `contentsAppliedTo` for the same reason.
			if (!ReferenceEquals(advisedFor, blueprint))
			{
				advisedFor = blueprint;
				for (int i = 0; i < advice.Count; i++)
				{
					Debug.Log($"[OniFramework] BlueprintBuilder: '{blueprint.name}': {advice[i]}");
				}
			}

			return errors.Count == 0;
		}

		/// <summary>
		/// Places every building, then applies contents once the stamp has finished.
		///
		/// <c>TemplateLoader.Stamp</c> is asynchronous across real sim ticks, not a single-frame
		/// call, so <paramref name="onComplete"/> is invoked from its callback rather than on
		/// return. A rig should wait on it the way <c>TemplateStampCompressorDemo</c> does rather
		/// than assuming the buildings exist on the next line.
		///
		/// Validation runs first and refuses the whole build on any error, because half a scenario
		/// is worse than none: it still produces numbers, and they look like physics.
		/// </summary>
		public static bool Build(RigBlueprint blueprint, System.Action onComplete)
		{
			if (blueprint == null)
			{
				FrameworkLog.Error("BlueprintBuilder.Build: null blueprint");
				return false;
			}

			List<string> errors;
			if (!ValidateAgainstGame(blueprint, out errors))
			{
				FrameworkLog.Error($"BlueprintBuilder: blueprint '{blueprint.name}' failed "
					+ $"validation against the live game with {errors.Count} error(s); nothing was built:");
				for (int i = 0; i < errors.Count; i++)
				{
					FrameworkLog.Error("  " + errors[i]);
				}
				return false;
			}

			// BAKE THE UTILITY CONNECTIONS BEFORE STAMPING. Vanilla's stamp path already knows how
			// to apply them -- `TemplateLoader.PlaceBuilding` reads `Prefab.connections` and hands
			// it to `electricalConduitSystem`/`gasConduitSystem`/`liquidConduitSystem.SetConnections`
			// -- but it can only apply what the spec carries, and a blueprint that never filled the
			// field hands it zero. See `ComputeUtilityConnections` for why zero is not "work it out".
			ComputeUtilityConnections(blueprint);

			// A fresh build seeds a fresh world, so the duplicate guard starts clear.
			contentsAppliedTo = null;

			var container = new TemplateContainer
			{
				name = blueprint.name ?? "RigBlueprint",
				buildings = new List<Prefab>(),
			};

			if (blueprint.buildings != null)
			{
				for (int i = 0; i < blueprint.buildings.Count; i++)
				{
					BuildingSpec building = blueprint.buildings[i];
					if (building != null && !string.IsNullOrEmpty(building.id))
					{
						container.buildings.Add(building.ToPrefab());
					}
				}
			}

			if (container.buildings.Count == 0)
			{
				ApplyContents(blueprint);
				if (onComplete != null)
				{
					onComplete();
				}
				return true;
			}

			// Stamped at the origin, so template-local coordinates and the blueprint's absolute
			// canvas coordinates are the same numbers. The blueprint owns the whole world; there is
			// no anchor to be relative to.
			TemplateLoader.Stamp(container, new Vector2(0f, 0f), delegate
			{
				ApplyContents(blueprint);
				if (onComplete != null)
				{
					onComplete();
				}
			});
			return true;
		}

		/// <summary>
		/// Conduit contents, the multi-species half of any mixture fill, and building settings
		/// beyond the three Klei's loader handles.
		///
		/// <see cref="Build"/> ALREADY CALLS THIS from inside the stamp callback, so a rig does
		/// not need to and must not. Calling it a second time DUPLICATES MATTER, and this comment
		/// used to claim the opposite -- "safe to call again; every step is a write of an absolute
		/// value rather than an increment". The write is indeed absolute, and that is exactly the
		/// problem: between the first application and the second the sim has ticked, so some of
		/// the seeded mass has already flowed out of the cells it was written to. Setting those
		/// cells back to their seeded value does not move it back -- it makes a second copy.
		///
		/// Measured by the BRIDGE rig, whose upstream pipe run was seeded with 20 kg
		/// and whose arm then held 30 kg: 20 kg restored upstream plus the 10 kg that had already
		/// crossed the bridge. The rig's own precondition caught it; nothing else would have,
		/// because a duplicated seed looks exactly like a generous one.
		///
		/// A second call is therefore refused and reported. Pass <paramref name="reseed"/> to mean
		/// it -- a rig that deliberately re-charges a pipe mid-run is a legitimate thing to want,
		/// and it should have to say so.
		///
		/// ONE TICK OF LATENCY on the mixture step: every <c>ext::</c> message the gas mixture
		/// facade sends is deferred a tick natively, so a mixture applied here is not observable
		/// until the next one. A rig sampling immediately afterwards reads the pre-conversion
		/// state, which looks exactly like a mixture that never arrived.
		/// </summary>
		public static void ApplyContents(RigBlueprint blueprint)
		{
			ApplyContents(blueprint, reseed: false);
		}

		/// <summary>
		/// <see cref="ApplyContents(RigBlueprint)"/>, with the duplicate guard lifted when
		/// <paramref name="reseed"/> is true.
		/// </summary>
		public static void ApplyContents(RigBlueprint blueprint, bool reseed)
		{
			if (blueprint == null)
			{
				return;
			}
			if (!reseed && ReferenceEquals(contentsAppliedTo, blueprint))
			{
				Debug.LogWarning("[OniFramework] BlueprintBuilder: ApplyContents was called again "
					+ $"for '{blueprint.name}' and was REFUSED. Build() already applies contents "
					+ "from inside the stamp callback; a second application would duplicate every "
					+ "gram that had already flowed out of a seeded cell. Delete the redundant "
					+ "call, or pass reseed:true if a mid-run recharge is what was meant.");
				return;
			}
			contentsAppliedTo = blueprint;
			ApplyConduits(blueprint);
			ApplyMixtures(blueprint);
			ApplySettings(blueprint);
		}

		/// <summary>
		/// The blueprint whose contents have already been applied, so a second application can be
		/// refused rather than silently doubling the world's matter. Cleared by <see cref="Build"/>
		/// so that rebuilding the same blueprint object in one process still seeds it.
		/// </summary>
		private static RigBlueprint contentsAppliedTo;

		/// <summary>The blueprint whose utility advisories have already been printed.</summary>
		private static RigBlueprint advisedFor;

		/// <summary>
		/// Writes conduit contents with <c>ConduitFlow.SetContents</c>, deliberately not
		/// <c>AddElement</c>: AddElement clamps to the per-tile cap and blends the incoming
		/// temperature into whatever is already in the tile, so a blueprint asking for a specific
		/// mass at a specific temperature would quietly get neither. Recorded in
		/// <see cref="PipeMatterFacade"/>, which made the same choice for the same reason.
		/// </summary>
		private static void ApplyConduits(RigBlueprint blueprint)
		{
			if (blueprint.conduits == null || blueprint.conduits.Count == 0 || Game.Instance == null)
			{
				return;
			}

			int written = 0;
			int skipped = 0;

			for (int i = 0; i < blueprint.conduits.Count; i++)
			{
				ConduitSpec spec = blueprint.conduits[i];
				if (spec == null)
				{
					continue;
				}

				int cell = Grid.XYToCell(spec.x, spec.y);
				if (!Grid.IsValidCell(cell))
				{
					Debug.LogWarning($"[OniFramework] BlueprintBuilder: conduits[{i}] at "
						+ $"({spec.x},{spec.y}) is not a valid cell");
					skipped++;
					continue;
				}

				ObjectLayer layer = spec.type == ConduitType.Liquid
					? ObjectLayer.LiquidConduit
					: ObjectLayer.GasConduit;
				if (Grid.Objects[cell, (int)layer] == null)
				{
					Debug.LogWarning($"[OniFramework] BlueprintBuilder: conduits[{i}] at "
						+ $"({spec.x},{spec.y}) asks for {spec.type} contents but there is no "
						+ $"{spec.type} conduit there; the blueprint has to build the pipe before it "
						+ "can fill it");
					skipped++;
					continue;
				}

				ConduitFlow flow = spec.type == ConduitType.Liquid
					? Game.Instance.liquidConduitFlow
					: Game.Instance.gasConduitFlow;

				byte diseaseIdx = string.IsNullOrEmpty(spec.diseaseName)
					? byte.MaxValue
					: Db.Get().Diseases.GetIndex(spec.diseaseName);

				flow.SetContents(cell, new ConduitFlow.ConduitContents(
					spec.element, spec.mass, spec.temperature, diseaseIdx, spec.diseaseCount));
				written++;
			}

			Debug.Log($"[OniFramework] BlueprintBuilder: filled {written} conduit tile(s), "
				+ $"skipped {skipped}.");
		}

		/// <summary>
		/// The runtime half of a mixture fill. <see cref="BlueprintWorld"/> already put the fill's
		/// TOTAL mass into every cell as the FIRST listed species, because a sim cell holds exactly
		/// one element. This promotes each cell's room to the new engine and converts the remaining
		/// species across with <c>GasMixtureFacade.ConvertFromVanilla</c>, which injects into the
		/// mixture layer and takes the same amount back out of the vanilla layer in one pair -- so
		/// the total mass in the cell is unchanged by construction, not by arithmetic that has to
		/// be checked.
		/// </summary>
		private static void ApplyMixtures(RigBlueprint blueprint)
		{
			if (blueprint.fills == null || blueprint.fills.Count == 0)
			{
				return;
			}

			int converted = 0;

			for (int i = 0; i < blueprint.fills.Count; i++)
			{
				FillSpec fill = blueprint.fills[i];
				if (fill == null || fill.mixture == null || fill.mixture.Count < 2)
				{
					continue;
				}

				RegionSpec rect = RectOf(blueprint, fill);
				if (rect == null)
				{
					continue;
				}

				foreach (Vector2I point in rect.Cells())
				{
					int cell = Grid.XYToCell(point.x, point.y);
					if (!Grid.IsValidCell(cell))
					{
						continue;
					}

					GasMixtureFacade.PromoteRoom(cell);

					// From index 1: species 0 is already in the cell as its vanilla element, put
					// there by the canvas, and converting it too would leave the cell with no
					// vanilla mass for the others to be taken from.
					for (int s = 1; s < fill.mixture.Count; s++)
					{
						SpeciesSpec species = fill.mixture[s];
						if (species == null || species.mass <= 0f)
						{
							continue;
						}
						Element element = ElementLoader.FindElementByHash(species.element);
						if (element == null)
						{
							continue;
						}
						GasMixtureFacade.ConvertFromVanilla(cell,
							ElementLoader.elements.IndexOf(element), species.mass);
						converted++;
					}
				}
			}

			if (converted > 0)
			{
				Debug.Log($"[OniFramework] BlueprintBuilder: converted {converted} species-cell(s) into "
					+ "the gas mixture layer. Native message effects are deferred one tick, so these "
					+ "are not readable until the next.");
			}
		}

		/// <summary>
		/// Building settings that Klei's own <c>other_values</c> switch does not handle. Tries the
		/// game's <c>Amounts</c> first, since that is the same mechanism the template schema's
		/// <c>amounts</c> list uses, then <see cref="SettingAppliers"/>. An id matched by neither is
		/// logged as an error: a setting that silently does not happen is a scenario that silently
		/// is not the scenario the blueprint describes.
		/// </summary>
		private static void ApplySettings(RigBlueprint blueprint)
		{
			if (blueprint.buildings == null)
			{
				return;
			}

			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec building = blueprint.buildings[i];
				if (building == null || building.settings == null || building.settings.Count == 0)
				{
					continue;
				}

				int cell = Grid.XYToCell(building.x, building.y);
				if (!Grid.IsValidCell(cell))
				{
					continue;
				}

				GameObject placed = Grid.Objects[cell, (int)ObjectLayer.Building];
				if (placed == null)
				{
					FrameworkLog.Error($"BlueprintBuilder: buildings[{i}] '{building.id}' has "
						+ "settings but no building was found at its cell after the stamp");
					continue;
				}

				foreach (KeyValuePair<string, float> setting in building.settings)
				{
					if (BuildingSpec.IsKleiOtherValue(setting.Key))
					{
						// Already emitted as an other_value and applied by TemplateLoader.
						continue;
					}

					if (Db.Get().Amounts.Get(setting.Key) != null)
					{
						placed.GetAmounts().SetValue(setting.Key, setting.Value);
						continue;
					}

					System.Action<GameObject, float> applier;
					if (SettingAppliers.TryGetValue(setting.Key, out applier))
					{
						applier(placed, setting.Value);
						continue;
					}

					FrameworkLog.Error($"BlueprintBuilder: buildings[{i}] '{building.id}' asks "
						+ $"for setting '{setting.Key}' = {setting.Value}, which is neither a game Amount "
						+ "nor a registered applier. Register one in BlueprintBuilder.SettingAppliers; "
						+ "it has NOT been applied.");
				}
			}
		}

		/// <summary>
		/// Every cell a building actually covers, resolved through
		/// <c>BuildingDef.PlacementOffsets</c> and rotated the way the building will be. This is the
		/// authoritative answer; a building's own cell is not its corner, and for anything wider
		/// than one tile it is not even inside the same column as its left edge.
		/// </summary>
		/// <summary>
		/// Fills in every utility building's <c>connections</c> bitmask from the blueprint's own
		/// adjacency, so that Klei's stamp path has something real to apply.
		///
		/// WHY THIS IS NEEDED AT ALL, since it looks like something the game should do. It does
		/// not, and the reason is exact (<c>UtilityNetworkManager&lt;,&gt;</c>):
		/// <c>RebuildNetworks</c>'s flood fill steps from a cell to its neighbour only when that
		/// cell's STORED bitmask has the matching direction bit, and <c>SetConnections</c> ANDs
		/// the caller's value against live adjacency instead of replacing it. So a piece that
		/// arrives with <c>None</c> keeps <c>None</c> no matter how many real neighbours it has,
		/// and every wire and pipe becomes a permanent one-node network.
		///
		/// HOW IT HID. A generator counts as connected when <c>CircuitID != ushort.MaxValue</c>,
		/// and a one-tile circuit satisfies that. So a rig passes its power checks while two
		/// generators on what looks like a shared bus are on separate circuits and the tiles are
		/// visibly disconnected stubs -- a defect only someone looking at the screen sees.
		///
		/// SAME OBJECT LAYER IS THE CRITERION, not a hardcoded list of prefab ids. Two utility
		/// pieces share a network precisely when they occupy the same <c>ObjectLayer</c>
		/// (<c>Grid.Objects[cell, (int)layer]</c>), which is the same rule
		/// <see cref="ValidateAgainstGame"/> uses for overlap. It picks up any wire, pipe or logic
		/// run a mod adds without this method having heard of it.
		///
		/// NO BIT IS SET TOWARDS A MACHINE, and none is needed: a building's power or utility port
		/// lives at a cell the run passes THROUGH, on a different layer, so the run only has to
		/// reach that cell. That is why a wire spine crossing a generator works.
		///
		/// ONLY 1x1 PIECES, and only ones that left <c>connections</c> at zero. A multi-cell
		/// utility piece (a bridge) has ends that mean different things and is left to state its
		/// own mask; an explicit non-zero value is always honoured, so a blueprint can still
		/// describe a deliberately broken run.
		/// </summary>
		private static void ComputeUtilityConnections(RigBlueprint blueprint)
		{
			if (blueprint == null || blueprint.buildings == null)
			{
				return;
			}

			// KEYED BY (LAYER, CELL), NOT BY CELL. A wire and a pipe routinely share a tile --
			// ONI keeps one object per cell PER LAYER -- so a cell-keyed set silently drops one of
			// them. Caught live on this blueprint's first bake: it reported "17 of 17 utility
			// pieces" for a world holding 13 wires and 8 conduits, because the four tiles carrying
			// both collapsed into one entry each, and the shadowed pieces got their neighbours
			// computed against the wrong layer -- the same cell-versus-layer distinction
			// `ValidateAgainstGame` makes.
			var occupied = new HashSet<long>();
			int pieces = 0;
			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				ObjectLayer layer;
				if (!TryUtilityLayer(spec, out layer))
				{
					continue;
				}
				occupied.Add(Claim(Grid.XYToCell(spec.x, spec.y), layer));
				pieces++;
			}
			if (occupied.Count == 0)
			{
				return;
			}

			int baked = 0;
			for (int i = 0; i < blueprint.buildings.Count; i++)
			{
				BuildingSpec spec = blueprint.buildings[i];
				ObjectLayer layer;
				if (!TryUtilityLayer(spec, out layer) || spec.connections != 0)
				{
					continue;
				}

				UtilityConnections mask = (UtilityConnections)0;
				if (Neighbour(occupied, spec.x - 1, spec.y, layer)) mask |= UtilityConnections.Left;
				if (Neighbour(occupied, spec.x + 1, spec.y, layer)) mask |= UtilityConnections.Right;
				if (Neighbour(occupied, spec.x, spec.y + 1, layer)) mask |= UtilityConnections.Up;
				if (Neighbour(occupied, spec.x, spec.y - 1, layer)) mask |= UtilityConnections.Down;

				if (mask != (UtilityConnections)0)
				{
					spec.connections = (int)mask;
					baked++;
				}
			}

			if (baked > 0)
			{
				Debug.Log($"[OniFramework] BlueprintBuilder: baked utility connections for {baked} "
					+ $"of {pieces} utility piece(s) in '{blueprint.name}'.");
			}
		}

		/// <summary>
		/// Whether <paramref name="spec"/> is a single-tile utility piece, and on which layer.
		/// Anything that is not on a network layer, or is bigger than one tile, is not baked.
		/// </summary>
		private static bool TryUtilityLayer(BuildingSpec spec, out ObjectLayer layer)
		{
			layer = ObjectLayer.NumLayers;
			if (spec == null || string.IsNullOrEmpty(spec.id))
			{
				return false;
			}
			BuildingDef def = Assets.GetBuildingDef(spec.id);
			if (def == null || def.WidthInCells != 1 || def.HeightInCells != 1)
			{
				return false;
			}
			switch (def.ObjectLayer)
			{
				case ObjectLayer.Wire:
				case ObjectLayer.GasConduit:
				case ObjectLayer.LiquidConduit:
				case ObjectLayer.SolidConduit:
				case ObjectLayer.LogicWire:
					layer = def.ObjectLayer;
					return true;
				default:
					return false;
			}
		}

		private static bool Neighbour(HashSet<long> occupied, int x, int y, ObjectLayer layer)
		{
			if (x < 0 || y < 0 || x >= Grid.WidthInCells || y >= Grid.HeightInCells)
			{
				return false;
			}
			return occupied.Contains(Claim(Grid.XYToCell(x, y), layer));
		}

		/// <summary>
		/// One cell on one object layer, as a single key. Same composition
		/// <see cref="ValidateAgainstGame"/> uses, and for the same reason: a cell alone is not a
		/// unique place for a utility piece to be.
		/// </summary>
		private static long Claim(int cell, ObjectLayer layer)
		{
			return ((long)(int)layer << 32) | (uint)cell;
		}

		public static List<int> Footprint(BuildingDef def, int cell, Orientation orientation)
		{
			var cells = new List<int>();
			if (def == null || !Grid.IsValidCell(cell) || def.PlacementOffsets == null)
			{
				return cells;
			}

			foreach (CellOffset offset in def.PlacementOffsets)
			{
				CellOffset rotated = Rotatable.GetRotatedCellOffset(offset, orientation);
				int target = Grid.OffsetCell(cell, rotated);
				if (Grid.IsValidCell(target) && !cells.Contains(target))
				{
					cells.Add(target);
				}
			}
			return cells;
		}

		/// <summary>The footprint as an inclusive coordinate range, for error messages.</summary>
		/// <summary>
		/// True when this building sits on a layer that terrain does not occupy -- pipes, wires,
		/// logic and conveyor rails -- so a solid cell underneath it is not an obstruction.
		///
		/// WHY THE LAYER AND NOT THE BUILD RULE. A conduit declares
		/// <c>BuildLocationRule.Anywhere</c>, and <c>BuildingDef.IsValidBuildLocation</c> returns
		/// true for that case without ever consulting <c>Grid.Solid</c> -- so the game agrees a
		/// buried pipe is legal, and running one inside a tile is the ordinary way to plumb a farm.
		/// But 123 of Klei's buildings share that rule, including GasPump, GasVent and
		/// DevGenerator, and a pump buried in rock IS the mis-placement this check exists to catch.
		/// <c>ObjectLayer</c> separates the two: a machine claims <c>Building</c>, and terrain and
		/// utilities never contend for a cell on any of the layers below.
		/// </summary>
		private static bool RunsThroughSolid(BuildingDef def)
		{
			if (def == null)
			{
				return false;
			}

			switch (def.ObjectLayer)
			{
				case ObjectLayer.GasConduit:
				case ObjectLayer.GasConduitConnection:
				case ObjectLayer.LiquidConduit:
				case ObjectLayer.LiquidConduitConnection:
				case ObjectLayer.SolidConduit:
				case ObjectLayer.SolidConduitConnection:
				case ObjectLayer.Wire:
				case ObjectLayer.WireConnectors:
				case ObjectLayer.LogicWire:
				case ObjectLayer.LogicGate:
				case ObjectLayer.TravelTube:
				case ObjectLayer.TravelTubeConnection:
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		/// True when this building overwrites the element of every cell it covers, rather than
		/// being placed into it -- foundation tiles, window tiles, farm tiles and the like.
		///
		/// WHY THE SOLID CHECK NEEDS THIS. <c>SimCellOccupier.OnSpawn</c> runs
		/// <c>ReplaceAndDisplaceElement(cell, primaryElement.ElementID, ...)</c> over its whole
		/// area whenever <c>doReplaceElement</c> is set, which it is by default. So a tile stamped
		/// onto solid rock does not end up behind that rock -- the rock ends up as the tile. A rig
		/// that paints a solid block and then puts its insulated walls on the block's own wall
		/// cells is doing the ordinary thing, and the validator flagging all 129 of them told the
		/// author to go looking for a misplacement that was not there.
		///
		/// Read off the completed prefab rather than inferred from <c>def.IsFoundation</c>: the
		/// two agree for Klei's tiles, but <c>doReplaceElement</c> is the field the sim message is
		/// actually gated on, and a mod building may set either one without the other.
		/// </summary>
		private static bool ReplacesItsOwnCell(BuildingDef def)
		{
			if (def == null || def.BuildingComplete == null)
			{
				return false;
			}

			bool answer;
			if (replacesOwnCellByDef.TryGetValue(def.PrefabID, out answer))
			{
				return answer;
			}

			SimCellOccupier occupier = def.BuildingComplete.GetComponent<SimCellOccupier>();
			answer = occupier != null && occupier.doReplaceElement;
			replacesOwnCellByDef[def.PrefabID] = answer;
			return answer;
		}

		/// <summary>
		/// Memo for <see cref="ReplacesItsOwnCell"/>, keyed on the def's prefab id. Validation runs
		/// twice on the way to a world and a blueprint may hold a hundred tiles of one kind, so the
		/// component lookup is worth doing once per building type per session.
		/// </summary>
		private static readonly Dictionary<string, bool> replacesOwnCellByDef
			= new Dictionary<string, bool>();

		private static string DescribeFootprint(List<int> footprint)
		{
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			for (int i = 0; i < footprint.Count; i++)
			{
				Vector2I at = Grid.CellToXY(footprint[i]);
				minX = Mathf.Min(minX, at.x);
				minY = Mathf.Min(minY, at.y);
				maxX = Mathf.Max(maxX, at.x);
				maxY = Mathf.Max(maxY, at.y);
			}
			return $"({minX},{minY})..({maxX},{maxY})";
		}

		private static RegionSpec RectOf(RigBlueprint blueprint, FillSpec fill)
		{
			if (!string.IsNullOrEmpty(fill.region))
			{
				return blueprint.FindRegion(fill.region);
			}
			if (fill.w > 0 && fill.h > 0)
			{
				return new RegionSpec { name = "inline", x = fill.x, y = fill.y, w = fill.w, h = fill.h };
			}
			return null;
		}
	}
}
