using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace OniFramework
{
	/// <summary>How much a fault matters.</summary>
	public enum RigFaultSeverity
	{
		/// <summary>Worth knowing when reading a run back; not necessarily wrong.</summary>
		Info,

		/// <summary>Probably wrong, but a rig can legitimately do this.</summary>
		Warning,

		/// <summary>The scenario is not the scenario it claims to be.</summary>
		Error,
	}

	/// <summary>
	/// One detected problem, addressed by CELL so it can be found in a captured frame.
	/// </summary>
	public struct RigFault
	{
		/// <summary>Stable short identifier, e.g. <c>building-entombed</c>. Machine-greppable
		/// across runs; the message is for humans and may change wording.</summary>
		public string Code;

		public RigFaultSeverity Severity;

		/// <summary>Bounding box of the affected cells, inclusive. A single-cell fault has
		/// min == max. This is what a frame crop is taken from.</summary>
		public int MinX, MinY, MaxX, MaxY;

		/// <summary>How many cells are actually affected inside the box; a box can be sparse.</summary>
		public int CellCount;

		public string Message;

		public string ToJson(int frameIndex, string phase)
		{
			CultureInfo c = CultureInfo.InvariantCulture;
			return "{"
				+ "\"frame\":" + frameIndex.ToString(c)
				+ ",\"phase\":\"" + Escape(phase) + "\""
				+ ",\"code\":\"" + Escape(Code) + "\""
				+ ",\"severity\":\"" + Severity.ToString().ToLowerInvariant() + "\""
				+ ",\"minx\":" + MinX.ToString(c)
				+ ",\"miny\":" + MinY.ToString(c)
				+ ",\"maxx\":" + MaxX.ToString(c)
				+ ",\"maxy\":" + MaxY.ToString(c)
				+ ",\"cells\":" + CellCount.ToString(c)
				+ ",\"message\":\"" + Escape(Message) + "\""
				+ "}";
		}

		internal static string Escape(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return string.Empty;
			}
			return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
		}
	}

	/// <summary>
	/// Automatic fault detection for rigs, reported in cell coordinates so every finding can be
	/// located in a frame that was already captured.
	///
	/// WHAT THIS REPLACES. Without it, a scenario defect is found by a human looking at a
	/// screenshot and saying where the problem was -- fog aimed at the wrong place, a
	/// reservoir built into rock, a row of raw terrain inside a "sealed" volume. Each cost a round
	/// trip, and each was a class of fault a machine can check directly. These detectors check
	/// exactly those classes, at every checkpoint, and write what they find next to the frames.
	///
	/// The point is not that the detectors are clever. It is that a finding carries a CELL BOX, and
	/// <see cref="FrameGeometry"/> has recorded, for each captured frame, where cells landed in
	/// pixels. So a fault found on frame 412 can be cropped out of frame 412 afterwards, and a
	/// fault reported by a human days later can be located in frames already on disk without
	/// re-running anything.
	///
	/// SCOPE, honestly stated. Three detectors need nothing but the live grid and work for any rig.
	/// Two more compare against a <see cref="RigBlueprint"/> and only run when a rig was built from
	/// one, because without a declaration of intent there is no way to tell a deliberate wall from
	/// an inherited one.
	/// </summary>
	public static class RigDiagnostics
	{
		/// <summary>Where to write sidecars. Wired by the mod that owns the recorder, the same way
		/// <see cref="DemoNarrator"/>'s hooks are, so the framework never reaches into a mod.
		/// Null disables file output and leaves logging intact.</summary>
		public static Func<string> RecordingDirectoryHook;

		/// <summary>The frame index a fault should be attributed to. Wired by the recorder.
		/// Returns 0 when nothing is recording, which is a valid state, not an error.</summary>
		public static Func<int> FrameIndexHook;

		/// <summary>
		/// Asks the recorder to PRESERVE the frame a new fault was found on.
		///
		/// This is what makes "look at it later without re-running" actually true. The encoder
		/// deletes every `frame_*.jpg` once it has an .mp4 -- a recording is hundreds of megabytes
		/// of JPEGs and the video is the artifact worth keeping -- so by the time anyone reads
		/// diagnostics.jsonl, the frame it points at is normally gone. A preserved frame is written
		/// under a different name, which the encoder's own delete pattern does not match, so the
		/// handful of frames that actually show something survive and the bulk still does not.
		/// </summary>
		public static System.Action KeepFrameHook;

		/// <summary>Mass above which a cell that is supposed to be vacuum counts as contaminated.
		/// Vanilla's own massless-cell handling zeroes anything under a gram, so this is an order
		/// of magnitude above the noise floor and well below anything a leak would leave.</summary>
		public const float VacuumMassToleranceKg = 0.01f;

		private static bool haveTestBounds;
		private static int testMinX, testMinY, testMaxX, testMaxY;

		private static bool haveJacketBounds;
		private static int jacketMinX, jacketMinY, jacketMaxX, jacketMaxY, jacketVacuumRing;

		private static bool layoutWritten;

		/// <summary>
		/// How often a scan may actually run. <c>RigHarness.Checkpoint</c> is called from rig hold
		/// loops, not a handful of times per run: the first live run produced thirteen scans in
		/// four seconds. At that rate even a CORRECT finding is unusable -- it fills the log,
		/// and every Debug.LogError pops ONI's dev console over the recording being captured.
		/// </summary>
		public const float MinimumScanIntervalSeconds = 2f;

		/// <summary>A scan longer than this is reported, because at 60 Hz a frame is 16.7 ms and
		/// anything approaching that is a hitch a player can see.</summary>
		public const double SlowScanMilliseconds = 8.0;

		private static float lastScanRealtime = float.NegativeInfinity;
		private static List<RigFault> lastScanResult = new List<RigFault>();

		/// <summary>
		/// Signatures already reported, so a fault that simply persists is stated ONCE. A steady
		/// condition repeated every scan is noise; a CHANGING one (23 contaminated cells becoming
		/// 24) has a different signature and is reported again, which is the part worth seeing.
		/// </summary>
		private static readonly HashSet<string> reportedSignatures = new HashSet<string>();

		/// <summary>
		/// UTF-8 with no byte order mark. <c>Encoding.UTF8</c> emits one on the first write, and a
		/// BOM sitting in front of the first JSON object makes that line unparseable to a strict
		/// reader -- so the very first frame, and the very first fault, would silently disappear
		/// from any tool reading the sidecars. Found live on the first recorded run.
		/// </summary>
		private static readonly UTF8Encoding NoBomUtf8 = new UTF8Encoding(false);

		/// <summary>
		/// The rectangle the rig considers its own, inclusive. Set by <c>RigHarness</c> when the
		/// rig reveals its test area; without it the whole-map detectors have no idea what to
		/// look at and stay quiet rather than reporting the entire asteroid.
		/// </summary>
		public static void SetTestBounds(int minX, int minY, int maxX, int maxY)
		{
			testMinX = Mathf.Min(minX, maxX);
			testMinY = Mathf.Min(minY, maxY);
			testMaxX = Mathf.Max(minX, maxX);
			testMaxY = Mathf.Max(minY, maxY);
			haveTestBounds = true;
		}

		/// <summary>
		/// PASS THE RECTANGLE THE VACUUM IS MEASURED FROM, which on the runtime path is the test
		/// volume PLUS its inner sealing ring rather than the volume itself -- RigHarness expands
		/// it by one before calling here, so that vacuum stays at rings 1..vacuumRing and the
		/// shell at vacuumRing+1 whichever path built the jacket.
		///
		/// The volume the rig jacketed, inclusive, and how many cells of vacuum sit between it and
		/// the sealing ring. Set by <c>RigHarness.JacketTestVolume</c>.
		/// </summary>
		public static void SetJacketBounds(int minX, int minY, int maxX, int maxY, int vacuumRing)
		{
			jacketMinX = Mathf.Min(minX, maxX);
			jacketMinY = Mathf.Min(minY, maxY);
			jacketMaxX = Mathf.Max(minX, maxX);
			jacketMaxY = Mathf.Max(minY, maxY);
			jacketVacuumRing = Mathf.Max(1, vacuumRing);
			haveJacketBounds = true;
		}

		/// <summary>Forgets everything, for a process that runs more than one rig.</summary>
		public static void Reset()
		{
			haveTestBounds = false;
			haveJacketBounds = false;
			layoutWritten = false;
			lastScanRealtime = float.NegativeInfinity;
			lastScanResult = new List<RigFault>();
			reportedSignatures.Clear();
		}

		/// <summary>
		/// Runs every applicable detector and returns what it found, most severe first.
		/// </summary>
		public static List<RigFault> Scan()
		{
			var faults = new List<RigFault>();
			if (Grid.CellCount <= 0)
			{
				return faults;
			}

			DetectUnrevealedTestArea(faults);
			DetectEntombedBuildings(faults);
			DetectJacketBreach(faults);

			RigBlueprint blueprint = BlueprintWorld.Active;
			if (blueprint != null)
			{
				DetectUnexpectedSolids(blueprint, faults);
				DetectStarvedConduits(blueprint, faults);
			}

			faults.Sort((a, b) => b.Severity.CompareTo(a.Severity));
			return faults;
		}

		/// <summary>
		/// Scans, logs everything found, and appends it to <c>diagnostics.jsonl</c> beside the
		/// frames, tagged with the frame index that was current when it was found.
		///
		/// Returns the faults so a rig can fail itself on them; deliberately does NOT fail anything
		/// on its own, because whether an Error here should end a run is the rig's judgement, not
		/// the detector's.
		/// </summary>
		public static List<RigFault> ScanAndRecord(string phase)
		{
			// Rate limited, because Checkpoint is called from hold loops. Returns the PREVIOUS
			// result rather than an empty list, so a caller that fails on Errors still sees a fault
			// that is genuinely still present between scans.
			float now = Time.realtimeSinceStartup;
			if (now - lastScanRealtime < MinimumScanIntervalSeconds)
			{
				return lastScanResult;
			}
			lastScanRealtime = now;

			// TIMED, because "the game went choppy while the rig was running" is a question this
			// tool should be able to answer with a number rather than a theory. One line only
			// when a scan is slow enough to be visible as a hitch at 60 Hz.
			var watch = System.Diagnostics.Stopwatch.StartNew();
			List<RigFault> faults = Scan();
			watch.Stop();
			if (watch.Elapsed.TotalMilliseconds > SlowScanMilliseconds)
			{
				Debug.LogWarning(string.Format(
					"[OniFramework] DIAG scan took {0:F1} ms (phase {1}) -- over the {2:F0} ms "
					+ "budget, so this scan is visible as a frame hitch",
					watch.Elapsed.TotalMilliseconds, phase, SlowScanMilliseconds));
			}
			lastScanResult = faults;
			int frameIndex = FrameIndexHook != null ? FrameIndexHook() : 0;

			var fresh = new List<RigFault>();
			for (int i = 0; i < faults.Count; i++)
			{
				RigFault fault = faults[i];
				string signature = $"{fault.Code}|{fault.MinX}|{fault.MinY}|{fault.MaxX}|"
					+ $"{fault.MaxY}|{fault.CellCount}";
				if (!reportedSignatures.Add(signature))
				{
					continue;
				}
				fresh.Add(fault);

				// Only for a fault that is NEW at this scan, so a condition that persists for a
				// whole run preserves one frame rather than one per checkpoint.
				if (KeepFrameHook != null)
				{
					KeepFrameHook();
				}

				string where = fault.MinX == fault.MaxX && fault.MinY == fault.MaxY
					? $"({fault.MinX},{fault.MinY})"
					: $"({fault.MinX},{fault.MinY})..({fault.MaxX},{fault.MaxY}), {fault.CellCount} cell(s)";
				string line = $"[OniFramework] DIAG {fault.Severity.ToString().ToUpperInvariant()} "
					+ $"{fault.Code} at {where} [frame {frameIndex}, phase {phase}]: {fault.Message}";

				// Warning, not LogError, even for an Error-severity finding. Debug.LogError pops
				// ONI's dev console, which lands on top of the recording the rig is capturing --
				// so a diagnostic about the scenario would deface the very frames it is pointing
				// at. Severity still travels in the text and in diagnostics.jsonl, where the tools
				// read it.
				if (fault.Severity == RigFaultSeverity.Info)
				{
					Debug.Log(line);
				}
				else
				{
					Debug.LogWarning(line);
				}
			}

			AppendFaults(fresh, frameIndex, phase);
			WriteLayoutOnce();
			return faults;
		}

		// ------------------------------------------------------------------ detectors

		/// <summary>
		/// Rule 6, as a check: a rig that reveals the wrong rectangle films a black screen where
		/// the experiment should be. Reported as one box over the unrevealed cells rather than one
		/// fault per cell, because a mis-aimed reveal misses thousands at once and a log line each
		/// would bury everything else.
		///
		/// READS <c>Grid.Visible</c>, NOT <c>Grid.Revealed</c>. They are different arrays and only
		/// one of them answers "is this black on camera". <c>Grid.Reveal</c> -- what
		/// <c>DemoRig.RevealOnly</c> calls -- writes <c>Visible</c> and <c>Spawnable</c> and never
		/// touches <c>VisMasks</c>, which is what the <c>Grid.Revealed</c> indexer reads. Written
		/// against <c>Revealed</c> first, this detector reported every cell of every rig's test
		/// area as fogged on the first live run (598 of 598, on a rig that was plainly visible in
		/// its own recording), which is how the mistake was caught.
		/// </summary>
		private static void DetectUnrevealedTestArea(List<RigFault> faults)
		{
			if (!haveTestBounds)
			{
				return;
			}

			int count = 0;
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

			for (int y = testMinY; y <= testMaxY; y++)
			{
				for (int x = testMinX; x <= testMaxX; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || Grid.Visible[cell] > 0)
					{
						continue;
					}
					count++;
					minX = Mathf.Min(minX, x);
					minY = Mathf.Min(minY, y);
					maxX = Mathf.Max(maxX, x);
					maxY = Mathf.Max(maxY, y);
				}
			}

			if (count == 0)
			{
				return;
			}

			int total = (testMaxX - testMinX + 1) * (testMaxY - testMinY + 1);
			faults.Add(new RigFault
			{
				Code = "test-area-unrevealed",
				Severity = RigFaultSeverity.Error,
				MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
				CellCount = count,
				Message = $"{count} of {total} cells in the declared test area are still under fog. "
					+ "The reveal was aimed somewhere other than where the rig actually built "
					+ "(rule 6); on camera this region is solid black.",
			});
		}

		/// <summary>
		/// The entombed-reservoir defect, as a check. A building placed into natural solid terrain
		/// is placed anyway and ends up behind the tiles instead of in a room.
		///
		/// Constructed tiles are excluded by reading the cell's own ConstructedTile property bit
		/// rather than by guessing from the building's def: a rig legitimately builds solid tiles,
		/// and a tile's own cell being solid is not a fault. Natural solid under a building always
		/// is.
		/// </summary>
		private static void DetectEntombedBuildings(List<RigFault> faults)
		{
			// SCOPED TO THE JACKET WHEN THERE IS ONE, and to the test area only when there is not.
			//
			// The test area is deliberately WIDER than the rig -- JacketTestVolume reveals a few
			// tiles of surrounding rock so an isolated experiment does not read as a rendering
			// fault -- and on a real save that margin contains the map's own furniture: widening
			// the reveal by two tiles can bring a base's entombed Gas Vent into range, and this
			// detector would duly report it though it has nothing to do with the rig.
			int scanMinX, scanMinY, scanMaxX, scanMaxY;
			if (haveJacketBounds)
			{
				scanMinX = jacketMinX; scanMinY = jacketMinY;
				scanMaxX = jacketMaxX; scanMaxY = jacketMaxY;
			}
			else if (haveTestBounds)
			{
				scanMinX = testMinX; scanMinY = testMinY;
				scanMaxX = testMaxX; scanMaxY = testMaxY;
			}
			else
			{
				return;
			}

			const byte ConstructedTile = 0x80;
			var reported = new HashSet<GameObject>();

			for (int y = scanMinY; y <= scanMaxY; y++)
			{
				for (int x = scanMinX; x <= scanMaxX; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell))
					{
						continue;
					}

					GameObject building = Grid.Objects[cell, (int)ObjectLayer.Building];
					if (building == null || !reported.Add(building))
					{
						continue;
					}

					var complete = building.GetComponent<BuildingComplete>();
					if (complete == null || complete.Def == null || complete.Def.IsFoundation)
					{
						continue;
					}

					int buried = 0;
					int bMinX = int.MaxValue, bMinY = int.MaxValue, bMaxX = int.MinValue, bMaxY = int.MinValue;

					foreach (int footprintCell in EnumerateFootprint(complete))
					{
						if (!Grid.Solid[footprintCell]
							|| (Grid.Properties[footprintCell] & ConstructedTile) != 0)
						{
							continue;
						}
						buried++;
						Grid.CellToXY(footprintCell, out int fx, out int fy);
						bMinX = Mathf.Min(bMinX, fx);
						bMinY = Mathf.Min(bMinY, fy);
						bMaxX = Mathf.Max(bMaxX, fx);
						bMaxY = Mathf.Max(bMaxY, fy);
					}

					if (buried == 0)
					{
						continue;
					}

					faults.Add(new RigFault
					{
						Code = "building-entombed",
						Severity = RigFaultSeverity.Error,
						MinX = bMinX, MinY = bMinY, MaxX = bMaxX, MaxY = bMaxY,
						CellCount = buried,
						Message = $"'{complete.Def.PrefabID}' at ({x},{y}) has {buried} footprint "
							+ $"cell(s) in natural solid terrain. Its footprint is "
							+ $"{complete.Def.WidthInCells}x{complete.Def.HeightInCells}, and a wide "
							+ "building extends to BOTH sides of its own cell, so a carve-out sized "
							+ "from the cell alone leaves the edges buried.",
					});
				}
			}
		}

		/// <summary>
		/// Rule 5, as a check, in both directions: the sealing ring must actually be solid all the
		/// way round, and the vacuum between it and the volume must actually be empty. A jacket
		/// that leaks reads as a thermodynamics result rather than as a geometry mistake, which is
		/// exactly how it gets believed.
		/// </summary>
		private static void DetectJacketBreach(List<RigFault> faults)
		{
			if (!haveJacketBounds)
			{
				return;
			}

			int gaps = 0;
			int gMinX = int.MaxValue, gMinY = int.MaxValue, gMaxX = int.MinValue, gMaxY = int.MinValue;

			foreach (Vector2I point in RingCells(jacketMinX, jacketMinY, jacketMaxX, jacketMaxY,
				jacketVacuumRing + 1))
			{
				int cell = Grid.XYToCell(point.x, point.y);
				if (!Grid.IsValidCell(cell) || Grid.Solid[cell])
				{
					continue;
				}
				gaps++;
				gMinX = Mathf.Min(gMinX, point.x);
				gMinY = Mathf.Min(gMinY, point.y);
				gMaxX = Mathf.Max(gMaxX, point.x);
				gMaxY = Mathf.Max(gMaxY, point.y);
			}

			if (gaps > 0)
			{
				faults.Add(new RigFault
				{
					Code = "jacket-shell-gap",
					Severity = RigFaultSeverity.Error,
					MinX = gMinX, MinY = gMinY, MaxX = gMaxX, MaxY = gMaxY,
					CellCount = gaps,
					Message = $"{gaps} cell(s) of the sealing ring are not solid, so the surrounding "
						+ "atmosphere can flow into the vacuum gap and conduct straight through the "
						+ "jacket (rule 5).",
				});
			}

			int contaminated = 0;
			float worstMass = 0f;
			int cMinX = int.MaxValue, cMinY = int.MaxValue, cMaxX = int.MinValue, cMaxY = int.MinValue;

			for (int ring = 1; ring <= jacketVacuumRing; ring++)
			{
				foreach (Vector2I point in RingCells(jacketMinX, jacketMinY, jacketMaxX, jacketMaxY, ring))
				{
					int cell = Grid.XYToCell(point.x, point.y);
					if (!Grid.IsValidCell(cell) || Grid.Mass[cell] <= VacuumMassToleranceKg)
					{
						continue;
					}
					contaminated++;
					worstMass = Mathf.Max(worstMass, Grid.Mass[cell]);
					cMinX = Mathf.Min(cMinX, point.x);
					cMinY = Mathf.Min(cMinY, point.y);
					cMaxX = Mathf.Max(cMaxX, point.x);
					cMaxY = Mathf.Max(cMaxY, point.y);
				}
			}

			if (contaminated > 0)
			{
				faults.Add(new RigFault
				{
					Code = "jacket-vacuum-contaminated",
					Severity = RigFaultSeverity.Warning,
					MinX = cMinX, MinY = cMinY, MaxX = cMaxX, MaxY = cMaxY,
					CellCount = contaminated,
					Message = $"{contaminated} cell(s) of the vacuum gap hold mass (worst "
						+ $"{worstMass:F3} kg). Vacuum conducts nothing; anything in the gap does, so "
						+ "the volume is thermally connected to the map after all.",
				});
			}
		}

		/// <summary>
		/// Terrain the rig did not ask for. Only runs when the rig was built from a blueprint,
		/// because that is the only thing that states intent: on a blank canvas every cell was
		/// declared, so a solid cell the blueprint did not put there is inherited or stray, and
		/// that is precisely the "row of raw terrain inside a sealed scenario" defect.
		///
		/// Solids only. Gases move on their own the instant the sim runs, so comparing them against
		/// a blueprint would report the simulation working as a fault.
		/// </summary>
		private static void DetectUnexpectedSolids(RigBlueprint blueprint, List<RigFault> faults)
		{
			if (!haveTestBounds)
			{
				return;
			}

			const byte ConstructedTile = 0x80;
			var declared = DeclaredSolidCells(blueprint);

			int unexpected = 0;
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			SimHashes worstElement = SimHashes.Vacuum;

			for (int y = testMinY; y <= testMaxY; y++)
			{
				for (int x = testMinX; x <= testMaxX; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || !Grid.Solid[cell])
					{
						continue;
					}
					// A tile the rig built is not stray terrain.
					if ((Grid.Properties[cell] & ConstructedTile) != 0)
					{
						continue;
					}
					if (declared.Contains(((long)x << 32) | (uint)y))
					{
						continue;
					}

					unexpected++;
					worstElement = Grid.Element[cell].id;
					minX = Mathf.Min(minX, x);
					minY = Mathf.Min(minY, y);
					maxX = Mathf.Max(maxX, x);
					maxY = Mathf.Max(maxY, y);
				}
			}

			if (unexpected == 0)
			{
				return;
			}

			faults.Add(new RigFault
			{
				Code = "unexpected-solid",
				Severity = RigFaultSeverity.Error,
				MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
				CellCount = unexpected,
				Message = $"{unexpected} solid cell(s) inside the test area were never declared by "
					+ $"blueprint '{blueprint.name}' (e.g. {worstElement}). On a blank canvas every "
					+ "cell is declared, so these are stray or inherited, and anything measured "
					+ "through them is measuring the wrong scenario.",
			});
		}

		/// <summary>
		/// Pipes the blueprint said to fill that are empty. A prefilled pipe that silently did not
		/// fill turns a charged run into an empty one, and every number afterwards is consistent
		/// with a working rig that simply had nothing in it.
		/// </summary>
		private static void DetectStarvedConduits(RigBlueprint blueprint, List<RigFault> faults)
		{
			if (blueprint.conduits == null || blueprint.conduits.Count == 0 || Game.Instance == null)
			{
				return;
			}

			int starved = 0;
			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

			for (int i = 0; i < blueprint.conduits.Count; i++)
			{
				ConduitSpec spec = blueprint.conduits[i];
				if (spec == null || spec.mass <= 0f)
				{
					continue;
				}

				int cell = Grid.XYToCell(spec.x, spec.y);
				if (!Grid.IsValidCell(cell))
				{
					continue;
				}

				ConduitFlow flow = spec.type == ConduitType.Liquid
					? Game.Instance.liquidConduitFlow
					: Game.Instance.gasConduitFlow;
				ConduitFlow.ConduitContents contents = flow.GetContents(cell);
				if (contents.mass > spec.mass * 0.01f)
				{
					continue;
				}

				starved++;
				minX = Mathf.Min(minX, spec.x);
				minY = Mathf.Min(minY, spec.y);
				maxX = Mathf.Max(maxX, spec.x);
				maxY = Mathf.Max(maxY, spec.y);
			}

			if (starved == 0)
			{
				return;
			}

			faults.Add(new RigFault
			{
				Code = "conduit-starved",
				Severity = RigFaultSeverity.Warning,
				MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
				CellCount = starved,
				Message = $"{starved} conduit tile(s) the blueprint charged are effectively empty. "
					+ "Either the fill never happened or the contents drained; an empty run still "
					+ "produces numbers, and they look like a working rig with nothing in it.",
			});
		}

		// ------------------------------------------------------------------ helpers

		/// <summary>
		/// Every cell a blueprint deliberately made solid: any fill or explicit cell whose element
		/// is a solid, plus the jacket's sealing ring and the world border.
		/// Packed as (x &lt;&lt; 32) | y so the set is one allocation rather than a Vector2I box each.
		/// </summary>
		private static HashSet<long> DeclaredSolidCells(RigBlueprint blueprint)
		{
			var declared = new HashSet<long>();
			if (blueprint.canvas == null)
			{
				return declared;
			}

			Action<int, int> add = (x, y) => declared.Add(((long)x << 32) | (uint)y);

			int thickness = blueprint.canvas.borderThickness;
			int width = blueprint.canvas.width;
			int height = blueprint.canvas.height;
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					if (x < thickness || y < thickness || x >= width - thickness || y >= height - thickness)
					{
						add(x, y);
					}
				}
			}

			if (blueprint.canvas.background != SimHashes.Vacuum && IsSolidElement(blueprint.canvas.background))
			{
				for (int y = thickness; y < height - thickness; y++)
				{
					for (int x = thickness; x < width - thickness; x++)
					{
						add(x, y);
					}
				}
			}

			if (blueprint.fills != null)
			{
				for (int i = 0; i < blueprint.fills.Count; i++)
				{
					FillSpec fill = blueprint.fills[i];
					if (fill == null || !IsSolidElement(fill.element))
					{
						continue;
					}
					RegionSpec rect = !string.IsNullOrEmpty(fill.region)
						? blueprint.FindRegion(fill.region)
						: new RegionSpec { x = fill.x, y = fill.y, w = fill.w, h = fill.h };
					if (rect == null)
					{
						continue;
					}
					foreach (Vector2I point in rect.Cells())
					{
						add(point.x, point.y);
					}
				}
			}

			if (blueprint.jacket != null)
			{
				RegionSpec rect = !string.IsNullOrEmpty(blueprint.jacket.region)
					? blueprint.FindRegion(blueprint.jacket.region)
					: new RegionSpec
					{
						x = blueprint.jacket.x, y = blueprint.jacket.y,
						w = blueprint.jacket.w, h = blueprint.jacket.h,
					};
				if (rect != null)
				{
					// BOTH sealing rings: one immediately outside the volume and one outside the
					// vacuum. BlueprintWorld.ApplyJacket paints three bands, not two.
					int maxX = rect.x + rect.w - 1;
					int maxY = rect.y + rect.h - 1;
					foreach (Vector2I point in RingCells(rect.x, rect.y, maxX, maxY, 1))
					{
						add(point.x, point.y);
					}
					foreach (Vector2I point in RingCells(rect.x, rect.y, maxX, maxY,
						blueprint.jacket.vacuumRing + 2))
					{
						add(point.x, point.y);
					}
				}
			}

			if (blueprint.cells != null)
			{
				for (int i = 0; i < blueprint.cells.Count; i++)
				{
					TemplateClasses.Cell cell = blueprint.cells[i];
					if (cell != null && IsSolidElement(cell.element))
					{
						add(cell.location_x, cell.location_y);
					}
				}
			}

			return declared;
		}

		private static bool IsSolidElement(SimHashes id)
		{
			Element element = ElementLoader.FindElementByHash(id);
			return element != null && element.IsSolid;
		}

		/// <summary>Every cell of the single-cell-wide outline <paramref name="distance"/> cells
		/// outside the given inclusive rectangle.</summary>
		private static IEnumerable<Vector2I> RingCells(int minX, int minY, int maxX, int maxY, int distance)
		{
			int left = minX - distance;
			int right = maxX + distance;
			int bottom = minY - distance;
			int top = maxY + distance;

			for (int x = left; x <= right; x++)
			{
				yield return new Vector2I(x, bottom);
				yield return new Vector2I(x, top);
			}
			for (int y = bottom + 1; y <= top - 1; y++)
			{
				yield return new Vector2I(left, y);
				yield return new Vector2I(right, y);
			}
		}

		/// <summary>
		/// A placed building's real cells. <c>BuildingComplete.PlacementCells</c> is the game's own
		/// answer and already accounts for width and rotation, so nothing here re-derives it.
		/// </summary>
		private static IEnumerable<int> EnumerateFootprint(BuildingComplete building)
		{
			int[] cells = building.PlacementCells;
			if (cells == null)
			{
				yield break;
			}
			for (int i = 0; i < cells.Length; i++)
			{
				if (Grid.IsValidCell(cells[i]))
				{
					yield return cells[i];
				}
			}
		}

		// ------------------------------------------------------------------ sidecars

		private static void AppendFaults(List<RigFault> faults, int frameIndex, string phase)
		{
			string directory = RecordingDirectoryHook != null ? RecordingDirectoryHook() : null;
			if (string.IsNullOrEmpty(directory) || faults.Count == 0)
			{
				return;
			}

			try
			{
				var text = new StringBuilder();
				for (int i = 0; i < faults.Count; i++)
				{
					text.Append(faults[i].ToJson(frameIndex, phase)).Append('\n');
				}
				File.AppendAllText(Path.Combine(directory, "diagnostics.jsonl"), text.ToString(),
					NoBomUtf8);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] RigDiagnostics: could not append diagnostics.jsonl: "
					+ e.Message);
			}
		}

		/// <summary>
		/// Writes <c>layout.json</c> once per run: the test area, the jacket, and every building
		/// with its real footprint. This is what lets a fault be described by NAME afterwards --
		/// "the reservoir" resolves to a cell box, and the cell box resolves to pixels through the
		/// per-frame geometry.
		/// </summary>
		public static void WriteLayoutOnce()
		{
			string directory = RecordingDirectoryHook != null ? RecordingDirectoryHook() : null;
			if (layoutWritten || string.IsNullOrEmpty(directory) || Grid.CellCount <= 0)
			{
				return;
			}

			try
			{
				CultureInfo c = CultureInfo.InvariantCulture;
				var text = new StringBuilder();
				text.Append("{\n");
				text.Append("  \"gridw\": ").Append(Grid.WidthInCells.ToString(c)).Append(",\n");
				text.Append("  \"gridh\": ").Append(Grid.HeightInCells.ToString(c)).Append(",\n");

				RigBlueprint blueprint = BlueprintWorld.Active;
				text.Append("  \"blueprint\": ")
					.Append(blueprint != null ? "\"" + RigFault.Escape(blueprint.name) + "\"" : "null")
					.Append(",\n");

				if (haveTestBounds)
				{
					text.Append("  \"testArea\": ").Append(Box(testMinX, testMinY, testMaxX, testMaxY))
						.Append(",\n");
				}
				if (haveJacketBounds)
				{
					text.Append("  \"jacket\": ")
						.Append(Box(jacketMinX, jacketMinY, jacketMaxX, jacketMaxY)).Append(",\n");
					text.Append("  \"jacketVacuumRing\": ").Append(jacketVacuumRing.ToString(c))
						.Append(",\n");
				}

				if (blueprint != null && blueprint.regions != null)
				{
					text.Append("  \"regions\": [\n");
					for (int i = 0; i < blueprint.regions.Count; i++)
					{
						RegionSpec region = blueprint.regions[i];
						if (region == null)
						{
							continue;
						}
						text.Append("    {\"name\": \"").Append(RigFault.Escape(region.name))
							.Append("\", \"minx\": ").Append(region.x.ToString(c))
							.Append(", \"miny\": ").Append(region.y.ToString(c))
							.Append(", \"maxx\": ").Append((region.x + region.w - 1).ToString(c))
							.Append(", \"maxy\": ").Append((region.y + region.h - 1).ToString(c))
							.Append("}");
						text.Append(i < blueprint.regions.Count - 1 ? ",\n" : "\n");
					}
					text.Append("  ],\n");
				}

				text.Append("  \"buildings\": [\n");
				var seen = new HashSet<GameObject>();
				var entries = new List<string>();
				for (int cell = 0; cell < Grid.CellCount; cell++)
				{
					GameObject building = Grid.Objects[cell, (int)ObjectLayer.Building];
					if (building == null || !seen.Add(building))
					{
						continue;
					}
					var complete = building.GetComponent<BuildingComplete>();
					if (complete == null || complete.Def == null)
					{
						continue;
					}

					int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
					foreach (int footprintCell in EnumerateFootprint(complete))
					{
						Grid.CellToXY(footprintCell, out int fx, out int fy);
						minX = Mathf.Min(minX, fx);
						minY = Mathf.Min(minY, fy);
						maxX = Mathf.Max(maxX, fx);
						maxY = Mathf.Max(maxY, fy);
					}
					if (minX == int.MaxValue)
					{
						continue;
					}

					Grid.CellToXY(cell, out int ox, out int oy);
					entries.Add("    {\"id\": \"" + RigFault.Escape(complete.Def.PrefabID)
						+ "\", \"x\": " + ox.ToString(c) + ", \"y\": " + oy.ToString(c)
						+ ", \"minx\": " + minX.ToString(c) + ", \"miny\": " + minY.ToString(c)
						+ ", \"maxx\": " + maxX.ToString(c) + ", \"maxy\": " + maxY.ToString(c) + "}");
				}
				for (int i = 0; i < entries.Count; i++)
				{
					text.Append(entries[i]).Append(i < entries.Count - 1 ? ",\n" : "\n");
				}
				text.Append("  ]\n}\n");

				File.WriteAllText(Path.Combine(directory, "layout.json"), text.ToString(), NoBomUtf8);
				layoutWritten = true;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] RigDiagnostics: could not write layout.json: " + e.Message);
			}
		}

		private static string Box(int minX, int minY, int maxX, int maxY)
		{
			CultureInfo c = CultureInfo.InvariantCulture;
			return "{\"minx\": " + minX.ToString(c) + ", \"miny\": " + minY.ToString(c)
				+ ", \"maxx\": " + maxX.ToString(c) + ", \"maxy\": " + maxY.ToString(c) + "}";
		}
	}
}
