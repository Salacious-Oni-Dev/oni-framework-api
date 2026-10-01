using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The Wheezewort's cooling, given a destination. The last of the four vanilla heat-deletion
	/// channels this framework closes, and the smallest: a
	/// <c>ColdBreather</c> inhales gas, exhales the same mass five degrees colder, and the energy
	/// that removal took simply ceases to exist.
	///
	/// WHAT VANILLA DOES. <c>ColdBreather</c> is a plant, not a building. An
	/// <c>ElementConsumer</c> (<c>AllGas</c>, 2 kg capacity, 1 kg/s once replanted) draws the
	/// surrounding atmosphere into its <c>Storage</c> at whatever temperature it was, and once a
	/// second <c>ColdBreather.Exhale</c> emits one stored species back to the world:
	///
	/// <code>
	/// float temperature = Mathf.Max(component.Element.lowTemp + 5f,
	///                               component.Temperature + deltaEmitTemperature);   // -5 K
	/// SimMessages.EmitMass(gameCell, idx, component.Mass, temperature, ...);
	/// </code>
	///
	/// <c>ProduceEnergy</c> is never called anywhere in the file and the plant's own temperature
	/// is left untouched. So <c>mass * specificHeatCapacity * (T_in - T_out)</c> is deleted every
	/// exhale, with no accounting of any kind -- the audit's own words for this entry were
	/// "deletion with no accounting at all".
	///
	/// WHAT THIS DOES INSTEAD. The plant is left exactly as designed -- it still exhales five
	/// degrees colder, on the same schedule, consuming the same mass. The only change is that the
	/// heat it removed from that gas is now deposited into the plant's own body, and from there
	/// conducts into whatever the plant is standing in through ONI's ordinary element-chunk
	/// transfer. Nothing new is invented: this is the same "reject into the body, let the body
	/// conduct" model <see cref="TurbineHeat"/> uses, and the
	/// body in question is the <c>SimTemperatureTransfer</c> element chunk every plant already
	/// registers (surface area 10 m^2, thickness 0.001 m -- <c>ColdBreatherConfig</c>).
	///
	/// WHY THE BODY AND NOT THE ROOM. A plant has a per-entity
	/// temperature and a real overheat threshold -- <c>TemperatureVulnerable.Configure(213.15,
	/// 183.15, 368.15, 463.15)</c>, so 368.15 K wilts it and 463.15 K kills it. Depositing the
	/// heat into the body makes that threshold the same kind of self-throttle the Steam Turbine's
	/// 373.15 K <c>building_too_hot</c> gate became under its own rework: a Wheezewort in a space
	/// it cannot reject heat from eventually cooks itself, and a Wheezewort whose body a player
	/// actively cools keeps working. The plant stops being a free area cooler and becomes a heat
	/// pump that has to be serviced, which is the engineering the "heat must go somewhere" rule
	/// exists to create. Pushing the heat straight into the room air was the considered
	/// alternative and was not taken: it kills the free cooling just the same but with no failure
	/// mode and nothing for the player to engineer around.
	///
	/// NO CUSTOM SIMDLL NEEDED. The deposit is <c>SimMessages.ModifyElementChunkEnergy</c>, which
	/// is <c>ElementChunk::ModifyEnergy</c> -- a real vanilla sim message at a real vanilla
	/// address, floored at zero and not ceilinged. This rule therefore works on a stock DLL as
	/// well as the fork; only the fork's energy ledger sees it as a booked transfer rather than
	/// an anonymous one.
	///
	/// WHAT IS DELIBERATELY NOT CHANGED. The five-degree delta itself, the clamp to
	/// <c>lowTemp + 5</c>, the consumption rate, the radiation output, the fertilisation. This is
	/// a destination for an energy flow, not a redesign of the plant.
	/// </summary>
	public static class ColdBreatherHeat
	{
		private static bool installed;

		private static float rejectionFraction = 1f;

		/// <summary>Reads back a <c>ColdBreather</c>'s private <c>lastEmitTag</c>. Cached once;
		/// the field is the plant's own in-flight-emit handshake and there is no public accessor
		/// for it.</summary>
		private static readonly FieldInfo lastEmitTagField =
			AccessTools.Field(typeof(ColdBreather), "lastEmitTag");

		/// <summary>Whether <see cref="Install"/> has run. False means every Wheezewort in the
		/// colony is still deleting the heat it removes.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		/// <summary>
		/// Fraction of the removed heat that reaches the plant body. One by default, which is the
		/// whole point of this class.
		///
		/// It exists only so a balance run can measure a partial rejection without rebuilding.
		/// Anything below one is a deliberate re-opening of the deletion this rule closes -- the
		/// missing fraction has no destination, exactly as in vanilla -- and
		/// <see cref="StillDeletedKJ"/> counts what a setting below one has thrown away so a run
		/// that uses it cannot quietly forget.
		/// </summary>
		public static float RejectionFraction
		{
			get { return rejectionFraction; }
			set { rejectionFraction = Mathf.Clamp01(value); }
		}

		// ------------------------------------------------------------------ accounting

		/// <summary>Heat delivered to Wheezewort bodies by this rule since load, kJ.</summary>
		public static double DeliveredKJ { get; private set; }

		/// <summary>
		/// What vanilla would have deleted over the same period, kJ -- the whole of it, since
		/// vanilla deletes 100 %. With <see cref="RejectionFraction"/> at its default of one this
		/// equals <see cref="DeliveredKJ"/> and is the measure of the rule's effect.
		/// </summary>
		public static double VanillaWouldHaveDeletedKJ { get; private set; }

		/// <summary>
		/// Heat this rule itself failed to place, kJ. The sum of the
		/// <see cref="RejectionFraction"/> shortfall and any exhale whose plant had no valid
		/// element-chunk handle yet (registration is asynchronous, same as every other sim
		/// component). Zero in the steady state at the default fraction.
		/// </summary>
		public static double StillDeletedKJ { get; private set; }

		/// <summary>Exhales priced since load. Zero while Wheezeworts are visibly breathing would
		/// mean the patch applied and is never reached.</summary>
		public static long TickCount { get; private set; }

		/// <summary>Exhales whose plant had no element-chunk handle yet, so the heat went
		/// nowhere. Counted separately from the fraction shortfall because it is transient --
		/// it should fall to zero once a freshly planted colony's chunks finish registering --
		/// where the fraction shortfall is a standing choice.</summary>
		public static long UnregisteredExhales { get; private set; }

		/// <summary>Reset the running totals.</summary>
		public static void ResetAccounting()
		{
			DeliveredKJ = 0.0;
			VanillaWouldHaveDeletedKJ = 0.0;
			StillDeletedKJ = 0.0;
			TickCount = 0;
			UnregisteredExhales = 0;
		}

		// ------------------------------------------------------------------ installation

		/// <summary>
		/// Patch <c>ColdBreather.OnSimEmitted</c>. Idempotent.
		///
		/// A prefix, not a postfix: the method's last act is <c>lastEmitTag = Tag.Invalid</c>,
		/// and the tag is what says which species was just emitted. Running before the body also
		/// means the emitted gas is still in <c>Storage</c> (the body's own
		/// <c>ConsumeIgnoringDisease</c> has not run yet), so its pre-exhale temperature is still
		/// readable.
		///
		/// Throws rather than degrading if the member has moved: a prefix that stopped matching
		/// would leave the deletion switched on with no symptom, which is the failure mode this
		/// project has been bitten by before.
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

			MethodInfo onSimEmitted = AccessTools.Method(typeof(ColdBreather), "OnSimEmitted",
				new Type[] { typeof(Sim.MassEmittedCallback) });
			if (onSimEmitted == null)
			{
				throw new InvalidOperationException(
					"ColdBreatherHeat.Install could not find "
					+ "ColdBreather.OnSimEmitted(Sim.MassEmittedCallback). The game build has "
					+ "moved; re-check the member against the current game assembly. This throws rather "
					+ "than degrading, because degrading here means silently leaving a "
					+ "heat-deletion channel switched on.");
			}
			if (lastEmitTagField == null)
			{
				throw new InvalidOperationException(
					"ColdBreatherHeat.Install could not find ColdBreather.lastEmitTag.");
			}

			harmony.Patch(onSimEmitted, new HarmonyMethod(
				AccessTools.Method(typeof(ColdBreatherHeat), "OnSimEmittedPrefix")));

			installed = true;
		}

		// ------------------------------------------------------------------ the patch
		//
		// Not attributed, for the same reason PowerHeat's and TurbineHeat's are not: OniFramework
		// must never patch anything merely by being loaded, and an attributed class would be
		// swept up by a consumer's Harmony.PatchAll(assembly).

		/// <summary>
		/// Books the heat one successful exhale removed, and deposits it into the plant's body.
		///
		/// Runs for every <c>OnSimEmitted</c> callback; a failed emit (<c>suceeded != 1</c>) or a
		/// callback with no live emit tag is ignored, so this fires exactly once per exhale that
		/// actually moved mass.
		/// </summary>
		private static void OnSimEmittedPrefix(ColdBreather __instance, Sim.MassEmittedCallback info)
		{
			try
			{
				if (info.suceeded != 1 || info.mass <= 0f)
				{
					return;
				}
				object tagObj = lastEmitTagField.GetValue(__instance);
				Tag tag = tagObj is Tag ? (Tag)tagObj : Tag.Invalid;
				if (!tag.IsValid)
				{
					return;
				}

				Element element = ElementLoader.elements != null
					&& info.elemIdx < ElementLoader.elements.Count
					? ElementLoader.elements[info.elemIdx]
					: null;
				if (element == null || element.specificHeatCapacity <= 0f)
				{
					return;
				}

				// The pre-exhale temperature of the gas, read from the plant's own storage while
				// it is still there. ElementConsumer stores each consumed lump at the temperature
				// it was drawn in at (AddGasChunk with consumed_info.temperature), mass-weighted
				// across lumps, so this is genuinely the temperature of the gas being emitted.
				Storage storage = __instance.GetComponent<Storage>();
				PrimaryElement stored = storage != null
					? storage.FindPrimaryElement(element.id) : null;
				if (stored == null)
				{
					// Storage already emptied by a race, or the tag names a species no longer
					// present. Without the inbound temperature the removed energy cannot be
					// computed; count the exhale as unplaced rather than guess.
					UnregisteredExhales++;
					TickCount++;
					return;
				}

				float inboundTemperatureK = stored.Temperature;

				// Vanilla's own clamp, reproduced exactly (ColdBreather.Exhale): the
				// emit temperature never falls below lowTemp + 5, so near a species' condensation
				// point the real delta is less than the nominal five degrees.
				float outboundTemperatureK = Mathf.Max(element.lowTemp + 5f,
					inboundTemperatureK + __instance.deltaEmitTemperature);
				float deltaK = inboundTemperatureK - outboundTemperatureK;
				if (deltaK <= 0f)
				{
					// The gas was already at or below its floor; vanilla removed no heat, so
					// there is nothing to place. Still a real exhale, so it is counted.
					TickCount++;
					return;
				}

				// kDTU == kJ in ONI's units, matching HeatFromCoolingSteam and every ProduceEnergy
				// caller: mass in kg, specificHeatCapacity in DTU/(g*K) which is kDTU/(kg*K).
				float removedKJ = info.mass * element.specificHeatCapacity * deltaK;
				float deliverKJ = removedKJ * rejectionFraction;

				VanillaWouldHaveDeletedKJ += removedKJ;
				TickCount++;

				SimTemperatureTransfer body = __instance.GetComponent<SimTemperatureTransfer>();
				int handle = body != null ? body.SimHandle : -1;
				if (!Sim.IsValidHandle(handle))
				{
					// The plant's element chunk has not finished registering. Nothing to deliver
					// into yet; the heat is deleted this tick exactly as in vanilla, and the
					// counter says so.
					UnregisteredExhales++;
					StillDeletedKJ += removedKJ;
					return;
				}

				// Positive delta raises the chunk's temperature: next = deltaKJ / heat_capacity +
				// temperature (the SimDLL's ModifyChunkEnergy). The chunk then
				// conducts into the plant's cell through vanilla StepElementChunks.
				SimMessages.ModifyElementChunkEnergy(handle, deliverKJ);
				DeliveredKJ += deliverKJ;
				StillDeletedKJ += removedKJ - deliverKJ;
			}
			catch (Exception e)
			{
				// A plant must never fail to breathe because the accounting threw. Vanilla's
				// answer -- delete the heat -- is the safe fallback: it is the defect this class
				// exists to fix, but it is the behaviour the game already shipped with.
				Debug.LogWarning("[OniFramework] ColdBreatherHeat.OnSimEmittedPrefix skipped an "
					+ "exhale after an exception: " + e);
			}
		}
	}
}
