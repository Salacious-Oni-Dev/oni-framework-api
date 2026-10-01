using System;
using System.Collections.Generic;
using System.IO;
using Klei;
using ProcGen;
using TemplateClasses;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The declarative description of a test scenario: the world it needs, every cell in that
	/// world, every building, how those buildings are turned and filled, what is already sitting
	/// in their pipes, and the thermal jacket around the whole thing. A rig loads one of these and
	/// builds itself from it; nothing about the scenario is computed from whatever terrain
	/// happened to be under the rig, because there is no inherited terrain -- see
	/// <see cref="BlueprintWorld"/>.
	///
	/// WHY THIS EXISTS AT ALL, given ONI already has a template format. It reuses ONI's, and only
	/// extends it. <c>TemplateClasses.Cell</c> and <c>TemplateClasses.StorageItem</c> are used
	/// verbatim here, and <see cref="BuildingSpec.ToPrefab"/> emits a real
	/// <c>TemplateClasses.Prefab</c> so that placement can go through Klei's own
	/// <c>TemplateLoader</c> -- which already handles orientation, prefilled storage, utility
	/// connections and facades, and which stays correct when Klei changes it. Five things a rig
	/// needs are genuinely absent from that schema, and only those are added:
	///
	///   1. A CANVAS. Templates stamp onto terrain that already exists; they cannot say how big
	///      the world is, what it is made of, or where it starts. <see cref="CanvasSpec"/>.
	///   2. CONDUIT CONTENTS. Nothing in the template schema can prefill a pipe -- ConduitFlow's
	///      contents are not part of it. <see cref="ConduitSpec"/>.
	///   3. MULTI-SPECIES GAS. <c>Cell.element</c> is one <c>SimHashes</c>; this project's
	///      simulation holds several species per cell (<see cref="GasMixtureFacade"/>).
	///      <see cref="FillSpec.mixture"/>.
	///   4. RIG METADATA. Test volume, vacuum jacket, claimed region -- harness concepts vanilla
	///      has no reason to model. <see cref="JacketSpec"/>, <see cref="RegionSpec"/>.
	///   5. ARBITRARY BUILDING SETTINGS. <c>TemplateLoader</c>'s <c>other_values</c> switch
	///      handles exactly three ids (<c>joulesAvailable</c>, <c>sealedDoorDirection</c>,
	///      <c>switchSetting</c>) and silently ignores everything else.
	///      <see cref="BuildingSpec.settings"/>.
	///
	/// COORDINATES ARE ABSOLUTE, in canvas space, with (0,0) at the world's bottom-left. There is
	/// deliberately no anchor and no rig-relative offset: the rig owns the entire world, so there
	/// is nothing to be relative to. Every scenario bug of the form "the rig landed somewhere it
	/// did not expect relative to its anchor" is unrepresentable in this format rather than
	/// guarded against.
	/// </summary>
	[Serializable]
	public class RigBlueprint
	{
		/// <summary>Identifies the blueprint in logs and in validation errors.</summary>
		public string name { get; set; }

		/// <summary>The world to generate. Required.</summary>
		public CanvasSpec canvas { get; set; }

		/// <summary>Named rectangles, so fills and the jacket can refer to one region by name
		/// instead of repeating four numbers that then drift apart.</summary>
		public List<RegionSpec> regions { get; set; }

		/// <summary>Rectangular element fills, applied in order, after the background and border
		/// and before <see cref="cells"/>.</summary>
		public List<FillSpec> fills { get; set; }

		/// <summary>Per-tile overrides, using ONI's own template cell type verbatim. Applied last,
		/// so a single awkward tile can override whatever fill covered it.</summary>
		public List<Cell> cells { get; set; }

		/// <summary>Buildings, placed through Klei's <c>TemplateLoader</c>.</summary>
		public List<BuildingSpec> buildings { get; set; }

		/// <summary>Contents to put into conduits once they exist.</summary>
		public List<ConduitSpec> conduits { get; set; }

		/// <summary>The vacuum jacket and sealing shell (the demo checklist's rule 5).</summary>
		public JacketSpec jacket { get; set; }

		/// <summary>
		/// Let a circuit draw more than its wire is rated for. Default false, and it should stay
		/// false: an overloaded circuit damages a random wire on it every 6 s, a canvas has no
		/// duplicant to repair one, and the resulting mid-run power cut is invisible to a rig that
		/// measures anything but wires. Set it only for a rig whose SUBJECT is the overload, which
		/// turns the error into a logged advisory so the intent is on the transcript.
		/// See <see cref="BlueprintUtilities.CheckCircuits"/>.
		/// </summary>
		public bool allowCircuitOverload { get; set; }

		/// <summary>
		/// Reads a blueprint from a YAML file using Klei's own <c>YamlIO</c> -- the same parser
		/// and the same conventions as vanilla template and worldgen yaml, so element names,
		/// enums and nested lists all behave the way they do in the game's own data files.
		/// Returns null and logs on a parse failure rather than throwing into a rig.
		/// </summary>
		public static RigBlueprint Load(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				FrameworkLog.Error("RigBlueprint.Load: no such file: " + path);
				return null;
			}

			RigBlueprint blueprint = YamlIO.LoadFile<RigBlueprint>(path, delegate(YamlIO.Error error, bool force_log_as_warning)
			{
				FrameworkLog.Error("RigBlueprint.Load: " + path + ": " + error.message);
			});

			if (blueprint == null)
			{
				FrameworkLog.Error("RigBlueprint.Load: parsed null from " + path);
			}
			return blueprint;
		}

		/// <summary>
		/// Resolves a region by name. Returns null when <paramref name="regionName"/> is null or
		/// unknown, so callers can treat "no region named" and "region named but missing" the same
		/// way only when they mean to.
		/// </summary>
		public RegionSpec FindRegion(string regionName)
		{
			if (string.IsNullOrEmpty(regionName) || regions == null)
			{
				return null;
			}
			for (int i = 0; i < regions.Count; i++)
			{
				if (regions[i] != null && regions[i].name == regionName)
				{
					return regions[i];
				}
			}
			return null;
		}

		/// <summary>
		/// Everything checkable without a live game: required sections present, sizes sane, every
		/// referenced region actually declared, every coordinate inside the canvas, every element
		/// name real. Building footprints are NOT checked here -- that needs
		/// <c>BuildingDef.PlacementOffsets</c>, so it lives in
		/// <see cref="BlueprintBuilder.ValidateAgainstGame"/> and runs once the defs exist.
		///
		/// Returns true when <paramref name="errors"/> comes back empty. Errors name the offending
		/// entry by index and by what it said, because a blueprint that fails to load is otherwise
		/// a wall of coordinates with no indication which line is wrong.
		/// </summary>
		public bool Validate(out List<string> errors)
		{
			errors = new List<string>();

			if (canvas == null)
			{
				errors.Add("no 'canvas' section; a blueprint must say what world to build");
				return false;
			}
			if (canvas.width < 16 || canvas.height < 16)
			{
				errors.Add($"canvas is {canvas.width}x{canvas.height}; both sides must be at least 16");
			}
			if (canvas.borderThickness < 1)
			{
				errors.Add($"canvas.borderThickness is {canvas.borderThickness}; the world needs a solid "
					+ "border of at least 1 or the sim flows off its own edge");
			}
			if (canvas.borderThickness * 2 >= canvas.width || canvas.borderThickness * 2 >= canvas.height)
			{
				errors.Add($"canvas.borderThickness {canvas.borderThickness} leaves no interior in a "
					+ $"{canvas.width}x{canvas.height} canvas");
			}
			if (!IsInsideInterior(canvas.startX, canvas.startY))
			{
				errors.Add($"canvas.start ({canvas.startX},{canvas.startY}) is outside the canvas interior; "
					+ "it sets the camera and the initial reveal, so it must be somewhere the rig actually is");
			}
			ValidateElement("canvas.background", canvas.background, errors);
			ValidateElement("canvas.border", canvas.border, errors);

			if (regions != null)
			{
				var seen = new HashSet<string>();
				for (int i = 0; i < regions.Count; i++)
				{
					RegionSpec region = regions[i];
					if (region == null)
					{
						errors.Add($"regions[{i}] is empty");
						continue;
					}
					if (string.IsNullOrEmpty(region.name))
					{
						errors.Add($"regions[{i}] has no name");
					}
					else if (!seen.Add(region.name))
					{
						errors.Add($"regions[{i}]: duplicate region name '{region.name}'");
					}
					if (region.w <= 0 || region.h <= 0)
					{
						errors.Add($"regions[{i}] '{region.name}' is {region.w}x{region.h}; both sides must be positive");
					}
					RequireInsideInterior($"regions[{i}] '{region.name}'", region.x, region.y, region.w, region.h, errors);
				}
			}

			if (fills != null)
			{
				for (int i = 0; i < fills.Count; i++)
				{
					FillSpec fill = fills[i];
					if (fill == null)
					{
						errors.Add($"fills[{i}] is empty");
						continue;
					}
					RegionSpec resolved = ResolveRect(fill.region, fill.x, fill.y, fill.w, fill.h, $"fills[{i}]", errors);
					if (resolved != null)
					{
						RequireInsideInterior($"fills[{i}]", resolved.x, resolved.y, resolved.w, resolved.h, errors);
					}
					if (fill.mixture != null && fill.mixture.Count > 0)
					{
						for (int s = 0; s < fill.mixture.Count; s++)
						{
							SpeciesSpec species = fill.mixture[s];
							if (species == null)
							{
								errors.Add($"fills[{i}].mixture[{s}] is empty");
								continue;
							}
							ValidateElement($"fills[{i}].mixture[{s}].element", species.element, errors);
							if (species.mass <= 0f)
							{
								errors.Add($"fills[{i}].mixture[{s}] has mass {species.mass}; must be positive");
							}
						}
					}
					else
					{
						ValidateElement($"fills[{i}].element", fill.element, errors);
					}
					if (fill.temperature <= 0f)
					{
						errors.Add($"fills[{i}] has temperature {fill.temperature}; must be above absolute zero");
					}
				}
			}

			if (cells != null)
			{
				for (int i = 0; i < cells.Count; i++)
				{
					Cell cell = cells[i];
					if (cell == null)
					{
						errors.Add($"cells[{i}] is empty");
						continue;
					}
					if (!IsInsideInterior(cell.location_x, cell.location_y))
					{
						errors.Add($"cells[{i}] at ({cell.location_x},{cell.location_y}) is outside the canvas interior");
					}
					ValidateElement($"cells[{i}].element", cell.element, errors);
				}
			}

			if (buildings != null)
			{
				for (int i = 0; i < buildings.Count; i++)
				{
					BuildingSpec building = buildings[i];
					if (building == null)
					{
						errors.Add($"buildings[{i}] is empty");
						continue;
					}
					if (string.IsNullOrEmpty(building.id))
					{
						errors.Add($"buildings[{i}] has no id");
					}
					if (!IsInsideInterior(building.x, building.y))
					{
						errors.Add($"buildings[{i}] '{building.id}' at ({building.x},{building.y}) is outside "
							+ "the canvas interior");
					}
					if (building.temperature <= 0f)
					{
						errors.Add($"buildings[{i}] '{building.id}' has temperature {building.temperature}; "
							+ "must be above absolute zero");
					}
				}
			}

			if (conduits != null)
			{
				for (int i = 0; i < conduits.Count; i++)
				{
					ConduitSpec conduit = conduits[i];
					if (conduit == null)
					{
						errors.Add($"conduits[{i}] is empty");
						continue;
					}
					if (!IsInsideInterior(conduit.x, conduit.y))
					{
						errors.Add($"conduits[{i}] at ({conduit.x},{conduit.y}) is outside the canvas interior");
					}
					ValidateElement($"conduits[{i}].element", conduit.element, errors);
					if (conduit.mass <= 0f)
					{
						errors.Add($"conduits[{i}] has mass {conduit.mass}; must be positive");
					}
					if (conduit.temperature <= 0f)
					{
						errors.Add($"conduits[{i}] has temperature {conduit.temperature}; must be above absolute zero");
					}
				}
			}

			if (jacket != null)
			{
				RegionSpec resolved = ResolveRect(jacket.region, jacket.x, jacket.y, jacket.w, jacket.h, "jacket", errors);
				if (resolved != null)
				{
					// The jacket paints OUTSIDE the region it protects: one vacuum ring, then the
					// shell. Both have to fit between the region and the world border, or the shell
					// is silently clipped and the "sealed" volume is not sealed -- which reads as a
					// thermodynamics bug rather than as a geometry one.
					int margin = jacket.vacuumRing + 1;
					RequireInsideInterior("jacket (region plus its rings)",
						resolved.x - margin, resolved.y - margin,
						resolved.w + margin * 2, resolved.h + margin * 2, errors);
				}
				if (jacket.vacuumRing < 1)
				{
					errors.Add($"jacket.vacuumRing is {jacket.vacuumRing}; a jacket with no vacuum gap "
						+ "conducts straight through and is not a jacket");
				}
				ValidateElement("jacket.shell", jacket.shell, errors);
			}

			return errors.Count == 0;
		}

		/// <summary>
		/// Resolves either a named region or an inline rectangle into one rectangle, recording an
		/// error when the caller gave neither, both, or a name that was never declared.
		/// </summary>
		private RegionSpec ResolveRect(string regionName, int x, int y, int w, int h, string what,
			List<string> errors)
		{
			bool hasInline = w > 0 && h > 0;
			bool hasName = !string.IsNullOrEmpty(regionName);

			if (hasName && hasInline)
			{
				errors.Add($"{what} gives both region '{regionName}' and an inline rectangle; use one");
				return null;
			}
			if (hasName)
			{
				RegionSpec region = FindRegion(regionName);
				if (region == null)
				{
					errors.Add($"{what} refers to region '{regionName}', which is not declared");
				}
				return region;
			}
			if (hasInline)
			{
				return new RegionSpec { name = what, x = x, y = y, w = w, h = h };
			}
			errors.Add($"{what} has neither a region name nor a rectangle");
			return null;
		}

		/// <summary>True when the cell is inside the canvas and outside the solid border.</summary>
		private bool IsInsideInterior(int x, int y)
		{
			int t = canvas.borderThickness;
			return x >= t && y >= t && x < canvas.width - t && y < canvas.height - t;
		}

		private void RequireInsideInterior(string what, int x, int y, int w, int h, List<string> errors)
		{
			if (w <= 0 || h <= 0)
			{
				return;
			}
			if (!IsInsideInterior(x, y) || !IsInsideInterior(x + w - 1, y + h - 1))
			{
				errors.Add($"{what} spans ({x},{y})..({x + w - 1},{y + h - 1}), which leaves the "
					+ $"interior of a {canvas.width}x{canvas.height} canvas with a "
					+ $"{canvas.borderThickness}-thick border");
			}
		}

		private static void ValidateElement(string what, SimHashes id, List<string> errors)
		{
			if (ElementLoader.FindElementByHash(id) == null)
			{
				errors.Add($"{what} names element '{id}', which is not registered");
			}
		}
	}

	/// <summary>
	/// The world itself: how big, what every cell starts as, what the outer wall is made of, and
	/// where the camera opens. Sizes are in cells, temperatures in kelvin, masses in kilograms.
	/// </summary>
	[Serializable]
	public class CanvasSpec
	{
		/// <summary>Canvas width in cells, border included.</summary>
		public int width { get; set; } = 128;

		/// <summary>Canvas height in cells, border included.</summary>
		public int height { get; set; } = 96;

		/// <summary>What every interior cell holds before any fill runs. Vacuum by default, which
		/// is the point of the blank canvas: a rig starts from nothing and adds only what it
		/// asked for.</summary>
		public SimHashes background { get; set; } = SimHashes.Vacuum;

		/// <summary>Mass per interior cell of <see cref="background"/>. Ignored for Vacuum, which
		/// is always massless.</summary>
		public float backgroundMass { get; set; }

		/// <summary>Temperature of the background and of the border.</summary>
		public float backgroundTemperature { get; set; } = 293.15f;

		/// <summary>The world's outer wall. Unobtanium (Neutronium in game strings) is
		/// indestructible and conducts nothing, which is what a wall around a thermal test should
		/// be.</summary>
		public SimHashes border { get; set; } = SimHashes.Unobtanium;

		/// <summary>How many cells thick the border is. Vanilla's own smallest world
		/// (worlds/TinyEmpty) uses 1.</summary>
		public int borderThickness { get; set; } = 1;

		/// <summary>
		/// The subworld zone every cell on the canvas reports as. Defaults to
		/// <c>Sandstone</c>, matching the world a canvas borrows its settings from.
		///
		/// IT DEFAULTS TO SOMETHING BECAUSE THE GAME'S OWN DEFAULT IS `Space`, AND THAT IS WRONG
		/// FOR A CANVAS. `SubworldZoneRenderData.GenerateTexture` seeds every cell of
		/// `worldZoneTypes` to `SubWorld.ZoneType.Space` and then overwrites only the cells covered
		/// by a polygon in `SaveLoader.Instance.clusterDetailSave.overworldCells`.
		/// <see cref="BlueprintWorld"/> installs an EMPTY `WorldDetailSave` -- deliberately, since
		/// a canvas has no biomes -- so that list has no entries, the overwrite loop never runs,
		/// and every cell keeps the seeded value.
		///
		/// That is not cosmetic. `Grid.IsCellBiomeSpaceBiome` reads exactly that array, and
		/// `Grid.IsCellOpenToSpace` is it plus three gates -- not solid, no backwall, nothing on
		/// object layer two -- and <see cref="BlueprintWorld"/> calls `BackwallManager.Clear()`, so a
		/// canvas has no backwall either. Left alone, EVERY non-solid unoccupied cell of a canvas
		/// carved to look like a sealed underground chamber reports open to space.
		///
		/// Nothing in the physics reads it: the custom SimDLL has no handler for `SetWorldZones`
		/// or `ModifyCellWorldZone`. It is the managed gameplay predicates that were wrong, and
		/// anything a rig might later assert about a cold side -- radiative rejection is the
		/// obvious one -- would have fired everywhere on every canvas and measured nothing.
		///
		/// DEFAULTS TO THE STARTING BIOME. A rig canvas stands in for
		/// a chamber a player could have dug in their own colony, and a player's colony is
		/// Sandstone, not vacuum-exposed space. `Space` is the GAME's default only because
		/// `GenerateTexture` seeds it and a canvas gives it no polygons to overwrite -- it is an
		/// artefact of how a canvas is built, not a choice anybody made.
		///
		/// THIS DEFAULT HAS FAILED ONCE BEFORE AND THE HISTORY MATTERS. It was set to `Sandstone`
		/// earlier the same day on the strength of ONE green TURBINEFED run, and two further runs
		/// of that same build came back 20 PASS / 2 FAIL, twice, with the turbine body reading
		/// 28.91 K and phase A's first sample arriving at t=59.5 s instead of t~15 s:
		///
		///     Sandstone   26/0   20/2   20/2          overlay warnings 2502, 156756, 59910
		///     Space       26/0                        overlay warnings 0
		///
		/// The overlay-warning counts are the part worth reading. `Space` produced NONE and
		/// `Sandstone` produced up to 156756 in a run -- each one a logged line, and logging is
		/// synchronous -- so the difference between the two arms was not a physical one, it was
		/// tens of thousands of log writes stealing frames. That is consistent with what actually
		/// changed in the failing runs: a sample arriving 45 s late, and a body that had not yet
		/// had time to heat. Nothing in the sim reads this field.
		///
		/// SO THE RULE FOR CHANGING IT AGAIN IS: two consecutive green TURBINEFED runs, not one,
		/// and check the overlay-warning count in the transcript rather than only the verdict.
		/// </summary>
		public SubWorld.ZoneType zone { get; set; } = SubWorld.ZoneType.Sandstone;

		/// <summary>Where the game opens the camera and reveals the map. Set it to the middle of
		/// what the rig builds.</summary>
		public int startX { get; set; }

		/// <summary>See <see cref="startX"/>.</summary>
		public int startY { get; set; }
	}

	/// <summary>A named rectangle in canvas coordinates, inclusive of its origin cell.</summary>
	[Serializable]
	public class RegionSpec
	{
		public string name { get; set; }
		public int x { get; set; }
		public int y { get; set; }
		public int w { get; set; }
		public int h { get; set; }

		/// <summary>Enumerates every cell coordinate in the rectangle, bottom row first.</summary>
		public IEnumerable<Vector2I> Cells()
		{
			for (int dy = 0; dy < h; dy++)
			{
				for (int dx = 0; dx < w; dx++)
				{
					yield return new Vector2I(x + dx, y + dy);
				}
			}
		}
	}

	/// <summary>
	/// A rectangular element fill. Either name a <see cref="region"/> or give an inline rectangle,
	/// not both.
	///
	/// A fill carrying a <see cref="mixture"/> is a two-stage thing, and the split is not
	/// cosmetic: the canvas stage runs before a sim exists, and a sim cell holds exactly one
	/// element, so the canvas gets the whole mass as the FIRST listed species and the remaining
	/// species are converted across at runtime by <see cref="GasMixtureFacade.ConvertFromVanilla"/>,
	/// which is mass-conserving by construction. That means a mixture fill is briefly single
	/// species, for the frames between the world appearing and the builder's mixture stage
	/// running.
	/// </summary>
	[Serializable]
	public class FillSpec
	{
		/// <summary>Name of a declared region to fill.</summary>
		public string region { get; set; }

		/// <summary>Inline rectangle, used when <see cref="region"/> is not given.</summary>
		public int x { get; set; }
		public int y { get; set; }
		public int w { get; set; }
		public int h { get; set; }

		/// <summary>Single-element fill. Ignored when <see cref="mixture"/> is given.</summary>
		public SimHashes element { get; set; }

		/// <summary>Mass per cell. Ignored when <see cref="mixture"/> is given, which carries its
		/// own per-species masses.</summary>
		public float mass { get; set; }

		/// <summary>Temperature of every cell in the fill.</summary>
		public float temperature { get; set; } = 293.15f;

		/// <summary>A multi-species gas fill. See this type's own remarks for the two-stage
		/// behaviour.</summary>
		public List<SpeciesSpec> mixture { get; set; }

		/// <summary>
		/// The BACKWALL laid behind every cell of this fill: the rock face you see through a
		/// carved-out room, rather than the contents of the room itself.
		///
		/// WHY A ROOM NEEDS ONE. `BlueprintWorld` calls `BackwallManager.Clear()`, so a canvas has
		/// no backwall anywhere. In a one-tile corridor nothing shows through and it never
		/// mattered; the first rig with a real 21x21 room made it obvious -- the room rendered
		/// with the void visible behind it, which reads exactly like the "why is my rig in space"
		/// complaint the subworld-zone fix was supposed to have closed. Two different causes, the
		/// same symptom on screen.
		///
		/// It is not only cosmetic. `Grid.IsCellOpenToSpace` is false only when the cell has a
		/// backwall AND is not in a space zone AND has nothing on the building layer; the zone
		/// half was fixed by giving the canvas an overworld cell, and this is the other half. A
		/// sealed chamber that still answers "open to space" is a rig measuring against a boundary
		/// condition nobody chose.
		///
		/// Defaults to <see cref="SimHashes.Vacuum"/>, which means "no backwall" and leaves every
		/// existing blueprint behaving exactly as before. <see cref="BackwallManager.HasBackwall"/>
		/// tests `Element.IsSolid`, so a gas here would be silently ignored -- name a solid,
		/// normally the same material the room was carved out of.
		/// </summary>
		public SimHashes backwall { get; set; } = SimHashes.Vacuum;

		/// <summary>Backwall mass per cell. Must be above zero for the backwall to exist.</summary>
		public float backwallMass { get; set; } = 200f;

		/// <summary>Backwall temperature; defaults to the fill's own.</summary>
		public float backwallTemperature { get; set; }

		/// <summary>Total mass per cell across every species, or <see cref="mass"/> for a
		/// single-element fill.</summary>
		public float TotalMass()
		{
			if (mixture == null || mixture.Count == 0)
			{
				return mass;
			}
			float total = 0f;
			for (int i = 0; i < mixture.Count; i++)
			{
				if (mixture[i] != null)
				{
					total += mixture[i].mass;
				}
			}
			return total;
		}
	}

	/// <summary>One component of a <see cref="FillSpec.mixture"/>.</summary>
	[Serializable]
	public class SpeciesSpec
	{
		public SimHashes element { get; set; }
		public float mass { get; set; }
	}

	/// <summary>
	/// One building. Reuses ONI's own storage type so prefilled tanks and reservoirs are described
	/// exactly the way vanilla templates describe them, and converts to a real
	/// <c>TemplateClasses.Prefab</c> so Klei's <c>TemplateLoader</c> does the placement.
	/// </summary>
	[Serializable]
	public class BuildingSpec
	{
		/// <summary>Prefab id, e.g. <c>GasReservoir</c>. This is the <c>BuildingDef.PrefabID</c>,
		/// not the display name.</summary>
		public string id { get; set; }

		/// <summary>Position in canvas coordinates. This is the same origin cell
		/// <c>BuildingDef.Build(cell, ...)</c> takes: <c>TemplateLoader.PlaceBuilding</c>'s width
		/// offset and <c>BuildingDef.GetBuildingCell</c>'s cancel exactly (see
		/// <c>DemoRig.MakeBuildingPrefab</c>). A wide
		/// building therefore extends to BOTH sides of this cell -- which is why
		/// <see cref="BlueprintBuilder.ValidateAgainstGame"/> resolves the real footprint through
		/// <c>BuildingDef.PlacementOffsets</c> rather than assuming this cell is a corner.</summary>
		public int x { get; set; }

		/// <summary>See <see cref="x"/>.</summary>
		public int y { get; set; }

		/// <summary>Rotation. Neutral unless the building is one that can turn.</summary>
		public Orientation orientation { get; set; } = Orientation.Neutral;

		/// <summary>Construction material. <c>Void</c> means "the def's own first default
		/// element", which is what a player building it would get.</summary>
		public SimHashes element { get; set; } = SimHashes.Void;

		/// <summary>Structure temperature at spawn.</summary>
		public float temperature { get; set; } = 298.15f;

		/// <summary>Contents to put in the building's <c>Storage</c> before it ever runs -- a
		/// prefilled tank, reservoir or hopper. ONI's own type, applied by
		/// <c>TemplateLoader.PlaceBuilding</c>.</summary>
		public List<StorageItem> storage { get; set; }

		/// <summary>Utility connection bitmask (<c>UtilityConnections</c>), applied by
		/// <c>TemplateLoader.PlaceUtilityConnection</c>.
		///
		/// LEAVE IT ZERO AND <see cref="BlueprintBuilder"/> COMPUTES IT from the blueprint's own
		/// adjacency before stamping. Set it only to override that.
		///
		/// This comment used to say zero meant "work it out from adjacency, which is what
		/// conduits normally do". That was WRONG, and wrong in the direction that hides itself:
		/// nothing works it out. <c>UtilityNetworkManager.RebuildNetworks</c> floods from a cell
		/// to a neighbour only when the cell's STORED bitmask already has that direction bit, and
		/// <c>SetConnections</c> masks the caller's value against live adjacency rather than
		/// replacing it -- so <c>None &amp; (real neighbours)</c> stays <c>None</c> forever. Every
		/// wire and pipe a blueprint placed was its own one-node network. It went unnoticed for
		/// weeks because a generator only requires <c>CircuitID != ushort.MaxValue</c> to count as
		/// connected, and a one-tile circuit satisfies that: the rigs passed, the power flowed
		/// nowhere, and the tiles were visibly disconnected stubs the whole time.</summary>
		public int connections { get; set; }

		/// <summary>Named settings applied after placement. The three ids Klei's own
		/// <c>other_values</c> switch understands are passed through to it; everything else is
		/// applied by <see cref="BlueprintBuilder"/> against the building's components, and an
		/// unrecognised id is reported rather than ignored.</summary>
		public Dictionary<string, float> settings { get; set; }

		/// <summary>
		/// The vanilla template prefab for this building, so placement can go through Klei's
		/// loader. Settings that Klei's own switch understands are emitted as
		/// <c>other_values</c>; the rest are left for <see cref="BlueprintBuilder"/>.
		/// </summary>
		public Prefab ToPrefab()
		{
			SimHashes material = element;
			if (material == SimHashes.Void)
			{
				BuildingDef def = Assets.GetBuildingDef(id);
				if (def != null && def.DefaultElements().Count > 0)
				{
					Element resolved = ElementLoader.GetElement(def.DefaultElements()[0]);
					material = resolved != null ? resolved.id : SimHashes.Copper;
				}
				else
				{
					material = SimHashes.Copper;
				}
			}

			List<Prefab.template_amount_value> passThrough = null;
			if (settings != null)
			{
				foreach (KeyValuePair<string, float> setting in settings)
				{
					if (!IsKleiOtherValue(setting.Key))
					{
						continue;
					}
					if (passThrough == null)
					{
						passThrough = new List<Prefab.template_amount_value>();
					}
					passThrough.Add(new Prefab.template_amount_value(setting.Key, setting.Value));
				}
			}

			var prefab = new Prefab(id, Prefab.Type.Building, x, y, material, temperature, 1f, null, 0,
				orientation, null, passThrough != null ? passThrough.ToArray() : null, connections);

			if (storage != null)
			{
				for (int i = 0; i < storage.Count; i++)
				{
					if (storage[i] != null)
					{
						prefab.AssignStorage(storage[i]);
					}
				}
			}
			return prefab;
		}

		/// <summary>
		/// The exact set <c>TemplateLoader.PlaceBuilding</c>'s <c>other_values</c> switch handles.
		/// Anything outside it is silently dropped by that switch, so this project applies those
		/// itself rather than letting a setting quietly not happen.
		/// </summary>
		public static bool IsKleiOtherValue(string id)
		{
			return id == "joulesAvailable" || id == "sealedDoorDirection" || id == "switchSetting";
		}
	}

	/// <summary>
	/// Contents to put into one conduit tile. Written with <c>ConduitFlow.SetContents</c> rather
	/// than <c>AddElement</c>, for the reason recorded in <see cref="PipeMatterFacade"/>:
	/// <c>AddElement</c> clamps to the per-tile cap and blends temperature into whatever is
	/// already there, so a blueprint asking for a specific mass at a specific temperature would
	/// silently get neither.
	/// </summary>
	[Serializable]
	public class ConduitSpec
	{
		public int x { get; set; }
		public int y { get; set; }

		/// <summary><c>Gas</c> or <c>Liquid</c>.</summary>
		public ConduitType type { get; set; } = ConduitType.Gas;

		public SimHashes element { get; set; }
		public float mass { get; set; }
		public float temperature { get; set; } = 293.15f;

		/// <summary>Disease to seed alongside the contents; null for none.</summary>
		public string diseaseName { get; set; }
		public int diseaseCount { get; set; }
	}

	/// <summary>
	/// The demo checklist's rule 5, in three bands outward from the protected rectangle: a solid
	/// sealing ring, then <see cref="vacuumRing"/> rings of vacuum, which conduct nothing, then a
	/// second sealing ring, which stops the surrounding atmosphere flowing in to replace the
	/// vacuum. The inner ring is what stops the protected volume's own gas expanding OUT into the
	/// vacuum, which is a real failure this project measured rather than a hypothetical one --
	/// see <c>DemoRig.PaintVacuumJacket</c>.
	///
	/// Either name a <see cref="region"/> or give an inline rectangle. The rings are painted
	/// OUTSIDE that rectangle, so the protected volume itself is untouched, and the jacket's total
	/// thickness is <see cref="vacuumRing"/> + 2 cells on every side.
	/// </summary>
	[Serializable]
	public class JacketSpec
	{
		public string region { get; set; }

		public int x { get; set; }
		public int y { get; set; }
		public int w { get; set; }
		public int h { get; set; }

		/// <summary>How many cells of vacuum sit between the volume and the shell.</summary>
		public int vacuumRing { get; set; } = 1;

		/// <summary>The sealing ring's element.</summary>
		public SimHashes shell { get; set; } = SimHashes.Unobtanium;

		/// <summary>Mass per shell cell; zero means the element's own default.</summary>
		public float shellMass { get; set; }
	}
}
