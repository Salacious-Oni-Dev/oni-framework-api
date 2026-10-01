using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// A named colour palette for buildings, and the one call that applies a colour to one.
	///
	/// WHERE THE PALETTE COMES FROM. These sixteen colours are Stationeers' own structure paints,
	/// lifted out of its <c>ColorPaletteArray</c> (a 1024x1024 <c>Texture2DArray</c>, 16 slices,
	/// the only colour array in the install). Stationeers paints a structure by swapping the
	/// renderer's material for a shared texture-array material and setting a per-instance float
	/// <c>_DiffuseIndex</c> to a slice number -- the colour is an INDEX, never an RGB value. The
	/// serialized prefab material is not even the material the game draws.
	///
	/// WHY ONI NEEDS NONE OF THAT MACHINERY. <c>KAnimControllerBase.TintColour</c> already exists
	/// and is already per-instance: it writes through <c>batchInstanceData.SetTintColour</c>, so a
	/// tinted building costs no extra draw call and no custom shader. Porting Stationeers'
	/// texture-array path would mean writing a shader ONI does not need and batches worse. So the
	/// only thing worth taking from Stationeers here is the palette itself, which is a coherent
	/// sixteen-colour set somebody already art-directed.
	///
	/// TWO WAYS TO APPLY A COLOUR, AND WHICH ONE A BUILDING GETS.
	/// <see cref="TryApply(GameObject,int)"/> tints the WHOLE anim, because that is what
	/// <c>TintColour</c> does. Stationeers is more precise: it repaints only the renderer slots
	/// whose material matches the prefab's <c>PaintableMaterial</c>, which is why a painted
	/// machine there changes its accent panels and not its warning decals.
	/// <see cref="TryApplyToSymbol"/> matches that, and is what a building should use when its
	/// art was packed with a paint symbol -- the asset pipeline splits a render into the
	/// spraypaintable geometry and everything else using the same <c>PaintableMaterial</c> field,
	/// read straight out of the Stationeers prefab. <see cref="TryApplyBest"/> picks between them.
	///
	/// WHY THE SYMBOL IS NAMED AFTER THE BUILD (<see cref="PaintSymbolFor"/>) rather than being a
	/// bare "paint". ONI resolves <c>SetSymbolTint</c> through <c>KBatchGroupData.GetSymbol</c>,
	/// and that dictionary is keyed by symbol hash across the whole BATCH GROUP, not per build --
	/// <c>AddBuildSymbol</c> keeps only the first build to claim a hash. Two builds in one batch
	/// group sharing a symbol name would resolve to one of them and tint the wrong symbol index on
	/// the other. A build-unique name costs nothing and removes the failure mode.
	///
	/// A TINT IS A MULTIPLY, not a replacement. White therefore means "unpainted" and is the only
	/// entry that leaves a building's art exactly as authored; every other entry darkens as well
	/// as colours it, more so the more saturated it is. That is a property of the mechanism, not a
	/// bug to correct here -- a consumer that wants a lighter result should pick a lighter swatch.
	/// </summary>
	public static class BuildingPaint
	{
		/// <summary>
		/// One entry in the palette: a stable index, a display name, and the tint to apply.
		///
		/// <see cref="Index"/> is the <c>ColorPaletteArray</c> slice number, kept rather than
		/// renumbered so a colour saved by a consumer keeps meaning the same colour if entries are
		/// ever added, and so a value here can be checked against Stationeers directly.
		/// </summary>
		public struct Swatch
		{
			public readonly int Index;
			public readonly string Name;
			public readonly Color32 Colour;

			public Swatch(int index, string name, byte r, byte g, byte b)
			{
				Index = index;
				Name = name;
				Colour = new Color32(r, g, b, 255);
			}
		}

		/// <summary>
		/// Slice 6, white -- a no-op tint, and the value a building carries when unpainted.
		/// </summary>
		public const int Unpainted = 6;

		// The eight slices whose names are Stationeers' own, established by pixel identity against
		// its placeholder palette textures rather than by eye. Named so a building config can state
		// its factory colour as BuildingPaint.Orange instead of a bare 3. The other eight slices
		// have no confirmed name and deliberately get no constant.
		public const int Blue = 0;
		public const int Gray = 1;
		public const int Green = 2;
		public const int Orange = 3;
		public const int Red = 4;
		public const int Yellow = 5;
		public const int White = 6;
		public const int Black = 7;

		// Slices 0-7 are confirmed by pixel identity: each of Stationeers' own placeholder palette
		// textures (color_blue, color_gray, color_green, color_orange, color_red, color_yellow,
		// color_white, color_black) is bit-identical to the slice named here -- mean absolute
		// difference 0.00 across all 1024x1024 pixels, with the next-closest slice no nearer than
		// 11.9. Their names are therefore Stationeers'.
		//
		// Slices 8-15 have no placeholder texture in the extracted set, so nothing pins their real
		// DisplayName, which lives in GameManager.CustomColors and was not read. THOSE EIGHT NAMES
		// ARE DESCRIPTIVE, chosen from the sampled colour, and may not match what Stationeers calls
		// them. The RGB values are measured either way: the most saturated pixel in each slice's
		// paint ramp.
		private static readonly Swatch[] palette =
		{
			new Swatch(0,  "Blue",     0,   60,  111),
			new Swatch(1,  "Gray",     244, 245, 244),
			new Swatch(2,  "Green",    93,  193, 85),
			new Swatch(3,  "Orange",   255, 102, 44),
			new Swatch(4,  "Red",      231, 2,   0),
			new Swatch(5,  "Yellow",   255, 188, 27),
			new Swatch(6,  "White",    255, 255, 255),
			new Swatch(7,  "Black",    53,  52,  53),
			new Swatch(8,  "Brown",    84,  43,  27),
			new Swatch(9,  "Khaki",    126, 123, 84),
			new Swatch(10, "Pink",     201, 0,   131),
			new Swatch(11, "Purple",   145, 70,  208),
			new Swatch(12, "Gunmetal", 96,  101, 110),
			new Swatch(13, "Silver",   216, 220, 228),
			new Swatch(14, "Bronze",   184, 115, 62),
			new Swatch(15, "Gold",     225, 174, 66)
		};

		private static readonly IReadOnlyList<Swatch> readOnlyPalette = System.Array.AsReadOnly(palette);

		/// <summary>
		/// Every swatch, in slice order. Index into this with <see cref="Swatch.Index"/>; the two
		/// agree today and <see cref="Get"/> does not assume they always will.
		/// </summary>
		public static IReadOnlyList<Swatch> Palette
		{
			get { return readOnlyPalette; }
		}

		/// <summary>
		/// How many swatches there are. Use this to bound a cycle rather than hardcoding 16.
		/// </summary>
		public static int Count
		{
			get { return palette.Length; }
		}

		/// <summary>
		/// The swatch with this slice index, or the <see cref="Unpainted"/> swatch when the index
		/// names nothing. Returns a valid swatch for every input on purpose: a save carrying an
		/// index from a future palette should render as unpainted, not throw in OnSpawn.
		/// </summary>
		public static Swatch Get(int index)
		{
			for (int i = 0; i < palette.Length; i++)
			{
				if (palette[i].Index == index)
				{
					return palette[i];
				}
			}
			return palette[Unpainted];
		}

		/// <summary>
		/// True when this index names a real swatch. Distinguishes "unpainted" from "unrecognised",
		/// which <see cref="Get"/> deliberately collapses.
		/// </summary>
		public static bool IsValid(int index)
		{
			for (int i = 0; i < palette.Length; i++)
			{
				if (palette[i].Index == index)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Tint every symbol of this building's anim to the given swatch. Returns false when the
		/// object has no <c>KAnimControllerBase</c> -- which is not an error worth logging on the
		/// caller's behalf, because a config can legitimately paint a prefab that has no anim yet.
		/// </summary>
		public static bool TryApply(GameObject go, int index)
		{
			if (go == null)
			{
				return false;
			}
			return TryApply(go.GetComponent<KAnimControllerBase>(), index);
		}

		/// <summary>
		/// Controller overload, for a caller that already has one -- an <c>OnSpawn</c> that resolved
		/// it once, or a building with several controllers where only one should be painted.
		/// </summary>
		public static bool TryApply(KAnimControllerBase controller, int index)
		{
			if (controller == null)
			{
				return false;
			}
			controller.TintColour = Get(index).Colour;
			return true;
		}

		/// <summary>
		/// The suffix the asset pipeline appends to a build's name to name its paint symbol.
		/// Kept here rather than in the pipeline alone because both sides have to agree, and this
		/// is the side a consumer mod can read.
		/// </summary>
		public const string PaintSymbolSuffix = "_paint";

		/// <summary>
		/// The suffix ONI's own loader appends to a mod anim directory's name to form the
		/// <c>KAnimFile</c> asset name -- `anim/assets/StructureStirlingEngine/` loads as
		/// "StructureStirlingEngine_kanim".
		/// </summary>
		public const string AnimFileSuffix = "_kanim";

		/// <summary>
		/// The paint symbol name for a build -- e.g. "StructureStirlingEngine" gives
		/// "StructureStirlingEngine_paint". Build-unique on purpose; see the type docstring.
		///
		/// STRIPS A TRAILING "_kanim" FIRST, and that is not cosmetic. Symbol names come from the
		/// SCML the asset pipeline writes, which uses the source directory's name
		/// ("StructureStirlingEngine"), while the name on the loaded <c>KAnimFile</c> carries
		/// ONI's own "_kanim" suffix. A caller passing the KAnimFile's name -- the obvious thing
		/// to pass, since it is what a controller can actually tell you -- would otherwise ask for
		/// "StructureStirlingEngine_kanim_paint", which resolves to nothing, and the failure would
		/// look exactly like a building whose art has no paint symbol. So both spellings are
		/// accepted here rather than left for every consumer to get wrong.
		/// </summary>
		public static string PaintSymbolFor(string animName)
		{
			if (animName == null)
			{
				return null;
			}
			if (animName.EndsWith(AnimFileSuffix))
			{
				animName = animName.Substring(0, animName.Length - AnimFileSuffix.Length);
			}
			return animName + PaintSymbolSuffix;
		}

		/// <summary>
		/// True when this controller's batch group can resolve the named symbol, which is the
		/// exact condition under which <c>SetSymbolTint</c> does anything.
		///
		/// Worth checking rather than calling blind: <c>SetSymbolTint</c> silently returns when
		/// the symbol is missing, so a building whose art was packed WITHOUT a paint symbol would
		/// otherwise look like it had simply been painted white, with nothing to distinguish that
		/// from a real no-op. This lets a caller fall back to the whole-anim tint instead.
		/// </summary>
		public static bool HasSymbol(KBatchedAnimController controller, KAnimHashedString symbol)
		{
			if (controller == null)
			{
				return false;
			}
			KAnimBatchManager manager = KAnimBatchManager.Instance();
			if (manager == null)
			{
				return false;
			}
			KBatchGroupData group = manager.GetBatchGroupData(controller.GetBatchGroupID());
			if (group == null)
			{
				return false;
			}
			return group.GetSymbol(symbol) != null;
		}

		/// <summary>
		/// Tint ONE symbol of this building's anim, leaving every other symbol as authored. This
		/// is the Stationeers-equivalent behaviour: only the spraypaintable geometry changes
		/// colour, and displays, warning decals and glass keep theirs.
		///
		/// Returns false when the symbol does not exist in this build, so a caller can fall back;
		/// see <see cref="TryApplyBest"/>, which does exactly that. Safe alongside the whole-anim
		/// tint: every symbol's instance colour starts at white (<c>SymbolInstanceGpuData</c>'s
		/// constructor sets it), so tinting one symbol never disturbs the others.
		/// </summary>
		public static bool TryApplyToSymbol(KBatchedAnimController controller, KAnimHashedString symbol, int index)
		{
			if (!HasSymbol(controller, symbol))
			{
				return false;
			}
			controller.SetSymbolTint(symbol, Get(index).Colour);
			return true;
		}

		/// <summary>
		/// The build this controller is actually drawing, or null when it has none yet.
		///
		/// <c>curBuild</c> is the authority and is public, but it is only populated once the
		/// controller has set its anim up, and a <c>PaintableBuilding.OnSpawn</c> is not
		/// guaranteed to run after that. So the anim file's own build is the fallback: it is
		/// resolved from the batch group rather than from controller state, and is therefore
		/// available as soon as the kanim is loaded.
		/// </summary>
		private static KAnim.Build ResolveBuild(KBatchedAnimController controller)
		{
			if (controller == null)
			{
				return null;
			}
			if (controller.curBuild != null)
			{
				return controller.curBuild;
			}
			KAnimFile[] files = controller.AnimFiles;
			if (files == null || files.Length == 0 || files[0] == null)
			{
				return null;
			}
			KAnimFileData data = files[0].GetData();
			return data != null ? data.build : null;
		}

		/// <summary>
		/// Tint EVERY symbol of this building's own build, which is how a building with no paint
		/// symbol gets painted.
		///
		/// WHY NOT JUST SET <c>TintColour</c>, which does the same thing in one line. Because ONI
		/// already owns that field on a completed building and takes it back without warning:
		/// <c>OverlayModes.Mode.ResetDisplayValues</c> assigns <c>TintColour = Color.white</c>, and
		/// the power, plumbing, ventilation, conveyor and suit overlays drive it live on every
		/// building they collect. A colour written there survives until the player opens an
		/// overlay and closes it again. <c>Ownable</c> (bed assignment), <c>TreeFilterable</c>
		/// (storage filters) and <c>DesiccationMonitor</c> own it on their buildings permanently.
		/// The per-symbol channel is a different piece of instance state --
		/// <c>SymbolInstanceGpuData</c>, one colour per symbol -- and nothing in vanilla clears it
		/// wholesale, so paint written there stays written.
		///
		/// WHY THIS DOES NOT SIMPLY CALL <see cref="TryApplyToSymbol"/> IN A LOOP.
		/// <c>KBatchedAnimController.SetSymbolTint</c> resolves a name through
		/// <c>KBatchGroupData.GetSymbol</c>, whose dictionary is keyed by hash across the whole
		/// BATCH GROUP and whose <c>AddBuildSymbol</c> keeps only the FIRST build to claim a hash.
		/// A batch group holds up to thirty builds, grouped by anim home directory, and vanilla
		/// builds share ordinary symbol names constantly. So for a symbol some other build claimed
		/// first, the resolved <c>symbolIndexInSourceBuild</c> belongs to that other build, and
		/// writing it tints an unrelated symbol of ours -- or none. Comparing the resolved symbol
		/// against the one we are iterating detects exactly that case, and the index straight off
		/// our own build is used instead. Klei's own path is kept where it is correct because it
		/// also un-suspends the controller.
		/// </summary>
		/// <summary>
		/// The tint that actually lands a swatch on this art, rather than the swatch itself.
		///
		/// WHY THE RAW SWATCH IS THE WRONG VALUE TO SEND. A kanim tint is a per-channel multiply
		/// into art that is already mid-dark -- measured mean value 0.36 to 0.57 across seven
		/// vanilla buildings -- and most swatches are themselves dark in at least two channels.
		/// The Blue swatch is (0, 60, 111): multiplying a warm chassis by it zeroes the red
		/// channel the art's brightness mostly lives in, and the building lands near black. That
		/// is the muddiness, and it is not hue crushing from coloured art as first assumed -- it
		/// is brightness loss.
		///
		/// THE THREE STEPS.
		/// 1. HUE. Normalise the swatch so its brightest channel is 1: the colour is carried by
		///    the RATIO between channels, and scaling all three does not change the hue.
		/// 2. COMPENSATION. Predict what that hue tint does to this build's mean art colour, and
		///    scale up so the result keeps the art's own brightness. This is the step that needs
		///    a measured <c>MeanArt</c> and the step that removes the mud. It relies on the
		///    symbol instance texture being <c>TextureFormat.RGBAFloat</c> (KAnimBatchGroup line
		///    43), so a tint above 1.0 really does brighten instead of clamping.
		/// 3. LIGHTNESS. Put back a fraction of the swatch's own darkness, so Black still reads
		///    darker than Silver. See <c>SymbolPaintAnalysis.SwatchLightnessWeight</c>.
		///
		/// WHITE IS EXACTLY THE IDENTITY and that is load-bearing, not a nicety: unpainted
		/// buildings and overlay-suppressed buildings both go through here. Its hue tint is
		/// white, so the prediction equals the art and the compensation is 1; its value is 1, so
		/// the lightness term is 1. The result is <c>Color.white</c> with no rounding drift.
		/// </summary>
		private static Color CompensatedTint(Color swatch, Color meanArt)
		{
			float peak = Mathf.Max(swatch.r, Mathf.Max(swatch.g, swatch.b));
			if (peak <= 0f)
			{
				return Color.black;
			}

			Color hue = new Color(swatch.r / peak, swatch.g / peak, swatch.b / peak, 1f);

			float artValue = Mathf.Max(meanArt.r, Mathf.Max(meanArt.g, meanArt.b));
			float predicted = Mathf.Max(meanArt.r * hue.r, Mathf.Max(meanArt.g * hue.g, meanArt.b * hue.b));

			// Capped because a hue that lands almost entirely off the art's dominant channel --
			// a pure blue tint on pure orange art -- would otherwise ask for an unbounded scale
			// to recover a brightness that channel simply does not carry.
			float scale = predicted > 0.001f ? Mathf.Clamp(artValue / predicted, 1f, 4f) : 1f;

			// HOW MUCH OF THE SWATCH'S OWN DARKNESS TO KEEP, AND WHY IT DEPENDS ON THE SWATCH.
			//
			// A flat weight cannot serve both ends of the palette, which is what a live look
			// found: at 0.4 the Black swatch landed a mid chassis at 0.36 against art at 0.53 --
			// grey, not black -- while raising the weight to 1 to fix that pushed Blue back down
			// to 0.23 and returned the mud.
			//
			// The two are different cases and the palette says which is which. For a CHROMATIC
			// swatch the player is picking a hue, and its low peak channel is an artifact of how
			// a saturated colour is encoded (Blue is (0,60,111): its peak is 0.435 not because
			// blue is dark but because two channels are spent on being blue). For a NEUTRAL
			// swatch there is no hue at all -- Black is (53,52,53) -- so its value IS the whole
			// of the player's intent and must apply in full, or "black" means "slightly grey".
			//
			// Scaling the weight by the swatch's own saturation puts each end where it belongs:
			// Black and Gray keep all their lightness, Blue and Purple keep 40% of theirs, and
			// White stays the exact identity from both directions at once.
			float trough = Mathf.Min(swatch.r, Mathf.Min(swatch.g, swatch.b));
			float swatchSaturation = (peak - trough) / peak;
			float weight = Mathf.Lerp(1f, SymbolPaintAnalysis.SwatchLightnessWeight, swatchSaturation);

			float lightness = Mathf.Lerp(1f, peak, weight);
			float k = scale * lightness;

			return new Color(hue.r * k, hue.g * k, hue.b * k, 1f);
		}

		private static bool WriteBuildTint(KBatchedAnimController controller, Color colour)
		{
			KAnim.Build build = ResolveBuild(controller);
			if (build == null || build.symbols == null || build.symbols.Length == 0)
			{
				return false;
			}
			SymbolInstanceGpuData gpu = controller.symbolInstanceGpuData;
			if (gpu == null)
			{
				return false;
			}
			KAnimBatchManager manager = KAnimBatchManager.Instance();
			KBatchGroupData group = manager != null
				? manager.GetBatchGroupData(controller.GetBatchGroupID())
				: null;
			if (group == null)
			{
				return false;
			}

			// Which symbols take paint, and how bright their art is. Null means the atlas could
			// not be read: fall back to the old behaviour of painting every symbol with the raw
			// swatch, because a muddy building is still a painted building and a paint button
			// that does nothing is not.
			SymbolPaintAnalysis.BuildProfile profile = SymbolPaintAnalysis.Profile(build, group);

			Color paintTint = profile != null ? CompensatedTint(colour, profile.MeanArt) : colour;

			// The gpu data is sized to maxSymbolsPerBuild (KBatchedAnimController line 226), and
			// SymbolInstanceGpuData.SetSymbolTint guards the bound with a DebugUtil.Assert that
			// does not stop the indexer from throwing. Bound it here instead.
			int limit = group.maxSymbolsPerBuild;
			bool wroteAny = false;
			for (int i = 0; i < build.symbols.Length; i++)
			{
				KAnim.Build.Symbol symbol = build.symbols[i];
				if (symbol == null)
				{
					continue;
				}

				// A symbol that does not take paint is written WHITE rather than skipped. Skipping
				// would leave the previous colour on it when the player repaints, which is how a
				// readout ends up permanently stained by a colour the building no longer wears.
				bool takesPaint = profile == null || profile.Paintable.Contains(symbol.hash.HashValue);
				Color value = takesPaint ? paintTint : Color.white;

				if (group.GetSymbol(symbol.hash) == symbol)
				{
					controller.SetSymbolTint(symbol.hash, value);
					wroteAny = true;
				}
				else if (symbol.symbolIndexInSourceBuild >= 0 && symbol.symbolIndexInSourceBuild < limit)
				{
					gpu.SetSymbolTint(symbol.symbolIndexInSourceBuild, value);
					wroteAny = true;
				}
			}
			if (wroteAny)
			{
				controller.SetDirty();
			}
			return wroteAny;
		}

		/// <summary>
		/// Tint every symbol of this building's build to the given swatch. This is the fallback
		/// for art that was not packed with a paint symbol -- which is every vanilla building --
		/// and it is what makes a colour stick where <see cref="TryApply"/> would be overwritten
		/// by the next overlay. See <c>WriteBuildTint</c> for why the symbol channel and not
		/// <c>TintColour</c>.
		/// </summary>
		public static bool TryApplyToBuild(KBatchedAnimController controller, int index)
		{
			if (controller == null)
			{
				return false;
			}
			return WriteBuildTint(controller, Get(index).Colour);
		}

		/// <summary>
		/// Put every symbol of this build back to white, undoing <see cref="TryApplyToBuild"/>.
		///
		/// White is the identity for a multiply, so this is a true restore rather than an
		/// approximation -- with one caveat worth stating: it also clears a symbol tint some
		/// OTHER component set on this build, such as <c>LogicDiseaseSensor</c>'s "germs" or
		/// <c>SpiceGrinder</c>'s stripes. Those components reassert their colour on their own
		/// update, so the loss is transient; a consumer that cannot tolerate even that should
		/// not paint those buildings.
		/// </summary>
		public static bool ClearBuildTint(KBatchedAnimController controller)
		{
			if (controller == null)
			{
				return false;
			}
			return WriteBuildTint(controller, Color.white);
		}

		/// <summary>
		/// Paint this building the most precise way its art allows: one symbol if the build was
		/// packed with a paint symbol, every symbol of the build otherwise. Returns the mode
		/// actually used, so a caller can say which -- or say nothing, and simply get the better
		/// result on buildings whose art has been through the paint-aware pipeline.
		///
		/// The whole-anim tint is reset to white in both symbol cases. Both tints multiply into
		/// the same pixel, so leaving a stale <c>TintColour</c> behind would keep colouring the
		/// parts that are supposed to stop being coloured -- which is exactly what happens to a
		/// building whose art gains a paint symbol after it was already painted whole.
		///
		/// <see cref="PaintMode.WholeAnim"/> is now only reached when the build cannot be
		/// resolved at all, and is retained because it is the one path that works before a
		/// controller has an anim. It should be treated as a degraded result: a colour applied
		/// that way does not survive an overlay.
		/// </summary>
		public static PaintMode TryApplyBest(KBatchedAnimController controller, string animName, int index)
		{
			if (controller == null)
			{
				return PaintMode.None;
			}
			if (!string.IsNullOrEmpty(animName)
				&& TryApplyToSymbol(controller, new KAnimHashedString(PaintSymbolFor(animName)), index))
			{
				controller.TintColour = Color.white;
				return PaintMode.Symbol;
			}
			if (TryApplyToBuild(controller, index))
			{
				controller.TintColour = Color.white;
				return PaintMode.AllSymbols;
			}
			return TryApply(controller, index) ? PaintMode.WholeAnim : PaintMode.None;
		}

		/// <summary>
		/// How a colour actually got applied. Reported rather than assumed because the answer
		/// depends on the art, not the code: the same building becomes <see cref="Symbol"/>
		/// instead of <see cref="WholeAnim"/> the moment its kanim is repacked with a paint
		/// symbol, with no code change.
		/// </summary>
		public enum PaintMode
		{
			/// <summary>Nothing was painted -- no controller, or no anim to tint.</summary>
			None,
			/// <summary>
			/// The whole-anim <c>TintColour</c> was set, because the build could not be resolved.
			/// A degraded result: ONI's overlay modes reset that field, so the colour will not
			/// survive the player opening and closing an overlay.
			/// </summary>
			WholeAnim,
			/// <summary>Only the spraypaintable symbol tinted, as Stationeers does it.</summary>
			Symbol,
			/// <summary>
			/// Every symbol of the build tinted, because this build has no paint symbol. Looks
			/// like <see cref="WholeAnim"/> and behaves unlike it: written to the per-symbol
			/// channel, which nothing in vanilla clears.
			/// </summary>
			AllSymbols
		}

		/// <summary>
		/// The next swatch after this one, wrapping. Exists so a cycle control does not have to
		/// know whether indices are contiguous.
		/// </summary>
		public static int Next(int index)
		{
			for (int i = 0; i < palette.Length; i++)
			{
				if (palette[i].Index == index)
				{
					return palette[(i + 1) % palette.Length].Index;
				}
			}
			return palette[0].Index;
		}
	}
}
