using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace OniFramework
{
	/// <summary>
	/// The colour picker for <see cref="PaintableBuilding"/>: a grid of the sixteen palette
	/// swatches in the building's details panel, one click to any colour.
	///
	/// WHY NOT THE USER-MENU CYCLE BUTTON THAT CAME FIRST. A cycle button is the right size of UI
	/// for a mechanic on one demo building. Once every building in the game is paintable it is
	/// the wrong one: reaching a chosen colour costs up to fifteen clicks, each of which rebuilds
	/// the user menu, and the only feedback is the button's own label. Sixteen swatches is one
	/// click and shows the whole palette at once. The cycle button survives as a fallback --
	/// <see cref="PaintableBuilding.UseUserMenuButton"/> is only cleared once this screen has
	/// actually registered -- so a future ONI build that moves the private list this screen
	/// inserts itself into degrades the feature to clumsy rather than to missing.
	///
	/// WHY THE UI IS BUILT IN CODE rather than cloned from a vanilla side screen. Nothing in ONI
	/// is shaped like a swatch grid, so a clone would mean finding a screen with roughly the
	/// right widgets and then deleting and re-parenting most of them -- more code than building
	/// four primitives, and code that breaks whenever Klei edits the screen it copied. What is
	/// built here is a <c>GridLayoutGroup</c> and thirty-two <c>Image</c>s, all Unity types, none
	/// of them Klei's. The one Klei type used is <c>ToolTip</c>, for the colour names, and it is
	/// guarded because it reads <c>PluginAssets.Instance</c>.
	/// </summary>
	public class BuildingPaintSideScreen : SideScreenContent
	{
		/// <summary>Edge length of one swatch, in reference pixels.</summary>
		private const float SwatchSize = 32f;

		/// <summary>Gap between swatches, and the width of the selection border.</summary>
		private const float SwatchGap = 4f;

		private const float SelectionBorder = 3f;

		private const int Columns = 8;

		private static readonly Color SelectedBorder = new Color32(255, 255, 255, 255);

		private static readonly Color UnselectedBorder = new Color32(0, 0, 0, 90);

		private PaintableBuilding target;

		private readonly List<Image> borders = new List<Image>();

		public override bool IsValidForTarget(GameObject target)
		{
			return target != null && target.GetComponent<PaintableBuilding>() != null;
		}

		public override string GetTitle()
		{
			return "Paint";
		}

		public override void SetTarget(GameObject newTarget)
		{
			target = newTarget != null ? newTarget.GetComponent<PaintableBuilding>() : null;
			RefreshSelection();
		}

		public override void ClearTarget()
		{
			target = null;
		}

		/// <summary>
		/// Show which swatch the building is currently wearing. Driven off the building's own
		/// value on every retarget rather than cached, so the screen stays right when the colour
		/// changes from somewhere else -- the fallback cycle button, or a future paint tool.
		/// </summary>
		private void RefreshSelection()
		{
			int current = target != null ? target.PaintIndex : -1;
			for (int i = 0; i < borders.Count && i < BuildingPaint.Count; i++)
			{
				if (borders[i] != null)
				{
					borders[i].color = BuildingPaint.Palette[i].Index == current
						? SelectedBorder
						: UnselectedBorder;
				}
			}
		}

		private void OnSwatchClicked(int paletteSlot)
		{
			if (target == null)
			{
				return;
			}
			target.PaintIndex = paletteSlot;
			RefreshSelection();
		}

		/// <summary>
		/// Build the swatch grid onto this screen's own GameObject. Called once, on the prefab,
		/// before it is ever instantiated -- <c>Instantiate</c> then copies the finished
		/// hierarchy, so the per-instance cost of showing this screen is a clone and nothing
		/// else.
		///
		/// THIS BUILDS THE WIDGETS AND WIRES NOTHING. Components and their serialized fields are
		/// copied by <c>Instantiate</c>; a <c>Button.onClick</c> listener added in code is NOT --
		/// <c>AddListener</c> registers a non-persistent listener, which lives only in the
		/// runtime object it was added to and is not part of what gets cloned. A grid built and
		/// wired here would arrive at the player as sixteen swatches that do nothing at all. So
		/// the wiring is <see cref="BindUi"/>, which runs on each clone instead, and the same
		/// applies to the tooltips: <c>ToolTip</c>'s strings are set through a method call, not a
		/// serialized field, so they are set per instance too.
		/// </summary>
		private void BuildUi()
		{
			RectTransform root = gameObject.GetComponent<RectTransform>();
			if (root == null)
			{
				root = gameObject.AddComponent<RectTransform>();
			}

			GridLayoutGroup grid = gameObject.AddComponent<GridLayoutGroup>();
			grid.cellSize = new Vector2(SwatchSize, SwatchSize);
			grid.spacing = new Vector2(SwatchGap, SwatchGap);
			grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
			grid.constraintCount = Columns;
			grid.childAlignment = TextAnchor.UpperCenter;
			grid.padding = new RectOffset(8, 8, 8, 8);

			ContentSizeFitter fitter = gameObject.AddComponent<ContentSizeFitter>();
			fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			for (int i = 0; i < BuildingPaint.Count; i++)
			{
				BuildingPaint.Swatch swatch = BuildingPaint.Palette[i];

				GameObject cell = new GameObject("Swatch_" + swatch.Name, typeof(RectTransform));
				cell.transform.SetParent(gameObject.transform, worldPositionStays: false);

				// The outer image is the selection border; the inner one, inset on all four
				// sides, is the colour. Two flat images rather than an outline effect so the
				// selected state is legible against both a light and a dark swatch.
				Image border = cell.AddComponent<Image>();
				border.color = UnselectedBorder;

				GameObject fill = new GameObject("Fill", typeof(RectTransform));
				fill.transform.SetParent(cell.transform, worldPositionStays: false);
				RectTransform fillRect = fill.GetComponent<RectTransform>();
				fillRect.anchorMin = Vector2.zero;
				fillRect.anchorMax = Vector2.one;
				fillRect.offsetMin = new Vector2(SelectionBorder, SelectionBorder);
				fillRect.offsetMax = new Vector2(-SelectionBorder, -SelectionBorder);
				Image fillImage = fill.AddComponent<Image>();
				fillImage.color = swatch.Colour;
				fillImage.raycastTarget = false;

				Button button = cell.AddComponent<Button>();
				button.targetGraphic = border;

				cell.AddComponent<ToolTip>();
			}
		}

		/// <summary>
		/// Wire this instance's swatches up: cache their border images, name their tooltips and
		/// give each button its click handler. Runs per clone -- see <see cref="BuildUi"/> for
		/// why none of this can be done on the prefab.
		///
		/// The swatches are matched to palette entries BY SIBLING ORDER, which is the order
		/// <see cref="BuildUi"/> created them in and the order the grid lays them out in, so the
		/// two cannot disagree. Matching by name would look sturdier and would quietly break the
		/// day two palette entries were given the same name.
		/// </summary>
		private void BindUi()
		{
			borders.Clear();

			int count = Mathf.Min(transform.childCount, BuildingPaint.Count);
			for (int i = 0; i < count; i++)
			{
				Transform cell = transform.GetChild(i);
				BuildingPaint.Swatch swatch = BuildingPaint.Palette[i];

				borders.Add(cell.GetComponent<Image>());

				Button button = cell.GetComponent<Button>();
				if (button != null)
				{
					int slot = swatch.Index;
					button.onClick.RemoveAllListeners();
					button.onClick.AddListener(delegate { OnSwatchClicked(slot); });
				}

				try
				{
					ToolTip tip = cell.GetComponent<ToolTip>();
					if (tip != null)
					{
						tip.SetSimpleTooltip(swatch.Name);
					}
				}
				catch (Exception)
				{
					// PluginAssets is not up yet, or ToolTip changed shape. A swatch without a
					// name is still a usable swatch, so this is not worth failing the screen for.
				}
			}

			RefreshSelection();
		}

		protected override void OnPrefabInit()
		{
			base.OnPrefabInit();
			BindUi();
		}

		// -------------------------------------------------------------------------------------
		// REGISTRATION
		// -------------------------------------------------------------------------------------

		private static bool installed;

		private static BuildingPaintSideScreen prefab;

		/// <summary>
		/// Put this screen into <c>DetailsScreen</c>'s side-screen list. Idempotent.
		///
		/// <c>DetailsScreen.sideScreens</c> is a private serialized field with no accessor, so
		/// reflection is the only way in -- there is no registration API for side screens in ONI
		/// and every mod that adds one does this. The failure is contained: if the field cannot
		/// be found or is not the expected type, the screen is not registered,
		/// <see cref="PaintableBuilding.UseUserMenuButton"/> is left true, and painting still
		/// works from the user menu.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			installed = true;

			harmony.Patch(
				AccessTools.Method(typeof(DetailsScreen), "OnPrefabInit"),
				postfix: new HarmonyMethod(typeof(BuildingPaintSideScreen), nameof(OnPrefabInitPostfix)));
		}

		private static void OnPrefabInitPostfix(DetailsScreen __instance)
		{
			try
			{
				if (prefab == null)
				{
					GameObject go = new GameObject("BuildingPaintSideScreen", typeof(RectTransform));

					// Inactive before the component is added so the KScreen never runs on the
					// prefab itself; DetailsScreen calls Show() on the clone.
					go.SetActive(false);
					UnityEngine.Object.DontDestroyOnLoad(go);

					prefab = go.AddComponent<BuildingPaintSideScreen>();
					prefab.ContentContainer = go;
					prefab.BuildUi();
				}

				List<DetailsScreen.SideScreenRef> screens =
					AccessTools.Field(typeof(DetailsScreen), "sideScreens")
						?.GetValue(__instance) as List<DetailsScreen.SideScreenRef>;
				if (screens == null)
				{
					FrameworkLog.Warn("BuildingPaintSideScreen: DetailsScreen.sideScreens is not a "
						+ "List<SideScreenRef>; keeping the user-menu paint button instead.");
					return;
				}

				for (int i = 0; i < screens.Count; i++)
				{
					if (screens[i] != null && screens[i].screenPrefab is BuildingPaintSideScreen)
					{
						return;
					}
				}

				screens.Add(new DetailsScreen.SideScreenRef
				{
					name = "BuildingPaintSideScreen",
					screenPrefab = prefab,
					offset = Vector2.zero,
					tab = DetailsScreen.SidescreenTabTypes.Config
				});

				// Only now, with a picker the player can actually reach, does the cycle button
				// stop being the way to paint.
				PaintableBuilding.UseUserMenuButton = false;
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("BuildingPaintSideScreen could not register: " + e.Message
					+ " -- keeping the user-menu paint button instead.");
			}
		}
	}
}
