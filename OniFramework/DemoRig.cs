using System.Collections.Generic;
using TemplateClasses;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The two things every live test scenario needs, and that are easy to get wrong by hand.
	///
	/// They live in the framework rather than in a flagship mod because both flagship mods build
	/// scenarios, and a second copy of either is a second copy that drifts. Neither is gameplay:
	/// they are harness support for demonstrating what the platform can do.
	/// </summary>
	public static class DemoRig
	{
		/// <summary>
		/// Dismisses whatever modal dialog is up and unpauses the game.
		///
		/// CALL THIS AT EVERY CHECKPOINT, not once at the start. A modal dialog PAUSES the game,
		/// and a paused game starves the native sim of ticks, so every sample afterwards reads the
		/// same numbers -- which looks exactly like several independently broken mechanics rather
		/// than like one paused game. That misreading cost a full debugging round.
		///
		/// Two different screens do it, and they are unrelated types:
		///   - the MOD DIFFERENCES confirmation, a <c>ConfirmDialogScreen</c>;
		///   - COLONY LOST, a <c>GameOverScreen : KModalScreen</c> -- NOT a ConfirmDialogScreen, so
		///     a search for the first type never finds it. It appears on its own schedule:
		///     `GameFlowManager.CheckForGameOver` fires as soon as
		///     `Components.LiveMinionIdentities.Count == 0`, which is the normal state of this
		///     project's sandbox test-bed saves.
		///
		/// The game-over screen is closed with `Show(false)`, its own documented close path and
		/// what its Dismiss button calls, rather than `Deactivate()`.
		/// </summary>
		/// <param name="logPrefix">Prefix for the log lines, so the calling scenario is identifiable.</param>
		public static void DismissModalsAndUnpause(string logPrefix)
		{
			var modDialog = Object.FindFirstObjectByType<ConfirmDialogScreen>();
			if (modDialog != null)
			{
				Debug.Log(logPrefix + "dismissing MOD DIFFERENCES (or similar) modal dialog");
				modDialog.Deactivate();
			}

			var gameOverScreen = Object.FindFirstObjectByType<GameOverScreen>();
			if (gameOverScreen != null && gameOverScreen.isActiveAndEnabled)
			{
				Debug.Log(logPrefix + "dismissing COLONY LOST (GameOverScreen) modal dialog");
				gameOverScreen.Show(false);
			}

			if (SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused)
			{
				Debug.Log(logPrefix + "game paused, unpausing so sim ticks actually run");
				SpeedControlScreen.Instance.Unpause(playSound: false);
			}
		}

		/// <summary>
		/// Hides the vanilla HUD a scenario is not using, so the only things on screen are the
		/// rig, this framework's own panels, and the game's notifications.
		///
		/// WHAT STAYS, and why it is not simply "everything off":
		///   - NotificationScreen, because the game's own warnings about the rig -- overpressure,
		///     condensation, damage -- are part of what a scenario demonstrates.
		///   - SpeedControlScreen, because <see cref="SetFastForward"/> drives it and a scenario
		///     that hid it would be disabling its own clock. It is also the one control a viewer
		///     needs when the narration asks them to pause and read a panel.
		///
		/// Everything else here is a menu or a readout addressed to a player who is playing, not
		/// to a viewer watching a measurement: the build menu, the management and overlay menus,
		/// the tool bar, the resource list, the date/cycle readout and the top-left meters.
		///
		/// Deactivates the GameObject rather than destroying anything, so nothing about the save
		/// is changed and the HUD comes back with a reload.
		/// </summary>
		public static void HideVanillaHud()
		{
			EnsurePinnedResourcesPanelExists();

			HideScreen(Object.FindFirstObjectByType<PlanScreen>());
			HideScreen(Object.FindFirstObjectByType<ManagementMenu>());
			HideScreen(Object.FindFirstObjectByType<OverlayMenu>());
			HideScreen(Object.FindFirstObjectByType<ToolMenu>());
			HideScreen(Object.FindFirstObjectByType<ResourceCategoryScreen>());
			HideScreen(Object.FindFirstObjectByType<TopLeftControlScreen>());
			HideScreen(Object.FindFirstObjectByType<MeterScreen>());
			HideScreen(Object.FindFirstObjectByType<DateTime>());
		}

		/// <summary>
		/// Builds the game's own HUD -- overlay buttons, build menu, tool bar, notifications, pinned
		/// resources, the details screen -- on a world that never got one, and says whether it did.
		///
		/// WHY A BLUEPRINT CANVAS HAS NO HUD (measured/14 by the <c>--hud-census</c>
		/// flag's creation stacks). Those screens are not in any prefab C# instantiates: a scene
		/// <c>ScheduledUIInstantiation</c> spawns them when <c>GameHashes.StartGameUser</c> fires.
		/// Vanilla fires it from <c>Game.OnSpawn</c> on a loaded save, straight after
		/// <c>BaseAlreadyCreated</c>, and from <c>MinionSelectScreen.OnProceed</c> on a new game. A
		/// canvas is a new game whose duplicant screen <see cref="BlueprintWorld"/> dismisses with
		/// <c>BaseAlreadyCreated</c> alone, so <c>StartGameUser</c> never fires and the HUD never
		/// exists -- not hidden, not destroyed, never made.
		///
		/// THE CURE IS KLEI'S OWN EVENT, fired the way their load path fires it. Firing it again is
		/// harmless: <c>ScheduledUIInstantiation</c> guards itself with a completed flag, and a world
		/// that already has <c>OverlayMenu.Instance</c> is left alone here before it is even tried.
		///
		/// Opt-in, called by <see cref="RigHarness"/> only when a run passes
		/// <see cref="RigHarness.KeepHudFlag"/>: every run without it keeps the HUD-less world its
		/// rig was gated on.
		/// </summary>
		/// <returns>True when <c>OverlayMenu.Instance</c> exists afterwards.</returns>
		public static bool BuildVanillaHud(string logPrefix)
		{
			if (Game.Instance == null)
			{
				Debug.Log(logPrefix + "no Game instance, so there is no HUD to build yet");
				return false;
			}
			if (OverlayMenu.Instance != null)
			{
				Debug.Log(logPrefix + "vanilla HUD already built (StartGameUser has fired); nothing to do");
				return true;
			}
			Game.Instance.Trigger((int)GameHashes.StartGameUser);
			bool built = OverlayMenu.Instance != null;
			Debug.Log(logPrefix + "fired GameHashes.StartGameUser to build the vanilla HUD: OverlayMenu "
				+ (built ? "ALIVE" : "still NULL") + ", ToolMenu " + (ToolMenu.Instance != null ? "ALIVE" : "NULL")
				+ ", PlanScreen " + (PlanScreen.Instance != null ? "ALIVE" : "NULL"));
			return built;
		}

		/// <summary>
		/// Makes sure <c>PinnedResourcesPanel.Instance</c> is set before the HUD is hidden, by
		/// activating the panel if the scene has one that never spawned.
		///
		/// WHY A RIG HAS TO CARE ABOUT A UI PANEL. <c>WorldInventory.Update</c> calls
		/// <c>PinnedResourcesPanel.Instance.ClearExcessiveNewItems()</c> with no null check, once,
		/// the first time it finishes counting the inventory. On a colony loaded from a save the
		/// panel has long since spawned and set its own static in <c>OnSpawn</c>; on a blueprint
		/// canvas it had not, so that call threw a NullReferenceException on screen every run.
		///
		/// MEASURED, NOT ASSUMED. The first suspect was this very method -- deactivating an
		/// ancestor before <c>OnSpawn</c> would leave the static null forever -- and a probe put
		/// either side of the hide disproved it: the Instance was already NULL *before* anything
		/// was hidden. Nothing in the game's code constructs this panel either, so it is a scene
		/// object that simply never came up, which is why the fix is to activate it rather than to
		/// stop hiding something.
		///
		/// It is then hidden along with the rest of the HUD by the caller, so a presentation run
		/// looks exactly as it did -- the panel exists, sets its static, and goes away again.
		/// </summary>
		private static void EnsurePinnedResourcesPanelExists()
		{
			if (PinnedResourcesPanel.Instance != null)
			{
				return;
			}

			PinnedResourcesPanel[] panels = Object.FindObjectsByType<PinnedResourcesPanel>(
				FindObjectsInactive.Include, FindObjectsSortMode.None);
			if (panels == null || panels.Length == 0)
			{
				Debug.Log("[OniFramework] DemoRig: no PinnedResourcesPanel in the scene at all, so "
					+ "WorldInventory.Update will throw once on its first inventory pass. Vanilla "
					+ "dereferences that static without a null check.");
				return;
			}

			for (int i = 0; i < panels.Length; i++)
			{
				if (panels[i] != null && panels[i].gameObject != null
					&& !panels[i].gameObject.activeSelf)
				{
					panels[i].gameObject.SetActive(true);
				}
			}

			Debug.Log("[OniFramework] DemoRig: woke " + panels.Length + " inactive "
				+ "PinnedResourcesPanel(s) so their OnSpawn sets the static WorldInventory.Update "
				+ "dereferences; Instance is now "
				+ (PinnedResourcesPanel.Instance != null ? "ALIVE" : "still NULL"));
		}

		/// <summary>
		/// Logs, for each screen of the vanilla HUD, whether it exists and in what state: never
		/// instantiated, active, or present but held inactive -- and by which ancestor. Also whether
		/// <c>WorldSpaceCanvas</c> is up (<c>OverlayScreen.OnPrefabInit</c> dereferences it) and which
		/// screens Klei's own HUD prefab carries.
		///
		/// A MEASUREMENT, NOT A FIX. A blueprint canvas has never had these screens -- the sixth and
		/// eighth canvas gaps are both a HUD screen's static left null -- although
		/// <c>Game.SpawnPlayer</c> starts <c>ScreenPrefabs.Instance.HudScreen</c> on every path. A
		/// screen that exists inactive is cured by waking it, the way
		/// <see cref="EnsurePinnedResourcesPanelExists"/> cures its own; one that was never
		/// instantiated is not, and the DetailsScreen gap already showed that the obvious guess can
		/// be the wrong one. This says which before anything is chosen.
		/// </summary>
		public static void LogHudCensus(string logPrefix)
		{
			LogUiSpawners(logPrefix);
			System.Type[] screens =
			{
				typeof(Hud), typeof(RootMenu), typeof(PlanScreen), typeof(BuildMenu), typeof(ToolMenu),
				typeof(OverlayMenu), typeof(OverlayScreen), typeof(ManagementMenu),
				typeof(TopLeftControlScreen), typeof(DetailsScreen), typeof(NotificationScreen),
				typeof(SpeedControlScreen), typeof(PinnedResourcesPanel)
			};
			foreach (System.Type type in screens)
			{
				Object[] found = Object.FindObjectsByType(type, FindObjectsInactive.Include,
					FindObjectsSortMode.None);
				if (found == null || found.Length == 0)
				{
					Debug.Log(logPrefix + "HUD census: " + type.Name + " -- NOT INSTANTIATED");
					continue;
				}
				Component first = found[0] as Component;
				Debug.Log(logPrefix + "HUD census: " + type.Name + " x" + found.Length + " -- "
					+ (first != null ? DescribeActivation(first.gameObject) : "not a component"));
			}
			Debug.Log(logPrefix + "HUD census: WorldSpaceCanvas "
				+ (GameObject.Find("WorldSpaceCanvas") != null ? "present and active"
					: "NOT FOUND among active objects"));
			Debug.Log(logPrefix + "HUD census: GameScreenManager.Instance "
				+ (GameScreenManager.Instance != null ? "present" : "NULL"));
			if (GameScreenManager.Instance != null && GameScreenManager.Instance.ssOverlayCanvas != null)
			{
				Transform overlay = GameScreenManager.Instance.ssOverlayCanvas.transform;
				System.Text.StringBuilder children = new System.Text.StringBuilder();
				for (int i = 0; i < overlay.childCount; i++)
				{
					Transform child = overlay.GetChild(i);
					if (i > 0)
					{
						children.Append(", ");
					}
					children.Append(child.name).Append(child.gameObject.activeSelf ? "" : " (inactive)")
						.Append(" {").Append(child.childCount).Append('}');
				}
				Debug.Log(logPrefix + "HUD census: ScreenSpaceOverlayCanvas has " + overlay.childCount
					+ " children: " + children);
			}
			ScreenPrefabs prefabs = ScreenPrefabs.Instance;
			if (prefabs == null || prefabs.HudScreen == null)
			{
				Debug.Log(logPrefix + "HUD census: ScreenPrefabs.Instance.HudScreen is NULL");
				return;
			}
			KScreen[] inPrefab = prefabs.HudScreen.GetComponentsInChildren<KScreen>(true);
			System.Text.StringBuilder names = new System.Text.StringBuilder();
			for (int i = 0; i < inPrefab.Length; i++)
			{
				if (i > 0)
				{
					names.Append(", ");
				}
				names.Append(inPrefab[i].GetType().Name);
			}
			Debug.Log(logPrefix + "HUD census: Klei's HudScreen prefab carries " + inPrefab.Length
				+ " KScreen(s): " + names);
		}

		/// <summary>
		/// The command-line switch that runs <see cref="LogHudCensus"/> in an ordinary game, not a
		/// rig: 5 s of unscaled time after <c>Game.OnSpawn</c>, once.
		/// </summary>
		public const string HudCensusFlag = "--hud-census";

		private static bool hudCensusInstalled;

		/// <summary>
		/// Patches <c>Game.OnSpawn</c> to take one HUD census, but only when the game was launched
		/// with <see cref="HudCensusFlag"/>; without the flag nothing is patched. Exists because a
		/// canvas rig's census is only half a measurement: it lists what a canvas lacks, and the
		/// other half -- where each missing screen lives in a game that HAS a HUD, and what put it
		/// there -- has to be taken from a loaded colony. Idempotent.
		/// </summary>
		public static void InstallHudCensusFlag(HarmonyLib.Harmony harmony)
		{
			if (hudCensusInstalled || harmony == null || !RigRegistry.AnyFlag(HudCensusFlag))
			{
				return;
			}
			hudCensusInstalled = true;
			harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Game), "OnPrefabInit"), null,
				new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(DemoRig),
					nameof(GamePrefabInitCensusPostfix))));
			harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Game), "OnSpawn"), null,
				new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(DemoRig),
					nameof(GameSpawnCensusPostfix))));
			harmony.Patch(HarmonyLib.AccessTools.Method(typeof(KScreen), "Deactivate"),
				new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(DemoRig),
					nameof(KScreenDeactivateCensusPrefix))));

			// WHO CREATES THEM. On a canvas, TopRight and BottomPanelRight are already empty at
			// Game.OnPrefabInit and nothing destroyed, so a canvas never gets these
			// screens. Instantiate is synchronous -- Awake, then OnPrefabInit, run inside the call --
			// so a stack taken in each screen's own OnPrefabInit names whatever instantiated it in a
			// game that does have them.
			System.Type[] created = { typeof(OverlayMenu), typeof(ToolMenu), typeof(NotificationScreen),
				typeof(PinnedResourcesPanel) };
			foreach (System.Type type in created)
			{
				harmony.Patch(HarmonyLib.AccessTools.DeclaredMethod(type, "OnPrefabInit"), null,
					new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(DemoRig),
						nameof(HudScreenPrefabInitCensusPostfix))));
			}
		}

		private static void HudScreenPrefabInitCensusPostfix(KMonoBehaviour __instance)
		{
			if (__instance == null)
			{
				return;
			}
			Debug.Log("[OniFramework] " + HudCensusFlag + ": CREATED " + __instance.GetType().Name
				+ " -- " + DescribeActivation(__instance.gameObject) + " at frame " + Time.frameCount
				+ "\n" + System.Environment.StackTrace);
		}

		// WHEN, NOT ONLY WHETHER. A canvas and a loaded colony load the same "backend" scene and
		// share the same GameScreenManager, yet the colony's TopRight holds 7 objects and the
		// canvas's holds none. Either the canvas never gets them or it
		// loses them. These hooks settle which: the overlay canvas's containers are counted as
		// early as Game.OnPrefabInit and again at Game.OnSpawn, every object then under them gets a
		// witness that logs its own destruction, and KScreen.Deactivate -- which Destroy()s its
		// GameObject -- logs who called it.
		private static void GamePrefabInitCensusPostfix()
		{
			LogOverlayCanvasChildren("[OniFramework] " + HudCensusFlag + " at Game.OnPrefabInit: ");
			AttachHudDestroyWitnesses();
		}

		private static void GameSpawnCensusPostfix()
		{
			LogOverlayCanvasChildren("[OniFramework] " + HudCensusFlag + " at Game.OnSpawn: ");
			AttachHudDestroyWitnesses();
			UIScheduler.Instance.Schedule("OniFrameworkHudCensus", 5f, delegate
			{
				LogHudCensus("[OniFramework] " + HudCensusFlag + ": ");
			});
		}

		private static void KScreenDeactivateCensusPrefix(KScreen __instance)
		{
			if (__instance == null)
			{
				return;
			}
			Debug.Log("[OniFramework] " + HudCensusFlag + ": KScreen.Deactivate on "
				+ __instance.GetType().Name + " '" + __instance.name + "' at frame " + Time.frameCount
				+ "\n" + System.Environment.StackTrace);
		}

		private static void LogOverlayCanvasChildren(string logPrefix)
		{
			if (GameScreenManager.Instance == null || GameScreenManager.Instance.ssOverlayCanvas == null)
			{
				Debug.Log(logPrefix + "no GameScreenManager overlay canvas yet");
				return;
			}
			Transform overlay = GameScreenManager.Instance.ssOverlayCanvas.transform;
			System.Text.StringBuilder children = new System.Text.StringBuilder();
			for (int i = 0; i < overlay.childCount; i++)
			{
				Transform child = overlay.GetChild(i);
				if (i > 0)
				{
					children.Append(", ");
				}
				children.Append(child.name).Append(child.gameObject.activeSelf ? "" : " (inactive)")
					.Append(" {").Append(child.childCount).Append('}');
			}
			Debug.Log(logPrefix + "ScreenSpaceOverlayCanvas has " + overlay.childCount + " children at frame "
				+ Time.frameCount + ": " + children);
		}

		private static void AttachHudDestroyWitnesses()
		{
			if (GameScreenManager.Instance == null || GameScreenManager.Instance.ssOverlayCanvas == null)
			{
				return;
			}
			Transform overlay = GameScreenManager.Instance.ssOverlayCanvas.transform;
			int attached = 0;
			for (int i = 0; i < overlay.childCount; i++)
			{
				Transform container = overlay.GetChild(i);
				for (int c = 0; c < container.childCount; c++)
				{
					GameObject held = container.GetChild(c).gameObject;
					if (held.GetComponent<HudDestroyWitness>() != null)
					{
						continue;
					}
					held.AddComponent<HudDestroyWitness>().Label = container.name + "/" + held.name;
					attached++;
				}
			}
			Debug.Log("[OniFramework] " + HudCensusFlag + ": attached " + attached
				+ " destroy witness(es) under the overlay canvas's containers");
		}

		/// <summary>
		/// Logs its own GameObject's destruction. Destroy() is deferred to the end of the frame, so
		/// the stack here is Unity's, not the caller's -- the frame number is what ties it to a
		/// KScreen.Deactivate line logged earlier in the same frame. Unity only sends OnDestroy to
		/// an object that was ever active, so a witness on an object that never woke stays silent.
		/// </summary>
		private sealed class HudDestroyWitness : MonoBehaviour
		{
			private static bool quitting;

			public string Label;

			private void Awake()
			{
				Application.quitting -= OnQuitting;
				Application.quitting += OnQuitting;
			}

			private static void OnQuitting()
			{
				quitting = true;
			}

			private void OnDestroy()
			{
				if (quitting)
				{
					return;
				}
				Debug.Log("[OniFramework] " + HudCensusFlag + ": DESTROYED " + Label + " at frame "
					+ Time.frameCount);
			}
		}

		/// <summary>
		/// The second half of the census, and the one that names the fix. Klei's HUD screens are not
		/// inside the HUD prefab (it carries only <c>Hud</c>) and no C# code instantiates them: they
		/// come from <c>InstantiateUIPrefabChild</c> components in the scene, each holding a serialized
		/// prefab list it spawns on awake or when <c>Instantiate()</c> is called. So for every spawner:
		/// where it is, whether it is active, whether it fires on awake, whether it already fired,
		/// and which KScreen types each of its prefabs carries.
		/// </summary>
		private static void LogUiSpawners(string logPrefix)
		{
			InstantiateUIPrefabChild[] spawners = Object.FindObjectsByType<InstantiateUIPrefabChild>(
				FindObjectsInactive.Include, FindObjectsSortMode.None);
			Debug.Log(logPrefix + "HUD census: " + (spawners == null ? 0 : spawners.Length)
				+ " InstantiateUIPrefabChild spawner(s) in the scene");
			if (spawners == null)
			{
				return;
			}
			foreach (InstantiateUIPrefabChild spawner in spawners)
			{
				if (spawner == null)
				{
					continue;
				}
				bool already = HarmonyLib.Traverse.Create(spawner).Field("alreadyInstantiated").GetValue<bool>();
				System.Text.StringBuilder carried = new System.Text.StringBuilder();
				GameObject[] prefabs = spawner.prefabs ?? new GameObject[0];
				for (int i = 0; i < prefabs.Length; i++)
				{
					if (i > 0)
					{
						carried.Append("; ");
					}
					if (prefabs[i] == null)
					{
						carried.Append("null");
						continue;
					}
					carried.Append(prefabs[i].name).Append(" [");
					KScreen[] kinds = prefabs[i].GetComponentsInChildren<KScreen>(true);
					for (int k = 0; k < kinds.Length; k++)
					{
						if (k > 0)
						{
							carried.Append('/');
						}
						carried.Append(kinds[k].GetType().Name);
					}
					carried.Append(']');
				}
				Debug.Log(logPrefix + "HUD census: spawner " + DescribeActivation(spawner.gameObject)
					+ ", InstantiateOnAwake " + spawner.InstantiateOnAwake + ", alreadyInstantiated "
					+ already + ", prefabs: " + carried);
			}

			// THE OTHER SPAWNER. Klei has a second, simpler one: SpawnScreen holds one serialized
			// GameObject and KInstantiateUIs it under itself in OnPrefabInit -- no flag, no list, and
			// OnPrefabInit only runs once the object has been awake, so one held inactive never
			// fires. The first census counted only InstantiateUIPrefabChild and so could not see
			// these at all. For each: where, whether active, what it carries, and whether a copy of
			// that prefab is already sitting under it.
			SpawnScreen[] screenSpawners = Object.FindObjectsByType<SpawnScreen>(
				FindObjectsInactive.Include, FindObjectsSortMode.None);
			Debug.Log(logPrefix + "HUD census: " + (screenSpawners == null ? 0 : screenSpawners.Length)
				+ " SpawnScreen spawner(s) in the scene");
			if (screenSpawners == null)
			{
				return;
			}
			foreach (SpawnScreen screenSpawner in screenSpawners)
			{
				if (screenSpawner == null)
				{
					continue;
				}
				GameObject prefab = screenSpawner.Screen;
				string carriedKinds = "";
				bool spawned = false;
				if (prefab != null)
				{
					KScreen[] kinds = prefab.GetComponentsInChildren<KScreen>(true);
					for (int k = 0; k < kinds.Length; k++)
					{
						carriedKinds += (k > 0 ? "/" : "") + kinds[k].GetType().Name;
					}
					Transform holder = screenSpawner.transform;
					for (int c = 0; c < holder.childCount; c++)
					{
						string childName = holder.GetChild(c).name;
						if (childName == prefab.name || childName == prefab.name + "(Clone)")
						{
							spawned = true;
							break;
						}
					}
				}
				Debug.Log(logPrefix + "HUD census: SpawnScreen " + DescribeActivation(screenSpawner.gameObject)
					+ ", children " + screenSpawner.transform.childCount + ", screen "
					+ (prefab == null ? "null" : prefab.name + " [" + carriedKinds + "]")
					+ ", copy present " + spawned);
			}
		}

		private static string DescribeActivation(GameObject go)
		{
			string state = go.activeInHierarchy ? "ACTIVE"
				: (go.activeSelf ? "inactive through an ancestor" : "inactive itself");
			Transform heldBy = null;
			for (Transform t = go.transform; t != null; t = t.parent)
			{
				if (!t.gameObject.activeSelf)
				{
					heldBy = t;
					break;
				}
			}
			string path = go.name;
			Transform parent = go.transform.parent;
			for (int depth = 0; parent != null && depth < 6; depth++)
			{
				path = parent.name + "/" + path;
				parent = parent.parent;
			}
			return state + " at " + path
				+ (heldBy != null && heldBy.gameObject != go ? " (held off by '" + heldBy.name + "')" : "");
		}

		private static void HideScreen(Component screen)
		{
			if (screen != null && screen.gameObject != null && screen.gameObject.activeSelf)
			{
				screen.gameObject.SetActive(false);
			}
		}

		/// <summary>
		/// Slides ONI's notification list down the screen by <paramref name="pixelsDown"/>, so a
		/// scenario's own overlay panels along the top do not sit on top of the game's warnings.
		///
		/// The warnings matter in a rig like the phase-change loop: overpressure and
		/// condensation notices are part of what is being demonstrated, and a demonstration that
		/// hides the game's own reaction to it is showing half the picture. Moving the list rather
		/// than moving our panels keeps the panels where a viewer already expects them.
		///
		/// Safe to call repeatedly: the offset is applied once and remembered, so a per-sample call
		/// does not walk the list off the bottom of the screen.
		/// </summary>
		public static void PushNotificationsDown(float pixelsDown)
		{
			if (notificationsMoved)
			{
				return;
			}

			var screen = Object.FindFirstObjectByType<NotificationScreen>();
			if (screen == null)
			{
				return;
			}

			var rect = screen.transform as RectTransform;
			if (rect == null)
			{
				return;
			}

			rect.anchoredPosition = new Vector2(rect.anchoredPosition.x,
				rect.anchoredPosition.y - pixelsDown);
			notificationsMoved = true;
		}

		private static bool notificationsMoved;

		/// <summary>
		/// Runs the simulation at ONI's own debug super-speed while a scenario waits through a
		/// phase that only needs to ELAPSE -- a charge settling, a reservoir filling, a transient
		/// decaying -- and puts the speed back afterwards.
		///
		/// `SpeedControlScreen.ToggleRidiculousSpeed` is vanilla's own switch between a 3x and a
		/// 10x ultra speed (it flips `ultraSpeed` between those two values
		/// and selects speed index 2). Using the game's own control rather than touching
		/// `Time.timeScale` keeps every system that reads the game speed consistent, sim included.
		///
		/// This does NOT change what the simulation computes, only how fast it is stepped, so a
		/// measurement taken afterwards is unaffected. Take measurements at normal speed anyway:
		/// a sample loop that pauses for a fixed wall-clock interval would otherwise be sampling a
		/// different amount of simulated time per sample depending on the speed.
		/// </summary>
		public static void SetFastForward(bool on)
		{
			SpeedControlScreen screen = SpeedControlScreen.Instance;
			if (screen == null)
			{
				return;
			}

			bool isRidiculous = Mathf.Approximately(screen.ultraSpeed, 10f);
			if (on != isRidiculous)
			{
				screen.ToggleRidiculousSpeed();
			}

			screen.SetSpeed(on ? 2 : 1);
		}

		/// <summary>
		/// Blacks out the ENTIRE map, then reveals only the rectangle a scenario built in.
		///
		/// Two problems, one fix. A stamp paints terrain but does not EXPLORE it, so a rig that
		/// lands in unexplored map renders as solid black and the run is invisible until someone
		/// turns on debug mode by hand (user-reported). And a rig revealed inside an already-
		/// explored colony competes for attention with everything else on screen. Blacking the map
		/// out first and then revealing only the test area solves both: the scenario is the only
		/// thing visible, on any save, explored or not.
		///
		/// <c>Grid.Reveal</c> only ever RAISES visibility (it takes a Math.Max against the current
		/// value), so there is no reveal call that can darken a cell; the blackout writes
		/// <c>Grid.Visible</c> and <c>Grid.Spawnable</c> directly, which is the only way to lower
		/// them.
		///
		/// Coordinates are inclusive world cell coordinates, not template-local ones.
		/// </summary>
		public static void RevealOnly(int minX, int minY, int maxX, int maxY)
		{
			if (Grid.Visible == null || Grid.Spawnable == null)
			{
				return;
			}

			for (int i = 0; i < Grid.Visible.Length; i++)
			{
				Grid.Visible[i] = 0;
			}
			for (int i = 0; i < Grid.Spawnable.Length; i++)
			{
				Grid.Spawnable[i] = 0;
			}

			for (int y = minY; y <= maxY; y++)
			{
				for (int x = minX; x <= maxX; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (Grid.IsValidCell(cell))
					{
						// forceReveal, because PreventFogOfWarReveal is set on some map areas and
						// a scenario's own rig must be visible regardless of what it landed on.
						Grid.Reveal(cell, byte.MaxValue, forceReveal: true);
					}
				}
			}
		}

		/// <summary>
		/// Walks outward from an anchor looking for a <paramref name="width"/> x
		/// <paramref name="height"/> area, plus <paramref name="margin"/> on every side, with
		/// nothing BUILT in it. Returns the bottom-left corner of the inner area.
		///
		/// Searched rather than offset by a constant because scenarios that each pick their own
		/// fixed offset from the same colony anchor only avoid each other by convention, and that
		/// convention has already failed once -- a rig stamped straight on top of another one.
		///
		/// Terrain is deliberately NOT a criterion: a stamp repaints every cell it covers, so rock,
		/// algae and open cave are equally fine to build into. What matters is that no BUILDING is
		/// there, because a stamp will not remove one and the result is two rigs interleaved.
		/// </summary>
		public static bool FindClearRegion(int anchorX, int anchorY, int width, int height,
			int margin, out int foundX, out int foundY)
		{
			foundX = anchorX;
			foundY = anchorY;

			// FAILURE DIAGNOSTICS, not decoration. When this returns false the caller can only say
			// "no clear area found", which is exactly the report PHASELOOP produced for a whole
			// session without anyone being able to say WHY -- an offline replica of this walk
			// against the same save found a clear site well inside the budget, so the model and
			// the live grid disagreed and neither could be checked against the other. The best
			// candidate and its blockers are the ground truth that settles it, and they are free
			// to collect while the search is running anyway.
			int bestX = anchorX;
			int bestY = anchorY;
			int bestBlocked = int.MaxValue;
			int bestRadius = 0;
			int tested = 0;

			for (int radius = 0; radius <= SearchRadius; radius += 2)
			{
				for (int dy = -radius; dy <= radius; dy += 2)
				{
					for (int dx = -radius; dx <= radius; dx += 2)
					{
						// Only the ring, so the search genuinely expands outward rather than
						// re-testing the middle on every pass.
						if (radius > 0 && Mathf.Abs(dx) != radius && Mathf.Abs(dy) != radius)
						{
							continue;
						}
						int x = anchorX + dx;
						int y = anchorY + dy;
						tested++;
						int blocked = CountRegionBlockers(x, y, width, height, margin, null);
						if (blocked == 0)
						{
							foundX = x;
							foundY = y;
							return true;
						}
						if (blocked < bestBlocked)
						{
							bestBlocked = blocked;
							bestX = x;
							bestY = y;
							bestRadius = radius;
						}
					}
				}
			}

			ReportSearchFailure(anchorX, anchorY, width, height, margin, tested,
				bestX, bestY, bestBlocked, bestRadius);
			return false;
		}

		/// <summary>
		/// How far <see cref="FindClearRegion"/> walks out from the anchor, in tiles.
		/// </summary>
		public const int SearchRadius = 60;

		/// <summary>
		/// Writes the one report that makes a failed site search actionable: where it looked, how
		/// close it got, and what was standing in the closest spot. Logged as an error because a
		/// rig that cannot place itself scores zero and every later assertion is meaningless.
		/// </summary>
		private static void ReportSearchFailure(int anchorX, int anchorY, int width, int height,
			int margin, int tested, int bestX, int bestY, int bestBlocked, int bestRadius)
		{
			int span = width + 2 * margin + 1;
			int rows = height + 2 * margin + 1;
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			sb.Append("DemoRig.FindClearRegion FAILED: no ").Append(width).Append('x')
				.Append(height).Append(" area (").Append(span).Append('x').Append(rows)
				.Append(" = ").Append(span * rows).Append(" cells including a ").Append(margin)
				.Append("-tile margin) is free of buildings, conduits or wire within ")
				.Append(SearchRadius).Append(" tiles of the anchor at ").Append(anchorX).Append(',')
				.Append(anchorY).Append(". ").Append(tested).Append(" candidate(s) tested.");
			sb.Append(" Grid is ").Append(Grid.WidthInCells).Append('x').Append(Grid.HeightInCells)
				.Append('.');

			if (bestBlocked != int.MaxValue)
			{
				List<string> blockers = new List<string>();
				CountRegionBlockers(bestX, bestY, width, height, margin, blockers);
				sb.Append(" CLOSEST was ").Append(bestX).Append(',').Append(bestY)
					.Append(" (radius ").Append(bestRadius).Append(") with ").Append(bestBlocked)
					.Append(" of ").Append(span * rows).Append(" cells blocked");
				if (blockers.Count > 0)
				{
					sb.Append(" by: ");
					for (int i = 0; i < blockers.Count; i++)
					{
						if (i > 0) { sb.Append(", "); }
						sb.Append(blockers[i]);
					}
				}
				sb.Append('.');
			}
			FrameworkLog.Error(sb.ToString());
		}

		/// <summary>
		/// Counts the cells of a region that <see cref="IsRegionClear"/> would reject, and
		/// optionally names what is standing in them.
		///
		/// This is the counting form of the same predicate rather than a second copy of it: a
		/// boolean that short-circuits on the first blocker cannot say whether a site missed by
		/// one tile or by three hundred, and that difference is what tells a "move the rig" fix
		/// apart from a "widen the search" one.
		/// </summary>
		/// <param name="blockers">
		/// When non-null, receives a deduplicated "Name xN" summary of what occupies the region.
		/// Capped, because a site inside a colony can be blocked by hundreds of tiles and the
		/// first handful already identify what is there.
		/// </param>
		public static int CountRegionBlockers(int baseX, int baseY, int width, int height,
			int margin, List<string> blockers)
		{
			const int MaxNamed = 8;
			Dictionary<string, int> counts = blockers != null
				? new Dictionary<string, int>() : null;
			int blocked = 0;
			int offGrid = 0;

			for (int y = -margin; y <= height + margin; y++)
			{
				for (int x = -margin; x <= width + margin; x++)
				{
					int cell = Grid.XYToCell(baseX + x, baseY + y);
					if (!Grid.IsValidCell(cell))
					{
						blocked++;
						offGrid++;
						continue;
					}
					GameObject occupant = BuildingAt(cell);
					if (occupant == null) { continue; }
					blocked++;
					if (counts == null) { continue; }
					string name = occupant.name ?? "<unnamed>";
					int seen;
					counts.TryGetValue(name, out seen);
					counts[name] = seen + 1;
				}
			}

			if (counts != null)
			{
				if (offGrid > 0)
				{
					blockers.Add("off-grid x" + offGrid);
				}
				List<KeyValuePair<string, int>> ordered =
					new List<KeyValuePair<string, int>>(counts);
				ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
				for (int i = 0; i < ordered.Count && blockers.Count < MaxNamed; i++)
				{
					blockers.Add(ordered[i].Key + " x" + ordered[i].Value);
				}
				if (ordered.Count > blockers.Count)
				{
					blockers.Add("and " + (ordered.Count - blockers.Count) + " more kind(s)");
				}
			}
			return blocked;
		}

		/// <summary>
		/// Paints a vacuum jacket and its sealing ring around a template block, in the one order
		/// that works.
		///
		/// WHY BOTH RINGS. Insulation Tile stops two rooms leaking into each other; it does not
		/// stop them leaking into the MAP. A rig is stamped into whatever terrain was there, that
		/// terrain has its own temperature and effectively infinite thermal mass, and the rig gets
		/// dragged toward it -- a heat path the experiment never intended and cannot control.
		/// Vacuum conducts nothing, so one tile of it isolates the block from the map completely.
		/// A solid ring OUTSIDE the vacuum then seals it, because otherwise the surrounding
		/// atmosphere simply flows into the vacuum and undoes the isolation.
		///
		/// ONE TILE OF VACUUM IS ENOUGH. Vacuum conducts nothing, so a thicker jacket isolates no
		/// better; the margin exists to be sealed, not to be deep.
		///
		/// ORDER MATTERS AND IT IS THE OUTER RING FIRST. Both rings are painted as full
		/// rectangles rather than as outlines, so the later, inner paint overwrites the middle of
		/// the earlier one. Painting the vacuum first would leave the seal ring stamped over it.
		///
		/// Coordinates are TEMPLATE-LOCAL, matching the cells the caller is already building; the
		/// block is assumed to occupy (0,0) to (blockWidth-1, blockHeight-1).
		///
		/// Shared here so that no rig has to grow its own copy, or go without one.
		/// </summary>
		/// <param name="cells">The template cell list being built. Appended to.</param>
		/// <param name="blockWidth">Width of the rig's own block, in tiles.</param>
		/// <param name="blockHeight">Height of the rig's own block, in tiles.</param>
		/// <param name="vacuumMargin">Thickness of the vacuum ring. One is the right answer.</param>
		/// <param name="temperatureK">Temperature for both rings.</param>
		/// <param name="sealMassKg">Mass per tile of the sealing ring.</param>
		/// <param name="sealElement">Element for the sealing ring. Granite by default.</param>
		public static void AddVacuumJacket(List<TemplateClasses.Cell> cells,
			int blockWidth, int blockHeight, int vacuumMargin, float temperatureK,
			float sealMassKg, SimHashes sealElement = SimHashes.Granite)
		{
			for (int y = -vacuumMargin - 1; y < blockHeight + vacuumMargin + 1; y++)
			{
				for (int x = -vacuumMargin - 1; x < blockWidth + vacuumMargin + 1; x++)
				{
					cells.Add(new TemplateClasses.Cell(x, y, sealElement,
						temperatureK, sealMassKg, null, 0));
				}
			}
			for (int y = -vacuumMargin; y < blockHeight + vacuumMargin; y++)
			{
				for (int x = -vacuumMargin; x < blockWidth + vacuumMargin; x++)
				{
					cells.Add(new TemplateClasses.Cell(x, y, SimHashes.Vacuum,
						temperatureK, 0f, null, 0));
				}
			}
		}

		/// <summary>
		/// Clears and paints exactly the cells a building will occupy, BEFORE it is built there.
		///
		/// THE BUG THIS EXISTS TO STOP: cutting out space for a building and missing its first
		/// column of tiles, so the building stands behind tiles that were never cleared. It is an
		/// off-by-anchor rather than an off-by-one. A building's cell is not its bottom-left
		/// corner: for a 5-wide Gas Reservoir the footprint runs from cell.x MINUS TWO to
		/// cell.x plus two. A rig that carves a room starting at the building's own cell and
		/// extending rightwards leaves the left two columns solid, and ONI reports exactly what
		/// has happened -- "Building entombment".
		///
		/// Guessing the offsets per building is how the bug got in. <c>BuildingDef.PlacementOffsets</c>
		/// is Klei's own answer and is what placement itself uses, so asking it cannot disagree
		/// with where the building actually lands, whatever its size or origin convention.
		///
		/// Painting rather than merely emptying, and with a trace mass rather than true vacuum,
		/// for the reason CompressorScreenshotDemo worked out the hard way: a permanently massless
		/// cell has its temperature zeroed every tick by vanilla's own ZeroMasslessCells and then
		/// reads 0 K -- and therefore 0 Pa -- forever, even once real mass arrives.
		/// </summary>
		/// <param name="def">The building about to be built.</param>
		/// <param name="cell">The cell it will be built at -- the same cell passed to Build.</param>
		/// <param name="orientation">The orientation it will be built with.</param>
		/// <param name="element">What to fill the footprint with.</param>
		/// <param name="massKg">Mass per cell. A trace amount, not zero.</param>
		/// <param name="temperatureK">Temperature for the painted cells.</param>
		/// <returns>The cells painted, so a caller can log or check them.</returns>
		public static List<int> ClearBuildingFootprint(BuildingDef def, int cell,
			Orientation orientation = Orientation.Neutral,
			SimHashes element = SimHashes.Oxygen, float massKg = 1f,
			float temperatureK = 293.15f)
		{
			var painted = new List<int>();
			if (def == null || !Grid.IsValidCell(cell))
			{
				return painted;
			}

			foreach (CellOffset offset in def.PlacementOffsets)
			{
				CellOffset rotated = Rotatable.GetRotatedCellOffset(offset, orientation);
				int target = Grid.OffsetCell(cell, rotated);
				if (!Grid.IsValidCell(target))
				{
					continue;
				}
				SimMessages.ReplaceElement(target, element, null, massKg, temperatureK);
				painted.Add(target);
			}
			return painted;
		}

		/// <summary>
		/// The RUNTIME twin of <see cref="AddVacuumJacket"/>, for the rigs that paint their test
		/// volume with <c>SimMessages.ReplaceElement</c> instead of stamping a template.
		///
		/// Same physics and same two rings, for the reasons written out on the template version:
		/// vacuum conducts nothing, so one tile of it cuts the rig off from a map whose thermal
		/// mass is effectively infinite, and a solid ring outside the vacuum stops the surrounding
		/// atmosphere simply flowing in and undoing it.
		///
		/// ONE DIFFERENCE, and it is the whole reason this is a separate method rather than a
		/// wrapper. The template version paints both rings as FULL RECTANGLES and relies on the
		/// rig's own cells being appended afterwards to overwrite the middle. At runtime the rig
		/// has already painted its interior, so a full rectangle would erase the experiment. This
		/// paints OUTLINES only.
		///
		/// THREE BANDS, NOT TWO. Outward from
		/// the block: one SEALING ring immediately outside it, then
		/// <paramref name="vacuumMargin"/> rings of vacuum, then one more sealing ring. The
		/// footprint is therefore <paramref name="vacuumMargin"/> + 2 tiles on every side, one
		/// more than it used to be.
		///
		/// WHY THE INNER RING EXISTS. The template path never needed one because the block it
		/// jackets is the rig's own SOLID rectangle, with every chamber carved well inside it --
		/// so a solid wall already stood between the experiment and the vacuum. The runtime path
		/// is handed the test volume ITSELF, and a test volume is gas. Vacuum against gas is not
		/// a boundary: the gas expands into it on the next sim tick, the jacket fills, and the
		/// isolation the rig thinks it has quietly stops existing.
		///
		/// MEASURED, not reasoned. Scanned frame by frame, a two-band jacket's vacuum ring held 36
		/// contaminated cells early in a run and all 54 -- the entire ring -- by the end, with the
		/// worst cell going from 0.451 kg to 7.899 kg: a pipe row open straight from the test
		/// volume into the vacuum ring vents the rig into its own jacket. Any rig that measures a
		/// temperature would be isolated by a wall with a hole in it.
		///
		/// The inner ring is painted at <paramref name="temperatureK"/>, which is the temperature
		/// the caller is staging the volume at, so it starts at equilibrium and steals nothing
		/// measurable on its way there.
		///
		/// Coordinates are WORLD cell coordinates, inclusive, describing the block to enclose.
		/// Cells that are off-grid are skipped; nothing inside the block is touched.
		/// </summary>
		/// <param name="minX">Left edge of the block to enclose, inclusive.</param>
		/// <param name="minY">Bottom edge of the block to enclose, inclusive.</param>
		/// <param name="maxX">Right edge of the block to enclose, inclusive.</param>
		/// <param name="maxY">Top edge of the block to enclose, inclusive.</param>
		/// <param name="vacuumMargin">Thickness of the vacuum ring. One is the right answer.</param>
		/// <param name="temperatureK">Temperature for the sealing ring.</param>
		/// <param name="sealMassKg">Mass per tile of the sealing ring.</param>
		/// <param name="sealElement">Element for the sealing ring. Igneous rock by default,
		/// because it is the worst conductor among the ordinary solids and the ring's only jobs
		/// are to be solid and to conduct as little as possible.</param>
		public static void PaintVacuumJacket(int minX, int minY, int maxX, int maxY,
			int vacuumMargin = 1, float temperatureK = 294.15f, float sealMassKg = 2000f,
			SimHashes sealElement = SimHashes.IgneousRock)
		{
			if (vacuumMargin < 1)
			{
				vacuumMargin = 1;
			}

			// Outermost band first, innermost last. The three bands do not overlap, so this is
			// not the correctness constraint it is on the template path -- but a rig that is
			// watched while it builds should be seen to close the box before it empties it.
			PaintRing(minX, minY, maxX, maxY, vacuumMargin + 2, sealElement, sealMassKg,
				temperatureK);
			for (int d = 2; d <= vacuumMargin + 1; d++)
			{
				PaintRing(minX, minY, maxX, maxY, d, SimHashes.Vacuum, 0f, temperatureK);
			}
			PaintRing(minX, minY, maxX, maxY, 1, sealElement, sealMassKg, temperatureK);
		}

		/// <summary>
		/// One square outline at <paramref name="distance"/> tiles outside the given block.
		/// </summary>
		private static void PaintRing(int minX, int minY, int maxX, int maxY, int distance,
			SimHashes element, float massKg, float temperatureK)
		{
			int left = minX - distance;
			int right = maxX + distance;
			int bottom = minY - distance;
			int top = maxY + distance;

			for (int x = left; x <= right; x++)
			{
				PaintJacketCell(x, bottom, element, massKg, temperatureK);
				PaintJacketCell(x, top, element, massKg, temperatureK);
			}
			for (int y = bottom + 1; y <= top - 1; y++)
			{
				PaintJacketCell(left, y, element, massKg, temperatureK);
				PaintJacketCell(right, y, element, massKg, temperatureK);
			}
		}

		private static void PaintJacketCell(int x, int y, SimHashes element, float massKg,
			float temperatureK)
		{
			int cell = Grid.XYToCell(x, y);
			if (!Grid.IsValidCell(cell))
			{
				return;
			}
			SimMessages.ReplaceElement(cell, element, null, massKg, temperatureK);
		}

		/// <summary>
		/// Rings a rectangle in vanilla Insulation Tile built from igneous rock.
		///
		/// Igneous rock because it is the best-insulating material the tile accepts, and
		/// Insulation Tile because it is the best-insulating tile vanilla has. Rule 5.
		///
		/// The ring is the boundary of the rectangle given, so pass the rectangle you want
		/// ENCLOSED and the tiles land one outside it on every side. Cells already claimed by
		/// something the rig cares about more -- a window column, a door, a pipe chase -- are
		/// skipped rather than overwritten, which is why <paramref name="claimed"/> exists.
		///
		/// Coordinates are template-local, and <paramref name="claimed"/> is keyed the same way
		/// the caller keys it, through <paramref name="keyOf"/>.
		/// </summary>
		public static void AddInsulatedShell(List<Prefab> buildings, int minX, int minY,
			int maxX, int maxY, ICollection<int> claimed, System.Func<int, int, int> keyOf)
		{
			BuildingDef insulated = Assets.GetBuildingDef("InsulationTile");
			if (insulated == null)
			{
				return;
			}

			var boundary = new List<Vector2I>();
			for (int x = minX - 1; x <= maxX + 1; x++)
			{
				boundary.Add(new Vector2I(x, minY - 1));
				boundary.Add(new Vector2I(x, maxY + 1));
			}
			for (int y = minY - 1; y <= maxY + 1; y++)
			{
				boundary.Add(new Vector2I(minX - 1, y));
				boundary.Add(new Vector2I(maxX + 1, y));
			}
			PlaceShellTiles(buildings, insulated, boundary, claimed, keyOf);
		}

		/// <summary>
		/// Puts an Insulation Tile of igneous rock on every point of a boundary the caller has
		/// worked out for itself, skipping cells already claimed and never placing twice on one
		/// cell.
		///
		/// THE BOUNDARY STAYS THE RIG'S OWN, and that is the point of splitting it out here.
		/// <see cref="AddInsulatedShell"/> rings a rectangle, which is right for a rig with one
		/// chamber and wrong for the two that have several: AirLoopDemo's shell traces a corridor,
		/// a plant chamber and three tanks, and PhaseChangeLoopDemo's traces two rooms and the
		/// pipe chase between them. Neither is a rectangle and neither should be forced into one.
		/// What they DID share, in two copies, was this loop -- the claimed/placed bookkeeping and
		/// the igneous-rock choice -- so that is what moved.
		///
		/// Igneous rock because it is the worst-conducting material Insulation Tile accepts, and
		/// Insulation Tile because it is the best-insulating tile vanilla has. Rule 5.
		/// </summary>
		public static void PlaceShellTiles(List<Prefab> buildings, BuildingDef insulated,
			IEnumerable<Vector2I> boundary, ICollection<int> claimed,
			System.Func<int, int, int> keyOf)
		{
			if (buildings == null || insulated == null || boundary == null || keyOf == null)
			{
				return;
			}

			var placed = new HashSet<int>();
			foreach (Vector2I point in boundary)
			{
				int key = keyOf(point.x, point.y);
				if ((claimed != null && claimed.Contains(key)) || !placed.Add(key))
				{
					continue;
				}
				buildings.Add(MakeBuildingPrefab(insulated, point.x, point.y,
					SimHashes.IgneousRock));
			}
		}

		/// <summary>
		/// One template <c>Prefab</c> for a building, at template-local coordinates.
		///
		/// <c>TemplateLoader.PlaceBuilding</c>'s own width offset and
		/// <c>BuildingDef.GetBuildingCell</c>'s cancel exactly, so a Prefab's location is the same
		/// origin cell <c>BuildingDef.Build(cell, ...)</c> would take (by algebra on those two
		/// methods).
		/// </summary>
		public static Prefab MakeBuildingPrefab(BuildingDef def, int localX, int localY,
			SimHashes materialOverride = SimHashes.Void)
		{
			SimHashes elementHash;
			if (materialOverride != SimHashes.Void)
			{
				elementHash = materialOverride;
			}
			else
			{
				Tag materialTag = def.DefaultElements()[0];
				Element material = ElementLoader.GetElement(materialTag);
				elementHash = material != null ? material.id : SimHashes.Copper;
			}
			return new Prefab(def.PrefabID, Prefab.Type.Building, localX, localY, elementHash,
				298.15f);
		}

		/// <summary>
		/// True when every cell of the area plus its margin is valid and carries no building,
		/// conduit or wire. The margin should cover whatever shell or jacket the rig stamps around
		/// its own block, plus a tile, so two rigs cannot end up flush against each other with one
		/// stamping over the other's outer wall.
		/// </summary>
		public static bool IsRegionClear(int baseX, int baseY, int width, int height, int margin)
		{
			for (int y = -margin; y <= height + margin; y++)
			{
				for (int x = -margin; x <= width + margin; x++)
				{
					int cell = Grid.XYToCell(baseX + x, baseY + y);
					if (!Grid.IsValidCell(cell) || BuildingAt(cell) != null)
					{
						return false;
					}
				}
			}
			return true;
		}

		/// <summary>
		/// The BUILDING occupying a cell on any layer a stamp cannot overwrite, or null.
		///
		/// WHY THIS IS NOT JUST A NULL TEST ON Grid.Objects, which is what it used to be, and why
		/// that cost PHASELOOP every run it ever made on `harness-big`. ONI puts PLANTS on
		/// ObjectLayer.Building -- `EntityTemplates.ExtendEntityToBasicPlant` ends with
		/// `AddOrGet&lt;OccupyArea&gt;().objectLayers = new ObjectLayer[1] { ObjectLayer.Building }`,
		/// and `ObjectLayer.Plants` is referenced by nothing in the game but the sandbox spawner
		/// tool. So every wild mushroom, swamp lily and prickle flower on a 234-cycle map used to
		/// veto a site, and a mature colony has hundreds of them scattered through exactly the
		/// unmined rock a rig wants to stamp into. Measured: 3721 candidates rejected, the closest
		/// blocked by two cells, both of them `MushroomPlant`.
		///
		/// A plant is not an obstacle, and the check's own reason says so -- a site is rejected
		/// "because the stamp will not remove one". The stamp DOES remove plants:
		/// `TemplateLoader.BuildPhase1` calls `ClearPickups` and then `ClearEntities&lt;Crop&gt;`,
		/// `ClearEntities&lt;Health&gt;` and `ClearEntities&lt;Geyser&gt;` over every painted cell,
		/// destroying them outright. It does not remove buildings. `Building` is the shared base of
		/// `BuildingComplete` and `BuildingUnderConstruction`, so asking for that component is the
		/// exact question the doc was already claiming to ask.
		///
		/// Each layer is tested separately rather than with a `??` chain: a cell can hold a plant
		/// on the Building layer AND a wire on the Wire layer, and a chain would stop at the plant
		/// and call the cell free.
		/// </summary>
		public static GameObject BuildingAt(int cell)
		{
			for (int i = 0; i < BlockingLayers.Length; i++)
			{
				GameObject occupant = Grid.Objects[cell, (int)BlockingLayers[i]];
				if (occupant != null && occupant.GetComponent<Building>() != null)
				{
					return occupant;
				}
			}
			return null;
		}

		/// <summary>
		/// The object layers a stamp cannot overwrite. Logic wire and solid conduit are absent
		/// deliberately: a rig has never needed to avoid them, and adding a layer here makes every
		/// site search stricter on every map.
		/// </summary>
		private static readonly ObjectLayer[] BlockingLayers =
		{
			ObjectLayer.Building,
			ObjectLayer.GasConduit,
			ObjectLayer.LiquidConduit,
			ObjectLayer.Wire,
		};
	}
}
