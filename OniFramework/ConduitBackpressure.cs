using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Redefines vanilla's <c>pipesHaveRoom</c> gate from "the output tile is EMPTY" to "the
	/// output tile HAS ROOM", which is what its name has always claimed and what a
	/// continuous-flow fluid model requires.
	///
	/// THE VANILLA RULE, and why it is right for vanilla. Every building with an output conduit
	/// type gets a <c>RequireOutputs</c> from <c>BuildingLoader.CreateBuildingComplete</c>. Its
	/// test is not what the flag name suggests:
	///
	/// <code>
	/// private bool OutputPipeIsEmpty() {
	///     if (ignoreFullPipe) return true;
	///     bool result = true;
	///     if (connected) result = GetConduitFlow().IsConduitEmpty(utilityCell);
	///     return result;
	/// }
	/// // ConduitFlow.IsConduitEmpty(cell) => grid[cell].contents.mass &lt;= 0f
	/// </code>
	///
	/// <c>pipesHaveRoom</c> is a <c>Flag.Type.Requirement</c>, so a false there makes the building
	/// non-operational outright. In vanilla that is correct: a vanilla consumer runs at
	/// <c>consumptionRate = float.PositiveInfinity</c> with <c>alwaysConsume</c> and drains the
	/// whole segment every frame, so the output tile genuinely does return to empty between
	/// batches, and "empty" is a fine proxy for "the line is keeping up".
	///
	/// WHY IT CANNOT WORK HERE. This platform's fluid model keeps pipes deliberately non-empty --
	/// that is the entire point of it. A tank plumbed to a pipe with no valve between them is one
	/// connected system, so <c>GasMixtureFacade.EqualizeLiquidVolumeMass</c> converges the two
	/// toward equal FILL FRACTION rather than draining one into the other. With a 5 m^3 tank
	/// against a 0.02 m^3 pipe the tile keeps a proportional share forever: the convergence is
	/// geometric, so the mass falls and never reaches zero. Vanilla's test can therefore never
	/// pass, and the producer upstream is held non-operational for good.
	///
	/// MEASURED on a rig feeding two Steam Turbines: both went ACTIVE for one sample and then sat
	/// at <c>op=False</c> for the rest of the run with all three of
	/// <c>SteamTurbine</c>'s own refusals false. The flag dump named <c>pipesHaveRoom</c>, and the
	/// pipe dump said why -- five distinct conduits, five distinct indices, identical mass and
	/// identical temperature, decaying toward a floor around 0.04 kg and never to zero:
	///
	/// <code>
	/// (20,16)i0=0.887kg Water @375K  (21,16)i1=0.887kg  (22,16)i2=0.887kg
	/// (23,16)i3=0.887kg  (24,16)i4=0.887kg  -> reservoir Storage 0.000 kg
	/// </code>
	///
	/// WHY NOT <c>ignoreFullPipe</c>. Vanilla's own escape hatch switches the gate off entirely,
	/// and that throws away the half of it that is still doing real work. The flag has two jobs:
	/// stop a producer between batches (wrong here) and stop a producer whose line is genuinely
	/// backed up (right everywhere). Setting <c>ignoreFullPipe</c> deletes both, and a producer on
	/// a saturated line then keeps producing into its own <c>Storage</c>, which <c>AddLiquid</c>
	/// never clamps. Patching per building would be whack-a-mole against a list that includes every Desalinator, Oil Refinery and Water Sieve
	/// the moment it is plumbed into one of these networks.
	///
	/// WHAT REPLACES IT. The same question ONI's own flow solver asks before it moves anything
	/// into a tile: <c>ConduitContents.GetEffectiveCapacity(MaxMass) &gt; 0</c>. That is not a
	/// threshold invented here -- it is the exact expression <c>ConduitFlow.UpdateConduit</c> uses
	/// at its own accept test. A producer runs while the tile it feeds can take more and stops
	/// when it cannot, which is real backpressure and is what the flag name says.
	///
	/// SOLID CONDUITS ARE LEFT ON VANILLA'S RULE. A solid conduit carries discrete packages
	/// through <c>SolidConduitFlow</c>, a different class with a different notion of occupancy,
	/// and nothing in this platform's fluid model touches it. "Empty" remains the right test
	/// there, so this changes nothing for a Conveyor Loader.
	///
	/// <c>ignoreFullPipe</c> still wins where a caller has set it: this only ever turns a false
	/// into a true, never the other way, so a building vanilla would have run is never stopped.
	/// </summary>
	public static class ConduitBackpressure
	{
		private static bool installed;

		// ConduitFlow.MaxMass is private and is handed in at construction (1 kg for gas, 10 kg for
		// liquid, from Game.OnPrefabInit). Read from the live instance rather than hardcoded, so a
		// future build that retunes either number is followed rather than contradicted.
		//
		// CACHED PER FLOW INSTANCE, AND THAT IS NOT AN OPTIMISATION DETAIL. `OutputPipeIsEmpty`
		// is reached from `RequireOutputs.UpdatePipeState`, which is registered as a conduit
		// updater, so it runs for EVERY building with an output conduit on EVERY conduit tick. A
		// reflective `FieldInfo.GetValue` there also boxes a float on every one of those calls.
		// The first version of this class did exactly that and the game visibly lagged; the value
		// is fixed for the life of the flow, so it is read once per instance instead.
		private static FieldInfo maxMassField;
		private static ConduitFlow cachedGasFlow;
		private static float cachedGasMaxMass;
		private static ConduitFlow cachedLiquidFlow;
		private static float cachedLiquidMaxMass;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool IsInstalled => installed;

		/// <summary>
		/// Patches <c>RequireOutputs.OutputPipeIsEmpty</c> so a gas or liquid output tile with
		/// capacity left counts as having room.
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

			MethodInfo outputPipeIsEmpty = AccessTools.Method(typeof(RequireOutputs),
				"OutputPipeIsEmpty", Type.EmptyTypes);
			if (outputPipeIsEmpty == null)
			{
				throw new InvalidOperationException(
					"ConduitBackpressure.Install could not find "
					+ "RequireOutputs.OutputPipeIsEmpty(). Without it every producer feeding one of "
					+ "this platform's networks is held non-operational forever by a gate that can "
					+ "never pass, so this throws rather than degrading silently.");
			}

			maxMassField = AccessTools.Field(typeof(ConduitFlow), "MaxMass");
			if (maxMassField == null)
			{
				throw new InvalidOperationException(
					"ConduitBackpressure.Install could not find ConduitFlow.MaxMass. Without it "
					+ "there is no capacity to compare a tile against and the replacement gate "
					+ "would have to invent one, so this throws rather than guessing.");
			}

			harmony.Patch(outputPipeIsEmpty, null, new HarmonyMethod(
				AccessTools.Method(typeof(ConduitBackpressure), "OutputPipeIsEmptyPostfix")));
			installed = true;
		}

		private static void OutputPipeIsEmptyPostfix(ref bool __result, bool ___connected,
			int ___utilityCell, ConduitType ___conduitType)
		{
			// Only ever turns a refusal into an acceptance. Vanilla already said yes when the
			// tile is empty, when nothing is connected, or when ignoreFullPipe is set.
			if (__result || !___connected)
			{
				return;
			}
			if (___conduitType != ConduitType.Gas && ___conduitType != ConduitType.Liquid)
			{
				return;
			}
			__result = HasRoom(___conduitType, ___utilityCell);
		}

		/// <summary>
		/// Whether a gas or liquid conduit tile can accept any more mass, asked exactly the way
		/// <c>ConduitFlow</c> asks it of itself.
		/// </summary>
		public static bool HasRoom(ConduitType conduitType, int cell)
		{
			ConduitFlow flow = FlowFor(conduitType);
			if (flow == null || maxMassField == null || !Grid.IsValidCell(cell))
			{
				return false;
			}
			if (!flow.HasConduit(cell))
			{
				return false;
			}
			float maxMass = MaxMassFor(conduitType, flow);
			if (maxMass <= 0f)
			{
				return false;
			}
			return flow.GetContents(cell).GetEffectiveCapacity(maxMass) > 0f;
		}

		/// <summary>
		/// This flow's maximum tile mass, read reflectively once per instance and then cached.
		/// The instance identity is the cache key rather than the conduit type alone, so
		/// re-entering a world -- which builds fresh <c>ConduitFlow</c> objects -- re-reads
		/// rather than serving a stale number from the previous world.
		/// </summary>
		private static float MaxMassFor(ConduitType conduitType, ConduitFlow flow)
		{
			if (conduitType == ConduitType.Gas)
			{
				if (!ReferenceEquals(cachedGasFlow, flow))
				{
					cachedGasFlow = flow;
					cachedGasMaxMass = (float)maxMassField.GetValue(flow);
				}
				return cachedGasMaxMass;
			}
			if (!ReferenceEquals(cachedLiquidFlow, flow))
			{
				cachedLiquidFlow = flow;
				cachedLiquidMaxMass = (float)maxMassField.GetValue(flow);
			}
			return cachedLiquidMaxMass;
		}

		private static ConduitFlow FlowFor(ConduitType conduitType)
		{
			if (Game.Instance == null)
			{
				return null;
			}
			switch (conduitType)
			{
				case ConduitType.Gas:
					return Game.Instance.gasConduitFlow;
				case ConduitType.Liquid:
					return Game.Instance.liquidConduitFlow;
				default:
					return null;
			}
		}
	}
}
