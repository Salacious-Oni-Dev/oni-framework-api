using System.Collections.Generic;
using KSerialization;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Lets the player recolour a building, and remembers the choice across a save.
	///
	/// THE DEMONSTRATION THIS IS. The colours are Stationeers' own sixteen structure paints, and
	/// the mechanism is not: Stationeers repaints by swapping the renderer's material for a shared
	/// <c>Texture2DArray</c> material and setting a per-instance slice index, while ONI already
	/// ships per-instance colour on every kanim controller. So the port kept the palette and threw
	/// the machinery away. <see cref="BuildingPaint"/> holds both halves of that decision.
	///
	/// HOW MUCH OF THE BUILDING CHANGES COLOUR IS DECIDED BY THE ART, NOT BY THIS FILE. When the
	/// kanim was packed with a paint symbol, only the spraypaintable geometry is tinted, exactly
	/// as Stationeers repaints only the slots matching the prefab's <c>PaintableMaterial</c> --
	/// so displays, warning decals and glass keep their own colours. When it was not -- which is
	/// every vanilla building, because Klei's kanims carry no such split and the
	/// <c>colourChannel</c> field their format does carry is parsed by <c>KGlobalAnimParser</c>
	/// and then read by nothing -- every symbol of the build is tinted instead.
	/// <c>BuildingPaint.TryApplyBest</c> chooses, and a building gains the precise behaviour by
	/// having its art repacked, with no change here.
	///
	/// WHY THIS LIVES IN THE FRAMEWORK RATHER THAN IN A MOD. It is attached to every eligible
	/// building by <see cref="BuildingPaintAll"/>, including buildings added by mods that have
	/// never heard of us, so the component has to be reachable from the layer those mods already
	/// depend on. It was a flagship type first, when it painted exactly one building.
	///
	/// A TINT IS A MULTIPLY, so White is the unpainted identity and every other swatch darkens as
	/// well as colours.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	public class PaintableBuilding : KMonoBehaviour
	{
		/// <summary>
		/// The chosen swatch, as a <c>ColorPaletteArray</c> slice index rather than an RGB value.
		/// Serialized because a repainted building that forgets its colour on reload is a
		/// save/load defect, and stored as an index so a later palette correction repaints
		/// existing buildings instead of stranding them on a hardcoded colour.
		///
		/// Starts at <c>NeverPainted</c> rather than at a colour, so a building the player has not
		/// touched can adopt <see cref="defaultPaintIndex"/> -- including a later change to that
		/// default -- while one that HAS been painted keeps its choice even if the player happened
		/// to choose the same colour the default would have given.
		/// </summary>
		[Serialize] private int paintIndex = NeverPainted;

		/// <summary>
		/// The building's factory colour, set by its config before the prefab is instantiated.
		/// Left as White (a no-op tint) unless a config says otherwise, so adding
		/// <see cref="PaintableBuilding"/> to a building never changes how it looks by itself --
		/// which is the property that lets <see cref="BuildingPaintAll"/> add it to everything.
		/// </summary>
		public int defaultPaintIndex = BuildingPaint.White;

		/// <summary>
		/// Sentinel for "the player has never chosen a colour for this one". Negative because
		/// every real slice index is non-negative, so it cannot collide with a palette entry.
		/// </summary>
		private const int NeverPainted = -1;

		private KAnimControllerBase controller;

		/// <summary>
		/// The build whose paint symbol should be tinted, or null to tint the whole build.
		/// Resolved in <see cref="OnSpawn"/> from the controller's own anim file rather than
		/// configured per building, so a building's art and its paint behaviour cannot drift
		/// apart -- repacking the kanim with a paint symbol is enough to switch it over.
		/// </summary>
		private string animName;

		/// <summary>
		/// The swatch this building is painted, as a palette slice index. Setting it repaints
		/// immediately and is what the side screen writes; a value the palette does not know is
		/// coerced to <see cref="BuildingPaint.Unpainted"/> rather than rejected, so a caller
		/// cannot leave a building holding an index nothing can name.
		/// </summary>
		public int PaintIndex
		{
			get { return paintIndex; }
			set
			{
				int wanted = BuildingPaint.IsValid(value) ? value : BuildingPaint.Unpainted;
				if (wanted == paintIndex)
				{
					return;
				}
				paintIndex = wanted;
				Refresh();
			}
		}

		// ---------------------------------------------------------------------------------
		// OVERLAY SUPPRESSION
		//
		// While an overlay is open, paint is lifted off every painted building and put back when
		// the overlay closes, so an overlay reads exactly as it does in vanilla. It is not a
		// correctness fix -- symbol paint and the overlay's own TintColour are separate channels
		// and neither clobbers the other -- it is legibility: the two multiply, so a painted
		// building would read darker than its unpainted neighbour in a view whose whole job is
		// to make colour mean one thing.
		//
		// ONE SUBSCRIPTION, NOT ONE PER BUILDING. OverlayScreen.OnOverlayChanged is a plain
		// Action<HashedString>, so several hundred buildings each adding a handler would be
		// several hundred delegate invocations per overlay toggle and several hundred chances to
		// leak one on destruction. A live list plus a single handler costs one list slot per
		// building and iterates only on a toggle.
		// ---------------------------------------------------------------------------------

		private static readonly List<PaintableBuilding> live = new List<PaintableBuilding>();

		/// <summary>The screen the handler is attached to. A screen, not a bool: every save load
		/// builds a new OverlayScreen, and a flag that stayed set would leave the second save of a
		/// session with no handler at all.</summary>
		private static OverlayScreen hookedScreen;

		private static bool overlayActive;

		/// <summary>
		/// Attach the single overlay handler, once, the first time a painted building spawns.
		///
		/// Done lazily instead of from a patch because <c>OverlayScreen.Instance</c> is a
		/// gameplay-scene object and there is no useful moment to hook it that is guaranteed to
		/// come before the first building spawns and after the screen exists. A world with no
		/// overlay screen at all -- a blueprint canvas rig -- simply never hooks, and paint stays
		/// applied, which is the right answer there.
		///
		/// The mode is read through <c>GetMode()</c>, not the <c>mode</c> property. Klei sets
		/// <c>Instance</c> in <c>OnPrefabInit</c> but fills the current mode only in
		/// <c>OnSpawn</c>, and a save's buildings spawn in between: the Printing Pod does on every
		/// load. <c>mode</c> throws there; <c>GetMode()</c> is Klei's own guard and answers None,
		/// which is what the screen will open on.
		/// </summary>
		private static void HookOverlay()
		{
			OverlayScreen screen = OverlayScreen.Instance;
			if (screen == null || screen == hookedScreen)
			{
				return;
			}
			hookedScreen = screen;
			overlayActive = screen.GetMode() != OverlayModes.None.ID;
			screen.OnOverlayChanged += OnOverlayChanged;
		}

		private static void OnOverlayChanged(HashedString mode)
		{
			bool nowActive = mode != OverlayModes.None.ID;
			if (nowActive == overlayActive)
			{
				return;
			}
			overlayActive = nowActive;
			for (int i = 0; i < live.Count; i++)
			{
				if (live[i] != null)
				{
					live[i].Refresh();
				}
			}
		}

		protected override void OnSpawn()
		{
			base.OnSpawn();

			controller = GetComponent<KAnimControllerBase>();

			// AnimFiles[0] is the build this controller draws from, and its name is the build
			// name the pipeline derived the paint symbol from. Guarded rather than indexed
			// because a controller with no anim files is legal (it just draws nothing).
			KAnimFile[] files = controller != null ? controller.AnimFiles : null;
			if (files != null && files.Length > 0 && files[0] != null)
			{
				animName = files[0].name;
			}

			// Never painted: adopt the config's factory colour. Done here rather than as the
			// field's initialiser because defaultPaintIndex is only set once the config has run.
			if (paintIndex == NeverPainted)
			{
				paintIndex = defaultPaintIndex;
			}

			// An index from a newer palette resolves to Unpainted rather than throwing, but it
			// should not silently persist as a value nothing can name -- rewrite it so the next
			// save is self-consistent.
			if (!BuildingPaint.IsValid(paintIndex))
			{
				paintIndex = BuildingPaint.Unpainted;
			}

			HookOverlay();
			live.Add(this);

			Refresh();

			Subscribe((int)GameHashes.RefreshUserMenu, OnRefreshUserMenu);
		}

		protected override void OnCleanUp()
		{
			live.Remove(this);
			base.OnCleanUp();
		}

		/// <summary>
		/// Set when a <see cref="Refresh"/> could not resolve the build, so the next chance is
		/// taken instead of the colour being silently dropped. <c>KAnimFileData.build</c> reads
		/// through <c>KAnimBatchManager</c>, and while it has always resolved by <c>OnSpawn</c>
		/// in testing, a building that painted itself only on the frame it spawned would fail
		/// invisibly if that ever stopped being true.
		/// </summary>
		private bool pendingApply;

		/// <summary>
		/// The swatch currently written into the anim, or <see cref="NeverPainted"/> before the
		/// first successful write.
		///
		/// THIS IS A PERFORMANCE GUARD, and the numbers are why it exists. Suppressing paint for
		/// an overlay walks every painted building and, for each, every symbol of its build --
		/// on a mature colony that is order 500 buildings times order 20 symbols, ten thousand
		/// hash lookups, on both the open and the close of every overlay. But the overwhelming
		/// majority of buildings are unpainted, and unpainted and suppressed are the SAME colour:
		/// white is the identity for a multiply. So a building whose anim already holds the
		/// colour being asked for does nothing at all, and an overlay toggle costs work only for
		/// the buildings the player has actually painted.
		/// </summary>
		private int appliedIndex = NeverPainted;

		/// <summary>
		/// Push the current swatch at the anim, or lift it while an overlay is open.
		///
		/// Separated from <see cref="OnSpawn"/> because the tint has to be reasserted: on every
		/// overlay close, and whenever the player picks a colour.
		///
		/// THIS DELIBERATELY NEVER WRITES <c>TintColour</c>, and so deliberately does not call
		/// <c>BuildingPaint.TryApplyBest</c>, which does. On one hand-picked building that was
		/// harmless. Attached to every building in the game it is not: <c>Ownable</c> stores bed
		/// assignment in <c>TintColour</c>, <c>TreeFilterable</c> stores whether a storage has a
		/// filter set, <c>DesiccationMonitor</c> stores plant health, and every overlay drives it
		/// live. Painting through that field would silently overwrite four vanilla state displays
		/// and be overwritten back by them at unpredictable moments. The per-symbol channel is
		/// ours alone, so both the paint and the vanilla tints survive.
		/// </summary>
		private void Refresh()
		{
			// The symbol paths need a KBatchedAnimController specifically -- SetSymbolTint and
			// symbolInstanceGpuData live there, not on the base class. BuildingPaintAll only
			// attaches to buildings that have one.
			KBatchedAnimController batched = controller as KBatchedAnimController;
			if (batched == null)
			{
				return;
			}

			// Suppressing paint is painting it white: white is the identity for a multiply, so
			// the building renders exactly as its art was authored for the duration.
			int index = overlayActive ? BuildingPaint.Unpainted : paintIndex;

			// Already wearing this colour. See appliedIndex: this is what keeps an overlay
			// toggle from re-tinting every symbol of every unpainted building in the colony.
			if (!pendingApply && index == appliedIndex)
			{
				return;
			}

			if (BuildingPaint.TryApplyToSymbol(
					batched,
					new KAnimHashedString(BuildingPaint.PaintSymbolFor(animName)),
					index))
			{
				appliedIndex = index;
				pendingApply = false;
				return;
			}

			if (BuildingPaint.TryApplyToBuild(batched, index))
			{
				appliedIndex = index;
				pendingApply = false;
				return;
			}

			pendingApply = true;
		}

		/// <summary>
		/// True while no side screen has claimed the job of picking colours, in which case the
		/// user menu carries a cycle button instead.
		///
		/// WHY BOTH EXIST. A cycle button is fine for one demo building and poor across a base --
		/// reaching one of sixteen colours costs up to fifteen clicks -- so
		/// <see cref="BuildingPaintSideScreen"/> is the intended UI. But that side screen has to
		/// insert itself into a private list on <c>DetailsScreen</c>, and if a future ONI build
		/// moves that list the registration fails. Falling back to the button means the feature
		/// degrades to clumsy rather than to unreachable.
		/// </summary>
		public static bool UseUserMenuButton = true;

		private void OnRefreshUserMenu(object data)
		{
			// The player selecting the building is the first moment after spawn that is
			// guaranteed to exist, so it is where a deferred paint gets its retry.
			if (pendingApply)
			{
				Refresh();
			}

			if (!UseUserMenuButton)
			{
				return;
			}
			BuildingPaint.Swatch swatch = BuildingPaint.Get(paintIndex);
			Game.Instance.userMenu.AddButton(gameObject, new KIconButtonMenu.ButtonInfo(
				"action_switch_toggle",
				"Paint: " + swatch.Name,
				OnPaintClicked,
				tooltipText: "Recolour this building. Cycles through the sixteen Stationeers structure paints; White leaves the art untinted."));
		}

		private void OnPaintClicked()
		{
			PaintIndex = BuildingPaint.Next(paintIndex);

			// The button's own label carries the colour name, so the menu has to be rebuilt for
			// the click to be visible as anything other than the building changing colour.
			Game.Instance.userMenu.Refresh(gameObject);
		}
	}
}
