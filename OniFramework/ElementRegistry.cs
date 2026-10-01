using System.Collections.Generic;
using ElementData;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// ADDING A NEW ELEMENT TO OXYGEN NOT INCLUDED, from managed code, without touching the game's
	/// own files.
	///
	/// WHY THIS IS A FRAMEWORK CAPABILITY. Vanilla loads its element table from
	/// <c>Application.streamingAssetsPath + "/elements/"</c> and nowhere else
	/// (<c>ElementLoader.CollectElementsFromYAML</c>), so the only ways to
	/// add an element are to write into the player's game install or to intercept that collection.
	/// Writing into the install is not something a mod should do; intercepting it is a single
	/// postfix, but every mod that wants a new element would otherwise write its own copy of that
	/// postfix, and two mods each appending to the same list through two separate patches is
	/// exactly how element tables end up order-dependent. One registry, one patch, many callers.
	///
	/// WHAT THE GAME ALREADY DOES FOR US, checked rather than assumed:
	///   - `ElementLoader.Load` hashes `elementId` with `Hash.SDBMLower` and casts the result to
	///     `SimHashes`, so an element does NOT need an entry in the `SimHashes` enum. The enum is
	///     a convenience over a hash, not the source of truth.
	///   - `ManifestSubstanceForElement` CREATES a `Substance` when the substance table has none,
	///     and generates a UI colour from the element's index. So a gas or liquid needs no art to
	///     exist and be simulated. A SOLID sets `renderedByWorld` and will want real material and
	///     texture work before it looks like anything; that is a separate job from this one.
	///   - The native sim receives the whole element table as one message (`elements.Load` in the
	///     custom SimDLL) rather than a fixed list, and indexes elements as `uint16_t`. There are
	///     65535 slots and vanilla uses a few hundred, so the native side is not a constraint.
	///
	/// TIMING. `Register` must be called before `ElementLoader.Load` runs, which in practice means
	/// from a mod's `OnLoad`/`UserMod2.OnLoad` or a static initialiser reached from it. Registering
	/// later cannot work -- the table is already built, the substances already manifested, and the
	/// sim already told -- so a late call is refused loudly rather than silently doing nothing.
	///
	/// STRINGS. `ElementLoader.Load` resolves an element's display name through
	/// `Strings.Get(localizationID)`, which throws for a key that was never added. Callers should
	/// use <see cref="AddStrings"/> (or add the keys themselves) before registering.
	/// </summary>
	public static class ElementRegistry
	{
		private static readonly List<ElementEntry> Pending = new List<ElementEntry>();
		private static readonly HashSet<string> RegisteredIds = new HashSet<string>();

		/// <summary>
		/// Set once the game has collected its element table. Registrations after this point
		/// cannot take effect, so they are refused with a warning that names the element.
		/// </summary>
		public static bool ElementTableBuilt { get; private set; }

		/// <summary>
		/// When true, registered elements are NOT contributed to the game's table.
		///
		/// Set by <see cref="SimCorpus.Install"/> and by nothing else. It exists because a
		/// message corpus is supposed to be a recording of the SHIPPED game, and the element
		/// table it records is built managed-side from whatever <c>ElementLoader</c> ended up
		/// holding -- so a single element added by any loaded mod makes the artefact this
		/// project's rather than Klei's. Suppressing at the point of contribution is
		/// order-independent, needs no change to mods.json, and leaves nothing behind for the
		/// next launch.
		/// </summary>
		public static bool Suppressed { get; set; }

		/// <summary>
		/// Every element id registered through this API so far, in registration order. Exposed so a
		/// mod can tell whether the element it depends on was actually contributed by someone,
		/// rather than probing <c>ElementLoader</c> and getting a null it cannot explain.
		/// </summary>
		public static IEnumerable<string> Registered
		{
			get { return RegisteredIds; }
		}

		/// <summary>
		/// Registers one fully-specified element entry. Prefer <see cref="RegisterGas"/> or
		/// <see cref="RegisterLiquid"/>, which fill in the fields that are boilerplate for a phase
		/// and leave only the ones that actually describe the material.
		///
		/// Returns false, with a warning, for a duplicate id or a call made too late. It does not
		/// throw: a mod that fails to add an element should degrade to not having it, the same
		/// contract every other facade in this framework uses.
		/// </summary>
		public static bool Register(ElementEntry entry)
		{
			if (entry == null || string.IsNullOrEmpty(entry.elementId))
			{
				Debug.LogWarning("[OniFramework] ElementRegistry: refusing an entry with no elementId.");
				return false;
			}

			if (ElementTableBuilt)
			{
				Debug.LogWarning("[OniFramework] ElementRegistry: '" + entry.elementId
					+ "' was registered after the element table was already built, so it cannot "
					+ "take effect. Register from a mod's OnLoad, before ElementLoader.Load runs.");
				return false;
			}

			if (!RegisteredIds.Add(entry.elementId))
			{
				Debug.LogWarning("[OniFramework] ElementRegistry: '" + entry.elementId
					+ "' is already registered; ignoring the second registration.");
				return false;
			}

			Pending.Add(entry);
			return true;
		}

		/// <summary>
		/// A new GAS. The parameters are the ones that describe the substance; everything else is
		/// given the value vanilla's own gases use, read from the shipped
		/// <c>StreamingAssets/elements/gas.yaml</c> rather than invented:
		/// <c>flow: 0.1</c>, the 25/1/1 surface-area multipliers, 101.3 kPa default pressure and
		/// <c>state: Gas</c>.
		/// </summary>
		/// <param name="elementId">Internal id, hashed to the element's SimHashes value.</param>
		/// <param name="localizationID">A Strings key -- see <see cref="AddStrings"/>.</param>
		/// <param name="condensesTo">
		/// The liquid this becomes below <paramref name="condensationPointK"/>. Pass the element's
		/// own id to give it no condensed phase at all: vanilla reads a transition to self as "does
		/// not transition", which is how several of its own gases are written.
		/// </param>
		public static bool RegisterGas(string elementId, string localizationID,
			float specificHeatCapacity, float thermalConductivity, float molarMass,
			string condensesTo, float condensationPointK, float toxicity = 0f,
			float defaultTemperatureK = 300f, string materialCategory = "Unbreathable",
			float lightAbsorptionFactor = 0.1f, float radiationAbsorptionFactor = 0.08f)
		{
			return Register(new ElementEntry
			{
				elementId = elementId,
				localizationID = localizationID,
				dlcId = "",
				state = Element.State.Gas,
				specificHeatCapacity = specificHeatCapacity,
				thermalConductivity = thermalConductivity,
				molarMass = molarMass,
				solidSurfaceAreaMultiplier = 25f,
				liquidSurfaceAreaMultiplier = 1f,
				gasSurfaceAreaMultiplier = 1f,
				flow = 0.1f,
				lowTemp = condensationPointK,
				lowTempTransitionTarget = condensesTo,
				defaultTemperature = defaultTemperatureK,
				defaultPressure = 101.3f,
				toxicity = toxicity,
				lightAbsorptionFactor = lightAbsorptionFactor,
				radiationAbsorptionFactor = radiationAbsorptionFactor,
				radiationPer1000Mass = 0f,
				materialCategory = materialCategory,
				isDisabled = false
			});
		}

		/// <summary>
		/// A new LIQUID. As <see cref="RegisterGas"/>, the unlisted fields take the values vanilla's
		/// own liquids use (<c>StreamingAssets/elements/liquid.yaml</c>): <c>maxMass: 1000</c>,
		/// <c>liquidCompression: 1.01</c>, <c>speed: 125</c>, the 0.01 minimum flows and the 1/25/1
		/// surface-area multipliers.
		/// </summary>
		/// <param name="freezesTo">
		/// The solid below <paramref name="freezingPointK"/>; pass the element's own id for a
		/// liquid with no modelled solid phase.
		/// </param>
		/// <param name="boilsTo">The gas above <paramref name="boilingPointK"/>.</param>
		public static bool RegisterLiquid(string elementId, string localizationID,
			float specificHeatCapacity, float thermalConductivity, float molarMass,
			string freezesTo, float freezingPointK, string boilsTo, float boilingPointK,
			float toxicity = 0f, float defaultTemperatureK = 300f,
			string materialCategory = "Unbreathable", float lightAbsorptionFactor = 0.7f,
			float radiationAbsorptionFactor = 0.8f)
		{
			return Register(new ElementEntry
			{
				elementId = elementId,
				localizationID = localizationID,
				dlcId = "",
				state = Element.State.Liquid,
				specificHeatCapacity = specificHeatCapacity,
				thermalConductivity = thermalConductivity,
				molarMass = molarMass,
				maxMass = 1000f,
				liquidCompression = 1.01f,
				speed = 125f,
				minHorizontalFlow = 0.01f,
				minVerticalFlow = 0.01f,
				solidSurfaceAreaMultiplier = 1f,
				liquidSurfaceAreaMultiplier = 25f,
				gasSurfaceAreaMultiplier = 1f,
				lowTemp = freezingPointK,
				lowTempTransitionTarget = freezesTo,
				highTemp = boilingPointK,
				highTempTransitionTarget = boilsTo,
				defaultTemperature = defaultTemperatureK,
				defaultMass = 1000f,
				toxicity = toxicity,
				lightAbsorptionFactor = lightAbsorptionFactor,
				radiationAbsorptionFactor = radiationAbsorptionFactor,
				radiationPer1000Mass = 0f,
				materialCategory = materialCategory,
				isDisabled = false
			});
		}

		/// <summary>
		/// Adds the name and description strings an element's <c>localizationID</c> points at.
		///
		/// `ElementLoader.Load` calls `Strings.Get` on that key while building the table, and
		/// `Strings.Get` throws on a missing key rather than returning a placeholder -- so an
		/// element registered without its strings takes the whole element table down with it, at
		/// startup, with a stack trace that names Strings rather than the element. Call this first.
		///
		/// The key convention matches vanilla's: <c>STRINGS.ELEMENTS.&lt;ID&gt;.NAME</c> and
		/// <c>.DESC</c>, upper-cased.
		/// </summary>
		public static void AddStrings(string elementId, string name, string description)
		{
			string key = "STRINGS.ELEMENTS." + elementId.ToUpperInvariant();
			Strings.Add(key + ".NAME", name);
			Strings.Add(key + ".DESC", description);
		}

		/// <summary>
		/// Appends every registered element to the list vanilla just built from its own YAML.
		///
		/// A postfix rather than a transpiler or a replacement: vanilla's own collection, error
		/// handling and mod-manager error reporting all run untouched, and this adds to the result.
		/// If no mod registered anything this is a no-op that allocates nothing.
		/// </summary>
		[HarmonyPatch(typeof(ElementLoader), nameof(ElementLoader.CollectElementsFromYAML))]
		internal static class ElementLoader_CollectElementsFromYAML_AddRegistered
		{
			private static void Postfix(List<ElementEntry> __result)
			{
				ElementTableBuilt = true;

				if (__result == null || Pending.Count == 0)
				{
					return;
				}

				// A CORPUS RECORDING GETS KLEI'S TABLE AND NOTHING ELSE.
				//
				// This is the single seam every element added by this project passes through, and
				// it fires long after every mod's OnLoad -- which is what makes refusing here
				// order-independent, unlike anything that tries to stop a mod from loading. See
				// SimCorpus for what goes wrong without it: `Elements_CreateTable` is one of the
				// corpus's 111 records, ElementLoader SORTS, so an added element lands mid-table
				// and shifts every index after it, and vftest's pinned v16 fixture then resolves
				// its stored indices to the wrong elements. Measured: 216 rows instead
				// of 212, the four extras in at index 133, one suite failure, and nothing about
				// the corpus that LOOKED wrong.
				//
				// The suppression is silent to the game and loud in the log: the elements are
				// still registered, still described, and still listed by Registered -- they
				// simply do not reach the table on this one kind of run.
				if (Suppressed)
				{
					Debug.Log("[OniFramework] ElementRegistry: SUPPRESSED -- withheld "
						+ Pending.Count + " element(s) from the game's table because this is a "
						+ "corpus recording, which must see Klei's table alone: " + string.Join(
							", ", Pending.ConvertAll(e => e.elementId).ToArray()));
					return;
				}

				__result.AddRange(Pending);
				Debug.Log("[OniFramework] ElementRegistry: contributed " + Pending.Count
					+ " element(s) to the game's table: " + string.Join(", ",
						Pending.ConvertAll(e => e.elementId).ToArray()));
			}
		}

		/// <summary>
		/// Gives every registered element the substance ART it needs to have a dropped-chunk
		/// prefab, by borrowing a vanilla element of the same phase.
		///
		/// NOT COSMETIC. `GeneratedOre.LoadGeneratedOre` builds the
		/// gas/liquid/solid "ore entity" prefab for an element only when
		/// `element.substance.anim != null`, and
		/// `ElementLoader.ManifestSubstanceForElement` manufactures a Substance with a generated
		/// colour but no anim. A registered element therefore has no chunk prefab -- and the
		/// moment ANY vanilla code path tries to make a chunk of it, `GeneratedOre.CreateChunk`
		/// logs "Could not find prefab for element" and then throws a NullReferenceException out
		/// of `Util.KInstantiate`.
		///
		/// That path is not exotic. `ElementConsumer.AddMassInternal` calls
		/// `Storage.AddGasChunk` for anything it consumes that will not fit, which means an
		/// ordinary vanilla Gas Pump standing in a room full of a registered gas throws on every
		/// tick: the `--mod1-airloop` run logged the pair 1004 times in sixty seconds. Nitrogen
		/// found it; Pollutant had the same hole and nothing had happened to hit it yet.
		///
		/// Borrowed rather than authored: this framework cannot make kanims, and a chunk of
		/// nitrogen that looks like a chunk of oxygen is enormously better than one that crashes
		/// the sim tick. The element keeps its OWN colour -- only `anim` and `material` come from
		/// the donor -- so the two are still told apart everywhere colour is what the player
		/// reads. Real art is a kanim-port-list job.
		/// </summary>
		internal static void EnsureSubstanceArt()
		{
			if (RegisteredIds.Count == 0 || ElementLoader.elements == null)
			{
				return;
			}

			int repaired = 0;
			foreach (string id in RegisteredIds)
			{
				Element element = ElementLoader.FindElementByHash((SimHashes)Hash.SDBMLower(id));
				if (element == null || element.substance == null
					|| element.substance.anim != null)
				{
					continue;
				}

				Element donor = FindArtDonor(element);
				if (donor == null || donor.substance == null || donor.substance.anim == null)
				{
					continue;
				}

				element.substance.anim = donor.substance.anim;
				if (element.substance.material == null)
				{
					element.substance.material = donor.substance.material;
				}
				repaired++;
			}

			if (repaired > 0)
			{
				Debug.Log("[OniFramework] ElementRegistry: borrowed substance art for " + repaired
					+ " registered element(s) so their dropped chunks have a prefab");
			}
		}

		/// <summary>
		/// A vanilla element of the same phase to borrow chunk art from. Oxygen, Water and Sand
		/// are chosen because all three are present in every build and every DLC combination --
		/// an art donor that might not exist would reintroduce exactly the null this fixes.
		/// </summary>
		private static Element FindArtDonor(Element element)
		{
			if (element.IsGas)
			{
				return ElementLoader.FindElementByHash(SimHashes.Oxygen);
			}
			if (element.IsLiquid)
			{
				return ElementLoader.FindElementByHash(SimHashes.Water);
			}
			return ElementLoader.FindElementByHash(SimHashes.Sand);
		}

		/// <summary>
		/// Fills in the borrowed art immediately before the game generates its chunk prefabs,
		/// which is the last moment it can still matter and the first moment every element
		/// actually exists.
		/// </summary>
		[HarmonyPatch(typeof(GeneratedOre), nameof(GeneratedOre.LoadGeneratedOre))]
		internal static class GeneratedOre_LoadGeneratedOre_BorrowSubstanceArt
		{
			private static void Prefix()
			{
				try
				{
					EnsureSubstanceArt();
				}
				catch (System.Exception e)
				{
					Debug.LogWarning("[OniFramework] ElementRegistry: could not borrow substance "
						+ "art: " + e);
				}
			}
		}
	}
}
