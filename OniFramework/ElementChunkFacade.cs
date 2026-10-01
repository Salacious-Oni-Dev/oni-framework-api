using System;

namespace OniFramework
{
	/// <summary>
	/// Thin managed wrapper over vanilla ONI's own <c>ElementChunk</c> mechanism -- the real
	/// system Klei already uses for "an off-grid mass with its own temperature that thermally
	/// conducts with the grid cell it sits in" (loose ore/debris, and every stored item that
	/// isn't temperature-insulated -- see <c>SimTemperatureTransfer</c>). ONI already has this
	/// system, so it is reused wholesale here rather than inventing new native conduction math --
	/// the replacement SimDLL's own <c>ElementChunk</c> kernel (<c>sim/chunks.h</c>) is already bit-exact vanilla,
	/// and the managed-side API (<c>SimMessages.AddElementChunk</c>/<c>SetElementChunkData</c>/
	/// <c>MoveElementChunk</c>/<c>RemoveElementChunk</c>, <c>Game.Instance.simData.elementChunks</c>)
	/// is Klei's own public code, not something this project needs to add. This class exists only
	/// to give that existing API a documented, non-unsafe surface any flagship mod can call --
	/// e.g. a gas mixture tank, which needs its own isolated gas mass to conduct real heat with
	/// whatever surrounds the tank instead of sitting thermally isolated forever.
	///
	/// Registration is asynchronous, same as every other vanilla sim-component registration this
	/// project has hit before (<c>ConduitFlow.HasConduit</c>'s own ~2s real-world registration
	/// latency, found and fixed): <see cref="Register"/> only reserves a callback slot
	/// and sends the message; the real handle arrives later via <paramref name="onRegistered"/>,
	/// typically within a few sim ticks (vanilla's own <c>SimTemperatureTransfer.OnSimRegistered</c>
	/// uses the identical pattern). Callers must not assume the handle is valid immediately.
	/// </summary>
	public static class ElementChunkFacade
	{
		/// <summary>
		/// Registers a new vanilla ElementChunk at <paramref name="cell"/>: an off-grid mass of
		/// <paramref name="element"/>, starting at <paramref name="massKg"/>/<paramref name="temperatureK"/>,
		/// that conducts real heat with that cell's own grid temperature using vanilla's own
		/// surface-area/thickness/ground-transfer-scale model. Defaults reuse
		/// <c>SimTemperatureTransfer</c>'s own real serialized defaults (10 m^2, 0.01 m, 0.0625) --
		/// real vanilla values, not invented ones.
		///
		/// The chosen <paramref name="element"/> only matters for registration-time flavor
		/// (vanilla assigns an initial heat capacity from it internally) -- a caller that knows
		/// its own real heat capacity should immediately follow up with <see cref="SetData"/>
		/// once <paramref name="onRegistered"/> fires, the same correction vanilla's own
		/// <c>SimTemperatureTransfer.OnSimRegistered</c> performs on itself.
		/// </summary>
		public static void Register(int cell, SimHashes element, float massKg, float temperatureK,
			Action<int> onRegistered,
			float surfaceArea = 10f, float thickness = 0.01f, float groundTransferScale = 0.0625f)
		{
			var handle = Game.Instance.simComponentCallbackManager.Add(
				(simHandle, data) => ((Action<int>)data)(simHandle), onRegistered,
				"ElementChunkFacade.Register");
			SimMessages.AddElementChunk(cell, element, massKg, temperatureK, surfaceArea, thickness,
				groundTransferScale, handle.index);
		}

		/// <summary>
		/// Reads a registered chunk's CURRENT temperature -- reflects whatever real conduction
		/// vanilla's own native <c>StepElementChunks</c> already applied against the grid cell
		/// since the caller last wrote to it. False if <paramref name="simHandle"/> is not (or
		/// not yet) a valid handle -- e.g. still waiting on <see cref="Register"/>'s callback.
		/// </summary>
		public static unsafe bool TryGetTemperature(int simHandle, out float temperatureK)
		{
			if (!Sim.IsValidHandle(simHandle))
			{
				temperatureK = 0f;
				return false;
			}
			temperatureK = Game.Instance.simData.elementChunks[Sim.GetHandleIndex(simHandle)].temperature;
			return true;
		}

		/// <summary>
		/// Pushes a caller-computed temperature and heat capacity into an already-registered chunk
		/// -- the same call vanilla's own <c>SimTemperatureTransfer.OnDataChanged</c> makes any
		/// time its held mass or temperature changes. This is how a caller keeps a chunk's true
		/// heat capacity correct even when its real contents are a mix of several elements (an
		/// ElementChunk itself only ever tracks one, chosen at <see cref="Register"/> time) -- the
		/// heat-capacity number given here fully overrides whatever vanilla assumed at
		/// registration. No-op if the handle isn't valid yet.
		///
		/// UNITS -- <paramref name="heatCapacity"/> is <c>massKg * Element.specificHeatCapacity</c>
		/// with NO g-&gt;kg conversion, i.e. Klei's own kDTU/K, NOT J/K. This doc comment used to
		/// claim "real J/K" and that was wrong; it licensed a real 1000x energy bug in Mod 1's
		/// tank. Ground truth, both halves agreeing:
		///   * real vanilla managed code -- <c>SimTemperatureTransfer.OnDataChanged</c>/
		///     <c>OnMassChanged</c>/<c>OnSimRegistered</c> (three separate call sites) all compute
		///     <c>float heat_capacity = primary_element.Mass * primary_element.Element.specificHeatCapacity;</c>
		///     and pass exactly that, unscaled.
		///   * the native kernel -- the replacement SimDLL's <c>sim/chunks.h</c> assigns it verbatim
		///     (<c>ModifyChunk</c>: <c>d-&gt;heat_capacity = m.heatCapacity;</c>) and then feeds it
		///     to <c>CalculateTemperatureExchange</c> against the CELL's own
		///     <c>mass * e.specificHeatCapacity</c>, also unscaled. Both sides of that exchange
		///     must be in the same units or the split of energy between chunk and cell is wrong.
		/// A caller doing latent-heat math in J/kg needs the g-&gt;kg factor for THAT math, but
		/// must not carry it into this call.
		/// </summary>
		public static void SetData(int simHandle, float temperatureK, float heatCapacity)
		{
			if (Sim.IsValidHandle(simHandle))
			{
				SimMessages.SetElementChunkData(simHandle, temperatureK, heatCapacity);
			}
		}

		/// <summary>Unregisters a chunk. No-op if the handle isn't valid.</summary>
		public static void Unregister(int simHandle)
		{
			if (Sim.IsValidHandle(simHandle))
			{
				SimMessages.RemoveElementChunk(simHandle, -1);
			}
		}
	}
}
