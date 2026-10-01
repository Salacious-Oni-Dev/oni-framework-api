using System;
using System.Collections.Generic;

namespace OniFramework
{
	/// <summary>
	/// The connected conduit runs of the colony, taken from ONI'S OWN network partition rather
	/// than re-derived, and cached until the game says the topology changed.
	///
	/// WHY THIS EXISTS, AND WHY IT IS NOT A FLOOD FILL. <c>GasMixtureFacade.FloodFillPipeNetwork</c>
	/// / <c>FloodFillLiquidNetwork</c> is a breadth-first search over
	/// <c>UtilityNetworkManager.GetConnections</c>, run FRESH ON EVERY CALL with no cache, and
	/// many callers are per-tick components. Each call allocates a <c>HashSet</c>, a
	/// <c>Queue</c>, a <c>List</c> and an array, and allocation -- not CPU time -- is ONI's main
	/// lag cause, because the freeze players feel is Mono's GC and
	/// the only lever a mod has is how often a collection happens.
	///
	/// The BFS was also re-deriving something the game already knows.
	/// <c>UtilityNetworkManager.RebuildNetworks</c> is ONI's own connected-components pass; it
	/// stamps <c>networkIdx</c> into its physical grid for every node it visits, and
	/// <c>GetNetworkForCell(cell)</c> reads that stamp in O(1). It runs when, and only when, the
	/// manager is dirty -- that is, when a pipe or a port was built, deconstructed or
	/// disconnected -- and it publishes that moment through
	/// <c>AddNetworksRebuiltListener</c>. So the partition is available, correct by the game's own
	/// definition, and free; what was missing was somebody holding onto it.
	///
	/// WHAT THIS FIXES BESIDES ALLOCATION. Two real behavioural bugs came with the flood fill:
	///
	///  * It capped at <c>MaxPipeNetworkTiles</c> (500) and returned exactly that many cells for a
	///    larger run, so a 600-tile network reported the mass, volume and therefore the PRESSURE of
	///    an arbitrary 500-tile subset of itself. There is no cap here -- a network is its cells.
	///  * Two components reading the same run in the same tick each walked it. Nine per-tick
	///    components on one loop walked it nine times.
	///
	/// WHAT IT DELIBERATELY DOES NOT CHANGE. The partition is the game's, so the Gas Filter case
	/// still holds and holds for the same reason: the
	/// filter's input, output and secondary output sit in three mutually adjacent cells and are
	/// three separate networks because ONI's rebuild says so. Bridges likewise: a
	/// <c>ConduitBridge</c> is a source/sink pair, not a link -- <c>UtilityNetworkManager.AddLink</c>
	/// has callers only in the logic, travel-tube and wire systems, none in gas or liquid -- so a
	/// bridge ends one network and starts another here exactly as it did under the flood fill.
	///
	/// A BROKEN CONDUIT IS NOT IN ITS RUN, AND THAT IS A REAL BEHAVIOURAL DIFFERENCE FROM THE
	/// FLOOD FILL. <c>UtilityNetworkManager.RebuildNetworks</c> skips any item that is
	/// <c>IDisconnectable</c> and reports <c>IsDisconnected()</c> -- which a broken
	/// <c>BuildingHP</c> conduit does -- BEFORE stamping its network id, and never traverses
	/// through it. So a break SPLITS a run into two smaller runs with the broken tile in neither.
	/// The flood fill did the opposite: the broken pipe still has a GameObject on the layer and
	/// still has its connection bits, so it stayed a member.
	///
	/// Both answers are defensible and ONI's is the right one for the question most callers ask
	/// -- you cannot push gas into a broken pipe, so a discharge loop should not offer it one.
	/// But it silently broke one caller that was relying on the other answer, and the failure is
	/// worth recording because it is the shape this class will break again:
	/// <c>ConduitFreezePatch.DamageWeakestMember</c> implemented "a run with a hole in it takes no
	/// further damage" by looking for a broken member INSIDE the run. Under this partition the
	/// hole is never inside the run -- it is the boundary the run now ends at -- so the check
	/// stopped firing and a freezing pipe destroyed all twelve of its segments instead of one
	/// (caught by the pipe-stress rig's freezing phases).
	/// That caller now looks at the boundary as well. ANY CALLER THAT INFERS SOMETHING FROM A
	/// TILE'S ABSENCE, rather than from the tiles present, has to be read with this in mind.
	///
	/// THE ONE CASE THAT STILL NEEDS THE FLOOD FILL. <c>AddToNetworks</c> only sets
	/// <c>dirty</c>; the rebuild happens later in <c>UtilityNetworkManager.Update</c>. Between a
	/// pipe being placed and that update running, the new tile has a conduit object but no network
	/// stamp. <see cref="TryGetCells"/> reports that as a miss so the caller can fall back, rather
	/// than reporting an empty network, because "not partitioned yet" and "empty run" are different
	/// claims and only one of them is worth acting on.
	/// </summary>
	public static class ConduitNetworks
	{
		/// <summary>
		/// One conduit system's cached partition. Gas and liquid are separate
		/// <c>UtilityNetworkManager</c> instances with separate object layers and separate rebuild
		/// events, so they are cached separately and never share an id space.
		/// </summary>
		private sealed class LayerCache
		{
			public readonly PipeContentType ContentType;
			public readonly int ObjectLayerIdx;

