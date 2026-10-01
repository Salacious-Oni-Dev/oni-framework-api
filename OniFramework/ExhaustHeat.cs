using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Redirects Klei's <c>StructureTemperatureComponents.ExhaustHeat</c> into the sim, so the
	/// heat it cannot deliver goes into the building's own body instead of being destroyed.
	///
	/// WHY THIS EXISTS. <c>ExhaustHeat</c> is the widest heat-deletion channel in vanilla. It
	/// destroys energy on four separate paths in two assemblies (game build 744825):
	///
	/// <list type="number">
	/// <item><b>Managed, before any message exists.</b> Delivery is scaled by
	/// <c>Mathf.Min(Grid.Mass[cell], 1.5f) / 1.5f</c> and the remainder is dropped. A cell at
	/// 0.2 kg loses 87% of it.</item>
	/// <item><b>Native.</b> A vacuum cell (element state 0) refuses the whole lump.</item>
	/// <item><b>Native.</b> So does a cell under 0.001 kg.</item>
	/// <item><b>Native.</b> The <c>maxTemperature</c> ceiling -- and it is <b>not</b> 10000 K.
	/// <c>StructureTemperatureComponents.OnSpawn</c> sets it from
	/// <c>Overheatable.OverheatTemperature</c>, <c>BuildingDef.Overheatable</c> defaults to
	/// <c>true</c> and <c>BuildingDef.OverheatTemperature</c> defaults to <b>348.15 K</b>, so
	/// an ordinary machine in a room already above 75 C delivers <i>nothing at all</i>.</item>
	/// </list>
	///
	/// That fourth path is the one worth stressing, because it is not the one the work item was
	/// named after. It is measured, not argued: <c>vftest</c>'s middle arm puts a building over
	/// a cell holding a <b>full 20 kg of oxygen</b> -- not a vacuum, not thin -- and vanilla
	/// still delivers zero, because the cell sits above the ceiling.
	///
	/// HOW. The delivery is moved into the sim as a rate (<c>ext::kSetBuildingExhaust</c>). The
	/// arithmetic there is Klei's, reproduced rather than improved -- the same per-cell split,
	/// the same 1.5 kg mass factor, the same ceiling, and it shares one function with the
	/// <c>ModifyCellEnergy</c> handler so the two cannot drift apart. The only change is where
	/// the undelivered remainder ends up: the building body, which is the same destination
	/// <see cref="PowerHeat"/> uses, and which <see cref="Radiation"/> then gives an outlet.
	///
	/// WHY A RATE RATHER THAN LETTING THE MESSAGES THROUGH. Two reasons, both measured:
	/// vanilla sends one <c>ModifyCellEnergy</c> per covered cell per 200 ms tick, which is
	/// hundreds of messages a frame in a real colony; and it computes the lump against the
	/// <b>managed</b> clock, which the first live run of the power -> heat rule proved does not
	/// agree with the sim's -- 34.2 s of substep time against 21.1 s of <c>GameClock</c> over
	/// one window. A rate the sim integrates over its own substeps has neither problem.
	///
	/// REQUIRES THE CUSTOM SIMDLL. On a stock DLL the extension message is dropped and the
	/// prefix would suppress vanilla's delivery with nothing taking its place, which would be
	/// strictly worse than doing nothing. <see cref="Install"/> therefore refuses to install
	/// unless the sim answers.
	/// </summary>
	public static class ExhaustHeat
	{
		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run. False means vanilla still owns the
		/// delivery, and its four deletion paths are all still in place.</summary>
		public static bool IsInstalled
		{
			get { return installed; }
		}

		/// <summary>
		/// Sweeps a handle's rate to zero once it has gone this many consecutive 200 ms sweeps
		/// without vanilla asking to exhaust anything.
		///
		/// Not 1. <c>ExhaustHeat</c> has two callers on two different cadences --
		/// <c>StructureTemperatureComponents.Sim200ms</c>, which lines up with this sweep
		/// exactly, and <c>SpaceHeater.GenerateHeat</c>, which is a state-machine
		/// <c>.Update</c> and does not. Zeroing on the first missed sweep would make a space
		/// heater's exhaust flicker on and off. Five sweeps is one second, which is far longer
		/// than any update gap and far shorter than a player would notice.
		/// </summary>
		private const int SweepsBeforeZero = 5;

		private sealed class Tracked
		{
			public float Kilowatts;
			public float MaxTemperature;
			public int SweepsUnseen;
		}

		private static readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();

		/// <summary>Buildings currently exhausting, as of the last 200 ms sweep.</summary>
		public static int LastSweepCount { get; private set; }

		/// <summary>Total kilowatts of exhaust in force across every tracked building.</summary>
		public static float LastSweepKilowatts { get; private set; }

		/// <summary>Sweeps completed since load. Zero means the patch is not firing at all.</summary>
		public static long SweepCount { get; private set; }

		/// <summary>
		/// Installs the redirect. Idempotent.
		///
		/// Throws rather than degrading if either member has moved: a prefix that stopped
		/// matching would put vanilla's deletion back silently, and a postfix that stopped
		/// matching would leave stale rates running forever. Both are failures with no symptom.
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

			MethodInfo exhaust = AccessTools.Method(
				typeof(StructureTemperatureComponents), "ExhaustHeat",
				new Type[] { typeof(Extents), typeof(float), typeof(float), typeof(float) });
			if (exhaust == null)
			{
				throw new InvalidOperationException(
					"ExhaustHeat.Install could not find "
					+ "StructureTemperatureComponents.ExhaustHeat(Extents, float, float, float). "
					+ "The game build has moved; vanilla's heat deletion would stay in place with "
					+ "no symptom, so this throws instead.");
			}

			MethodInfo sim200ms = AccessTools.Method(
				typeof(StructureTemperatureComponents), "Sim200ms", new Type[] { typeof(float) });
			if (sim200ms == null)
			{
				throw new InvalidOperationException(
					"ExhaustHeat.Install could not find StructureTemperatureComponents.Sim200ms(float), "
					+ "which is what retires a building's rate once it stops exhausting. Without it "
					+ "every rate ever set would run forever.");
			}

			harmony.Patch(exhaust, new HarmonyMethod(
				AccessTools.Method(typeof(ExhaustHeat), "ExhaustHeatPrefix")));
			harmony.Patch(sim200ms, null, new HarmonyMethod(
				AccessTools.Method(typeof(ExhaustHeat), "Sim200msPostfix")));

			installed = true;
		}

		// ------------------------------------------------------------------ the patches
		//
		// Not attributed, for the same reason PowerHeat's are not: OniFramework must never
		// patch anything merely by being loaded, and an attributed class would be swept up by
		// a consumer's Harmony.PatchAll(assembly).

		/// <summary>
		/// Replaces vanilla's per-cell delivery outright.
		///
		/// Taking the arguments here rather than re-deriving the rate from
		/// <c>payload.ExhaustKilowatts</c> is deliberate and covers BOTH of vanilla's callers
		/// with one code path. <c>SpaceHeater.AddExhaustHeat</c> passes a rate that is neither
		/// the def constant nor anything derivable from the payload -- it is
		/// <c>CurrentExhaustedKW</c> minus whatever the bubble emitter diverted -- and a
		/// ceiling that switches to 10000 K in turbo mode. Reading the call rather than
		/// reimplementing its gating means those cases are right for free, and cannot rot.
		/// </summary>
		private static bool ExhaustHeatPrefix(Extents extents, float kw, float maxTemperature)
		{
			int handle = ResolveSimHandle(extents);
			if (handle < 0)
			{
				// No building could be resolved under those extents. Let vanilla have it:
				// its deletion is bad, but silently dropping the heat entirely is worse.
				return true;
			}

			Tracked entry;
			if (!tracked.TryGetValue(handle, out entry))
			{
				entry = new Tracked();
				tracked[handle] = entry;
			}
			entry.SweepsUnseen = 0;
			if (entry.Kilowatts != kw || entry.MaxTemperature != maxTemperature)
			{
				entry.Kilowatts = kw;
				entry.MaxTemperature = maxTemperature;
				Push(handle, kw, maxTemperature);
			}
			return false;
		}

		/// <summary>
		/// Retires rates for buildings that have stopped exhausting.
		///
		/// A sweep rather than an event subscription, for the same reason
		/// <see cref="PowerHeat"/> uses one: the alternative is hooking every path by which a
		/// building can stop exhausting -- operational change, despawn, state-machine exit,
		/// turbo toggle -- and a rate left running because one of those was missed has no
		/// symptom beyond a machine that quietly never cools down.
		/// </summary>
		private static void Sim200msPostfix()
		{
			List<int> retire = null;
			float total = 0f;
			int live = 0;
			foreach (KeyValuePair<int, Tracked> pair in tracked)
			{
				Tracked entry = pair.Value;
				entry.SweepsUnseen++;
				if (entry.SweepsUnseen > SweepsBeforeZero)
				{
					if (retire == null)
					{
						retire = new List<int>();
					}
					retire.Add(pair.Key);
					continue;
				}
				if (entry.Kilowatts != 0f)
				{
					total += entry.Kilowatts;
					live++;
				}
			}
			if (retire != null)
			{
				for (int i = 0; i < retire.Count; i++)
				{
					int handle = retire[i];
					Tracked entry = tracked[handle];
					if (entry.Kilowatts != 0f && global::Sim.IsValidHandle(handle))
					{
						Push(handle, 0f, entry.MaxTemperature);
					}
					tracked.Remove(handle);
				}
			}
			LastSweepKilowatts = total;
			LastSweepCount = live;
			SweepCount++;
		}

		// ------------------------------------------------------------------ plumbing

		/// <summary>
		/// The sim handle of the building occupying <paramref name="extents"/>, or -1.
		///
		/// Resolved through the building layer at the extents' own origin cell rather than
		/// passed in, because <c>ExhaustHeat</c> is <c>static</c> and takes no building: its
		/// two callers have the building in hand and neither hands it over.
		/// </summary>
		private static int ResolveSimHandle(Extents extents)
		{
			int cell = Grid.XYToCell(extents.x, extents.y);
			if (!Grid.IsValidCell(cell))
			{
				return -1;
			}
			GameObject go = Grid.Objects[cell, (int)ObjectLayer.Building];
			if (go == null)
			{
				return -1;
			}
			HandleVector<int>.Handle handle = GameComps.StructureTemperatures.GetHandle(go);
			if (!handle.IsValid())
			{
				return -1;
			}
			StructureTemperaturePayload payload = GameComps.StructureTemperatures.GetPayload(handle);
			return global::Sim.IsValidHandle(payload.simHandleCopy) ? payload.simHandleCopy : -1;
		}

		/// <summary>
		/// Sends <c>ext::kSetBuildingExhaust</c>. Same shape as <see cref="PowerHeat"/>'s push:
		/// reuses Klei's own <c>Sim.SIM_HandleMessage</c>, since this only sends.
		/// </summary>
		private static unsafe void Push(int simHandle, float kilowatts, float maxTemperature)
		{
			byte[] payload = new byte[12];
			Buffer.BlockCopy(BitConverter.GetBytes(simHandle), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(kilowatts), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(maxTemperature), 0, payload, 8, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetBuildingExhaust, payload.Length, msg);
			}
		}
	}
}
