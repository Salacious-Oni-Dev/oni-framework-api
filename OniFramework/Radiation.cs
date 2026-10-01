using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OniFramework
{
	/// <summary>
	/// Radiative heat loss from a building body to the environment -- the outlet that stops
	/// <see cref="ExhaustHeat"/>'s bounce, and <see cref="PowerHeat"/>'s derived waste heat,
	/// from simply cooking every machine that sits in a vacuum.
	///
	/// PORTED FROM STATIONEERS, structurally, from <c>AtmosphereHelper.CalculateEntropy</c> /
	/// <c>CalculateThingEntropy</c>:
	///
	/// <code>
	/// if (internal.T &lt;= globalAtmosphere(grid).T) return 0;      // one-way: only ever sheds
	/// Tref  = global.T &lt; 1 ? 50 : global.T;                       // vacuum floor
	/// Tsink = Tref;
	/// if (world != null &amp;&amp; Tref &lt; world.T)                       // local air blends in,
	///     Tsink = Lerp(Tref, world.T, clamp01(world.moles/...));  // weighted by ITS pressure
	/// return Clamp(radiationFactor * surfaceArea * internal.HeatExchangeRatio() * curve(dT),
	///              0, internal.TotalEnergy / 2);                   // overshoot guard
	/// </code>
	///
	/// THE ONE PLACE THIS DELIBERATELY DOES NOT FOLLOW STATIONEERS, and the reason matters.
	/// Stationeers lerps the <i>sink</i> toward the local medium. Reproducing that verbatim was
	/// tried, shipped, and caught by the first live run: the flux becomes
	/// <c>σA(T_body⁴ − T_cell⁴)</c>, which is a <b>second building ↔ cell heat path running
	/// alongside the conduction ONI already does</b>, with the energy leaving the world instead
	/// of reaching the cell. Against a real 234-cycle colony that radiated <b>55750 kJ over
	/// 32 s of sim time against 1380 kJ of exhaust</b> — roughly 1.7 MW of continuous cooling
	/// out of the world, which is exactly the magic heat deletion this work exists to remove.
	///
	/// Stationeers gets away with the lerp because <c>CalculateThingEntropy</c> is its device's
	/// <i>only</i> exchange with the room — its things have no conduction. ONI's buildings do.
	/// So here the medium fraction scales the <b>flux</b> toward zero rather than retargeting
	/// the sink: a full cell is opaque and radiates exactly nothing, a vacuum is transparent
	/// and radiation is the only outlet left. Measured across the range in <c>vftest</c>:
	/// 11.608510 kJ in vacuum against a closed form of 11.6096, <b>exactly 0.000000 kJ</b> in a
	/// full room with the body 100 K hotter than it, and 5.804642 kJ at half fill — precisely
	/// half.
	///
	/// TWO FURTHER DEVIATIONS, from what could not be ported rather than from choice.
	/// Stationeers evaluates the
	/// flux from <c>AtmosphericsManager.EntropyCurve</c>, a hand-authored Unity
	/// <c>AnimationCurve</c> whose keyframes live in a scene asset rather than in code and
	/// therefore cannot be read; the sim uses Stefan-Boltzmann in that slot, which is the law
	/// the curve is a tuned approximation of. And its overshoot clamp is half the body's
	/// <i>total</i> energy, which in ONI's units is enormous and would never bite; the sim
	/// clamps to half the energy <i>above the sink</i>, which is what Stationeers' own
	/// <c>GasMixture.TransferEnergyTo</c> primitive implements elsewhere.
	///
	/// THE FAR SIDE IS A STUB, AND IS DOCUMENTED AS ONE. Here the environment is an infinite
	/// reservoir at a fixed temperature. The energy is still <i>accounted</i> -- the sim charges
	/// it to its own ledger bucket as an outflow, so it is a named boundary crossing with a
	/// receiver rather than a hole -- but the receiver does not warm up. A planetary environment
	/// is what gives it a body whose temperature moves.
	///
	/// STATIONEERS DOES NO BETTER. Its <c>PlanetaryAtmosphereSimulation</c> looks like a real
	/// reservoir that receives the energy (<c>AddEnergy</c>) and gives it back when the gradient
	/// reverses (<c>RemoveEnergy</c>), but both methods begin <c>if (!IsGlobalInteraction ...) return;</c>,
	/// and <c>IsGlobalInteraction</c> is a get-only property returning the literal <c>false</c>
	/// in the shipped build. Its planet discards a radiator's heat exactly as this does. What it
	/// has that this does not is a sink <i>temperature</i> that moves -- driven by solar angle and
	/// by the planet's own phase changes, never by the player's machines. So making the
	/// reservoir finite is something Stationeers wrote the mechanism for and switched off, not a
	/// port.
	///
	/// OFF BY DEFAULT, in the sim and here. Turning it on globally is a policy decision, so it
	/// is an API call rather than a native default.
	/// </summary>
	public static class Radiation
	{
		/// <summary>
		/// Stationeers' own fallback sink temperature when its planetary atmosphere is
		/// effectively absent: <c>CalculateEntropy</c>'s <c>global.T &lt; 1 ? 50 : global.T</c>.
		/// Used as this API's default so the number has a provenance rather than being picked.
		/// </summary>
		public const float DefaultEnvironmentKelvin = 50f;

		/// <summary>
		/// Emissivity applied to a building when nothing more specific is set. 1.0 is a black
		/// body, which is the honest default for a machine casing and, more importantly, the
		/// value whose consequences are checkable against the closed form.
		/// </summary>
		public const float DefaultEmissivity = 1f;

		/// <summary>The environment temperature last pushed, in kelvin. 0 means never set.</summary>
		public static float EnvironmentKelvin { get; private set; }

		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run. False means no building has an
		/// emissivity and a bounced exhaust has no outlet but conduction.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}
		private static float globalEmissivity;
		private static readonly HashSet<int> sent = new HashSet<int>();

		/// <summary>Buildings that have been given an emissivity, as of the last sweep.</summary>
		public static int TrackedBuildingCount { get { return sent.Count; } }

		/// <summary>
		/// Turns radiation on globally: sets the environment temperature, then gives every
		/// building with a structure temperature the same emissivity as it registers.
		///
		/// A sweep on <c>StructureTemperatureComponents.Sim200ms</c>, riding the same list
		/// <see cref="PowerHeat"/> and <see cref="ExhaustHeat"/> ride, because a building's sim
		/// handle does not exist until the sim's registration callback has run -- there is no
		/// single spawn moment at which this could be pushed instead. Emissivity does not vary
		/// over a building's life, so each handle is pushed exactly once.
		/// </summary>
		/// <param name="emissivity">
		/// Applied to every building. <see cref="DefaultEmissivity"/> unless a caller has a
		/// reason; per-building values can still be set afterwards with
		/// <see cref="SetBuildingRadiation"/>.
		/// </param>
		/// <param name="environmentKelvin">
		/// The reservoir temperature. <see cref="DefaultEnvironmentKelvin"/> is Stationeers'
		/// own fallback and is the value with a provenance.
		/// </param>
		public static void Install(Harmony harmony, float emissivity, float environmentKelvin)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (installed)
			{
				return;
			}
			MethodInfo sim200ms = AccessTools.Method(
				typeof(StructureTemperatureComponents), "Sim200ms", new Type[] { typeof(float) });
			if (sim200ms == null)
			{
				throw new InvalidOperationException(
					"Radiation.Install could not find StructureTemperatureComponents.Sim200ms(float). "
					+ "Without it no building would ever be given an emissivity and the exhaust "
					+ "bounce would have no outlet, so this throws rather than degrading.");
			}
			globalEmissivity = emissivity;
			SetEnvironmentTemperature(environmentKelvin);
			harmony.Patch(sim200ms, null, new HarmonyMethod(
				AccessTools.Method(typeof(Radiation), "Sim200msPostfix")));
			installed = true;
		}

		private static void Sim200msPostfix(StructureTemperatureComponents __instance)
		{
			List<StructureTemperatureHeader> headers;
			List<StructureTemperaturePayload> payloads;
			__instance.GetDataLists(out headers, out payloads);

			// Handles carry a recycled version byte, so a destroyed building's entry is never
			// hit again. Clearing when the cache has outgrown the live set costs one redundant
			// resend pass and is simpler than tracking liveness per entry -- the same trade
			// PowerHeat's own cache makes.
			if (sent.Count > payloads.Count * 2 + 64)
			{
				sent.Clear();
			}
			for (int i = 0; i < payloads.Count; i++)
			{
				int handle = payloads[i].simHandleCopy;
				if (!global::Sim.IsValidHandle(handle) || sent.Contains(handle))
				{
					continue;
				}
				// A prefab with a thermal profile of its own owns BOTH of its numbers -- a
				// radiator's emissivity is part of what makes it a radiator, and handing it the
				// global one here would undo that or not, depending on which Sim200ms postfix
				// happened to run last. Convection's sweep pushes those buildings.
				Building building = payloads[i].building;
				BuildingThermalProfile profile;
				if (building != null && building.Def != null &&
					Convection.TryGetPrefabProfile(building.Def.PrefabID, out profile))
				{
					continue;
				}
				sent.Add(handle);
				// Area 0 means "the building's own footprint", which the sim already knows.
				SetBuildingRadiation(handle, globalEmissivity, 0f);
			}
		}

		/// <summary>
		/// Sets the temperature of the reservoir radiating bodies shed into.
		///
		/// <paramref name="kelvin"/> at or below zero restores the sim's default of 0, which
		/// together with an emissivity that is also 0 by default leaves radiation inert.
		/// </summary>
		public static unsafe void SetEnvironmentTemperature(float kelvin)
		{
			EnvironmentKelvin = kelvin > 0f ? kelvin : 0f;
			byte[] payload = new byte[4];
			Buffer.BlockCopy(BitConverter.GetBytes(EnvironmentKelvin), 0, payload, 0, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(
					OniExtMessages.SetEnvironmentTemperature, payload.Length, msg);
			}
		}

		/// <summary>
		/// Sets one building's emissivity and radiating area.
		/// </summary>
		/// <param name="simHandle">
		/// The building's sim handle -- <c>StructureTemperaturePayload.simHandleCopy</c>, the
		/// value <c>AddBuildingHeatExchange</c> handed back. Not a cell.
		/// </param>
		/// <param name="emissivity">0..1. Zero switches radiation off for this building.</param>
		/// <param name="surfaceAreaM2">
		/// Radiating area. At or below zero the sim uses the building's own footprint in
		/// square metres, which is the right answer for almost every building.
		/// </param>
		public static unsafe void SetBuildingRadiation(int simHandle, float emissivity,
			float surfaceAreaM2)
		{
			if (!global::Sim.IsValidHandle(simHandle))
			{
				return;
			}
			byte[] payload = new byte[12];
			Buffer.BlockCopy(BitConverter.GetBytes(simHandle), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(emissivity), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(surfaceAreaM2), 0, payload, 8, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(
					OniExtMessages.SetBuildingRadiation, payload.Length, msg);
			}
		}
	}
}
