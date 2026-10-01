using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// What a rig concluded, as one value.
	///
	/// <see cref="RigOutcome.Inconclusive"/> is not a third flavour of failure and must not be
	/// treated as one. It means the run did not produce a measurement -- most often because the
	/// game was paused, so every sample read the same numbers -- and a verdict computed from a
	/// paused sim is not a verdict at all.
	/// </summary>
	public enum RigOutcome
	{
		Pass,
		Fail,
		Inconclusive,
	}

	/// <summary>
	/// The common half of every live test scenario (a "rig"), so that the bespoke half is the
	/// only thing a new rig has to write.
	///
	/// WHY THIS EXISTS. Every rig needs the same scaffold -- a log prefix, an <c>Update</c> that
	/// fires once and starts a coroutine, a boot wait, a failure count, PASS and FAIL lines, and
	/// a summary -- and hand-written copies drift apart. The rules that matter most are the easy
	/// ones to skip: read <c>GameClock</c> so a paused game is not mistaken for a result, dismiss
	/// the modal dialogs that PAUSE the game, and always emit a final summary. This class is
	/// those rules as structure: a rig that derives from it gets them.
	///
	/// WHAT IT DELIBERATELY DOES NOT DO. It does not build anything, stamp anything, decide what
	/// to measure or decide what counts as correct. Those are the parts that are genuinely
	/// different per rig and the parts worth writing by hand. This class owns the lifecycle, the
	/// clock discipline and the reporting, and nothing else.
	///
	/// THE SHAPE OF A RIG.
	/// <code>
	/// public class Mod1ThingProbe : RigHarness
	/// {
	///     protected override string ModTag =&gt; "Mod1ThermoFluid";
	///     protected override string RigTag =&gt; "THING";
	///
	///     protected override IEnumerator Run()
	///     {
	///         if (!Require(Thing.IsInstalled, "Thing.Install ran")) yield break;
	///
	///         yield return Checkpoint(10f);
	///         Check(measured &gt; expected, "the thing moved heat");
	///     }
	/// }
	/// </code>
	/// The base emits the preamble, the summary and the machine-readable verdict line. The rig
	/// never counts anything, never formats a verdict, and never writes the word PASS.
	/// </summary>
	public abstract class RigHarness : MonoBehaviour
	{
		// ------------------------------------------------------------------ what a rig supplies

		/// <summary>Owning mod, e.g. <c>Mod1ThermoFluid</c>. Only ever used to build the prefix.</summary>
		protected abstract string ModTag { get; }

		/// <summary>
		/// Short upper-case tag for this rig, e.g. <c>EXHAUST</c>. It is what someone greps the
		/// log for, so it should be stable across renames of the class.
		/// </summary>
		protected abstract string RigTag { get; }

		/// <summary>
		/// The bespoke test. Everything specific to this rig lives here and nowhere else.
		///
		/// A <c>yield break</c> anywhere in here is a legitimate early exit -- the base still
		/// runs <see cref="Report"/> afterwards, so a rig that bails on a failed precondition
		/// still produces a verdict rather than a silent log.
		/// </summary>
		protected abstract IEnumerator Run();

		/// <summary>
		/// The blueprint canvas this rig measures on, relative to the mod DLL -- e.g.
		/// <c>"blueprints/turbine-fed.yaml"</c> -- or null for a rig that reads whatever save was
		/// loaded.
		///
		/// DECLARING IT DOES TWO THINGS, and the second is the one that matters. It names the
		/// canvas for <see cref="BlueprintRig.Install"/>, and it makes the base assert, before
		/// <see cref="Run"/> is ever entered, that the world really was built from it. That
		/// assertion exists because the failure it catches is silent and total: if the arming
		/// prefix does not run -- the flag missing, the YAML not copied next to the DLL, another
		/// rig already armed -- the game loads a real colony instead, every building the rig looks
		/// for is absent or is somebody else's, and the numbers that come out are measurements of
		/// the wrong world. A rig that discovered that by hand was writing the same Require() as
		/// every other rig; a rig that did not discover it reported nonsense.
		///
		/// A rig with no canvas is not wrong -- some rigs genuinely measure a live colony -- but
		/// it is now a stated choice rather than the default.
		/// </summary>
		protected virtual string BlueprintFile => null;

		/// <summary>
		/// Set true by a rig that deliberately measures a real colony save instead of building its
		/// own canvas.
		///
		/// THE DEFAULT FOR A NEW RIG IS THE BLUEPRINT CANVAS, and this property is what makes that
		/// a rule rather than a habit. A rig that names no
		/// <see cref="BlueprintFile"/> and does not set this fails immediately, before it can
		/// measure anything, with a message saying which of the two to pick.
		///
		/// WHY IT IS ENFORCED. Without enforcement the default is whatever a new rig gets by writing
		/// nothing, which is the colony save, and a save is a poor test bed: a large colony runs
		/// an order of magnitude slower than a canvas, may have no room left to stamp into, and its
		/// own plumbing writes to the same global counters a rig asserts on. None of those failures
		/// announces itself as "wrong test bed".
		///
		/// So the two failure modes this default prevents are: a rig measuring somebody else's
		/// world without knowing it, and a rig paying a colony's entire simulation cost for
		/// nothing. A rig that genuinely wants a populated colony -- a screenshot of a real base,
		/// a save/load round trip -- says so here, in one line, and that line is the record of the
		/// decision.
		/// </summary>
		protected virtual bool UsesColonySave => false;

		// ------------------------------------------------------------------ tunables

		/// <summary>
		/// Real seconds to wait after the scene exists before touching anything.
		///
		/// Every rig in the survey had this as a bare <c>WaitForSecondsRealtime(2f)</c> with no
		/// explanation, so here is the explanation: conduit tiles register with
		/// <c>ConduitFlow</c> on a QUEUED NATIVE CALLBACK that lands roughly half a second after
		/// the stamp, and the vanilla HUD screens a rig wants to find or hide are built over the
		/// first few frames of the scene. Two seconds is comfortably past both. A rig that needs
		/// longer should override this rather than adding a second wait of its own.
		/// </summary>
		protected virtual float BootDelaySeconds => 2f;

		/// <summary>
		/// Whether this rig is WATCHED -- narrated, filmed, or screenshotted -- rather than only
		/// read back out of a log.
		///
		/// When true the harness prepares the screen for it: the vanilla HUD a scenario is not
		/// using is hidden, and the game's notification list is pushed down so it does not sit
		/// under the narration's own panels (<c>DemoRig.HideVanillaHud</c> and
		/// <c>DemoRig.PushNotificationsDown</c>), so no rig has to remember to call them.
		///
		/// It also makes <see cref="RevealTestArea"/> mandatory in the sense that matters: a
		/// presentation rig that never calls it gets a loud line in its own log saying the run
		/// was filmed against whatever fog of war the save happened to have.
		///
		/// DEFAULTS TO TRUE, and that is the point. These were opt-in helpers for months and
		/// almost nobody opted in: one rig of twenty-two hid the HUD, one pushed the
		/// notifications, three revealed their own area. A presentation default that has to be
		/// asked for is a presentation default that does not happen.
		///
		/// Set false only for a rig that deliberately wants the player's own screen left alone --
		/// there is currently no such rig, and any that appears should say why here.
		/// </summary>
		protected virtual bool IsPresentation => true;

		/// <summary>
		/// Whether this rig keeps the game's own UI up on every run, flag or no flag -- the per-rig
		/// form of <see cref="KeepHudFlag"/>, which does exactly the same thing for one run.
		///
		/// Default false, and NO FLAGSHIP RIG SETS IT. The game UI is an opt-in for a run, not a
		/// property of a rig, so pass <see cref="KeepHudFlag"/> instead. A presentation rig films
		/// itself, and a HUD it did not ask for is a defect in the recording. Kept as a default-off
		/// member rather than removed because removing a shipped member is a MAJOR bump; any rig
		/// that ever overrides it should say here why its run can never be filmed.
		/// </summary>
		protected virtual bool ShowsGameUi => false;

		/// <summary>
		/// Command-line flag that gives a run the game's own UI -- the build and overlay menus, the
		/// tool bar, the details screen -- so a human can use them while the rig's world is up.
		/// Optional and off by default: without it an <see cref="IsPresentation"/> rig hides the
		/// HUD, because a HUD in a recording is a defect.
		///
		/// ON A BLUEPRINT CANVAS IT BUILDS THE HUD FIRST, because a canvas never has one to keep:
		/// every HUD screen is spawned on <c>GameHashes.StartGameUser</c>, which a canvas never fires
		/// (see <see cref="DemoRig.BuildVanillaHud"/> for the measurement). With this flag the
		/// harness fires that event before the boot wait, then logs
		/// <see cref="DemoRig.LogHudCensus"/> after it, so the log shows which screens came up. On a
		/// colony save the HUD already exists and is simply not hidden. Skipping the hide alone is
		/// not enough on a canvas: PlanScreen, ToolMenu and DetailsScreen are never created there.
		/// </summary>
		public const string KeepHudFlag = "--rig-keep-hud";

		/// <summary>
		/// Pixels to slide ONI's notification list down by when <see cref="IsPresentation"/> is
		/// set. The default clears a two-line title block plus a phase card.
		/// </summary>
		protected virtual float NotificationPushdownPixels => 220f;

		/// <summary>
		/// Whether this rig BUILDS a test volume of its own -- paints terrain, stamps a template,
		/// puts up walls -- as opposed to reading a live colony or a static table.
		///
		/// It gates one warning: a rig that stages a volume and never calls
		/// <see cref="JacketTestVolume"/> is measuring against the whole asteroid. Default false,
		/// and the default is the honest one -- a rig that measures a running colony has no volume
		/// to seal, and nagging it about a jacket would be noise. A rig that stages one sets this true, and
		/// the ones that do are exactly the ones that call JacketTestVolume anyway.
		/// </summary>
		protected virtual bool StagesATestVolume => false;

		/// <summary>
		/// Blacks out the whole map and reveals only this rig's own rectangle.
		///
		/// CALL THIS ONCE THE RIG KNOWS WHERE IT LANDED, which is why the harness cannot do it
		/// for you: the region is the one thing about a rig the base class cannot derive. What it
		/// CAN do is notice that a presentation rig never called it, and say so.
		///
		/// A stamp paints terrain but does not EXPLORE it, so a rig that lands in unexplored map
		/// renders as solid black and the run is invisible until someone turns on debug mode by
		/// hand. And a rig revealed inside an already-explored colony competes for attention with
		/// everything else on screen. Blacking out first and then revealing only the test area
		/// solves both, on any save. Rule 6.
		///
		/// Coordinates are inclusive world cell coordinates. <see cref="ClaimTestRegion"/> calls
		/// this for you, and is the better entry point when the rig is searching for somewhere to
		/// build rather than computing its own rectangle.
		/// </summary>
		protected void RevealTestArea(int minX, int minY, int maxX, int maxY)
		{
			DemoRig.RevealOnly(minX, minY, maxX, maxY);
			revealedTestArea = true;

			// Hands the diagnostics the one thing they cannot derive: which rectangle is this
			// rig's. Without it the whole-map detectors stay quiet rather than reporting the
			// entire asteroid, so this call is what switches them on.
			RigDiagnostics.SetTestBounds(minX, minY, maxX, maxY);
			Log(string.Format("revealed the test area only: ({0},{1}) to ({2},{3}), rest of the "
				+ "map blacked out", minX, minY, maxX, maxY));
		}

		/// <summary>
		/// <see cref="RevealTestArea"/> as a square around a cell, for a rig that has an anchor
		/// rather than a rectangle.
		///
		/// THIS IS NOT <c>Mod1AutoSetup.RevealAround</c>, and the difference is the whole point.
		/// That helper calls <c>Grid.Reveal</c>, which takes a Math.Max against the current
		/// visibility and therefore only ever RAISES it -- so it uncovers the rig and leaves the
		/// rest of the map exactly as explored as it already was. Ten rigs called it and looked
		/// like they were following rule 6 while doing the opposite of the half that matters: the
		/// map is never darkened, so a rig stamped inside an explored colony is filmed competing
		/// with the whole base for attention.
		/// </summary>
		protected void RevealTestAreaAround(int centerCell, int radius)
		{
			Grid.CellToXY(centerCell, out int cx, out int cy);
			RevealTestArea(cx - radius, cy - radius, cx + radius, cy + radius);
		}

		/// <summary>
		/// Seals this rig's test volume off from the map: one ring of vacuum around it, and one
		/// solid ring outside that to hold the vacuum in. Rule 5.
		///
		/// WHY A RIG NEEDS THIS AT ALL. A rig is built into whatever terrain the save happened to
		/// have there. That terrain has its own temperature and, for any purpose a rig has, an
		/// infinite thermal mass, so the test volume is dragged toward it through a heat path the
		/// experiment never chose and cannot control. Vacuum conducts nothing, so one tile of it
		/// removes the path entirely; the solid ring outside is what stops the surrounding
		/// atmosphere flowing into the vacuum and undoing it. An insulated wall is NOT a
		/// substitute -- insulation slows the leak, vacuum removes it.
		///
		/// The size of what it hides is easy to underestimate. PhaseChangeLoopDemo's two rooms sit
		/// inside a jacket and STILL drift a few tenths of a kelvin over a run as they equilibrate
		/// with their own shell; without the jacket that drift is against the whole asteroid, and
		/// it buried a working heat pump's signal completely.
		///
		/// CALL IT AFTER THE VOLUME IS PAINTED, not before: this lays rings around the block and
		/// deliberately does not touch anything inside it, but a rig that paints its interior
		/// afterwards over a wider rectangle would erase its own jacket. Coordinates are inclusive
		/// world cell coordinates describing the block to enclose.
		///
		/// A rig that stamps a TemplateContainer should use <c>DemoRig.AddVacuumJacket</c>
		/// instead, which appends the same two rings to the template's own cell list.
		/// </summary>
		protected void JacketTestVolume(int minX, int minY, int maxX, int maxY,
			float temperatureK = 294.15f, int vacuumMargin = 1, bool alsoReveal = true)
		{
			DemoRig.PaintVacuumJacket(minX, minY, maxX, maxY, vacuumMargin, temperatureK);
			jacketedTestVolume = true;

			// THE BOUNDS HANDED TO THE DIAGNOSTICS ARE THE VOLUME PLUS ITS INNER SEALING RING,
			// not the volume. PaintVacuumJacket lays a solid ring immediately outside the block
			// (see its comment for the measurement that put it there), so the vacuum starts one
			// tile further out than the caller's rectangle would suggest. Expanding the rectangle
			// by one keeps every ring index in RigDiagnostics -- vacuum at 1..margin, shell at
			// margin+1 -- pointing at the band it names, with no second convention to keep in
			// step.
			RigDiagnostics.SetJacketBounds(minX - 1, minY - 1, maxX + 1, maxY + 1, vacuumMargin);
			Log(string.Format("jacketed the test volume: ({0},{1}) to ({2},{3}), a sealing ring "
				+ "around it, {4} ring(s) of vacuum outside that and a second sealing ring "
				+ "outside those, so the map cannot conduct into it and it cannot expand out",
				minX, minY, maxX, maxY, vacuumMargin));

			// AND RE-AIM THE FOG OF WAR AT IT, because this rectangle is the one thing in a rig
			// that is definitionally the test volume. A rig reveals around its anchor cell, and
			// the anchor is NOT where the rig gets built -- rigs offset from it by whatever keeps
			// them clear of each other's footprints -- so a reveal around the anchor can leave
			// half the rig in darkness beside an empty lit rectangle.
			//
			// A jacketed rig no longer has to get that right by hand. The margin is a few tiles
			// wider than the jacket so the sealing ring and a little of the rock it is set into
			// are visible -- a rig floating in a black void reads as a rendering fault rather
			// than as an isolated experiment.
			if (alsoReveal)
			{
				// vacuumMargin + 2 is the jacket's full thickness: inner seal, vacuum, outer seal.
				const int RevealMargin = 5;
				int outer = vacuumMargin + 2 + RevealMargin;
				RevealTestArea(minX - outer, minY - outer, maxX + outer, maxY + outer);
			}
		}

		/// <summary>
		/// The TEMPLATE-PATH twin of <see cref="JacketTestVolume"/>: appends the two jacket rings
		/// to a template's own cell list, and re-aims the fog of war at where that template will
		/// actually land.
		///
		/// THE REVEAL IS THE HALF THAT KEEPS GOING WRONG, and it is why this exists rather than
		/// rigs calling <c>DemoRig.AddVacuumJacket</c> directly. A rig reveals around its ANCHOR
		/// and then builds at an OFFSET from it, and the two have no reason to agree: a rig stamped
		/// just above the lit area films as a black screen.
		///
		/// ORDER MATTERS: call this BEFORE appending the rig's own cells. Both jacket rings are
		/// full rectangles and every later cell overwrites the one under it, so the rig's own
		/// block is what punches the middle of the jacket back out. A rig that does not paint its
		/// whole block leaves VACUUM wherever it skipped -- which is a hole in a wall, not a wall.
		/// </summary>
		/// <param name="cells">The template cell list being built. Appended to.</param>
		/// <param name="originX">Where the template's local (0,0) will land.</param>
		/// <param name="originY">Where the template's local (0,0) will land.</param>
		/// <param name="blockWidth">Width of the rig's own block, in tiles.</param>
		/// <param name="blockHeight">Height of the rig's own block, in tiles.</param>
		protected void JacketTemplateBlock(List<TemplateClasses.Cell> cells, int originX,
			int originY, int blockWidth, int blockHeight, float temperatureK = 294.15f,
			float sealMassKg = 2000f, int vacuumMargin = 1, bool alsoReveal = true)
		{
			DemoRig.AddVacuumJacket(cells, blockWidth, blockHeight, vacuumMargin, temperatureK,
				sealMassKg);
			jacketedTestVolume = true;
			RigDiagnostics.SetJacketBounds(originX, originY,
				originX + blockWidth - 1, originY + blockHeight - 1, vacuumMargin);
			Log(string.Format("jacketed the template block: local (0,0) to ({0},{1}) landing at "
				+ "({2},{3}), {4} ring(s) of vacuum and a sealing ring outside them",
				blockWidth - 1, blockHeight - 1, originX, originY, vacuumMargin));

			if (alsoReveal)
			{
				const int RevealMargin = 5;
				RevealTestArea(originX - vacuumMargin - RevealMargin,
					originY - vacuumMargin - RevealMargin,
					originX + blockWidth - 1 + vacuumMargin + RevealMargin,
					originY + blockHeight - 1 + vacuumMargin + RevealMargin);
			}
		}

		/// <summary>
		/// <see cref="JacketTestVolume"/> as a square around a cell, for a rig that has an anchor
		/// rather than a rectangle.
		/// </summary>
		protected void JacketTestVolumeAround(int centerCell, int radius,
			float temperatureK = 294.15f)
		{
			Grid.CellToXY(centerCell, out int cx, out int cy);
			JacketTestVolume(cx - radius, cy - radius, cx + radius, cy + radius, temperatureK);
		}

		/// <summary>
		/// Finds somewhere clear to build, remembers it, and reveals exactly it.
		///
		/// THREE CHECKLIST RULES IN ONE CALL, which is why it exists as one call. Rule 3 says
		/// search for a clear region rather than using a fixed anchor, because a fixed offset
		/// lands on whatever an earlier rig stamped there, one rig on top of another. Black out the map and reveal
		/// only the test area, because a stamp paints terrain without EXPLORING it and a rig in
		/// unexplored map films as a black rectangle. And the reveal needs the region, so a rig
		/// that does its own searching then has to remember to reveal what it found -- an easy step
		/// to skip.
		///
		/// <paramref name="margin"/> should cover whatever shell, jacket or sealing ring the rig
		/// stamps around its own block, plus a tile, so two rigs cannot end up flush against each
		/// other with one stamping over the other's outer wall. The revealed rectangle covers the
		/// margin too, so the jacket is visible rather than a black frame around the rig.
		///
		/// Returns false when nothing clear was found within the search radius, which is a fatal
		/// condition for any rig that builds -- there is nowhere to put it.
		/// </summary>
		protected bool ClaimTestRegion(int anchorX, int anchorY, int width, int height, int margin,
			out int foundX, out int foundY)
		{
			if (!DemoRig.FindClearRegion(anchorX, anchorY, width, height, margin,
				out foundX, out foundY))
			{
				Log(string.Format("no clear {0}x{1} region (plus {2} margin) found near ({3},{4})",
					width, height, margin, anchorX, anchorY));
				return false;
			}

			Log(string.Format("claimed a clear {0}x{1} region at ({2},{3}), margin {4}",
				width, height, foundX, foundY, margin));
			RevealTestArea(foundX - margin, foundY - margin,
				foundX + width + margin, foundY + height + margin);
			return true;
		}

		/// <summary>
		/// Whether the world is far enough along for this rig to start.
		///
		/// Defaults to "the grid exists", which is what every rig in the survey tested and is the
		/// right gate for anything that builds, stamps or measures cells. A rig whose subject is
		/// not the grid should override it with the readiness condition it actually needs --
		/// Mod 2's material report waits on <c>ElementLoader</c> instead, because that is what
		/// supplies the molar masses it checks, and the grid is irrelevant to it.
		///
		/// Polled from <c>Update</c>, so it must be cheap and must not throw before the thing it
		/// is testing for exists.
		/// </summary>
		protected virtual bool IsReady => Grid.CellCount > 0;

		/// <summary>
		/// Whether <see cref="Report"/> may print a PASS/FAIL verdict when the game clock never
		/// advanced. Default false, and it should stay false for anything that measures.
		///
		/// The exceptions are rigs that assert on STATIC structure -- that a building def
		/// resolves, that a template stamped, that a texture loaded -- where nothing about the
		/// answer depends on the sim having ticked. Those rigs set this true and say why.
		/// </summary>
		protected virtual bool VerdictSurvivesAStoppedClock => false;

		// ------------------------------------------------------------------ what a rig gets

		/// <summary>Log prefix, e.g. <c>"[Mod1ThermoFluid] EXHAUST: "</c>. Built once.</summary>
		protected string Prefix { get; private set; }

		/// <summary>Passing assertions so far.</summary>
		protected int PassCount { get; private set; }

		/// <summary>Failing assertions so far.</summary>
		protected int FailCount { get; private set; }

		/// <summary>Game-clock seconds between the first and the most recent checkpoint.</summary>
		protected float ElapsedSimSeconds =>
			clockAtLastCheckpoint - clockAtFirstCheckpoint;

		/// <summary>
		/// True once the game clock has been observed to move. Read it before drawing a
		/// conclusion from a rate, and note that <see cref="Report"/> already refuses to publish
		/// a verdict without it unless <see cref="VerdictSurvivesAStoppedClock"/> says otherwise.
		/// </summary>
		protected bool ClockAdvanced => clockAtLastCheckpoint > clockAtFirstCheckpoint;

		// ------------------------------------------------------------------ the live snapshot

		/// <summary>
		/// The rig running in this process, or null before one starts. One per process: rigs are
		/// selected by a command-line flag and no launch has ever armed two.
		/// </summary>
		private static RigHarness live;

		/// <summary>
		/// What the RUNNING rig has already asserted, for <see cref="DemoNarrator.Begin"/> to
		/// start its tally from instead of from zero.
		///
		/// WHY IT EXISTS. A rig calls <c>DemoNarrator.Begin</c> from inside its own body, which is
		/// well after the harness has run the canvas check, the blueprint check and whatever
		/// preconditions the rig declares -- and the narrator counts by scraping log lines, so
		/// every assertion made before that call was never offered to it. The on-screen tally
		/// therefore under-reported for the whole run: measured on the recorded films,
		/// AIRLOOP's tally read 12 pass against 30 asserted, PIPESTRESS 31 against 37, BREATHTEST
		/// 1 against 5. The gap is exactly the pre-Begin assertions in each case.
		///
		/// This is NOT a second source of truth for the number -- the point of handing the
		/// narrator a seed rather than letting it keep its own running total is that both numbers
		/// still come from the same assertions in the same order. It only stops the narrator's
		/// copy from beginning halfway through.
		/// </summary>
		internal static void CurrentAssertionCounts(out int pass, out int fail)
		{
			RigHarness rig = live;
			pass = rig != null ? rig.PassCount : 0;
			fail = rig != null ? rig.FailCount : 0;
		}

		/// <summary>
		/// What a rig had done at the moment somebody asked, for a reader OUTSIDE the rig.
		///
		/// This exists for exactly one caller -- <see cref="RigCrashScreen"/>, which has to
		/// describe a run from inside Klei's crash dialog, where there is no rig instance to ask
		/// and every field it wants is <c>protected</c>. A crash screen that said "an error
		/// occurred" when the process knows "47 PASS / 0 FAIL, in the phase called verifying" is
		/// throwing away the only context anybody has at that moment.
		///
		/// A snapshot rather than a live handle, and public rather than internal, because the
		/// caller is allowed to read what a rig has done and is not allowed to touch it.
		/// </summary>
		public struct LiveStatus
		{
			/// <summary>False when no rig has started; every other field is then meaningless.</summary>
			public bool Running;

			/// <summary>The rig's own tag, e.g. <c>"EXTREGISTRY"</c>.</summary>
			public string Rig;

			/// <summary>The owning mod's tag, e.g. <c>"Mod1ThermoFluid"</c>.</summary>
			public string Mod;

			/// <summary>Passing assertions at the moment of the snapshot.</summary>
			public int Pass;

			/// <summary>Failing assertions at the moment of the snapshot.</summary>
			public int Fail;

			/// <summary>The phase name the rig last set, e.g. <c>"verifying"</c>.</summary>
			public string Phase;

			/// <summary>Game-clock seconds between the first and most recent checkpoint.</summary>
			public float SimSeconds;
		}

		/// <summary>
		/// A snapshot of <see cref="live"/>, or <c>Running == false</c> when no rig is going.
		/// </summary>
		public static LiveStatus Status
		{
			get
			{
				LiveStatus s = default(LiveStatus);
				RigHarness r = live;
				if (r == null)
				{
					return s;
				}
				s.Running = true;
				s.Rig = r.RigTag;
				s.Mod = r.ModTag;
				s.Pass = r.PassCount;
				s.Fail = r.FailCount;
				s.Phase = r.CheckpointPhaseName;
				s.SimSeconds = r.ElapsedSimSeconds;
				return s;
			}
		}

		// ------------------------------------------------------------------ assertions

		/// <summary>
		/// A precondition. Logs PASS or FAIL exactly like <see cref="Check"/> and returns the
		/// condition, so the call site reads
		/// <c>if (!Require(x, "...")) yield break;</c>.
		///
		/// Separate from <see cref="Check"/> because a precondition failure means the run never
		/// happened, and the summary says so: a FAIL from a rig that was never built correctly tells
		/// you nothing about the mechanism.
		/// </summary>
		protected bool Require(bool condition, string what)
		{
			if (condition)
			{
				Pass(what);
				return true;
			}
			preconditionFailed = true;
			Fail(what);
			return false;
		}

		/// <summary>
		/// One assertion about the mechanism under test. Logs a PASS or FAIL line and counts it.
		///
		/// <paramref name="what"/> is stated as the thing that is TRUE when the check passes
		/// ("the room cooled", not "checking whether the room cooled"), so the same string reads
		/// correctly after both PASS and FAIL.
		/// </summary>
		protected bool Check(bool condition, string what)
		{
			if (condition)
			{
				Pass(what);
			}
			else
			{
				Fail(what);
			}
			return condition;
		}

		/// <summary>Logs and counts a passing assertion.</summary>
		protected void Pass(string what)
		{
			PassCount++;
			Log("PASS -- " + what);
		}

		/// <summary>Logs and counts a failing assertion.</summary>
		protected void Fail(string what)
		{
			FailCount++;
			Log("FAIL -- " + what);
		}

		/// <summary>
		/// Declares that this run cannot produce a verdict, and why.
		///
		/// Latches: once a run is inconclusive nothing later can make it PASS, because the reason
		/// it was inconclusive (usually a stopped clock, sometimes an empty colony with nothing to
		/// measure) applies to every sample the run went on to take.
		/// </summary>
		protected void Inconclusive(string why)
		{
			inconclusiveReason = inconclusiveReason ?? why;
			Log("INCONCLUSIVE -- " + why);
		}

		/// <summary>A plain log line, prefixed. Use for ledgers, readings and narration.</summary>
		protected void Log(string message)
		{
			// Plain `Debug`, deliberately: Assembly-CSharp-firstpass declares a global-namespace
			// `Debug` that outranks `using UnityEngine;`, and DemoNarrator's on-screen run log is
			// a Harmony patch on THAT one. Reaching for UnityEngine.Debug explicitly here would
			// put the line in Player.log and nowhere the viewer can see it.
			Debug.Log(Prefix + message);
		}

		// ------------------------------------------------------------------ shape of the report

		/// <summary>
		/// A section heading, so every rig's transcript breaks into phases the same way.
		///
		/// Rigs had been writing their own -- <c>"=== PHASE A ==="</c>, <c>"---- setup ----"</c>,
		/// bare sentences -- which made two transcripts side by side hard to compare even when the
		/// runs were comparable. One helper, one shape.
		/// </summary>
		protected void Section(string title)
		{
			Log("=== " + (title ?? string.Empty).ToUpperInvariant() + " ===");
		}

		/// <summary>
		/// One line of scene-setting before the assertions start: what this rig is measuring and
		/// what would make it wrong. Printed once, at the top, in the same place in every rig.
		/// </summary>
		protected void Scenario(string whatIsMeasured)
		{
			Log("scenario -- " + whatIsMeasured);
		}

		// Column widths for the current table, so rows line up under their header without every
		// rig hand-tuning format strings.
		private int[] tableWidths;

		/// <summary>
		/// Starts a sample table and prints its header. Column width is taken from the header
		/// text (with a floor), and <see cref="TableRow"/> pads to the same widths, so a rig gets
		/// aligned output without inventing a format string per column.
		/// </summary>
		protected void TableHeader(params string[] columns)
		{
			if (columns == null || columns.Length == 0)
			{
				tableWidths = null;
				return;
			}
			tableWidths = new int[columns.Length];
			var sb = new System.Text.StringBuilder("  ");
			for (int i = 0; i < columns.Length; i++)
			{
				string c = columns[i] ?? string.Empty;
				tableWidths[i] = Mathf.Max(c.Length, 6);
				sb.Append(c.PadRight(tableWidths[i]));
				if (i + 1 < columns.Length)
				{
					sb.Append("  ");
				}
			}
			Log(sb.ToString());
		}

		/// <summary>
		/// One row of the table opened by <see cref="TableHeader"/>. Values are stringified with
		/// <c>ToString()</c>, so a caller formats numbers itself
		/// (<c>value.ToString("F2")</c>) and this only aligns them.
		/// </summary>
		protected void TableRow(params object[] values)
		{
			if (values == null || values.Length == 0)
			{
				return;
			}
			var sb = new System.Text.StringBuilder("  ");
			for (int i = 0; i < values.Length; i++)
			{
				string v = values[i] == null ? string.Empty : values[i].ToString();
				int width = tableWidths != null && i < tableWidths.Length ? tableWidths[i] : v.Length;
				sb.Append(v.PadRight(width));
				if (i + 1 < values.Length)
				{
					sb.Append("  ");
				}
			}
			Log(sb.ToString());
		}

		// ------------------------------------------------------------------ the clock discipline

		/// <summary>
		/// Waits <paramref name="realSeconds"/>, dismissing modal dialogs and unpausing on the way
		/// in and on the way out, and samples the game clock at both ends.
		///
		/// USE THIS INSTEAD OF <c>WaitForSecondsRealtime</c>, everywhere, and that is the whole
		/// point of it. A modal dialog PAUSES the game; a paused game starves the native sim of
		/// ticks; every sample afterwards then reads identical numbers, which looks exactly like
		/// several independently broken mechanics rather than like one paused game. That
		/// misreading cost a full debugging round before the rule was written down, and the rule
		/// then went unfollowed by 18 of 22 rigs because following it was manual.
		///
		/// Dismissing on the way OUT as well as in is not redundant: COLONY LOST appears on its
		/// own schedule -- <c>GameFlowManager.CheckForGameOver</c> fires as soon as there are no
		/// live minions, which is the normal state of this project's sandbox saves -- so a dialog
		/// that was absent when the wait began is routinely up when it ends.
		/// </summary>
		protected IEnumerator Checkpoint(float realSeconds)
		{
			DemoRig.DismissModalsAndUnpause(Prefix);
			SampleClock();

			if (realSeconds > 0f)
			{
				// Metered per wait rather than only over the whole run, because the complaint
				// this instrument exists to answer is always about a CHANGE -- "it starts
				// lagging after about ten seconds" -- and a single run-long average cannot show
				// one. Each wait prints its own figure, so degradation reads straight off the
				// transcript.
				framesInWindow = 0;
				worstFrameSecondsInWindow = 0f;
				float wallStart = Time.realtimeSinceStartup;
				float simStart = GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f;

				yield return new WaitForSecondsRealtime(realSeconds);

				float wallSeconds = Time.realtimeSinceStartup - wallStart;
				float simSeconds = (GameClock.Instance != null
					? GameClock.Instance.GetTime() : 0f) - simStart;
				if (wallSeconds > 0f)
				{
					float fps = framesInWindow / wallSeconds;
					// Accumulated for every wait, but only PRINTED for one long enough to mean
					// something. A probe that samples on a 0.25 s loop would otherwise bury its
					// own measurement under eighty perf lines, and a quarter-second window is too
					// short to average a frame rate over anyway.
					bool worthPrinting = realSeconds >= 1f;
					meteredWallSeconds += wallSeconds;
					if (!meteredSimSeen)
					{
						meteredSimSecondsStart = simStart;
						meteredSimSeen = true;
					}
					if (fps < slowestWindowFps)
					{
						slowestWindowFps = fps;
					}
					if (worthPrinting)
					{
					Log(string.Format(
						"perf: {0:F1} fps over {1:F1} s of wall clock (worst frame {2:F0} ms), "
						+ "sim advanced {3:F1} s = {4:F2}x",
						fps, wallSeconds, worstFrameSecondsInWindow * 1000f, simSeconds,
						wallSeconds > 0f ? simSeconds / wallSeconds : 0f));
					}
				}
			}

			DemoRig.DismissModalsAndUnpause(Prefix);
			SampleClock();
		}

		/// <summary>
		/// The dismiss-and-sample half of <see cref="Checkpoint"/> with no wait, for a rig that is
		/// running its own timing loop and only needs the clock kept honest.
		/// </summary>
		protected void Checkpoint()
		{
			DemoRig.DismissModalsAndUnpause(Prefix);
			SampleClock();

			// Runs the cell-addressed fault detectors and writes what they find beside the frames,
			// tagged with the frame index that was current. Nothing here fails the rig: whether a
			// detected Error should end a run is the rig's judgement, not the detector's, and a rig
			// that WANTS that behaviour calls FailOnDiagnostics instead.
			RigDiagnostics.ScanAndRecord(CheckpointPhaseName);
		}

		/// <summary>
		/// What the current checkpoint is called in <c>diagnostics.jsonl</c>. Rigs with named
		/// phases should override or set this so a finding can be traced back to the part of the
		/// run that produced it; the default is good enough to be greppable and no more.
		/// </summary>
		protected string CheckpointPhaseName { get; set; } = "checkpoint";

		/// <summary>
		/// Scans, and fails the rig on any <see cref="RigFaultSeverity.Error"/>. For a rig that
		/// would rather stop than measure a scenario that is not the one it described -- which is
		/// most of them, since a wrong scenario still produces numbers and they still look like
		/// physics.
		/// </summary>
		protected bool FailOnDiagnostics(string phase)
		{
			System.Collections.Generic.List<RigFault> faults = RigDiagnostics.ScanAndRecord(phase);
			for (int i = 0; i < faults.Count; i++)
			{
				if (faults[i].Severity != RigFaultSeverity.Error)
				{
					continue;
				}
				Fail($"{faults[i].Code} at ({faults[i].MinX},{faults[i].MinY}).."
					+ $"({faults[i].MaxX},{faults[i].MaxY}): {faults[i].Message}");
				return false;
			}
			return true;
		}

		/// <summary>
		/// Validates the active blueprint against the live game and stamps its buildings, waiting
		/// for Klei's loader to finish.
		///
		/// THE VALIDATION IS NOT A FORMALITY. <c>BlueprintBuilder.ValidateAgainstGame</c> is the
		/// only thing that knows a five-wide building does not occupy the cell it is named at, and
		/// that a building stamped into solid terrain is placed anyway and ends up buried -- a
		/// defect that was found by eye on a screenshot after several rounds of patching the wrong
		/// thing. Running it here rather than leaving it to each rig means a blueprint cannot be
		/// half-built: the builder refuses the whole thing on any error, because half a scenario
		/// still produces numbers and they still look like physics.
		/// </summary>
		private IEnumerator PlaceBlueprintBuildings()
		{
			blueprintBuildFailed = true;

			System.Collections.Generic.List<string> errors;
			if (!Require(BlueprintBuilder.ValidateAgainstGame(BlueprintWorld.Active, out errors),
				string.Format("every building in {0} resolves and its real footprint fits "
					+ "({1} error(s))", BlueprintFile, errors.Count)))
			{
				for (int i = 0; i < errors.Count; i++)
				{
					Log("  " + errors[i]);
				}
				yield break;
			}

			bool built = false;
			if (!Require(BlueprintBuilder.Build(BlueprintWorld.Active, () => built = true),
				"the builder accepted " + BlueprintFile))
			{
				yield break;
			}

			// TemplateLoader.Stamp runs across real sim ticks and calls back when it is done, so
			// the buildings do not exist on the next line. Twenty seconds is far longer than any
			// blueprint this project has needed and is a timeout rather than a wait.
			float waited = 0f;
			while (!built && waited < BlueprintStampTimeoutSeconds)
			{
				yield return Checkpoint(0.5f);
				waited += 0.5f;
			}
			if (!Require(built, "the builder finished placing the buildings in " + BlueprintFile))
			{
				yield break;
			}

			blueprintBuildFailed = false;
		}

		/// <summary>
		/// True when the rig places the blueprint's buildings in its own <c>Run</c> and the base
		/// must not. Default false: naming a <see cref="BlueprintFile"/> is normally enough, and
		/// the base validates and stamps before <c>Run</c> is entered.
		/// </summary>
		protected virtual bool BuildsBlueprintItself => false;

		/// <summary>How long to wait for Klei's asynchronous stamp before calling it a failure.</summary>
		protected virtual float BlueprintStampTimeoutSeconds => 20f;

		private bool blueprintBuildFailed;

		private void SampleClock()
		{
			if (GameClock.Instance == null)
			{
				return;
			}

			float now = GameClock.Instance.GetTime();
			if (!clockSeen)
			{
				clockAtFirstCheckpoint = now;
				clockSeen = true;
			}
			clockAtLastCheckpoint = now;
		}

		/// <summary>
		/// Publishes the summary and the verdict NOW, for a rig whose <see cref="Run"/> never
		/// returns.
		///
		/// An inspect-mode rig builds something and then logs a ledger in a <c>while (true)</c>
		/// until the process is killed. Nothing is wrong with that, but a coroutine that never
		/// returns never reaches the base's own reporting, so it would emit no RIG-VERDICT line at
		/// all -- and a runner gating on that line cannot tell "never reported" from "never ran".
		///
		/// <paramref name="whyItNeverReturns"/> is recorded as the reason the verdict is
		/// inconclusive, because it is: a rig that runs until someone kills it has not concluded
		/// anything, it has produced a log for a person to read.
		///
		/// Call this immediately before entering the loop. Anything asserted before the call is
		/// counted in the summary; anything after it is logged but not counted, so put the
		/// assertions first.
		///
		/// A RIG THAT DID CONCLUDE SOMETHING BEFORE THE LOOP WANTS
		/// <see cref="ReportThenKeepLogging"/> INSTEAD. The latch above is
		/// unconditional, so a rig that asserts its checks first and only then starts narrating
		/// would have its whole verdict overwritten by this method.
		/// </summary>
		protected void ReportBeforeLoopingForever(string whyItNeverReturns)
		{
			Inconclusive(whyItNeverReturns);
			Report();
			reported = true;
		}

		/// <summary>
		/// Publishes the REAL verdict NOW, for a rig that has finished asserting and then keeps
		/// logging until the process is killed.
		///
		/// THE DIFFERENCE FROM <see cref="ReportBeforeLoopingForever"/> IS WHETHER THE RIG
		/// CONCLUDED ANYTHING. That method latches <see cref="Inconclusive"/>, and it is right to:
		/// a rig that builds something and then narrates a ledger for a person to read has not
		/// established a result, so calling it PASS would be a verdict computed from nothing. A rig
		/// that asserts its checks and THEN narrates has established one, and latching
		/// Inconclusive over the top of passing checks would misreport it.
		///
		/// So this reports what <see cref="Report"/> computes -- PASS, FAIL or Inconclusive on its
		/// own merits -- and records <paramref name="whatTheLoopIsFor"/> as narration rather than
		/// as a reason the run cannot conclude.
		///
		/// IT CANNOT MAKE AN EMPTY RIG GREEN. Every guard in <see cref="Report"/> still applies:
		/// a stopped clock, a failed precondition, a failed assertion, or a rig that asserted
		/// nothing at all each still resolve the way they always did. This surface removes one
		/// unconditional latch; it does not weaken a single check behind it.
		///
		/// Call this immediately before entering the loop, for the same reason as its sibling:
		/// anything asserted before the call is counted, anything after it is logged and lost.
		/// A rig that has genuinely concluded nothing wants the sibling, not this.
		/// </summary>
		protected void ReportThenKeepLogging(string whatTheLoopIsFor)
		{
			Log("MEASURED WINDOW CLOSED -- " + whatTheLoopIsFor);
			Report();
			reported = true;
		}

		// ------------------------------------------------------------------ lifecycle

		private bool started;
		private bool reported;

		/// <summary>Set while <see cref="ReportIfQuitFirst"/> reports from inside
		/// Application.quitting, when the narrator's UI may already be going away.</summary>
		private bool quitting;

		/// <summary>
		/// Whether <c>yield return Run()</c> returned. Only the quit path reads it: a verdict
		/// published from <see cref="ReportIfQuitFirst"/> while this is false covers a rig that
		/// never reached its own conclusions.
		/// </summary>
		private bool runCompleted;
		private bool revealedTestArea;

		/// <summary>Whether <see cref="JacketTestVolume"/> ran, so the base can notice a rig
		/// that stages a volume and never sealed it.</summary>
		private bool jacketedTestVolume;
		private bool clockSeen;
		private bool preconditionFailed;
		private float clockAtFirstCheckpoint;
		private float clockAtLastCheckpoint;
		private string inconclusiveReason;

		// ---------------------------------------------------------------- the frame-time meter
		//
		// WHY A RIG MEASURES ITS OWN FRAME RATE. "The sim is lagging" without a number attached
		// starts every investigation by guessing which of a dozen per-tick sweeps to suspect,
		// and a claim about speed (a blank canvas runs faster than a large colony) with no
		// before-and-after figure is an opinion.
		//
		// SIM THROUGHPUT IS NOT THE SAME QUANTITY and both are reported. A rig runs at 1x, so
		// GameClock advancing one second per real second is the ceiling and says nothing about
		// how the game FELT; a run can hold 1.00x sim while drawing at 6 fps. The pair separates
		// "the sim cannot keep up" from "the render thread cannot".
		private int framesInWindow;
		private float worstFrameSecondsInWindow;
		private long framesTotal;
		private float worstFrameSeconds;
		private float meteredWallSeconds;
		private float meteredSimSecondsStart;
		private bool meteredSimSeen;
		private float slowestWindowFps = float.MaxValue;

		private void Update()
		{
			// Frame accounting FIRST, before the early return below -- the whole run happens
			// after `started` goes true, so counting under that guard would meter nothing.
			// Unscaled: this is wall-clock frame time, and the game's own timescale is exactly
			// the thing being measured against.
			framesInWindow++;
			framesTotal++;
			float frameSeconds = Time.unscaledDeltaTime;
			if (frameSeconds > worstFrameSecondsInWindow)
			{
				worstFrameSecondsInWindow = frameSeconds;
			}
			if (frameSeconds > worstFrameSeconds)
			{
				worstFrameSeconds = frameSeconds;
			}

			// Firing once is what the per-rig `done` flag was for; IsReady is the per-rig
			// "the world exists yet" test, defaulting to the grid.
			if (started || !IsReady)
			{
				return;
			}
			started = true;
			Application.quitting += ReportIfQuitFirst;
			StartCoroutine(Drive());
		}

		/// <summary>
		/// Publishes the verdict when the game quits before <see cref="Drive"/> could.
		///
		/// WHY. Report() runs after <c>yield return Run()</c> resumes, and a rig that calls
		/// Application.Quit inside Run() (an Abort on a failed precondition is the usual shape)
		/// can shut the game down before that happens, leaving a FAIL line and no RIG-VERDICT, so a
		/// runner waiting on the verdict times out and reports a broken run instead of a failing
		/// one. Handling it here covers every rig that quits from inside Run().
		///
		/// IT DOES NOT SAY WHY THE GAME IS QUITTING, BECAUSE IT DOES NOT KNOW. This handler runs
		/// off <c>Application.quitting</c>, which fires for every graceful shutdown whatever
		/// caused it -- a rig's own Application.Quit, a `taskkill /PID` posting WM_CLOSE, or a
		/// human closing the window. A guessed cause gets believed, so this reports the fact (the
		/// game is going), what it costs (Run() was still in flight), and leaves the cause to
		/// whoever can actually establish it.
		/// </summary>
		private void ReportIfQuitFirst()
		{
			Application.quitting -= ReportIfQuitFirst;
			// A harness destroyed without quitting leaves nothing to report for; Unity's
			// overloaded == says so after Destroy even though the managed object lives on.
			if (this == null || reported)
			{
				return;
			}
			quitting = true;
			Log(runCompleted
				? "the game is quitting before the harness reported, but Run() had already "
					+ "finished, so the verdict below is the whole run; reporting now"
				: "the game is quitting while Run() is STILL IN FLIGHT -- this harness cannot "
					+ "tell what asked it to quit (the rig's own Application.Quit, a WM_CLOSE "
					+ "from a script or a person closing the window all arrive here identically). "
					+ "Whatever the cause, the rig did not finish, so the verdict below covers "
					+ "only the checks that had already run; reporting now");
			Report();
		}

		private void OnDestroy()
		{
			Application.quitting -= ReportIfQuitFirst;
		}

		private IEnumerator Drive()
		{
			Prefix = "[" + ModTag + "] " + RigTag + ": ";

			// Published here rather than in OnSpawn, because this is the first line that can say
			// the rig is actually going rather than merely constructed. Never cleared: a rig that
			// has finished is still the rig this process ran, and that is what a crash screen
			// opening afterwards should say.
			live = this;

			Log("start -- " + GetType().Name);
			bool keepsGameUi = ShowsGameUi || RigRegistry.AnyFlag(KeepHudFlag);
			if (keepsGameUi)
			{
				// Before the boot wait, so the screens it spawns finish their own OnSpawn inside it.
				DemoRig.BuildVanillaHud(Prefix);
			}
			yield return Checkpoint(BootDelaySeconds);

			if (keepsGameUi)
			{
				// What is actually there to keep, logged before anything else touches it.
				DemoRig.LogHudCensus(Prefix);
				// THE HUD IS WHAT MAKES A RIG'S WORLD INSPECTABLE BY HAND. HideVanillaHud takes
				// ToolMenu with it, and ToolMenu owns the SELECT tool -- so on a rig that DOES
				// have a HUD, hiding it leaves a human unable to select anything and therefore
				// unable to reach any building's own user menu. Right for a recording, wrong when
				// the point of the run is to read a building's UI.
				//
				// Opt-in rather than the default because IsPresentation rigs film themselves, and
				// a stray HUD in a recording is a defect. Nothing else about the run changes.
				Log("vanilla HUD KEPT (" + (ShowsGameUi ? "this rig's ShowsGameUi" : KeepHudFlag)
					+ ") so the world can be used by hand");
			}
			else if (IsPresentation)
			{
				// After the boot wait, not before: these reach for screens the game builds over
				// the first few frames of the scene, and a search that runs too early finds
				// nothing and silently does nothing.
				DemoRig.HideVanillaHud();
				DemoRig.PushNotificationsDown(NotificationPushdownPixels);
				Log("presentation mode: vanilla HUD hidden, notifications pushed down "
					+ NotificationPushdownPixels.ToString("F0") + " px");
			}

			// THE TEST BED MUST BE A DECLARED CHOICE. A rig that names neither a canvas nor
			// UsesColonySave has not decided where it runs; it has simply inherited whatever the
			// harness does when asked nothing, and for years that was "load a 234-cycle colony".
			// See UsesColonySave for the three separate defects that produced.
			if (BlueprintFile == null && !UsesColonySave)
			{
				Require(false,
					"this rig declares a test bed: either override BlueprintFile with a canvas "
					+ "(the default for new rigs) or override UsesColonySave to true if it really "
					+ "does need a populated colony save");
				Checkpoint();
				if (!reported)
				{
					Report();
				}
				yield break;
			}

			// THE CANVAS CHECK, BEFORE ANY RIG CODE RUNS. See BlueprintFile for why this is a
			// precondition rather than something each rig remembers to assert: a rig whose arming
			// did not happen measures a real colony while believing it is on its own canvas, and
			// every number it then prints is of the wrong world.
			if (BlueprintFile != null)
			{
                if (!Require(BlueprintWorld.Active != null,
                    "the blueprint world was built from " + BlueprintFile
                        + " -- a null here means the game loaded a save instead and every "
                        + "measurement below would be of somebody else's world"))
				{
					Checkpoint();
					if (!reported)
					{
						Report();
					}
					yield break;
				}

				// A JACKET DECLARED IN THE BLUEPRINT IS STILL A JACKET. `jacketedTestVolume` is
				// otherwise only set by the two runtime calls (JacketTestVolume /
				// JacketTemplateBlock), so a rig that moved its jacket into its canvas -- where it
				// is strictly better, because it exists before the first sim tick rather than
				// being painted after the rig has started -- got told off at the end of every run
				// for not calling a method it had correctly stopped needing.
				//
				// A warning that fires on the rigs doing it RIGHT is worse than no warning, because
				// the next person to see it on a rig that really is unjacketed will have learned to
				// ignore it.
				if (BlueprintWorld.Active != null && BlueprintWorld.Active.jacket != null)
				{
					jacketedTestVolume = true;
				}

				// AND THE BUILDINGS, before any rig code runs. BlueprintWorld builds the CANVAS --
				// cells, fills, border, camera -- and nothing else; the buildings are stamped
				// separately through Klei's own TemplateLoader, which is asynchronous across real
				// sim ticks rather than a call that returns when it is done.
				//
				// This lived in each rig privately, in the same ~30 lines five times over, and
				// the sixth rig to be migrated simply did not know to write them: it named a
				// blueprint with 45 buildings in it, built the canvas, and then censused a world
				// containing none of them. It reported 0 EnergyConsumers and stopped -- honestly,
				// but only because that particular probe happened to check. A rig that had merely
				// measured the air would have reported a clean, wrong number.
				if (BuildsBlueprintItself)
				{
					// A rig that stamps its own blueprint opts out of the base's stamp.
					Log("this rig stamps " + BlueprintFile + " itself (BuildsBlueprintItself); "
						+ "the base is not doing it.");
				}
				else
				{
					yield return PlaceBlueprintBuildings();
				}
				if (blueprintBuildFailed)
				{
					Checkpoint();
					if (!reported)
					{
						Report();
					}
					yield break;
				}
			}

			// A yield break inside Run() lands here, which is why Report() is outside it: a rig
			// that bails on a precondition still has to say so.
			yield return Run();
			// The rig's own body is over -- set BEFORE anything below can throw, because its only
			// consumer is the quit path, and a quit that lands during the warnings below is still
			// a quit that arrived after a complete run. See Report()'s interrupted-run branch.
			runCompleted = true;

			if (IsPresentation && !revealedTestArea)
			{
				// Not a Fail: the run's measurements are all still valid, and turning a
				// presentation defect into a failed assertion would misreport what went wrong.
				// Loud, though, because the symptom is a video of a black screen and the cause is
				// four characters of missing call.
				Log("WARNING -- this rig presents on screen and never called RevealTestArea, so "
					+ "it was filmed against whatever fog of war the save happened to have.");
			}

			if (StagesATestVolume && !jacketedTestVolume)
			{
				// Same shape of warning as the reveal one above, and the same reasoning for why
				// it is not a Fail: an unjacketed rig still measures something, it just measures
				// it against the whole asteroid. Opt-in rather than automatic because the base
				// cannot tell a rig that stages a volume from one that reads a live colony, and
				// jacketing a live colony would be actively wrong.
				Log("WARNING -- this rig stages its own test volume and never called "
					+ "JacketTestVolume, so the map is free to conduct into it.");
			}

			Checkpoint();
			if (!reported)
			{
				Report();
			}
		}

		// ------------------------------------------------------------------ reporting

		/// <summary>
		/// Emits the summary and the machine-readable verdict.
		///
		/// TWO LINES, ON PURPOSE. The human line is the summary -- per-mechanism lines have
		/// already been printed, and this totals them with the elapsed simulated time, so a
		/// partial failure stays legible ("12 PASS / 1 FAIL, and the 1 is the room temperature"
		/// is a very different report from "the demo failed"). The RIG-VERDICT line is for a
		/// runner, which otherwise has nothing to gate on but the game's exit code and a grep for
		/// exceptions -- a rig could fail every assertion it had and the run would still report
		/// success.
		///
		/// THE VERDICT LINE IS ALSO WHAT MAKES "RUN IT TWICE" CHEAP, and a single green run is not
		/// proof of a reproducible one. Two runs' verdict lines either match or they do not, and comparing them
		/// is a diff rather than a reading.
		/// </summary>
		private void Report()
		{
			// Once per run, whichever path gets here first: Drive's own, a rig that loops
			// forever, or the game quitting underneath both.
			reported = true;

			RigOutcome outcome;
			string why;

			if (inconclusiveReason != null)
			{
				outcome = RigOutcome.Inconclusive;
				why = inconclusiveReason;
			}
			else if (!ClockAdvanced && !VerdictSurvivesAStoppedClock)
			{
				// Reaching here means every dismiss-and-unpause in Checkpoint failed to get the
				// sim moving. Reporting PASS would be worse than reporting nothing: it would be
				// the paused-game misreading, recorded as a result.
				outcome = RigOutcome.Inconclusive;
				why = "the game clock did not advance between checkpoints, so nothing measured "
					+ "here is a measurement";
				Log("INCONCLUSIVE -- " + why);
			}
			else if (preconditionFailed)
			{
				outcome = RigOutcome.Fail;
				why = "a precondition failed, so the mechanism was never exercised";
			}
			else if (FailCount > 0)
			{
				outcome = RigOutcome.Fail;
				why = FailCount + " assertion(s) failed";
			}
			else if (quitting && !runCompleted)
			{
				// AN INTERRUPTED RUN IS NOT A PASSING RUN. A game closed while Run() is still
				// observing can have passed every precondition and run none of the checks that are
				// the rig's actual verdict; counting only what ran would publish PASS for a run
				// that established none of its conclusions.
				//
				// Inconclusive rather than Fail: nothing was observed to be wrong. The rig simply
				// does not know, which is a third thing and already has a name here.
				outcome = RigOutcome.Inconclusive;
				why = "the game quit while Run() was still in flight, so the rig never reached "
					+ "its own conclusions -- the " + PassCount + " check(s) below are only what "
					+ "had already run";
				Log("INCONCLUSIVE -- " + why);
			}
			else if (PassCount == 0)
			{
				// A rig that asserted nothing is not a passing rig. Without this, deleting every
				// Check() in a file turns it green.
				outcome = RigOutcome.Inconclusive;
				why = "the rig made no assertions at all";
				Log("INCONCLUSIVE -- " + why);
			}
			else
			{
				outcome = RigOutcome.Pass;
				why = "all assertions passed";
			}

			Log(string.Format("SUMMARY: {0} PASS / {1} FAIL over {2:F1} s of simulated time -- {3}",
				PassCount, FailCount, ElapsedSimSeconds, why));

			// Close the narration too, when there is one. AirLoopDemo and PhaseChangeLoopDemo
			// each carried their own Report() that did exactly this, in the same two lines, and
			// no other narrated rig did it at all -- so a run that ended left its last phase and
			// its markers on screen. The narrator counts PASS and FAIL by scraping the log lines
			// (DemoNarrator.OnLogMessage), so it is already in step with the numbers above by
			// construction; nothing here has to tell it. Skipped while quitting: the screen is
			// going away, and an exception from it here would cost the verdict line below.
			if (DemoNarrator.IsActive && !quitting)
			{
				DemoNarrator.End(string.Format("{0} checks passed, {1} failed -- {2}",
					PassCount, FailCount, outcome.ToString().ToUpperInvariant()));
				DemoNarrator.ClearMarkers();
			}

			// The run's own cost, reported unconditionally. See the frame-time meter's fields
			// for why a rig measures this at all: it is the only way a claim about speed --
			// including this project's own claim that the blueprint canvas is cheaper than a
			// 234-cycle colony -- becomes checkable rather than remembered.
			float meanFps = meteredWallSeconds > 0f ? framesTotal / meteredWallSeconds : 0f;
			Log(string.Format(
				"PERF: {0:F1} fps mean over {1:F1} s of metered wall clock, slowest window "
				+ "{2:F1} fps, worst single frame {3:F0} ms",
				meanFps, meteredWallSeconds,
				slowestWindowFps == float.MaxValue ? 0f : slowestWindowFps,
				worstFrameSeconds * 1000f));

			// Machine-readable, one line, stable field order. Grep target: "RIG-VERDICT".
			// fps and worstms were appended rather than inserted: a runner that already parses
			// this line by field name keeps working, and one that split on position still finds
			// the first five fields where they were.
			Debug.Log(string.Format(
				"RIG-VERDICT rig={0} outcome={1} pass={2} fail={3} simsec={4:F1} fps={5:F1} "
				+ "worstms={6:F0}",
				RigTag, outcome.ToString().ToUpperInvariant(), PassCount, FailCount,
				ElapsedSimSeconds, meanFps, worstFrameSeconds * 1000f));
		}
	}
}
