using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Decides WHICH symbols of a kanim build are spraypaintable, by looking at the pixels.
	///
	/// THE PROBLEM THIS SOLVES. A kanim tint is a per-channel multiply, and vanilla ONI building
	/// art is already coloured. Multiplying all of it by a saturated swatch does not recolour the
	/// building, it crushes it: a Gas Pump's orange trim (255,102,44) multiplied by the Blue
	/// swatch (0,60,111) lands on (0,24,19), which is black. Every warm pixel in the building
	/// dies because the swatch has no red in it. The result reads as a dark silhouette with the
	/// detail gone -- confirmed on a live colony rather than predicted.
	///
	/// WHAT STATIONEERS DOES INSTEAD, and why this is the same idea. Stationeers repaints only
	/// the renderer slots whose material matches the prefab's <c>PaintableMaterial</c>, which is
	/// why a painted machine there changes its chassis panels and keeps its warning decals and
	/// displays. Our own asset pipeline reproduces that for ported buildings by packing a
	/// <c>_paint</c> symbol. Klei's kanims carry no such split and no usable metadata for one --
	/// <c>KAnim.Build.Symbol.colourChannel</c> exists in the format, is read by
	/// <c>KGlobalAnimParser</c>, and is then used by nothing in the entire assembly. So the split
	/// has to be DERIVED, and the only ground truth available is the art itself.
	///
	/// THE RULE. A symbol is paintable when its opaque pixels are close to neutral -- low
	/// saturation -- because neutral pixels are the ones a multiply recolours cleanly rather than
	/// crushes. Strongly coloured symbols are exactly the ones that carry meaning: displays,
	/// indicator lights, warning stripes, liquid in a window. Leaving them alone is not a
	/// compromise forced by the shader, it is the behaviour we want and the behaviour Stationeers
	/// has.
	///
	/// THE THRESHOLD IS NOT GUESSED. See <see cref="SaturationThreshold"/>.
	///
	/// COST, AND WHY IT IS ACCEPTABLE. Classifying a build means reading its atlas pixels, which
	/// is expensive and which is why it happens lazily, once per BUILD (not per building), the
	/// first time something asks -- and the answer is cached for the rest of the session. A
	/// colony where the player never paints anything never runs this at all. The pixel buffers
	/// are local to one build's pass and dropped at the end of it, so the cache holds a set of
	/// symbol hashes per build and no textures.
	/// </summary>
	public static class SymbolPaintAnalysis
	{
		/// <summary>
		/// Per-symbol measurements, in the units a threshold can be argued about.
		/// </summary>
		public struct SymbolStats
		{
			public string Name;
			public int Hash;
			public int Sampled;
			public int Opaque;
			public float MeanR;
			public float MeanG;
			public float MeanB;
			/// <summary>HSV saturation of the mean opaque colour, 0 (grey) to 1 (pure hue).</summary>
			public float MeanSaturation;
			/// <summary>HSV value of the mean opaque colour: how bright the symbol is.</summary>
			public float MeanValue;
			public bool Paintable;
		}

		/// <summary>
		/// Above this HSV saturation a symbol is treated as carrying colour of its own and is
		/// left unpainted.
		///
		/// MEASURED, NOT GUESSED. Read off seven vanilla buildings through the debug inspector's
		/// <c>/symbols/{id}</c> route. The distribution has two clusters and they
		/// do not overlap:
		///
		///   chassis and structure   0.00 - 0.60   (StorageLocker body 0.51, door 0.56, leg 0.52;
		///                                          BatteryMedium base 0.53, top 0.54;
		///                                          Electrolyzer body 0.58, exhaust 0.59;
		///                                          every GasPump symbol 0.00 - 0.24)
		///   readouts and indicators 0.68 - 1.00   (BatteryMedium meter_fill 1.00 green,
		///                                          StorageLocker meter_level 0.83 green,
		///                                          SolarPanel glow_object 0.81 cyan,
		///                                          MicrobeMusher bulb_bloom 0.70 yellow,
		///                                          LiquidPumpingStation handle 0.68)
		///
		/// 0.65 sits in the gap. The first draft of this file guessed 0.35, which would have
		/// classified most CHASSIS as coloured and painted almost nothing -- Klei's machinery is
		/// warm brown, not neutral grey, and the earlier arithmetic here that assumed otherwise
		/// was wrong about real ONI art.
		///
		/// SATURATION ALONE IS NOT ENOUGH, though, and the wider sample said so: widening from
		/// four buildings to ten put structure on the wrong side of this line. See
		/// <see cref="ValueThreshold"/>.
		/// </summary>
		public static float SaturationThreshold = 0.65f;

		/// <summary>
		/// A symbol must also be brighter than this to count as a readout rather than as dark
		/// coloured structure.
		///
		/// WHY A SECOND AXIS. The "clean gap" claimed for saturation alone was an artifact of a
		/// four-building sample. At ten buildings it closed: dark green pipework and dark brown
		/// legs sit in the same saturation band as indicators.
		///
		///   spared correctly, saturated AND bright   meter_fill 1.00/0.80, meter_level 0.83/0.88,
		///                                            glow_object 0.81/0.99, bulb_bloom 0.70/1.00,
		///                                            light 0.85/0.63, filterwater 0.73/0.91
		///   structure, saturated but DARK            LiquidPumpingStation handle 0.68/0.23,
		///                                            spine 0.68/0.24, spout 0.68/0.19,
		///                                            PowerTransformer leg 0.73/0.14, ui 0.68/0.24,
		///                                            BatteryMedium knob 0.76/0.31, wire2 0.63/0.24,
		///                                            RationBox lid 0.68/0.46, button 0.67/0.51
		///
		/// Brightness separates them where saturation does not: a readout is saturated AND bright,
		/// dark green pipework is saturated and dim. 0.55 sits between RationBox's button at 0.51
		/// and Electrolyzer's meter_waterlevel at 0.56.
		/// </summary>
		public static float ValueThreshold = 0.55f;

		/// <summary>
		/// Symbol-name fragments that mark a readout regardless of how bright it measures.
		///
		/// THE ONE CLASS TWO AXES STILL MISS. Additive glow sprites are drawn dark because they
		/// are ADDED to what is behind them rather than replacing it, so they measure saturated
		/// and dim -- exactly like the structure the value test is there to catch.
		/// PowerTransformer's <c>bolt_bloom</c> (1.00/0.42), <c>arm_arrow_bloom</c> (1.00/0.54)
		/// and <c>node_small_bloom</c> (1.00/0.40) are indistinguishable from its <c>leg</c>
		/// (0.73/0.14) on the numbers alone.
		///
		/// THIS IS A CONVENTION, NOT A GUARANTEE, and is deliberately the last word rather than
		/// the first. Klei names these consistently across the whole game, so it is reliable for
		/// vanilla; a mod need not follow it, which is why the measurement remains the primary
		/// test and this only ever applies to a symbol the saturation test ALREADY flagged as
		/// coloured. A structural symbol that merely happens to contain "meter" -- Electrolyzer's
		/// <c>u2h_meter_tank</c>, the tank housing at saturation 0.45 -- never reaches this test
		/// and is still painted.
		/// </summary>
		public static string[] IndicatorNameFragments = { "_bloom", "meter", "glow" };

		private static bool NameSuggestsIndicator(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			for (int i = 0; i < IndicatorNameFragments.Length; i++)
			{
				if (name.IndexOf(IndicatorNameFragments[i], StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// How much of a swatch's own darkness is kept when compensating for the multiply.
		///
		/// 0 means a swatch contributes hue only and never darkens; 1 means the raw swatch, which
		/// is the behaviour that measured as muddy. At 0.4 the Blue swatch lands a mid-grey
		/// chassis at about 77% of its original brightness instead of 44%, which reads as painted
		/// blue rather than as unlit. White is unaffected at any weight, because its hue tint is
		/// already white and its value is already 1 -- see <c>BuildingPaint.CompensatedTint</c>.
		/// </summary>
		public static float SwatchLightnessWeight = 0.4f;

		/// <summary>
		/// Cap on pixels sampled per symbol. A classification is a mean over a region; reading
		/// every pixel of a large symbol buys precision the threshold cannot use.
		/// </summary>
		private const int MaxSamplesPerSymbol = 1024;

		/// <summary>Alpha below which a pixel is background and says nothing about the art.</summary>
		private const float OpaqueAlpha = 0.5f;

		/// <summary>
		/// What painting needs to know about one build: which symbols take paint, and how bright
		/// the art it will be multiplied into actually is.
		/// </summary>
		public sealed class BuildProfile
		{
			/// <summary>Hashes of the symbols that take paint.</summary>
			public HashSet<int> Paintable;

			/// <summary>
			/// Mean opaque colour of the paintable symbols, weighted by pixel count. This is the
			/// value the multiply compensation is computed against: without it a tint can only
			/// be guessed at, and the guess is what produced a muddy building.
			/// </summary>
			public Color MeanArt;
		}

		private static readonly Dictionary<int, BuildProfile> profileByBuild =
			new Dictionary<int, BuildProfile>();

		/// <summary>
		/// The paint profile for this build, computed on first ask and cached. Null when the
		/// build's pixels could not be read at all, which a caller should treat as "paint
		/// everything, uncompensated" rather than "paint nothing" -- the old behaviour, muddy
		/// but visible, beats a paint button that silently does nothing.
		/// </summary>
		public static BuildProfile Profile(KAnim.Build build, KBatchGroupData group)
		{
			if (build == null || group == null)
			{
				return null;
			}
			if (profileByBuild.TryGetValue(build.fileHash.HashValue, out BuildProfile cached))
			{
				return cached;
			}

			List<SymbolStats> stats = Analyse(build, group, out string error);
			BuildProfile result = null;
			if (stats != null && error == null)
			{
				var paintable = new HashSet<int>();
				double sumR = 0.0, sumG = 0.0, sumB = 0.0;
				long weight = 0;
				for (int i = 0; i < stats.Count; i++)
				{
					if (!stats[i].Paintable)
					{
						continue;
					}
					paintable.Add(stats[i].Hash);
					int w = stats[i].Opaque;
					sumR += stats[i].MeanR * w;
					sumG += stats[i].MeanG * w;
					sumB += stats[i].MeanB * w;
					weight += w;
				}

				if (weight > 0)
				{
					result = new BuildProfile
					{
						Paintable = paintable,
						MeanArt = new Color(
							(float)(sumR / weight), (float)(sumG / weight), (float)(sumB / weight), 1f)
					};
				}
			}

			profileByBuild[build.fileHash.HashValue] = result;
			return result;
		}

		/// <summary>
		/// Drop every cached classification, so the next paint recomputes. Exists for tuning
		/// <see cref="SaturationThreshold"/> and <see cref="SwatchLightnessWeight"/> against a
		/// live colony without a relaunch.
		/// </summary>
		public static void ClearCache()
		{
			profileByBuild.Clear();
		}

		/// <summary>
		/// Measure every symbol of a build. <paramref name="error"/> is null on success and
		/// carries the reason when the atlas could not be read.
		/// </summary>
		public static List<SymbolStats> Analyse(KAnim.Build build, KBatchGroupData group, out string error)
		{
			error = null;
			if (build == null || build.symbols == null || group == null)
			{
				error = "no build";
				return null;
			}

			// One readable copy per atlas for the duration of this build's pass, then dropped.
			// Builds share atlases across their symbols, so this is the difference between one
			// copy and one per symbol.
			var pixelCache = new Dictionary<int, Color32[]>();
			var sizeCache = new Dictionary<int, Vector2Int>();
			var temporaries = new List<Texture2D>();

			var results = new List<SymbolStats>();
			try
			{
				for (int i = 0; i < build.symbols.Length; i++)
				{
					KAnim.Build.Symbol symbol = build.symbols[i];
					if (symbol == null)
					{
						continue;
					}

					SymbolStats s = new SymbolStats
					{
						Name = symbol.hash.ToString(),
						Hash = symbol.hash.HashValue
					};

					if (symbol.numFrames > 0)
					{
						KAnim.Build.SymbolFrameInstance frame = symbol.GetFrame(0, group);
						Texture2D tex = build.GetTexture(frame.buildImageIdx, group);
						if (tex != null)
						{
							Color32[] pixels = GetPixels(tex, pixelCache, sizeCache, temporaries, out string texError);
							if (pixels == null)
							{
								error = texError;
							}
							else
							{
								Vector2Int size = sizeCache[tex.GetInstanceID()];
								Accumulate(pixels, size, frame.uvMin, frame.uvMax, ref s);
							}
						}
					}

					// A symbol is spared only when it is BOTH saturated and either bright or
					// named as a readout. Anything not saturated enough to reach the first test
					// is structure and takes paint, whatever it is called.
					bool coloured = s.MeanSaturation > SaturationThreshold;
					bool indicator = coloured
						&& (s.MeanValue > ValueThreshold || NameSuggestsIndicator(s.Name));

					s.Paintable = s.Opaque > 0 && !indicator;
					results.Add(s);
				}
			}
			finally
			{
				for (int i = 0; i < temporaries.Count; i++)
				{
					if (temporaries[i] != null)
					{
						UnityEngine.Object.Destroy(temporaries[i]);
					}
				}
			}

			return results;
		}

		/// <summary>
		/// Pixels for an atlas, straight off the texture when it is CPU-readable and through a
		/// RenderTexture blit when it is not.
		///
		/// BOTH PATHS EXIST BECAUSE THE ANSWER WAS NOT KNOWN WHEN THIS WAS WRITTEN. A Unity
		/// texture imported without Read/Write Enabled throws on <c>GetPixels</c>, and whether
		/// Klei ships kanim atlases readable is not something to settle by reasoning about it.
		/// The blit path costs a full-texture copy and works regardless, so the classifier is
		/// correct either way and the measurement says which path a given install takes.
		/// </summary>
		private static Color32[] GetPixels(
			Texture2D tex,
			Dictionary<int, Color32[]> pixelCache,
			Dictionary<int, Vector2Int> sizeCache,
			List<Texture2D> temporaries,
			out string error)
		{
			error = null;
			int id = tex.GetInstanceID();
			if (pixelCache.TryGetValue(id, out Color32[] cached))
			{
				return cached;
			}

			Color32[] pixels = null;
			try
			{
				pixels = tex.GetPixels32();
			}
			catch (Exception)
			{
				pixels = null;
			}

			if (pixels == null)
			{
				try
				{
					RenderTexture rt = RenderTexture.GetTemporary(
						tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
					Graphics.Blit(tex, rt);
					RenderTexture previous = RenderTexture.active;
					RenderTexture.active = rt;

					Texture2D readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
					readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
					readable.Apply();
					temporaries.Add(readable);

					RenderTexture.active = previous;
					RenderTexture.ReleaseTemporary(rt);

					pixels = readable.GetPixels32();
				}
				catch (Exception e)
				{
					error = "could not read atlas " + tex.name + ": " + e.Message;
					return null;
				}
			}

			pixelCache[id] = pixels;
			sizeCache[id] = new Vector2Int(tex.width, tex.height);
			return pixels;
		}

		/// <summary>
		/// Mean colour and saturation of the opaque pixels inside one symbol's UV rect.
		/// </summary>
		private static void Accumulate(
			Color32[] pixels, Vector2Int size, Vector2 uvMin, Vector2 uvMax, ref SymbolStats s)
		{
			int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(uvMin.x, uvMax.x) * size.x), 0, size.x - 1);
			int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(uvMin.x, uvMax.x) * size.x), 0, size.x);
			int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(uvMin.y, uvMax.y) * size.y), 0, size.y - 1);
			int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(uvMin.y, uvMax.y) * size.y), 0, size.y);

			int w = Mathf.Max(1, x1 - x0);
			int h = Mathf.Max(1, y1 - y0);

			// Stride the region so a large symbol costs the same as a small one.
			int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)(w * h) / MaxSamplesPerSymbol)));

			double sumR = 0.0, sumG = 0.0, sumB = 0.0;
			int sampled = 0, opaque = 0;

			for (int y = y0; y < y1; y += step)
			{
				int row = y * size.x;
				for (int x = x0; x < x1; x += step)
				{
					Color32 p = pixels[row + x];
					sampled++;
					if (p.a < OpaqueAlpha * 255f)
					{
						continue;
					}
					opaque++;
					sumR += p.r;
					sumG += p.g;
					sumB += p.b;
				}
			}

			s.Sampled = sampled;
			s.Opaque = opaque;
			if (opaque == 0)
			{
				return;
			}

			float r = (float)(sumR / opaque) / 255f;
			float g = (float)(sumG / opaque) / 255f;
			float b = (float)(sumB / opaque) / 255f;
			s.MeanR = r;
			s.MeanG = g;
			s.MeanB = b;

			float max = Mathf.Max(r, Mathf.Max(g, b));
			float min = Mathf.Min(r, Mathf.Min(g, b));
			s.MeanValue = max;
			s.MeanSaturation = max <= 0f ? 0f : (max - min) / max;
		}
	}
}
