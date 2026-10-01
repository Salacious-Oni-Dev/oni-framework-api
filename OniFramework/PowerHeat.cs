using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The power -> heat rule: electrical energy a building draws becomes heat in that
	/// building's own structure temperature, unless the building accounts for it itself.
	///
	/// WHY THIS EXISTS. Measured across every powered building config in the game (135 of
	/// them), <b>not one</b> emits heat equal to its draw: 102 emit more (Polymerizer draws
	/// 240 W and declares 32.5 kW, a factor of 135) and 33 emit none at all (the Thermo
	/// Aquatuner draws 1600 W and declares no self-heat whatsoever). <c>SelfHeatKilowatts-
	/// WhenActive</c> and <c>ExhaustKilowattsWhenActive</c> are hand-tuned constants with no
	/// derivation from <c>EnergyConsumptionWhenActive</c>. This replaces 135 unrelated
	/// constants with one rule: power a building draws becomes heat, and no heat is deleted.
	///
	/// SHAPE BORROWED FROM STATIONEERS. Its <c>Device</c> base carries
	/// <c>protected virtual float EnergyToHeatRatio => 0.2f</c> and, on the atmospheric tick,
	/// does <c>atmosphere.GasMixture.AddEnergy(new MoleEnergy(usedPower * EnergyToHeatRatio))</c>
	/// -- a virtual per-device coefficient on <i>live</i> draw, on by default, overridden to
	/// <c>0f</c> to opt out (<c>Battery</c>, <c>AreaPowerControl</c>) or to a different value
	/// (<c>Transformer</c>, <c>0.05f</c>). We take that shape verbatim and change two things:
	///
	/// <list type="number">
	/// <item>The default coefficient is <b>1.0, not 0.2</b>. Stationeers can afford 0.2
	/// because its machines that do real thermodynamic work also account their transfers
	/// explicitly into named atmospheres, so the 0.2 only covers incidental loss. ONI's 102
	/// creators have no such second accounting, so 0.2 there would merely be a smaller
	/// deletion.</item>
	/// <item>The heat lands in the <b>building body</b>, not the room air. Stationeers dumps
	/// to the room because its devices have no per-device temperature; ONI's do. The body
	/// path also cannot destroy the energy: ONI's room path (<c>ExhaustHeat</c>) scales
	/// delivery by <c>min(Grid.Mass, 1.5) / 1.5</c> and discards the remainder, so in vacuum
	/// it would delete exactly the heat this rule exists to conserve.</item>
	/// </list>
	///
	/// MECHANISM -- A FIELD THE SIM OWNS. The rate is pushed to the custom SimDLL through
	/// <c>ext::kSetBuildingWasteHeatKilowatts</c> (abi/sim_abi_ext.h), which lands in a
	/// <c>waste_heat_kilowatts</c> field on the building's native record and is summed with
	/// Klei's own <c>operating_kilowatts</c> at the same post-clamp line in
	/// <c>StepBuildingHeatExchange</c>.
	///
	/// This was first written the other way -- a Harmony postfix on
	/// <c>StructureTemperaturePayload.OperatingKilowatts</c>, so that vanilla's own
	/// <c>ModifyBuildingHeatExchange</c> would carry a larger number. That shape works, and it
	/// is wrong for two reasons:
	///
	/// <list type="number">
	/// <item>It lies to the sim. The sim cannot then tell vanilla's hand-tuned self-heat from
	/// the derived rule, so neither can the native energy ledger, and the whole point of that
	/// ledger is to be able to answer how much of a colony's heat came from the power grid --
	/// not merely that heat appeared. With its own field, <c>bldgwasteheat</c> stays separable
	/// for the whole run.</item>
	/// <item>It was fragile in a way with no symptom. That getter is tiny and has two call
	/// sites; if Mono inlined it, the detour would be bypassed and the rule would silently do
	/// nothing.</item>
	/// </list>
	///
	/// The physics has always been native -- <c>operating_kilowatts * dt / total_hc</c> in
	/// the replacement SimDLL's <c>sim/buildings.h</c>. What changed is that the derived rate is now a
	/// number the sim holds rather than a number a patch whispers into a vanilla getter. The
	/// managed side does what only it can do: read the power grid, which is entirely managed
	/// (<c>CircuitManager</c>, <c>EnergySim</c>, <c>EnergyConsumer</c> -- the SimDLL has never
	/// seen a wire).
	///
	/// <b>This requires the custom SimDLL.</b> On a stock <c>SimDLL.dll</c> the message id is
	/// unrecognised and dropped, so the rule does nothing. That is deliberate and is the whole
	/// reason the fork exists: the kernels and the conservation instrument live in the DLL,
	/// where they are fast and measurable, rather than being reimplemented in C#.
	///
	/// What the native side then does with it (<c>StepBuildingHeatExchange</c> in the
	/// replacement SimDLL's <c>sim/buildings.h</c>):
	/// <code>
	/// float next = Clamp(t_building + accumulated / total_hc, seen_min, seen_max);
	/// next += (dt * d.operating_kilowatts) / total_hc;   // added AFTER the clamp
	/// </code>
	/// Unconditional -- no medium test, nothing can drop it. The building then conducts to
	/// each cell under it at a rate set by the <i>hotter</i> side's heat capacity, and vacuum
	/// cells (<c>c.mass &lt;= 0</c>) are skipped entirely. So a machine in a pressurised room
	/// sheds its draw into the air about as fast as it arrives; a machine in a thin room or a
	/// vacuum heats up and can trip <c>Overheatable</c>. That is the intended gameplay
	/// consequence, not a side effect: a thin atmosphere is a bad heat sink.
	///
	/// NOT INSTALLED BY DEFAULT. OniFramework ships no Harmony patches of its own -- see
	/// mod.yaml. A consumer opts in with one line in its <c>UserMod2.OnLoad</c>:
	/// <code>
	/// public override void OnLoad(Harmony harmony)
	/// {
	///     base.OnLoad(harmony);
	///     OniFramework.PowerHeat.Install(harmony);
	/// }
	/// </code>
	/// Third-party mods get the identical surface: the capability is not buried in a gameplay
	/// mod.
	/// </summary>
	public static class PowerHeat
	{
		/// <summary>
		/// The fraction of drawn electrical power that becomes heat when a building has no
		/// override. 1.0 -- full conservation. Stationeers' equivalent default is 0.2; see the
		/// class remarks for why ours differs.
		/// </summary>
		public const float DefaultRatio = 1f;

		private struct Override
		{
			public float ratio;
			public bool hasRatio;
			public float biasKilowatts;
			public string reason;
		}

		private static readonly Dictionary<string, Override> overrides =
			new Dictionary<string, Override>();

		private static bool installed;

		private static float globalRatio = DefaultRatio;

		/// <summary>
		/// Coefficient applied to every powered building that has no per-building override.
		/// Setting this does not retroactively change buildings already spawned in a save --
		/// it is read live on each 200 ms sim tick, so it takes effect on the next one.
		/// Negative values are rejected: a building cannot absorb heat by drawing power.
		/// </summary>
		public static float GlobalRatio
		{
			get { return globalRatio; }
			set
			{
				if (value < 0f)
				{
					throw new ArgumentOutOfRangeException("value",
						"PowerHeat.GlobalRatio cannot be negative; use 0 to disable the rule.");
				}
				globalRatio = value;
			}
		}

		/// <summary>True once <see cref="Install"/> has run in this process.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		/// <summary>
		/// Override the coefficient for one building prefab.
		/// </summary>
		/// <param name="prefabId">A <c>BuildingDef.PrefabID</c>, e.g. "HeatCompressor".</param>
		/// <param name="ratio">Fraction of live draw that becomes heat. 0 exempts the
		/// building entirely; values above 1 are permitted but mean the building creates
		/// energy, so they need a justification in <paramref name="reason"/>.</param>
		/// <param name="reason">Why this building differs. Recorded and reported by
		/// <see cref="DescribeOverrides"/>; required, because an unexplained coefficient is
		/// exactly the hand-tuned constant this class exists to remove.</param>
		public static void SetRatio(string prefabId, float ratio, string reason)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			if (ratio < 0f)
			{
				throw new ArgumentOutOfRangeException("ratio",
					"A ratio cannot be negative; a building that removes heat should account for it "
					+ "with GameComps.StructureTemperatures.ProduceEnergy instead.");
			}
			if (string.IsNullOrEmpty(reason))
			{
				throw new ArgumentNullException("reason",
					"Every PowerHeat override must record why it differs from the rule.");
			}
			Override o;
			overrides.TryGetValue(prefabId, out o);
			o.ratio = ratio;
			o.hasRatio = true;
			o.reason = reason;
			overrides[prefabId] = o;
		}

		/// <summary>
		/// Exempt a building from the rule -- shorthand for <see cref="SetRatio"/> with 0.
		///
		/// The bar for this is high and no vanilla building currently clears it. Only six
		/// vanilla files call <c>ProduceEnergy</c> at all (AirConditioner, SpaceHeater,
		/// RefrigeratorController, DirectVolumeHeater, ContactConductivePipeBridge,
		/// SteamTurbine) and every one of them accounts for heat it <i>moved</i> or fabricated,
		/// never for the power it <i>drew</i> -- so they are additive, not exempt. A correct
		/// exemption is a building that converts its own draw explicitly, such as a heat engine
		/// built on <see cref="StirlingCycleRule"/>.
		/// </summary>
		public static void Exempt(string prefabId, string reason)
		{
			SetRatio(prefabId, 0f, reason);
		}

		/// <summary>
		/// Extra heat in kilowatts for one prefab, added on top of the derived draw whenever
		/// the building is active. This is what is left of vanilla's
		/// <c>SelfHeatKilowattsWhenActive</c>: a bias on a derived number rather than the
		/// whole answer, and it must name a non-electrical energy source (combustion, an
		/// exothermic reaction) to be legitimate. Chemical process heat generally belongs in
		/// <c>ElementConverter</c>'s enthalpy accounting instead.
		/// </summary>
		public static void SetBias(string prefabId, float kilowatts, string reason)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			if (string.IsNullOrEmpty(reason))
			{
				throw new ArgumentNullException("reason",
					"Every PowerHeat bias must name the non-electrical energy source it represents.");
			}
			Override o;
			overrides.TryGetValue(prefabId, out o);
			o.biasKilowatts = kilowatts;
			o.reason = reason;
			overrides[prefabId] = o;
		}

		/// <summary>Drop any ratio and bias registered for this prefab.</summary>
		public static void ClearOverride(string prefabId)
		{
			if (!string.IsNullOrEmpty(prefabId))
			{
				overrides.Remove(prefabId);
			}
		}

		/// <summary>The coefficient in force for this prefab, override or global.</summary>
		public static float RatioFor(string prefabId)
		{
			Override o;
			if (prefabId != null && overrides.TryGetValue(prefabId, out o) && o.hasRatio)
			{
				return o.ratio;
			}
			return globalRatio;
		}

		/// <summary>The bias in kilowatts registered for this prefab, or 0.</summary>
		public static float BiasFor(string prefabId)
		{
			Override o;
			if (prefabId != null && overrides.TryGetValue(prefabId, out o))
			{
				return o.biasKilowatts;
			}
			return 0f;
		}

		/// <summary>
		/// Every registered override, one human-readable line each, for logging and for the
		/// debug inspector. Ordering follows registration order of the underlying dictionary
		/// and is not guaranteed stable.
		/// </summary>
		public static IEnumerable<string> DescribeOverrides()
		{
			foreach (KeyValuePair<string, Override> kv in overrides)
			{
				Override o = kv.Value;
				string ratioText = o.hasRatio
					? o.ratio.ToString("0.###")
					: "global(" + globalRatio.ToString("0.###") + ")";
				yield return kv.Key + ": ratio=" + ratioText
					+ ", bias=" + o.biasKilowatts.ToString("0.###") + " kW"
					+ " -- " + (o.reason ?? "(no reason recorded)");
			}
		}

		/// <summary>
		/// Heat in kilowatts this building should be producing right now from its own power
		/// draw, before vanilla's own <c>SelfHeatKilowattsWhenActive</c> is added. Public so a
		/// third-party mod can read the same number the rule applies -- for a UI readout, a
		/// sensor, or an IC10 device field -- without duplicating the derivation.
		///
		/// Returns 0 for anything with no <c>Operational</c>, mirroring vanilla's own
		/// <c>OperatingKilowatts</c> gate, and 0 while the building is not active.
		/// </summary>
		public static float DerivedKilowatts(GameObject go)
		{
			if (go == null)
			{
				return 0f;
			}
			return DerivedKilowatts(go.GetComponent<Building>(), go.GetComponent<Operational>());
		}

		/// <summary>
		/// The derivation itself, taking the two components the sim payload already holds so
		/// the hot path does not have to re-resolve them.
		/// </summary>
		public static float DerivedKilowatts(Building building, Operational operational)
		{
			if (building == null)
			{
				return 0f;
			}
			BuildingDef def = building.Def;
			if (def == null)
			{
				return 0f;
			}
			// Vanilla's OperatingKilowatts returns 0 when there is no Operational; match that
			// rather than inventing a second convention. It also keeps EnergyConsumer.WattsUsed
			// (which dereferences its own Operational) from throwing.
			if (operational == null || !operational.IsActive)
			{
				return 0f;
			}
			string prefabId = def.PrefabID;
			float ratio = RatioFor(prefabId);
			float bias = BiasFor(prefabId);
			if (ratio <= 0f && bias == 0f)
			{
				return 0f;
			}
			float kilowatts = bias;
			if (ratio > 0f)
			{
				EnergyConsumer consumer = building.GetComponent<EnergyConsumer>();
				if (consumer != null)
				{
					// WattsUsed, not WattsNeededWhenActive: the live figure the circuit is
					// actually billed, which buildings such as the Refrigerator and Space
					// Heater vary at runtime through BaseWattageRating.
					kilowatts += consumer.WattsUsed * ratio * 0.001f;
				}
			}
			return kilowatts;
		}

		/// <summary>
		/// Apply the rule. Idempotent -- a second call with the same or a different Harmony
		/// instance is a no-op, so several mods can each ask for it without double-patching.
		/// </summary>
		/// <param name="harmony">The calling mod's Harmony instance, normally the one handed
		/// to <c>UserMod2.OnLoad</c>.</param>
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

			// ONE patch, on a method far too large for the JIT to inline. The earlier design
			// needed three (a property getter, a prefab-init latch and a wattage setter) and
			// the getter was the fragile one; pushing a native field instead removes all of
			// that. Sim200ms is also exactly the right sweep to ride on: it already visits
			// every building with a structure temperature, once every 200 ms.
			MethodInfo sim200ms = AccessTools.Method(
				typeof(StructureTemperatureComponents), "Sim200ms", new Type[] { typeof(float) });
			if (sim200ms == null)
			{
				throw new InvalidOperationException(
					"PowerHeat.Install could not find StructureTemperatureComponents.Sim200ms(float). "
					+ "The game build has moved and the rule would silently do nothing, so this "
					+ "throws instead. Re-check the member against the current game assembly before shipping.");
			}
			harmony.Patch(sim200ms, null, new HarmonyMethod(
				AccessTools.Method(typeof(PowerHeat), "Sim200msPostfix")));

			installed = true;
		}

		// ------------------------------------------------------------------ the push
		//
		// Not attributed. OniFramework must never patch anything merely by being loaded, and
		// an attributed class would be swept up by a consumer's Harmony.PatchAll(assembly).
		// Install() wires this by hand instead.

		/// <summary>
		/// The last rate actually sent for each native building handle, so an unchanged
		/// building costs a dictionary probe and no message. Keyed on the sim handle rather
		/// than on the GameObject because the handle is what the message addresses.
		/// </summary>
		private static readonly Dictionary<int, float> sent = new Dictionary<int, float>();

		/// <summary>
		/// Total kilowatts the rule currently has in force across every building it tracks, as
		/// of the last 200 ms sweep. This is the rule's OWN view of what it told the sim, which
		/// is not the same statement as a caller re-deriving the sum itself: a diagnostic that
		/// re-enumerates buildings independently is testing its own enumeration as much as the
		/// rule. Exposed so a probe can compare the sim's charge against what was actually
		/// pushed.
		/// </summary>
		public static float LastSweepKilowatts { get; private set; }

		/// <summary>Buildings carrying a non-zero rate as of the last sweep.</summary>
		public static int LastSweepCount { get; private set; }

		/// <summary>Sweeps completed since load. Zero means the patch is not firing at all.</summary>
		public static long SweepCount { get; private set; }

		/// <summary>
		/// Walks the buildings the sim already tracks and pushes any rate that changed.
		///
		/// A sweep rather than an event subscription, deliberately. The alternative is to
		/// hook every way a draw can change -- operational flags, <c>BaseWattageRating</c>
		/// writes, spawn, despawn, overheat -- and a rate that silently goes stale because
		/// one of those paths was missed has no symptom at all. Comparing 100-odd floats
		/// five times a second costs nothing measurable next to the sweep this rides on,
		/// which is already walking the same list.
		/// </summary>
		private static void Sim200msPostfix(StructureTemperatureComponents __instance)
		{
			List<StructureTemperatureHeader> headers;
			List<StructureTemperaturePayload> payloads;
			__instance.GetDataLists(out headers, out payloads);

			// Handles are recycled with a version byte, so a destroyed building's entry is
			// never hit again and would sit here for the rest of the session. Clearing when
			// the cache has outgrown the live set costs one redundant resend pass and is
			// simpler than tracking liveness per entry.
			if (sent.Count > payloads.Count * 2 + 64)
			{
				sent.Clear();
			}

			float total = 0f;
			int nonZero = 0;
			for (int i = 0; i < payloads.Count; i++)
			{
				StructureTemperaturePayload payload = payloads[i];
				int handle = payload.simHandleCopy;
				if (!global::Sim.IsValidHandle(handle))
				{
					continue;
				}
				float kilowatts = DerivedKilowatts(payload.building, payload.operational);
				if (kilowatts != 0f)
				{
					total += kilowatts;
					nonZero++;
				}
				float previous;
				if (sent.TryGetValue(handle, out previous) && previous == kilowatts)
				{
					continue;
				}
				sent[handle] = kilowatts;
				Push(handle, kilowatts);
			}
			LastSweepKilowatts = total;
			LastSweepCount = nonZero;
			SweepCount++;
		}

		/// <summary>
		/// Sends <c>ext::kSetBuildingWasteHeatKilowatts</c>. Same shape as
		/// <see cref="ThermalMassBonus"/>: reuses Klei's own <c>Sim.SIM_HandleMessage</c>,
		/// since this only sends and reads nothing back.
		///
		/// On a stock SimDLL the id is unrecognised and the message is dropped, so this is a
		/// silent no-op rather than a crash -- but unlike ThermalMassBonus that is not a
		/// graceful degradation to be relied on: without the custom DLL there is no rule.
		/// </summary>
		private static unsafe void Push(int simHandle, float kilowatts)
		{
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(simHandle), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(kilowatts), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(
					OniExtMessages.SetBuildingWasteHeatKilowatts, payload.Length, msg);
			}
		}
	}
}
