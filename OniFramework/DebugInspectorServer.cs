using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace OniFramework
{
    /// <summary>
    /// An HTTP debug server for development: inspect live game state with a plain <c>curl</c>
    /// call instead of reading tooltip numbers by hand. Any mod gets it by calling
    /// <see cref="Start"/>; nothing starts it by default.
    ///
    /// ARCHITECTURE: a background thread accepts TCP connections and parses just enough of an
    /// HTTP/1.1 request line to route it (no headers/body parsing -- this is a debug tool, not a
    /// real web server). Actual game-state reads (<c>Grid</c>, <c>Game.Instance</c>, component
    /// fields) are NOT safe to touch from a background thread -- same constraint
    /// <c>sim/simdll.cpp</c>'s own <c>WaitIdle()</c> exists for, just the other direction (here
    /// the BACKGROUND thread is the one that must wait, and the GAME's main thread is the one
    /// that does the real work). So a request is queued and the accepting thread blocks on a
    /// <see cref="ManualResetEventSlim"/> until <see cref="DebugInspectorPump"/> (a real
    /// MonoBehaviour, driven by Unity's own per-frame Update -- the same "run the queue once a
    /// frame from the main thread" shape)
    /// drains the queue and fills in the response. A stuck main thread (e.g. paused on a modal
    /// dialog) means a stuck request, not a crash -- bounded by <see cref="RequestTimeoutMs"/>.
    ///
    /// SCOPE: binds <see cref="IPAddress.Any"/>, not strict loopback, so that a WSL2 guest can
    /// reach a game running on its Windows host: WSL2 cannot reach a socket Windows binds to its
    /// OWN 127.0.0.1 (a different loopback interface), but it can reach one bound to any
    /// interface through the WSL vEthernet gateway address. Windows Firewall's default inbound
    /// rules block external connections. There is NO AUTHENTICATION: this is development tooling
    /// for a trusted single machine, and a mod must never start it in a build meant for
    /// players.
    /// </summary>
    public static class DebugInspectorServer
    {
        private const int RequestTimeoutMs = 5000;

        private static TcpListener listener;
        private static Thread acceptThread;
        private static volatile bool running;

        /// <summary>
        /// One in-flight request. A reference type (not a struct) since the accept thread must
        /// observe the SAME instance's <see cref="Response"/> field after
        /// <see cref="DebugInspectorPump"/> writes it on the main thread.
        /// </summary>
        public sealed class Ticket
        {
            public string Path;
            public string Query;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

            /// <summary>The JSON body, used when <see cref="Binary"/> is null.</summary>
            public string Response;

            /// <summary>
            /// A raw body, set instead of <see cref="Response"/> by a binary route
            /// (<see cref="DebugBulkRoutes"/>). It is a POOLED buffer and may be longer than
            /// the response -- <see cref="BinaryLength"/> is the valid prefix -- and it belongs
            /// to the pool again the moment the write finishes, which is why only
            /// <see cref="HandleClient"/> ever returns it.
            /// </summary>
            public byte[] Binary;

            /// <summary>Valid bytes in <see cref="Binary"/>. Meaningless when it is null.</summary>
            public int BinaryLength;

            /// <summary>Response content type; null means the JSON default.</summary>
            public string ContentType;
        }

        private static readonly ConcurrentQueue<Ticket> queue = new ConcurrentQueue<Ticket>();

        /// <summary>
        /// Starts the server on <paramref name="port"/> if it isn't already running. Safe to
        /// call every time a mod's Game.OnSpawn fires (e.g. across a save reload) -- a second
        /// call while already running is a no-op, not a second listener/port conflict.
        /// </summary>
        public static void Start(int port)
        {
            if (running)
            {
                return;
            }
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                running = true;
                acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DebugInspectorAccept" };
                acceptThread.Start();
                Debug.Log($"[OniFramework] DebugInspectorServer listening on port {port} (all interfaces -- " +
                          $"use http://127.0.0.1:{port}/ from Windows itself, or the WSL vEthernet gateway " +
                          "IP -- `ip route | grep default` inside WSL -- from a WSL shell).");
            }
            catch (Exception e)
            {
                // A busy port (two instances launched, or a leftover from an unclean previous
                // exit) should degrade to "no debug server this run," never crash the mod that
                // called Start().
                Debug.LogWarning($"[OniFramework] DebugInspectorServer failed to start on port {port}: {e.Message}");
                running = false;
            }
        }

        public static void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { /* best-effort */ }
        }

        private static void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    // Stop() calling listener.Stop() lands here as an exception on the blocking
                    // Accept call -- the expected way this loop ends, not a real error.
                    break;
                }
                // One thread per connection -- a debug tool talking to one curl call at a time
                // needs no pooling.
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                string requestLine;
                try
                {
                    // Only the request line matters (route parsing) -- headers/body are read and
                    // discarded up to the blank line so the client's own connection doesn't hang
                    // waiting on us, but never inspected.
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    requestLine = reader.ReadLine();
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) { /* discard headers */ }
                }
                catch
                {
                    return;
                }

                if (string.IsNullOrEmpty(requestLine))
                {
                    return;
                }

                // "GET /path?query HTTP/1.1"
                string[] parts = requestLine.Split(' ');
                string rawTarget = parts.Length >= 2 ? parts[1] : "/";
                string path = rawTarget;
                string query = "";
                int qIdx = rawTarget.IndexOf('?');
                if (qIdx >= 0)
                {
                    path = rawTarget.Substring(0, qIdx);
                    query = rawTarget.Substring(qIdx + 1);
                }

                var ticket = new Ticket { Path = path, Query = query };
                queue.Enqueue(ticket);

                string body;
                byte[] binary = null;
                int binaryLength = 0;
                int statusCode;
                if (ticket.Done.Wait(RequestTimeoutMs))
                {
                    binary = ticket.Binary;
                    binaryLength = ticket.BinaryLength;
                    body = ticket.Response ?? "{\"error\":\"handler returned null\"}";
                    statusCode = 200;
                }
                else
                {
                    // A timed-out ticket may still be filled in later by the pump, which
                    // would be writing into the very buffer this thread would be handing back.
                    // So a timeout drops that buffer to the GC instead of pooling it: one
                    // missed reuse, versus two owners of live memory.
                    body = "{\"error\":\"timed out waiting for the game's main thread (is it paused on a dialog?)\"}";
                    statusCode = 504;
                }

                // A binary route answers with bytes the main thread already filled; every
                // other route answers with JSON text. The two differ only here and in the
                // content type -- the framing, the timeout and the close are shared.
                byte[] bodyBytes;
                int bodyLength;
                string contentType;
                if (binary != null)
                {
                    bodyBytes = binary;
                    bodyLength = binaryLength;
                    contentType = ticket.ContentType ?? "application/octet-stream";
                }
                else
                {
                    bodyBytes = Encoding.UTF8.GetBytes(body);
                    bodyLength = bodyBytes.Length;
                    contentType = "application/json; charset=utf-8";
                }

                string header =
                    $"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Timeout")}\r\n" +
                    $"Content-Type: {contentType}\r\n" +
                    $"Content-Length: {bodyLength}\r\n" +
                    "Connection: close\r\n\r\n";
                try
                {
                    byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    stream.Write(bodyBytes, 0, bodyLength);
                    stream.Flush();
                }
                catch
                {
                    // Client already gone -- nothing to do.
                }
                finally
                {
                    // The buffer is the pool's again only now: it was live for the whole
                    // write, and a client that hung up mid-write must not leak it.
                    DebugBulkRoutes.Release(binary);
                }
            }
        }

        /// <summary>
        /// Drains every request queued since the last call and answers each one, routing by
        /// path. MUST be called from the main/Unity thread -- <see cref="DebugInspectorPump"/>
        /// is the real caller, once per frame. Bounded per call by whatever is actually queued;
        /// a debug tool being polled interactively never queues enough per frame for this to be
        /// a real cost.
        /// </summary>
        public static void ProcessPending()
        {
            while (queue.TryDequeue(out Ticket ticket))
            {
                try
                {
                    // Binary routes get first refusal, because they answer with bytes rather
                    // than with a string and so cannot go through Route()'s return type. One
                    // that owns the path still answers a BAD request in JSON, which is why
                    // this is a handled/not-handled question rather than a null check.
                    if (!DebugBulkRoutes.TryHandleBinary(ticket))
                    {
                        ticket.Response = Route(ticket.Path, ticket.Query);
                    }
                }
                catch (Exception e)
                {
                    ticket.Binary = null;
                    ticket.BinaryLength = 0;
                    ticket.ContentType = null;
                    ticket.Response = Json.Obj(("error", e.ToString()));
                }
                finally
                {
                    ticket.Done.Set();
                }
            }
        }

        // ------------------------------------------------------------------
        // Routing. Deliberately a plain if-chain, not a registry/attribute system -- a handful
        // of routes for a debug tool doesn't earn that abstraction yet. Add a new route here as
        // a new `if` when a new inspection need shows up; nothing else needs touching.
        // ------------------------------------------------------------------
        private static string Route(string path, string query)
        {
            string[] seg = path.Trim('/').Split('/');

            if (path == "/" || path == "/help")
            {
                return Json.Obj(
                    ("routes", new[]
                    {
                        "/ping",
                        "/cell/{idx}",
                        "/pos/{x}/{y}",
                        "/building/{cell}",
                        "/conduit/{cell}?type=gas|liquid",
                        "/pipenetwork/{cell}?type=gas|liquid",
                        "/find/{TypeName}",
                        "/placement/{PrefabId}/{cell}",
                        "/world",
                        "/elements",
                        "/grid?arrays=mass,temperature|all",
                        "/extcells (binary; ?list=1 for a JSON summary)",
                        "/extevents (binary; a bare GET lists the declared streams)",
                        "/extelements?values=0",
                        "/profile (live per-kernel timings; ?arm=1 starts, ?arm=0 stops)",
                        "/simcheck (Klei's SimCheckErrorMap over the live grid; ?limit=N ?world=all)",
                        "/overlaycache (?on=1 ?off=1 ?verify=1 ?cap=N)"
                    }));
            }

            if (seg.Length == 1 && seg[0] == "ping")
            {
                return Json.Obj(("ok", true), ("pong", true), ("frame", Time.frameCount));
            }

            if (seg.Length == 2 && seg[0] == "cell" && int.TryParse(seg[1], out int cellIdx))
            {
                return DumpCell(cellIdx);
            }

            if (seg.Length == 3 && seg[0] == "pos" &&
                int.TryParse(seg[1], out int x) && int.TryParse(seg[2], out int y))
            {
                int cell = Grid.XYToCell(x, y);
                return DumpCell(cell);
            }

            if (seg.Length == 2 && seg[0] == "building" && int.TryParse(seg[1], out int bCell))
            {
                return DumpBuilding(bCell);
            }

            if (seg.Length == 2 && seg[0] == "symbols")
            {
                return DumpSymbols(seg[1]);
            }

            if (seg.Length == 3 && seg[0] == "placement" && int.TryParse(seg[2], out int placeCell))
            {
                return DumpPlacement(seg[1], placeCell);
            }

            if (seg.Length == 2 && seg[0] == "statusitems" && int.TryParse(seg[1], out int sCell))
            {
                return DumpStatusItems(sCell);
            }

            if (seg.Length == 2 && seg[0] == "conduit" && int.TryParse(seg[1], out int cCell))
            {
                string type = GetQueryParam(query, "type") ?? "gas";
                return DumpConduit(cCell, type);
            }

            if (seg.Length == 2 && seg[0] == "pipenetwork" && int.TryParse(seg[1], out int pCell))
            {
                string pType = GetQueryParam(query, "type") ?? "gas";
                return DumpPipeNetwork(pCell, pType);
            }

            if (seg.Length == 2 && seg[0] == "find")
            {
                return DumpFind(seg[1]);
            }

            // The two metadata halves of the binary bulk route -- JSON, so they live on this
            // side of the split; the bytes themselves are DebugBulkRoutes.TryHandleBinary.
            if (seg.Length == 1 && seg[0] == "world")
            {
                return DebugBulkRoutes.World();
            }

            if (seg.Length == 1 && seg[0] == "elements")
            {
                return DebugBulkRoutes.Elements();
            }

            // Registry 2, which /elements cannot carry: that route is Klei's managed Element
            // fields, and an extension attribute is by definition a property Klei's table has
            // no field for.
            if (seg.Length == 1 && seg[0] == "extelements")
            {
                return DebugBulkRoutes.ExtElements(query);
            }

            // Where the frame went, live. The one route here that can CHANGE something -- the
            // profiler's millisecond half has to be armed before it measures anything, and a
            // debug tool that could read the timings but not switch them on would be asking the
            // user to go and press the backtick key in the game window, which is the manual
            // relay this whole server exists to remove.
            if (seg.Length == 1 && seg[0] == "profile")
            {
                return DumpProfile(query);
            }

            // Klei's own cell validator, over the live grid. See DumpSimCheck.
            if (seg.Length == 1 && seg[0] == "simcheck")
            {
                return DumpSimCheck(query);
            }

            // The overlay property-texture cache. Same reasoning as /profile: a switch that can
            // only be flipped from inside the game window is a switch an automated A/B cannot
            // use, and this one has to be flipped mid-run to measure what it is worth.
            if (seg.Length == 1 && seg[0] == "overlaycache")
            {
                return DumpOverlayCache(query);
            }

            return Json.Obj(("error", "unknown route: " + path), ("see", "/help"));
        }

        /// <summary>
        /// Asks the game whether one prefab could be placed at one cell, and shows what is
        /// already standing there on every object layer.
        ///
        /// WHY THIS IS A ROUTE AND NOT A READ OF THE GAME'S CODE. `BuildingDef.IsAreaClear` only
        /// tests the def's OWN `ObjectLayer` and `TileLayer` (plus AttachableBuilding and Gantry
        /// for a Building-layer def), so reading it says a normal building and a conduit should
        /// be able to share a cell. That is a claim about a code path with several other gates
        /// in front of it, and the project's rule is that a claim like that gets measured before
        /// anything is built on top of it.
        ///
        /// `fail_reason` is the game's own string, so a refusal names itself rather than having
        /// to be inferred from which check is missing.
        /// </summary>
        private static string DumpPlacement(string prefabId, int cell)
        {
            BuildingDef def = Assets.GetBuildingDef(prefabId);
            if (def == null)
            {
                return Json.Obj(("error", "no such building"), ("id", prefabId));
            }

            if (!Grid.IsValidCell(cell))
            {
                return Json.Obj(("error", "invalid cell"), ("id", prefabId), ("cell", cell));
            }

            bool valid = def.IsValidPlaceLocation(null, cell, Orientation.Neutral,
                false, out string failReason);

            var occupants = new System.Collections.Generic.List<string>();
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                GameObject occupant = Grid.Objects[cell, layer];
                if (occupant == null)
                {
                    continue;
                }

                occupants.Add(Json.Obj(
                    ("layer", ((ObjectLayer)layer).ToString()),
                    ("index", layer),
                    ("name", occupant.name)));
            }

            Grid.CellToXY(cell, out int px, out int py);

            return Json.Obj(
                ("id", prefabId),
                ("cell", cell),
                ("x", px),
                ("y", py),
                ("valid", valid),
                ("failReason", failReason),
                ("objectLayer", def.ObjectLayer.ToString()),
                ("tileLayer", def.TileLayer.ToString()),
                ("isTilePiece", def.IsTilePiece),
                ("isKAnimTile", def.isKAnimTile),
                ("buildLocationRule", def.BuildLocationRule.ToString()),
                ("width", def.WidthInCells),
                ("height", def.HeightInCells),
                ("occupants", new RawJson("[" + string.Join(",", occupants) + "]")));
        }

        /// <summary>
        /// Per-symbol pixel statistics for one building's kanim build, which is the measurement
        /// <see cref="SymbolPaintAnalysis"/>'s saturation threshold has to be read off rather
        /// than guessed at. Also reports whether the atlas was CPU-readable, since that decides
        /// which of the analyser's two pixel paths a given install takes.
        ///
        /// Answers off the PREFAB, not off a placed instance, so a building can be measured
        /// whether or not the colony happens to contain one.
        /// </summary>
        private static string DumpSymbols(string prefabId)
        {
            BuildingDef def = Assets.GetBuildingDef(prefabId);
            if (def == null || def.BuildingComplete == null)
            {
                return Json.Obj(("error", "no such building"), ("id", prefabId));
            }

            KBatchedAnimController kbac = def.BuildingComplete.GetComponent<KBatchedAnimController>();
            if (kbac == null || kbac.AnimFiles == null || kbac.AnimFiles.Length == 0 || kbac.AnimFiles[0] == null)
            {
                return Json.Obj(("error", "no anim controller"), ("id", prefabId));
            }

            KAnimFileData data = kbac.AnimFiles[0].GetData();
            KAnim.Build build = data != null ? data.build : null;
            if (build == null)
            {
                return Json.Obj(("error", "no build"), ("id", prefabId), ("anim", kbac.AnimFiles[0].name));
            }

            KBatchGroupData group = KAnimBatchManager.Instance().GetBatchGroupData(build.batchTag);
            System.Collections.Generic.List<SymbolPaintAnalysis.SymbolStats> stats =
                SymbolPaintAnalysis.Analyse(build, group, out string error);

            var rows = new System.Collections.Generic.List<string>();
            int paintable = 0;
            if (stats != null)
            {
                foreach (SymbolPaintAnalysis.SymbolStats s in stats)
                {
                    if (s.Paintable) paintable++;
                    rows.Add(Json.Obj(
                        ("symbol", s.Name),
                        ("hash", s.Hash),
                        ("sampled", s.Sampled),
                        ("opaque", s.Opaque),
                        ("r", (float)Math.Round(s.MeanR, 3)),
                        ("g", (float)Math.Round(s.MeanG, 3)),
                        ("b", (float)Math.Round(s.MeanB, 3)),
                        ("sat", (float)Math.Round(s.MeanSaturation, 3)),
                        ("val", (float)Math.Round(s.MeanValue, 3)),
                        ("paintable", s.Paintable)));
                }
            }

            Texture2D probe = build.textureCount > 0 ? build.GetTexture(0, group) : null;

            return Json.Obj(
                ("id", prefabId),
                ("anim", kbac.AnimFiles[0].name),
                ("build", build.name),
                ("batchTag", build.batchTag.ToString()),
                ("atlasReadable", probe != null && probe.isReadable),
                ("atlasSize", probe != null ? probe.width + "x" + probe.height : "none"),
                ("threshold", SymbolPaintAnalysis.SaturationThreshold),
                ("symbols", stats != null ? stats.Count : 0),
                ("paintable", paintable),
                ("error", error),
                ("rows", new RawJson("[" + string.Join(",", rows) + "]")));
        }

        /// <summary>
        /// Reads and switches <see cref="OverlayTextureCache"/>.
        ///
        /// <c>?verify=1</c> arms the per-strip comparison and, by design, stops anything from
        /// actually being skipped: the point of that mode is to find out whether the detector
        /// would have been wrong, which cannot be learned from a pass that did not run.
        /// </summary>
        private static string DumpOverlayCache(string query)
        {
            string cap = GetQueryParam(query, "cap");
            if (cap != null && int.TryParse(cap, out int capFrames) && capFrames > 0)
            {
                OverlayTextureCache.MaxSkippedFrames = capFrames;
            }

            string verify = GetQueryParam(query, "verify");
            if (verify == "1")
            {
                OverlayTextureCache.InstallVerify();
            }
            else if (verify == "0")
            {
                // The postfix stays patched -- Harmony cannot cheaply remove one patch from one
                // method -- but it does nothing once Verify is false, because the prefix stops
                // publishing a pass for it to compare against.
                OverlayTextureCache.Verify = false;
            }

            if (GetQueryParam(query, "on") == "1")
            {
                OverlayTextureCache.Enabled = true;
            }

            if (GetQueryParam(query, "off") == "1")
            {
                OverlayTextureCache.Enabled = false;
            }

            return Json.Obj(
                ("enabled", OverlayTextureCache.Enabled),
                ("verify", OverlayTextureCache.Verify),
                ("maxSkippedFrames", OverlayTextureCache.MaxSkippedFrames),
                ("skipped", OverlayTextureCache.Skipped),
                ("ran", OverlayTextureCache.Ran),
                ("judged", OverlayTextureCache.Judged),
                ("falseClean", OverlayTextureCache.FalseClean),
                ("refillsChecked", OverlayTextureCache.RefillsChecked),
                ("refillWrong", OverlayTextureCache.RefillWrong),
                ("digestSweeps", OverlayTextureCache.DigestSweeps),
                ("digestCleanSweeps", OverlayTextureCache.DigestCleanSweeps),
                ("digestMillis", OverlayTextureCache.DigestMillis),
                ("text", OverlayTextureCache.StatusText()));
        }

        /// <summary>
        /// Reads one <c>name=value</c> pair out of a raw query string. Internal rather than
        /// private since <see cref="DebugBulkRoutes"/> parses its own query and there is no
        /// reason for a second copy of this to exist.
        /// </summary>
        internal static string GetQueryParam(string query, string name)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }
                if (pair.Substring(0, eq) == name)
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Handlers -- each one reuses the real facade/vanilla API this project already built,
        // never duplicating physics/formulas. This tool only ever READS state.
        // ------------------------------------------------------------------

        private static string DumpCell(int cell)
        {
            if (!Grid.IsValidCell(cell))
            {
                return Json.Obj(("error", "invalid cell"), ("cell", cell));
            }

            var fields = new System.Collections.Generic.List<(string, object)>
            {
                ("cell", cell),
                ("vanillaElement", Grid.Element[cell]?.tag.ToString() ?? "(none)"),
                ("vanillaMassKg", Grid.Mass[cell]),
                ("vanillaTemperatureK", Grid.Temperature[cell]),
                ("roomOwnedRaw", GasMixtureFacade.DebugRoomOwnedRaw(cell))
            };

            if (GasMixtureFacade.TryGetDominant(cell, out ushort dominantIdx, out float totalMass))
            {
                fields.Add(("mixtureDominantElement", ElementLoader.elements[dominantIdx].tag.ToString()));
                fields.Add(("mixtureTotalMassKg", totalMass));
                GasMixtureFacade.GasComponent[] composition = GasMixtureFacade.TryGetComposition(cell);
                var comp = new System.Collections.Generic.List<object>();
                foreach (GasMixtureFacade.GasComponent c in composition)
                {
                    comp.Add(Json.Obj(("element", ElementLoader.elements[c.ElementIdx].tag.ToString()), ("massKg", c.MassKg)));
                }
                fields.Add(("mixtureComposition", comp));
            }
            else
            {
                fields.Add(("mixture", "(no gas-mixture state at this cell)"));
            }

            if (GasMixtureFacade.TryGetPressure(cell, out float pressurePa))
            {
                fields.Add(("mixturePressurePa", pressurePa));
            }

            return Json.Obj(fields.ToArray());
        }

        /// <summary>
        /// The live per-kernel profile: where the simulation frame actually goes, in this
        /// colony, right now. <see cref="SimProfile"/> is the facade; this only shapes it into
        /// JSON.
        ///
        /// <b>Read <c>enabled</c> before reading any duration.</b> The millisecond half only
        /// counts while armed, so every <c>msPerFrame</c> is 0 until somebody arms it -- with
        /// <c>?arm=1</c> here, or with the backtick key in the game. The census fields
        /// (<c>examined</c>, <c>changes</c>, <c>invocations</c>) count either way, which is why
        /// a row can honestly report millions of cells and no time at all.
        ///
        /// <b><c>?arm=1</c> zeroes both halves</b> unless the profiler is already armed, in
        /// which case it is a no-op and somebody else's window survives. <c>?arm=0</c> stops the
        /// clock without clearing what it measured and without writing the log-file report the
        /// backtick key writes.
        /// </summary>
        /// <summary>
        /// Klei's <c>SimCheckErrorMap</c> overlay, as numbers, over the live grid -- plus the
        /// cells behind the numbers.
        ///
        /// WHY A ROUTE AND NOT A SCREENSHOT. The overlay itself has existed all along, in the
        /// DevTools' Sim Debug window, and reading it means a human launching the game, opening
        /// an ImGui panel, picking a mode and describing what they see. A route makes the same
        /// question a <c>curl</c> call that an automated rig can ask at the end of every run.
        ///
        /// IT CALLS KLEI'S FUNCTION, IT DOES NOT REIMPLEMENT IT. The colour comes out of
        /// <c>SimDebugView.getColourFuncs</c>, the same dictionary the overlay itself indexes, so
        /// this cannot drift from what the game draws.
        ///
        /// WHAT RED MEANS. NaN mass or temperature, mass over 10 000 kg, temperature over
        /// 10 000 K, or a non-vacuum cell under 10 K -- and **the world border is 20 000 kg a
        /// cell**, so a sealed world is red all the way round and that is not a fault. The
        /// samples are what makes the number usable: they name the element, so border cells are
        /// recognisable at a glance. Yellow (vacuum holding a temperature) and blue (vacuum
        /// holding mass) have no such excuse and are the two worth alerting on.
        /// </summary>
        private static string DumpSimCheck(string query)
        {
            if (SimDebugView.Instance == null || Grid.CellCount == 0)
            {
                return Json.Obj(
                    ("error", "no live grid"),
                    ("why", "SimDebugView.Instance is null or no world is loaded -- this route "
                        + "needs a game, not the main menu"));
            }

            var funcs = AccessColourFuncs();
            if (funcs == null)
            {
                return Json.Obj(
                    ("error", "SimDebugView.getColourFuncs is not readable"),
                    ("why", "the field was renamed or removed by a game update"));
            }

            Func<SimDebugView, int, Color> errorMap;
            Func<SimDebugView, int, Color> stateChange;
            funcs.TryGetValue(SimDebugView.OverlayModes.SimCheckErrorMap, out errorMap);
            funcs.TryGetValue(SimDebugView.OverlayModes.StateChange, out stateChange);
            if (errorMap == null)
            {
                return Json.Obj(("error", "no SimCheckErrorMap colour function in this build"));
            }

            string limitText = GetQueryParam(query, "limit");
            int limit;
            if (string.IsNullOrEmpty(limitText) || !int.TryParse(limitText, out limit))
            {
                limit = 20;
            }
            bool allWorlds = GetQueryParam(query, "world") == "all";
            int activeWorld = ClusterManager.Instance != null
                ? ClusterManager.Instance.activeWorldId : -1;

            // POSITIVE CONTROL, `?control=1`. Yellow and blue are the two classes worth alerting
            // on, and a healthy world rarely has one, so the branch that samples them would never
            // run: a clean "no yellow, no blue" is indistinguishable from a route that cannot see
            // them. The control borrows two empty cells (vacuum, 0 kg, 0 K --
            // Klei's gray), writes a temperature into one and a mass into the other through the
            // published arrays, scans, and restores both in a finally. It all happens inside this
            // one main-thread call, and the sim publishes into these arrays only between frames,
            // so nothing else can see the borrowed values. The two cells ARE in the counts and the
            // samples; the "control" row names them.
            bool control = GetQueryParam(query, "control") == "1";
            int yellowCell = -1, blueCell = -1;
            float yellowTempWas = 0f, blueMassWas = 0f;
            string yellowSeen = null, blueSeen = null;
            if (control)
            {
                for (int cell = 0; cell < Grid.CellCount && blueCell < 0; cell++)
                {
                    if (!allWorlds && activeWorld >= 0 && Grid.WorldIdx[cell] != activeWorld)
                    {
                        continue;
                    }
                    Element e = Grid.Element[cell];
                    if (e == null || !e.IsVacuum || Grid.Mass[cell] != 0f
                        || Grid.Temperature[cell] != 0f)
                    {
                        continue;
                    }
                    if (yellowCell < 0) yellowCell = cell;
                    else blueCell = cell;
                }
                if (blueCell < 0)
                {
                    return Json.Obj(
                        ("error", "control: fewer than two gray cells to borrow"),
                        ("why", "the control needs two vacuum cells holding 0 kg at 0 K in the "
                            + "scanned world; this one has " + (yellowCell < 0 ? "none" : "one")));
                }
                unsafe
                {
                    yellowTempWas = Grid.temperature[yellowCell];
                    blueMassWas = Grid.mass[blueCell];
                    Grid.temperature[yellowCell] = 300f;
                    Grid.mass[blueCell] = 1f;
                }
            }

            var counts = new System.Collections.Generic.Dictionary<string, int>();
            var vacuumSamples = new System.Collections.Generic.List<object>();
            var redSamples = new System.Collections.Generic.List<object>();
            int stateChangeCells = 0;
            int scanned = 0;
            try
            {
            for (int cell = 0; cell < Grid.CellCount; cell++)
            {
                if (!allWorlds && activeWorld >= 0 && Grid.WorldIdx[cell] != activeWorld)
                {
                    continue;
                }
                scanned++;
                string name = ClassName(errorMap(SimDebugView.Instance, cell));
                int had;
                counts[name] = counts.TryGetValue(name, out had) ? had + 1 : 1;
                if (stateChange != null && stateChange(SimDebugView.Instance, cell).r >= 0.5f)
                {
                    stateChangeCells++;
                }
                // Samples are drawn from the three classes that can be a real fault, and the
                // scan does not stop at the cap: a count is only useful if it is the whole
                // count, and stopping early would make "red 4383" mean "red, at least 20".
                //
                // YELLOW AND BLUE ARE KEPT SEPARATELY AND ALWAYS WIN. Measured on the first live
                // call: every one of ten samples was cell 0..9 of the world border,
                // because the scan starts at cell 0 and a sealed world's first row is 20 000 kg
                // of Unobtanium. One list filled in scan order therefore reports the one class
                // that has an innocent explanation and hides the two that do not. Red samples
                // fill whatever room is left after them.
                if (name == "yellow" || name == "blue")
                {
                    if (vacuumSamples.Count < limit) vacuumSamples.Add(SampleCell(cell, name));
                }
                else if (name == "red" && redSamples.Count < limit)
                {
                    redSamples.Add(SampleCell(cell, name));
                }
                if (cell == yellowCell) yellowSeen = name;
                if (cell == blueCell) blueSeen = name;
            }
            }
            finally
            {
                if (control)
                {
                    unsafe
                    {
                        Grid.temperature[yellowCell] = yellowTempWas;
                        Grid.mass[blueCell] = blueMassWas;
                    }
                }
            }

            var rows = new System.Collections.Generic.List<(string, object)>
            {
                ("cells", scanned),
                ("world", allWorlds ? "all" : activeWorld.ToString(CultureInfo.InvariantCulture)),
                ("statechangeCells", stateChangeCells),
                ("note", "red includes the 20 000 kg neutronium world border; yellow and blue "
                    + "(vacuum holding a temperature or mass) are the two with no innocent cause"),
            };
            if (control)
            {
                bool restored = Grid.Temperature[yellowCell] == yellowTempWas
                    && Grid.Mass[blueCell] == blueMassWas;
                rows.Add(("control", new RawJson(Json.Obj(
                    ("yellowCell", yellowCell),
                    ("yellowWrote", "temperature 300 K"),
                    ("yellowClass", yellowSeen ?? "(not scanned)"),
                    ("blueCell", blueCell),
                    ("blueWrote", "mass 1 kg"),
                    ("blueClass", blueSeen ?? "(not scanned)"),
                    ("pass", yellowSeen == "yellow" && blueSeen == "blue" && restored),
                    ("restored", restored)))));
            }
            foreach (var kv in counts)
            {
                rows.Add((kv.Key, kv.Value));
            }
            var samples = new System.Collections.Generic.List<object>(vacuumSamples);
            foreach (object r in redSamples)
            {
                if (samples.Count >= limit) break;
                samples.Add(r);
            }
            rows.Add(("samples", samples.ToArray()));
            return Json.ObjRaw(rows);
        }

        /// <summary>
        /// One sample row. Returned as <see cref="RawJson"/> and not as a string: a string
        /// handed to the array writer comes back QUOTED, so the first live call produced an
        /// array of JSON-escaped text that no client could index into. Measured.
        /// </summary>
        private static RawJson SampleCell(int cell, string className)
        {
            Element e = Grid.Element[cell];
            Vector2I xy = Grid.CellToXY(cell);
            return new RawJson(Json.Obj(
                ("cell", cell),
                ("x", xy.x),
                ("y", xy.y),
                ("class", className),
                ("element", e == null ? "(null)" : e.id.ToString()),
                ("mass", Grid.Mass[cell]),
                ("temperature", Grid.Temperature[cell])));
        }

        /// <summary>The overlay's own colour-function table, or null if the field has moved.</summary>
        private static System.Collections.Generic.Dictionary<HashedString, Func<SimDebugView, int, Color>>
            AccessColourFuncs()
        {
            try
            {
                FieldInfo f = typeof(SimDebugView).GetField(
                    "getColourFuncs", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null)
                {
                    return null;
                }
                return f.GetValue(SimDebugView.Instance)
                    as System.Collections.Generic.Dictionary<HashedString, Func<SimDebugView, int, Color>>;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Klei's function returns one of eight exact <see cref="Color"/> constants, so the name
        /// comes back by comparing against them rather than by reimplementing the rule. An
        /// unexpected colour is reported as its own channels instead of being forced into a
        /// bucket -- a game update that adds a ninth case should read as a new class, not as a
        /// miscount of an old one.
        /// </summary>
        private static string ClassName(Color c)
        {
            if (c == Color.black) return "black";
            if (c == Color.red) return "red";
            if (c == Color.yellow) return "yellow";
            if (c == Color.blue) return "blue";
            if (c == Color.gray) return "gray";
            if (c == Color.green) return "green";
            if (c == Color.magenta) return "magenta";
            if (c == Color.cyan) return "cyan";
            return string.Format(CultureInfo.InvariantCulture, "rgb({0:0.##},{1:0.##},{2:0.##})",
                c.r, c.g, c.b);
        }

        private static string DumpProfile(string query)
        {
            string arm = GetQueryParam(query, "arm");
            bool armChanged = false;
            bool wasArmed = false;
            if (!string.IsNullOrEmpty(arm))
            {
                bool want = arm != "0" && arm != "false";
                wasArmed = SimProfile.SetArmed(want);
                armChanged = true;
            }

            SimProfile.Summary summary;
            SimProfile.Slot[] slots;
            if (!SimProfile.TryRead(out summary, out slots))
            {
                return Json.Obj(
                    ("error", "no live profile"),
                    ("why", "this SimDLL does not export SIM_DebugProfile -- a stock SimDLL, "
                        + "or a custom build too old to have it"),
                    ("simVersion", SimVersion.Version ?? "(stock)"));
            }

            var rows = new System.Collections.Generic.List<object>();
            foreach (SimProfile.Slot s in slots)
            {
                // A row nothing has entered is dropped rather than published as a line of
                // zeroes: the table is 18 slots wide and a world with no disease, no radiation
                // and no element chunks leaves most of it untouched every frame.
                if (s.Calls == 0 && s.Invocations == 0)
                {
                    continue;
                }
                var f = new System.Collections.Generic.List<(string, object)>
                {
                    ("kernel", s.Name),
                    ("msTotal", s.Milliseconds),
                    ("msPerFrame", s.MillisecondsPerFrame(summary.Frames)),
                    ("calls", s.Calls),
                    ("examined", s.Examined),
                    ("skipped", s.Skipped),
                    ("changes", s.Changes),
                    ("invocations", s.Invocations),
                    ("cellSweep", s.IsCellSweep)
                };
                if (s.IsCellSweep)
                {
                    f.Add(("budgetPerInvocation", s.BudgetPerInvocation));
                    f.Add(("percentOfBound", s.BudgetPercent));
                    f.Add(("withinBudget", s.WithinBudget));
                }
                rows.Add(Json.Obj(f.ToArray()));
            }

            var top = new System.Collections.Generic.List<(string, object)>
            {
                ("enabled", summary.Enabled),
                ("frames", summary.Frames),
                ("regionCells", summary.RegionCells),
                ("regionCellsInclusive", summary.RegionCellsInclusive),
                ("gridCells", summary.GridCells),
                ("regions", summary.Regions),
                ("outsideAnySweep", summary.OutsideAnySweep),
                ("kernels", rows)
            };
            if (armChanged)
            {
                top.Add(("armWasAlready", wasArmed));
            }
            if (!summary.Enabled)
            {
                top.Add(("note", "the profiler is DISARMED, so every duration is 0. The census "
                    + "fields count regardless. Arm with /profile?arm=1."));
            }
            return Json.Obj(top.ToArray());
        }

        private static string DumpBuilding(int cell)
        {
            if (!Grid.IsValidCell(cell))
            {
                return Json.Obj(("error", "invalid cell"), ("cell", cell));
            }
            GameObject go = Grid.Objects[cell, (int)ObjectLayer.Building];
            if (go == null)
            {
                return Json.Obj(("error", "no building at this cell"), ("cell", cell));
            }

            var components = new System.Collections.Generic.List<object>();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    continue;
                }
                components.Add(ReflectDump(c));
            }

            return Json.Obj(
                ("cell", cell),
                ("name", go.name),
                ("components", components));
        }

        /// <summary>
        /// Ground-truth dump of a building's real, currently-ACTIVE status items -- what the
        /// player's hover tooltip actually shows -- rather than inferring it from whichever
        /// component field is guessed to drive it. Added after guessing wrong twice
        /// in a row about which boolean controlled a "Pipe Blocked" tooltip: reflects directly
        /// into <c>KSelectable</c>'s private <c>statusItemGroup</c> (a <c>StatusItemGroup</c>)
        /// and ITS private <c>items</c> list (<c>List&lt;StatusItemGroup.Entry&gt;</c>,
        /// in the current game build) -- the actual backing collection every hover tooltip
        /// reads from, regardless of which of a building's own components last toggled
        /// something into or out of it. Each entry's <c>StatusItem</c> (a Klei <c>Resource</c>
        /// subclass) reports its own <c>Id</c>/<c>Name</c> via the same generic reflection
        /// <see cref="ReflectDump"/> uses elsewhere, so it also picks up inherited base-class
        /// fields Resource itself declares.
        /// </summary>
        private static string DumpStatusItems(int cell)
        {
            if (!Grid.IsValidCell(cell))
            {
                return Json.Obj(("error", "invalid cell"), ("cell", cell));
            }
            // Conduits live on their own object layers, NOT on ObjectLayer.Building, so they are
            // looked up separately.
            GameObject go = Grid.Objects[cell, (int)ObjectLayer.Building]
                ?? Grid.Objects[cell, (int)ObjectLayer.GasConduit]
                ?? Grid.Objects[cell, (int)ObjectLayer.LiquidConduit];
            if (go == null)
            {
                return Json.Obj(("error", "no building or conduit at this cell"), ("cell", cell));
            }
            KSelectable selectable = go.GetComponent<KSelectable>();
            if (selectable == null)
            {
                return Json.Obj(("error", "no KSelectable on this building"), ("cell", cell));
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            object group = typeof(KSelectable).GetField("statusItemGroup", flags)?.GetValue(selectable);
            if (group == null)
            {
                return Json.Obj(("error", "KSelectable has no statusItemGroup"), ("cell", cell));
            }

            object itemsList = group.GetType().GetField("items", flags)?.GetValue(group);
            if (itemsList == null)
            {
                return Json.Obj(("error", "StatusItemGroup has no items field"), ("cell", cell));
            }

            var result = new System.Collections.Generic.List<object>();
            foreach (object entry in (IEnumerable)itemsList)
            {
                Type entryType = entry.GetType();
                object idVal = entryType.GetField("id", flags)?.GetValue(entry);
                object itemVal = entryType.GetField("item", flags)?.GetValue(entry);
                object categoryVal = entryType.GetField("category", flags)?.GetValue(entry);
                result.Add(new RawJson(Json.Obj(
                    ("id", idVal?.ToString() ?? "null"),
                    ("category", categoryVal?.ToString() ?? "null"),
                    ("item", new RawJson(ReflectDump(itemVal))))));
            }

            return Json.Obj(
                ("cell", cell),
                ("name", go.name),
                ("activeStatusItemCount", result.Count),
                ("activeStatusItems", result));
        }

        private static string DumpConduit(int cell, string type)
        {
            if (!Grid.IsValidCell(cell))
            {
                return Json.Obj(("error", "invalid cell"), ("cell", cell));
            }
            ConduitFlow flow = type == "liquid" ? Game.Instance.liquidConduitFlow : Game.Instance.gasConduitFlow;
            if (flow == null || !flow.HasConduit(cell))
            {
                return Json.Obj(("error", "no " + type + " conduit at this cell"), ("cell", cell));
            }
            ConduitFlow.ConduitContents contents = flow.GetContents(cell);
            Element element = ElementLoader.FindElementByHash(contents.element);
            return Json.Obj(
                ("cell", cell),
                ("conduitType", type),
                ("element", element != null ? element.tag.ToString() : contents.element.ToString()),
                ("massKg", contents.mass),
                ("temperatureK", contents.temperature),
                ("diseaseCount", contents.diseaseCount));
        }

        // The machine-readable twin of a pipe tooltip: every number such a tooltip prints, plus
        // the stress evaluation behind its warnings, straight from PipeNetworkFacade. A readout
        // rendered into a hover card can only be checked by a human reading it back; with this
        // route the same values are curl-able while the game runs.
        private static string DumpPipeNetwork(int cell, string type)
        {
            if (!Grid.IsValidCell(cell))
            {
                return Json.Obj(("error", "invalid cell"), ("cell", cell));
            }
            PipeContentType contentType = type == "liquid" ? PipeContentType.Liquid : PipeContentType.Gas;
            if (!PipeNetworkFacade.TryGetNetworkState(cell, contentType, out PipeNetworkState state))
            {
                return Json.Obj(("error", "no " + type + " conduit network at this cell"), ("cell", cell));
            }

            var contents = new System.Collections.Generic.List<object>();
            foreach (PipeSpecies species in state.Contents)
            {
                Element element = ElementLoader.elements[species.ElementIdx];
                contents.Add(new RawJson(Json.Obj(
                    ("element", element != null ? element.tag.ToString() : species.ElementIdx.ToString()),
                    ("massKg", species.MassKg),
                    ("moles", species.Moles),
                    ("moleFractionPct", species.MoleFraction * 100f))));
            }

            var fields = new System.Collections.Generic.List<(string, object)>
            {
                ("cell", cell),
                ("conduitType", type),
                ("tiles", state.Cells.Length),
                ("volumeLitres", state.VolumeLitres),
                ("totalMassKg", state.TotalMassKg),
                ("temperatureK", state.TemperatureK),
                ("pressurePa", state.PressurePa),
                ("totalMoles", state.TotalMoles),
                ("liquidVolumeLitres", state.LiquidVolumeLitres),
                ("fillFraction", state.FillFraction),
                ("contents", contents),
            };

            if (PipeNetworkFacade.TryEvaluateStress(state, out PipeStressReport stress))
            {
                Element stressElement = stress.ElementIdx >= 0
                    ? ElementLoader.elements[stress.ElementIdx]
                    : null;
                fields.Add(("stress", new RawJson(Json.Obj(
                    ("level", stress.Level.ToString()),
                    ("kind", stress.Kind.ToString()),
                    ("worstCell", stress.WorstCell),
                    ("element", stressElement != null ? stressElement.tag.ToString() : "(none)"),
                    ("value", stress.Value),
                    ("limit", stress.Limit),
                    ("severity", stress.Severity),
                    ("ambientPaAtWorstCell", PipeNetworkFacade.AmbientPressurePa(stress.WorstCell))))));
            }
            else
            {
                fields.Add(("stress", null));
            }

            return Json.ObjRaw(fields);
        }

        // Cache: simple-name -> Type, built lazily across every loaded assembly (vanilla
        // Assembly-CSharp included -- "inspect anything," not just this mod's own types). Typed
        // as a Dictionary rather than rebuilt per request since a full type scan across the
        // whole game assembly is not something to repeat every call from a background-triggered
        // main-thread frame.
        private static System.Collections.Generic.Dictionary<string, Type> typeCache;

        private static string DumpFind(string typeName)
        {
            if (typeCache == null)
            {
                typeCache = new System.Collections.Generic.Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t != null); }
                    catch { continue; }
                    foreach (Type t in types)
                    {
                        // Last one wins on a simple-name collision across assemblies -- named,
                        // not hidden: a caller hitting an ambiguous name gets SOME real type,
                        // just not a guaranteed specific one. Good enough for a debug tool.
                        typeCache[t.Name] = t;
                    }
                }
            }

            if (!typeCache.TryGetValue(typeName, out Type type))
            {
                return Json.Obj(("error", "no loaded type named " + typeName));
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                return Json.Obj(("error", type.FullName + " is not a UnityEngine.Object subtype -- can't be enumerated via FindObjectsOfType. Plain C# state isn't reachable this way."));
            }

            UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
            var dumped = new System.Collections.Generic.List<object>();
            foreach (UnityEngine.Object inst in instances)
            {
                var entry = new System.Collections.Generic.List<(string, object)>();
                if (inst is Component comp)
                {
                    entry.Add(("cell", Grid.PosToCell(comp.transform.position)));
                }
                entry.Add(("instance", ReflectDumpRaw(inst)));
                dumped.Add(Json.ObjRaw(entry));
            }

            return Json.Obj(("type", type.FullName), ("count", instances.Length), ("instances", dumped));
        }

        // ------------------------------------------------------------------
        // Generic reflection dump -- the part that makes this "inspect ANYTHING," not just the
        // specific things this mod's own mechanics happen to expose today. Reads public AND
        // private instance fields/properties (a debug tool wants the real internal state, not
        // just what a type's author chose to make public) via reflection. Deliberately shallow:
        // one level of primitive/collection unwrapping, everything else falls back to a type
        // name string -- a full object-graph walker would risk unbounded recursion through
        // Unity's own Transform/GameObject back-references for no real debugging benefit.
        // ------------------------------------------------------------------

        private static object ReflectDumpRaw(object obj)
        {
            return new RawJson(ReflectDump(obj));
        }

        private static string ReflectDump(object obj)
        {
            if (obj == null)
            {
                return "null";
            }
            Type type = obj.GetType();
            var entries = new System.Collections.Generic.List<(string, object)> { ("__type", type.Name) };

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo f in type.GetFields(flags))
            {
                object val;
                try { val = f.GetValue(obj); }
                catch { continue; }
                entries.Add((f.Name, new RawJson(DumpValue(val))));
            }
            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead)
                {
                    continue;
                }
                object val;
                try { val = p.GetValue(obj); }
                catch { continue; }
                entries.Add((p.Name, new RawJson(DumpValue(val))));
            }

            return Json.ObjRaw(entries);
        }

        private const int MaxCollectionItems = 50;

        private static string DumpValue(object val)
        {
            switch (val)
            {
                case null:
                    return "null";
                case bool b:
                    return b ? "true" : "false";
                case string s:
                    return Json.Quote(s);
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToString(val, CultureInfo.InvariantCulture);
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case Enum e:
                    return Json.Quote(e.ToString());
                case Vector2 or Vector3 or Vector4 or Color or Quaternion:
                    return Json.Quote(val.ToString());
                case IEnumerable en when !(val is string):
                    var items = new StringBuilder("[");
                    int count = 0;
                    bool first = true;
                    foreach (object item in en)
                    {
                        if (count++ >= MaxCollectionItems)
                        {
                            items.Append(first ? "" : ",").Append("\"...(truncated)\"");
                            break;
                        }
                        if (!first) items.Append(",");
                        first = false;
                        items.Append(DumpValue(item));
                    }
                    items.Append("]");
                    return items.ToString();
                default:
                    // Anything else (a nested component, a GameObject, a Transform, an
                    // arbitrary reference type) -- fall back to its type name plus ToString()
                    // rather than recursing, avoiding unbounded/cyclic object graphs.
                    return Json.Quote($"<{val.GetType().Name}: {val}>");
            }
        }

        /// <summary>
        /// Wraps an already-JSON string so <see cref="Json"/>'s builders embed it verbatim
        /// instead of re-quoting it as a string literal.
        /// </summary>
        internal sealed class RawJson
        {
            public readonly string Text;
            public RawJson(string text) { Text = text; }
        }

        /// <summary>
        /// A minimal hand-rolled JSON writer -- deliberately not a dependency on
        /// Newtonsoft.Json or any other external library. Pulling in a new DLL here would mean
        /// bundling a second copy into every flagship mod's own output, exactly the
        /// TypeLoadException-causing multi-copy problem <see cref="FrameworkVersion"/> describes
        /// for OniFramework.dll itself -- not worth
        /// the risk for a write-only debug serializer this small.
        /// </summary>
        internal static class Json
        {
            public static string Quote(string s)
            {
                if (s == null) return "null";
                var sb = new StringBuilder("\"");
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
                return sb.ToString();
            }

            public static string Obj(params (string name, object value)[] fields)
            {
                return ObjRaw(new System.Collections.Generic.List<(string, object)>(fields));
            }

            public static string ObjRaw(System.Collections.Generic.List<(string name, object value)> fields)
            {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach ((string name, object value) in fields)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(Quote(name)).Append(":").Append(Encode(value));
                }
                sb.Append("}");
                return sb.ToString();
            }

            private static string Encode(object value)
            {
                switch (value)
                {
                    case null: return "null";
                    case RawJson raw: return raw.Text;
                    case bool b: return b ? "true" : "false";
                    case string s: return Quote(s);
                    case int or long or float or double:
                        return Convert.ToString(value, CultureInfo.InvariantCulture);
                    case System.Collections.IEnumerable en:
                        var sb = new StringBuilder("[");
                        bool first = true;
                        foreach (object item in en)
                        {
                            if (!first) sb.Append(",");
                            first = false;
                            sb.Append(item is string str ? Quote(str) : Encode(item));
                        }
                        sb.Append("]");
                        return sb.ToString();
                    default:
                        return Quote(value.ToString());
                }
            }
        }
    }

    /// <summary>
    /// The Unity-side half of <see cref="DebugInspectorServer"/> -- a real MonoBehaviour so
    /// Unity's own per-frame Update() is what actually drains the request queue on the main
    /// thread. Add via <c>gameObject.AddComponent&lt;DebugInspectorPump&gt;()</c> once, anywhere
    /// with a stable lifetime (a mod's own DontDestroyOnLoad overlay object, same pattern
    /// Mod1ThermoFluid's other per-frame components already use).
    /// </summary>
    public class DebugInspectorPump : MonoBehaviour
    {
        private void Update()
        {
            DebugInspectorServer.ProcessPending();
        }

        private void OnDestroy()
        {
            DebugInspectorServer.Stop();
        }
    }
}
