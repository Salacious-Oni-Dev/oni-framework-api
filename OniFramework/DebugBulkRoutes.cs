using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace OniFramework
{
    /// <summary>
    /// The bulk half of <see cref="DebugInspectorServer"/>: whole published grid arrays served
    /// as raw binary, plus the two metadata routes a client needs to make sense of them.
    ///
    /// WHY THIS EXISTS. The per-cell routes (<c>/cell/{idx}</c>, <c>/pos/{x}/{y}</c>) answer a
    /// question about ONE cell and answer it in JSON, which is the right shape for a human at a
    /// terminal and the wrong shape for a tool that wants the whole world every frame. The
    /// a typical cluster is 636x404 = 256,944 cells; asking for it one JSON object at a time is
    /// not a slow version of the right thing, it is a different thing. A viewer that needs the
    /// frame, not a cell, needs a route that hands over the arrays themselves.
    ///
    /// WHY IT COSTS ALMOST NOTHING. The game does not own copies of these arrays -- it holds the
    /// sim's own pointers. <c>Sim.Start()</c> assigns <c>Grid.elementIdx</c>,
    /// <c>Grid.temperature</c>, <c>Grid.mass</c> and the rest straight out of the
    /// <c>GameDataUpdate</c> the SimDLL published, and does the same for the five property
    /// textures via <c>PropertyTextures.external*Tex</c>. So this route is a memcpy out of the
    /// sim's published buffers and a socket write. No SimDLL change was needed to add it, and
    /// none should be added later: if a future array is worth serving, it is worth serving the
    /// same way.
    ///
    /// THREADING. Same rule as every other route: the copy happens on the game's main thread
    /// inside <see cref="DebugInspectorServer.ProcessPending"/>, because that is the only place
    /// the sim is guaranteed not to be mid-write. Only the socket write happens on the
    /// connection thread, off a buffer the main thread already filled. The buffers are pooled
    /// (<see cref="Rent"/> / <see cref="Release"/>) rather than allocated per request: every
    /// surface together is 57 bytes per cell, so a full frame of the 636x404 cluster is
    /// 14,646,128 bytes, and handing the game's GC one of those per polled frame would make the
    /// debug tool the thing being debugged. Measured on that cluster: 20-50 ms per
    /// full-frame request over loopback, and 40 consecutive full frames -- 560 MB served --
    /// moved the game's working set by 704 KB.
    ///
    /// THE ONE ROUTE HERE THAT IS NOT FREE. <c>/extcells</c> serves the per-cell extension
    /// registry, and that one really does need a SimDLL export: the registry is not part of
    /// <c>GameDataUpdate</c> at all, so there is no published pointer to memcpy out of and no
    /// way to reach it except by asking the sim. It is served beside the grid arrays because a
    /// client wanting both wants them the same way, but it costs a wait on the sim thread and
    /// a full serialisation -- see <see cref="HandleExtCells"/>.
    /// </summary>
    public static class DebugBulkRoutes
    {
        /// <summary>
        /// Bumped whenever the wire layout changes in a way an existing client would misread.
        /// A client MUST check it: the body is raw memory, so a layout change that is not
        /// version-gated does not fail, it produces plausible wrong numbers.
        /// </summary>
        public const uint FormatVersion = 1;

        /// <summary>8 bytes, so the fixed header stays 4-aligned and a hexdump is readable.</summary>
        private static readonly byte[] Magic = { (byte)'O', (byte)'N', (byte)'I', (byte)'G', (byte)'R', (byte)'I', (byte)'D', 0 };

        private const int HeaderBytes = 32;
        private const int EntryBytes = 16;

        public const string BinaryContentType = "application/octet-stream";

        // ------------------------------------------------------------------
        // The surface table. Ids are part of the wire format: append, never renumber. The 1-19
        // band is the published grid arrays (the ones the game reads through Grid), the 20+
        // band is the property textures (the ones it reads through PropertyTextures), which is
        // the same split a frame digest groups by.
        // ------------------------------------------------------------------
        public readonly struct Surface
        {
            public readonly uint Id;
            public readonly string Name;
            public readonly int BytesPerCell;

            public Surface(uint id, string name, int bytesPerCell)
            {
                Id = id;
                Name = name;
                BytesPerCell = bytesPerCell;
            }
        }

        public static readonly Surface[] Surfaces =
        {
            new Surface(1, "elementIdx", 2),
            new Surface(2, "temperature", 4),
            new Surface(3, "mass", 4),
            new Surface(4, "radiation", 4),
            new Surface(5, "properties", 1),
            new Surface(6, "insulation", 1),
            new Surface(7, "strengthInfo", 1),
            new Surface(8, "diseaseIdx", 1),
            new Surface(9, "diseaseCount", 4),
            new Surface(10, "backwallElement", 2),
            new Surface(11, "backwallMass", 4),
            new Surface(12, "backwallTemperature", 4),
            new Surface(13, "accumulatedFlow", 4),
            new Surface(20, "propertyTextureFlow", 8),
            new Surface(21, "propertyTextureLiquid", 4),
            new Surface(22, "propertyTextureLiquidData", 4),
            new Surface(23, "propertyTextureMaterialData", 4),
            new Surface(24, "propertyTextureExposedToSunlight", 1)
        };

        // ------------------------------------------------------------------
        // Pointer resolution.
        // ------------------------------------------------------------------

        /// <summary>
        /// The pointer a surface's bytes live behind, or <see cref="IntPtr.Zero"/> if the game
        /// has not published it (before <c>Sim.Start()</c>, or on a SimDLL that leaves it null).
        /// An unpublished surface is reported as a zero-length entry rather than dropped, so a
        /// client can tell "not published this frame" from "never asked for".
        /// </summary>
        private static unsafe IntPtr Resolve(uint id)
        {
            switch (id)
            {
                case 1: return (IntPtr)Grid.elementIdx;
                case 2: return (IntPtr)Grid.temperature;
                case 3: return (IntPtr)Grid.mass;
                case 4: return (IntPtr)Grid.radiation;
                case 5: return (IntPtr)Grid.properties;
                case 6: return (IntPtr)Grid.insulation;
                case 7: return (IntPtr)Grid.strengthInfo;
                case 8: return (IntPtr)Grid.diseaseIdx;
                case 9: return (IntPtr)Grid.diseaseCount;
                case 10: return BackwallPointer(ref backwallElementField, "element_idx");
                case 11: return BackwallPointer(ref backwallMassField, "mass");
                case 12: return BackwallPointer(ref backwallTemperatureField, "temperature");
                case 13: return (IntPtr)Grid.AccumulatedFlowValues;
                case 20: return PropertyTextures.externalFlowTex;
                case 21: return PropertyTextures.externalLiquidTex;
                case 22: return PropertyTextures.externalLiquidDataTex;
                case 23: return PropertyTextures.externalMaterialDataTex;
                case 24: return PropertyTextures.externalExposedToSunlight;
                default: return IntPtr.Zero;
            }
        }

        private static FieldInfo backwallElementField;
        private static FieldInfo backwallMassField;
        private static FieldInfo backwallTemperatureField;

        /// <summary>
        /// The three backwall arrays are the one group the game does NOT expose publicly:
        /// <c>BackwallManager</c> keeps <c>element_idx</c>, <c>mass</c> and <c>temperature</c>
        /// as private static pointers, assigned in its own <c>UpdateFromSim</c> from the same
        /// <c>GameDataUpdate</c> everything else here comes from. Reflecting at them is the same
        /// move <c>DebugInspectorServer.DumpStatusItems</c> already makes for
        /// <c>StatusItemGroup</c>'s private list, and for the same reason: the alternative is
        /// <c>BackwallManager.At(cell)</c> a quarter of a million times.
        ///
        /// A pointer-typed field boxes as <see cref="System.Reflection.Pointer"/>, which is why
        /// this cannot just cast the result of <c>GetValue</c>.
        /// </summary>
        private static unsafe IntPtr BackwallPointer(ref FieldInfo cache, string fieldName)
        {
            if (cache == null)
            {
                cache = typeof(BackwallManager).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
                if (cache == null)
                {
                    return IntPtr.Zero;
                }
            }
            object boxed = cache.GetValue(null);
            if (boxed == null)
            {
                return IntPtr.Zero;
            }
            return (IntPtr)System.Reflection.Pointer.Unbox(boxed);
        }

        // ------------------------------------------------------------------
        // Buffer pool.
        // ------------------------------------------------------------------

        private const int MaxPooledBuffers = 4;
        private static readonly ConcurrentStack<byte[]> pool = new ConcurrentStack<byte[]>();
        private static int pooledCount;

        private static byte[] Rent(int size)
        {
            while (pool.TryPop(out byte[] candidate))
            {
                Interlocked.Decrement(ref pooledCount);
                if (candidate.Length >= size)
                {
                    return candidate;
                }
                // Too small for this request and every later one will be about this size --
                // drop it rather than putting it back to be re-rejected forever.
            }
            return new byte[size];
        }

        /// <summary>
        /// Returns a body buffer to the pool. Called by <see cref="DebugInspectorServer"/> once
        /// the response has been written, never by a route handler: the buffer is live until the
        /// socket write finishes on the connection thread.
        /// </summary>
        public static void Release(byte[] buffer)
        {
            if (buffer == null || Interlocked.Increment(ref pooledCount) > MaxPooledBuffers)
            {
                Interlocked.Decrement(ref pooledCount);
                return;
            }
            pool.Push(buffer);
        }

        // ------------------------------------------------------------------
        // Little-endian scalar writes. Explicit rather than BitConverter, so the wire format is
        // stated in the code that produces it instead of inherited from the host.
        // ------------------------------------------------------------------

        private static void PutU32(byte[] b, int offset, uint value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
            b[offset + 2] = (byte)(value >> 16);
            b[offset + 3] = (byte)(value >> 24);
        }

        private static int Align4(int offset)
        {
            return (offset + 3) & ~3;
        }

        // ------------------------------------------------------------------
        // /grid -- the binary bulk route.
        // ------------------------------------------------------------------

        /// <summary>
        /// Handles <c>/grid</c> if that is what was asked for, returning whether it owned the
        /// path. On success it fills the ticket's binary body; on a bad request it fills the
        /// ticket's JSON response instead and still returns true, because the path was still
        /// this route's to answer.
        ///
        /// MUST be called on the game's main thread -- it dereferences the sim's published
        /// pointers.
        /// </summary>
        public static bool TryHandleBinary(DebugInspectorServer.Ticket ticket)
        {
            if (ticket.Path == "/extcells")
            {
                if (DebugInspectorServer.GetQueryParam(ticket.Query, "published") != null)
                {
                    HandlePublishedExtCells(ticket);
                }
                else
                {
                    HandleExtCells(ticket);
                }
                return true;
            }

            if (ticket.Path == "/extevents")
            {
                HandleExtEvents(ticket);
                return true;
            }

            if (ticket.Path != "/grid")
            {
                return false;
            }

            string requested = DebugInspectorServer.GetQueryParam(ticket.Query, "arrays");
            if (string.IsNullOrEmpty(requested))
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "/grid needs an arrays= list, or arrays=all"),
                    ("names", SurfaceNames()),
                    ("note", "arrays=all is the whole published frame -- 57 bytes/cell, 14,646,128 B at 636x404"));
                return true;
            }

            if (!Grid.IsInitialized())
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "the grid is not initialized -- is a world loaded?"));
                return true;
            }

            var selected = new List<Surface>();
            if (requested == "all")
            {
                selected.AddRange(Surfaces);
            }
            else
            {
                foreach (string name in requested.Split(','))
                {
                    string trimmed = name.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                    bool found = false;
                    foreach (Surface s in Surfaces)
                    {
                        if (s.Name == trimmed)
                        {
                            selected.Add(s);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        ticket.Response = DebugInspectorServer.Json.Obj(
                            ("error", "unknown array: " + trimmed),
                            ("names", SurfaceNames()));
                        return true;
                    }
                }
            }

            if (selected.Count == 0)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "arrays= named nothing"),
                    ("names", SurfaceNames()));
                return true;
            }

            int width = Grid.WidthInCells;
            int height = Grid.HeightInCells;
            int cells = width * height;

            // Two passes: size the body exactly (so the pool sees one stable size), then fill
            // it. An unpublished surface still gets a table entry, with a zero length.
            var pointers = new IntPtr[selected.Count];
            var offsets = new int[selected.Count];
            var lengths = new int[selected.Count];

            int cursor = HeaderBytes + EntryBytes * selected.Count;
            for (int i = 0; i < selected.Count; i++)
            {
                pointers[i] = Resolve(selected[i].Id);
                if (pointers[i] == IntPtr.Zero)
                {
                    offsets[i] = 0;
                    lengths[i] = 0;
                    continue;
                }
                cursor = Align4(cursor);
                offsets[i] = cursor;
                lengths[i] = cells * selected[i].BytesPerCell;
                cursor += lengths[i];
            }

            byte[] body = Rent(cursor);

            Buffer.BlockCopy(Magic, 0, body, 0, Magic.Length);
            PutU32(body, 8, FormatVersion);
            PutU32(body, 12, (uint)width);
            PutU32(body, 16, (uint)height);
            PutU32(body, 20, (uint)cells);
            PutU32(body, 24, (uint)Time.frameCount);
            PutU32(body, 28, (uint)selected.Count);

            for (int i = 0; i < selected.Count; i++)
            {
                int entry = HeaderBytes + EntryBytes * i;
                PutU32(body, entry, selected[i].Id);
                PutU32(body, entry + 4, (uint)selected[i].BytesPerCell);
                PutU32(body, entry + 8, (uint)lengths[i]);
                PutU32(body, entry + 12, (uint)offsets[i]);

                if (lengths[i] > 0)
                {
                    Marshal.Copy(pointers[i], body, offsets[i], lengths[i]);
                }
            }

            ticket.Binary = body;
            ticket.BinaryLength = cursor;
            ticket.ContentType = BinaryContentType;
            return true;
        }

        // ------------------------------------------------------------------
        // /extcells -- the per-cell extension registry.
        // ------------------------------------------------------------------

        /// <summary>
        /// Serves the whole extension registry -- every property registered through
        /// <c>ext::CellPropertyRegistry</c>, by whoever registered it -- as the sim's own
        /// self-describing blob, byte for byte.
        ///
        /// WHY IT IS A SEPARATE ROUTE AND NOT A SURFACE. Every entry in
        /// <see cref="Surfaces"/> is one array of a fixed width over the game's grid, and the
        /// client is told its id and bytes-per-cell in advance. An extension property is none
        /// of those things: it is registered by name at runtime, it can carry several
        /// components per cell, and it is indexed on the sim's PADDED grid rather than the
        /// game's. Folding it into the surface table would mean inventing a per-frame surface
        /// id for something the sim names with a string, and would silently hand the client an
        /// array two rows and two columns larger than every other one.
        ///
        /// WHY THE BODY IS PASSED THROUGH UNTOUCHED. The blob already carries its own magic,
        /// version, and a full header per record -- name, type, arity, stride, cell count,
        /// registered default -- so a client needs no list of property names and no second
        /// format to learn. The replacement SimDLL's <c>sim/saveblob.h</c> is header-only and depends on
        /// nothing but the standard library, so a client decodes this with the SIM'S OWN
        /// decoder instead of a reimplementation that can drift. Re-wrapping it here would
        /// destroy that property for no gain.
        ///
        /// COST. Unlike <c>/grid</c>, this is not free: the export waits for the sim thread to
        /// go idle and serialises every non-default property, which on a world with an active
        /// gas mixture is about 13 MB. Poll it when something is actually reading extension
        /// values, not once per frame out of habit. A world where nothing has been written
        /// answers in 12 bytes, because the sim omits a property whose every cell is still at
        /// its registered default.
        ///
        /// <c>?list=1</c> answers in JSON instead: the record headers and how many cells of
        /// each differ from the default. That is for a human with curl -- it costs a full scan
        /// of the payload, and a program should decode the bytes.
        ///
        /// MUST be called on the game's main thread: the export drains into the sim.
        /// </summary>
        private static void HandleExtCells(DebugInspectorServer.Ticket ticket)
        {
            int size = SimExtCellState.StateSize();
            if (size <= 0)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", SimExtCellState.Available
                        ? "the sim has no allocated world -- is a save loaded?"
                        : "this SimDLL has no SIM_DebugExtCellStateAll export; it is either stock " +
                          "or too old"),
                    ("available", SimExtCellState.Available));
                return;
            }

            byte[] body = Rent(size);
            if (!SimExtCellState.TryFillState(body, size))
            {
                Release(body);
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "the sim reported " + size + " bytes and then declined to fill them; " +
                              "the world may have been reallocated between the two calls"));
                return;
            }

            if (DebugInspectorServer.GetQueryParam(ticket.Query, "list") == null)
            {
                ticket.Binary = body;
                ticket.BinaryLength = size;
                ticket.ContentType = BinaryContentType;
                return;
            }

            // The listing owns the buffer for the length of this call only -- it never reaches
            // the ticket, so it goes back to the pool here rather than after the socket write.
            string json = DescribeExtCells(body, size);
            Release(body);
            ticket.Response = json;
        }

        /// <summary>
        /// The <c>?list=1</c> answer: what the blob holds, without the bytes. Deliberately not
        /// the values -- see <see cref="SimExtCellState.Describe"/> for why this reader is a
        /// shallow one.
        /// </summary>
        private static string DescribeExtCells(byte[] body, int size)
        {
            // Describe walks to the exact end of what it is given, so it must be given the
            // blob and not the pool buffer, which may be longer.
            byte[] exact = body;
            if (body.Length != size)
            {
                exact = new byte[size];
                Buffer.BlockCopy(body, 0, exact, 0, size);
            }

            if (!SimExtCellState.Describe(exact, out List<SimExtCellState.Property> props, out string error))
            {
                return DebugInspectorServer.Json.Obj(("error", error), ("bytes", size));
            }

            var rows = new List<object>();
            foreach (SimExtCellState.Property p in props)
            {
                rows.Add(new DebugInspectorServer.RawJson(DebugInspectorServer.Json.Obj(
                    ("name", p.Name),
                    ("type", p.TypeName),
                    ("arity", p.Arity),
                    ("stride", p.Stride),
                    ("cells", p.CellCount),
                    ("bytes", p.ByteLength),
                    ("default", p.DefaultValue),
                    ("nonDefaultCells", SimExtCellState.CountNonDefault(exact, p)))));
            }

            int paddedWidth = Grid.IsInitialized() ? Grid.WidthInCells + 2 : 0;
            return DebugInspectorServer.Json.Obj(
                ("bytes", size),
                ("count", rows.Count),
                // Stated rather than left to be inferred: every record is indexed on the sim's
                // padded grid, so game cell (x, y) is at (y + 1) * paddedWidth + (x + 1).
                ("paddedWidth", paddedWidth),
                ("paddedHeight", Grid.IsInitialized() ? Grid.HeightInCells + 2 : 0),
                ("note", "cell-major: component c of padded cell i is at (i * arity + c) * stride"),
                ("properties", rows));
        }

        // ------------------------------------------------------------------
        // /extcells?published=1 -- the same registry, off the per-frame path.
        // ------------------------------------------------------------------

        /// <summary>
        /// How long a property or stream may go unrequested before this route unsubscribes it
        /// again, in seconds of wall clock.
        ///
        /// It exists because the failure this route must not have is a leak: a visualizer that
        /// is Ctrl-C'd, or a connection that drops, would otherwise leave the game copying tens
        /// of megabytes a second forever with nobody left to turn it off. A debug route is
        /// allowed to cost the game something while somebody is watching and is not allowed to
        /// cost it anything afterwards.
        ///
        /// IT USED TO BE A FRAME COUNT, AND THE COUNT DID NOT MEAN WHAT IT SAID. The constant
        /// was `600` bound frames, documented as "about two minutes at the sim's 5 ticks a
        /// second" -- but the thing being counted is <see cref="SimExtFrame.TickId"/>, which
        /// increments once per `PrepareGameData` message, and `Game.SimEveryTick` sends one
        /// EVERY RENDER FRAME: the 200 ms sim tick is `UnsafeSim200ms(0.2f)` on one sub-tick in
        /// twelve, and every other frame still calls `UnsafeSim200ms(0f)`. So the real lease was
        /// 600 render frames -- ten seconds at 60 fps, two and a half on the dev build's ~240 --
        /// and a client polling slower than that re-handshook forever instead of streaming.
        ///
        /// Worse, `Game.Update` is gated only on `isLoading`, so frames keep binding while the
        /// game is PAUSED. A lease measured in them expired under a client that had paused the
        /// game precisely in order to look at it.
        ///
        /// Wall clock says what was always meant, and is the one clock immune to frame rate, to
        /// pause and to game speed. The expiry still only runs on a bound frame, so a lease
        /// cannot expire while no frames are being published at all -- but nothing is being
        /// copied then either, so there is nothing to leak.
        /// </summary>
        public const double IdleSecondsBeforeUnsubscribe = 120.0;

        /// <summary>
        /// The lease clock. Monotonic, unaffected by wall-clock changes, and deliberately not
        /// Unity's <c>Time</c>: <c>Time.realtimeSinceStartup</c> is main-thread-only, and while
        /// every route handler does run on the main thread today, a lease timestamp is not worth
        /// making that a requirement.
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch leaseClock =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Seconds since this class was first touched. See <see cref="leaseClock"/>.</summary>
        private static double NowSeconds()
        {
            return leaseClock.Elapsed.TotalSeconds;
        }

        // Only what THIS ROUTE subscribed, and the lease-clock second each was last asked for.
        // A property a
        // mod subscribed for its own use is never unsubscribed here: the subscription belongs
        // to whoever asked for it, and stealing one would break a consumer that is reading it
        // every tick and never touches HTTP.
        private static readonly Dictionary<string, double> routeSubscriptions =
            new Dictionary<string, double>();
        private static bool expiryHooked;

        /// <summary>
        /// The per-frame half of <c>/extcells</c>: the same self-describing ONIE blob, built
        /// from the properties subscribed to the published descriptor table rather than from
        /// <c>SIM_DebugExtCellStateAll</c>.
        ///
        /// WHY IT IS THE SAME FORMAT AND NOT A NEW ONE. The blob's decoder is
        /// the replacement SimDLL's <c>sim/saveblob.h</c>, which is header-only and depends on
        /// nothing but the standard library, so a client decodes this with the SIM'S OWN reader
        /// by including that file. Inventing a second wire shape for
        /// the same bytes would mean a second parser that can drift from the sim, in exchange
        /// for nothing: the win here was never the format.
        ///
        /// WHAT THE WIN ACTUALLY IS, in order.
        ///
        /// 1. NO WORKER BARRIER. <c>SIM_DebugExtCellStateAll</c> waits for the sim thread to go
        ///    idle and serialises the world; the published table is already copied and is read
        ///    with no barrier at all, which is the entire reason the publish table exists.
        /// 2. A SUBSET. <c>props=</c> names what the caller is actually reading, where the
        ///    inspection route is all-or-nothing. That is where the bytes drop -- not from the
        ///    mechanism, which moves the same bytes for the same properties.
        /// 3. ABSENCE STOPS BEING AMBIGUOUS. The inspection blob OMITS a property whose every
        ///    cell is still at its registered default, so "registered but untouched" and "never
        ///    registered" arrive identically. A subscribed property is in this blob whatever its
        ///    contents, so a client can tell those apart for the first time.
        ///
        /// A GET HERE MUTATES SIM STATE, which is worth saying out loud because no other route
        /// in this file does. Asking for an unsubscribed property sends
        /// <c>kPublishCellProperty</c> and answers "subscribed, ask again next tick" rather
        /// than serving a frame it cannot serve -- the message is queued, so tick N+1 is
        /// genuinely the earliest the data exists. It is defensible only because the ABI defines
        /// publishing as a READ WINDOW: explicitly not a checkpoint component and not a save
        /// section, so it cannot change what the simulation computes, only what a frame costs
        /// to publish. See <see cref="IdleSecondsBeforeUnsubscribe"/> for the other half of that
        /// bargain.
        ///
        /// MUST be called on the game's main thread: it dereferences this tick's published
        /// pointers.
        /// </summary>
        private static void HandlePublishedExtCells(DebugInspectorServer.Ticket ticket)
        {
            if (!SimExtFrame.Installed)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "the per-tick binder is not installed, so there is no published " +
                              "frame to read -- a mod must call SimExtFrame.Install(harmony) " +
                              "before this route can answer"),
                    ("route", "/extcells (no published=1) still works and needs no binder"));
                return;
            }
            if (!SimExtFrame.Available || !SimExtCellProperties.Available)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "this SimDLL has no extension publish exports; it is either stock " +
                              "or too old"));
                return;
            }

            HookExpiry();

            var wanted = new List<string>();
            string requested = DebugInspectorServer.GetQueryParam(ticket.Query, "props");
            if (string.IsNullOrEmpty(requested))
            {
                // No list: whatever is published right now. Deliberately not "everything
                // registered" -- that would subscribe the world on a bare curl, which is the
                // one thing an opt-in mechanism must not let a typo do.
                foreach (SimExtFrame.PublishedProperty p in SimExtFrame.Properties)
                {
                    wanted.Add(p.Name);
                }
            }
            else
            {
                foreach (string name in requested.Split(','))
                {
                    string trimmed = name.Trim();
                    if (trimmed.Length > 0 && !wanted.Contains(trimmed))
                    {
                        wanted.Add(trimmed);
                    }
                }
            }

            // Resolve every name before subscribing to any of them, so a request with one typo
            // in it does not half-apply.
            var indices = new List<int>();
            var descriptors = new List<SimExtCellProperties.Descriptor>();
            foreach (string name in wanted)
            {
                int idx = SimExtCellProperties.Find(name);
                if (idx < 0)
                {
                    ticket.Response = DebugInspectorServer.Json.Obj(
                        ("error", "no extension property named " + name + " is registered"),
                        ("registered", RegisteredNames()));
                    return;
                }
                if (!SimExtCellProperties.TryDescribe(idx, out SimExtCellProperties.Descriptor d))
                {
                    ticket.Response = DebugInspectorServer.Json.Obj(
                        ("error", "the sim resolved " + name + " to index " + idx +
                                  " and then declined to describe it"));
                    return;
                }
                indices.Add(idx);
                descriptors.Add(d);
            }

            // Subscribe what is not subscribed, and answer with what was asked for rather than
            // with an empty frame. The caller is told exactly which names it must wait for.
            var pending = new List<string>();
            for (int i = 0; i < wanted.Count; i++)
            {
                if (!SimExtFrame.TryGetProperty(wanted[i], out SimExtFrame.PublishedProperty pp) ||
                    !pp.IsCurrent)
                {
                    if (!routeSubscriptions.ContainsKey(wanted[i]))
                    {
                        SimExtCellProperties.Publish(indices[i], true);
                    }
                    routeSubscriptions[wanted[i]] = NowSeconds();
                    pending.Add(wanted[i]);
                    continue;
                }
                // Already published: renew its lease whether this route subscribed it or a mod
                // did. Renewing one we do not own is harmless -- the expiry only ever touches
                // names in this dictionary, and a name gets in here only by being subscribed
                // above.
                if (routeSubscriptions.ContainsKey(wanted[i]))
                {
                    routeSubscriptions[wanted[i]] = NowSeconds();
                }
            }

            if (pending.Count > 0)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("subscribed", pending.ToArray()),
                    ("retry", "next tick"),
                    ("tick", SimExtFrame.TickId),
                    ("note", "kPublishCellProperty is queued, so a property subscribed during " +
                             "tick N first appears in the table tick N+1 publishes"));
                return;
            }

            // Two passes, same as /grid: size the body exactly, then fill it.
            int total = ExtBlobHeaderBytes + 4;
            for (int i = 0; i < wanted.Count; i++)
            {
                SimExtFrame.TryGetProperty(wanted[i], out SimExtFrame.PublishedProperty pp);
                total += ExtRecordHeaderBytes + pp.ByteCount;
            }

            byte[] body = Rent(total);
            PutU32(body, 0, ExtBlobMagic);
            PutU32(body, 4, ExtBlobVersion);
            PutU32(body, 8, (uint)wanted.Count);

            int cursor = ExtBlobHeaderBytes + 4;
            for (int i = 0; i < wanted.Count; i++)
            {
                SimExtFrame.TryGetProperty(wanted[i], out SimExtFrame.PublishedProperty pp);
                SimExtCellProperties.Descriptor d = descriptors[i];

                // The frame is authoritative about the bytes and the registry is authoritative
                // about the default. They must agree about the shape; if they do not, the world
                // was reallocated between the two calls and every offset below is wrong, so
                // this refuses rather than serving a blob whose records do not line up.
                if (pp.Arity != d.Arity || pp.Stride != d.Stride || pp.CellCount != d.CellCount ||
                    pp.ByteCount != d.ByteCount)
                {
                    Release(body);
                    ticket.Response = DebugInspectorServer.Json.Obj(
                        ("error", "the published frame and the registry disagree about the shape " +
                                  "of " + wanted[i] + "; the world was reallocated mid-request"),
                        ("frame", pp.Arity + "x" + pp.Stride + " over " + pp.CellCount + " cells"),
                        ("registry", d.Arity + "x" + d.Stride + " over " + d.CellCount + " cells"));
                    return;
                }

                Array.Clear(body, cursor, ExtRecordHeaderBytes);
                WriteAscii(body, cursor, wanted[i]);
                PutU32(body, cursor + 48, unchecked((uint)(int)d.Type));
                PutU32(body, cursor + 52, unchecked((uint)d.Arity));
                PutU32(body, cursor + 56, unchecked((uint)d.Stride));
                PutU32(body, cursor + 60, unchecked((uint)d.CellCount));
                PutU32(body, cursor + 64, d.DefaultBits);
                cursor += ExtRecordHeaderBytes;

                if (pp.ByteCount > 0)
                {
                    Marshal.Copy(pp.Data, body, cursor, pp.ByteCount);
                    cursor += pp.ByteCount;
                }
            }

            ticket.Binary = body;
            ticket.BinaryLength = cursor;
            ticket.ContentType = BinaryContentType;
        }

        // ------------------------------------------------------------------ /extevents

        /// <summary>
        /// How many buffered batches, and how many bytes of them, this route holds between
        /// polls. Both are ceilings on what a game will spend on a client that has stopped
        /// asking; neither is a tuning knob a caller can raise.
        ///
        /// The sim's own per-frame ceiling is <c>kExtStreamBytesPerFrame</c>, a megabyte per
        /// stream per frame, so a single pathological frame could fill this on its own. That is
        /// the intended behaviour: the alternative is a debug route that can be made to hold
        /// arbitrary memory by a producer nobody is reading.
        /// </summary>
        public const int MaxBufferedEventBatches = 240;

        /// <summary>Companion byte ceiling to <see cref="MaxBufferedEventBatches"/>.</summary>
        public const int MaxBufferedEventBytes = 2 * 1024 * 1024;

        /// <summary>
        /// One stream's records from one published frame. A batch is the unit because a stream
        /// is a per-frame fact: <c>count</c> and <c>dropped</c> describe that frame and nothing
        /// else, and concatenating two frames' records into one list would throw away the only
        /// thing that says when each happened.
        /// </summary>
        private struct EventBatch
        {
            public string Name;
            public long Tick;
            public int Stride;
            public int Count;
            public int Dropped;
            public byte[] Bytes;
        }

        // The accumulator, and the two counters that keep it honest. All three are touched only
        // from the game's main thread -- `Bound` fires there and the inspector server hands
        // tickets there -- so none of this is locked, for the same reason nothing else in this
        // file is.
        private static readonly List<EventBatch> bufferedEvents = new List<EventBatch>();
        private static int bufferedEventBytes;
        private static int lostEventBatches;

        // Only what THIS ROUTE subscribed, name -> the frame it was last asked for. Same rule
        // as `routeSubscriptions`: a stream a mod subscribed for its own use is never
        // unsubscribed here, and is never collected here either -- accumulating a stream nobody
        // asked this route about would be spending the game's memory on a guess.
        private static readonly Dictionary<string, double> eventSubscriptions =
            new Dictionary<string, double>();

        // The names in `eventSubscriptions` that were ALREADY collecting when this route first
        // asked for them, and whose sim-side subscription therefore belongs to somebody else.
        // They are collected and drained like any other -- a client asking for a stream should
        // get it -- but the expiry never turns them off.
        //
        // This is the invariant the comment above always claimed and the code did not keep.
        // FOUND LIVE: SimExtSelfTest subscribes sim.message_refused for its own
        // reads, /extevents subscribed it again, and one lapsed lease later the expiry called
        // Subscribe(idx, false) -- which is a plain bool in the sim (`simdll.cpp`'s
        // ApplySubscribeEventStream), not a refcount, so one route's housekeeping silently
        // stopped another consumer's stream. The route's own discovery JSON reported the
        // damage: "collecting" before the polls lapsed, "idle" after.
        //
        // WHAT THIS DOES NOT FIX. A mod that subscribes AFTER this route did is still invisible
        // -- there is no way to ask the sim who wants a stream, only whether anyone does. The
        // complete fix is a refcount in EventStreamRegistry, which is a sim-side ABI change;
        // this closes the case that actually happens (a mod subscribing at registration time,
        // long before any HTTP client exists) without one.
        private static readonly HashSet<string> foreignEventStreams = new HashSet<string>();
        private static bool eventCollectorHooked;

        /// <summary>
        /// <c>/extevents</c> -- the event-stream half of the extension registry on the wire.
        ///
        /// WHY THIS ROUTE ACCUMULATES AND <c>/extcells</c> DOES NOT. A cell property is STATE:
        /// poll it whenever you like and you get the current picture, because the picture is
        /// what the array holds. An event stream is a TIME SERIES, and the sim clears every
        /// stream at the end of each <c>BuildUpdate</c>. A client polling at 10 Hz against a
        /// game ticking at 60 would therefore see at most ten frames in sixty and silently lose
        /// the other fifty -- which is exactly the failure the sim's own <c>dropped</c> counter
        /// exists to prevent, reintroduced one layer up and this time invisible.
        ///
        /// So the game side collects on every published frame and the GET drains what has
        /// collected. The buffer is bounded (<see cref="MaxBufferedEventBatches"/>,
        /// <see cref="MaxBufferedEventBytes"/>) and reports its own overflow SEPARATELY from the
        /// sim's, because "the game was quiet", "the producer outran its per-frame cap" and
        /// "this route outran your polling" are three different facts and a client that cannot
        /// tell them apart will misread all three.
        ///
        /// A BARE GET SUBSCRIBES NOTHING. Without <c>streams=</c> this answers with the declared
        /// streams, their strides and whether each is collecting -- discovery, which is the
        /// thing <c>SIM_ExtEventStreamIndex</c> alone could never give a client that has no name
        /// to ask with. With <c>streams=</c> it subscribes what it must and drains.
        ///
        /// LIKE <c>/extcells?published=1</c>, A GET HERE MUTATES SIM STATE, and the same
        /// argument licenses it: a stream is explicitly not a checkpoint component and not a
        /// save section, so subscribing changes what a frame costs to publish and never what
        /// the simulation computes. <see cref="IdleSecondsBeforeUnsubscribe"/> is the other half
        /// of the bargain, and applies here too.
        ///
        /// MUST be called on the game's main thread: it reads this tick's published pointers.
        /// </summary>
        private static void HandleExtEvents(DebugInspectorServer.Ticket ticket)
        {
            if (!SimExtFrame.Installed)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "the per-tick binder is not installed, so there is no published " +
                              "frame to collect from -- a mod must call " +
                              "SimExtFrame.Install(harmony) before this route can answer"));
                return;
            }
            if (!SimExtFrame.Available || !SimExtEventStreams.Available)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "this SimDLL has no event-stream exports; it is either stock or " +
                              "too old"));
                return;
            }

            List<SimExtEventStreams.Descriptor> declared = SimExtEventStreams.Describe();
            if (declared.Count == 0 && SimExtEventStreams.Count() == 0)
            {
                // Not the same as "no streams": a SimDLL older than stage 4b declares streams
                // and cannot enumerate them, and saying "there are none" would be a lie a
                // client has no way to catch.
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("error", "this SimDLL cannot enumerate event streams; it is too old"),
                    ("note", "SIM_ExtEventStreamIndex still resolves a name you already know"));
                return;
            }

            HookEventCollector();

            string requested = DebugInspectorServer.GetQueryParam(ticket.Query, "streams");
            if (string.IsNullOrEmpty(requested))
            {
                var names = new string[declared.Count];
                var strides = new string[declared.Count];
                for (int i = 0; i < declared.Count; i++)
                {
                    names[i] = declared[i].Name;
                    strides[i] = declared[i].Name + " stride=" + declared[i].Stride +
                                 (declared[i].Subscribed ? " collecting" : " idle");
                }
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("streams", names),
                    ("detail", strides),
                    ("buffered", bufferedEvents.Count),
                    ("tick", SimExtFrame.TickId),
                    ("note", "add streams=a,b to subscribe and drain; a bare GET subscribes " +
                             "nothing, because a typo must not put the game to work"));
                return;
            }

            var wanted = new List<string>();
            foreach (string name in requested.Split(','))
            {
                string trimmed = name.Trim();
                if (trimmed.Length > 0 && !wanted.Contains(trimmed))
                {
                    wanted.Add(trimmed);
                }
            }

            // Resolve every name before subscribing to any, so one typo cannot half-apply.
            var indices = new List<int>();
            var alreadyCollecting = new List<bool>();
            for (int i = 0; i < wanted.Count; i++)
            {
                int found = -1;
                bool collecting = false;
                for (int j = 0; j < declared.Count; j++)
                {
                    if (declared[j].Name == wanted[i])
                    {
                        found = declared[j].DeclaredIndex;
                        collecting = declared[j].Subscribed;
                        break;
                    }
                }
                if (found < 0)
                {
                    var available = new string[declared.Count];
                    for (int j = 0; j < declared.Count; j++)
                    {
                        available[j] = declared[j].Name;
                    }
                    ticket.Response = DebugInspectorServer.Json.Obj(
                        ("error", "this SimDLL declares no event stream named " + wanted[i]),
                        ("streams", available),
                        ("note", "nothing was subscribed; a request is all-or-nothing"));
                    return;
                }
                indices.Add(found);
                alreadyCollecting.Add(collecting);
            }

            var pending = new List<string>();
            for (int i = 0; i < wanted.Count; i++)
            {
                if (eventSubscriptions.ContainsKey(wanted[i]))
                {
                    // Renew the lease whether this route subscribed it or a mod did; the expiry
                    // only ever looks at names in this dictionary.
                    eventSubscriptions[wanted[i]] = NowSeconds();
                    continue;
                }

                eventSubscriptions[wanted[i]] = NowSeconds();
                if (alreadyCollecting[i])
                {
                    // Somebody else's subscription. Collect it -- a client that asked for a
                    // stream should be handed it -- but record that the off-switch is not ours,
                    // and do not make the caller wait out a handshake for a stream that is
                    // already collecting. The first drain will usually be empty anyway: this
                    // route's buffer starts here, not where the other consumer's did.
                    foreignEventStreams.Add(wanted[i]);
                    continue;
                }

                SimExtEventStreams.Subscribe(indices[i], true);
                pending.Add(wanted[i]);
            }

            if (pending.Count > 0)
            {
                ticket.Response = DebugInspectorServer.Json.Obj(
                    ("subscribed", pending.ToArray()),
                    ("retry", "in two ticks"),
                    ("tick", SimExtFrame.TickId),
                    ("note", "kSubscribeEventStream is queued: the drain on tick N sets the " +
                             "flag, tick N+1 is the first frame that collects, so N+1 is the " +
                             "first frame this route can capture"));
                return;
            }

            // Drain. Only the streams this request named -- a client watching one stream should
            // not be handed another one's records because a second client asked for it.
            var batches = new List<EventBatch>();
            int kept = 0;
            for (int i = 0; i < bufferedEvents.Count; i++)
            {
                if (wanted.Contains(bufferedEvents[i].Name))
                {
                    batches.Add(bufferedEvents[i]);
                }
                else
                {
                    bufferedEvents[kept++] = bufferedEvents[i];
                }
            }
            bufferedEvents.RemoveRange(kept, bufferedEvents.Count - kept);
            bufferedEventBytes = 0;
            for (int i = 0; i < bufferedEvents.Count; i++)
            {
                bufferedEventBytes += bufferedEvents[i].Bytes.Length;
            }

            int total = EventBlobHeaderBytes;
            for (int i = 0; i < batches.Count; i++)
            {
                total += EventRecordHeaderBytes + Align4(batches[i].Bytes.Length);
            }

            byte[] body = Rent(total);
            PutU32(body, 0, EventBlobMagic);
            PutU32(body, 4, EventBlobVersion);
            PutU32(body, 8, (uint)batches.Count);
            PutU32(body, 12, unchecked((uint)lostEventBatches));
            lostEventBatches = 0;

            int cursor = EventBlobHeaderBytes;
            for (int i = 0; i < batches.Count; i++)
            {
                EventBatch batch = batches[i];
                Array.Clear(body, cursor, EventRecordHeaderBytes);
                WriteAscii(body, cursor, batch.Name);
                PutU32(body, cursor + 48, unchecked((uint)(int)batch.Tick));
                PutU32(body, cursor + 52, unchecked((uint)batch.Stride));
                PutU32(body, cursor + 56, unchecked((uint)batch.Count));
                PutU32(body, cursor + 60, unchecked((uint)batch.Dropped));
                PutU32(body, cursor + 64, unchecked((uint)batch.Bytes.Length));
                cursor += EventRecordHeaderBytes;

                Buffer.BlockCopy(batch.Bytes, 0, body, cursor, batch.Bytes.Length);
                int padded = Align4(batch.Bytes.Length);
                for (int pad = batch.Bytes.Length; pad < padded; pad++)
                {
                    body[cursor + pad] = 0;
                }
                cursor += padded;
            }

            ticket.Binary = body;
            ticket.BinaryLength = cursor;
            ticket.ContentType = BinaryContentType;
        }

        // The wire format, spelled here because there is no sim-side reader to reuse. Cell
        // properties had one -- `saveblob.h` is header-only and is literally the sim's decoder
        // -- but a stream is deliberately never serialised (it is not
        // a checkpoint component and not a save section), so no such file exists to borrow.
        //
        // Magic "ONIV" little-endian, a version, a batch count, and the batches this route
        // itself lost. Then one 68-byte batch header each -- name[48], tick, stride, count,
        // dropped, byteCount -- followed by the records, PADDED TO FOUR BYTES.
        //
        // The padding is the one place this deviates from ONIE, and it is not gratuitous: an
        // ONIE record's payload is a scalar array over a padded cell count, so it always ends
        // four-aligned by construction, while a stream's stride is whatever its producer
        // declared (1..4096). Without the pad a 3-byte record would leave every following
        // header misaligned, and a C++ reader casting straight to a record struct would be
        // reading unaligned. `byteCount` is the real length; the pad is never part of it.
        private const uint EventBlobMagic = 0x56494E4Fu;
        private const uint EventBlobVersion = 1u;
        private const int EventBlobHeaderBytes = 16;
        private const int EventRecordHeaderBytes = 68;

        /// <summary>
        /// Hooks the per-frame collector once. Collection has to happen on every published
        /// frame rather than on request for the reason <see cref="HandleExtEvents"/> gives:
        /// the sim clears every stream at the end of each frame, so a record nobody copied
        /// during that frame is gone, and a polling client would decide the game was quiet.
        /// </summary>
        private static void HookEventCollector()
        {
            if (eventCollectorHooked)
            {
                return;
            }
            SimExtFrame.Bound += CollectPublishedEvents;
            SimExtFrame.Bound += ExpireIdleEventSubscriptions;
            eventCollectorHooked = true;
        }

        private static void CollectPublishedEvents()
        {
            if (eventSubscriptions.Count == 0)
            {
                return;
            }
            foreach (SimExtFrame.PublishedStream stream in SimExtFrame.Streams)
            {
                if (!eventSubscriptions.ContainsKey(stream.Name))
                {
                    continue;
                }
                // A frame with no records and no drops is not a fact worth a batch. A frame
                // with drops and no records IS: it says the producer overran its cap, which is
                // the one thing a silent gap would otherwise look exactly like.
                if (stream.Count == 0 && stream.Dropped == 0)
                {
                    continue;
                }

                var batch = new EventBatch();
                batch.Name = stream.Name;
                batch.Tick = stream.Tick;
                batch.Stride = stream.Stride;
                batch.Count = stream.Count;
                batch.Dropped = stream.Dropped;
                batch.Bytes = new byte[stream.ByteCount];
                if (stream.ByteCount > 0)
                {
                    Marshal.Copy(stream.Data, batch.Bytes, 0, stream.ByteCount);
                }

                // A RING, and it drops the OLDEST -- which is the opposite of what the sim does
                // at its own cap, so it is worth saying why. The sim keeps a prefix because a
                // frame's list is one event: the first N of them are the beginning of what
                // happened. This buffer spans many frames while a client is away, and keeping
                // the prefix there would mean a buffer that filled once and then reported a
                // frozen past forever. `lostEventBatches` is what makes the gap visible instead
                // of silent.
                bufferedEvents.Add(batch);
                bufferedEventBytes += batch.Bytes.Length;
                while (bufferedEvents.Count > MaxBufferedEventBatches ||
                       (bufferedEventBytes > MaxBufferedEventBytes && bufferedEvents.Count > 1))
                {
                    bufferedEventBytes -= bufferedEvents[0].Bytes.Length;
                    bufferedEvents.RemoveAt(0);
                    lostEventBatches++;
                }
            }
        }

        private static void ExpireIdleEventSubscriptions()
        {
            if (eventSubscriptions.Count == 0)
            {
                return;
            }
            List<string> expired = null;
            double now = NowSeconds();
            foreach (KeyValuePair<string, double> entry in eventSubscriptions)
            {
                if (now - entry.Value < IdleSecondsBeforeUnsubscribe)
                {
                    continue;
                }
                if (expired == null)
                {
                    expired = new List<string>();
                }
                expired.Add(entry.Key);
            }
            if (expired == null)
            {
                return;
            }
            foreach (string name in expired)
            {
                eventSubscriptions.Remove(name);
                bool ours = !foreignEventStreams.Remove(name);
                if (ours)
                {
                    int idx = SimExtEventStreams.Find(name);
                    if (idx >= 0)
                    {
                        SimExtEventStreams.Subscribe(idx, false);
                    }
                }
                // The buffered batches go too. They belong to a subscription nobody is reading,
                // and holding them would mean the next client to ask for this stream is handed
                // records from before it existed as if they were current.
                int kept = 0;
                for (int i = 0; i < bufferedEvents.Count; i++)
                {
                    if (bufferedEvents[i].Name == name)
                    {
                        bufferedEventBytes -= bufferedEvents[i].Bytes.Length;
                        continue;
                    }
                    bufferedEvents[kept++] = bufferedEvents[i];
                }
                bufferedEvents.RemoveRange(kept, bufferedEvents.Count - kept);
                Debug.Log("[OniFramework] /extevents: " + (ours
                              ? "unsubscribed " + name
                              : "stopped collecting " + name + " (left subscribed; another " +
                                "consumer owns it)") + " after " +
                          IdleSecondsBeforeUnsubscribe.ToString("F0", CultureInfo.InvariantCulture) +
                          " s idle; nothing has asked for it and a collecting stream costs " +
                          "the game work every frame.");
            }
        }

        // sim/ext_state.h: magic "ONIE" little-endian, then a version, then the section
        // saveblob.h encodes -- an int32 count and one 68-byte record header per property.
        // Spelled here rather than derived, because these are the bytes a client's decoder is
        // matching on and a wrong constant produces a refusal at the far end with no clue why.
        private const uint ExtBlobMagic = 0x45494E4Fu;
        private const uint ExtBlobVersion = 1u;
        private const int ExtBlobHeaderBytes = 8;
        private const int ExtRecordHeaderBytes = 68;

        /// <summary>
        /// Writes <paramref name="text"/> as NUL-terminated ASCII into a 48-byte name field the
        /// caller has already zeroed. The sim caps a registered name at 47 characters precisely
        /// so it fits with its terminator, so this cannot truncate a name that came from the
        /// registry -- and it is bounded anyway, because a silently shortened name is a record
        /// no reader can match.
        /// </summary>
        private static void WriteAscii(byte[] destination, int offset, string text)
        {
            for (int i = 0; i < text.Length && i < 47; i++)
            {
                destination[offset + i] = (byte)text[i];
            }
        }

        private static string[] RegisteredNames()
        {
            var names = new List<string>();
            int count = SimExtCellProperties.Count();
            for (int i = 0; i < count; i++)
            {
                if (SimExtCellProperties.TryDescribe(i, out SimExtCellProperties.Descriptor d))
                {
                    names.Add(d.Name);
                }
            }
            return names.ToArray();
        }

        /// <summary>
        /// Hooks the per-frame expiry once. It runs on <see cref="SimExtFrame.Bound"/> rather
        /// than on the next request, because the case it exists for is precisely the one where
        /// no next request ever comes.
        /// </summary>
        private static void HookExpiry()
        {
            if (expiryHooked)
            {
                return;
            }
            SimExtFrame.Bound += ExpireIdleSubscriptions;
            expiryHooked = true;
        }

        private static void ExpireIdleSubscriptions()
        {
            if (routeSubscriptions.Count == 0)
            {
                return;
            }
            List<string> expired = null;
            double now = NowSeconds();
            foreach (KeyValuePair<string, double> entry in routeSubscriptions)
            {
                if (now - entry.Value < IdleSecondsBeforeUnsubscribe)
                {
                    continue;
                }
                if (expired == null)
                {
                    expired = new List<string>();
                }
                expired.Add(entry.Key);
            }
            if (expired == null)
            {
                return;
            }
            foreach (string name in expired)
            {
                routeSubscriptions.Remove(name);
                int idx = SimExtCellProperties.Find(name);
                if (idx >= 0)
                {
                    SimExtCellProperties.Publish(idx, false);
                }
                Debug.Log("[OniFramework] /extcells?published=1: unsubscribed " + name +
                          " after " +
                          IdleSecondsBeforeUnsubscribe.ToString("F0", CultureInfo.InvariantCulture) +
                          " s idle; nothing has asked for it and a published property costs a " +
                          "copy every frame.");
            }
        }

        private static string[] SurfaceNames()
        {
            var names = new string[Surfaces.Length];
            for (int i = 0; i < Surfaces.Length; i++)
            {
                names[i] = Surfaces[i].Name;
            }
            return names;
        }

        // ------------------------------------------------------------------
        // /world and /elements -- the metadata a client needs before the bytes mean anything.
        // ------------------------------------------------------------------

        /// <summary>
        /// Grid geometry, the per-world rectangles, and the surface table itself. A client
        /// fetches this once per attach: without the world rectangles the sunlight texture is
        /// unreadable (it is written per world, not per grid), and without the surface table a
        /// client has to hardcode ids it could have been told.
        ///
        /// The <c>unityFrame</c> field is Unity's own frame counter, NOT a published-sim-frame
        /// counter: the game steps the sim on some frames and not others, so this changes more
        /// often than the arrays do. It is a "has anything moved" signal and nothing stronger.
        /// The sim does not expose its own published-frame count to managed code -- Game.cs
        /// reads <c>numFramesProcessed</c> off the update struct and stores it nowhere.
        /// </summary>
        public static string World()
        {
            if (!Grid.IsInitialized())
            {
                return DebugInspectorServer.Json.Obj(("error", "the grid is not initialized -- is a world loaded?"));
            }

            var worlds = new List<object>();
            if (ClusterManager.Instance != null)
            {
                foreach (WorldContainer w in ClusterManager.Instance.WorldContainers)
                {
                    if (w == null)
                    {
                        continue;
                    }
                    worlds.Add(new DebugInspectorServer.RawJson(DebugInspectorServer.Json.Obj(
                        ("id", w.id),
                        ("name", w.GetProperName()),
                        ("offsetX", w.WorldOffset.x),
                        ("offsetY", w.WorldOffset.y),
                        ("width", w.WorldSize.x),
                        ("height", w.WorldSize.y))));
                }
            }

            var surfaces = new List<object>();
            foreach (Surface s in Surfaces)
            {
                surfaces.Add(new DebugInspectorServer.RawJson(DebugInspectorServer.Json.Obj(
                    ("id", (int)s.Id),
                    ("name", s.Name),
                    ("bytesPerCell", s.BytesPerCell),
                    ("published", Resolve(s.Id) != IntPtr.Zero))));
            }

            return DebugInspectorServer.Json.Obj(
                ("formatVersion", (int)FormatVersion),
                ("width", Grid.WidthInCells),
                ("height", Grid.HeightInCells),
                ("cells", Grid.WidthInCells * Grid.HeightInCells),
                ("unityFrame", Time.frameCount),
                ("worlds", worlds),
                ("surfaces", surfaces));
        }


        /// <summary>
        /// <c>/extelements</c> -- the element-attribute registry on the wire.
        ///
        /// <b>Why this is JSON and <c>/extcells</c> is not.</b> A per-cell property is one value
        /// per cell and the whole registry is 12.7 MB at 636x404, so it is a binary blob and a
        /// hand-written encoder would be the wrong tool twice over. An element attribute is one
        /// value per ELEMENT THAT HAS ONE -- sparse by construction, four entries for the only
        /// attribute a stock custom build has, capped at 64 attributes by the ABI. The whole
        /// registry fits in a few kilobytes of text, so the readable format wins and there is no
        /// new wire format to keep two writers agreeing on.
        ///
        /// <b>A GET HERE MUTATES NOTHING</b>, unlike <c>/extcells?published=1</c> and
        /// <c>/extevents</c>. There is nothing to subscribe to: this registry is not on the
        /// published frame, so reading it costs the game three exports per attribute and no
        /// per-frame work at all. Hence no lease, no handshake, and no idle expiry.
        ///
        /// <b>Values are included by default</b> and <c>values=0</c> drops them. That default is
        /// the opposite of <c>/grid</c>'s, deliberately: there the bytes are the expensive thing
        /// and a client must ask; here they are a handful of floats and a client that has to ask
        /// twice for them has been made to do the sparse-lookup work by hand.
        ///
        /// <b>An element the loaded table has no row for is REPORTED, not dropped.</b> Keys are
        /// SimHashes ids precisely so a value survives the element table being reloaded, so a mod
        /// may hold one for an element this table does not have -- <c>inTable</c> false, with the
        /// hash still there. Dropping it would show a value that exists as absent.
        ///
        /// MUST be called on the game's main thread: it reads <c>ElementLoader</c>.
        /// </summary>
        public static string ExtElements(string query)
        {
            if (!SimExtElementAttributes.CanEnumerate)
            {
                return NoElementAttributeEnumeration();
            }

            List<SimExtElementAttributes.Descriptor> declared = SimExtElementAttributes.Describe();
            if (declared.Count == 0)
            {
                // NOT "there are none". A custom SimDLL always registers sim.molecular_mass
                // natively in the sim's own constructor, so an empty list means either that no
                // sim exists yet or that this build cannot enumerate -- and CanEnumerate only
                // goes false once a call has actually thrown, which the Describe above would
                // have done.
                return SimExtElementAttributes.CanEnumerate
                    ? DebugInspectorServer.Json.Obj(
                        ("error", "no element attributes are registered, which on a custom " +
                                  "SimDLL means there is no sim yet -- sim.molecular_mass is " +
                                  "registered natively in the sim's own constructor"),
                        ("note", "load a world first; SIM_Initialize destroys and rebuilds " +
                                 "every registry, so this is empty between loads"))
                    : NoElementAttributeEnumeration();
            }

            bool withValues =
                DebugInspectorServer.GetQueryParam(query, "values") != "0";

            // One pass over the element table rather than a lookup per key. The table is ~700
            // rows and the keys are a handful, so either would do -- but this also gives the
            // `inTable` answer for free, which a per-key FindElementByHash would have to
            // special-case a null return to get.
            var names = new Dictionary<int, string>();
            if (ElementLoader.elements != null)
            {
                foreach (Element e in ElementLoader.elements)
                {
                    if (e != null)
                    {
                        names[(int)e.id] = ElementName(e);
                    }
                }
            }

            var rows = new List<object>();
            foreach (SimExtElementAttributes.Descriptor d in declared)
            {
                var fields = new List<(string, object)>
                {
                    ("idx", d.DeclaredIndex),
                    ("name", d.Name),
                    ("type", (int)d.Type),
                    ("typeName", ScalarTypeName(d.Type)),
                    ("arity", d.Arity),
                    ("stride", d.Stride),
                    // How many ELEMENTS carry a value. Not how many elements exist, and not the
                    // number of writes -- all three are different numbers and this route carries
                    // all three because a client cannot derive any one from the others.
                    ("valueCount", d.ValueCount),
                    ("firstParty", d.FirstParty),
                    // The only thing that tells "nobody has pushed this yet" from "the pusher
                    // ran and cleared everything", which read identically in valueCount.
                    ("writes", (long)d.Writes),
                };

                if (withValues)
                {
                    var values = new List<object>();
                    foreach (SimHashes id in SimExtElementAttributes.Keys(d.DeclaredIndex))
                    {
                        var components = new List<object>();
                        for (int c = 0; c < d.Arity; c++)
                        {
                            uint bits;
                            if (!SimExtElementAttributes.TryRead(d.DeclaredIndex, id, c, out bits))
                            {
                                // Unreachable through the sim's own storage -- an entry is
                                // created zero-filled across every component -- but a null here
                                // says "unset" rather than inventing a zero, because a stored
                                // zero is a value and the two must not print the same.
                                components.Add(null);
                                continue;
                            }
                            components.Add(DecodeScalar(d.Type, bits));
                        }

                        string name;
                        bool inTable = names.TryGetValue((int)id, out name);
                        values.Add(new DebugInspectorServer.RawJson(
                            DebugInspectorServer.Json.Obj(
                                ("idHash", (int)id),
                                ("id", inTable ? name : null),
                                ("inTable", inTable),
                                ("v", components))));
                    }
                    fields.Add(("values", values));
                }

                rows.Add(new DebugInspectorServer.RawJson(
                    DebugInspectorServer.Json.ObjRaw(fields)));
            }

            return DebugInspectorServer.Json.Obj(
                ("count", rows.Count),
                ("withValues", withValues),
                ("attributes", rows),
                ("note", "keys are SimHashes ids, never element-table indices -- inTable false " +
                         "is a real value for an element the loaded table has no row for"));
        }

        // A readable name for an element, and NOT simply `e.id.ToString()`.
        //
        // FOUND LIVE, on the first real run of this route. `Element.id` is a
        // `SimHashes`, and an enum value with no named member stringifies as its own NUMBER --
        // so four of the 23 elements carrying a molar-mass override came back as
        // `"id": "-1809421574"`, which a client has every reason to print as though it were a
        // name. One of them is NITROGEN, which is not an exotic case at all: its `tag` says
        // Nitrogen and the `SimHashes` enum this assembly compiles against has no member for
        // that hash.
        //
        // `/elements` has the same expression and gets away with it because it publishes `tag`
        // in the next field, so a client there has somewhere to fall back to. This route
        // publishes one name per value, so the fallback has to happen here.
        private static string ElementName(Element e)
        {
            string name = e.id.ToString();
            // An unnamed enum value renders as the signed integer, which is the only way a
            // SimHashes name can begin with a digit or a minus sign.
            if (name.Length > 0 && (name[0] == '-' || (name[0] >= '0' && name[0] <= '9')))
            {
                string tag = e.tag.ToString();
                if (!string.IsNullOrEmpty(tag))
                {
                    return tag;
                }
            }
            return name;
        }

        private static string NoElementAttributeEnumeration()
        {
            return DebugInspectorServer.Json.Obj(
                ("error", "this SimDLL cannot enumerate element attributes; it is either stock " +
                          "or too old"),
                ("note", "SIM_ExtElementAttributeIndex may still resolve a name you already know"));
        }

        private static string ScalarTypeName(ExtScalarType type)
        {
            switch (type)
            {
                case ExtScalarType.Float32: return "f32";
                case ExtScalarType.UInt8: return "u8";
                case ExtScalarType.UInt16: return "u16";
                case ExtScalarType.Int32: return "i32";
                case ExtScalarType.ElementIndex: return "element";
                default: return "type" + (int)type;
            }
        }

        // The bits as the registered type reads them. The sim stores every scalar in a uint32
        // and reinterprets per type on the way out -- bit-cast for f32, the low byte or low two
        // bytes for u8/u16, verbatim for i32 -- and this is the same reinterpretation, spelled
        // on this side because JSON has one number type and the wire cannot carry the tag.
        private static object DecodeScalar(ExtScalarType type, uint bits)
        {
            switch (type)
            {
                case ExtScalarType.Float32:
                    return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                case ExtScalarType.UInt8:
                    return (int)(bits & 0xFF);
                case ExtScalarType.UInt16:
                case ExtScalarType.ElementIndex:
                    return (int)(bits & 0xFFFF);
                case ExtScalarType.Int32:
                    return unchecked((int)bits);
                default:
                    return (long)bits;
            }
        }

        /// <summary>
        /// The element table, indexed the way the sim indexes it. <c>elementIdx</c> is an index
        /// into this and means nothing without it -- an offline tool lifts the same table out of
        /// a corpus, and a live one has no corpus to lift it from.
        /// </summary>
        public static string Elements()
        {
            if (ElementLoader.elements == null)
            {
                return DebugInspectorServer.Json.Obj(("error", "the element table is not loaded yet"));
            }

            var rows = new List<object>();
            foreach (Element e in ElementLoader.elements)
            {
                if (e == null)
                {
                    continue;
                }

                // The byte the SIM's copy of this element holds, which is not the managed
                // one: Sim.Element's constructor starts from (byte)e.state and then ORs in
                // Unstable (8) from a TAG, not from the enum. A consumer
                // that reads stateRaw and tests bit 8 therefore sees no unstable solid at
                // all -- sand included. Published beside the raw byte rather than replacing
                // it, because the two really are different values and a client indexing the
                // sim's element table wants this one.
                byte simState = (byte)e.state;
                if (e.HasTag(GameTags.Unstable))
                {
                    simState |= 8;
                }

                // Sim.Element packs Substance.colour as a Color32 seen little-endian:
                // a<<24 | b<<16 | g<<8 | r, so the LOW byte is red. Passed
                // through unchanged and as a long, because Json.Encode has no uint case and
                // would quote one as a string.
                long colour = 0;
                if (e.substance != null)
                {
                    Color32 c = e.substance.colour;
                    colour = ((long)c.a << 24) | ((long)c.b << 16) | ((long)c.g << 8) | c.r;
                }

                rows.Add(new DebugInspectorServer.RawJson(DebugInspectorServer.Json.Obj(
                    ("idx", (int)e.idx),
                    ("id", e.id.ToString()),
                    // SimHashes as the sim stores it in Element.id -- the value elementIdx
                    // resolves against, and the one a hover readout quotes. The name above
                    // is for people; this is the identity.
                    ("idHash", (int)e.id),
                    ("tag", e.tag.ToString()),
                    ("state", (int)((byte)e.state & Element.StateMask)),
                    ("stateRaw", (int)(byte)e.state),
                    ("simState", (int)simState),
                    ("colour", colour),
                    ("lowTemp", e.lowTemp),
                    ("highTemp", e.highTemp),
                    // The two transition targets, as SIM table indices, with 0xFFFF for
                    // "none" -- the spelling the sim's own element table uses and the one
                    // the replacement SimDLL's sim/conduits.h tests against. Present
                    // so a client can evaluate Klei's SimCheckErrorMap rule itself: its
                    // magenta and cyan cases both gate on `highTempTransition != null` /
                    // `lowTempTransition != null`, and without these two fields a synthesized
                    // table reads every element as having both, which turns the two most
                    // common phase-boundary colours into noise.
                    ("lowTempTransitionIdx",
                        e.lowTempTransition == null ? 0xFFFF : (int)e.lowTempTransition.idx),
                    ("highTempTransitionIdx",
                        e.highTempTransition == null ? 0xFFFF : (int)e.highTempTransition.idx),
                    ("specificHeatCapacity", e.specificHeatCapacity),
                    ("thermalConductivity", e.thermalConductivity))));
            }

            return DebugInspectorServer.Json.Obj(
                ("count", rows.Count),
                ("stateMask", (int)Element.StateMask),
                // Element.State's own order, which is NOT the intuitive one: Solid is the
                // HIGH value, not the low one. Named here so a client reads the mask rather
                // than assuming. The bits above the mask (Unbreakable 4, Unstable 8,
                // TemperatureInsulated 16) survive in stateRaw -- except Unstable, which the
                // managed enum does not carry at all; read simState for that one.
                ("states", new[] { "Vacuum", "Gas", "Liquid", "Solid" }),
                ("elements", rows));
        }
    }
}
