using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OniFramework
{
	/// <summary>
	/// One building's thermal profile: how well its body exchanges with the cells around it,
	/// and how well it radiates to the environment. Registered per prefab with
	/// <see cref="Convection.RegisterPrefab"/>.
	///
	/// Both pairs are Stationeers' own: a factor and a surface area in square metres, whose
	/// PRODUCT is what the flux is proportional to. An area of 0 means "the building's own
	/// footprint", which is what almost every building wants and what the sim assumes.
	/// </summary>
	public struct BuildingThermalProfile
	{
		/// <summary>Stationeers' <c>ConvectionFactor</c>. 0 leaves the body-to-cell term off.</summary>
		public float ConvectionFactor;

		/// <summary>Convecting area, m². 0 means the building's own footprint.</summary>
		public float ConvectionAreaM2;

		/// <summary>
		/// How far the convection reaches, in cells. 0 -- the default, and what an ordinary
		/// building gets -- is the footprint alone: a tile device. Anything higher spreads the
		/// same total conductance over every open cell within that many cells, which is what
		/// makes a radiator a ROOM device.
		///
		/// It is not a strength setting; it is the difference between heating a tile and heating
		/// a room. A building pushing kilowatts into one cell pins that cell within a tick, after
		/// which ONI's own cell-to-cell conduction is what limits it and the building's own
		/// numbers stop mattering: measured, a radiator at Stationeers' factor shed
		/// 1.03x what a plain Radiant Pipe did, and a HUNDREDFOLD factor still only reached
		/// 1.14x. Capped by the sim at <see cref="MaxReachCells"/>.
		/// </summary>
		public int ConvectionReachCells;

		/// <summary>
		/// Emissivity, 0..1. 0 leaves radiation off for this prefab -- which, when
		/// <see cref="Radiation.Install"/> has run, is a deliberate statement rather than a
		/// default: a prefab with a profile is no longer given the global emissivity.
		/// </summary>
		public float RadiationFactor;

		/// <summary>Radiating area, m². 0 means the building's own footprint.</summary>
		public float RadiationAreaM2;
	}

	/// <summary>
	/// Convection between a building's body and the cells it occupies, and the per-prefab
	/// thermal profiles that drive it and <see cref="Radiation"/> together.
	///
	/// PORTED FROM STATIONEERS, from <c>AtmosphereHelper.CalculateConvection</c>:
	///
	/// <code>
	/// heat = 100 * (T_internal - T_world) * surfaceArea
	///        * world.HeatExchangeRatio() * internal.HeatExchangeRatio()
	///        * TickSpeedSeconds * convectionFactor;
	/// </code>
	///
	/// The sim does the whole of it (<c>sim/buildings.h</c>): the body's own ratio is 1,
	/// because a machine casing is a solid; each footprint cell contributes its own
	/// <c>HeatExchangeRatio()</c> -- <see cref="PipeHeatExchange"/>'s
	/// <c>max(clamp01(P / 1 atm), clamp01(liquid volume ratio / 0.001))</c> -- and the area is
	/// split evenly across the cells taking part, which is the footprint alone unless the profile
	/// asks for a reach. The transfer is limited to the pair's equilibrium, as
	/// Stationeers' own <c>GasMixture.TransferEnergyTo</c> limits every exchange, so it moves
	/// heat and can never push either side past the other.
	///
	/// WHY THIS EXISTS AT ALL, when ONI already conducts between a building and its cells, and
	/// WHAT MEASURING IT CHANGED. The case made for this term was that Klei's conduction reaches a
	/// gas cell through the CELL's material conductivity, which is tiny for a gas -- an estimate
	/// of ~0.2 W/K for a copper Radiant Pipe in oxygen against Stationeers' 105 W/K. THE ESTIMATE
	/// WAS WRONG, and the live RADIATOR rig said so on its first run: a Radiant Pipe sheds about
	/// 60 W/K per tile into a 1 kg/cell room, and a radiator at Stationeers' own factor managed
	/// 63 W/K -- 3%. Raising the factor a hundredfold reached 1.14x and no further.
	///
	/// The reason is not the coefficient. A building can only put heat into cells, and one cell
	/// under a kilowatt reaches the body's own temperature within a tick or two; after that what
	/// limits the exchange is how fast ONI's cell-to-cell conduction drains that cell. Both pipes
	/// had pinned their own tile and were waiting on the same room.
	///
	/// So what makes a radiator a radiator here is
	/// <see cref="BuildingThermalProfile.ConvectionReachCells"/>: the same conductance spread over
	/// the open cells AROUND the building instead of the one under it. That is also what
	/// Stationeers does -- its radiators convect with the room's atmosphere, not with a tile. The
	/// formula is Stationeers', in the SimDLL; a radiator exchanges with the room, a pipe with its
	/// own tile only.
	///
	/// IT RUNS ALONGSIDE KLEI'S CONDUCTION, NOT INSTEAD OF IT, except on solid cells, where the
	/// cell ratio is 0 precisely because Klei's sweep already owns that exchange.
	///
	/// ONE EFFECT ON PIPES, and it is why a radiator works in vacuum at all. Under
	/// <see cref="ConduitNetworkPolicy.Convection"/> a pipe's contents-to-pipe exchange is
	/// scaled by the run's ratio AND the conduit cell's. For a building that convects for
	/// itself the cell ratio belongs to this term, so the sim drops it from the conduit leg the
	/// moment the message arrives. Without that, a radiator pipe in vacuum -- cell ratio 0 --
	/// could never take heat off its own contents, and would have nothing to radiate.
	///
	/// OFF BY DEFAULT, in the sim and here, exactly as <see cref="Radiation"/> is.
	/// </summary>
	public static class Convection
	{
		/// <summary>
		/// Stationeers' convection coefficient, the literal 100 in
		/// <c>AtmosphereHelper.GetConvectionHeat</c>, in W/(m² K). A building with a factor of 1
		/// and 1 m² of area exchanges 100 W/K with a cell at full ratio.
		///
		/// The stock value of <see cref="SimTunable.BuildingConvectionWPerM2K"/>; the sim uses the
		/// live row. Read it with
		/// <c>SimTunables.GetFloat(SimTunable.BuildingConvectionWPerM2K, WattsPerSquareMetreKelvin)</c>.
		/// </summary>
		public const float WattsPerSquareMetreKelvin = 100f;

		/// <summary>
		/// The largest reach the sim will honour (<c>ext::kMaxBuildingConvectionReach</c>). A
		/// reach of 4 is 41 cells around a 1x1 building, walked every substep for every building
		/// that convects.
		///
		/// The stock value of <see cref="SimTunable.MaxBuildingConvectionReach"/>, a ceiling: it
		/// may be lowered, never raised, and the sim clamps a building's reach to the live value.
		/// </summary>
		public const int MaxReachCells = 4;

		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		private static readonly Dictionary<string, BuildingThermalProfile> profiles =
			new Dictionary<string, BuildingThermalProfile>();

		private static readonly HashSet<int> sent = new HashSet<int>();

		/// <summary>Buildings whose profile has been pushed, as of the last sweep.</summary>
		public static int TrackedBuildingCount { get { return sent.Count; } }

		/// <summary>Prefabs with a registered profile.</summary>
		public static int RegisteredPrefabCount { get { return profiles.Count; } }

		/// <summary>
		/// Gives every building of one prefab a thermal profile. Call it before any of those
		/// buildings spawns -- mod load time is the natural place -- because a profile is pushed
		/// to each building once, as its sim handle appears.
		/// </summary>
		/// <param name="prefabId">
		/// The building's <c>BuildingDef.PrefabID</c>, i.e. its config's ID string.
		/// </param>
		/// <param name="profile">Its factors and areas. See <see cref="BuildingThermalProfile"/>.</param>
		public static void RegisterPrefab(string prefabId, BuildingThermalProfile profile)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				throw new ArgumentNullException("prefabId");
			}
			profiles[prefabId] = profile;
		}

		/// <summary>
		/// The profile registered for a prefab, if any. <see cref="Radiation"/> asks this before
		/// handing a building the global emissivity, so a prefab with a profile owns both of its
		/// numbers rather than having one of them overwritten by whichever sweep ran last.
		/// </summary>
		public static bool TryGetPrefabProfile(string prefabId, out BuildingThermalProfile profile)
		{
			if (string.IsNullOrEmpty(prefabId))
			{
				profile = default(BuildingThermalProfile);
				return false;
			}
			return profiles.TryGetValue(prefabId, out profile);
		}

		/// <summary>
		/// Starts pushing registered profiles to the buildings that carry them.
		///
		/// A sweep on <c>StructureTemperatureComponents.Sim200ms</c>, riding the same list
		/// <see cref="Radiation"/> and <see cref="PowerHeat"/> ride, because a building's sim
		/// handle does not exist until the sim's registration callback has run. A profile does
		/// not change over a building's life, so each handle is pushed exactly once.
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
			MethodInfo sim200ms = AccessTools.Method(
				typeof(StructureTemperatureComponents), "Sim200ms", new Type[] { typeof(float) });
			if (sim200ms == null)
			{
				throw new InvalidOperationException(
					"Convection.Install could not find StructureTemperatureComponents.Sim200ms(float). "
					+ "Without it no building would ever be given a thermal profile and every "
					+ "radiator would be an ordinary pipe, so this throws rather than degrading.");
			}
			harmony.Patch(sim200ms, null, new HarmonyMethod(
				AccessTools.Method(typeof(Convection), "Sim200msPostfix")));
			installed = true;
		}

		private static void Sim200msPostfix(StructureTemperatureComponents __instance)
		{
			if (profiles.Count == 0)
			{
				return;
			}
			List<StructureTemperatureHeader> headers;
			List<StructureTemperaturePayload> payloads;
			__instance.GetDataLists(out headers, out payloads);

			// Same cache rule as Radiation's: handles carry a recycled version byte, so a
			// destroyed building's entry is never hit again, and clearing when the cache has
			// outgrown the live set costs one redundant resend instead of per-entry liveness.
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
				Building building = payloads[i].building;
				if (building == null || building.Def == null)
				{
					continue;
				}
				BuildingThermalProfile profile;
				if (!profiles.TryGetValue(building.Def.PrefabID, out profile))
				{
					continue;
				}
				sent.Add(handle);
				SetBuildingConvection(handle, profile.ConvectionFactor, profile.ConvectionAreaM2,
					profile.ConvectionReachCells);
				Radiation.SetBuildingRadiation(handle, profile.RadiationFactor,
					profile.RadiationAreaM2);
			}
		}

		/// <summary>
		/// Sets one building's convection factor and area.
		/// </summary>
		/// <param name="simHandle">
		/// The building's sim handle -- <c>StructureTemperaturePayload.simHandleCopy</c>, the
		/// value <c>AddBuildingHeatExchange</c> handed back. Not a cell.
		/// </param>
		/// <param name="convectionFactor">
		/// Stationeers' <c>ConvectionFactor</c>. Zero switches the term off for this building,
		/// and also restores the conduit cell ratio for its pipe, if it is one.
		/// </param>
		/// <param name="surfaceAreaM2">
		/// Convecting area. At or below zero the sim uses the building's own footprint in square
		/// metres.
		/// </param>
		/// <param name="reachCells">
		/// 0 for the footprint alone; higher spreads the same conductance over the open cells
		/// within that many cells of the building. See
		/// <see cref="BuildingThermalProfile.ConvectionReachCells"/>.
		/// </param>
		public static unsafe void SetBuildingConvection(int simHandle, float convectionFactor,
			float surfaceAreaM2, int reachCells)
		{
			if (!global::Sim.IsValidHandle(simHandle))
			{
				return;
			}
			byte[] payload = new byte[16];
			Buffer.BlockCopy(BitConverter.GetBytes(simHandle), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(convectionFactor), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(surfaceAreaM2), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(reachCells), 0, payload, 12, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(
					OniExtMessages.SetBuildingConvection, payload.Length, msg);
			}
		}
	}
}
