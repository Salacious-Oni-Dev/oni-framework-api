using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// WHEN A MOD MAY REGISTER EXTENSION DATA, and the answer is not "at OnLoad".
	///
	/// <b>The window, measured rather than assumed.</b> <c>Sim.SIM_Initialize</c> destroys and
	/// rebuilds the SimDLL's entire state -- <c>delete g; g = new Sim();</c> -- and every message
	/// sent before it is dropped, because the DLL has nothing to apply it to. Registration then
	/// CLOSES at the first world allocation, because every per-cell array in a world is the same
	/// length for the life of that world. So the legal window is:
	/// <code>
	///     Sim.SIM_Initialize(...)          &lt;- the registries come into existence, empty
	///     SimMessages.CreateSimElementsTable(...)
	///     Sim.AllocateCells(w, h)          &lt;- registration closes here
	/// </code>
	/// and <c>SaveLoader</c> runs exactly that sequence, in that order, at both of its entry
	/// points (loading a save, and generating a cluster).
	///
	/// <b>TWO MANAGED ROUTES REACH ONE NATIVE ALLOCATE, and both have to be hooked.</b> Vanilla
	/// allocates with <c>Sim.AllocateCells</c>; <see cref="BlueprintWorld"/>'s canvas allocates
	/// with <c>SimMessages.SimDataInitializeFromCells</c>, which allocates and seeds every cell
	/// in one message instead. Hooking only the first left every canvas run registering nothing
	/// -- found live, on the second run of the self-test, not reasoned about. This closes on
	/// whichever of the two arrives, and the second one for the same world is ignored.
	///
	/// <b>AND IT REOPENS ON EVERY LOAD.</b> That is the half that catches people. Quitting to the
	/// menu and loading another save calls <c>SIM_Initialize</c> again, which deletes the sim
	/// that held your registration along with everything else in it. A property registered once
	/// per process exists for exactly one world; a property index cached across a load addresses
	/// whatever happens to occupy that slot next. So registration is a SUBSCRIPTION to this
	/// window, not a one-off call, and <see cref="Generation"/> is how a cached index is checked.
	///
	/// <b>How this was found.</b> The first live run of the facade's self-test registered from
	/// <c>UserMod2.OnLoad</c> -- which is what the native ABI's own comment suggested, and what
	/// this framework's first draft documented -- and every registration came back with the
	/// "there is no registry here" sentinel. Nothing was wrong with the payloads: the sim had not
	/// been created yet. An offline suite cannot see that, because offline the sim is always
	/// initialised before anything is sent to it.
	///
	/// <b>Usage.</b> One install, one subscription, and re-register every time:
	/// <code>
	/// public override void OnLoad(Harmony harmony)
	/// {
	///     base.OnLoad(harmony);
	///     if (!OniFramework.FrameworkVersion.Require(0, 1, "MyMod", out var owner)) return;
	///     OniFramework.SimExtRegistrar.Install(harmony);
	///     OniFramework.SimExtRegistrar.Opening += () =&gt;
	///     {
	///         myProperty = OniFramework.SimExtCellProperties.RegisterFloat(
	///             owner, "contamination", OniFramework.ExtPersistence.Saved, 0f);
	///     };
	/// }
	/// </code>
	/// </summary>
	public static class SimExtRegistrar
	{
		private static bool installed;

		/// <summary>
		/// Raised once per sim instance, after <c>SIM_Initialize</c> has built the registries and
		/// immediately before the world is allocated -- the only moment a per-cell property can
		/// be registered.
		///
		/// Subscribe once, from <c>OnLoad</c>. The handler runs again on every load, and it must:
		/// it is registering into a sim that did not exist a moment ago. A handler that throws is
		/// caught and logged rather than allowed to take down the load.
		/// </summary>
		public static event System.Action Opening;

		/// <summary>
		/// How many registration windows have opened. Every property and attribute index a mod
		/// holds belongs to one generation; an index kept across a load, without re-registering,
		/// addresses whatever now occupies that slot. Compare against
		/// <see cref="ExtGeneration"/>-stamped state, or simply re-register in
		/// <see cref="Opening"/> and never cache across it.
		/// </summary>
		public static long Generation { get; private set; }

		/// <summary>
		/// Install the window hook. Idempotent -- several mods may each ask for it, and the first
		/// call wins.
		/// </summary>
		/// <param name="harmony">The calling mod's Harmony instance, normally the one handed to
		/// <c>UserMod2.OnLoad</c>.</param>
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

			MethodInfo allocate = AccessTools.Method(typeof(global::Sim), "AllocateCells",
				new Type[] { typeof(int), typeof(int), typeof(bool) });
			if (allocate == null)
			{
				throw new InvalidOperationException(
					"SimExtRegistrar.Install could not find Sim.AllocateCells(int, int, bool). The "
					+ "game build has moved and every extension registration would silently be "
					+ "dropped, so this throws instead. Re-check the member against the "
					+ "current game assembly before shipping.");
			}

			// The canvas route. Not an alternative spelling of the call above -- it is a
			// different message that allocates and seeds in one go -- so a build that has one and
			// not the other is a build where half the worlds register nothing.
			MethodInfo initFromCells = AccessTools.Method(typeof(SimMessages),
				"SimDataInitializeFromCells");
			if (initFromCells == null)
			{
				throw new InvalidOperationException(
					"SimExtRegistrar.Install could not find SimMessages.SimDataInitializeFromCells. "
					+ "Every blueprint-canvas world would then register nothing, silently, so this "
					+ "throws instead.");
			}

			// PREFIXES, not postfixes: the allocation is what closes registration, so a postfix
			// would run one instruction too late and every registration would come back refused
			// with kExtRegisterClosed.
			HarmonyMethod prefix = new HarmonyMethod(
				AccessTools.Method(typeof(SimExtRegistrar), "AllocateCellsPrefix"));
			harmony.Patch(allocate, prefix);
			harmony.Patch(initFromCells, prefix);

			installed = true;
		}

		// Not attributed: OniFramework must never patch anything merely by being loaded, and an
		// attributed class would be swept up by a consumer's Harmony.PatchAll(assembly).
		private static void AllocateCellsPrefix()
		{
			Generation++;

			// First-party indices are resolved by name and cached; the sim they were resolved
			// against no longer exists, so every such cache is dropped here rather than being
			// left to hand back an index from a dead world.
			SimExtElementAttributes.InvalidateCaches();

			System.Action opening = Opening;
			if (opening == null)
			{
				return;
			}
			try
			{
				opening();
			}
			catch (Exception e)
			{
				FrameworkLog.Error("SimExtRegistrar: an Opening handler threw, so at "
					+ "least one extension registration did not happen for this world: " + e);
			}
		}
	}
}
