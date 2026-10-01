using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
    /// <summary>
    /// oni-mod-compat decision 0001 part 4: one framework-owned append point on
    /// Klei's real hover card, so a third-party tooltip mod (Better Info Cards, Thermal
    /// Tooltips, ...) contends with exactly one prefix on <c>HoverTextDrawer.EndDrawing</c>
    /// instead of one per mod that wants a line in it. Before this, Mod 1 owned that prefix
    /// directly (see git history, <c>HoverPatch.cs</c>) -- fine for one mod, but every future
    /// flagship mod (or third-party mod using this API) that also wanted a hover-card line
    /// would have needed its own competing patch on the same method, each one guessing at
    /// ordering and style against the others.
    ///
    /// WHY A PREFIX AND NOT A POSTFIX ON <c>EndDrawing</c>.
    /// <c>SelectToolHoverTextCard.UpdateHoverElements</c> owns the whole card -- it calls
    /// <c>HoverTextScreen.Instance.BeginDrawing()</c>, draws about twenty <c>BeginShadowBar</c>/
    /// <c>EndShadowBar</c> blocks, and calls <c>EndDrawing()</c> itself as its last statement.
    /// <c>EndDrawing</c>'s own body starts by finalizing four widget pools (shadowBars/
    /// iconWidgets/textWidgets/selectBorders); a Prefix runs before any of that, while the
    /// drawer is still writable. Whichever tool's hover card is active this frame has already
    /// made all of its own <c>BeginShadowBar</c>/<c>DrawText</c>/<c>EndShadowBar</c> calls into
    /// that same drawer by the time <c>EndDrawing</c> runs, so appending sections here just adds
    /// more lines to whatever is already on screen -- additive, not a replacement.
    ///
    /// WHY <c>Priority.Low</c>. So this always drains sections LAST among any other mod's own
    /// prefix on the same method -- registered sections stack below whatever a tooltip mod
    /// already drew this frame, never contesting first position. Costs nothing: draining is
    /// purely additive, so where it lands in the prefix chain changes only where it draws
    /// relative to everyone else, never what it draws.
    ///
    /// WHY THE DRAWER-IDENTITY GUARD. This patches the TYPE, so it fires for any
    /// <c>HoverTextDrawer</c> instance that calls <c>EndDrawing</c>, not just the one real
    /// players see (<c>HoverTextScreen.Instance.drawer</c>). Bailing on any other instance means
    /// a registered section is never asked to draw into a drawer it was never written against.
    /// </summary>
    public static class HoverCard
    {
        private static bool installed;

        /// <summary>
        /// Test-only override for which cell every registered section describes. -1 (the
        /// default) means "use the cell under the real mouse cursor," which is the only
        /// behaviour a player ever sees. See <see cref="DebugForcedCells"/> for the narrated-run
        /// counterpart.
        /// </summary>
        public static int DebugForcedCell = -1;

        /// <summary>
        /// Narrated-run counterpart to <see cref="DebugForcedCell"/>: the candidate cells a run
        /// wants the card to describe, in preference order. Wired from
        /// <see cref="DemoNarrator.PinnedHoverCellsHook"/>.
        ///
        /// A LIST rather than a single cell because a pinned tile is not stable across a run
        /// that deliberately destroys pipe (found live, Mod 1's liquid-freeze step): when the
        /// pinned tile was the one that ruptured, the card silently showed nothing for the whole
        /// step. Resolved at draw time, every frame, to the first candidate at least one
        /// registered section actually has something to say about (see <see cref="ResolveCell"/>),
        /// falling back to the first merely-valid candidate if none match.
        /// </summary>
        public static int[] DebugForcedCells;

        private sealed class Section
        {
            public string Owner = "";
            public Func<int, bool> AppliesTo = null!;
            public Action<HoverTextDrawer, int> Draw = null!;
            public int Order;
        }

        private sealed class Unregisterer : IDisposable
        {
            private Section section;
            public Unregisterer(Section section) { this.section = section; }
            public void Dispose()
            {
                if (section == null) return;
                lock (sections) { sections.Remove(section); }
                section = null!;
            }
        }

        private static readonly List<Section> sections = new List<Section>();

        /// <summary>
        /// Registers one hover-card section. <paramref name="appliesTo"/> decides, for a given
        /// cell, whether this section has anything to draw -- it gates both real rendering (a
        /// cell nothing applies to costs one delegate call, not a full <paramref name="draw"/>)
        /// and pinned-cell resolution during a narrated run (see <see cref="ResolveCell"/>).
        /// <paramref name="draw"/> does the actual <c>BeginShadowBar</c>/<c>DrawText</c>/
        /// <c>EndShadowBar</c> work; exceptions from either delegate are caught per-section so
        /// one section's bug degrades to "no extra line" for that section, never for the whole
        /// card or the other sections sharing it.
        ///
        /// Lower <paramref name="order"/> draws first (ties broken by registration order).
        /// Returns an <see cref="IDisposable"/> that unregisters the section on <c>Dispose</c>.
        /// </summary>
        public static IDisposable AddSection(string owner, Func<int, bool> appliesTo,
            Action<HoverTextDrawer, int> draw, int order = 0)
        {
            if (string.IsNullOrEmpty(owner))
            {
                throw new ArgumentException("owner must be non-empty", "owner");
            }
            if (appliesTo == null) throw new ArgumentNullException("appliesTo");
            if (draw == null) throw new ArgumentNullException("draw");

            Section section = new Section
            {
                Owner = owner,
                AppliesTo = appliesTo,
                Draw = draw,
                Order = order,
            };
            lock (sections)
            {
                int i = 0;
                while (i < sections.Count && sections[i].Order <= order) i++;
                sections.Insert(i, section);
            }
            return new Unregisterer(section);
        }

        // Klei's own hover-card style objects, borrowed the same way Mod 1's original
        // HoverTextDrawer_EndDrawing did: TextStyleSetting instances aren't reachable statically
        // (HoverTextConfiguration's Styles_Title/Styles_BodyText are per-prefab-instance, not
        // static), so this finds the live SelectToolHoverTextCard component once (a scene
        // singleton in practice) and exposes its style fields, the same ones Klei's own hover
        // cards draw with.
        private static SelectToolHoverTextCard styleSource;

        private static bool TryResolveStyleSource()
        {
            if (styleSource != null) return true;
            styleSource = UnityEngine.Object.FindFirstObjectByType<SelectToolHoverTextCard>();
            if (styleSource == null) return false;

            // iconDash/iconWarning are [NonSerialized] on SelectToolHoverTextCard and only
            // filled in by ConfigureHoverScreen. Vanilla's own UpdateHoverElements opens with
            // exactly this guard ("if (iconWarning == null) ConfigureHoverScreen()"), because the
            // card can be handed out before it has ever drawn.
            if (styleSource.iconDash == null && HoverTextScreen.Instance != null)
            {
                styleSource.ConfigureHoverScreen();
            }
            return true;
        }

        /// <summary>Klei's own title style, or null if the style source hasn't resolved yet.</summary>
        public static TextStyleSetting TitleStyle =>
            TryResolveStyleSource() ? styleSource.Styles_Title.Standard : null;

        /// <summary>Klei's own body-text style, or null if the style source hasn't resolved yet.</summary>
        public static TextStyleSetting BodyStyle =>
            TryResolveStyleSource() ? styleSource.Styles_BodyText.Standard : null;

        /// <summary>Klei's own dash/bullet icon, or null if the style source hasn't resolved yet.</summary>
        public static Sprite DashIcon =>
            TryResolveStyleSource() ? styleSource.iconDash : null;

        /// <summary>
        /// The cell every section is asked about this frame. Mirrors Mod 1's original
        /// <c>TryResolvePinnedCell</c> exactly, generalized: instead of a hardcoded list of
        /// "does this cell have a readout" checks (which named Mod-1-only component types the
        /// framework must not depend on), it asks the SAME <c>appliesTo</c> predicates sections
        /// already register, so a pinned run resolves to a cell some registered section actually
        /// describes.
        /// </summary>
        private static int ResolveCell()
        {
            if (DebugForcedCell >= 0 && Grid.IsValidCell(DebugForcedCell))
            {
                return DebugForcedCell;
            }

            int[] candidates = DebugForcedCells;
            if (candidates != null)
            {
                int firstValid = -1;
                Section[] snapshot;
                lock (sections) snapshot = sections.ToArray();

                foreach (int candidate in candidates)
                {
                    if (!Grid.IsValidCell(candidate)) continue;
                    if (firstValid < 0) firstValid = candidate;

                    foreach (Section section in snapshot)
                    {
                        bool applies;
                        try { applies = section.AppliesTo(candidate); }
                        catch { applies = false; }
                        if (applies) return candidate;
                    }
                }
                if (firstValid >= 0) return firstValid;
            }

            Camera main = Camera.main;
            if (main == null) return Grid.InvalidCell;
            Vector3 mouseWorld = main.ScreenToWorldPoint(KInputManager.GetMousePos());
            return Grid.PosToCell(mouseWorld);
        }

        private static void EndDrawingPrefix(HoverTextDrawer __instance)
        {
            try
            {
                // Same guard reasoning as the class doc comment: only the one real drawer
                // players see, never some other HoverTextDrawer instance this framework was
                // never written against.
                if (HoverTextScreen.Instance == null || __instance != HoverTextScreen.Instance.drawer)
                {
                    return;
                }

                int cell = ResolveCell();
                if (!Grid.IsValidCell(cell)) return;

                Section[] snapshot;
                lock (sections) snapshot = sections.ToArray();

                foreach (Section section in snapshot)
                {
                    try
                    {
                        if (section.AppliesTo(cell))
                        {
                            section.Draw(__instance, cell);
                        }
                    }
                    catch
                    {
                        // This runs every frame for every tool. One section's mistake must cost
                        // that section's line, never the rest of the card or vanilla's own.
                    }
                }
            }
            catch
            {
                // Same rule, one level up: a mistake resolving the cell itself must still leave
                // vanilla's own drawing untouched.
            }
        }

        /// <summary>Whether <see cref="Install"/> has run.</summary>
        public static bool IsInstalled => installed;

        /// <summary>
        /// Patches <c>HoverTextDrawer.EndDrawing</c> with the section-draining prefix. Idempotent
        /// -- a second call with the same or a different Harmony instance is a no-op, so several
        /// mods can each ask for this API without double-patching.
        /// </summary>
        /// <param name="harmony">The calling mod's Harmony instance, normally the one handed to
        /// <c>UserMod2.OnLoad</c>.</param>
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

            MethodInfo endDrawing = AccessTools.Method(typeof(HoverTextDrawer), "EndDrawing");
            if (endDrawing == null)
            {
                throw new InvalidOperationException(
                    "HoverCard.Install could not find HoverTextDrawer.EndDrawing(). The game "
                    + "build has moved and every registered section would silently never draw, "
                    + "so this throws instead. Re-check the member against the current game assembly "
                    + "before shipping.");
            }

            harmony.Patch(endDrawing,
                new HarmonyMethod(AccessTools.Method(typeof(HoverCard), "EndDrawingPrefix"))
                {
                    priority = Priority.Low,
                });

            installed = true;
        }
    }
}
