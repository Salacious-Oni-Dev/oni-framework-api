using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// THE CARRIAGE ITSELF: the handful of places ONI moves liquid mass in managed code, each
	/// taught to move the dissolved payload in the same proportion.
	///
	/// <b>NOT ATTRIBUTED.</b> <see cref="DissolvedCargo.Install"/> wires every one of these by
	/// hand, for the reason <see cref="SimExtFrame"/> records: OniFramework must never patch
	/// anything merely by being loaded, and an attributed class would be swept up by a consumer's
	/// <c>Harmony.PatchAll(assembly)</c>.
	///
	/// <b>THE SITES, AND WHY THESE ARE ALL OF THEM.</b> Liquid mass in managed code lives in
	/// exactly two containers, and there are only so many ways it moves between them:
	/// <list type="number">
	/// <item><c>ConduitFlow.UpdateConduit</c> -- tile to tile inside a pipe, vanilla's own mover.
	/// The share is the one vanilla computes for disease three lines further down.</item>
	/// <item><c>ConduitFlow.RemoveElement</c> and <c>AddElement</c> -- every OTHER way mass enters
	/// or leaves a pipe. Eighteen files in the game call this pair, and rather than patch
	/// eighteen callers, the removal parks a PARCEL and the next add on the same call stack
	/// claims it. A bridge, a valve, a filter, an overflow and a preferential-flow junction all
	/// remove-then-add, and all of them are carried by that one rule without knowing it
	/// exists.</item>
	/// <item><c>ConduitDispenser.Dispense</c> and <c>ConduitConsumer.Consume</c> -- the two
	/// crossings between a pipe and a building's storage, which is where a parcel has no
	/// counterpart because the other end is an item rather than a tile.</item>
	/// <item><c>PrimaryElement.OnSplitFromChunk</c> and <c>OnAbsorb</c> -- the only two places an
	/// item's mass splits or merges. Storage moves whole objects, so storing, hauling and
	/// dropping need no rule at all: the payload is attached to the object.</item>
	/// </list>
	/// A caller that removes liquid and does something this class does not model -- an Ice Kettle,
	/// a critter, a recipe -- leaves its parcel unclaimed, and an unclaimed parcel goes back to
	/// the world as gas at the cell it came from. Nothing is deleted and
	/// nothing is invented; the worst case is that dissolved CO2 bubbles out of a machine that
	/// ought to have kept it, which is a missing feature rather than a broken ledger.
	/// </summary>
	internal static class DissolvedCargoPatches
	{
		/// <summary>
		/// Mass removed from a conduit tile, and the payload that came with it, waiting for the
		/// add that will place it. THREAD-STATIC because <c>ConduitFlow</c>'s own update runs on
		/// worker threads; in practice every remove-then-add pair is on the game thread, and the
		/// attribute costs nothing to be sure.
		/// </summary>
		private class Parcel
		{
			public int FromCell;
			public SimHashes Element;
			public float MassRemaining;
			public float[] Lanes;
			public int Frame;
		}

		[ThreadStatic] private static Parcel parcel;
		[ThreadStatic] private static ConduitDispenser activeDispenser;
		[ThreadStatic] private static ConduitConsumer activeConsumer;

		// UpdateConduit's own capture, likewise per thread.
		[ThreadStatic] private static int updateCell;
		[ThreadStatic] private static float updateMassBefore;
		[ThreadStatic] private static bool updateWatching;

		internal static void Install(Harmony harmony)
		{
			Patch(harmony, AccessTools.Method(typeof(ConduitFlow), "UpdateConduit"),
				"UpdateConduitPrefix", "UpdateConduitPostfix", "ConduitFlow.UpdateConduit");
			Patch(harmony, AccessTools.Method(typeof(ConduitFlow), "RemoveElement",
					new[] { typeof(ConduitFlow.Conduit), typeof(float) }),
				"RemoveElementPrefix", "RemoveElementPostfix", "ConduitFlow.RemoveElement");
			Patch(harmony, AccessTools.Method(typeof(ConduitFlow), "AddElement"),
				null, "AddElementPostfix", "ConduitFlow.AddElement");
			Patch(harmony, AccessTools.Method(typeof(ConduitFlow), "Sim200ms"),
				null, "Sim200msPostfix", "ConduitFlow.Sim200ms");
			Patch(harmony, AccessTools.Method(typeof(ConduitDispenser), "Dispense"),
				"DispensePrefix", "DispensePostfix", "ConduitDispenser.Dispense");
			Patch(harmony, AccessTools.Method(typeof(ConduitConsumer), "Consume"),
				"ConsumePrefix", "ConsumePostfix", "ConduitConsumer.Consume");
			Patch(harmony, AccessTools.Method(typeof(PrimaryElement), "OnSplitFromChunk"),
				null, "SplitPostfix", "PrimaryElement.OnSplitFromChunk");
			Patch(harmony, AccessTools.Method(typeof(PrimaryElement), "OnAbsorb"),
				null, "AbsorbPostfix", "PrimaryElement.OnAbsorb");
			Patch(harmony, AccessTools.Method(typeof(Storage), "ConsumeAndGetDisease",
					new[]
					{
						typeof(Tag), typeof(float), typeof(float).MakeByRefType(),
						typeof(Klei.SimUtil.DiseaseInfo).MakeByRefType(),
						typeof(float).MakeByRefType(), typeof(SimHashes).MakeByRefType(),
					}),
				"ConsumeStoragePrefix", "ConsumeStoragePostfix",
				"Storage.ConsumeAndGetDisease");
			Patch(harmony, AccessTools.Method(typeof(GeneratedBuildings),
					"LoadGeneratedBuildings"),
				null, "AttachToConduitsPostfix", "GeneratedBuildings.LoadGeneratedBuildings");
			Patch(harmony, AccessTools.Method(typeof(Assets), "AddPrefab"),
				null, "AttachToItemPrefabPostfix", "Assets.AddPrefab");
			// The private overload every save path converges on -- manual, autosave and the
			// retire-colony save all reach it through the public Save(string, ...).
			Patch(harmony, AccessTools.Method(typeof(SaveLoader), "Save",
					new[] { typeof(System.IO.BinaryWriter) }),
				"SavePrefix", null, "SaveLoader.Save(BinaryWriter)");
		}

		// Before a save is written: take every credit the sim has not answered for back off its
		// carrier, and write the carriers' mirrors through, so the file holds each gram once.
		// See DissolvedCargo.WithdrawOpenCredits.
		private static void SavePrefix()
		{
			DissolvedCargo.WithdrawOpenCredits();
			DissolvedCargo.FlushMirrors();
		}

		// A member that has moved would leave the carriage silently never firing, and a silent
		// carriage loses matter on every pump in the world. Throw instead, the same way
		// SimExtFrame.Install does.
		private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix,
			string what)
		{
			if (target == null)
			{
				throw new InvalidOperationException(
					"DissolvedCargo.Install could not find " + what + ". The game build has moved "
					+ "and the dissolved payload would be dropped without a word, so this throws. "
					+ "Re-check the member against the current game assembly before shipping.");
			}
			harmony.Patch(target,
				prefix == null ? null
					: new HarmonyMethod(AccessTools.Method(typeof(DissolvedCargoPatches), prefix)),
				postfix == null ? null
					: new HarmonyMethod(AccessTools.Method(typeof(DissolvedCargoPatches), postfix)));
		}

		// ---- 1. tile to tile, inside a pipe -------------------------------------------------
		//
		// The prefix records what the tile holds; the postfix sees what is gone and where the
		// mover sent it. Reading the outcome rather than reimplementing the decision is
		// deliberate: vanilla picks the destination through four flow directions, a pull
		// direction and a capacity test, and a second copy of that logic would be a second thing
		// to keep right. THE SHARE IS EXACTLY VANILLA'S OWN, moved / mass_before -- the same
		// expression it uses for the disease count in the lines this postfix straddles.

		private static void UpdateConduitPrefix(ConduitFlow __instance, ConduitFlow.Conduit conduit)
		{
			updateWatching = false;
			if (Game.Instance == null || !ReferenceEquals(__instance, Game.Instance.liquidConduitFlow))
			{
				return;
			}
			int cell = __instance.soaInfo.GetCell(conduit.idx);
			if (!Grid.IsValidCell(cell) || !(DissolvedCargo.ConduitTotal(cell) > 0f))
			{
				return;
			}
			updateCell = cell;
			updateMassBefore = __instance.GetContents(cell).mass;
			updateWatching = updateMassBefore > 0f;
		}

		private static void UpdateConduitPostfix(ConduitFlow __instance,
			ConduitFlow.Conduit conduit)
		{
			if (!updateWatching)
			{
				return;
			}
			updateWatching = false;
			float after = __instance.GetContents(updateCell).mass;
			float moved = updateMassBefore - after;
			if (!(moved > 0f))
			{
				return;
			}
			ConduitFlow.FlowDirections direction =
				__instance.soaInfo.GetTargetFlowDirection(conduit.idx);
			ConduitFlow.Conduit target =
				__instance.soaInfo.GetConduitFromDirection(conduit.idx, direction);
			if (target.idx == -1)
			{
				return;
			}
			int toCell = __instance.soaInfo.GetCell(target.idx);
			if (!Grid.IsValidCell(toCell))
			{
				return;
			}
			DissolvedCargo.ConduitMoveShare(updateCell, toCell, moved / updateMassBefore);
		}

		// ---- 2. the parcel: every other way mass enters or leaves a pipe --------------------

		private static void RemoveElementPrefix(ConduitFlow __instance,
			ConduitFlow.Conduit conduit, out float __state)
		{
			__state = 0f;
			if (Game.Instance == null || !ReferenceEquals(__instance, Game.Instance.liquidConduitFlow))
			{
				return;
			}
			int cell = __instance.soaInfo.GetCell(conduit.idx);
			if (Grid.IsValidCell(cell) && DissolvedCargo.ConduitTotal(cell) > 0f)
			{
				__state = __instance.GetContents(cell).mass;
			}
		}

		private static void RemoveElementPostfix(ConduitFlow __instance,
			ConduitFlow.Conduit conduit, ConduitFlow.ConduitContents __result, float __state)
		{
			// THE CALLER SAID IT WOULD DO THIS ITSELF. See DissolvedCargo.OwnMove: a mod that
			// pulls liquid out of a pipe with its own RemoveElement AND moves the cargo with its
			// own ConduitToItem is shadowed twice if this runs, and the parcel this would build
			// has no ConduitConsumer to settle into, so SettleParcel releases it to the world.
			if (DissolvedCargo.InOwnMove)
			{
				return;
			}
			if (!(__state > 0f) || !(__result.mass > 0f))
			{
				return;
			}
			int cell = __instance.soaInfo.GetCell(conduit.idx);
			float share = Mathf.Min(1f, __result.mass / __state);
			float[] lanes = TakeShare(cell, share);
			if (lanes == null)
			{
				return;
			}
			SettleParcel();
			parcel = new Parcel
			{
				FromCell = cell,
				Element = __result.element,
				MassRemaining = __result.mass,
				Lanes = lanes,
				Frame = Time.frameCount,
			};
		}

		private static void AddElementPostfix(int cell_idx, SimHashes element, float __result)
		{
			// Same rule as RemoveElementPostfix, and needed on this half too: a caller inside a
			// DissolvedCargo.OwnMove scope that pushes liquid back into a pipe accounts for its
			// own cargo, and must not also be handed whatever parcel happens to be in flight.
			if (DissolvedCargo.InOwnMove)
			{
				return;
			}
			if (!(__result > 0f) || !Grid.IsValidCell(cell_idx))
			{
				return;
			}
			if (ClaimParcel(cell_idx, element, __result))
			{
				return;
			}
			// No parcel: the mass came from a building's storage rather than from another tile.
			ConduitDispenser dispenser = activeDispenser;
			if (dispenser == null || dispenser.storage == null)
			{
				return;
			}
			PrimaryElement chunk = dispenser.storage.FindPrimaryElement(element);
			if (chunk == null || !(chunk.Mass > 0f))
			{
				return;
			}
			// Dispense() has not yet decremented the chunk, so its mass is still the pre-transfer
			// one and the share is the accepted mass over it.
			MoveItemToConduit(chunk.gameObject, cell_idx, __result / chunk.Mass);
		}

		private static bool ClaimParcel(int cell, SimHashes element, float accepted)
		{
			Parcel p = parcel;
			if (p == null || p.Lanes == null || p.Element != element
				|| p.Frame != Time.frameCount || !(p.MassRemaining > 0f))
			{
				return false;
			}
			float share = Mathf.Min(1f, accepted / p.MassRemaining);
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				float take = p.Lanes[lane] * share;
				if (take > 0f)
				{
					p.Lanes[lane] -= take;
					DissolvedCargo.ConduitAdd(cell, lane, take);
				}
			}
			p.MassRemaining -= accepted;
			if (!(p.MassRemaining > 0f) || Total(p.Lanes) <= 0f)
			{
				parcel = null;
			}
			return true;
		}

		/// <summary>
		/// A parcel nobody claimed. Its liquid went into a building this class does not model, so
		/// its gas goes back to the world at the tile it left. Called whenever a new parcel
		/// displaces an old one, and once per conduit update in case nothing else did.
		/// </summary>
		private static void SettleParcel()
		{
			Parcel p = parcel;
			parcel = null;
			if (p == null || p.Lanes == null || Total(p.Lanes) <= 0f)
			{
				return;
			}
			ConduitConsumer consumer = activeConsumer;
			if (consumer != null && consumer.storage != null)
			{
				PrimaryElement chunk = consumer.storage.FindPrimaryElement(p.Element);
				if (chunk != null && chunk.Mass > 0f)
				{
					GiveToItem(chunk.gameObject, p.Lanes);
					return;
				}
			}
			float temperatureK = Grid.IsValidCell(p.FromCell) && Grid.Temperature[p.FromCell] > 0f
				? Grid.Temperature[p.FromCell] : 293.15f;
			DissolvedCargo.ReleaseLanes(p.Lanes, p.FromCell, temperatureK);
		}

		// ---- 3. pipe to storage and back ----------------------------------------------------

		private static void DispensePrefix(ConduitDispenser __instance)
		{
			activeDispenser = __instance;
		}

		private static void DispensePostfix()
		{
			activeDispenser = null;
		}

		private static void ConsumePrefix(ConduitConsumer __instance)
		{
			activeConsumer = __instance;
		}

		private static void ConsumePostfix()
		{
			// The consumer removed from the pipe and stored the result; its parcel is claimed
			// here, against the chunk the storage now holds.
			SettleParcel();
			activeConsumer = null;
		}

		// ---- 4. an item's mass splitting and merging ----------------------------------------

		private static void SplitPostfix(PrimaryElement __instance, object data)
		{
			Pickupable source = data as Pickupable;
			if (source == null || source.PrimaryElement == null)
			{
				return;
			}
			float mine = __instance.Mass;
			float theirs = source.PrimaryElement.Mass;
			if (!(mine > 0f) || !(mine + theirs > 0f))
			{
				return;
			}
			// The same share vanilla computes for disease one line above this postfix, in mass
			// rather than in units: for an element chunk the two are the same number, and mass is
			// the quantity the payload is denominated in.
			DissolvedCargo.ItemMoveShare(source.gameObject, __instance.gameObject,
				mine / (mine + theirs));
		}

		private static void AbsorbPostfix(PrimaryElement __instance, object data)
		{
			Pickupable source = data as Pickupable;
			if (source == null)
			{
				return;
			}
			DissolvedCargo.ItemMoveShare(source.gameObject, __instance.gameObject, 1f);
		}

		// ---- 5. housekeeping on the game thread ---------------------------------------------

		private static void Sim200msPostfix(ConduitFlow __instance)
		{
			if (Game.Instance == null || !ReferenceEquals(__instance, Game.Instance.liquidConduitFlow))
			{
				return;
			}
			SettleParcel();
			DissolvedCargo.FlushMirrors();
		}

		// ---- 6. putting the component on the prefabs ----------------------------------------

		private static void AttachToConduitsPostfix()
		{
			int attached = 0;
			foreach (BuildingDef def in Assets.BuildingDefs)
			{
				if (def == null || def.BuildingComplete == null
					|| def.BuildingComplete.GetComponent<Conduit>() == null)
				{
					continue;
				}
				def.BuildingComplete.AddOrGet<DissolvedCargoComponent>();
				attached++;
			}
			FrameworkLog.Info("DissolvedCargo: carriage attached to " + attached
				+ " conduit building(s)");
		}

		// Element chunks and bottles. Only the LIQUID ones: a solid ore or a gas chunk has no
		// liquid to hold anything dissolved, and this runs for every prefab in the game, so the
		// component is put where it can ever be used rather than everywhere.
		private static void AttachToItemPrefabPostfix(KPrefabID prefab)
		{
			if (prefab == null)
			{
				return;
			}
			GameObject go = prefab.gameObject;
			if (go.GetComponent<Pickupable>() == null)
			{
				return;
			}
			PrimaryElement element = go.GetComponent<PrimaryElement>();
			if (element == null || ElementLoader.elements == null)
			{
				// Prefabs are registered after ElementLoader has read elements.yaml, so the null
				// is a guard rather than a case: it would mean a load order this class has never
				// seen, and a missed prefab is a carrier that cannot hold anything, not a crash.
				return;
			}
			Element e = ElementLoader.FindElementByHash(element.ElementID);
			if (e == null || !e.IsLiquid)
			{
				return;
			}
			go.AddOrGet<DissolvedCargoComponent>();
		}

		// ---- helpers -------------------------------------------------------------------------

		private static float[] TakeShare(int cell, float share)
		{
			if (!(share > 0f))
			{
				return null;
			}
			float[] taken = null;
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				float have = DissolvedCargo.ConduitLane(cell, lane);
				float take = have * Mathf.Min(1f, share);
				if (!(take > 0f))
				{
					continue;
				}
				if (taken == null)
				{
					taken = new float[DissolvedCargo.Lanes];
				}
				taken[lane] = take;
				DissolvedCargo.ConduitAdd(cell, lane, -take);
			}
			return taken;
		}

		private static void GiveToItem(GameObject item, float[] lanes)
		{
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				if (lanes[lane] > 0f)
				{
					DissolvedCargo.ItemAdd(item, lane, lanes[lane]);
					lanes[lane] = 0f;
				}
			}
		}

		private static void MoveItemToConduit(GameObject item, int cell, float share)
		{
			if (!(share > 0f))
			{
				return;
			}
			share = Mathf.Min(1f, share);
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				float take = DissolvedCargo.ItemLane(item, lane) * share;
				if (take > 0f)
				{
					DissolvedCargo.ItemAdd(item, lane, -take);
					DissolvedCargo.ConduitAdd(cell, lane, take);
				}
			}
		}

		// ---- 5. mass consumed straight out of a stored chunk -------------------------------
		//
		// THE FIFTH UNSHADOWED MASS-MOVER, found while wiring a Soda Fountain to a
		// carbonated pond. `Storage.ConsumeAndGetDisease` -- how a fabricator eats an ingredient,
		// how a duplicant eats, how the fountain drinks -- reduces a chunk by assigning
		// `PrimaryElement.Units` and destroys the object when it hits zero. No split, no absorb,
		// no message: the two `PrimaryElement` patches above never see it. So a chunk of
		// carbonated water that lost a fifth of its mass kept ALL of its dissolved gas, and a
		// chunk consumed to nothing took its gas into the void with it, which is matter deletion
		// of exactly the kind this layer exists to stop.
		//
		// WHAT HAPPENS NOW: the share of the cargo that matches the share of the mass comes off
		// the chunk, and goes either to an open `DissolvedCargo.ClaimConsumedCargo` scope -- for
		// a consumer that wants to follow its gas onto whatever it produced -- or, with no claim,
		// back to the world as gas at the storage's own cell.

		private class ConsumedSnapshot
		{
			public GameObject Item;
			public PrimaryElement Element;
			public float UnitsBefore;
			public float[] Lanes;
		}

		[ThreadStatic] private static System.Collections.Generic.List<ConsumedSnapshot> consuming;

		private static void ConsumeStoragePrefix(Storage __instance, Tag tag)
		{
			if (consuming != null)
			{
				consuming.Clear();
			}
			if (__instance == null || __instance.items == null)
			{
				return;
			}
			for (int i = 0; i < __instance.items.Count; i++)
			{
				GameObject item = __instance.items[i];
				if (item == null || !item.HasTag(tag))
				{
					continue;
				}
				DissolvedCargoComponent cargo = item.GetComponent<DissolvedCargoComponent>();
				if (cargo == null || !(cargo.Total() > 0f))
				{
					continue;
				}
				PrimaryElement pe = item.GetComponent<PrimaryElement>();
				if (pe == null || !(pe.Units > 0f))
				{
					continue;
				}
				if (consuming == null)
				{
					consuming = new System.Collections.Generic.List<ConsumedSnapshot>();
				}
				// The lanes are COPIED, because the object holding them may not survive this
				// call: a chunk consumed to zero is destroyed inside it, component and all.
				float[] lanes = new float[DissolvedCargo.Lanes];
				for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
				{
					lanes[lane] = cargo.Lane(lane);
				}
				consuming.Add(new ConsumedSnapshot
				{
					Item = item,
					Element = pe,
					UnitsBefore = pe.Units,
					Lanes = lanes,
				});
			}
		}

		private static void ConsumeStoragePostfix(Storage __instance)
		{
			if (consuming == null || consuming.Count == 0)
			{
				return;
			}
			float[] taken = null;
			for (int i = 0; i < consuming.Count; i++)
			{
				ConsumedSnapshot snapshot = consuming[i];
				float share;
				if (snapshot.Item == null || snapshot.Element == null)
				{
					// Consumed to nothing and destroyed. All of it went.
					share = 1f;
				}
				else
				{
					float after = snapshot.Element.Units;
					share = snapshot.UnitsBefore > 0f
						? (snapshot.UnitsBefore - after) / snapshot.UnitsBefore
						: 0f;
					if (share <= 0f)
					{
						continue;
					}
					if (share > 1f)
					{
						share = 1f;
					}
				}
				for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
				{
					float kg = snapshot.Lanes[lane] * share;
					if (!(kg > 0f))
					{
						continue;
					}
					if (taken == null)
					{
						taken = new float[DissolvedCargo.Lanes];
					}
					taken[lane] += kg;
					if (snapshot.Item != null)
					{
						// Off the surviving chunk, so what is left rides the mass that is left.
						DissolvedCargo.ItemAdd(snapshot.Item, lane, -kg);
					}
				}
			}
			consuming.Clear();
			if (taken == null)
			{
				return;
			}
			if (DissolvedCargo.OfferConsumedCargo(taken))
			{
				return;
			}
			int cell = __instance != null
				? Grid.PosToCell(__instance.transform.GetPosition())
				: Grid.InvalidCell;
			float temperatureK = Grid.IsValidCell(cell) && Grid.Temperature[cell] > 0f
				? Grid.Temperature[cell] : 293.15f;
			DissolvedCargo.ReleaseLanes(taken, cell, temperatureK);
		}

		private static float Total(float[] lanes)
		{
			if (lanes == null)
			{
				return 0f;
			}
			float total = 0f;
			for (int lane = 0; lane < lanes.Length; lane++)
			{
				total += lanes[lane];
			}
			return total;
		}
	}
}
