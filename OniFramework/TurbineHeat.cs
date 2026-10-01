using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The Steam Turbine's waste heat, given a destination. The largest single act of energy
	/// deletion in vanilla Oxygen Not Included.
	///
	/// WHAT VANILLA DOES. <c>SteamTurbine.EnergySim200ms</c> pumps steam out of its gas storage,
	/// writes the same mass back as water at a fixed <c>outputElementTemperature</c> of 368.15 K,
	/// and computes the energy that removal took as
	/// <c>HeatFromCoolingSteam(steam) * (pumped / mass)</c>. It then hands the building's own body
	/// exactly <c>wasteHeatToTurbinePercent</c> of that -- 0.1 -- and the other 90 % simply ceases
	/// to exist. At the turbine's design point (2 kg/s of 473.15 K steam) the arithmetic is:
	///
	/// <code>
	/// heat removed from the steam   2 * 4.179 * (473.15 - 368.15)   =  877.6 kW
	/// delivered to the building     x 0.1                           =   87.8 kW
	/// DELETED                       x 0.9                           =  789.8 kW
	/// electricity generated         GeneratorWattageRating          =    0.85 kW
	/// </code>
	///
	/// So the building players use as their primary cooling tool converts **0.097 %** of the heat
	/// it removes into work and destroys the rest. For scale, the Carnot limit between those two
	/// temperatures is 22.2 %, or 194.7 kW; and the whole-colony correction
	/// <see cref="ConversionEnthalpy"/> makes on a 234-cycle save is 157 kW, from one turbine's
	/// 790.
	///
	/// The older <c>SteamTurbineConfig</c> (2000 W, 10 kg/s) never assigns
	/// <c>wasteHeatToTurbinePercent</c> at all, so it defaults to zero and deletes 100 %. This
	/// rule ignores the field entirely, so both configs are fixed by the same patch and neither
	/// needs its own case.
	///
	/// WHAT THIS DOES INSTEAD. A heat engine takes Q_hot from a hot reservoir, delivers work W,
	/// and rejects Q_hot - W to a cold one. There is no third destination. So:
	///
	/// <code>
	/// Q_hot   unchanged   the same heat vanilla already removes from the steam
	/// W       unchanged   vanilla's own JoulesToGenerate curve, capped at the wattage rating
	/// Q_cold  Q_hot - W   into the building body, where vanilla put a tenth of it
	/// </code>
	///
	/// Electricity and the output water temperature are both left exactly as Klei wrote them, so
	/// this changes neither the turbine's power output nor its cooling capacity. The only change
	/// is that the heat it removes now exists somewhere afterwards.
	///
	/// WHY THE BUILDING BODY, AND NOT THE OUTPUT WATER. Modelled on Stationeers'
	/// <c>StirlingEngine</c>, whose cold side exchanges with the world atmosphere rather than with
	/// its own output -- <c>AtmosphereHelper.GetConvectionHeat(atmosphere, _coldSideAtmosphere,
	/// ColdSideHeatExchangerArea * atmosphere.HeatExchangeRatio())</c> against
	/// <c>CloneGlobalAtmosphere</c>. The waste heat lands in the room the engine is standing in,
	/// which is the heat-rejection requirement expressed as a building rather than as a rule. The
	/// alternative -- rejecting into the output water -- is thermodynamically stricter and was
	/// considered and rejected: with steam and water both at 4.179 kJ/(kg K) in ONI, the water
	/// would leave at 473.05 K, above its own 372.5 K boiling point, so it would flash straight
	/// back to steam and the turbine would stop condensing anything at all.
	///
	/// AND WHY IT NEEDS NO NEW MECHANISM. <c>SteamTurbine</c> already refuses to run when
	/// the building's own temperature exceeds <c>maxBuildingTemperature</c>, 373.15 K:
	///
	/// <code>
	/// building_too_hot = GetComponent&lt;PrimaryElement&gt;().Temperature &gt; maxBuildingTemperature;
	/// </code>
	///
	/// Vanilla's 87.8 kW rarely makes that gate bind. At 789.8 kW it binds almost at once, and the
	/// turbine self-throttles until the player carries the heat away. The gameplay this rule
	/// creates was already built into the building; it was only ever slack because nine tenths of
	/// the heat was being thrown away before it could reach the gate.
	///
	/// WHAT IS DELIBERATELY NOT DONE HERE. Stationeers' Stirling derives its power from a real
	/// efficiency product -- Carnot times a machine curve times a pressure-differential term times
	/// the working gas's own <c>ThermalEfficiency()</c> (0.03 to 0.18). Porting that would change
	/// the turbine's power output, and at even 5 % of Carnot it would generate 9.7 kW, making it
	/// the strongest generator in the game and contradicting the decision that the reworked
	/// turbine be *deliberately far less efficient* than the alternatives. The efficiency model is
	/// therefore left alone and only the destination of the waste heat is changed. Revisiting it
	/// is a balance decision, not a correctness one.
	/// </summary>
	public static class TurbineHeat
	{
		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		/// <summary>
		/// Fraction of the unconverted heat that reaches the building body. One by default, which
		/// is the whole point of this class.
		///
		/// It exists as a knob only so a balance run can be made against a partial rejection
		/// without rebuilding, and anything below one is a deliberate re-opening of the deletion
		/// this rule closes -- the missing fraction has no destination, exactly as in vanilla.
		/// <see cref="DeletedKJ"/> counts what a setting below one has thrown away, so a run that
		/// uses this cannot quietly forget it.
		/// </summary>
		public static float RejectionFraction
		{
			get { return rejectionFraction; }
			set { rejectionFraction = Mathf.Clamp01(value); }
		}

		private static float rejectionFraction = 1f;

		// ------------------------------------------------------------------ accounting

		/// <summary>Heat delivered to turbine bodies by this rule since load, kJ.</summary>
		public static double RejectedKJ { get; private set; }

		/// <summary>
		/// Work the turbines took out as electricity over the same period, kJ. Subtracted from the
		/// rejection rather than delivered, because it left the building as power.
		/// </summary>
		public static double ElectricalKJ { get; private set; }

		/// <summary>
		/// What vanilla would have deleted over the same period, kJ -- the nine tenths this rule
		/// exists to keep. With <see cref="RejectionFraction"/> at its default of one, this is the
		/// measure of the rule's effect; below one, the shortfall is deleted for real and is
		/// counted in <see cref="StillDeletedKJ"/> as well.
		/// </summary>
		public static double VanillaWouldHaveDeletedKJ { get; private set; }

		/// <summary>
		/// Heat this rule itself failed to place, kJ. Always zero at the default
		/// <see cref="RejectionFraction"/> of one.
		/// </summary>
		public static double StillDeletedKJ { get; private set; }

		/// <summary>Turbine ticks priced since load.</summary>
		public static long TickCount { get; private set; }

		/// <summary>Reset the running totals.</summary>
		public static void ResetAccounting()
		{
			RejectedKJ = 0.0;
			ElectricalKJ = 0.0;
			VanillaWouldHaveDeletedKJ = 0.0;
			StillDeletedKJ = 0.0;
			TickCount = 0;
		}

		// ------------------------------------------------------------------ the rule

		/// <summary>
		/// The energy to hand the turbine's body this tick, in kilojoules. Replaces vanilla's
		/// <c>num2 * wasteHeatToTurbinePercent</c>.
		/// </summary>
		/// <param name="fullHeatKJ">Vanilla's <c>num2</c>: the whole energy removed from the steam
		/// this tick, kJ. Equal by construction to the difference between the steam taken out of
		/// gas storage and the water put into liquid storage, so accounting against it is exact
		/// rather than approximate.</param>
		/// <param name="turbine">The turbine, for its own <c>wasteHeatToTurbinePercent</c> --
		/// read only to report what vanilla would have done.</param>
		/// <param name="electricityJoules">Vanilla's <c>value</c>: the work generated this tick,
		/// in <b>joules</b>. <c>ProduceEnergy</c> takes kilojoules, so this is the one place a
		/// factor of a thousand is applied, and getting it backwards would be a 1000x error in the
		/// smaller of the two terms rather than an obvious one.</param>
		public static float RejectedEnergyKJ(float fullHeatKJ, SteamTurbine turbine,
			float electricityJoules)
		{
			try
			{
				// A turbine that pumped nothing has nothing to place. Guarding on the sign as well
				// as on zero because HeatFromCoolingSteam is a signed delta and steam colder than
				// outputElementTemperature would make it negative -- vanilla's own minActiveTemperature
				// of 398.15 K should prevent that, but the gate is on the steam and this is not.
				if (!(fullHeatKJ > 0f))
				{
					return 0f;
				}

				float workKJ = electricityJoules * 0.001f;
				if (workKJ < 0f)
				{
					workKJ = 0f;
				}
				// Work cannot exceed the heat it came from. Vanilla computes the two from separate
				// curves -- JoulesToGenerate is linear in temperature and capped at the wattage
				// rating, while the heat is the real enthalpy difference -- so nothing in Klei's
				// code guarantees the inequality. At the design point work is a thousandth of the
				// heat, but a modded turbine with a large rating and a small pump rate could invert
				// it, and the result would be a negative rejection, which is a cooling ray.
				if (workKJ > fullHeatKJ)
				{
					workKJ = fullHeatKJ;
				}

				float unconvertedKJ = fullHeatKJ - workKJ;
				float rejectedKJ = unconvertedKJ * rejectionFraction;

				float vanillaFraction = turbine == null ? 0f : turbine.wasteHeatToTurbinePercent;
				RejectedKJ += rejectedKJ;
				ElectricalKJ += workKJ;
				VanillaWouldHaveDeletedKJ += fullHeatKJ * (1f - vanillaFraction);
				StillDeletedKJ += unconvertedKJ - rejectedKJ;
				TickCount++;

				return rejectedKJ;
			}
			catch (Exception e)
			{
				// A turbine must never fail to run because the accounting threw. Vanilla's answer
				// is the safe fallback: it deletes energy, which is the defect this class exists to
				// fix, but it is the behaviour the game already shipped with.
				Debug.LogWarning("[OniFramework] TurbineHeat.RejectedEnergyKJ fell back to vanilla "
					+ "for this tick: " + e);
				return turbine == null ? fullHeatKJ : fullHeatKJ * turbine.wasteHeatToTurbinePercent;
			}
		}

		// ------------------------------------------------------------------ installation

		/// <summary>
		/// Patch <c>SteamTurbine.EnergySim200ms</c>. Idempotent.
		///
		/// One transpiler replacing one multiplication. Everything else in the method -- the
		/// operational flags, the disease transfer, the mass bookkeeping, the accumulator, the
		/// meter -- is left exactly as Klei wrote it, which is why this is a transpiler and not a
		/// reimplementation.
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
			MethodInfo tick = AccessTools.Method(
				typeof(SteamTurbine), "EnergySim200ms", new Type[] { typeof(float) });
			if (tick == null)
			{
				throw new InvalidOperationException(
					"TurbineHeat.Install could not find SteamTurbine.EnergySim200ms(float). The "
					+ "game build has moved; re-check the member against the current game assembly. This "
					+ "throws rather than degrading, because degrading here means silently leaving "
					+ "the largest energy deletion in the game switched on.");
			}
			harmony.Patch(tick, null, null,
				new HarmonyMethod(AccessTools.Method(typeof(TurbineHeat), "Transpiler")));
			installed = true;
		}

		/// <summary>
		/// Replace <c>num2 * wasteHeatToTurbinePercent</c> with a call to
		/// <see cref="RejectedEnergyKJ"/>.
		///
		/// ANCHORED ON AN INSTRUCTION SHAPE, NEVER ON AN INDEX. The sequence is
		/// <c>ldarg.0 / ldfld wasteHeatToTurbinePercent / mul</c>, and the field is loaded exactly
		/// once in the whole method -- verified against the real IL, where it is IL_0171..IL_0177,
		/// immediately after <c>ldloc.s 5</c> puts vanilla's <c>num2</c> on the stack and
		/// immediately before the <c>ProduceEnergy</c> call consumes the product. Requiring
		/// exactly one match and throwing otherwise is deliberate: a silent no-op here looks
		/// identical to a working install right up until a colony's heat budget is wrong by
		/// 790 kW per turbine.
		///
		/// The electricity is read from the local vanilla stores it in, found by shape as well
		/// rather than by number: the <c>stloc</c> that follows the second
		/// <c>Mathf.Min(float,float)</c> in the method, which is <c>value = Mathf.Min(
		/// JoulesToGenerate(...) * (num / pumpKGRate), WattageRating * dt)</c>. The first
		/// <c>Mathf.Min</c> is the pumped mass and must not be mistaken for it.
		/// </summary>
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);

			FieldInfo percentField =
				AccessTools.Field(typeof(SteamTurbine), "wasteHeatToTurbinePercent");
			if (percentField == null)
			{
				throw new InvalidOperationException(
					"TurbineHeat.Transpiler could not find SteamTurbine.wasteHeatToTurbinePercent.");
			}
			MethodInfo mathfMin = AccessTools.Method(
				typeof(Mathf), "Min", new Type[] { typeof(float), typeof(float) });
			if (mathfMin == null)
			{
				throw new InvalidOperationException(
					"TurbineHeat.Transpiler could not find Mathf.Min(float, float).");
			}

			// --- the electricity local, from the SECOND Mathf.Min(float,float) in the method ----
			CodeInstruction electricityLoad = null;
			int minMatches = 0;
			for (int i = 0; i + 1 < code.Count; i++)
			{
				if (code[i].opcode != OpCodes.Call
					|| !object.Equals(code[i].operand, mathfMin))
				{
					continue;
				}
				minMatches++;
				if (minMatches == 2 && IsStoreLocal(code[i + 1]))
				{
					electricityLoad = LoadForStore(code[i + 1]);
				}
			}
			if (minMatches != 2 || electricityLoad == null)
			{
				throw new InvalidOperationException(
					"TurbineHeat.Transpiler expected exactly two Mathf.Min(float,float) calls in "
					+ "SteamTurbine.EnergySim200ms, the second storing the generated joules, and "
					+ "found " + minMatches + " with "
					+ (electricityLoad == null ? "no" : "a") + " store after the second. The "
					+ "method has been rewritten; re-derive the anchors from the current game assembly.");
			}

			// --- the multiplication to replace -------------------------------------------------
			int site = -1;
			int siteMatches = 0;
			for (int i = 1; i + 1 < code.Count; i++)
			{
				if (code[i].opcode != OpCodes.Ldfld
					|| !object.Equals(code[i].operand, percentField))
				{
					continue;
				}
				if (code[i - 1].opcode != OpCodes.Ldarg_0 || code[i + 1].opcode != OpCodes.Mul)
				{
					continue;
				}
				siteMatches++;
				site = i - 1;
			}
			if (siteMatches != 1)
			{
				throw new InvalidOperationException(
					"TurbineHeat.Transpiler expected exactly one "
					+ "'ldarg.0; ldfld wasteHeatToTurbinePercent; mul' in "
					+ "SteamTurbine.EnergySim200ms and found " + siteMatches
					+ ". Refusing to patch: a partial match here would leave the turbine deleting "
					+ "energy while reporting itself installed.");
			}

			MethodInfo replacement = AccessTools.Method(typeof(TurbineHeat), "RejectedEnergyKJ");
			if (replacement == null)
			{
				throw new InvalidOperationException(
					"TurbineHeat.Transpiler could not find its own RejectedEnergyKJ.");
			}

			List<CodeInstruction> output = new List<CodeInstruction>(code.Count + 1);
			for (int i = 0; i < code.Count; i++)
			{
				if (i < site || i > site + 2)
				{
					output.Add(code[i]);
					continue;
				}
				if (i > site)
				{
					// The ldfld and the mul are replaced wholesale.
					continue;
				}
				// The stack already carries vanilla's num2. Push the turbine and the generated
				// joules after it, then call. Labels and exception blocks are carried over from
				// the instruction being replaced rather than recreated, so any branch that targeted
				// this ldarg.0 still lands on one.
				CodeInstruction self = new CodeInstruction(OpCodes.Ldarg_0);
				self.labels = code[i].labels;
				self.blocks = code[i].blocks;
				output.Add(self);
				output.Add(new CodeInstruction(electricityLoad.opcode, electricityLoad.operand));
				output.Add(new CodeInstruction(OpCodes.Call, replacement));
			}
			return output;
		}

		private static bool IsStoreLocal(CodeInstruction instruction)
		{
			OpCode op = instruction.opcode;
			return op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2
				|| op == OpCodes.Stloc_3 || op == OpCodes.Stloc || op == OpCodes.Stloc_S;
		}

		/// <summary>
		/// The load that reads back what a given store wrote. Built from the store rather than
		/// from a local index so the short forms (<c>stloc.1</c>, which carries no operand at all)
		/// map to their matching loads instead of being reconstructed from a number that the short
		/// form does not actually contain.
		/// </summary>
		private static CodeInstruction LoadForStore(CodeInstruction store)
		{
			OpCode op = store.opcode;
			if (op == OpCodes.Stloc_0) { return new CodeInstruction(OpCodes.Ldloc_0); }
			if (op == OpCodes.Stloc_1) { return new CodeInstruction(OpCodes.Ldloc_1); }
			if (op == OpCodes.Stloc_2) { return new CodeInstruction(OpCodes.Ldloc_2); }
			if (op == OpCodes.Stloc_3) { return new CodeInstruction(OpCodes.Ldloc_3); }
			if (op == OpCodes.Stloc_S) { return new CodeInstruction(OpCodes.Ldloc_S, store.operand); }
			if (op == OpCodes.Stloc) { return new CodeInstruction(OpCodes.Ldloc, store.operand); }
			return null;
		}
	}
}