			/// <summary>The manager instance this cache is subscribed to. Compared by REFERENCE:
			/// a new game or a load builds a fresh <c>Game.Instance</c> with fresh conduit
			/// systems, and the old cache describes a world that no longer exists.
			///
			/// Held as the CONCRETE type rather than as <c>IUtilityNetworkMgr</c>, and only for
			/// one reason: <c>IsDirty</c> is public on the class and absent from the interface,
			/// and it is the game telling us in one bool that this partition is out of date. See
			/// <see cref="EnsureSubscribed"/>.</summary>
			public UtilityNetworkManager<FlowUtilityNetwork, Vent> Manager;

			public Action<IList<UtilityNetwork>, ICollection<int>> Listener;

			/// <summary>Indexed by <c>UtilityNetwork.id</c>. An entry is null for a network id
			/// that holds no conduit tile of this layer -- the id space is shared across the
			/// manager's visual and physical passes and across both endpoint-only and conduit
			/// networks, so gaps are normal rather than a fault.</summary>
			public int[][] CellsByNetwork = EmptyPartition;

			/// <summary>Bumped on every rebuild. A caller that memoises anything derived from a
			/// network's cell set can compare this instead of re-reading the set.</summary>
			public int Version;

			/// <summary>False until the first build, so a miss can be distinguished from an
			/// answered "this cell is in no network".</summary>
			public bool Built;

			public LayerCache(PipeContentType contentType, int objectLayerIdx)
			{
				ContentType = contentType;
				ObjectLayerIdx = objectLayerIdx;
			}
		}

		private static readonly int[][] EmptyPartition = new int[0][];

		private static readonly LayerCache Gas =
			new LayerCache(PipeContentType.Gas, (int)ObjectLayer.GasConduit);

		private static readonly LayerCache Liquid =
			new LayerCache(PipeContentType.Liquid, (int)ObjectLayer.LiquidConduit);

		/// <summary>Scratch for the two-pass rebuild, reused across rebuilds so a topology change
		/// does not itself become an allocation event on a colony that is being actively built.
		/// Single-threaded by construction: the rebuild listener fires on the game thread from
		/// <c>UtilityNetworkManager.Update</c>.</summary>
		private static readonly List<int> RebuildCounts = new List<int>();
		private static readonly List<int> RebuildFill = new List<int>();

		/// <summary>
		/// Whether the game has conduit systems at all. False in the main menu, during load and
		/// after a world is torn down.
		/// </summary>
		public static bool Available
		{
			get
			{
				Game game = Game.Instance;
				return game != null && game.gasConduitSystem != null
					&& game.liquidConduitSystem != null;
			}
		}

		/// <summary>
		/// How many times this layer's partition has been rebuilt. Bumps on every topology change
		/// and on a world swap; never bumps because contents moved.
		/// </summary>
		public static int Version(PipeContentType contentType)
		{
			LayerCache cache = CacheFor(contentType);
			EnsureSubscribed(cache);
			return cache.Version;
		}

		/// <summary>
		/// The id of the network the conduit at <paramref name="cell"/> belongs to, or -1 when
		/// there is no conduit of that type there, or when the topology has changed and the game
		/// has not rebuilt yet.
		///
		/// Ids are the game's <c>UtilityNetwork.id</c> and are STABLE ONLY WITHIN ONE
		/// <see cref="Version"/> -- a rebuild renumbers from zero.
		/// </summary>
		public static int NetworkIdOf(int cell, PipeContentType contentType)
		{
			LayerCache cache = CacheFor(contentType);
			if (!EnsureSubscribed(cache) || !HasConduitAt(cache, cell))
			{
				return -1;
			}
			UtilityNetwork network = cache.Manager.GetNetworkForCell(cell);
			return network != null ? network.id : -1;
		}

