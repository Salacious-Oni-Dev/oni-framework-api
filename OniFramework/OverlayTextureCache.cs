using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Stops four of Klei's overlay property-texture passes from recomputing a rectangle that has
	/// not changed.
	///
	/// WHY THESE FOUR AND NOT THE OTHER EIGHT. Measured on a 234-cycle colony, with
	/// KProfiler's <c>/skip</c> probe: of the twelve managed passes, <c>UpdateSolidDigAmount</c>
	/// and <c>UpdateFogOfWar</c> produce byte-identical output on <b>99.4 %</b> of the frames they
	/// run and cost 0.285 ms a frame between them; <c>UpdateSolidLiquidGasMass</c> and
	/// <c>UpdateSolidLiquidGasMassForLight</c> do so on <b>86.4 %</b> and cost 0.426 ms. The other
	/// eight are round-robin, one of them a frame, so all eight together are worth 0.071 ms and
	/// four of them are under 3 % clean. Those eight are not worth a detector.
	///
	/// TWO DETECTORS, BECAUSE THE TWO PAIRS ARE NOT ALIKE. Dig amount and fog of war read only
	/// managed state, so every writer of every input can be hooked and the detector is a handful
	/// of epoch counters that cost nothing on a frame when nothing moves. The mass passes read
	/// <c>Grid.Mass</c> and <c>Grid.Element</c>, which the sim rewrites every tick through a raw
	/// pointer with no managed chokepoint anywhere, so hooking their writers would mean new
	/// sim-side touch information and an ABI addition to carry it.
	///
	/// They use a <b>state digest</b> instead. Recompute the four bytes the pass would write for
	/// each cell of the rectangle, compare them with the bytes the last run actually wrote, and
	/// skip only if every one matches. That needs no writer hooks at all and cannot miss a writer,
	/// because it compares state and never events -- the property that makes it the right answer
	/// for inputs whose writers cannot be enumerated. It is affordable because the pass quantises
	/// hard: the alpha byte is mass in steps of 2000/255 kg, so mass that moves every tick still
	/// produces an identical byte almost every tick, which is what the 86.4 % measurement is
	/// measuring. It returns at the first disagreement, so a genuinely dirty frame usually pays a
	/// fraction of a sweep.
	///
	/// WHY NOT A PER-CELL CACHE, which is the obvious design and is wrong. The same probe reports
	/// <b>100.0 %</b> of individual cells unchanged on the mass passes -- under 0.05 % of cells
	/// move -- and yet a pass still costs 0.262 ms over roughly 5,000 cells. That is 52 ns a cell,
	/// far too slow for the per-cell body. The cost is not the cells; it is the dispatch to
	/// <c>GlobalJobManager</c> and the block on it. Skipping per-cell work while still dispatching
	/// would save almost nothing. Only not dispatching at all saves anything, so the granularity
	/// here is the whole pass and nothing finer.
	///
	/// WHERE THE PATCH SITS, and it is chosen to match what was measured.
	/// <c>PropertyTextures.UpdateTextureThreaded</c> is both the dispatcher and the method
	/// KProfiler times, so the 0.285 ms figure is exactly the work this prefix removes. The
	/// surrounding <c>Lock</c>, <c>Apply</c> and <c>Blit</c> live in <c>UpdateProperty</c>, are not
	/// skipped, and are part of the separate 0.52 ms residue. Skipping those as well needs the
	/// property's identity before <c>Lock</c> is called, which is only available inside a private
	/// nested struct; that is a different and larger change and is not made here.
	///
	/// SOUNDNESS, which is the whole risk. A skipped pass leaves the render texture holding what
	/// it held before, so a skip is correct only if nothing the pass reads has changed AND every
	/// cell of the requested rectangle was written by the last pass that actually ran. Three
	/// mechanisms enforce that, in increasing order of how much they are trusted:
	///
	/// <list type="number">
	/// <item><b>Enumerated writer hooks.</b> Every managed writer of every input, found by reading
	/// the game's code rather than by reasoning about it. <c>Grid.Damage</c> is written in
	/// exactly one method, <c>WorldDamage.ApplyDamage</c>; the solid flag in exactly one,
	/// <c>Grid.UpdateBuildMask</c>; <c>Grid.Visible</c> in <c>Grid.Reveal</c> plus two sites that
	/// only run at world load and in a dev tool.</item>
	/// <item><b>Region containment.</b> A pass is skipped only if the rectangle it was handed is
	/// contained in the rectangle of the last pass that ran. A camera pan or a zoom out therefore
	/// always runs; a still camera and a zoom in do not. This is what keeps a cell that has never
	/// been drawn from being skipped.</item>
	/// <item><b>A forced refresh cap.</b> At most <see cref="MaxSkippedFrames"/> consecutive skips,
	/// after which the pass runs whatever the hooks say. This is the brace to the belt: backwall
	/// is read through a raw sim pointer and has no managed chokepoint at all, so a backwall
	/// change that no hook catches is bounded to at most that many frames of stale tint rather
	/// than being permanent.</item>
	/// </list>
	///
	/// The failure mode if all three are wrong together is a stale overlay tint for a fraction of
	/// a second. Nothing here writes game state, so nothing here can corrupt a save.
	///
	/// OFF BY DEFAULT. It changes how the base game renders, so a consumer has to ask for it.
	/// <see cref="Verify"/> runs the pass anyway and counts the times the detector claimed a pass
	/// was clean when it was not; that number must be zero before this is turned on anywhere.
	/// </summary>
	public static class OverlayTextureCache
	{
		/// <summary>Whether passes may actually be skipped. Off until a consumer asks.</summary>
		public static bool Enabled;

		/// <summary>
		/// Never skip, but check every decision against the bytes the pass really wrote, and count
		/// the ones that were wrong. Costs a per-cell compare on the worker strips, so it is a
		/// verification mode and not something to leave on.
		/// </summary>
		public static bool Verify;

		/// <summary>
		/// Longest run of consecutive skips before a pass is forced to run anyway. The bound on
		/// how long a change no hook saw can stay on screen.
		/// </summary>
		public static int MaxSkippedFrames = 30;

		/// <summary>Passes skipped since install.</summary>
		public static long Skipped { get { return skipped; } }

		/// <summary>Passes that ran because the detector said they had to.</summary>
		public static long Ran { get { return ran; } }

		/// <summary>
		/// Decisions <see cref="Verify"/> proved wrong: the detector said clean and the pass then
		/// wrote a different byte. Must be zero.
		/// </summary>
		public static long FalseClean { get { return falseClean; } }

		/// <summary>
		/// Runs <see cref="Verify"/> actually judged, meaning the detector claimed the pass was
		/// clean and the pass then ran anyway so the claim could be checked. This is the
		/// denominator <see cref="FalseClean"/> is meaningless without: zero failures out of zero
		/// judgements says nothing at all.
		/// </summary>
		public static long Judged
		{
			get
			{
				return dig.claimedCleanRuns + fog.claimedCleanRuns
					+ mass.claimedCleanRuns + massLight.claimedCleanRuns;
			}
		}

		/// <summary>
		/// Refills performed under <see cref="Verify"/> and then read back out of the page.
		/// </summary>
		public static long RefillsChecked { get { return refillsChecked; } }

		/// <summary>
		/// Bytes a refill left wrong where <c>Unlock</c> would have published them. Must be zero;
		/// a non-zero value is the flicker this check exists to catch.
		/// </summary>
		public static long RefillWrong { get { return refillWrong; } }

		/// <summary>
		/// Digest sweeps performed: at most one a frame, shared by both mass passes, and none at
		/// all while the cache is off.
		/// </summary>
		public static long DigestSweeps { get { return digestSweeps; } }

		/// <summary>Sweeps that reached the end of the rectangle without finding a changed byte.</summary>
		public static long DigestCleanSweeps { get { return digestCleanSweeps; } }

		/// <summary>
		/// Mean wall-clock cost of one digest sweep, in milliseconds.
		///
		/// This is the number that decides whether the digest detector is worth having, because
		/// unlike the writer hooks it is paid on every frame, including the frames the mass passes
		/// still have to run. The two passes cost 0.426 ms a frame between them and are clean on
		/// 86.4 % of frames, so the sweep has to stay well under about 0.1 ms to pay for itself.
		/// </summary>
		public static double DigestMillis
		{
			get
			{
				return digestSweeps == 0L
					? 0.0
					: (double)digestTicks * 1000.0 / Stopwatch.Frequency / digestSweeps;
			}
		}

		private const string Id = "OniFramework.OverlayTextureCache";

		/// <summary>The four callbacks this cache is allowed to skip, by method name.</summary>
		private const string DigCallback = "UpdateSolidDigAmount";
		private const string FogCallback = "UpdateFogOfWar";
		private const string MassCallback = "UpdateSolidLiquidGasMass";
		private const string MassLightCallback = "UpdateSolidLiquidGasMassForLight";

		/// <summary>
		/// Bytes per pixel the mass digest is written for. The pass calls the four-byte
		/// <c>TextureRegion.SetBytes</c> overload, so anything else means the texture format is
		/// not the one this code was written against and the digest declines to judge rather than
		/// guessing at a layout.
		/// </summary>
		private const int MassBytesPerPixel = 4;

		/// <summary>Denominator of the pass's own mass-to-alpha ramp, in kilograms.</summary>
		private const float MassRampKg = 2000f;

		/// <summary>The floor the pass puts under a cell that holds anything at all: alpha 1.</summary>
		private const float MassAlphaFloor = 0.003921569f;

		private sealed class Pass
		{
			public string name;

			/// <summary>
			/// True for the two mass passes. Their epoch is moved by the per-frame digest sweep
			/// rather than by writer hooks, so none of the hooks below apply to them and
			/// <see cref="Touches"/> is never asked about them.
			/// </summary>
			public bool digest;

			// Bumped by every writer hook for this pass's inputs. Compared rather than cleared, so
			// a write that lands WHILE the pass is running is not lost: the pass records the value
			// it started from, and a later bump leaves the two unequal.
			public int epoch;
			public int seenEpoch;

			// Rectangle of the last pass that actually ran. A skip requires containment in it.
			public bool everRan;
			public int x0, y0, x1, y1;

			public int skippedInARow;
			public long skipped;
			public long ran;

			// The bytes the last run wrote, keyed by grid cell. Load-bearing, not diagnostic:
			// a skipped frame copies these back into the page.
			public byte[] shadow;
			public int bpp;
			public bool shadowValid;

			// Verify mode only.
			public bool claimedClean;
			public long claimedCleanRuns;
			public long falseClean;
		}

		private static readonly Pass dig = new Pass { name = DigCallback };
		private static readonly Pass fog = new Pass { name = FogCallback };
		private static readonly Pass mass = new Pass { name = MassCallback, digest = true };
		private static readonly Pass massLight = new Pass { name = MassLightCallback, digest = true };

		private static Harmony harmony;
		private static bool installed;
		private static bool verifyInstalled;
		private static long skipped;
		private static long ran;
		private static long falseClean;
		private static int lastActiveWorld = -1;

		// Which hook dirtied what, so an over-dirtying detector names its own cause instead of
		// being reasoned about. Diagnostic only; nothing reads them but StatusText.
		private static long bumpSolid;
		private static long bumpDamage;
		private static long bumpBackwall;
		private static long bumpReveal;
		private static long bumpWorld;
		private static long filtered;
		private static long refillsChecked;
		private static long refillWrong;
		private static long refillRefused;

		// The digest, computed at most once a frame and answered from here for the second pass of
		// the pair. Keyed by the rectangle as well as the frame, so a pair that somehow disagreed
		// about the rectangle would sweep twice rather than reuse an answer about somewhere else.
		private static int digestFrame = -1;
		private static int digestX0;
		private static int digestY0;
		private static int digestX1;
		private static int digestY1;
		private static bool digestResult;
		private static long digestSweeps;
		private static long digestCleanSweeps;
		private static long digestCells;
		private static long digestTicks;

		/// <summary>The pass currently dispatched, for the verify postfix. Game thread publishes,
		/// worker strips read; <c>UpdateTextureThreaded</c> blocks, so only one is ever live.</summary>
		private static volatile Pass inFlight;

		/// <summary>The pass that just ran, for the postfix that refreshes its shadow.</summary>
		private static Pass lastRun;

		// ------------------------------------------------------------------ install

		/// <summary>
		/// Applies the patches. Idempotent, and does nothing on its own: <see cref="Enabled"/> is
		/// false until something sets it.
		/// </summary>
		public static void Install(Harmony consumerHarmony)
		{
			if (installed)
				return;

			installed = true;
			harmony = new Harmony(Id);

			try
			{
				Patch(typeof(PropertyTextures), "UpdateTextureThreaded", null, "DispatchPrefix", "DispatchPostfix");

				// --- inputs of UpdateSolidDigAmount: Grid.Solid, Grid.Damage, backwall ---
				Patch(typeof(Grid), "UpdateBuildMask", null, null, "DirtyBuildMask");
				Patch(typeof(WorldDamage), "ApplyDamage", new[]
				{
					typeof(int), typeof(float), typeof(int), typeof(WorldDamage.DamageType),
					typeof(string), typeof(string)
				}, null, "DirtyDamage");
				Patch(typeof(SimMessages), "SetBackwallData", null, null, "DirtyBackwallData");
				Patch(typeof(SimMessages), "Dig", null, null, "DirtyDug");

				// --- input of UpdateFogOfWar: Grid.Visible ---
				Patch(typeof(Grid), "Reveal", null, null, "DirtyFog");

				// --- anything that rebuilds the world or the textures dirties both ---
				Patch(typeof(Grid), "InitializeCells", null, null, "DirtyAll");
				Patch(typeof(PropertyTextures), "OnReset", null, null, "ResetAll");

				FrameworkLog.Info("OverlayTextureCache installed (disabled; set Enabled to use it)");
			}
			catch (Exception e)
			{
				// A half-applied detector is worse than none: without every writer hook a skip can
				// be wrong, so losing one hook disables the whole thing rather than degrading it.
				Enabled = false;
				installed = false;
				FrameworkLog.Warn("OverlayTextureCache could not install, stays off: " + e.Message);
			}
		}

		private static void Patch(Type type, string method, Type[] signature, string prefix, string postfix)
		{
			MethodInfo target = signature == null
				? AccessTools.Method(type, method)
				: AccessTools.Method(type, method, signature);

			if (target == null)
				throw new MissingMethodException(type.Name, method);

			harmony.Patch(target,
				prefix == null ? null : new HarmonyMethod(Self(prefix)),
				postfix == null ? null : new HarmonyMethod(Self(postfix)));
		}

		private static MethodInfo Self(string name)
		{
			MethodInfo m = typeof(OverlayTextureCache).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
			if (m == null)
				throw new MissingMethodException("OverlayTextureCache", name);

			return m;
		}

		// ------------------------------------------------------------------ dirty hooks
		//
		// Each takes the cell it wrote, because the dirty signal has to be spatial: these passes
		// draw only the visible rectangle, so a change anywhere else is not a change to anything
		// on screen. Beyond that they are deliberately unconditional -- deciding whether a
		// particular write altered the rendered BYTE would cost more than the pass being
		// protected, and being wrong about that is the one failure this class must not have.

		/// <summary>
		/// Whether a changed cell is one this pass has actually drawn.
		///
		/// WHY THIS TEST EXISTS, and it was measured rather than foreseen. Without it the hooks
		/// are global: a duplicant digging on the far side of the asteroid dirtied the dig-amount
		/// pass even though that pass only ever writes the visible rectangle. On a 22-duplicant
		/// colony that alone cut the share of frames the detector could call clean from the
		/// measured 99.4 % to 46 %.
		///
		/// Tested against the rectangle of the last pass that RAN, not the one being asked about,
		/// and that is what makes it sound: a cell outside the drawn rectangle is not on screen,
		/// and the only way it can get on screen is a camera move, which breaks the containment
		/// test in <see cref="DispatchPrefix"/> and forces a run regardless.
		/// </summary>
		private static bool Touches(Pass p, int cell)
		{
			if (!p.everRan || cell < 0)
				return true;

			int w = Grid.WidthInCells;
			if (w <= 0)
				return true;

			int x = cell % w;
			int y = cell / w;
			return x >= p.x0 && x <= p.x1 && y >= p.y0 && y <= p.y1;
		}

		/// <summary>
		/// Solid-flag changes only. <c>UpdateBuildMask</c> carries six flags and five of them --
		/// Foundation, DupeImpassable, DupePassable, CritterImpassable and Door -- have nothing to
		/// do with what this overlay draws. A door toggling on a busy colony would otherwise
		/// dirty the pass several times a second for no rendered difference.
		/// </summary>
		private static void DirtyBuildMask(int i, Grid.BuildFlags flag)
		{
			if ((flag & Grid.BuildFlags.Solid) == 0)
				return;

			if (!Touches(dig, i))
			{
				filtered++;
				return;
			}

			unchecked { dig.epoch++; }
			bumpSolid++;
		}

		private static void DirtyDamage(int cell)
		{
			if (!Touches(dig, cell))
			{
				filtered++;
				return;
			}

			unchecked { dig.epoch++; }
			bumpDamage++;
		}

		private static void DirtyBackwallData(int cell)
		{
			if (!Touches(dig, cell))
			{
				filtered++;
				return;
			}

			unchecked { dig.epoch++; }
			bumpBackwall++;
		}

		private static void DirtyDug(int gameCell)
		{
			if (!Touches(dig, gameCell))
			{
				filtered++;
				return;
			}

			unchecked { dig.epoch++; }
			bumpBackwall++;
		}

		private static void DirtyFog(int cell)
		{
			if (!Touches(fog, cell))
			{
				filtered++;
				return;
			}

			unchecked { fog.epoch++; }
			bumpReveal++;
		}

		private static void DirtyAll()
		{
			unchecked
			{
				dig.epoch++;
				fog.epoch++;
				mass.epoch++;
				massLight.epoch++;
			}
		}

		/// <summary>
		/// The texture buffers were recreated, so nothing on screen is ours any more and the
		/// remembered rectangles describe textures that no longer exist.
		/// </summary>
		private static void ResetAll()
		{
			Forget(dig);
			Forget(fog);
			Forget(mass);
			Forget(massLight);

			digestFrame = -1;
			digestResult = false;
			DirtyAll();
		}

		private static void Forget(Pass p)
		{
			p.everRan = false;
			p.shadow = null;
			p.shadowValid = false;
		}

		// ------------------------------------------------------------------ the decision

		/// <summary>
		/// Runs last among the prefixes on purpose. Returning false skips every prefix after it as
		/// well as the method itself, and Mod 1's overlay override is a prefix on this same method;
		/// letting it run first means its bookkeeping is never cut short even though neither
		/// property it overrides is one this cache touches.
		/// </summary>
		[HarmonyPriority(Priority.Low)]
		private static bool DispatchPrefix(TextureRegion texture_region, int x0, int y0, int x1, int y1,
			object update_texture_cb)
		{
			inFlight = null;
			lastRun = null;

			// NO EARLY RETURN WHEN DISABLED, and that is a fix rather than an oversight. The
			// rectangle recorded below is what Touches() tests a changed cell against, so a
			// prefix that bails while the cache is off leaves every pass with no rectangle, and
			// the spatial filter silently degrades to accepting everything. It read 0 cells
			// filtered on a colony that was demonstrably digging off screen. The bookkeeping is
			// a name comparison and four assignments about five times a frame; it always runs.
			Pass p;
			try
			{
				Delegate cb = update_texture_cb as Delegate;
				if (cb == null || cb.Method == null)
					return true;

				string name = cb.Method.Name;
				if (name == DigCallback)
					p = dig;
				else if (name == FogCallback)
					p = fog;
				else if (name == MassCallback)
					p = mass;
				else if (name == MassLightCallback)
					p = massLight;
				else
					return true;

				// Switching asteroid changes both what IsActiveWorld answers and the world extents
				// UpdateFogOfWar folds in, and it changes them for every cell at once.
				int world = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorldId : -1;
				if (world != lastActiveWorld)
				{
					lastActiveWorld = world;
					DirtyAll();
					bumpWorld++;
				}

				// The mass pair has no writer hooks. Their epoch moves here instead, once a frame
				// and only when a sweep of the rectangle finds an output byte that really changed.
				if (p.digest)
					MassDigestClean(x0, y0, x1, y1);

				bool clean = p.everRan
					&& p.epoch == p.seenEpoch
					&& x0 >= p.x0 && y0 >= p.y0 && x1 <= p.x1 && y1 <= p.y1
					&& p.skippedInARow < MaxSkippedFrames;

				// SKIPPING IS NOT ENOUGH, AND THAT WAS A LIVE BUG. This prefix sits INSIDE
				// UpdateProperty, between Lock and Unlock. Lock takes a scratch page from
				// TexturePagePool, which recycles pages by size and format only, and Unlock
				// blits whatever the page holds into the render texture whether or not anything
				// wrote it. Returning false on its own therefore published another property's
				// bytes -- dig amount shares RGB24 with Temperature, fog of war shares Alpha8
				// with gas danger, gas pressure, state change and falling solid -- and the
				// overlay flickered between real frames and borrowed ones.
				//
				// So a skipped frame REFILLS the page from the shadow instead of leaving it
				// blank. The bytes that land are exactly the bytes the last run that actually
				// executed wrote for that rectangle, which is precisely what the detector
				// asserts is still correct. What is saved is the per-cell work and the job
				// dispatch; what is paid is one row-sized memcpy per row.
				if (clean && Enabled && !Verify && p.shadowValid
					&& p.bpp == texture_region.bytesPerPixel && Restore(p, texture_region, x0, y0, x1, y1))
				{
					p.skippedInARow++;
					p.skipped++;
					skipped++;
					return false;
				}

				// Running. Record the epoch BEFORE the work, so a write that lands during it is
				// still ahead of us next frame.
				p.seenEpoch = p.epoch;
				p.x0 = x0;
				p.y0 = y0;
				p.x1 = x1;
				p.y1 = y1;
				p.everRan = true;
				p.skippedInARow = 0;
				p.ran++;
				ran++;
				lastRun = p;

				EnsureShadow(p, texture_region.bytesPerPixel);

				if (Verify)
				{
					p.claimedClean = clean;

					// EXERCISE THE SKIP PATH EVEN THOUGH NOTHING IS BEING SKIPPED. On a frame the
					// detector called clean, do the real refill into the real page and then read
					// the page back. Without this the verification tests only the judgement and
					// never the consequence, which is how a flickering overlay was reported as
					// verified: the page was being published unwritten, and no check ever looked
					// at the page.
					if (clean && p.shadowValid && p.bpp == texture_region.bytesPerPixel)
					{
						if (Restore(p, texture_region, x0, y0, x1, y1))
						{
							long bad = RefillMismatch(p, texture_region, x0, y0, x1, y1);
							if (bad != 0L)
								refillWrong += bad < 0L ? 1L : bad;

							refillsChecked++;
						}
						else
						{
							refillRefused++;
						}
					}

					// Counted because a false-clean tally is worth nothing without it. Zero
					// failures out of zero judgements is the shape of a check that never ran.
					if (p.claimedClean)
						p.claimedCleanRuns++;

					inFlight = p.shadow != null ? p : null;
				}
			}
			catch
			{
				inFlight = null;
			}

			return true;
		}

		/// <summary>
		/// Copies what the pass just wrote out of the page and into the shadow, so the next
		/// skipped frame has something correct to put back. Runs on the game thread after the job
		/// manager has returned, which is the only point at which the page is both complete and
		/// still alive -- Unlock releases it back to the pool immediately afterwards.
		/// </summary>
		private static void DispatchPostfix(TextureRegion texture_region, int x0, int y0, int x1, int y1)
		{
			Pass p = lastRun;
			lastRun = null;

			if (p == null || p.shadow == null || p.bpp != texture_region.bytesPerPixel)
				return;

			try
			{
				if (Copy(p, texture_region, x0, y0, x1, y1, toShadow: true))
					p.shadowValid = true;
			}
			catch
			{
				p.shadowValid = false;
			}
		}

		private static bool Restore(Pass p, TextureRegion region, int x0, int y0, int x1, int y1)
		{
			try
			{
				return Copy(p, region, x0, y0, x1, y1, toShadow: false);
			}
			catch
			{
				// Could not refill the page, so the page is not ours to publish. Say so and let
				// the pass run: a wrong overlay is worse than a slow one.
				return false;
			}
		}

		/// <summary>
		/// One row-run memcpy per row between the page and the grid-keyed shadow.
		///
		/// Unsafe on purpose. The managed alternative is a bounds-checked NativeArray index per
		/// byte, and at roughly fifteen kilobytes a pass that is the same order of cost as the
		/// per-cell work being avoided, which would have made the whole exercise pointless.
		/// </summary>
		private unsafe static bool Copy(Pass p, TextureRegion region, int x0, int y0, int x1, int y1,
			bool toShadow)
		{
			int bpp = p.bpp;
			int gridWidth = Grid.WidthInCells;
			int pageWidth = region.pageWidth;
			int run = (x1 - x0 + 1) * bpp;

			if (bpp <= 0 || gridWidth <= 0 || run <= 0 || x0 < 0 || y0 < 0)
				return false;

			NativeArray<byte> bytes = region.bytes;
			int limit = bytes.Length;
			byte* page = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(bytes);
			if (page == null)
				return false;

			fixed (byte* shadow = p.shadow)
			{
				int shadowLimit = p.shadow.Length;

				for (int y = y0; y <= y1; y++)
				{
					int pageOffset = ((y - region.y) * pageWidth + (x0 - region.x)) * bpp;
					int shadowOffset = (y * gridWidth + x0) * bpp;

					if (pageOffset < 0 || pageOffset + run > limit)
						return false;

					if (shadowOffset < 0 || shadowOffset + run > shadowLimit)
						return false;

					if (toShadow)
						UnsafeUtility.MemCpy(shadow + shadowOffset, page + pageOffset, run);
					else
						UnsafeUtility.MemCpy(page + pageOffset, shadow + shadowOffset, run);
				}
			}

			return true;
		}

		/// <summary>
		/// How many bytes of the page disagree with the shadow. Zero means a refill put exactly
		/// the right bytes where Unlock is about to read them.
		///
		/// This is the check whose absence let a flickering overlay be called verified. The old
		/// verification compared what the PASS wrote against the shadow, which tests the detector
		/// and nothing else; the skip path was never executed by it, and the skip path was where
		/// the bug was. This one runs the real refill and then reads the real page.
		/// </summary>
		private unsafe static long RefillMismatch(Pass p, TextureRegion region, int x0, int y0, int x1, int y1)
		{
			int bpp = p.bpp;
			int gridWidth = Grid.WidthInCells;
			int pageWidth = region.pageWidth;
			int run = (x1 - x0 + 1) * bpp;

			if (bpp <= 0 || gridWidth <= 0 || run <= 0)
				return -1L;

			NativeArray<byte> bytes = region.bytes;
			int limit = bytes.Length;
			byte* page = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(bytes);
			if (page == null)
				return -1L;

			long bad = 0L;

			fixed (byte* shadow = p.shadow)
			{
				int shadowLimit = p.shadow.Length;

				for (int y = y0; y <= y1; y++)
				{
					int pageOffset = ((y - region.y) * pageWidth + (x0 - region.x)) * bpp;
					int shadowOffset = (y * gridWidth + x0) * bpp;

					if (pageOffset < 0 || pageOffset + run > limit)
						return -1L;

					if (shadowOffset < 0 || shadowOffset + run > shadowLimit)
						return -1L;

					for (int i = 0; i < run; i++)
						if (page[pageOffset + i] != shadow[shadowOffset + i])
							bad++;
				}
			}

			return bad;
		}

		// ------------------------------------------------------------------ the mass digest

		/// <summary>
		/// Whether the two mass passes would write the bytes the render texture already holds.
		/// Computed once for a frame and answered from a cache when the second pass of the pair
		/// asks the same question about the same rectangle a moment later.
		///
		/// A dirty answer moves the epoch of BOTH mass passes rather than being consulted
		/// directly, and that is what keeps the pair honest when only one of them is dispatched on
		/// a given frame. A pass that sat out a dirty frame has an epoch behind the current one
		/// and cannot be skipped afterwards on the strength of a later clean sweep, even though
		/// that sweep is comparing against the other pass's shadow and is perfectly true about it.
		///
		/// The same mechanism covers the frames when no sweep runs at all: while the cache is off
		/// the sweep is not paid for, the answer is dirty, and the epoch moves, so arming the
		/// cache mid-run cannot inherit a clean verdict that nothing established.
		/// </summary>
		private static bool MassDigestClean(int x0, int y0, int x1, int y1)
		{
			int frame = Time.frameCount;

			if (digestFrame == frame
				&& digestX0 == x0 && digestY0 == y0 && digestX1 == x1 && digestY1 == y1)
			{
				return digestResult;
			}

			digestFrame = frame;
			digestX0 = x0;
			digestY0 = y0;
			digestX1 = x1;
			digestY1 = y1;

			bool clean = false;

			// A sweep of the rectangle is the whole cost of this detector, so it is not paid on a
			// frame where nothing could act on the answer.
			if (Enabled || Verify)
			{
				long start = Stopwatch.GetTimestamp();

				try
				{
					clean = MassDigestSweep(x0, y0, x1, y1);
				}
				catch
				{
					// Anything unexpected about the grid means this frame is not one to judge.
					clean = false;
				}

				digestTicks += Stopwatch.GetTimestamp() - start;
				digestSweeps++;

				if (clean)
					digestCleanSweeps++;
			}

			digestResult = clean;

			if (!clean)
			{
				unchecked
				{
					mass.epoch++;
					massLight.epoch++;
				}
			}

			return clean;
		}

		/// <summary>
		/// Recomputes the four bytes <c>UpdateSolidLiquidGasMass</c> would write for every cell of
		/// the rectangle and compares them with the bytes its last run actually wrote, stopping at
		/// the first disagreement.
		///
		/// IT COMPARES AGAINST THE IMPERMEABLE-DIFFERENTIATING PASS ON PURPOSE. That pass's output
		/// encodes every input either pass reads -- active world, element state, the
		/// liquid-impermeable property bit and quantised mass -- so a clean verdict covers the
		/// ForLight variant too, which reads a strict subset of the same state. Comparing against
		/// ForLight instead would not be sound in the other direction, because a building that
		/// changed only the impermeable bit would be invisible to it.
		///
		/// THE ARITHMETIC IS A TRANSCRIPTION, NOT A PARAPHRASE: the same order, the same
		/// constants, the same <c>Mathf</c> calls and the same byte positions as the pass. That is
		/// the one thing in this class that has to be exact, and it is also the one thing
		/// <see cref="Verify"/> can prove, because a mistranscribed byte shows up as a false clean.
		/// The <see cref="MaxSkippedFrames"/> cap is the brace to that belt: if this function ever
		/// drifts from the pass it copies, the drift shows as at most half a second of stale tint
		/// rather than as a permanently wrong overlay.
		/// </summary>
		private unsafe static bool MassDigestSweep(int x0, int y0, int x1, int y1)
		{
			Pass p = mass;

			if (!p.everRan || !p.shadowValid || p.shadow == null || p.bpp != MassBytesPerPixel)
				return false;

			// The shadow only describes what the last run drew, so anything outside it is unknown
			// rather than unchanged. A camera pan or a zoom out lands here.
			if (x0 < p.x0 || y0 < p.y0 || x1 > p.x1 || y1 > p.y1)
				return false;

			ClusterManager cluster = ClusterManager.Instance;
			Element[] elements = Grid.Element;
			byte[] worldIdx = Grid.WorldIdx;
			int width = Grid.WidthInCells;
			int cells = Grid.CellCount;

			if (cluster == null || elements == null || worldIdx == null || width <= 0 || cells <= 0)
				return false;

			if (elements.Length < cells || worldIdx.Length < cells
				|| p.shadow.Length < cells * MassBytesPerPixel)
			{
				return false;
			}

			float* massKg = Grid.mass;
			byte* properties = Grid.properties;

			if (massKg == null || properties == null)
				return false;

			if (x0 < 0 || y0 < 0 || x0 > x1 || y0 > y1 || x1 >= width)
				return false;

			if ((long)y1 * width + x1 >= cells)
				return false;

			// Hoisted out of the loop exactly as Grid.IsActiveWorld computes it, minus the null
			// check it repeats per cell. Nothing can replace the ClusterManager mid-sweep: this
			// runs on the game thread inside LateUpdate.
			int activeWorld = cluster.activeWorldId;
			long compared = 0L;
			bool clean = true;

			fixed (byte* shadow = p.shadow)
			fixed (byte* world = worldIdx)
			{
				for (int y = y0; y <= y1 && clean; y++)
				{
					int rowBase = y * width;

					for (int x = x0; x <= x1; x++)
					{
						int cell = rowBase + x;

						byte solid = 0;
						byte liquid = 0;
						byte gas = 0;
						byte alpha = 0;

						if (world[cell] == activeWorld)
						{
							Element element = elements[cell];

							if (element == null)
							{
								clean = false;
								break;
							}

							// ONE FIELD READ WHERE THE PASS MAKES UP TO FOUR PROPERTY CALLS, and
							// it is the same test. Element.IsSolid, IsLiquid, IsGas and IsVacuum
							// are each `(state & State.Solid)` compared against a constant, and
							// State.Solid is 3 -- so masking the field once and comparing the
							// result is not an approximation of those four properties, it is
							// their shared body hoisted. This loop runs over every cell of the
							// rectangle on every frame and Mono charges for every call it does
							// not inline, which is the whole reason the first version of this
							// sweep cost 0.177 ms.
							int phase = (int)element.state & 3;

							// Grid.LiquidImpermeable is this bit of the property byte; read
							// through the pointer so the sweep is not a static field load a cell.
							bool impermeable = (properties[cell] & 2) != 0;
							bool isSolid = phase == 3;

							if (isSolid || impermeable)
							{
								if (isSolid)
									solid = byte.MaxValue;

								if (impermeable)
								{
									solid = 250;

									// IsGas || IsVacuum
									if (phase <= 1)
										solid = 230;
								}
							}
							else if (phase == 2)
							{
								liquid = byte.MaxValue;
							}
							else
							{
								// Not solid and not liquid leaves gas and vacuum, which are the
								// pass's last branch and its only remaining cases.
								gas = byte.MaxValue;
							}

							float kg = massKg[cell];

							// Mathf.Min(1f, x) and Mathf.Max(floor, x) written out. Unity defines
							// both as the plain ternary, so this is the same comparison without a
							// call, and Verify is what proves it: a byte that came out different
							// from the pass's would be counted as a false clean.
							float ramp = kg / MassRampKg;
							if (1f < ramp)
								ramp = 1f;

							if (kg > 0f && MassAlphaFloor > ramp)
								ramp = MassAlphaFloor;

							alpha = (byte)(ramp * 255f);
						}

						compared++;

						// One four-byte compare instead of four one-byte compares, in the byte
						// order TextureRegion.SetBytes writes and a little-endian load reads. On a
						// big-endian machine the two would never agree and nothing would ever be
						// skipped, which is the safe direction to be wrong in; the game ships x64
						// only.
						uint want = solid | ((uint)liquid << 8) | ((uint)gas << 16) | ((uint)alpha << 24);

						if (*(uint*)(shadow + cell * MassBytesPerPixel) != want)
						{
							clean = false;
							break;
						}
					}
				}
			}

			digestCells += compared;
			return clean;
		}

		// ------------------------------------------------------------------ verification

		private static void EnsureShadow(Pass p, int bpp)
		{
			int cells = Grid.CellCount;
			if (bpp <= 0 || bpp > 4 || cells <= 0)
			{
				p.shadow = null;
				p.shadowValid = false;
				return;
			}

			if (p.shadow == null || p.bpp != bpp || p.shadow.Length != cells * bpp)
			{
				p.bpp = bpp;
				p.shadow = new byte[cells * bpp];
				p.shadowValid = false;

				// A fresh shadow makes the first comparison meaningless, so do not let the first
				// run after one be judged.
				p.claimedClean = false;
			}
		}

		/// <summary>
		/// One 16-row strip, on a worker thread. Compares what the pass wrote against what the
		/// render texture already held; if the detector claimed the pass was clean and a byte
		/// differs, the detector was wrong and that is the only number worth reporting.
		///
		/// Wired by <see cref="InstallVerify"/> rather than by <see cref="Install"/>, because a
		/// per-cell compare on every strip is not something to leave patched in.
		/// </summary>
		private static void VerifyStripPostfix(TextureRegion region, int x0, int y0, int x1, int y1)
		{
			Pass p = inFlight;
			if (p == null)
				return;

			try
			{
				byte[] shadow = p.shadow;
				int bpp = p.bpp;
				NativeArray<byte> bytes = region.bytes;
				int pageWidth = region.pageWidth;
				int rx = region.x;
				int ry = region.y;
				int gridWidth = Grid.WidthInCells;
				int limit = bytes.Length;
				long wrong = 0L;

				for (int y = y0; y <= y1; y++)
				{
					int rowBase = ((y - ry) * pageWidth - rx) * bpp;
					int cellBase = y * gridWidth;

					for (int x = x0; x <= x1; x++)
					{
						int idx = rowBase + x * bpp;
						if (idx < 0 || idx + bpp > limit)
							continue;

						int cell = cellBase + x;
						if (cell < 0 || (cell + 1) * bpp > shadow.Length)
							continue;

						int shadowIdx = cell * bpp;
						for (int b = 0; b < bpp; b++)
						{
							byte v = bytes[idx + b];
							if (shadow[shadowIdx + b] != v)
							{
								shadow[shadowIdx + b] = v;
								if (p.claimedClean)
									wrong++;
							}
						}
					}
				}

				if (wrong != 0L)
				{
					System.Threading.Interlocked.Add(ref p.falseClean, wrong);
					System.Threading.Interlocked.Add(ref falseClean, wrong);
				}
			}
			catch
			{
			}
		}

		/// <summary>
		/// Adds the per-strip comparison. Separate from <see cref="Install"/> so that verification
		/// is opt-in at the patch level and costs literally nothing when it is not wanted.
		/// </summary>
		public static void InstallVerify()
		{
			if (!installed)
				return;

			// Patching twice would run the comparison twice per strip and double every count,
			// which is exactly the kind of quiet arithmetic error a verification mode must not
			// have.
			if (verifyInstalled)
			{
				Verify = true;
				return;
			}

			try
			{
				Type[] sig =
				{
					typeof(TextureRegion), typeof(int), typeof(int), typeof(int), typeof(int)
				};

				HarmonyMethod post = new HarmonyMethod(Self("VerifyStripPostfix"));

				harmony.Patch(AccessTools.Method(typeof(PropertyTextures), DigCallback, sig), null, post);
				harmony.Patch(AccessTools.Method(typeof(PropertyTextures), FogCallback, sig), null, post);

				// UpdateSolidLiquidGasMass is overloaded -- the five-argument entry point forwards
				// to a six-argument body carrying the impermeable flag -- so the signature is what
				// picks it, and the five-argument one is the one the dispatcher holds a delegate
				// to and therefore the one the strips actually enter through.
				harmony.Patch(AccessTools.Method(typeof(PropertyTextures), MassCallback, sig), null, post);
				harmony.Patch(AccessTools.Method(typeof(PropertyTextures), MassLightCallback, sig), null, post);

				verifyInstalled = true;
				Verify = true;
				FrameworkLog.Info("OverlayTextureCache verification armed; passes will NOT be skipped");
			}
			catch (Exception e)
			{
				Verify = false;
				FrameworkLog.Warn("OverlayTextureCache verification could not be armed: " + e.Message);
			}
		}

		// ------------------------------------------------------------------ reporting

		/// <summary>One block of plain text for a debug endpoint or a log line.</summary>
		public static string StatusText()
		{
			StringBuilder sb = new StringBuilder(512);

			sb.AppendFormat("overlay texture cache   installed {0}  enabled {1}  verify {2}  cap {3} frames\n",
				installed, Enabled, Verify, MaxSkippedFrames);

			Append(sb, dig);
			Append(sb, fog);
			Append(sb, mass);
			Append(sb, massLight);

			sb.AppendFormat("total                   {0:n0} skipped, {1:n0} ran, {2:n0} false clean\n",
				skipped, ran, falseClean);

			sb.AppendFormat("dirtied by              solid {0:n0}  damage {1:n0}  backwall {2:n0}  reveal {3:n0}  world {4:n0}\n",
				bumpSolid, bumpDamage, bumpBackwall, bumpReveal, bumpWorld);
			sb.AppendFormat("off screen, ignored     {0:n0}\n", filtered);
			sb.AppendFormat("refills checked         {0:n0}, wrong bytes {1:n0}, refused {2:n0}\n",
				refillsChecked, refillWrong, refillRefused);
			sb.AppendFormat("mass digest             {0:n0} sweeps, {1:0.0%} clean, {2:0.000} ms each\n",
				digestSweeps,
				digestSweeps == 0L ? 0.0 : (double)digestCleanSweeps / digestSweeps,
				DigestMillis);
			sb.AppendFormat("                        {0:n0} cells compared before a verdict\n", digestCells);

			if (Verify)
				sb.Append("verification is armed, so nothing is being skipped; false clean must read 0\n");

			return sb.ToString();
		}

		private static void Append(StringBuilder sb, Pass p)
		{
			long total = p.skipped + p.ran;
			sb.AppendFormat("  {0,-32}{1,9:n0} skipped {2,9:n0} ran {3,7:0.0%}   judged {4,9:n0}  false clean {5:n0}\n",
				p.name, p.skipped, p.ran,
				total == 0L ? 0.0 : (double)p.skipped / total,
				p.claimedCleanRuns,
				p.falseClean);
		}
	}
}
