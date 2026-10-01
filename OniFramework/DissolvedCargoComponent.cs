using System.Collections.Generic;
using KSerialization;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// THE SERIALIZED HALF OF <see cref="DissolvedCargo"/>: the dissolved gas a conduit tile or an
	/// item is carrying, saved and loaded with the thing that carries it.
	///
	/// <b>ONE COMPONENT, TWO ROLES, and the difference is which side is authoritative.</b>
	/// <list type="bullet">
	/// <item>On a CONDUIT the runtime authority is <see cref="DissolvedCargo"/>'s per-cell array,
	/// because the pipe's own update path reads it on worker threads where a
	/// <c>GetComponent</c> is not allowed. This component is a write-through mirror of that array
	/// and only has to be correct at save time. Same arrangement, and the same reason, as
	/// <c>Mod1ThermoFluid.PipeTrappedMatterComponent</c>.</item>
	/// <item>On an ITEM this component IS the store. An item is touched at human rates -- stored,
	/// hauled, split, merged -- never in a per-tile sweep, so there is nothing to mirror and a
	/// second copy could only go stale.</item>
	/// </list>
	///
	/// <b>IT MUST BE ON THE PREFAB.</b> A component added at runtime has nothing for a loaded save
	/// to deserialize into, so <see cref="DissolvedCargoPatches"/> attaches it to every conduit
	/// building and every element-chunk prefab as they are registered. <see cref="DissolvedCargo"/>
	/// can still add one to a live object -- for something another mod spawns -- and that one
	/// serializes too, because KSerialization walks the live components of the object it saves.
	///
	/// <b>WHAT HAPPENS WHEN THE CARRIER DIES.</b> A pipe deconstructed, an item destroyed or
	/// eaten: the payload goes back into the world as gas at that object's own cell. It is never
	/// deleted, because a kilogram of CO2 that stops existing when you pick up the bottle is
	/// exactly the kind of magic the SDK exists to remove.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	public class DissolvedCargoComponent : KMonoBehaviour
	{
		// One float per lane, or null while the carrier holds nothing -- which is almost every
		// carrier in almost every world, so the null matters: it is what keeps this component
		// free to attach to every element chunk in the game.
		[Serialize] private float[] lanes;

		// Whether this component was born on a conduit, read once at OnSpawn. See OnCleanUp for
		// why it is cached rather than asked for. Not serialized: OnSpawn re-establishes it.
		private bool spawnedAsConduit;

		// Every live component that holds something, so a rig can total the world without
		// walking every GameObject in it. Not serialized: rebuilt by OnSpawn.
		private static readonly HashSet<DissolvedCargoComponent> loaded =
			new HashSet<DissolvedCargoComponent>();

		// A BUDGET, NOT A SWITCH. The cleanup line below is worth having in a shipped build --
		// it is the only place that can say why a carrier's cargo did not come back -- but a
		// world tearing down writes one per carrier, so it is capped instead of gated on a
		// flag nobody would remember to set. Reset with the rest of the store.
		private static int cleanupLogBudget = 64;

		internal static void ResetCleanupLogBudget()
		{
			cleanupLogBudget = 64;
		}

		/// <summary>
		/// The item half of <see cref="DissolvedCargo.Audit"/>: an item carrying dissolved gas
		/// with no mass left to carry it. The item twin of an orphaned conduit tile, and the
		/// shape a `Storage` transfer this class does not follow would leave behind.
		/// </summary>
		internal static void AuditItems(ref DissolvedCargo.AuditResult r, ref int logBudget)
		{
			foreach (DissolvedCargoComponent cargo in loaded)
			{
				if (cargo == null || cargo.lanes == null || cargo.IsConduit)
				{
					continue;
				}
				float total = cargo.Total();
				if (!(total > 0f))
				{
					continue;
				}
				r.ItemsWithCargo++;
				r.CarriedKg += total;
				PrimaryElement element = cargo.GetComponent<PrimaryElement>();
				if (element != null && element.Mass > 0f)
				{
					continue;
				}
				r.OrphanItems++;
				r.OrphanItemKg += total;
				if (logBudget > 0)
				{
					logBudget--;
					FrameworkLog.Warn("DissolvedCargo audit: item " + cargo.name + " holds "
						+ (total * 1000f).ToString("F3") + " g with "
						+ (element == null ? "no PrimaryElement" : "no mass")
						+ " -- cargo carried by nothing");
				}
			}
		}

		/// <summary>Kilograms on every item in the world that carries any. Conduits are not in
		/// here: they are in <see cref="DissolvedCargo"/>'s own per-cell store.</summary>
		internal static double TotalOnItemsKg()
		{
			double total = 0.0;
			foreach (DissolvedCargoComponent cargo in loaded)
			{
				if (cargo == null || cargo.lanes == null || cargo.IsConduit)
				{
					continue;
				}
				for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
				{
					total += cargo.lanes[lane];
				}
			}
			return total;
		}

		/// <summary>True when this component is mirroring a conduit tile rather than owning an
		/// item's payload. Read off the real <c>Conduit</c> component rather than stored, so it
		/// cannot go stale.</summary>
		internal bool IsConduit
		{
			get { return GetComponent<Conduit>() != null; }
		}

		/// <summary>Kilograms on <paramref name="lane"/>.</summary>
		internal float Lane(int lane)
		{
			return lanes == null || lane < 0 || lane >= DissolvedCargo.Lanes ? 0f : lanes[lane];
		}

		/// <summary>Every lane summed.</summary>
		internal float Total()
		{
			if (lanes == null)
			{
				return 0f;
			}
			float total = 0f;
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				total += lanes[lane];
			}
			return total;
		}

		/// <summary>Add (or, negative, remove) kilograms on one lane. Never goes below zero.
		/// </summary>
		internal void Add(int lane, float kg)
		{
			if (lane < 0 || lane >= DissolvedCargo.Lanes || float.IsNaN(kg))
			{
				return;
			}
			if (lanes == null)
			{
				if (!(kg > 0f))
				{
					return;
				}
				lanes = new float[DissolvedCargo.Lanes];
			}
			lanes[lane] = Mathf.Max(0f, lanes[lane] + kg);
		}

		/// <summary>Everything, taken off this carrier, or null if it held nothing.</summary>
		internal float[] TakeAll()
		{
			if (lanes == null)
			{
				return null;
			}
			float[] taken = null;
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				if (!(lanes[lane] > 0f))
				{
					continue;
				}
				if (taken == null)
				{
					taken = new float[DissolvedCargo.Lanes];
				}
				taken[lane] = lanes[lane];
				lanes[lane] = 0f;
			}
			return taken;
		}

		/// <summary>Write-through from <see cref="DissolvedCargo"/>'s per-cell store. Game thread
		/// only; <paramref name="source"/> may be null, meaning the tile now holds nothing.
		/// </summary>
		internal void Mirror(float[] source)
		{
			if (source == null)
			{
				lanes = null;
				return;
			}
			if (lanes == null)
			{
				lanes = new float[DissolvedCargo.Lanes];
			}
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				lanes[lane] = source[lane];
			}
		}

		protected override void OnSpawn()
		{
			base.OnSpawn();
			loaded.Add(this);
			// CACHED HERE AND NOWHERE ELSE, because OnCleanUp cannot ask. `IsConduit` reads the
			// live Conduit component, which is the right answer for as long as this object is
			// alive -- but during teardown the component may already have gone, and a conduit
			// that answers "item" at cleanup degasses out of the wrong store. Read once, while
			// the object is whole. Not serialized: a reloaded prefab re-runs OnSpawn.
			spawnedAsConduit = IsConduit;
			if (lanes == null)
			{
				return;
			}
			// A conduit's runtime authority is the per-cell array, so a loaded pipe has to push
			// what it deserialized back into it. An item is already its own authority and needs
			// nothing. Deliberately after base.OnSpawn, so the Conduit component IsConduit reads
			// is itself spawned.
			if (!IsConduit)
			{
				return;
			}
			int cell = Grid.PosToCell(transform.GetPosition());
			for (int lane = 0; lane < DissolvedCargo.Lanes; lane++)
			{
				if (lanes[lane] > 0f)
				{
					DissolvedCargo.ConduitAdd(cell, lane, lanes[lane]);
				}
			}
		}

		protected override void OnCleanUp()
		{
			loaded.Remove(this);
			// THE CARRIER IS GOING AWAY. SOMEWHERE IS NOT NOWHERE.
			//
			// A CONDUIT IS NOT ASKED ABOUT `lanes` AND THAT IS THE WHOLE POINT. For a conduit
			// this field is only the SERIALIZATION MIRROR, written by DissolvedCargo.
			// FlushMirrors on the game thread so a save has something to write; the runtime
			// authority is the per-cell store. Gating the degas on `lanes != null && Total() > 0`
			// therefore asked the mirror whether the store had anything, and on a tile destroyed
			// between flushes the mirror is null while the store is full, so destroying a tile
			// holding ten grams would delete them. So the conduit branch asks the store, and only the item branch asks the field,
			// which for an item IS the authority.
			//
			// ASKED BOTH WAYS, NOT ONE. The cached flag is still the primary answer, for the
			// reason above -- but it is only correct if this component was ever SPAWNED.
			// `KMonoBehaviour.Spawn` runs from `Start`, and `OnDestroy` calls `OnCleanUp`
			// UNCONDITIONALLY, so an object created and destroyed
			// without ever reaching `Start`, or one this component was added to after its
			// object had already spawned (`DissolvedCargo.CargoOf(go, true)` does exactly
			// that for a prefab the attachment pass missed), arrives here with
			// `spawnedAsConduit` still false while a live `Conduit` sits on the object. That
			// component would then take the ITEM branch, ask the mirror, find it empty, and
			// the tile's cargo would stay in the per-cell store with its carrier gone --
			// indistinguishable, from the ledger, from deleting it. So a `Conduit` visible
			// NOW is accepted as well; the cache only has to win when the component has
			// already been torn off, which is the case it was introduced for.
			bool conduitNow = GetComponent<Conduit>() != null;
			int cell = Grid.PosToCell(transform.GetPosition());
			float held = DissolvedCargo.ConduitTotal(cell);
			bool mirrorHas = lanes != null && Total() > 0f;

			// LOGGED BEFORE ANYTHING IS DECIDED, because the silence is the evidence. The 10:34
			// run degassed nothing and printed NO line at all, which left two readings alive
			// that the branches below cannot tell apart from the outside: the conduit branch
			// ran and resolved a cell that was not the one holding the cargo, or the flag and
			// the component had both gone and the item branch found an empty mirror. One line
			// carrying all four facts separates them.
			if (cleanupLogBudget > 0 && (spawnedAsConduit || conduitNow || held > 0f || mirrorHas))
			{
				cleanupLogBudget--;
				Vector3 at = transform.GetPosition();
				FrameworkLog.Info("DissolvedCargo: carrier cleanup on " + name
					+ " -- spawnedAsConduit=" + spawnedAsConduit + " conduitNow=" + conduitNow
					+ " cell=" + cell + " (" + Grid.CellToXY(cell).x + ","
					+ Grid.CellToXY(cell).y + ") pos=" + at.x.ToString("F2") + ","
					+ at.y.ToString("F2") + " storeHere=" + (held * 1000f).ToString("F3")
					+ " g mirror=" + (lanes == null ? "null"
						: (Total() * 1000f).ToString("F3") + " g"));
			}

			// CAUGHT, AND LOUDLY. Not defensive habit: an exception anywhere in here leaves the
			// payload in the store with its carrier gone and prints nothing anyone would connect
			// to it, which is precisely how the argument-evaluation throw above survived three
			// rig runs looking like a keying bug. A carrier can only die once, so a degas that
			// fails has no second chance and the least this can do is say so. `Degas` zeroes each
			// lane as it releases it, so a throw part way through leaves a consistent store
			// rather than a double release.
			try
			{
				if (spawnedAsConduit || conduitNow)
				{
					if (held > 0f)
					{
						float gave = DissolvedCargo.ConduitDegas(cell, ConduitTemperatureK(cell));
						FrameworkLog.Info("DissolvedCargo: conduit carrier at cell " + cell
							+ " died holding " + (held * 1000f).ToString("F3") + " g, gave back "
							+ (gave * 1000f).ToString("F3") + " g");
					}
					lanes = null;
				}
				else if (mirrorHas)
				{
					DissolvedCargo.ItemDegas(gameObject, CarrierTemperatureK());
				}
			}
			catch (System.Exception e)
			{
				FrameworkLog.Warn("DissolvedCargo: a carrier died without giving its payload back"
					+ " -- cell " + cell + ", " + (held * 1000f).ToString("F3")
					+ " g in the store, " + e);
			}
			base.OnCleanUp();
		}

		/// <summary>
		/// The temperature to give an ITEM's payload back at. <c>InternalTemperature</c>, not
		/// <c>Temperature</c>: the property runs <c>getTemperatureCallback</c>, and for anything
		/// registered with <c>StructureTemperatureComponents</c> that callback indexes a
		/// <c>KSplitCompactedVector</c> by a handle THE BUILDING HAS ALREADY RELEASED by the time
		/// this component's <c>OnCleanUp</c> runs, and a destroyed pipe throws
		/// <c>ArgumentOutOfRangeException ... KSplitCompactedVector.GetPayload ...
		/// StructureTemperatureComponents.OnGetTemperature ... PrimaryElement.get_Temperature ...
		/// DissolvedCargoComponent.CarrierTemperatureK ... OnCleanUp</c>, and because the throw
		/// happened while EVALUATING THE ARGUMENT, <c>ConduitDegas</c> was never entered at all.
		/// That is the whole of the three DISSOLVEPIPE failures first seen at 06:52: not a
		/// keying bug, not a missing component, an exception one frame before the degas.
		/// <c>InternalTemperature</c> is the backing field and cannot throw.
		/// </summary>
		private float CarrierTemperatureK()
		{
			PrimaryElement element = GetComponent<PrimaryElement>();
			if (element != null && element.InternalTemperature > 0f)
			{
				return element.InternalTemperature;
			}
			return GridTemperatureK(Grid.PosToCell(transform.GetPosition()));
		}

		/// <summary>
		/// The temperature to give a CONDUIT TILE's payload back at: the liquid's own, read off
		/// <c>ConduitFlow</c>. A pipe's <c>PrimaryElement</c> is the STRUCTURE -- the metal -- and
		/// the dissolved gas was never in the metal, so even where that property still answers it
		/// is the wrong number for this. Falls back to the cell's own reading once the contents
		/// are gone.
		/// </summary>
		private static float ConduitTemperatureK(int cell)
		{
			if (Game.Instance != null && Game.Instance.liquidConduitFlow != null
				&& Grid.IsValidCell(cell))
			{
				ConduitFlow.ConduitContents contents =
					Game.Instance.liquidConduitFlow.GetContents(cell);
				if (contents.mass > 0f && contents.temperature > 0f)
				{
					return contents.temperature;
				}
			}
			return GridTemperatureK(cell);
		}

		private static float GridTemperatureK(int cell)
		{
			return Grid.IsValidCell(cell) && Grid.Temperature[cell] > 0f
				? Grid.Temperature[cell] : 293.15f;
		}
	}
}