		/// <summary>
		/// The conduit cells of the whole run containing <paramref name="cell"/>.
		///
		/// THE RETURNED ARRAY IS THE CACHE'S OWN AND MUST NOT BE WRITTEN TO. It is handed out by
		/// reference on purpose -- copying it per call is the allocation this whole class exists
		/// to remove -- and it stays valid until the next topology change. A caller that needs to
		/// keep it across a build or a deconstruct should copy it or re-read after checking
		/// <see cref="Version"/>.
		///
		/// Returns false when there is no conduit of that type at the cell, and ALSO when the cell
		/// has a conduit the game has not partitioned yet (see this class's doc comment). Both are
		/// "ask somebody else", which is what a caller can act on; an empty run answers true with
		/// a zero-length array instead, because a real run that happens to hold nothing is a
		/// different fact.
		/// </summary>
		public static bool TryGetCells(int cell, PipeContentType contentType, out int[] cells)
		{
			cells = null;
			int id = NetworkIdOf(cell, contentType);
			if (id < 0)
			{
				return false;
			}
			LayerCache cache = CacheFor(contentType);
			if (!cache.Built || id >= cache.CellsByNetwork.Length)
			{
				return false;
			}
			int[] found = cache.CellsByNetwork[id];
			if (found == null)
			{
				return false;
			}
			cells = found;
			return true;
		}

		/// <summary>
		/// Drop both caches and unsubscribe. Called when a world goes away; also safe to call at
		/// any time, since the next read resubscribes and rebuilds.
		/// </summary>
		public static void Invalidate()
		{
			Detach(Gas);
			Detach(Liquid);
		}

		private static LayerCache CacheFor(PipeContentType contentType)
		{
			return contentType == PipeContentType.Liquid ? Liquid : Gas;
		}

		private static bool HasConduitAt(LayerCache cache, int cell)
		{
			return Grid.IsValidCell(cell)
				&& Grid.Objects[cell, cache.ObjectLayerIdx] != null;
		}

		/// <summary>
		/// Make sure this cache is attached to the CURRENT manager and holds a built partition.
		/// Cheap in the steady state: one reference compare and one bool.
		/// </summary>
		private static bool EnsureSubscribed(LayerCache cache)
		{
			Game game = Game.Instance;
			UtilityNetworkManager<FlowUtilityNetwork, Vent> manager = game == null
				? null
				: (cache.ContentType == PipeContentType.Liquid
					? game.liquidConduitSystem
					: game.gasConduitSystem);

			if (manager == null)
			{
				Detach(cache);
				return false;
			}

			if (!ReferenceEquals(manager, cache.Manager))
			{
				Detach(cache);
				cache.Manager = manager;
				// Captured once and kept, because RemoveNetworksRebuiltListener compares
				// delegates and a freshly-made one from the same method would not match.
				LayerCache captured = cache;
				cache.Listener = (networks, rootNodes) => Rebuild(captured, rootNodes);
				manager.AddNetworksRebuiltListener(cache.Listener);
			}

			if (!cache.Built)
			{
				// No root-node set to work from on the first attach -- the game only hands one
				// over with the rebuild event -- so this one build scans the grid. It is the only
				// full scan; every later rebuild walks the nodes the game names.
				Rebuild(cache, null);
			}

			// THE STALENESS GUARD, and it is not paranoia. `AddToNetworks`, `ClearCell` and
			// `ForceRebuildNetworks` all do nothing but set `dirty`; the partition is not
			// recomputed until `UtilityNetworkManager.Update` runs. In that window the stamped
			// `networkIdx` still describes the world as it was, so a pipe placed or DESTROYED
			// this frame is described wrongly -- and "destroyed" is the dangerous half, because a
			// caller would then walk cells that no longer hold a conduit. Refusing while dirty
			// sends the caller to the flood fill, which reads `Grid.Objects` live and is always
			// current. The window is at most one frame, and in the steady state this is one bool.
			return cache.Built && !manager.IsDirty;
		}

		private static void Detach(LayerCache cache)
		{
			if (cache.Manager != null && cache.Listener != null)
			{
				cache.Manager.RemoveNetworksRebuiltListener(cache.Listener);
			}
			cache.Manager = null;
			cache.Listener = null;
			cache.CellsByNetwork = EmptyPartition;
			cache.Built = false;
			cache.Version++;
		}

