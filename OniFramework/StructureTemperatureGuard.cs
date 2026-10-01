using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Stops a freshly built building from latching ABSOLUTE ZERO as its own temperature.
	///
	/// THE RACE, from Klei's own code rather than from a theory about it.
	/// <c>StructureTemperatureComponents.SimRegister</c> hands the sim a building through
	/// <c>SimMessages.AddBuildingHeatExchange</c> and gets its handle back ASYNCHRONOUSLY, through
	/// <c>Game.Instance.simComponentCallbackManager</c>. From the moment that handle becomes valid,
	/// <c>OnGetTemperature</c> stops reading the managed <c>PrimaryElement.InternalTemperature</c>
	/// and starts reading <c>Game.Instance.simData.buildingTemperatures[i].temperature</c> -- which
	/// is still ZERO until the sim publishes its first frame for that handle.
	///
	/// A read of zero would be harmless on its own. It is not harmless, because
	/// <c>OnSpawn</c> subscribes every ACTIVE building (one with
	/// <c>SelfHeatKilowattsWhenActive</c> or exhaust, so every generator and every turbine) to
	/// event 824508782, and that handler is:
	///
	///     payload.primaryElement.InternalTemperature = payload.Temperature;
	///
	/// -- a write BACK into the managed field, from the getter above. If the building's first
	/// active-state change lands inside that window, the zero is copied into
	/// <c>InternalTemperature</c> and STAYS there: the next <c>Sim200ms</c> sees the payload dirty
	/// and <c>UpdateSimState</c> pushes the zero out to the sim as the building's real temperature.
	///
	/// WHY THIS IS NEW WITH THE RIG CANVAS, which is what named it. In ordinary play a building is
	/// constructed by a duplicant and then waits -- for power, for an operator, for a supply -- so
	/// its first activation is many frames after its sim registration and the window has long
	/// closed. On a canvas every building is stamped complete, wired, fed and already surrounded by
	/// 573 K steam, so it can go active on the first tick after it spawns. The rig makes the race
	/// winnable in a way normal play does not.
	///
	/// MEASURED, and it is bimodal rather than noisy. TURBINEFED, same build, six runs: four came
	/// up <c>internal=293.15 K</c> and passed 26/0, two came up <c>internal=0.00 K</c> for BOTH
	/// arms and failed 20/2 with the turbine body reading 28.91 K and climbing at exactly the same
	/// 8.75 K per 2 s as a good run -- the whole curve shifted down by 293.14 K, which is the
	/// canvas's own build temperature. Nothing else differed.
	///
	/// THE FIX IS THE SMALLEST TRUE STATEMENT: a building whose sim temperature has not been
	/// published yet is not at absolute zero, it is at the temperature it was built at. So when the
	/// sim reports exactly zero and the managed field holds something real, the managed field is
	/// the answer. That is enough to break the chain -- the write-back copies 293.15 instead of 0
	/// and there is nothing to latch.
	///
	/// It deliberately does NOT try to repair a building that has already latched a zero. Repair
	/// would mean writing a temperature over one the sim may legitimately own by then, and the race
	/// is closed at its source here.
	///
	/// SCOPED TO CANVASES. The patch is installed with <see cref="BlueprintWorld"/> and does
	/// nothing unless a blueprint world is <see cref="BlueprintWorld.Active"/>. The race exists in
	/// vanilla too, but it has not been measured there, and a framework that silently changes
	/// temperature reporting in an ordinary colony is not something this project should ship on the
	/// strength of a rig.
	/// </summary>
	public static class StructureTemperatureGuard
	{
		private static bool installed;

		/// <summary>How many times the guard has actually substituted. Zero means the race never
		/// fired on this run, which is the common case and worth being able to tell apart from
		/// "the patch is not installed".</summary>
		public static int Substitutions { get; private set; }

		private static readonly HashSet<int> reported = new HashSet<int>();

		/// <summary>
		/// Patches <c>StructureTemperatureComponents.OnGetTemperature</c>. Idempotent, and inert
		/// until a canvas is active.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (installed || harmony == null)
			{
				return;
			}
			installed = true;

			MethodInfo target = AccessTools.Method(typeof(StructureTemperatureComponents),
				"OnGetTemperature", new System.Type[] { typeof(PrimaryElement) });
			if (target == null)
			{
				Debug.LogWarning("[OniFramework] StructureTemperatureGuard: "
					+ "StructureTemperatureComponents.OnGetTemperature(PrimaryElement) not found, "
					+ "so a building on a canvas can still latch 0 K as its temperature if it goes "
					+ "active before the sim publishes its first frame. See the class comment.");
				return;
			}

			harmony.Patch(target,
				postfix: new HarmonyMethod(AccessTools.Method(typeof(StructureTemperatureGuard),
					nameof(SubstituteUnpublished))));
			Debug.Log("[OniFramework] StructureTemperatureGuard installed; a canvas building whose "
				+ "sim temperature has not been published yet reports its build temperature "
				+ "instead of absolute zero.");
		}

		/// <summary>
		/// Exactly zero from the sim means "no frame published for this handle yet", not "this
		/// building is at absolute zero" -- nothing in ONI is, and the sim's array is simply still
		/// at its allocated default. The managed field is only preferred when it holds something
		/// real, so a genuine zero on both sides is left alone rather than invented over.
		/// </summary>
		private static void SubstituteUnpublished(PrimaryElement primary_element, ref float __result)
		{
			if (__result != 0f || primary_element == null || BlueprintWorld.Active == null)
			{
				return;
			}

			float managed = primary_element.InternalTemperature;
			if (managed <= 0f)
			{
				return;
			}

			__result = managed;
			Substitutions++;

			// One line per building, not per call: this runs from a getter that the active-state
			// write-back, Sim200ms and every UI hover all reach, and an unbounded log here would be
			// the second warning flood this rig has produced.
			int id = primary_element.GetInstanceID();
			if (reported.Add(id))
			{
				Debug.Log("[OniFramework] StructureTemperatureGuard: '"
					+ primary_element.name + "' asked for its temperature before the sim had "
					+ $"published one; answered {managed:F2} K from its build temperature rather "
					+ "than 0 K. Left unanswered this is latched permanently -- see the class "
					+ "comment for the run that measured it.");
			}
		}
	}
}
