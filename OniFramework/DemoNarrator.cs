using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
    /// <summary>
    /// The on-screen narration layer for this mod's harness runs: what is being demonstrated,
    /// which step it is on, what the simulation is reporting right now, which cells matter, and
    /// whether each step passed.
    ///
    /// WHY THIS EXISTS. Every harness in this mod writes its real findings to the Player log
    /// (<c>Debug.Log</c>), which is the correct place for a ground-truth assertion and a useless
    /// place for a viewer. A recording of a run without this layer shows a mostly-static ONI map
    /// with a pipe on it: nothing on screen says what the pipe is for, that its pressure just
    /// crossed a burst rating, or that the step passed. The request this file answers is
    /// explicit -- someone watching the replay with no commentary should be able to tell what is
    /// happening and why. So the narration is drawn INTO the frame rather than kept alongside it
    /// in a log file.
    ///
    /// WHY IMGUI (<c>OnGUI</c>) AND NOT A REAL UI PREFAB. Two reasons, both load-bearing:
    ///   1. a frame recorder (a mod supplies one, and reports its state here through
    ///      <see cref="RecorderStatusHook"/>) captures frames with
    ///      <c>ScreenCapture.CaptureScreenshotAsTexture</c> after <c>WaitForEndOfFrame</c>, which
    ///      reads the finished backbuffer. IMGUI has already been composited into it by then, so
    ///      everything drawn here is burned into the video with no extra work.
    ///   2. The flagship thermodynamics mod's overlays are IMGUI for the same reason, and one
    ///      UI technology for all of them is simpler to keep consistent.
    ///
    /// WHY THE EVENT LOG IS FED BY A LOG HOOK RATHER THAN BY EDITING EVERY DEMO. The demos
    /// already emit exactly the lines a viewer wants ("PHASE 4: PASS ...", "FAIL -- no suitable
    /// anchor"), dozens of call sites deep. Intercepting them picks all of those up with zero
    /// edits and, more importantly, cannot drift out of sync with what the harness actually
    /// concluded -- an event log that is separately maintained is an event log that eventually
    /// lies. Only the framing a log line genuinely does not contain (the run's title, the
    /// plain-English "what this step proves", which cells to point at) is pushed explicitly by
    /// the demos.
    ///
    /// AND WHY THAT HOOK IS A HARMONY PATCH, NOT <c>Application.logMessageReceived</c>. Unity's
    /// callback captures nothing at all here, because of a shadowed type:
    /// Assembly-CSharp-firstpass declares its own <c>public static class Debug</c> in
    /// the GLOBAL namespace, and a global-namespace type outranks a <c>using UnityEngine;</c>
    /// import, so every <c>Debug.Log</c> in this mod (and in the game) binds to Klei's class, not
    /// <c>UnityEngine.Debug</c>. Klei's implementation is
    /// <c>Console.WriteLine(TimeStamp() + ...)</c> -- it never touches UnityEngine.Debug at all,
    /// so Unity's log callback is never raised for any of it. Patching the method the calls
    /// actually reach is the only hook that sees them. See
    /// <see cref="Mod1DemoNarratorLogHook"/>.
    ///
    /// SAFE WHEN INACTIVE. All state is static and every entry point is a no-op-if-unstarted, so
    /// a demo can call <see cref="Note"/> or <see cref="Stat"/> unconditionally. The renderer
    /// spawns itself on the first <see cref="Begin"/>; nothing is drawn for a normal game session
    /// that never runs a harness.
    /// </summary>
    public static class DemoNarrator
    {
        /// <summary>
        /// What a frame recorder reports to the narration overlay's REC badge.
        /// </summary>
        public struct RecorderStatus
        {
            public bool IsRecording;

            public int FramesWritten;

            public int TargetFps;
        }

        /// <summary>
        /// Supplies the REC badge's contents. Left null, the overlay simply reads "live run".
        ///
        /// This exists because the narration overlay moved into the framework so that BOTH
        /// flagship mods can narrate a scenario, while frame capture stayed in Mod 1 where it was
        /// written. A framework type must not reach into a mod, so the mod pushes its status in
        /// instead. Same pattern, and the same reason, as PipeMatterFacade.MirrorHook.
        /// </summary>
        public static System.Func<RecorderStatus> RecorderStatusHook;

        /// <summary>
        /// Receives the cells <see cref="PinHoverCard"/> wants the game's hover readout forced
        /// onto, in priority order: the first one that still has something to say wins at draw
        /// time.
        ///
        /// Forcing the card requires a patch on `HoverTextDrawer.EndDrawing`, which lives in the
        /// mod that owns the readout rather than in the framework. Left null, PinHoverCard still
        /// parks the camera and the cursor; only the forced card is skipped. Same pattern, and the
        /// same reason, as <see cref="RecorderStatusHook"/>.
        /// </summary>
        public static System.Action<int[]> PinnedHoverCellsHook;

        /// <summary>
        /// Marker every rig log line carries, from <c>RigHarness.Prefix</c>.
        ///
        /// WAS <c>"[Mod1ThermoFluid]"</c>, which made this class silently Mod-1-only: every line
        /// Mod 2 logged failed the gate and was dropped, so the on-screen tally for a Mod 2
        /// scenario was always 0/0. PollutantMixtureDemo had been compensating by incrementing
        /// <see cref="PassCount"/> by hand -- a second source of truth for a number this class
        /// deliberately derives from ONE, and one that went unnoticed for as long as it was the
        /// only Mod 2 rig. Matching the bracketed tag generically is the actual fix.
        /// </summary>
        private const string ModLogPrefix = "] ";

        private const int MaxEventLines = 14;

        internal enum EventKind
        {
            Info,
            Pass,
            Fail,
            Skip,
            Note,
        }

        internal struct EventLine
        {
            public string Text;
            public EventKind Kind;
            public float RealtimeSeconds;
        }

        internal struct Marker
        {
            public int[] Cells;
            public string Label;
            public Color Color;

            /// <summary>Place the label UNDER the group instead of above it.</summary>
            public bool LabelBelow;

            /// <summary>
            /// THE TILE THE STEP IS ABOUT, drawn so it wins against everything around it.
            ///
            /// An emphasised marker gets a double outline at twice the thickness -- a target box
            /// rather than another cell border -- and it claims its label plate BEFORE any
            /// ordinary marker, so the anti-overlap rule can never be what drops it.
            ///
            /// Both halves exist because of a measured failure rather than a preference. On
            /// PIPESTRESS the one tile whose stress the whole step is about was marked correctly
            /// every phase, and neither half of that marking reached the screen: its outline was
            /// the same 2 px as the twelve-tile run it sits INSIDE, so it read as one more cell of
            /// the run, and its label plate -- centred over a single tile, directly under the
            /// run's own plate -- collided and was dropped by the rule that stops banners stacking
            /// into a wall. The step said "Critical / Overpressure" in the side panel and the map
            /// showed a uniform row of blue boxes.
            /// </summary>
            public bool Emphasis;
        }

        // All rendering state lives here rather than on the MonoBehaviour so a demo can push into
        // it before (or without) the renderer existing. Guarded by `gate` because the log hook
        // below sits on a method the whole game calls, including from Klei's own background
        // threads, while the renderer reads this state from OnGUI on the main one.
        private static readonly object gate = new object();
        private static readonly List<EventLine> events = new List<EventLine>();
        private static readonly List<KeyValuePair<string, string>> stats = new List<KeyValuePair<string, string>>();
        private static readonly List<Marker> markers = new List<Marker>();

        /// <summary>One tile's own readout: short text drawn inside the tile itself.</summary>
        internal struct TileReadout
        {
            public int Cell;
            public string Text;
            public Color Color;
        }

        private static readonly List<TileReadout> tileReadouts = new List<TileReadout>();

        /// <summary>One tile's temperature, for the scoped heat map.</summary>
        internal struct TileHeat
        {
            public int Cell;
            public float TemperatureK;
        }

        private static readonly List<TileHeat> tileHeat = new List<TileHeat>();

        private static float heatMinK;

        private static float heatMaxK;

        private static string heatLabel = string.Empty;

        private static string hintText = string.Empty;

        private static string briefingTitle = string.Empty;

        private static readonly List<string> briefingLines = new List<string>();

        private static float briefingUntilRealtime;
        private static readonly List<KeyValuePair<string, Color>> legend = new List<KeyValuePair<string, Color>>();

        public static string Title = string.Empty;
        public static string Subtitle = string.Empty;
        public static string PhaseLabel = string.Empty;
        public static string PhaseExplanation = string.Empty;
        public static int PhaseIndex;
        public static int PhaseCount;
        public static int PassCount;
        public static int FailCount;
        public static float StartRealtime;

        /// <summary>
        /// While true, <c>SelectToolHoverTextCard_UpdateHoverElements</c> replaces vanilla's
        /// hover card with an empty one, leaving this mod's own readout as the only shadow bar
        /// on screen. Set by <see cref="PinHoverCard"/> and never on its own, so a session that
        /// has not deliberately pinned the card keeps vanilla's tooltip untouched.
        ///
        /// Named <c>Demo...</c> rather than the plain <c>SuppressVanillaHoverCard</c> it started
        /// as (oni-mod-compat decision 0001 part 3): a static bool with a name this
        /// generic reads, out of context, like a real gameplay toggle. It is not -- the only
        /// caller of <see cref="PinHoverCard"/> is a <c>--mod1-*</c> capture/screenshot rig -- and
        /// a name that says so is worth more than the four extra characters.
        /// </summary>
        public static bool DemoSuppressVanillaHoverCard;

        // The comparison panel. Two strings and a caveat rather than a structured type: this is
        // presentation, and the only thing the renderer needs to know is which side is which.
        private static string compareVanilla;
        private static string compareMod;
        private static string compareNote;

        /// <summary>
        /// The comparison panel's own heading, or null for <see cref="DefaultCompareHeader"/>.
        ///
        /// Settable because one fixed heading ("THE SAME PIPE, TWO SIMULATIONS") is right for a
        /// pipe comparison and wrong for, say, a duplicant breathing a room. A caption that names
        /// the wrong thing is worse than none, because the viewer believes it.
        /// </summary>
        private static string compareHeader;
        private static bool cursorParked;

        /// <summary>
        /// Used when a caller does not name the subject of its comparison. Deliberately generic
        /// rather than guessing: the panel's claim is that the two columns describe ONE thing,
        /// and it can make that claim without knowing what the thing is.
        /// </summary>
        internal const string DefaultCompareHeader = "THE SAME SCENARIO, TWO SIMULATIONS";

        private static bool started;

        public static bool IsActive
        {
            get { return started; }
        }

        /// <summary>
        /// Names the run and turns the narration on. Idempotent: a second call re-titles the run
        /// (and resets the tally) rather than stacking a second renderer.
        ///
        /// THE TALLY IS SEEDED FROM THE RIG, not started at zero. A rig calls
        /// this from inside its own body, and by then the harness has already run the canvas
        /// check, the blueprint check and the rig's own build assertions -- none of which were
        /// ever offered to <see cref="OnLogMessage"/>, because the log hook is gated on the
        /// narration being live. Started at zero, the "CHECKS SO FAR" panel would report only a
        /// fraction of the work, missing exactly the assertions that preceded this call.
        /// </summary>
        public static void Begin(string title, string subtitle)
        {
            lock (gate)
            {
                Title = title ?? string.Empty;
                Subtitle = subtitle ?? string.Empty;
                PhaseLabel = string.Empty;
                PhaseExplanation = string.Empty;
                PhaseIndex = 0;
                PhaseCount = 0;
                RigHarness.CurrentAssertionCounts(out int seedPass, out int seedFail);
                PassCount = seedPass;
                FailCount = seedFail;
                events.Clear();
                stats.Clear();
                markers.Clear();
                legend.Clear();
                compareVanilla = null;
                compareMod = null;
                compareNote = null;
                compareHeader = null;
                StartRealtime = Time.realtimeSinceStartup;
                started = true;
            }

            EnsureRenderer();
        }

        /// <summary>
        /// Declares how many steps the run has, so the phase card can show "3 / 9" and a progress
        /// bar instead of an unanchored step name. Optional; a run that does not know its own
        /// length up front simply gets no denominator.
        /// </summary>
        public static void SetPhaseCount(int count)
        {
            lock (gate)
            {
                PhaseCount = Mathf.Max(0, count);
            }
        }

        /// <summary>
        /// The current step, plus the one sentence a viewer needs to understand why the step
        /// exists. <paramref name="explanation"/> is the part a log line cannot supply: the log
        /// says "PHASE 2 OVERPRESSURE: PASS", it does not say "pumping past the burst rating is
        /// supposed to raise a critical warning".
        /// </summary>
        public static void Phase(string label, string explanation)
        {
            lock (gate)
            {
                if (!started)
                {
                    return;
                }
                PhaseIndex++;
                PhaseLabel = label ?? string.Empty;
                PhaseExplanation = explanation ?? string.Empty;
                // NOT cleared. A phase pushes its readings once, at the end of its hold, so
                // clearing here left the panel showing a single line for most of every step --
                // visible in the first recorded run. Keeping the last known values means the
                // panel always says something, and Stat overwrites by key anyway.
            }

            Push("▶ " + label, EventKind.Note);
        }

        /// <summary>
        /// A live key/value readout for the current phase (pressure, temperature, mass...).
        /// Replaces any previous value for the same key, so a demo can call this every hold step
        /// and the panel simply tracks the newest reading rather than growing. Values persist
        /// across phases until overwritten.
        /// </summary>
        public static void Stat(string key, string value)
        {
            lock (gate)
            {
                if (!started || string.IsNullOrEmpty(key))
                {
                    return;
                }
                for (int i = 0; i < stats.Count; i++)
                {
                    if (stats[i].Key == key)
                    {
                        stats[i] = new KeyValuePair<string, string>(key, value);
                        return;
                    }
                }
                stats.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        /// <summary>Adds an explicit line to the on-screen event log.</summary>
        public static void Note(string text)
        {
            Push(text, EventKind.Note);
        }

        /// <summary>
        /// Points at the cells a step is actually about, drawn as an outline on the map with a
        /// label attached. This is the half of "obvious without commentary" that no text panel
        /// can do: without it a viewer has no way to know WHICH of the tiles on screen is the
        /// pipe under test.
        /// </summary>
        public static void MarkCells(IEnumerable<int> cells, string label, Color color,
            bool labelBelow = false, bool emphasis = false)
        {
            if (cells == null)
            {
                return;
            }

            var list = new List<int>();
            foreach (int cell in cells)
            {
                if (Grid.IsValidCell(cell))
                {
                    list.Add(cell);
                }
            }
            if (list.Count == 0)
            {
                return;
            }

            lock (gate)
            {
                if (!started)
                {
                    return;
                }
                markers.Add(new Marker
                {
                    Cells = list.ToArray(),
                    Label = label ?? string.Empty,
                    Color = color,
                    LabelBelow = labelBelow,
                    Emphasis = emphasis,
                });
            }
        }

        /// <summary>Single-cell convenience form of <see cref="MarkCells"/>.</summary>
        public static void MarkCell(int cell, string label, Color color,
            bool labelBelow = false, bool emphasis = false)
        {
            MarkCells(new[] { cell }, label, color, labelBelow, emphasis);
        }

        /// <summary>
        /// Points the camera at a group of cells, so the thing the narration is talking about is
        /// actually on screen.
        ///
        /// Needed because these harnesses build their rigs relative to a save's own anchor and
        /// then never move the view: the first recorded run framed the colony's telepad while the
        /// pipe under test sat off to one side, which makes every word of the narration a claim
        /// about something the viewer cannot see. Zoom is the orthographic half-height in cells,
        /// matching CameraController.SnapTo's own parameter.
        /// </summary>
        public static void FocusCamera(IEnumerable<int> cells, float zoom)
        {
            if (cells == null || CameraController.Instance == null)
            {
                return;
            }

            double sumX = 0.0;
            double sumY = 0.0;
            int count = 0;
            foreach (int cell in cells)
            {
                if (!Grid.IsValidCell(cell))
                {
                    continue;
                }
                Grid.CellToXY(cell, out int x, out int y);
                sumX += x;
                sumY += y;
                count++;
            }
            if (count == 0)
            {
                return;
            }

            int centreCell = Grid.XYToCell((int)(sumX / count), (int)(sumY / count));
            if (!Grid.IsValidCell(centreCell))
            {
                return;
            }
            CameraController.Instance.SnapTo(Grid.CellToPos(centreCell), zoom);
        }

        /// <summary>
        /// The side-by-side that is the actual pitch of this mod: what VANILLA reports about a
        /// thing, next to what the expanded simulation reports about the same thing.
        ///
        /// Exists because the two used to be shown by stacking Klei's own hover card above ours,
        /// which had three problems: the ambient-element box in between was pure noise, the card
        /// drifted with the mouse, and the two readings look like they contradict each other
        /// (vanilla reports ONE conduit tile, this mod reports the whole network) with nothing on
        /// screen to say why. <paramref name="note"/> is where that difference gets stated
        /// instead of left for the viewer to trip over.
        /// </summary>
        /// <param name="header">
        /// The panel's heading. Optional, and optional on purpose: naming the subject is the
        /// caller's business (only the rig knows whether it is comparing a pipe, a room or a
        /// power bus), but a rig that does not care must not be forced to invent one. Pass a
        /// complete heading, e.g. "THE SAME ROOM, TWO SIMULATIONS"; null takes
        /// <see cref="DefaultCompareHeader"/>.
        /// </param>
        public static void Compare(string vanilla, string mod, string note, string header = null)
        {
            lock (gate)
            {
                if (!started)
                {
                    return;
                }
                compareVanilla = vanilla;
                compareMod = mod;
                compareNote = note;
                compareHeader = header;
            }
        }

        /// <summary>
        /// Points this mod's hover readout at a specific cell and suppresses vanilla's own hover
        /// card for the rest of the run, so exactly one card is on screen and it always describes
        /// the thing being narrated.
        ///
        /// Vanilla's card cannot be trimmed instead:
        /// <c>SelectToolHoverTextCard.UpdateHoverElements</c> owns the entire card -- it calls
        /// <c>BeginDrawing()</c>, draws about twenty <c>BeginShadowBar</c>/<c>EndShadowBar</c>
        /// blocks, and calls <c>EndDrawing()</c> itself at the end. This mod's readout is a
        /// Prefix on THAT <c>EndDrawing</c>, so simply skipping vanilla's method would take our
        /// card down with it, and suppressing one individual box would mean reaching into the
        /// middle of a 650-line method. Replacing the whole card with a bare
        /// <c>BeginDrawing()/EndDrawing()</c> pair keeps the hook point and drops the content.
        ///
        /// The cursor is parked once, because <c>HoverTextScreen.BeginDrawing</c> anchors the
        /// card at <c>KInputManager.GetMousePos()</c>: pinning only the CELL would still leave
        /// the card wherever the mouse happened to be at launch, including on top of the panels
        /// above.
        /// </summary>
        public static void PinHoverCard(IEnumerable<int> cells)
        {
            if (cells == null)
            {
                return;
            }

            var valid = new List<int>();
            foreach (int cell in cells)
            {
                if (Grid.IsValidCell(cell))
                {
                    valid.Add(cell);
                }
            }
            if (valid.Count == 0)
            {
                return;
            }

            // A LIST, not one cell. Found live: pinning to a run's first tile showed no card at
            // all through the whole liquid-freeze step, because that step deliberately ruptures a
            // segment and the tile that got destroyed was the pinned one. The readout resolves
            // the first tile that still has something to say, at draw time.
            if (PinnedHoverCellsHook != null)
            {
                PinnedHoverCellsHook(valid.ToArray());
            }
            DemoSuppressVanillaHoverCard = true;

            if (cursorParked)
            {
                return;
            }
            cursorParked = true;
            try
            {
                // Desktop metrics, not Screen.width/height: SetCursorPos works in desktop
                // coordinates, and the two are not the same number on this machine (see
                // TooltipScreenshotDemo.cs, which hit exactly this).
                int w = GetSystemMetrics(0);
                int h = GetSystemMetrics(1);
                if (w > 0 && h > 0)
                {
                    // Left of centre, in the band between the legend and the run log. The
                    // camera frames the rig in the MIDDLE of the screen, so a cursor near the
                    // centre puts the card straight on top of the thing it is describing --
                    // which is exactly what the first pinned run did. The card grows right and
                    // down from its anchor, so this keeps it in open space.
                    SetCursorPos((int)(w * 0.06f), (int)(h * 0.40f));
                }
            }
            catch (Exception e)
            {
                // Non-fatal: an unparked cursor gives a card in an awkward place, not a broken
                // run.
                Debug.Log("[Mod1ThermoFluid] narrator: could not park the cursor: " + e.Message);
            }
        }

        /// <summary>Single-cell form of <see cref="PinHoverCard(IEnumerable{int})"/>.</summary>
        public static void PinHoverCard(int cell)
        {
            PinHoverCard(new[] { cell });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        /// <summary>Drops every world marker. Called by a demo when it moves on to a new rig.</summary>
        public static void ClearMarkers()
        {
            lock (gate)
            {
                markers.Clear();
            }
        }

        /// <summary>
        /// Declares what a colour means, so the outlines above are self-explaining. Replaces the
        /// entry for an existing label rather than duplicating it.
        /// </summary>
        public static void Legend(string label, Color color)
        {
            lock (gate)
            {
                if (!started || string.IsNullOrEmpty(label))
                {
                    return;
                }
                for (int i = 0; i < legend.Count; i++)
                {
                    if (legend[i].Key == label)
                    {
                        legend[i] = new KeyValuePair<string, Color>(label, color);
                        return;
                    }
                }
                legend.Add(new KeyValuePair<string, Color>(label, color));
            }
        }

        /// <summary>
        /// Ends the run: the banner switches to a final verdict so the last frames of a recording
        /// say how it went rather than freezing mid-phase.
        /// </summary>
        public static void End(string summary)
        {
            lock (gate)
            {
                if (!started)
                {
                    return;
                }
                PhaseLabel = "RUN COMPLETE";
                PhaseExplanation = summary ?? string.Empty;
            }
            Push("■ " + (summary ?? "run complete"), EventKind.Note);
        }

        /// <summary>
        /// The zero-edit half of the event log. Every line this mod logs is offered here; only
        /// the ones carrying a verdict or an explicit narration marker are shown, because a
        /// harness like PipeStressDemo emits several diagnostic lines per phase and putting all
        /// of them on screen would bury the one line that says whether the step passed.
        ///
        /// The PASS/FAIL tally is counted from the same source for the same reason the log is:
        /// it can then only ever disagree with the log by the log being wrong.
        ///
        /// THE ONE SUBTLETY is not what this method counts but when it starts counting. Nothing reaches here until the narration is
        /// live, and a rig turns the narration on from inside its own body, after the harness has
        /// already asserted the canvas, the blueprint and the rig's own preconditions, and those
        /// assertions are never offered here. <see cref="Begin"/> seeds the tally from
        /// <c>RigHarness.CurrentAssertionCounts</c>, so the count still comes from one place and
        /// does not omit its own opening.
        /// </summary>
        internal static void OnLogMessage(string condition)
        {
            if (!started || string.IsNullOrEmpty(condition)
                || condition.IndexOf(ModLogPrefix, StringComparison.Ordinal) < 0)
            {
                return;
            }

            // MATCH THE ASSERTION SHAPE, not the bare word. RigHarness writes exactly
            // "PASS -- ", "FAIL -- " and "INCONCLUSIVE -- " and nothing else does, so this counts
            // assertions and only assertions.
            //
            // Searching for the bare word used to be enough and stopped being enough the moment
            // the harness gained a summary line: "SUMMARY: 12 PASS / 1 FAIL over 31.4 s" contains
            // both words, and a substring search would have counted the summary as a thirteenth
            // pass and pushed it into the event log as though it were one. A tally that counts
            // its own total is worse than no tally.
            EventKind kind;
            if (condition.IndexOf("PASS -- ", StringComparison.Ordinal) >= 0)
            {
                kind = EventKind.Pass;
            }
            else if (condition.IndexOf("FAIL -- ", StringComparison.Ordinal) >= 0)
            {
                kind = EventKind.Fail;
            }
            else if (condition.IndexOf("INCONCLUSIVE -- ", StringComparison.Ordinal) >= 0
                || condition.IndexOf("SKIPPED", StringComparison.Ordinal) >= 0)
            {
                // Shown, never counted. An inconclusive run has not passed and has not failed,
                // and folding it into either total is the exact misreading the verdict exists to
                // prevent.
                kind = EventKind.Skip;
            }
            else
            {
                return;
            }

            lock (gate)
            {
                if (kind == EventKind.Pass)
                {
                    PassCount++;
                }
                else if (kind == EventKind.Fail)
                {
                    FailCount++;
                }
            }

            Push(Condense(condition), kind);
        }

        /// <summary>
        /// Trims a harness log line down to something readable at a glance: drops the mod prefix
        /// and the parenthesised assertion detail, which is the log's job, not the overlay's.
        ///
        /// IT DOES NOT CAP THE LENGTH. The event panel wraps and carries a height budget of its
        /// own, dropping the OLDEST entries when it runs out (see
        /// <c>NarratorOverlay.DrawEventLog</c>), so one long line cannot push it off screen, and a
        /// cut here would truncate text the panel could have wrapped. Long lines are the norm: a
        /// rig's assertions run to a median of about 140 characters. A rig's PASS text IS the measurement -- the claim, the numbers behind it and the
        /// reason it is the right claim. A film that stops it at 110 characters shows the reader
        /// the half that says nothing.
        /// </summary>
        private static string Condense(string line)
        {
            int prefixEnd = line.IndexOf(ModLogPrefix, StringComparison.Ordinal);
            if (prefixEnd >= 0)
            {
                line = line.Substring(prefixEnd + ModLogPrefix.Length).TrimStart();
            }

            int newline = line.IndexOf('\n');
            if (newline >= 0)
            {
                line = line.Substring(0, newline);
            }

            // Trim the parenthesised assertion detail, which is the log's job -- but only the
            // one that follows the verdict. The first recorded run showed
            // "2 OVERPRESSURE (BY MASS): PASS" rendered as "2 OVERPRESSURE", because a blind
            // first-parenthesis cut ate a parenthesis that was part of the step's own name.
            int verdict = line.IndexOf("PASS -- ", StringComparison.Ordinal);
            if (verdict < 0)
            {
                verdict = line.IndexOf("FAIL -- ", StringComparison.Ordinal);
            }
            if (verdict < 0)
            {
                verdict = line.IndexOf("INCONCLUSIVE -- ", StringComparison.Ordinal);
            }
            if (verdict >= 0)
            {
                int paren = line.IndexOf(" (", verdict, StringComparison.Ordinal);
                if (paren >= 0)
                {
                    line = line.Substring(0, paren);
                }
            }

            return line;
        }

        private static void Push(string text, EventKind kind)
        {
            lock (gate)
            {
                if (!started)
                {
                    return;
                }
                events.Add(new EventLine
                {
                    Text = text ?? string.Empty,
                    Kind = kind,
                    RealtimeSeconds = Time.realtimeSinceStartup - StartRealtime,
                });
                while (events.Count > MaxEventLines)
                {
                    events.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Per-tile readouts: short text drawn INSIDE each tile, centred, with a plate only as
        /// wide as the text.
        ///
        /// Deliberately not <see cref="MarkCells"/> with one cell per call. That was tried and
        /// looked terrible: every marker draws a full outline plus a callout plate above its
        /// group, so a hundred single-cell markers produced a hundred callouts stacked over each
        /// other and spilling far outside the rig (user-reported: "it looks really bad"). Markers
        /// are for a handful of GROUPS; this is for "what is in every tile", which is a different
        /// job and needs a different renderer.
        ///
        /// Replaces the whole set each call, so a scenario can rebuild it every sample and the
        /// numbers track the simulation. Readouts hide themselves automatically when the camera is
        /// zoomed out far enough that the text would not fit in a tile.
        /// </summary>
        public static void SetTileReadouts(IEnumerable<KeyValuePair<int, string>> entries,
            Color color)
        {
            lock (gate)
            {
                if (!started)
                {
                    return;
                }
                tileReadouts.RemoveAll(r => r.Color == color);
                if (entries == null)
                {
                    return;
                }
                foreach (KeyValuePair<int, string> entry in entries)
                {
                    if (Grid.IsValidCell(entry.Key) && !string.IsNullOrEmpty(entry.Value))
                    {
                        tileReadouts.Add(new TileReadout
                        {
                            Cell = entry.Key,
                            Text = entry.Value,
                            Color = color,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// A temperature heat map drawn over ONLY the cells given, rather than the whole screen.
        ///
        /// ONI's own temperature overlay is all-or-nothing: it recolours the entire map, which
        /// buries a two-room test bed in a wash of colour describing the rest of the asteroid.
        /// This tints exactly the cells a scenario hands it, so the rooms under test are the only
        /// thing coloured and the map around them stays readable.
        ///
        /// The scale is explicit rather than inferred per frame: a range that rescaled itself
        /// every sample would make a room that cooled by half a kelvin look identical to one that
        /// cooled by ten. Pass the range the scenario actually cares about and leave it fixed.
        /// </summary>
        public static void SetTileHeatmap(IEnumerable<KeyValuePair<int, float>> cellTemperatures,
            float minK, float maxK, string label)
        {
            lock (gate)
            {
                tileHeat.Clear();
                heatMinK = minK;
                heatMaxK = maxK;
                heatLabel = label ?? string.Empty;
                if (cellTemperatures == null || !started)
                {
                    return;
                }
                foreach (KeyValuePair<int, float> entry in cellTemperatures)
                {
                    if (Grid.IsValidCell(entry.Key))
                    {
                        tileHeat.Add(new TileHeat
                        {
                            Cell = entry.Key,
                            TemperatureK = entry.Value,
                        });
                    }
                }
            }
        }

        /// <summary>Drops the scoped heat map.</summary>
        public static void ClearTileHeatmap()
        {
            lock (gate)
            {
                tileHeat.Clear();
            }
        }

        /// <summary>Drops every per-tile readout.</summary>
        public static void ClearTileReadouts()
        {
            lock (gate)
            {
                tileReadouts.Clear();
            }
        }

        /// <summary>
        /// A standing instruction to the person watching, drawn along the bottom of the screen.
        ///
        /// Exists because these runs go past faster than the panels can be read: the whole point
        /// of the readouts is lost if nobody is told they can pause on them. Pass null or an empty
        /// string to clear it.
        /// </summary>
        public static void Hint(string text)
        {
            lock (gate)
            {
                hintText = text ?? string.Empty;
            }
        }

        /// <summary>
        /// A briefing card, shown centred for <paramref name="seconds"/> before a scenario starts
        /// doing anything: what this test is, what it demonstrates, and how it goes beyond what
        /// vanilla ONI can do.
        ///
        /// Show it AFTER the camera has been aimed and AFTER recording has started, so the
        /// recording opens on an explanation of what is about to happen rather than on a rig
        /// already halfway through its first phase.
        /// </summary>
        public static void Briefing(string title, IEnumerable<string> lines, float seconds)
        {
            lock (gate)
            {
                briefingTitle = title ?? string.Empty;
                briefingLines.Clear();
                if (lines != null)
                {
                    briefingLines.AddRange(lines);
                }
                briefingUntilRealtime = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
            }
        }

        /// <summary>
        /// Starts frame capture, if whatever owns the recorder wired itself up through
        /// <see cref="StartRecordingHook"/>. A no-op otherwise, so a scenario can always call it.
        ///
        /// The ORDER matters and is the reason this exists rather than each scenario poking a
        /// recorder directly: aim the camera, start recording, show the briefing, then run the
        /// test. Recording started before the camera move opens on the wrong part of the map.
        /// </summary>
        public static void StartRecording()
        {
            System.Action hook = StartRecordingHook;
            if (hook != null)
            {
                hook();
            }
        }

        /// <summary>Wired by whichever mod owns frame capture. See <see cref="StartRecording"/>.</summary>
        public static System.Action StartRecordingHook;

        /// <summary>
        /// Snapshots the extra display state for one OnGUI pass, alongside
        /// <see cref="Snapshot"/>. Separate rather than folded into that method's parameter list,
        /// which is already long and is called from verified code.
        /// </summary>
        internal static void SnapshotExtras(List<TileReadout> readoutsOut,
            List<string> briefingLinesOut, out string hintOut, out string briefingTitleOut,
            out float briefingUntilOut)
        {
            readoutsOut.Clear();
            briefingLinesOut.Clear();
            lock (gate)
            {
                readoutsOut.AddRange(tileReadouts);
                briefingLinesOut.AddRange(briefingLines);
                hintOut = hintText;
                briefingTitleOut = briefingTitle;
                briefingUntilOut = briefingUntilRealtime;
            }
        }

        /// <summary>Snapshots the scoped heat map for one OnGUI pass.</summary>
        internal static void SnapshotHeat(List<TileHeat> heatOut, out float minK, out float maxK,
            out string label)
        {
            heatOut.Clear();
            lock (gate)
            {
                heatOut.AddRange(tileHeat);
                minK = heatMinK;
                maxK = heatMaxK;
                label = heatLabel;
            }
        }

        /// <summary>
        /// A temperature written in all three scales: "297.29 K (24.1 C / 75.4 F)".
        ///
        /// The simulation works in kelvin and every threshold in this project is stated in kelvin,
        /// so kelvin stays the primary figure -- but nobody reads 297.29 K as room temperature at
        /// a glance, which is the point of a live demonstration. Both, always, rather than picking
        /// one and making the reader convert.
        /// </summary>
        public static string KAndC(float kelvin)
        {
            float celsius = kelvin - 273.15f;
            return string.Format("{0:F2} K ({1:F1} C / {2:F1} F)", kelvin, celsius,
                celsius * 9f / 5f + 32f);
        }

        /// <summary>
        /// Blue through white to red across the given range. Clamped at both ends, so a cell
        /// outside the scale reads as "at least this cold" or "at least this hot" rather than
        /// wrapping around to the wrong colour.
        /// </summary>
        internal static Color HeatColor(float temperatureK, float minK, float maxK)
        {
            if (maxK - minK < 0.0001f)
            {
                return new Color(1f, 1f, 1f, 0.45f);
            }

            float t = Mathf.Clamp01((temperatureK - minK) / (maxK - minK));
            Color c = t < 0.5f
                ? Color.Lerp(new Color(0.25f, 0.45f, 1f), new Color(0.96f, 0.96f, 0.96f), t * 2f)
                : Color.Lerp(new Color(0.96f, 0.96f, 0.96f), new Color(1f, 0.32f, 0.22f),
                    (t - 0.5f) * 2f);
            c.a = 0.45f;
            return c;
        }

        /// <summary>
        /// Snapshots the shared state for one OnGUI pass. Copying under the lock and then drawing
        /// outside it keeps the renderer from holding the lock across Unity calls, and keeps a
        /// log line arriving mid-draw from resizing a list the draw loop is walking.
        /// </summary>
        internal static void Snapshot(List<EventLine> eventsOut,
            List<KeyValuePair<string, string>> statsOut, List<Marker> markersOut,
            List<KeyValuePair<string, Color>> legendOut,
            out string vanillaOut, out string modOut, out string noteOut, out string headerOut)
        {
            eventsOut.Clear();
            statsOut.Clear();
            markersOut.Clear();
            legendOut.Clear();
            lock (gate)
            {
                eventsOut.AddRange(events);
                statsOut.AddRange(stats);
                markersOut.AddRange(markers);
                legendOut.AddRange(legend);
                vanillaOut = compareVanilla;
                modOut = compareMod;
                noteOut = compareNote;
                headerOut = compareHeader;
            }
        }

        private static void EnsureRenderer()
        {
            if (UnityEngine.Object.FindFirstObjectByType<Mod1DemoNarratorRenderer>() != null)
            {
                return;
            }
            var go = new GameObject("Mod1ThermoFluidNarrator");
            go.AddComponent<Mod1DemoNarratorRenderer>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }
    }

    /// <summary>
    /// Feeds this mod's own log lines into <see cref="DemoNarrator"/>'s on-screen run log.
    ///
    /// Patches the <c>Debug</c> that the calls actually reach: Assembly-CSharp-firstpass declares
    /// a <c>public static class Debug</c> in the GLOBAL namespace whose <c>Log</c> is
    /// <c>Console.WriteLine(TimeStamp() + ...)</c>, so it never raises
    /// <c>Application.logMessageReceived</c> and Unity's callback sees none of it. That is why
    /// this is a Harmony patch and not an event subscription -- see DemoNarrator's own class
    /// comment for how that was established.
    ///
    /// This sits on a method the entire game calls, so the gate is deliberately the cheapest
    /// possible one: a static bool that is false in any session with no narration running. The
    /// body is wrapped because a throw here would propagate into whatever was merely trying to
    /// write a log line.
    /// </summary>
    [HarmonyPatch(typeof(global::Debug), nameof(global::Debug.Log), new[] { typeof(object) })]
    internal static class Mod1DemoNarratorLogHook
    {
        private static void Prefix(object obj)
        {
            if (!DemoNarrator.IsActive || obj == null)
            {
                return;
            }

            try
            {
                DemoNarrator.OnLogMessage(obj as string ?? obj.ToString());
            }
            catch
            {
                // Never let the narration break logging. See class comment.
            }
        }
    }

    /// <summary>
    /// Draws whatever <see cref="DemoNarrator"/> currently holds. Spawned by
    /// <see cref="DemoNarrator.Begin"/>; there is no reason to add it by hand.
    ///
    /// Everything is sized off <c>Screen.height</c> rather than in fixed pixels: this mod's
    /// captures run at the dev machine's native resolution (2542x1351 as measured), where a
    /// 12 px label is unreadable in a scaled-down video, while the same layout has to stay sane
    /// in a small window. One scale factor, applied everywhere.
    /// </summary>
    public class Mod1DemoNarratorRenderer : MonoBehaviour
    {
        private static Texture2D whiteTex;

        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle phaseStyle;
        private GUIStyle explainStyle;
        private GUIStyle bodyStyle;
        private GUIStyle smallStyle;
        private GUIStyle tallyStyle;
        private float builtForScale = -1f;

        private readonly List<DemoNarrator.EventLine> eventBuffer = new List<DemoNarrator.EventLine>();
        private readonly List<KeyValuePair<string, string>> statBuffer = new List<KeyValuePair<string, string>>();
        private readonly List<DemoNarrator.Marker> markerBuffer = new List<DemoNarrator.Marker>();

        /// <summary>
        /// <see cref="markerBuffer"/> re-ordered so emphasised markers claim their label plate
        /// first. A field rather than a local so the label pass allocates nothing per frame --
        /// OnGUI runs every frame, and per-frame garbage is what causes GC stutter.
        /// </summary>
        private readonly List<DemoNarrator.Marker> labelOrder = new List<DemoNarrator.Marker>();
        private readonly List<KeyValuePair<string, Color>> legendBuffer = new List<KeyValuePair<string, Color>>();
        private readonly List<Rect> placedPlates = new List<Rect>();

        private readonly List<DemoNarrator.TileReadout> tileReadoutBuffer =
            new List<DemoNarrator.TileReadout>();

        private readonly List<string> briefingBuffer = new List<string>();

        private readonly List<DemoNarrator.TileHeat> heatBuffer = new List<DemoNarrator.TileHeat>();

        private static readonly Color PanelColor = new Color(0.05f, 0.06f, 0.08f, 0.82f);
        private static readonly Color BannerColor = new Color(0.05f, 0.08f, 0.13f, 0.92f);
        private static readonly Color AccentColor = new Color(0.30f, 0.72f, 1.00f);
        private static readonly Color PassColor = new Color(0.42f, 0.90f, 0.45f);
        private static readonly Color FailColor = new Color(1.00f, 0.40f, 0.38f);
        private static readonly Color SkipColor = new Color(0.80f, 0.78f, 0.45f);
        private static readonly Color MutedColor = new Color(0.78f, 0.80f, 0.84f);

        private static Texture2D White
        {
            get
            {
                if (whiteTex == null)
                {
                    whiteTex = new Texture2D(1, 1);
                    whiteTex.SetPixel(0, 0, Color.white);
                    whiteTex.Apply();
                    whiteTex.hideFlags = HideFlags.HideAndDontSave;
                }
                return whiteTex;
            }
        }

        private float Scale
        {
            get { return Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f); }
        }

        private void EnsureStyles(float s)
        {
            if (titleStyle != null && Mathf.Approximately(builtForScale, s))
            {
                return;
            }
            builtForScale = s;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(26f * s),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };
            subtitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(16f * s),
                normal = { textColor = MutedColor },
                wordWrap = true,
            };
            phaseStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(21f * s),
                fontStyle = FontStyle.Bold,
                normal = { textColor = AccentColor },
            };
            explainStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(15f * s),
                normal = { textColor = MutedColor },
                wordWrap = true,
            };
            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(15f * s),
                normal = { textColor = Color.white },
            };
            smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13f * s),
                normal = { textColor = MutedColor },
            };
            tallyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(24f * s),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };
        }

        private static void Fill(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White);
            GUI.color = prev;
        }

        /// <summary>
        /// Four filled rects rather than a box style, because the outline has to sit ON the map
        /// (over whatever ONI is rendering) and has to be a specific colour per marker. A GUI.Box
        /// would bring its own skin background with it.
        /// </summary>
        private static void Outline(Rect r, Color c, float thickness)
        {
            Fill(new Rect(r.x, r.y, r.width, thickness), c);
            Fill(new Rect(r.x, r.yMax - thickness, r.width, thickness), c);
            Fill(new Rect(r.x, r.y, thickness, r.height), c);
            Fill(new Rect(r.xMax - thickness, r.y, thickness, r.height), c);
        }

        /// <summary>
        /// Text with a hard black drop shadow. The narration is drawn over an arbitrary game
        /// frame -- white text on a lit sandstone biome is genuinely unreadable, and this is the
        /// cheapest fix that works everywhere without a panel behind every single label.
        /// </summary>
        private static void Shadowed(Rect r, string text, GUIStyle style)
        {
            Color original = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, style);
            style.normal.textColor = original;
            GUI.Label(r, text, style);
        }

        private void OnGUI()
        {
            if (!DemoNarrator.IsActive)
            {
                return;
            }

            // Drawn last, over every other IMGUI layer this mod has (Overlay.cs's corner panel,
            // DebugGridOverlay.cs's per-cell grid). Lower depth means later/on top in IMGUI.
            GUI.depth = -1000;

            float s = Scale;
            EnsureStyles(s);
            DemoNarrator.Snapshot(eventBuffer, statBuffer, markerBuffer, legendBuffer,
                out string compareVanilla, out string compareMod, out string compareNote,
                out string compareHeader);

            DrawMarkers(s);

            DemoNarrator.SnapshotExtras(tileReadoutBuffer, briefingBuffer, out string hint,
                out string briefTitle, out float briefUntil);
            DrawTileHeatmap(s);
            DrawTileReadouts(s);
            float bannerHeight = DrawBanner(s);
            DrawPhaseCard(s, bannerHeight);
            float statsBottom = DrawStats(s, bannerHeight);
            DrawComparison(s, statsBottom, compareVanilla, compareMod, compareNote,
                compareHeader);
            DrawEventLog(s);
            DrawTally(s);

            // The hint and the briefing card are drawn LAST, over every panel this overlay draws.
            // They were drawn before the event log to begin with and the log covered the hint
            // (user-reported: "the pause text is behind the run log"). Anything addressed to the
            // person watching has to sit on top of everything addressed to the person reading the
            // results afterwards.
            DrawHint(s, hint);
            DrawBriefing(s, briefTitle, briefUntil);
        }

        private float DrawBanner(float s)
        {
            float height = 84f * s;
            float pad = 14f * s;
            Fill(new Rect(0f, 0f, Screen.width, height), BannerColor);
            Fill(new Rect(0f, height, Screen.width, 2f * s), AccentColor);

            float clockWidth = 340f * s;
            Shadowed(new Rect(pad, pad * 0.6f, Screen.width - clockWidth - pad * 2f, 34f * s),
                DemoNarrator.Title, titleStyle);
            Shadowed(new Rect(pad, pad * 0.6f + 32f * s, Screen.width - clockWidth - pad * 2f, 44f * s),
                DemoNarrator.Subtitle, subtitleStyle);

            // Elapsed clock plus a recording indicator, so a replay is obviously a live run and
            // not a still. Whatever owns frame capture is the authority on whether frames are
            // actually being written; asking it through RecorderStatusHook keeps the dot from
            // lying when narration runs without capture, and keeps this framework type from
            // depending on any one mod's recorder.
            float elapsed = Time.realtimeSinceStartup - DemoNarrator.StartRealtime;
            string clock = string.Format("{0:00}:{1:00}", Mathf.FloorToInt(elapsed / 60f),
                Mathf.FloorToInt(elapsed % 60f));
            var clockStyle = new GUIStyle(titleStyle) { alignment = TextAnchor.MiddleRight };
            Shadowed(new Rect(Screen.width - clockWidth - pad, pad * 0.6f, clockWidth, 34f * s),
                clock, clockStyle);

            DemoNarrator.RecorderStatus rec = DemoNarrator.RecorderStatusHook != null
                ? DemoNarrator.RecorderStatusHook()
                : default(DemoNarrator.RecorderStatus);
            string recLine = rec.IsRecording
                ? string.Format("● REC  {0} frames @ {1} fps", rec.FramesWritten, rec.TargetFps)
                : "live run";
            var recStyle = new GUIStyle(smallStyle)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = rec.IsRecording ? FailColor : MutedColor },
            };
            Shadowed(new Rect(Screen.width - clockWidth - pad, pad * 0.6f + 34f * s, clockWidth, 22f * s),
                recLine, recStyle);

            return height + 2f * s;
        }

        private void DrawPhaseCard(float s, float top)
        {
            if (string.IsNullOrEmpty(DemoNarrator.PhaseLabel))
            {
                return;
            }

            float width = 620f * s;
            float height = 96f * s;
            float pad = 12f * s;
            // 36, not 12: the game's build watermark sits just under the banner, and a card that
            // starts at 12 cuts its lower half off for the whole run -- unreadable in every
            // recorded video. The stamp is how a viewer tells which build a video shows.
            var rect = new Rect(14f * s, top + 36f * s, width, height);
            Fill(rect, PanelColor);
            Fill(new Rect(rect.x, rect.y, 4f * s, rect.height), AccentColor);

            string counter = DemoNarrator.PhaseCount > 0
                ? string.Format("STEP {0} / {1}", DemoNarrator.PhaseIndex, DemoNarrator.PhaseCount)
                : string.Format("STEP {0}", DemoNarrator.PhaseIndex);
            Shadowed(new Rect(rect.x + pad, rect.y + 6f * s, width - pad * 2f, 20f * s), counter, smallStyle);
            Shadowed(new Rect(rect.x + pad, rect.y + 24f * s, width - pad * 2f, 28f * s),
                DemoNarrator.PhaseLabel, phaseStyle);
            Shadowed(new Rect(rect.x + pad, rect.y + 52f * s, width - pad * 2f, 40f * s),
                DemoNarrator.PhaseExplanation, explainStyle);

            if (DemoNarrator.PhaseCount > 0)
            {
                float barY = rect.yMax + 6f * s;
                float barH = 6f * s;
                Fill(new Rect(rect.x, barY, width, barH), new Color(1f, 1f, 1f, 0.15f));
                float progress = Mathf.Clamp01((float)DemoNarrator.PhaseIndex / DemoNarrator.PhaseCount);
                Fill(new Rect(rect.x, barY, width * progress, barH), AccentColor);
            }

            DrawLegend(s, rect.yMax + 20f * s);
        }

        private void DrawLegend(float s, float top)
        {
            if (legendBuffer.Count == 0)
            {
                return;
            }

            float rowH = 24f * s;
            float width = 360f * s;
            var rect = new Rect(14f * s, top, width, rowH * legendBuffer.Count + 12f * s);
            Fill(rect, PanelColor);
            for (int i = 0; i < legendBuffer.Count; i++)
            {
                float y = rect.y + 6f * s + rowH * i;
                Fill(new Rect(rect.x + 10f * s, y + 5f * s, 14f * s, 10f * s), legendBuffer[i].Value);
                Shadowed(new Rect(rect.x + 32f * s, y, width - 42f * s, rowH), legendBuffer[i].Key, smallStyle);
            }
        }

        /// <summary>Returns the y the panel ended at, so the comparison below can stack under it.</summary>
        /// <summary>
        /// The live readout panel.
        ///
        /// Rows are measured, not assumed. The first version gave every row the same fixed
        /// height and drew the value with word wrap off, so any value longer than the column --
        /// "7.5% O2 / 92.5% other, 101.4 kPa total", for instance -- spilled out of its own row
        /// and printed straight through the label underneath it. Each row now takes the height
        /// its own wrapped value needs, and the panel is sized from the sum, so a long value
        /// pushes the rest of the panel down instead of overwriting it.
        /// </summary>
        private float DrawStats(float s, float top)
        {
            if (statBuffer.Count == 0)
            {
                return top + 12f * s;
            }

            float width = 430f * s;
            float minRowH = 26f * s;
            float labelWidth = width * 0.44f;
            float valueWidth = width - labelWidth - 28f * s;

            var valueStyle = new GUIStyle(bodyStyle)
            {
                alignment = TextAnchor.UpperRight,
                wordWrap = true,
            };
            var labelStyle = new GUIStyle(smallStyle) { alignment = TextAnchor.UpperLeft };

            // Measure first, draw second: the panel's own height depends on every row's height,
            // so nothing can be drawn until all of them are known.
            float[] rowHeights = new float[statBuffer.Count];
            float rowsHeight = 0f;
            for (int i = 0; i < statBuffer.Count; i++)
            {
                float valueHeight = valueStyle.CalcHeight(
                    new GUIContent(statBuffer[i].Value ?? string.Empty), valueWidth);
                rowHeights[i] = Mathf.Max(minRowH, valueHeight + 6f * s);
                rowsHeight += rowHeights[i];
            }

            var rect = new Rect(Screen.width - width - 14f * s, top + 12f * s, width,
                rowsHeight + 40f * s);
            Fill(rect, PanelColor);
            Fill(new Rect(rect.xMax - 4f * s, rect.y, 4f * s, rect.height), AccentColor);

            Shadowed(new Rect(rect.x + 12f * s, rect.y + 6f * s, width - 24f * s, 22f * s),
                "WHAT THE SIMULATION REPORTS", smallStyle);

            float y = rect.y + 30f * s;
            for (int i = 0; i < statBuffer.Count; i++)
            {
                Shadowed(new Rect(rect.x + 12f * s, y, labelWidth, rowHeights[i]),
                    statBuffer[i].Key, labelStyle);
                Shadowed(new Rect(rect.x + 12f * s + labelWidth, y, valueWidth, rowHeights[i]),
                    statBuffer[i].Value, valueStyle);
                y += rowHeights[i];
            }

            return rect.yMax;
        }

        /// <summary>
        /// "What vanilla sees" beside "what Mod 1 sees", for the same object.
        ///
        /// This replaced a stack of Klei's own hover cards. Two columns rather than two boxes on
        /// purpose: the point being made is a COMPARISON, and two separately-drawn tooltips make
        /// the viewer find the correspondence themselves. The note line underneath carries the
        /// caveat that made the old stacked version actively misleading.
        /// </summary>
        private void DrawComparison(float s, float top, string vanilla, string mod, string note,
            string header)
        {
            if (string.IsNullOrEmpty(vanilla) && string.IsNullOrEmpty(mod))
            {
                return;
            }

            float width = 430f * s;
            var columnStyle = new GUIStyle(bodyStyle) { wordWrap = true, fontSize = Mathf.RoundToInt(14f * s) };
            float innerWidth = width - 24f * s;

            float vanillaH = columnStyle.CalcHeight(new GUIContent(vanilla ?? string.Empty), innerWidth);
            float modH = columnStyle.CalcHeight(new GUIContent(mod ?? string.Empty), innerWidth);
            var noteStyle = new GUIStyle(smallStyle) { wordWrap = true };
            float noteH = string.IsNullOrEmpty(note)
                ? 0f
                : noteStyle.CalcHeight(new GUIContent(note), innerWidth) + 8f * s;

            float height = 30f * s + 22f * s + vanillaH + 12f * s + 22f * s + modH + noteH + 12f * s;
            var rect = new Rect(Screen.width - width - 14f * s, top + 12f * s, width, height);
            Fill(rect, PanelColor);
            Fill(new Rect(rect.xMax - 4f * s, rect.y, 4f * s, rect.height), AccentColor);

            float x = rect.x + 12f * s;
            float y = rect.y + 6f * s;
            Shadowed(new Rect(x, y, innerWidth, 22f * s),
                string.IsNullOrEmpty(header) ? DemoNarrator.DefaultCompareHeader : header, smallStyle);
            y += 24f * s;

            var vanillaLabel = new GUIStyle(smallStyle) { normal = { textColor = MutedColor } };
            Shadowed(new Rect(x, y, innerWidth, 22f * s), "VANILLA ONI SEES", vanillaLabel);
            y += 20f * s;
            var vanillaValue = new GUIStyle(columnStyle) { normal = { textColor = MutedColor } };
            Shadowed(new Rect(x, y, innerWidth, vanillaH), vanilla ?? string.Empty, vanillaValue);
            y += vanillaH + 12f * s;

            var modLabel = new GUIStyle(smallStyle) { normal = { textColor = AccentColor } };
            Shadowed(new Rect(x, y, innerWidth, 22f * s), "MOD 1 SEES", modLabel);
            y += 20f * s;
            Shadowed(new Rect(x, y, innerWidth, modH), mod ?? string.Empty, columnStyle);
            y += modH;

            if (!string.IsNullOrEmpty(note))
            {
                y += 8f * s;
                Shadowed(new Rect(x, y, innerWidth, noteH), note, noteStyle);
            }
        }

        private void DrawEventLog(float s)
        {
            if (eventBuffer.Count == 0)
            {
                return;
            }

            // 26, not 20. Found in the first recorded run: GUI.skin.label carries its own padding
            // on top of the font size, so a row pitch set to roughly the font size stacks each
            // line's descenders into the next line's caps. The pitch has to clear the style's
            // real line box, not the point size.
            float minRowH = 26f * s;
            float rowGap = 4f * s;
            // 30% of the screen, not 62%. A demonstration centres its camera on the build, and a
            // log reaching past the centre line covered the thing being demonstrated. Narrower
            // means more wrapping and so fewer entries in the same height budget; the newest
            // still show.
            float width = Mathf.Min(980f * s, Screen.width * 0.30f);
            float textWidth = width - 90f * s;

            // WRAPPED, NOT CLIPPED. A fixed single-line rect cuts any assertion longer than the
            // panel mid-sentence, and a rig's PASS text IS the measurement;
            // a log that truncates it is showing the reader the half that says nothing.
            var wrapStyle = new GUIStyle(bodyStyle) { wordWrap = true };
            var heights = new float[eventBuffer.Count];
            for (int i = 0; i < eventBuffer.Count; i++)
            {
                heights[i] = Mathf.Max(minRowH,
                    wrapStyle.CalcHeight(new GUIContent(eventBuffer[i].Text ?? string.Empty),
                        textWidth)) + rowGap;
            }

            // BOUND THE PANEL AND DROP THE OLDEST, rather than letting it grow up the screen.
            // Wrapping turns a 14-entry log into as many as 30 lines; the newest entries are the
            // ones being narrated, so when the budget runs out it is the top of the list that
            // goes, exactly as it already does when MaxEventLines is exceeded.
            float budget = Screen.height * 0.42f - 32f * s;
            int first = 0;
            float used = 0f;
            for (int i = eventBuffer.Count - 1; i >= 0; i--)
            {
                if (used + heights[i] > budget)
                {
                    first = i + 1;
                    break;
                }
                used += heights[i];
            }

            // THE NEWEST ENTRY IS DRAWN EVEN IF IT ALONE OVERRUNS THE BUDGET. Without this the
            // loop above answers "nothing fits" by setting `first` past the end, and a panel that
            // drops the very line being narrated is worse than a panel that is too tall. It
            // cannot happen at today's text -- AIRLOOP's longest assertion is 517 characters,
            // which wraps to roughly a third of the budget at 1080p -- but "cannot happen today"
            // is a property of the strings, not of this code, and the strings are written
            // somewhere else.
            if (first > eventBuffer.Count - 1)
            {
                first = eventBuffer.Count - 1;
                used = heights[first];
            }

            float height = used + 32f * s;
            var rect = new Rect(14f * s, Screen.height - height - 14f * s, width, height);
            Fill(rect, PanelColor);

            Shadowed(new Rect(rect.x + 12f * s, rect.y + 5f * s, width - 24f * s, 20f * s),
                "RUN LOG", smallStyle);

            float y = rect.y + 26f * s;
            for (int i = first; i < eventBuffer.Count; i++)
            {
                DemoNarrator.EventLine e = eventBuffer[i];
                Color c;
                switch (e.Kind)
                {
                    case DemoNarrator.EventKind.Pass: c = PassColor; break;
                    case DemoNarrator.EventKind.Fail: c = FailColor; break;
                    case DemoNarrator.EventKind.Skip: c = SkipColor; break;
                    case DemoNarrator.EventKind.Note: c = AccentColor; break;
                    default: c = MutedColor; break;
                }

                // The stamp keeps a single-line box pinned to the TOP of a wrapped entry, so a
                // three-line assertion still reads as one timestamped event rather than as three.
                var stampStyle = new GUIStyle(smallStyle);
                Shadowed(new Rect(rect.x + 12f * s, y, 62f * s, minRowH),
                    string.Format("{0:00}:{1:00}", Mathf.FloorToInt(e.RealtimeSeconds / 60f),
                        Mathf.FloorToInt(e.RealtimeSeconds % 60f)), stampStyle);

                var lineStyle = new GUIStyle(wrapStyle) { normal = { textColor = c } };
                Shadowed(new Rect(rect.x + 78f * s, y, textWidth, heights[i] - rowGap),
                    e.Text, lineStyle);
                y += heights[i];
            }
        }

        private void DrawTally(float s)
        {
            if (DemoNarrator.PassCount == 0 && DemoNarrator.FailCount == 0)
            {
                return;
            }

            float width = 300f * s;
            float height = 76f * s;
            var rect = new Rect(Screen.width - width - 14f * s, Screen.height - height - 14f * s, width, height);
            Fill(rect, PanelColor);
            Fill(new Rect(rect.x, rect.y, width, 4f * s),
                DemoNarrator.FailCount > 0 ? FailColor : PassColor);

            Shadowed(new Rect(rect.x + 12f * s, rect.y + 8f * s, width - 24f * s, 20f * s),
                "CHECKS SO FAR", smallStyle);

            var passStyle = new GUIStyle(tallyStyle) { normal = { textColor = PassColor } };
            var failStyle = new GUIStyle(tallyStyle) { normal = { textColor = DemoNarrator.FailCount > 0 ? FailColor : MutedColor } };
            Shadowed(new Rect(rect.x, rect.y + 30f * s, width * 0.5f - 6f * s, 34f * s),
                DemoNarrator.PassCount + " pass", passStyle);
            Shadowed(new Rect(rect.x + width * 0.5f, rect.y + 30f * s, width * 0.5f - 12f * s, 34f * s),
                DemoNarrator.FailCount + " fail", failStyle);
        }

        /// <summary>
        /// The world-space half of the narration: an outline on every marked cell plus one label
        /// per marker group.
        ///
        /// Screen positions come from the camera's own conversion, with one pitfall:
        /// <c>WorldToScreenPoint</c> puts y=0 at the BOTTOM of
        /// the screen while IMGUI puts it at the TOP, so every y here is flipped. Cell size in
        /// pixels is measured from the camera rather than assumed, so the outline stays glued to
        /// the tile at any zoom.
        /// </summary>
        /// <summary>
        /// Fills the scoped cells with a temperature colour, under the per-tile readouts so the
        /// numbers stay legible on top of it, and draws the scale it is using so a colour can
        /// actually be read as a temperature.
        /// </summary>
        private void DrawTileHeatmap(float s)
        {
            DemoNarrator.SnapshotHeat(heatBuffer, out float minK, out float maxK,
                out string heatLabel);
            if (heatBuffer.Count == 0)
            {
                return;
            }

            Camera main = Camera.main;
            if (main == null)
            {
                return;
            }

            Vector3 originScreen = main.WorldToScreenPoint(Vector3.zero);
            Vector3 unitScreen = main.WorldToScreenPoint(
                new Vector3(Grid.CellSizeInMeters, Grid.CellSizeInMeters, 0f));
            float cellPx = Mathf.Abs(unitScreen.x - originScreen.x);
            if (cellPx < 2f)
            {
                return;
            }

            Color previous = GUI.color;
            foreach (DemoNarrator.TileHeat heat in heatBuffer)
            {
                if (!Grid.IsValidCell(heat.Cell))
                {
                    continue;
                }
                Vector3 bottomLeft = main.WorldToScreenPoint(Grid.CellToPos(heat.Cell));
                var tile = new Rect(bottomLeft.x, Screen.height - (bottomLeft.y + cellPx),
                    cellPx, cellPx);
                if (tile.xMax < 0f || tile.yMax < 0f || tile.xMin > Screen.width
                    || tile.yMin > Screen.height)
                {
                    continue;
                }
                GUI.color = DemoNarrator.HeatColor(heat.TemperatureK, minK, maxK);
                GUI.DrawTexture(tile, White);
            }
            GUI.color = previous;

            // The scale. A colour with no key is decoration, not a measurement.
            float barW = 380f * s;
            float barH = 14f * s;
            // Top centre: the scale belongs next to the thing it describes, and the bottom-right
            // corner is where the run log and the pause hint already live.
            var bar = new Rect((Screen.width - barW) * 0.5f, 108f * s, barW, barH);
            int steps = 48;
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)(steps - 1);
                GUI.color = DemoNarrator.HeatColor(Mathf.Lerp(minK, maxK, t), minK, maxK);
                GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, 0.95f);
                GUI.DrawTexture(new Rect(bar.x + bar.width * t, bar.y, bar.width / steps + 1f,
                    bar.height), White);
            }
            GUI.color = previous;

            var scaleStyle = new GUIStyle(smallStyle) { alignment = TextAnchor.UpperLeft };
            scaleStyle.normal.textColor = MutedColor;
            // Short form at the ends, not the full three-scale string: the long one is wide
            // enough that the two ends met in the middle and printed over each other.
            GUI.Label(new Rect(bar.x, bar.yMax + 2f, bar.width * 0.45f, 20f * s),
                string.Format("{0:F1} K / {1:F1} F", minK, (minK - 273.15f) * 9f / 5f + 32f),
                scaleStyle);
            var rightStyle = new GUIStyle(scaleStyle) { alignment = TextAnchor.UpperRight };
            GUI.Label(new Rect(bar.x + bar.width * 0.55f, bar.yMax + 2f, bar.width * 0.45f,
                20f * s),
                string.Format("{0:F1} K / {1:F1} F", maxK, (maxK - 273.15f) * 9f / 5f + 32f),
                rightStyle);
            if (!string.IsNullOrEmpty(heatLabel))
            {
                var labelStyle = new GUIStyle(scaleStyle) { alignment = TextAnchor.LowerRight };
                labelStyle.normal.textColor = AccentColor;
                GUI.Label(new Rect(bar.x, bar.y - 20f * s, bar.width, 18f * s), heatLabel,
                    labelStyle);
            }
        }

        /// <summary>
        /// Draws each tile's own readout inside that tile: centred, on a plate sized to the text,
        /// with no outline and no callout. Hides itself when a tile is too small on screen for the
        /// text to fit, which is what keeps a zoomed-out view from turning into the wall of
        /// overlapping labels this replaced.
        /// </summary>
        private void DrawTileReadouts(float s)
        {
            if (tileReadoutBuffer.Count == 0)
            {
                return;
            }

            Camera main = Camera.main;
            if (main == null)
            {
                return;
            }

            Vector3 originScreen = main.WorldToScreenPoint(Vector3.zero);
            Vector3 unitScreen = main.WorldToScreenPoint(
                new Vector3(Grid.CellSizeInMeters, Grid.CellSizeInMeters, 0f));
            float cellPx = Mathf.Abs(unitScreen.x - originScreen.x);

            // Below this a tile is narrower than the shortest useful readout, so drawing anything
            // would be noise rather than information.
            if (cellPx < 34f)
            {
                return;
            }

            var style = new GUIStyle(smallStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(cellPx * 0.20f), 8, 16),
                wordWrap = false,
            };

            foreach (DemoNarrator.TileReadout readout in tileReadoutBuffer)
            {
                if (!Grid.IsValidCell(readout.Cell))
                {
                    continue;
                }

                Vector3 bottomLeft = main.WorldToScreenPoint(Grid.CellToPos(readout.Cell));
                var tile = new Rect(bottomLeft.x, Screen.height - (bottomLeft.y + cellPx),
                    cellPx, cellPx);
                if (tile.xMax < 0f || tile.yMax < 0f || tile.xMin > Screen.width
                    || tile.yMin > Screen.height)
                {
                    continue;
                }

                // Rich text so a readout can colour its own lines to match the legend beside the
                // rig -- four unlabelled numbers in a box are only legible if the colour says
                // which is which, and there is no room in the box for labels.
                style.richText = true;
                GUIContent content = new GUIContent(readout.Text);
                Vector2 size = style.CalcSize(content);
                float plateW = Mathf.Min(tile.width - 2f, size.x + 6f);

                // A readout may be several lines -- a cell's whole composition is more than one
                // number -- so the plate is allowed to stand taller than its own tile rather than
                // clipping the lines that did not fit. Capped so it cannot swallow the rig.
                float plateH = Mathf.Min(tile.height * 1.9f, size.y + 4f);
                var plate = new Rect(tile.center.x - plateW * 0.5f, tile.center.y - plateH * 0.5f,
                    plateW, plateH);

                Color previous = GUI.color;
                GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.72f);
                GUI.DrawTexture(plate, White);
                GUI.color = previous;

                style.normal.textColor = readout.Color;
                GUI.Label(plate, content, style);
            }
        }

        /// <summary>
        /// A standing instruction along the bottom of the screen, telling the viewer they can
        /// pause and read the panels. Drawn last and low so it never covers the rig.
        /// </summary>
        private void DrawHint(float s, string hint)
        {
            if (string.IsNullOrEmpty(hint))
            {
                return;
            }

            var style = new GUIStyle(smallStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
            style.normal.textColor = AccentColor;

            float height = 30f * s;
            var plate = new Rect(Screen.width * 0.2f, Screen.height - height - 12f * s,
                Screen.width * 0.6f, height);

            Color previous = GUI.color;
            GUI.color = BannerColor;
            GUI.DrawTexture(plate, White);
            GUI.color = previous;
            GUI.Label(plate, hint, style);
        }

        /// <summary>
        /// The briefing card: what this scenario is and what it demonstrates, shown for a few
        /// seconds before the test starts. Disappears on its own once its deadline passes.
        /// </summary>
        private void DrawBriefing(float s, string title, float untilRealtime)
        {
            if (Time.realtimeSinceStartup > untilRealtime || briefingBuffer.Count == 0)
            {
                return;
            }

            float width = Mathf.Min(Screen.width * 0.62f, 900f * s);

            var bodyStyle = new GUIStyle(smallStyle)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = Mathf.RoundToInt(17f * s),
                wordWrap = true,
            };
            bodyStyle.normal.textColor = MutedColor;

            // MEASURED, NOT ASSUMED, and for the same reason the stat panel had to be: a fixed
            // 30-pixel line height is only correct while every briefing line fits on one line.
            // These lines are sentences, they wrap to two and three, and a fixed advance printed
            // each one straight through the one below it -- the whole card was unreadable.
            float innerWidth = width - 48f * s;
            float[] lineHeights = new float[briefingBuffer.Count];
            float bodyHeight = 0f;
            float lineGap = 8f * s;
            for (int i = 0; i < briefingBuffer.Count; i++)
            {
                lineHeights[i] = bodyStyle.CalcHeight(new GUIContent(briefingBuffer[i]),
                    innerWidth);
                bodyHeight += lineHeights[i] + lineGap;
            }

            float height = 76f * s + bodyHeight;
            var card = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.42f,
                width, height);

            Color previous = GUI.color;
            GUI.color = new Color(0.04f, 0.06f, 0.10f, 0.94f);
            GUI.DrawTexture(card, White);
            GUI.color = previous;
            Outline(card, AccentColor, Mathf.Max(2f, 2.5f * s));

            var titleStyle = new GUIStyle(tallyStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(26f * s),
            };
            titleStyle.normal.textColor = AccentColor;
            GUI.Label(new Rect(card.x, card.y + 12f * s, card.width, 34f * s), title, titleStyle);

            float y = card.y + 56f * s;
            for (int i = 0; i < briefingBuffer.Count; i++)
            {
                GUI.Label(new Rect(card.x + 24f * s, y, innerWidth, lineHeights[i]),
                    briefingBuffer[i], bodyStyle);
                y += lineHeights[i] + lineGap;
            }
        }

        private void DrawMarkers(float s)
        {
            if (markerBuffer.Count == 0)
            {
                return;
            }

            Camera main = Camera.main;
            if (main == null)
            {
                return;
            }

            // One cell's on-screen size, measured: project the cell's own corners rather than
            // guessing a pixels-per-metre constant that would break the moment the camera zooms.
            Vector3 originScreen = main.WorldToScreenPoint(Vector3.zero);
            Vector3 unitScreen = main.WorldToScreenPoint(new Vector3(Grid.CellSizeInMeters, Grid.CellSizeInMeters, 0f));
            float cellPx = Mathf.Abs(unitScreen.x - originScreen.x);
            if (cellPx < 1f)
            {
                return;
            }

            // Where a label plate has already been placed this frame. Two markers on the same
            // tiles -- the run under test and the one tile its warning names, which is the normal
            // case here -- otherwise put both plates at the same centroid and draw one on top of
            // the other, as the first recorded run did.
            placedPlates.Clear();

            // TWO PASSES: every outline first, then every label. One pass drew marker A's label
            // and then marker B's outlines straight over the top of it, so labels ended up behind
            // the cell grid of whichever marker came next (user-reported: "the text looks bad,
            // its behind the cell outlines"). Outlines are background; labels are what is being
            // read.
            // ORDINARY OUTLINES FIRST, EMPHASISED ONES OVER THE TOP. Within one pass the later
            // marker wins the overlapping pixels, and the emphasised tile is by definition inside
            // the group it is being distinguished from -- so drawing in declaration order left the
            // run's own border painted over the target box on every tile they share.
            foreach (DemoNarrator.Marker outlinePass in markerBuffer)
            {
                if (outlinePass.Emphasis)
                {
                    continue;
                }
                foreach (int cell in outlinePass.Cells)
                {
                    if (!Grid.IsValidCell(cell))
                    {
                        continue;
                    }
                    Vector3 cellBottomLeft = main.WorldToScreenPoint(Grid.CellToPos(cell));
                    Outline(new Rect(cellBottomLeft.x,
                        Screen.height - (cellBottomLeft.y + cellPx), cellPx, cellPx),
                        outlinePass.Color, Mathf.Max(1.5f, 2f * s));
                }
            }

            foreach (DemoNarrator.Marker outlinePass in markerBuffer)
            {
                if (!outlinePass.Emphasis)
                {
                    continue;
                }
                foreach (int cell in outlinePass.Cells)
                {
                    if (!Grid.IsValidCell(cell))
                    {
                        continue;
                    }
                    Vector3 cellBottomLeft = main.WorldToScreenPoint(Grid.CellToPos(cell));
                    var box = new Rect(cellBottomLeft.x,
                        Screen.height - (cellBottomLeft.y + cellPx), cellPx, cellPx);

                    // A RING JUST OUTSIDE THE TILE AND A HEAVY ONE ON IT. The outer ring is what
                    // separates this tile from an identically-coloured neighbour in the same run;
                    // the inner is what survives being drawn at a zoomed-out camera, where one
                    // cell can be under ten pixels across and a single hairline disappears.
                    float thick = Mathf.Max(3f, 4f * s);
                    float pad = Mathf.Max(2f, 3f * s);
                    Outline(new Rect(box.x - pad, box.y - pad,
                        box.width + pad * 2f, box.height + pad * 2f),
                        outlinePass.Color, Mathf.Max(1.5f, 2f * s));
                    Outline(box, outlinePass.Color, thick);
                }
            }

            // EMPHASISED LABELS CLAIM THEIR PLATE FIRST. `placedPlates` is first-come-first-served
            // and a clashing plate is dropped outright, so plate order IS priority order. In
            // declaration order the group marker is added first and wins, which is exactly
            // backwards: the group is the context, the emphasised tile is the point.
            labelOrder.Clear();
            foreach (DemoNarrator.Marker m in markerBuffer)
            {
                if (m.Emphasis)
                {
                    labelOrder.Add(m);
                }
            }
            foreach (DemoNarrator.Marker m in markerBuffer)
            {
                if (!m.Emphasis)
                {
                    labelOrder.Add(m);
                }
            }

            foreach (DemoNarrator.Marker marker in labelOrder)
            {
                float minX = float.MaxValue;
                float minY = float.MaxValue;
                float maxX = float.MinValue;
                float maxY = float.MinValue;
                bool any = false;

                foreach (int cell in marker.Cells)
                {
                    if (!Grid.IsValidCell(cell))
                    {
                        continue;
                    }

                    // Grid.CellToPos(cell) is the tile's BOTTOM-LEFT corner (x =
                    // CellSizeInMeters * col, y = CellSizeInMeters * row, no half-cell offset),
                    // so the tile spans one CellSizeInMeters up and to the right of it.
                    Vector3 bottomLeft = main.WorldToScreenPoint(Grid.CellToPos(cell));
                    float x = bottomLeft.x;
                    float yTop = Screen.height - (bottomLeft.y + cellPx);
                    var r = new Rect(x, yTop, cellPx, cellPx);

                    minX = Mathf.Min(minX, r.xMin);
                    minY = Mathf.Min(minY, r.yMin);
                    maxX = Mathf.Max(maxX, r.xMax);
                    maxY = Mathf.Max(maxY, r.yMax);
                    any = true;
                }

                if (!any || string.IsNullOrEmpty(marker.Label))
                {
                    continue;
                }

                // Label above the group, with its own plate so it stays readable over the map,
                // and a short leader so it is unambiguous which outline it names.
                var labelStyle = new GUIStyle(bodyStyle) { alignment = TextAnchor.MiddleCenter };
                Vector2 size = labelStyle.CalcSize(new GUIContent(marker.Label));
                float plateW = size.x + 18f * s;
                float plateH = size.y + 8f * s;
                float cx = (minX + maxX) * 0.5f;
                float plateY = marker.LabelBelow
                    ? maxY + 10f * s
                    : minY - plateH - 10f * s;
                var plate = new Rect(cx - plateW * 0.5f, plateY, plateW, plateH);

                // Nudge back inside the screen before testing for collisions, so a marker near an
                // edge competes for the same space everything else does rather than half-drawing
                // off the side.
                plate.x = Mathf.Clamp(plate.x, 8f * s, Screen.width - plate.width - 8f * s);

                // A COLLIDING BANNER IS DROPPED, NOT STACKED. This used to push a clashing plate
                // up until it found room, which is how a rig with a dozen markers ended up drawing
                // a wall of banners across the top of the screen with leader lines raking down
                // through the very thing they described: every label present, none of them
                // readable, and the rig hidden behind them.
                //
                // Skipping the clash means the banners that ARE shown sit on what they name, which
                // is the only placement that makes a callout worth having. The outline is drawn
                // either way, so nothing goes unmarked -- only unlabelled. How many labels fit is
                // then a real constraint on the scenario, which is the right place for it: if a
                // rig wants fifteen banners it should be asking for fewer.
                bool clashes = plate.yMin < 0f || plate.yMax > Screen.height;
                if (!clashes)
                {
                    foreach (Rect taken in placedPlates)
                    {
                        if (plate.Overlaps(taken))
                        {
                            clashes = true;
                            break;
                        }
                    }
                }
                if (clashes)
                {
                    continue;
                }
                placedPlates.Add(plate);

                Fill(plate, PanelColor);
                Fill(new Rect(plate.x, plate.yMax - 2f * s, plate.width, 2f * s), marker.Color);
                // Leader down to the outline it names. Its length is measured from the plate's
                // final position, since the anti-overlap pass above may have pushed it higher.
                Fill(new Rect(cx - 1f * s, plate.yMax, 2f * s, Mathf.Max(4f * s, minY - plate.yMax)),
                    marker.Color);
                Shadowed(plate, marker.Label, labelStyle);
            }
        }
    }
}