		/// <summary>
		/// Rebuild one layer's partition. Two passes over the candidate cells -- count per network,
		/// then fill -- so every network's cell array is allocated exactly once at its exact size.
		///
		/// <paramref name="rootNodes"/> is the game's own physical-node set for this manager,
		/// handed to the rebuild listener. It is a superset of the conduit tiles (it also holds
		/// building port cells), which is why every candidate is filtered through the object layer.
		/// Null means "no node set available", which only happens on the first attach, and falls
		/// back to a whole-grid scan.
		/// </summary>
		private static void Rebuild(LayerCache cache, ICollection<int> rootNodes)
		{
			cache.Built = false;
			cache.CellsByNetwork = EmptyPartition;
			cache.Version++;

			UtilityNetworkManager<FlowUtilityNetwork, Vent> manager = cache.Manager;
			if (manager == null || Grid.CellCount <= 0)
			{
				return;
			}

			IList<UtilityNetwork> networks = manager.GetNetworks();
			int networkCount = networks != null ? networks.Count : 0;
			if (networkCount <= 0)
			{
				// A colony with no conduits at all is a built, empty partition -- not a miss.
				cache.CellsByNetwork = EmptyPartition;
				cache.Built = true;
				return;
			}

			RebuildCounts.Clear();
			for (int i = 0; i < networkCount; i++)
			{
				RebuildCounts.Add(0);
			}

			if (rootNodes != null)
			{
				foreach (int cell in rootNodes)
				{
					int id = IdOfCandidate(cache, manager, cell, networkCount);
					if (id >= 0)
					{
						RebuildCounts[id] = RebuildCounts[id] + 1;
					}
				}
			}
			else
			{
				int cellCount = Grid.CellCount;
				for (int cell = 0; cell < cellCount; cell++)
				{
					int id = IdOfCandidate(cache, manager, cell, networkCount);
					if (id >= 0)
					{
						RebuildCounts[id] = RebuildCounts[id] + 1;
					}
				}
			}

			var partition = new int[networkCount][];
			RebuildFill.Clear();
			for (int i = 0; i < networkCount; i++)
			{
				int count = RebuildCounts[i];
				partition[i] = count > 0 ? new int[count] : null;
				RebuildFill.Add(0);
			}

			if (rootNodes != null)
			{
				foreach (int cell in rootNodes)
				{
					PlaceCandidate(cache, manager, cell, networkCount, partition);
				}
			}
			else
			{
				int cellCount = Grid.CellCount;
				for (int cell = 0; cell < cellCount; cell++)
				{
					PlaceCandidate(cache, manager, cell, networkCount, partition);
				}
			}

			// Shrink any array the fill pass did not finish. It can only happen if the grid
			// changed between the two passes, and a trailing unwritten slot would read as cell 0 --
			// a perfectly valid cell index, and therefore a silently wrong member rather than an
			// obvious one.
			for (int i = 0; i < networkCount; i++)
			{
				int[] target = partition[i];
				if (target == null)
				{
					continue;
				}
				int filled = RebuildFill[i];
				if (filled == 0)
				{
					partition[i] = null;
				}
				else if (filled < target.Length)
				{
					var trimmed = new int[filled];
					Array.Copy(target, trimmed, filled);
					partition[i] = trimmed;
					target = trimmed;
				}

				// ASCENDING CELL ORDER, and it is a deliberate choice rather than a tidy-up.
				// The flood fill this replaces returned BFS order from the queried cell, so the
				// same run came back in a different order depending on who asked, and several
				// callers walk the array backwards to reach "the far end". Neither property
				// survives a shared cached array: there is one array per network, not one per
				// asker. Ascending cell index is the one order that is the same for every caller,
				// the same on every rebuild, and the same across a save and reload -- which is
				// what makes a rig reproducible. Callers that walk backwards now reach the
				// highest-indexed tile rather than the BFS-farthest one; none of them depend on
				// distance, they depend on "not the port cell", and they all say so explicitly.
				if (target != null)
				{
					Array.Sort(target);
				}
			}

			cache.CellsByNetwork = partition;
			cache.Built = true;
		}

		private static int IdOfCandidate(LayerCache cache,
			UtilityNetworkManager<FlowUtilityNetwork, Vent> manager, int cell, int networkCount)
		{
			if (!HasConduitAt(cache, cell))
			{
				return -1;
			}
			UtilityNetwork network = manager.GetNetworkForCell(cell);
			if (network == null)
			{
				return -1;
			}
			int id = network.id;
			return id >= 0 && id < networkCount ? id : -1;
		}

		private static void PlaceCandidate(LayerCache cache,
			UtilityNetworkManager<FlowUtilityNetwork, Vent> manager, int cell, int networkCount,
			int[][] partition)
		{
			int id = IdOfCandidate(cache, manager, cell, networkCount);
			if (id < 0)
			{
				return;
			}
			int[] target = partition[id];
			if (target == null)
			{
				return;
			}
			int at = RebuildFill[id];
			if (at >= target.Length)
			{
				// Counted and filled from the same predicate over the same set, so this cannot
				// happen without the grid changing underneath the two passes. Dropping the cell is
				// the safe half of that: an under-full array would report a shorter run, which the
				// caller can survive, where a write past the end could not.
				return;
			}
			target[at] = cell;
			RebuildFill[id] = at + 1;
		}
	}
}
