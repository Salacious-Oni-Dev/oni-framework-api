using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace OniFramework
{
	/// <summary>
	/// What the sim does to a conduit run of its own accord. Set per cell with
	/// <see cref="SimConduitNetworks.SetPolicy"/>; the sim applies it to the whole run that cell
	/// belongs to. Values are <c>ONI_CONDUIT_POLICY_*</c> in <c>abi/sim_ext_api.h</c>.
	/// </summary>
	[Flags]
	public enum ConduitNetworkPolicy
	{
		/// <summary>Klei's arithmetic only: every conduit exchanges with its own building and
		/// carries its own temperature.</summary>
		None = 0,

		/// <summary>
		/// Axial heat exchange along the run. After each 200 ms update's conduit-to-building
		/// exchange, every two conduits piped to each other that both hold something exchange
		/// <c>q = k (T_i - T_j) C_i C_j / (C_i + C_j)</c>, where <c>k</c> is the policy's mix
		/// fraction, in (0, <see cref="SimConduitNetworks.MaxMixFraction"/>]: the share of a pair's
		/// temperature gap one link closes per update. The run's contents energy is conserved, no
		/// energy crosses to or from any building, and at k &lt;= 0.25 every mixed temperature is
		/// a weighted average of the temperatures around it, so nothing overshoots. Packets still
		/// move under the game's own <c>ConduitFlow</c>, carrying their temperature with them.
		/// Trapped matter is not mixed: the side-car's temperatures are managed.
		///
		/// <b>Deliberately not Stationeers' one temperature per network.</b> Stationeers can level
		/// a whole network because the devices in a refrigeration loop split its hot and cold
		/// sides into separate networks. ONI splits runs at pumps and valves too, but a run is free
		/// to cross rooms with nothing in between, and Mod 1's own PHASELOOP liquid run does -- its
		/// sub-cool leg sheds the liquid's heat in the hot room before the same run crosses the
		/// cold one, and levelling the run whole would carry that heat straight into the cold room
		/// (<c>sim/conduits.h</c>, <c>MixNetworks</c>, has the measurement). Exchange between
		/// neighbours keeps a gradient along a run and still levels a run left alone.
		/// </summary>
		Mix = 0x1,

		/// <summary>
		/// Pressure-driven phase change, decided by the sim once per 200 ms update: gas in a gas
		/// run condensing when the run's pressure puts its dew point above it, liquid in a liquid
		/// run boiling under its headspace pressure, and headspace gas condensing back -- the
		/// decisions <see cref="PipeMatterFacade.TickNetworkPhaseChange"/> made from outside,
		/// with the same curve (<c>sim.phase_curve</c>, pushed by
		/// <see cref="MaterialPropertyRegistry.PushPhaseCurvesToSim"/>) and the same native step.
		/// The sim only decides; the mass is ConduitFlow's and the side-car's, so
		/// <see cref="SimConduitNetworks.Install"/>'s patch carries each decision out after every
		/// update. Without <c>Install</c> nothing applies them, and
		/// <see cref="PipeMatterFacade.TickNetworkPhaseChange"/> keeps deciding for itself.
		/// </summary>
		Phase = 0x2,

		/// <summary>
		/// Stationeers' pipe-to-world convection shape on Klei's own coefficient. Each conduit's
		/// 200 ms exchange with its building -- Klei's conductivity mean (the minimum when
		/// insulated) times the gap, a contact area and a rate, so pipe material still matters --
		/// is multiplied by two <see cref="PipeHeatExchange.HeatExchangeRatio"/>s, the way
		/// Stationeers' <c>AtmosphereHelper.CalculateConvection</c> scales a pipe's exchange with
		/// the world: the RUN's (its gas pressure over one atmosphere, or its liquid volume over a
		/// thousandth of its volume, whichever is larger) and the conduit's own CELL's (the same
		/// ratio of the cell the frame holds; vacuum 0). So a pipe in vacuum stops exchanging, a
		/// low-pressure gas run exchanges in proportion to its pressure, and any real liquid on
		/// either side is full strength.
		///
		/// One departure, on purpose: a pipe inside a SOLID tile counts as full contact, where
		/// Stationeers answers 0 for a grid that cannot hold atmosphere. A Stationeers pipe cannot
		/// run through a wall; an ONI pipe can, and there it touches matter, which ONI's
		/// conduction already owns. Takes no parameters. A SimDLL without
		/// convection refuses the flag, and <see cref="SimConduitNetworks"/> then pushes the rest
		/// of the run's policy without it and says so once.
		/// </summary>
		Convection = 0x4,
	}

	/// <summary>
	/// A WHOLE CONDUIT RUN, AS THE SIM SEES IT. The managed half of <c>SIM_ConduitNetworkBind</c>,
	/// <c>SIM_ConduitNetworkPolicy</c> and <c>SIM_ConduitNetworkAggregate</c>
	/// (<c>abi/sim_ext_api.h</c>, <c>sim/conduits.h</c>).
	///
	/// <b>What the sim already had, and could not use.</b> The game hands Klei's conduit
	/// temperature manager every conduit's temperature, mass and element on every conduit frame.
	/// Klei's manager kept only <c>mass * specificHeat</c> and never learned where a conduit was or
	/// which run it belonged to -- <c>ConduitTemperatureManager.Allocate</c> files the conduit's
	/// type and index on the managed side only. So the one native object that sees every pipe's
	/// contents could not name them, and could not treat a run as a run. Now it keeps the mass and
	/// the element, and this class tells it which run each conduit is in.
	///
	/// <b>The partition is the game's own, pushed rather than re-derived.</b>
	/// <c>UtilityNetworkManager</c> already computes each conduit layer's connected components
	/// (<see cref="ConduitNetworks"/> is built on the same fact), and <c>ConduitFlow</c> already
	/// keeps each conduit's four neighbours (<c>SOAInfo.GetConduitConnections</c>), so native
	/// needs no union-find, no grid walk and no bridge semantics. A broken conduit is in NO run -- the game drops it from
	/// the partition, and <c>ConduitFlow.RebuildConnections</c> does not even give it a temperature
	/// handle -- as <see cref="ConduitNetworks"/>' own doc explains.
	///
	/// <b>Why the bind is repeated on every rebuild.</b> <c>ConduitFlow.RebuildConnections</c>
	/// frees EVERY temperature handle of its conduit type and allocates new ones, one per connected
	/// conduit, on every topology change. A binding made once at allocation would be wrong after
	/// the first pipe placed anywhere. So this subscribes to <c>ConduitFlow.onConduitsRebuilt</c>
	/// -- the public event the game raises the moment those handles exist -- and pushes the whole
	/// layer's <c>handle -> cell + network + neighbours</c> in one call each time. No Harmony patch
	/// is involved: the event, the <c>soaInfo</c> field, <c>SOAInfo.GetCell</c> and
	/// <c>SOAInfo.GetConduitConnections</c> are public, and the one
	/// private member read (<c>SOAInfo.temperatureHandles</c>) is read through a cached
	/// <see cref="AccessTools.FieldRef{T, F}"/>.
	///
	/// <b>Opt-in, like everything else in this framework.</b> Nothing binds until a caller asks:
	/// the first read, <see cref="SetPolicy"/> or <see cref="EnsureBound"/> subscribes, binds the
	/// layer as it stands, and from then on the game's own event keeps it current -- across loads
	/// too, because a new <c>Game</c> has new <c>ConduitFlow</c>s and the next call notices.
	///
	/// <b>A run's contents are ConduitFlow's, as of the last conduit frame.</b> The game publishes
	/// contents to the sim at the end of each conduit frame, so mass and composition lag the
	/// game's grid by at most one frame; temperature is the sim's own and is as fresh as the last
	/// 200 ms update. Standing matter the conduit cannot represent -- <see cref="PipeMatterFacade"/>'s
	/// condensate and headspace gas -- is mirrored into the sim tile by tile from that class's own
	/// mutation choke point and reported in the <c>Trapped*</c> fields, apart from the contents.
	///
	/// <b>Refusal is not emptiness.</b> Like <see cref="ConduitNetworks"/>, a read refuses while
	/// the game has a topology change it has not partitioned yet, and when this DLL cannot answer
	/// at all; a real run holding nothing answers true with zero mass.
	///
	/// <b>Its own latch.</b> These exports are newer than the gas-mixture and room exports, so a
	/// real install can have those and not these. On a stock or older custom
	/// SimDLL the first call raises <see cref="EntryPointNotFoundException"/>; this class says so
	/// once, and every call afterwards answers "unavailable" without calling again.
	///
	/// <b>No allocation on the read path.</b> The aggregate is a struct with fixed buffers filled
	/// from a stack buffer. The bind reuses four arrays that only ever grow.
	/// </summary>
	public static class SimConduitNetworks
	{
		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ConduitNetworkBind(int conduitType, int* handles,
			int* cells, int* networkIds, int* neighbourHandles, int count, float volumePerConduitM3,
			float maxMassPerConduitKg);

		[DllImport("SimDLL")]
		private static extern int SIM_ConduitNetworkPolicy(int conduitType, int networkId,
			int flags, float mixFraction, float phaseRatePerSecond, float phaseMinRemainderKg);

		[DllImport("SimDLL")]
		private static extern int SIM_ConduitTrappedSet(int conduitType, int cell, int elementIdx,
			float massKg, float temperatureK);

		[DllImport("SimDLL")]
		private static extern int SIM_ConduitTrappedClear(int conduitType);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ConduitPhaseProposals(byte* outProposals, int capacity);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ConduitNetworkAggregate(int conduitType,
			int networkId, byte* outAggregate, int outSize);

		// ------------------------------------------------------------------ layout
		//
		// DECODED BY FIELD OFFSET, NOT BY [StructLayout] MARSHALLING, for the reason SimRooms
		// gives: a marshalled struct that drifts reads the wrong four bytes silently, an offset
		// table that drifts fails a live-gate arm naming the field. OniConduitNetworkAggregate in
		// abi/sim_ext_api.h; its static_assert pins 140 bytes, and sim_abi_ext.h pins the array
		// and tail offsets. OniConduitPhaseProposal likewise, at 44.

		private const int AggregateBytes = 140;
		private const int OffsetNetworkId = 0;
		private const int OffsetConduitType = 4;
		private const int OffsetGeneration = 8;
		private const int OffsetPolicyFlags = 12;
		private const int OffsetConduitCount = 16;
		private const int OffsetFilledCount = 20;
		private const int OffsetSpeciesCount = 24;
		private const int OffsetSpeciesOverflow = 28;
		private const int OffsetMixFraction = 32;
		private const int OffsetVolumeM3 = 36;
		private const int OffsetTotalMassKg = 40;
		private const int OffsetTotalMoles = 44;
		private const int OffsetHeatCapacity = 48;
		private const int OffsetTemperatureK = 52;
		private const int OffsetMassWeightedTemperatureK = 56;
		private const int OffsetPressurePa = 60;
		private const int OffsetSpecies = 64;
		private const int OffsetMassBySpecies = 80;
		private const int OffsetTrappedGasKg = 112;
		private const int OffsetTrappedLiquidKg = 116;
		private const int OffsetTrappedSolidKg = 120;
		private const int OffsetLiquidVolumeM3 = 124;
		private const int OffsetHeadspacePressurePa = 128;
		private const int OffsetPhaseRatePerSecond = 132;
		private const int OffsetLinkCount = 136;

		private const int ProposalBytes = 44;
		private const int ProposalOffsetConduitType = 0;
		private const int ProposalOffsetNetworkId = 4;
		private const int ProposalOffsetCell = 8;
		private const int ProposalOffsetKind = 12;
		private const int ProposalOffsetSourceElement = 16;
		private const int ProposalOffsetProductElement = 18;
		private const int ProposalOffsetSourceMassKg = 20;
		private const int ProposalOffsetSourceTemperatureK = 24;
		private const int ProposalOffsetConvertedMassKg = 28;
		private const int ProposalOffsetBoundaryK = 32;
		private const int ProposalOffsetPressurePa = 36;
		private const int ProposalOffsetLatentHeat = 40;

		/// <summary>Composition slots a run's aggregate carries -- <c>ONI_CONDUIT_NET_MAX_SPECIES</c>.
		/// A vanilla conduit holds one element at a time, so only a hand-built mixture fills them.</summary>
		public const int MaxSpecies = 8;

		/// <summary>What a refused read reports as its network id.</summary>
		public const int NoNetwork = -1;

		/// <summary>
		/// The largest <see cref="ConduitNetworkPolicy.Mix"/> fraction the sim accepts
		/// (<c>ONI_CONDUIT_MIX_FRACTION_MAX</c>). A conduit has at most four links, so four times
		/// this must not exceed one for a mixed temperature to stay between its neighbours'.
		/// </summary>
		public const float MaxMixFraction = 0.25f;

		/// <summary>Neighbour handles per conduit in a bind (<c>ONI_CONDUIT_NEIGHBOUR_SLOTS</c>).</summary>
		private const int NeighbourSlots = 4;

		// The game's ConduitType numbering, which is what the native side speaks.
		private const int NativeGas = 1;
		private const int NativeLiquid = 2;

		private static int NativeType(PipeContentType contentType)
		{
			return contentType == PipeContentType.Liquid ? NativeLiquid : NativeGas;
		}

		// ------------------------------------------------------------------ availability

		private static bool unavailable;
		private static bool warned;

		/// <summary>
		/// Whether this SimDLL carries the conduit-run exports. True until a call has actually
		/// failed; an export cannot be probed for without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		private static void MarkUnavailable(string export)
		{
			unavailable = true;
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL does not export " + export
					+ " -- conduit runs are not readable or mixable from this DLL (stock SimDLL, or a "
					+ "custom build too old to have them). Every conduit-run read answers 'unavailable' "
					+ "from here on and every policy is refused; nothing will throw.");
			}
		}

		private static void ReportMissingLibrary(DllNotFoundException e)
		{
			// Not latched: a DLL that could not be found may be found on a later call. Same
			// reasoning as SimRooms.ReportMissingLibrary.
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL could not be loaded while binding conduit runs: "
					+ e.Message + ". Not latching -- a later call may still succeed.");
			}
		}

		// ------------------------------------------------------------------ the aggregate

		/// <summary>
		/// One conduit run, summed by the sim. A value type with fixed buffers, so obtaining one
		/// allocates nothing.
		/// </summary>
		public unsafe struct ConduitNetworkAggregate
		{
			/// <summary>Which layer the run is on.</summary>
			public PipeContentType ContentType;

			/// <summary>
			/// <c>UtilityNetwork.id</c> -- the same number <see cref="ConduitNetworks.NetworkIdOf"/>
			/// reports -- or <see cref="NoNetwork"/>. Meaningful only with <see cref="Generation"/>.
			/// </summary>
			public int NetworkId;

			/// <summary>
			/// Binds of this layer so far. Ids are re-assigned on every topology change; two answers
			/// with different generations are about different numberings.
			/// </summary>
			public int Generation;

			/// <summary>The policy the sim currently has in force on this run.</summary>
			public ConduitNetworkPolicy Policy;

			/// <summary>Conduits in the run -- every one the game gave a temperature handle.</summary>
			public int ConduitCount;

			/// <summary>Of those, conduits holding any mass.</summary>
			public int FilledCount;

			/// <summary>Entries filled in the composition -- at most <see cref="MaxSpecies"/>.</summary>
			public int SpeciesCount;

			/// <summary>Distinct species with no slot. Their mass is still in <see cref="TotalMassKg"/>.</summary>
			public int SpeciesOverflow;

			/// <summary>The <see cref="ConduitNetworkPolicy.Mix"/> coupling, 0 without it.</summary>
			public float MixFraction;

			/// <summary><see cref="ConduitCount"/> times the per-conduit volume the bind declared --
			/// <see cref="GasMixtureFacade.GasConduitVolumeM3"/> or
			/// <see cref="GasMixtureFacade.LiquidConduitVolumeM3"/>.</summary>
			public float VolumeM3;

			/// <summary>Mass of everything in the run's conduits.</summary>
			public float TotalMassKg;

			/// <summary>Moles, through the sim's <c>sim.molecular_mass</c>-corrected table.</summary>
			public float TotalMoles;

			/// <summary>Sum of every conduit's contents heat capacity, kJ/K.</summary>
			public float HeatCapacityKJPerK;

			/// <summary>
			/// Heat-capacity-weighted mean -- the temperature the contents would settle at if mixed,
			/// and the one <see cref="PressurePa"/> is computed at. 0 for an empty run.
			/// </summary>
			public float TemperatureK;

			/// <summary>
			/// Mass-weighted mean, which is what <see cref="PipeNetworkFacade.TryReadNetwork"/>
			/// reports. Equal to <see cref="TemperatureK"/> for a run of one element.
			/// </summary>
			public float MassWeightedTemperatureK;

			/// <summary>
			/// A gas run's ideal-gas pressure over <see cref="VolumeM3"/>, through the same native
			/// formula <see cref="GasMixtureFacade.TryComputePressure"/> calls. 0 for a liquid run:
			/// its pressure is its headspace, which is gas the conduit cannot hold.
			/// </summary>
			public float PressurePa;

			/// <summary>Standing gas in the run's conduits, from the side-car mirror.</summary>
			public float TrappedGasKg;

			/// <summary>Standing liquid -- condensate, in a gas run.</summary>
			public float TrappedLiquidKg;

			/// <summary>Standing solid -- what froze out.</summary>
			public float TrappedSolidKg;

			/// <summary>
			/// Liquid volume in the run: conduit liquid (water-dense when its material has no
			/// density) plus trapped liquid (not counted when its material has none) -- the two
			/// rules <see cref="PipeNetworkFacade"/> and <see cref="PipeMatterFacade"/> use.
			/// </summary>
			public float LiquidVolumeM3;

			/// <summary>
			/// A liquid run's headspace pressure: the trapped gas over what the liquid leaves of
			/// <see cref="VolumeM3"/>. 0 with no trapped gas or no room -- the same answer
			/// <see cref="PipeMatterFacade.TryGetLiquidHeadspacePressurePa(PipeNetworkReading, out float)"/>
			/// gives. 0 for a gas run.
			/// </summary>
			public float HeadspacePressurePa;

			/// <summary>The <see cref="ConduitNetworkPolicy.Phase"/> rate, 0 without it.</summary>
			public float PhaseRatePerSecond;

			/// <summary>
			/// Pipe-to-pipe links inside the run, each counted once -- the edges
			/// <see cref="ConduitNetworkPolicy.Mix"/> exchanges along. A run is connected, so a
			/// correct bind reports at least <see cref="ConduitCount"/> - 1, exactly that for a run
			/// with no loop; fewer means the neighbours reached the sim wrong.
			/// </summary>
			public int LinkCount;

			private fixed ushort species[MaxSpecies];
			private fixed float massBySpeciesKg[MaxSpecies];

			/// <summary>The ElementTable index in slot <paramref name="slot"/>, or 0 past
			/// <see cref="SpeciesCount"/>.</summary>
			public ushort SpeciesAt(int slot)
			{
				if (slot < 0 || slot >= SpeciesCount)
				{
					return 0;
				}
				fixed (ushort* p = species)
				{
					return p[slot];
				}
			}

			/// <summary>The run's mass of the species in slot <paramref name="slot"/>, or 0 past
			/// <see cref="SpeciesCount"/>.</summary>
			public float MassKgAt(int slot)
			{
				if (slot < 0 || slot >= SpeciesCount)
				{
					return 0f;
				}
				fixed (float* p = massBySpeciesKg)
				{
					return p[slot];
				}
			}

			internal void SetSlot(int slot, ushort speciesIdx, float massKg)
			{
				if (slot < 0 || slot >= MaxSpecies)
				{
					return;
				}
				fixed (ushort* s = species)
				{
					s[slot] = speciesIdx;
				}
				fixed (float* m = massBySpeciesKg)
				{
					m[slot] = massKg;
				}
			}

			/// <summary>The run's mass of one species by ElementTable index; false when it holds
			/// none (or, with <see cref="SpeciesOverflow"/> non-zero, found no slot for it).</summary>
			public bool TryGetMassKg(ushort speciesIdx, out float massKg)
			{
				for (int i = 0; i < SpeciesCount; i++)
				{
					if (SpeciesAt(i) == speciesIdx)
					{
						massKg = MassKgAt(i);
						return true;
					}
				}
				massKg = 0f;
				return false;
			}

			/// <summary>Everything on one line, for a log a person reads.</summary>
			public override string ToString()
			{
				if (NetworkId == NoNetwork)
				{
					return "no run";
				}
				StringBuilder sb = new StringBuilder();
				sb.Append(ContentType).Append(" run ").Append(NetworkId).Append(" (gen ")
					.Append(Generation).Append("): ").Append(ConduitCount).Append(" conduits (")
					.Append(FilledCount).Append(" filled, ").Append(LinkCount).Append(" links), ").Append(TotalMassKg.ToString("F4"))
					.Append(" kg, ").Append(TotalMoles.ToString("F3")).Append(" mol, ")
					.Append(TemperatureK.ToString("F2")).Append(" K (mass-weighted ")
					.Append(MassWeightedTemperatureK.ToString("F2")).Append(" K), ")
					.Append(PressurePa.ToString("F0")).Append(" Pa over ")
					.Append(VolumeM3.ToString("F3")).Append(" m3, policy ").Append(Policy);
				if ((Policy & ConduitNetworkPolicy.Mix) != 0)
				{
					sb.Append(" mix@").Append(MixFraction.ToString("F2"));
				}
				if ((Policy & ConduitNetworkPolicy.Phase) != 0)
				{
					sb.Append(" phase@").Append(PhaseRatePerSecond.ToString("F2")).Append("/s");
				}
				if (TrappedGasKg > 0f || TrappedLiquidKg > 0f || TrappedSolidKg > 0f)
				{
					sb.Append(", trapped gas ").Append(TrappedGasKg.ToString("F4"))
						.Append(" / liquid ").Append(TrappedLiquidKg.ToString("F4"))
						.Append(" / solid ").Append(TrappedSolidKg.ToString("F4")).Append(" kg");
				}
				if (ContentType == PipeContentType.Liquid)
				{
					sb.Append(", liquid ").Append((LiquidVolumeM3 * 1000f).ToString("F2"))
						.Append(" L, headspace ").Append(HeadspacePressurePa.ToString("F0")).Append(" Pa");
				}
				for (int i = 0; i < SpeciesCount; i++)
				{
					sb.Append(i == 0 ? " [" : ", ").Append(SpeciesAt(i)).Append('=')
						.Append(MassKgAt(i).ToString("F4"));
				}
				if (SpeciesCount > 0)
				{
					sb.Append(']');
				}
				if (SpeciesOverflow > 0)
				{
					sb.Append(" (+").Append(SpeciesOverflow).Append(" species with no slot)");
				}
				return sb.ToString();
			}
		}

		// ------------------------------------------------------------------ binding

		private sealed class LayerBinding
		{
			public PipeContentType ContentType;
			public ConduitFlow Flow;
			public System.Action Handler;
			public int[] Handles = new int[0];
			public int[] Cells = new int[0];
			public int[] NetworkIds = new int[0];
			/// <summary>Four per conduit -- left, right, up, down -- as the sim takes them.</summary>
			public int[] Neighbours = new int[0];
			public int LastPushed;
			public int LastBound = -1;
			public long Binds;
			public readonly Dictionary<int, PolicyEntry> Policies = new Dictionary<int, PolicyEntry>();
		}

		private struct PolicyEntry
		{
			public ConduitNetworkPolicy Flags;
			public float MixFraction;
			public float PhaseRatePerSecond;
			public float PhaseMinRemainderKg;
		}

		private static readonly LayerBinding Gas = new LayerBinding { ContentType = PipeContentType.Gas };
		private static readonly LayerBinding Liquid = new LayerBinding { ContentType = PipeContentType.Liquid };

		private static LayerBinding BindingFor(PipeContentType contentType)
		{
			return contentType == PipeContentType.Liquid ? Liquid : Gas;
		}

		private static AccessTools.FieldRef<ConduitFlow.SOAInfo, List<HandleVector<int>.Handle>> temperatureHandles;
		private static bool handlesUnreadable;

		private static List<HandleVector<int>.Handle> TemperatureHandlesOf(ConduitFlow.SOAInfo soa)
		{
			if (handlesUnreadable)
			{
				return null;
			}
			if (temperatureHandles == null)
			{
				try
				{
					temperatureHandles = AccessTools.FieldRefAccess<ConduitFlow.SOAInfo,
						List<HandleVector<int>.Handle>>("temperatureHandles");
				}
				catch (Exception e)
				{
					handlesUnreadable = true;
					FrameworkLog.Warn("ConduitFlow.SOAInfo.temperatureHandles could not be bound ("
						+ e.GetType().Name + ": " + e.Message + "). The game build has moved; conduit "
						+ "runs cannot be bound and every conduit-run read answers 'unavailable'. "
						+ "Re-check the member against the current game assembly.");
					return null;
				}
			}
			return temperatureHandles(soa);
		}

		/// <summary>
		/// Subscribes to both conduit layers' rebuild events and binds each layer as it stands.
		/// Idempotent and cheap once subscribed -- two reference comparisons -- so every read calls
		/// it. False when there is no game, the DLL cannot bind, or the handles cannot be read.
		/// </summary>
		public static bool EnsureBound()
		{
			bool gas = EnsureBound(Gas);
			bool liquid = EnsureBound(Liquid);
			return gas && liquid;
		}

		private static bool EnsureBound(LayerBinding b)
		{
			if (unavailable || handlesUnreadable)
			{
				return false;
			}
			Game game = Game.Instance;
			ConduitFlow flow = game == null ? null
				: (b.ContentType == PipeContentType.Liquid ? game.liquidConduitFlow : game.gasConduitFlow);
			if (flow == null)
			{
				Detach(b);
				return false;
			}
			if (ReferenceEquals(flow, b.Flow))
			{
				return b.LastBound >= 0;
			}

			Detach(b);
			b.Flow = flow;
			// Captured once and kept: an event unsubscription compares delegates, and a fresh one
			// made from the same lambda would not match.
			LayerBinding captured = b;
			b.Handler = () => Bind(captured);
			flow.onConduitsRebuilt += b.Handler;
			// A new ConduitFlow is a new Game, and a new Game cleared the sim's conduit manager --
			// the side-car mirror with it. Re-push the whole store before the first bind, so the
			// first aggregate after a load already sees what stands in the pipes.
			ResyncTrapped(b.ContentType);
			Bind(b);
			return b.LastBound >= 0;
		}

		private static void Detach(LayerBinding b)
		{
			if (b.Flow != null && b.Handler != null)
			{
				b.Flow.onConduitsRebuilt -= b.Handler;
			}
			b.Flow = null;
			b.Handler = null;
			b.LastBound = -1;
			b.LastPushed = 0;
		}

		private static UtilityNetworkManager<FlowUtilityNetwork, Vent> ManagerFor(PipeContentType contentType)
		{
			Game game = Game.Instance;
			if (game == null)
			{
				return null;
			}
			return contentType == PipeContentType.Liquid ? game.liquidConduitSystem : game.gasConduitSystem;
		}

		private static AccessTools.FieldRef<ConduitFlow, float> maxMassField;

		/// <summary>
		/// <c>ConduitFlow.MaxMass</c>, the per-conduit capacity the game was constructed with --
		/// private, so read through a cached field ref. Falls back to vanilla's liquid 10 kg / gas
		/// 1 kg if the member has moved, which is logged once.
		/// </summary>
		internal static float MaxMassOf(ConduitFlow flow)
		{
			if (maxMassField == null)
			{
				try
				{
					maxMassField = AccessTools.FieldRefAccess<ConduitFlow, float>("MaxMass");
				}
				catch (Exception e)
				{
					FrameworkLog.Warn("ConduitFlow.MaxMass could not be bound (" + e.GetType().Name
						+ "); binding conduit runs with vanilla's capacities instead.");
					return flow.conduitType == ConduitType.Liquid ? 10f : 1f;
				}
			}
			float max = maxMassField(flow);
			return max > 0f ? max : (flow.conduitType == ConduitType.Liquid ? 10f : 1f);
		}

		private static float VolumePerConduitM3(PipeContentType contentType)
		{
			return contentType == PipeContentType.Liquid
				? GasMixtureFacade.LiquidConduitVolumeM3
				: GasMixtureFacade.GasConduitVolumeM3;
		}

		/// <summary>
		/// Pushes one layer's whole partition. Runs from <c>ConduitFlow.onConduitsRebuilt</c> --
		/// inside <c>UtilityNetworkManager.Update</c>, after <c>RebuildNetworks</c> has stamped every
		/// cell's network and after <c>RebuildConnections</c> has allocated every handle -- and once
		/// when a layer is first subscribed.
		/// </summary>
		private static unsafe void Bind(LayerBinding b)
		{
			ConduitFlow flow = b.Flow;
			UtilityNetworkManager<FlowUtilityNetwork, Vent> manager = ManagerFor(b.ContentType);
			if (flow == null || manager == null)
			{
				return;
			}
			// THE FIRST BIND CAN LAND MID-CHANGE. A pipe placed since the last partition leaves the
			// manager dirty with its rebuild a frame away, and that rebuild will raise the event this
			// is subscribed to. Binding now would read a partition the game is about to replace, so
			// the first bind waits for it instead. The event path never sees a dirty manager.
			if (manager.IsDirty)
			{
				return;
			}
			ConduitFlow.SOAInfo soa = flow.soaInfo;
			List<HandleVector<int>.Handle> handles = TemperatureHandlesOf(soa);
			if (handles == null)
			{
				return;
			}
			int n = soa.NumEntries;
			if (handles.Count < n)
			{
				FrameworkLog.Warn("SimConduitNetworks: " + b.ContentType + " layer has " + n
					+ " conduits but " + handles.Count + " temperature handles; not binding.");
				return;
			}
			if (b.Handles.Length < n)
			{
				int size = Math.Max(n, b.Handles.Length * 2);
				b.Handles = new int[size];
				b.Cells = new int[size];
				b.NetworkIds = new int[size];
				b.Neighbours = new int[size * NeighbourSlots];
			}
			for (int i = 0; i < n; i++)
			{
				int cell = soa.GetCell(i);
				UtilityNetwork network = manager.GetNetworkForCell(cell);
				b.Handles[i] = handles[i].index;
				b.Cells[i] = cell;
				b.NetworkIds[i] = network != null ? network.id : NoNetwork;
				// The game's own adjacency, as conduit indices into this same SOA (-1 for none),
				// mapped through the same handle table. Built from each conduit's OWN connection
				// mask, so a link can appear from one side only; the sim counts it once either way.
				ConduitFlow.ConduitConnections links = soa.GetConduitConnections(i);
				int at = i * NeighbourSlots;
				b.Neighbours[at] = NeighbourHandle(handles, n, links.left);
				b.Neighbours[at + 1] = NeighbourHandle(handles, n, links.right);
				b.Neighbours[at + 2] = NeighbourHandle(handles, n, links.up);
				b.Neighbours[at + 3] = NeighbourHandle(handles, n, links.down);
			}

			int bound;
			try
			{
				fixed (int* h = b.Handles)
				fixed (int* c = b.Cells)
				fixed (int* id = b.NetworkIds)
				fixed (int* nb = b.Neighbours)
				{
					bound = SIM_ConduitNetworkBind(NativeType(b.ContentType), h, c, id, nb, n,
						VolumePerConduitM3(b.ContentType), MaxMassOf(flow));
				}
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ConduitNetworkBind");
				Detach(b);
				return;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return;
			}
			b.LastPushed = n;
			b.LastBound = bound;
			b.Binds++;
			ApplyAllPolicies(b, manager);
		}

		private static int NeighbourHandle(List<HandleVector<int>.Handle> handles, int count,
			int conduitIdx)
		{
			return conduitIdx >= 0 && conduitIdx < count ? handles[conduitIdx].index : -1;
		}

		/// <summary>Binds of this layer since it was subscribed -- zero means the event is not
		/// reaching this class at all.</summary>
		public static long BindCount(PipeContentType contentType)
		{
			return BindingFor(contentType).Binds;
		}

		/// <summary>Handles the last bind pushed, and how many the sim accepted. Equal unless a
		/// handle was stale or a conduit had no network; -1 bound means never bound.</summary>
		public static void LastBind(PipeContentType contentType, out int pushed, out int bound)
		{
			LayerBinding b = BindingFor(contentType);
			pushed = b.LastPushed;
			bound = b.LastBound;
		}

		// ------------------------------------------------------------------ policy

		/// <summary>
		/// Asks the sim to apply <paramref name="policy"/> to the run containing
		/// <paramref name="cell"/>, now and after every rebuild, until
		/// <see cref="ClearPolicy"/>. Keyed by cell rather than by run because run ids are
		/// re-assigned on every topology change and a cell is the one thing that survives it.
		///
		/// <b>Several cells in one run combine</b>: their flags are OR'd and the largest of each
		/// parameter wins, so two mods that each want a run mixed do not undo one another, and a
		/// run that grows to swallow a second policy cell keeps both.
		///
		/// Returns false, storing nothing, when the DLL cannot bind conduit runs, or a parameter
		/// is out of range for a flag that is set: <paramref name="mixFraction"/> in
		/// (0, <see cref="MaxMixFraction"/>] for <see cref="ConduitNetworkPolicy.Mix"/> (the default
		/// is that ceiling); <paramref name="phaseRatePerSecond"/> &gt; 0 and
		/// <paramref name="phaseMinRemainderKg"/> &gt;= 0 for <see cref="ConduitNetworkPolicy.Phase"/>
		/// (the defaults are Stationeers' 10 %/s and the managed pass's 10 g floor). Returns true
		/// when stored; it takes effect at once if the cell is in a partitioned run, otherwise at
		/// the next rebuild that puts it in one. A broken conduit is in no run. Storing the same
		/// policy again is free.
		/// </summary>
		public static bool SetPolicy(int cell, PipeContentType contentType, ConduitNetworkPolicy policy,
			float mixFraction = MaxMixFraction, float phaseRatePerSecond = 0.1f,
			float phaseMinRemainderKg = 0.01f)
		{
			if (unavailable)
			{
				return false;
			}
			bool mix = (policy & ConduitNetworkPolicy.Mix) != 0;
			bool phase = (policy & ConduitNetworkPolicy.Phase) != 0;
			if (mix && !(mixFraction > 0f && mixFraction <= MaxMixFraction))
			{
				return false;
			}
			if (phase && (!(phaseRatePerSecond > 0f) || float.IsInfinity(phaseRatePerSecond)
				|| !(phaseMinRemainderKg >= 0f) || float.IsInfinity(phaseMinRemainderKg)))
			{
				return false;
			}
			LayerBinding b = BindingFor(contentType);
			PolicyEntry entry = new PolicyEntry
			{
				Flags = policy,
				MixFraction = mix ? mixFraction : 0f,
				PhaseRatePerSecond = phase ? phaseRatePerSecond : 0f,
				PhaseMinRemainderKg = phase ? phaseMinRemainderKg : 0f,
			};
			PolicyEntry existing;
			if (b.Policies.TryGetValue(cell, out existing) && existing.Flags == entry.Flags
				&& existing.MixFraction == entry.MixFraction
				&& existing.PhaseRatePerSecond == entry.PhaseRatePerSecond
				&& existing.PhaseMinRemainderKg == entry.PhaseMinRemainderKg
				&& EnsureBound(b))
			{
				// Already stored and already pushed -- every bind re-applies the whole table, so an
				// unchanged entry has nothing left to do. This is the path a caller that re-asserts
				// its policy every tick takes.
				return true;
			}
			b.Policies[cell] = entry;
			if (EnsureBound(b))
			{
				ApplyPolicyForCell(b, cell);
			}
			return !unavailable;
		}

		/// <summary>Withdraws the policy stored for <paramref name="cell"/>. The run keeps
		/// whatever other cells in it still ask for.</summary>
		public static void ClearPolicy(int cell, PipeContentType contentType)
		{
			LayerBinding b = BindingFor(contentType);
			if (!b.Policies.Remove(cell))
			{
				return;
			}
			if (!unavailable && EnsureBound(b))
			{
				ApplyPolicyForCell(b, cell);
			}
		}

		/// <summary>The policy stored for this exact cell (not the combined policy of its run --
		/// read the aggregate for that).</summary>
		public static ConduitNetworkPolicy PolicyStoredAt(int cell, PipeContentType contentType)
		{
			PolicyEntry entry;
			return BindingFor(contentType).Policies.TryGetValue(cell, out entry)
				? entry.Flags : ConduitNetworkPolicy.None;
		}

		/// <summary>Cells with a stored policy on this layer.</summary>
		public static int PolicyCellCount(PipeContentType contentType)
		{
			return BindingFor(contentType).Policies.Count;
		}

		/// <summary>Policy pushes the sim refused since load -- non-zero means a run id this class
		/// computed was not in the sim's bind, which should not happen.</summary>
		public static long PolicyRefusals { get; private set; }

		private static readonly Dictionary<int, PolicyEntry> CombinedScratch = new Dictionary<int, PolicyEntry>();
		private static readonly List<int> PruneScratch = new List<int>();

		private static void ApplyAllPolicies(LayerBinding b, UtilityNetworkManager<FlowUtilityNetwork, Vent> manager)
		{
			if (b.Policies.Count == 0)
			{
				return;
			}
			int layer = (int)(b.ContentType == PipeContentType.Liquid
				? ObjectLayer.LiquidConduit : ObjectLayer.GasConduit);
			PruneScratch.Clear();
			CombinedScratch.Clear();
			foreach (KeyValuePair<int, PolicyEntry> entry in b.Policies)
			{
				int cell = entry.Key;
				// A policy outlives a broken pipe (the conduit object is still there) but not a
				// deconstructed one: nothing will ever put that cell back in a run by itself.
				if (!Grid.IsValidCell(cell) || Grid.Objects[cell, layer] == null)
				{
					PruneScratch.Add(cell);
					continue;
				}
				UtilityNetwork network = manager.GetNetworkForCell(cell);
				if (network == null)
				{
					continue;
				}
				Combine(CombinedScratch, network.id, entry.Value);
			}
			for (int i = 0; i < PruneScratch.Count; i++)
			{
				b.Policies.Remove(PruneScratch[i]);
			}
			foreach (KeyValuePair<int, PolicyEntry> run in CombinedScratch)
			{
				PushPolicy(b.ContentType, run.Key, run.Value);
			}
		}

		private static void ApplyPolicyForCell(LayerBinding b, int cell)
		{
			UtilityNetworkManager<FlowUtilityNetwork, Vent> manager = ManagerFor(b.ContentType);
			if (manager == null || manager.IsDirty)
			{
				// Applied by the rebuild that is about to happen.
				return;
			}
			UtilityNetwork target = manager.GetNetworkForCell(cell);
			if (target == null)
			{
				return;
			}
			PolicyEntry combined = new PolicyEntry();
			foreach (KeyValuePair<int, PolicyEntry> entry in b.Policies)
			{
				UtilityNetwork network = manager.GetNetworkForCell(entry.Key);
				if (network != null && network.id == target.id)
				{
					combined = Combined(combined, entry.Value);
				}
			}
			PushPolicy(b.ContentType, target.id, combined);
		}

		private static void Combine(Dictionary<int, PolicyEntry> into, int networkId, PolicyEntry entry)
		{
			PolicyEntry existing;
			into[networkId] = into.TryGetValue(networkId, out existing) ? Combined(existing, entry) : entry;
		}

		private static PolicyEntry Combined(PolicyEntry a, PolicyEntry b)
		{
			return new PolicyEntry
			{
				Flags = a.Flags | b.Flags,
				MixFraction = Math.Max(a.MixFraction, b.MixFraction),
				PhaseRatePerSecond = Math.Max(a.PhaseRatePerSecond, b.PhaseRatePerSecond),
				PhaseMinRemainderKg = Math.Max(a.PhaseMinRemainderKg, b.PhaseMinRemainderKg),
			};
		}

		private static void PushPolicy(PipeContentType contentType, int networkId, PolicyEntry policy)
		{
			int ok;
			try
			{
				ok = SIM_ConduitNetworkPolicy(NativeType(contentType), networkId, (int)policy.Flags,
					policy.MixFraction, policy.PhaseRatePerSecond, policy.PhaseMinRemainderKg);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ConduitNetworkPolicy");
				return;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return;
			}
			if (ok == 0 && (policy.Flags & ConduitNetworkPolicy.Convection) != 0)
			{
				// A SimDLL without convection refuses the whole push for the one bit it does not
				// know, which would take a run's Mix and Phase down with it. Push the rest alone,
				// and say once that this DLL cannot convect.
				ConduitNetworkPolicy without = policy.Flags & ~ConduitNetworkPolicy.Convection;
				ok = SIM_ConduitNetworkPolicy(NativeType(contentType), networkId, (int)without,
					policy.MixFraction, policy.PhaseRatePerSecond, policy.PhaseMinRemainderKg);
				if (ok != 0 && !convectionWarned)
				{
					convectionWarned = true;
					FrameworkLog.Warn("SimDLL refused ConduitNetworkPolicy.Convection (a DLL without "
						+ "convection) -- runs keep their other policies and exchange heat by "
						+ "Klei's arithmetic alone.");
				}
			}
			if (ok == 0)
			{
				PolicyRefusals++;
			}
		}

		private static bool convectionWarned;

		// ------------------------------------------------------------------ the side-car mirror

		private static bool trappedUnavailable;

		/// <summary>
		/// Pushes one tile of <see cref="PipeMatterFacade"/>'s store to the sim. Called by that
		/// class after every mutation it makes, through the one choke point it already had for
		/// persistence, so the sim's copy cannot drift from a path someone forgot.
		/// </summary>
		internal static void MirrorTrapped(bool gasConduit, int cell)
		{
			if (unavailable || trappedUnavailable)
			{
				return;
			}
			PipeMatterFacade.TrappedMatter matter;
			bool present = PipeMatterFacade.TryGet(gasConduit, cell, out matter) && matter.MassKg > 0f;
			PushTrapped(gasConduit ? NativeGas : NativeLiquid, cell, present ? matter.ElementIdx : 0,
				present ? matter.MassKg : 0f, present ? matter.TemperatureK : 0f);
		}

		private static void PushTrapped(int nativeType, int cell, int elementIdx, float massKg,
			float temperatureK)
		{
			try
			{
				SIM_ConduitTrappedSet(nativeType, cell, elementIdx, massKg, temperatureK);
			}
			catch (EntryPointNotFoundException)
			{
				trappedUnavailable = true;
				MarkUnavailable("SIM_ConduitTrappedSet");
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
			}
		}

		private static void ResyncTrapped(PipeContentType contentType)
		{
			if (unavailable || trappedUnavailable)
			{
				return;
			}
			int nativeType = NativeType(contentType);
			try
			{
				SIM_ConduitTrappedClear(nativeType);
			}
			catch (EntryPointNotFoundException)
			{
				trappedUnavailable = true;
				MarkUnavailable("SIM_ConduitTrappedClear");
				return;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return;
			}
			bool gasConduit = contentType != PipeContentType.Liquid;
			foreach (int cell in PipeMatterFacade.TrackedCells(gasConduit))
			{
				PipeMatterFacade.TrappedMatter matter;
				if (PipeMatterFacade.TryGet(gasConduit, cell, out matter) && matter.MassKg > 0f)
				{
					PushTrapped(nativeType, cell, matter.ElementIdx, matter.MassKg, matter.TemperatureK);
				}
			}
		}

		// ------------------------------------------------------------------ phase proposals

		/// <summary>The three things a <see cref="ConduitNetworkPolicy.Phase"/> decision can
		/// move, <c>ONI_CONDUIT_PHASE_*</c>.</summary>
		public enum PhaseChangeKind
		{
			/// <summary>A gas run's gas contents condensing to standing liquid.</summary>
			CondenseContents = 0,

			/// <summary>A liquid run's liquid contents boiling to standing gas.</summary>
			BoilContents = 1,

			/// <summary>A liquid run's standing gas condensing back into its contents.</summary>
			CondenseHeadspace = 2,

			/// <summary>
			/// A gas run's standing liquid evaporating back into its contents: the reverse of
			/// <see cref="CondenseContents"/>, judged against the same boundary on the same
			/// temperature (the contents' when the conduit has any, the liquid's own when it has
			/// none). Without it the sim would decide only the condensing half of a gas run, and
			/// whatever put condensate back would decide the other half on its own cadence --
			/// returning a tile the sim had just put on its dew point, and fighting it.
			/// A consumer that has its own return path should skip it for any run where
			/// <see cref="IsNativePhaseInForce"/> answers true.
			/// </summary>
			EvaporateTrapped = 3,
		}

		/// <summary>How many <see cref="PhaseChangeKind"/>s there are; the size of the per-kind
		/// counters.</summary>
		public const int PhaseChangeKindCount = 4;

		/// <summary>One phase-change decision of the sim's, as it published it.</summary>
		public struct PhaseProposal
		{
			public PipeContentType ContentType;
			public int NetworkId;
			public int Cell;
			public PhaseChangeKind Kind;
			public ushort SourceElementIdx;
			public ushort ProductElementIdx;
			public float SourceMassKg;
			public float SourceTemperatureK;
			public float ConvertedMassKg;
			public float BoundaryK;
			public float PressurePa;
			public float LatentHeatJPerKg;

			public override string ToString()
			{
				return ContentType + " run " + NetworkId + " cell " + Cell + ": " + Kind + " "
					+ SourceElementIdx + "->" + ProductElementIdx + " "
					+ ConvertedMassKg.ToString("F5") + " of " + SourceMassKg.ToString("F4") + " kg at "
					+ SourceTemperatureK.ToString("F2") + " K (boundary " + BoundaryK.ToString("F2")
					+ " K at " + PressurePa.ToString("F0") + " Pa)";
			}
		}

		private static byte[] proposalBytes = new byte[ProposalBytes * 64];

		/// <summary>
		/// The decisions of the sim's most recent conduit update, copied into
		/// <paramref name="into"/> (cleared first). Returns false when the DLL cannot say. For
		/// diagnostics and for a mod that wants to watch the sim decide; applying them is
		/// <see cref="Install"/>'s job, and applying them twice would convert twice.
		/// </summary>
		public static unsafe bool TryReadPhaseProposals(List<PhaseProposal> into)
		{
			if (into == null)
			{
				throw new ArgumentNullException("into");
			}
			into.Clear();
			if (unavailable)
			{
				return false;
			}
			int total;
			try
			{
				total = SIM_ConduitPhaseProposals(null, 0);
				if (total <= 0)
				{
					return true;
				}
				if (proposalBytes.Length < total * ProposalBytes)
				{
					proposalBytes = new byte[Math.Max(total, proposalBytes.Length / ProposalBytes * 2) * ProposalBytes];
				}
				fixed (byte* raw = proposalBytes)
				{
					total = Math.Min(SIM_ConduitPhaseProposals(raw, proposalBytes.Length / ProposalBytes),
						proposalBytes.Length / ProposalBytes);
					for (int i = 0; i < total; i++)
					{
						byte* r = raw + (i * ProposalBytes);
						into.Add(new PhaseProposal
						{
							ContentType = ReadInt(r, ProposalOffsetConduitType) == NativeLiquid
								? PipeContentType.Liquid : PipeContentType.Gas,
							NetworkId = ReadInt(r, ProposalOffsetNetworkId),
							Cell = ReadInt(r, ProposalOffsetCell),
							Kind = (PhaseChangeKind)ReadInt(r, ProposalOffsetKind),
							SourceElementIdx = *(ushort*)(r + ProposalOffsetSourceElement),
							ProductElementIdx = *(ushort*)(r + ProposalOffsetProductElement),
							SourceMassKg = ReadFloat(r, ProposalOffsetSourceMassKg),
							SourceTemperatureK = ReadFloat(r, ProposalOffsetSourceTemperatureK),
							ConvertedMassKg = ReadFloat(r, ProposalOffsetConvertedMassKg),
							BoundaryK = ReadFloat(r, ProposalOffsetBoundaryK),
							PressurePa = ReadFloat(r, ProposalOffsetPressurePa),
							LatentHeatJPerKg = ReadFloat(r, ProposalOffsetLatentHeat),
						});
					}
				}
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ConduitPhaseProposals");
				return false;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return false;
			}
			return true;
		}

		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run, i.e. whether anything carries out the
		/// sim's phase decisions.</summary>
		public static bool Installed
		{
			get { return installed; }
		}

		/// <summary>Proposals carried out since load.</summary>
		public static long ProposalsApplied { get; private set; }

		/// <summary>Proposals refused since load because the tile no longer matched the state
		/// the sim decided against. Should stay at zero: contents reach the sim through
		/// <c>ConduitFlow.SetContents</c>, and nothing moves them between the update and the
		/// patch that applies its decisions.</summary>
		public static long ProposalsRefused { get; private set; }

		/// <summary>Mass the applied proposals converted since load, kg.</summary>
		public static double ProposalMassAppliedKg { get; private set; }

		/// <summary>Proposals the most recent update published.</summary>
		public static int LastProposalCount { get; private set; }

		private static readonly long[] AppliedByKind = new long[PhaseChangeKindCount];
		private static readonly double[] MassAppliedByKind = new double[PhaseChangeKindCount];

		/// <summary>
		/// Proposals of one kind carried out since load. The totals above cannot say which way
		/// matter moved, and that matters: a run condensing and
		/// evaporating the same mass reads as busy in <see cref="ProposalMassAppliedKg"/> and as
		/// nothing in the room temperatures. 0 for a kind outside the enum.
		/// </summary>
		public static long ProposalsAppliedOfKind(PhaseChangeKind kind)
		{
			int k = (int)kind;
			return k >= 0 && k < PhaseChangeKindCount ? AppliedByKind[k] : 0L;
		}

		/// <summary>Mass the applied proposals of one kind converted since load, kg.</summary>
		public static double ProposalMassAppliedOfKind(PhaseChangeKind kind)
		{
			int k = (int)kind;
			return k >= 0 && k < PhaseChangeKindCount ? MassAppliedByKind[k] : 0.0;
		}

		/// <summary>
		/// Called once for every proposal <see cref="Install"/>'s patch handles, straight after
		/// handling it, with the mass it converted -- 0 when the helper found nothing to do, and
		/// negative when the proposal was refused as stale. For a consumer that wants to see WHERE
		/// the sim is changing phase (a per-tile tally, an overlay), which the counters cannot say.
		///
		/// Runs on the game thread inside <c>ConduitTemperatureManager.Sim200ms</c>, once per
		/// proposal, so keep it cheap. An exception is logged and the rest of the update's
		/// proposals are still applied: an observer must never be the thing that stops the
		/// physics. Same shape as <see cref="PipeMatterFacade.MirrorHook"/>.
		/// </summary>
		public static Action<PhaseProposal, float> ProposalApplied;

		/// <summary>
		/// Carries out the sim's <see cref="ConduitNetworkPolicy.Phase"/> decisions: one Harmony
		/// postfix on <c>ConduitTemperatureManager.Sim200ms</c>, the call that runs the sim's
		/// conduit update, so every update's decisions are applied before anything else reads
		/// the pipes -- and, because an update re-decides from scratch, never applied twice or
		/// skipped. Idempotent.
		///
		/// NOT INSTALLED BY DEFAULT: OniFramework ships no Harmony patches of its own. A mod that
		/// sets a <see cref="ConduitNetworkPolicy.Phase"/> policy opts in with one line in its
		/// <c>UserMod2.OnLoad</c>; without it the sim still decides, nothing applies, and
		/// <see cref="PipeMatterFacade.TickNetworkPhaseChange"/> keeps deciding for itself.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (installed)
			{
				return;
			}
			MethodInfo sim200ms = AccessTools.Method(typeof(ConduitTemperatureManager), "Sim200ms",
				new Type[] { typeof(float) });
			if (sim200ms == null)
			{
				throw new InvalidOperationException(
					"SimConduitNetworks.Install could not find ConduitTemperatureManager.Sim200ms(float). "
					+ "The game build has moved and phase decisions would silently never be applied, so "
					+ "this throws instead. Re-check the member against the current game assembly.");
			}
			harmony.Patch(sim200ms, null, new HarmonyMethod(
				AccessTools.Method(typeof(SimConduitNetworks), "Sim200msPostfix")));
			installed = true;
		}

		private static readonly List<PhaseProposal> ProposalScratch = new List<PhaseProposal>();

		// Not attributed, for the reason PowerHeat gives: an attributed class would be swept up by
		// a consumer's Harmony.PatchAll(assembly). Install() wires it by hand.
		private static void Sim200msPostfix(float dt)
		{
			// dt 0 is RebuildConnections publishing new handles; the sim decides nothing on it.
			if (dt <= 0f || unavailable)
			{
				return;
			}
			if (!TryReadPhaseProposals(ProposalScratch))
			{
				return;
			}
			LastProposalCount = ProposalScratch.Count;
			Action<PhaseProposal, float> observer = ProposalApplied;
			for (int i = 0; i < ProposalScratch.Count; i++)
			{
				PhaseProposal proposal = ProposalScratch[i];
				float kg = PipeMatterFacade.ApplyNativePhaseProposal(proposal);
				if (kg < 0f)
				{
					ProposalsRefused++;
				}
				else
				{
					ProposalsApplied++;
					ProposalMassAppliedKg += kg;
					int k = (int)proposal.Kind;
					if (k >= 0 && k < PhaseChangeKindCount)
					{
						AppliedByKind[k]++;
						MassAppliedByKind[k] += kg;
					}
				}
				if (observer != null)
				{
					try
					{
						observer(proposal, kg);
					}
					catch (Exception e)
					{
						FrameworkLog.Warn("SimConduitNetworks.ProposalApplied threw; the proposal was "
							+ "still applied: " + e);
					}
				}
			}
		}

		/// <summary>
		/// Whether the sim is deciding phase change for the run containing <paramref name="cell"/>
		/// AND something is carrying those decisions out -- the question
		/// <see cref="PipeMatterFacade.TickNetworkPhaseChange"/> asks before deciding for itself, so
		/// that a run is never converted by both.
		/// </summary>
		public static bool IsNativePhaseInForce(int cell, PipeContentType contentType)
		{
			if (!installed || unavailable)
			{
				return false;
			}
			ConduitNetworkAggregate aggregate;
			return TryGetAggregate(cell, contentType, out aggregate)
				&& (aggregate.Policy & ConduitNetworkPolicy.Phase) != 0;
		}

		// ------------------------------------------------------------------ reads

		/// <summary>
		/// The whole run containing <paramref name="cell"/>, summed by the sim, with no allocation.
		///
		/// False, with <paramref name="aggregate"/> holding <see cref="NoNetwork"/> and zeros, when
		/// the DLL cannot answer, there is no conduit of that type at the cell, the cell's conduit
		/// is broken (in no run), or the game has a topology change it has not partitioned yet --
		/// the same refusals as <see cref="ConduitNetworks.TryGetCells"/>, whose id this uses.
		/// </summary>
		public static bool TryGetAggregate(int cell, PipeContentType contentType,
			out ConduitNetworkAggregate aggregate)
		{
			aggregate = default(ConduitNetworkAggregate);
			aggregate.ContentType = contentType;
			aggregate.NetworkId = NoNetwork;
			if (unavailable || !EnsureBound(BindingFor(contentType)))
			{
				return false;
			}
			int networkId = ConduitNetworks.NetworkIdOf(cell, contentType);
			if (networkId < 0)
			{
				return false;
			}
			return TryGetAggregateById(contentType, networkId, out aggregate);
		}

		/// <summary>
		/// One run by id. The id must come from the current partition -- from
		/// <see cref="ConduitNetworks.NetworkIdOf"/> this frame -- because ids are re-assigned on
		/// every topology change.
		/// </summary>
		public static unsafe bool TryGetAggregateById(PipeContentType contentType, int networkId,
			out ConduitNetworkAggregate aggregate)
		{
			aggregate = default(ConduitNetworkAggregate);
			aggregate.ContentType = contentType;
			aggregate.NetworkId = NoNetwork;
			if (unavailable || !EnsureBound(BindingFor(contentType)))
			{
				return false;
			}

			byte* raw = stackalloc byte[AggregateBytes];
			int ok;
			try
			{
				ok = SIM_ConduitNetworkAggregate(NativeType(contentType), networkId, raw, AggregateBytes);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ConduitNetworkAggregate");
				return false;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return false;
			}
			if (ok == 0)
			{
				return false;
			}

			aggregate.NetworkId = ReadInt(raw, OffsetNetworkId);
			int nativeType = ReadInt(raw, OffsetConduitType);
			aggregate.ContentType = nativeType == NativeLiquid ? PipeContentType.Liquid : PipeContentType.Gas;
			aggregate.Generation = ReadInt(raw, OffsetGeneration);
			aggregate.Policy = (ConduitNetworkPolicy)ReadInt(raw, OffsetPolicyFlags);
			aggregate.ConduitCount = ReadInt(raw, OffsetConduitCount);
			aggregate.FilledCount = ReadInt(raw, OffsetFilledCount);
			aggregate.SpeciesCount = ReadInt(raw, OffsetSpeciesCount);
			aggregate.SpeciesOverflow = ReadInt(raw, OffsetSpeciesOverflow);
			aggregate.MixFraction = ReadFloat(raw, OffsetMixFraction);
			aggregate.VolumeM3 = ReadFloat(raw, OffsetVolumeM3);
			aggregate.TotalMassKg = ReadFloat(raw, OffsetTotalMassKg);
			aggregate.TotalMoles = ReadFloat(raw, OffsetTotalMoles);
			aggregate.HeatCapacityKJPerK = ReadFloat(raw, OffsetHeatCapacity);
			aggregate.TemperatureK = ReadFloat(raw, OffsetTemperatureK);
			aggregate.MassWeightedTemperatureK = ReadFloat(raw, OffsetMassWeightedTemperatureK);
			aggregate.PressurePa = ReadFloat(raw, OffsetPressurePa);
			aggregate.TrappedGasKg = ReadFloat(raw, OffsetTrappedGasKg);
			aggregate.TrappedLiquidKg = ReadFloat(raw, OffsetTrappedLiquidKg);
			aggregate.TrappedSolidKg = ReadFloat(raw, OffsetTrappedSolidKg);
			aggregate.LiquidVolumeM3 = ReadFloat(raw, OffsetLiquidVolumeM3);
			aggregate.HeadspacePressurePa = ReadFloat(raw, OffsetHeadspacePressurePa);
			aggregate.PhaseRatePerSecond = ReadFloat(raw, OffsetPhaseRatePerSecond);
			aggregate.LinkCount = ReadInt(raw, OffsetLinkCount);

			// Clamped rather than trusted, as in SimRooms: the count indexes a fixed buffer here.
			int count = aggregate.SpeciesCount;
			if (count < 0)
			{
				count = 0;
			}
			if (count > MaxSpecies)
			{
				count = MaxSpecies;
			}
			aggregate.SpeciesCount = count;
			for (int i = 0; i < count; i++)
			{
				aggregate.SetSlot(i, *(ushort*)(raw + OffsetSpecies + (i * 2)),
					ReadFloat(raw, OffsetMassBySpecies + (i * 4)));
			}
			return true;
		}

		private static unsafe int ReadInt(byte* raw, int offset)
		{
			return *(int*)(raw + offset);
		}

		private static unsafe float ReadFloat(byte* raw, int offset)
		{
			return *(float*)(raw + offset);
		}
	}
}
