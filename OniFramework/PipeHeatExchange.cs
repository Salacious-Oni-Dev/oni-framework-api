using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// STATIONEERS' HEAT-EXCHANGER PORT, for any building that wants to trade heat with a pipe
	/// network without taking matter out of it.
	///
	/// WHAT IT PORTS. Stationeers' phase chambers (<c>StateChangeDevice</c>) carry a secondary pipe
	/// connection, <c>InputNetwork2</c>, and every atmospheric tick do exactly this:
	/// <code>
	/// heat = AtmosphereHelper.GetConvectionHeat(InputNetwork2.Atmosphere, InternalAtmosphere,
	///            45f * HeatExchangeRatio());          // 100 * area * (Ta - Tb), W
	/// InputNetwork2.Atmosphere.GasMixture.TransferEnergyTo(ref InternalAtmosphere.GasMixture,
	///            heat * TickSpeedSeconds);            // clamped to equilibrium
	/// </code>
	/// with <c>HeatExchangeRatio()</c> the product of both sides'
	/// <c>max(clamp01(P / 1 atm), clamp01(LiquidVolumeRatio / 0.001))</c>. Energy moves; matter
	/// does not. <c>DirectHeatExchanger</c>, <c>PassthroughHeatExchanger</c> and <c>HeatSink</c>
	/// have the same shape, so this is the whole engine-wide convection primitive, not one building's.
	///
	/// THE NETWORK IS ONE BODY, as a Stationeers network is one <c>Atmosphere</c>.
	/// <see cref="TryReadNetworkBody"/> collapses every tile's conduit contents AND its standing
	/// trapped matter into one heat capacity and one heat-capacity-weighted temperature, and
	/// <see cref="AddHeatToNetwork"/> spreads the energy back as one uniform temperature change
	/// (<see cref="PipeMatterFacade.BillLatentHeatToNetwork(int[], bool, ConduitFlow, float)"/>),
	/// which keeps ONI's per-tile gradients rather than flattening them.
	///
	/// WHY THE CLAMP IS NOT OPTIONAL. Stationeers' <c>GasMixture.TransferEnergyTo</c> refuses
	/// any transfer that would carry either side past the temperature both would share, and
	/// <see cref="ClampToEquilibriumJ"/> is that rule. At 45 m^2 the uncapped rate is 4500 W/K,
	/// against a gas network that may hold a few J/K, so without it one tick would swing the two
	/// sides past each other and ring.
	///
	/// What is NOT here: the pipe-to-room half. Stationeers pipes convect with the world by the
	/// same coefficient scaled by both pressure ratios; that lives in the SimDLL's conduit kernel
	/// (<c>sim/conduits.h</c>) as <see cref="ConduitNetworkPolicy.Convection"/>,
	/// which keeps ONI's conductivity mean as the coefficient and applies these same two ratios.
	/// </summary>
	public static class PipeHeatExchange
	{
		/// <summary>
		/// <c>AtmosphereHelper.GetConvectionHeat</c>'s one coefficient, W per m^2 per K. Not a
		/// material property -- Stationeers uses the same 100 everywhere.
		/// </summary>
		public const float ConvectionCoefficientWPerM2K = 100f;

		/// <summary><c>Chemistry.Pressure.OneAtmosphere</c> (101.32499694824219 kPa), in Pa.</summary>
		public const float OneAtmospherePa = 101324.99694824219f;

		/// <summary>
		/// The liquid volume ratio at which <c>Atmosphere.HeatExchangeRatio()</c> saturates: a
		/// thousandth of the volume. Any real amount of liquid is full strength.
		/// </summary>
		public const float LiquidVolumeRatioForFullExchange = 0.001f;

		/// <summary>The universal gas constant, J/(mol K).</summary>
		public const float GasConstantJPerMolK = 8.314462618f;

		/// <summary>
		/// One side's share of the exchange: <c>Atmosphere.HeatExchangeRatio()</c>. Linear in
		/// pressure up to one atmosphere, so a vacuum insulates; saturated by a trace of liquid,
		/// so a wet side conducts fully.
		/// </summary>
		public static float HeatExchangeRatio(float pressurePa, float liquidVolumeRatio)
		{
			return Mathf.Max(Mathf.Clamp01(pressurePa / OneAtmospherePa),
				Mathf.Clamp01(liquidVolumeRatio / LiquidVolumeRatioForFullExchange));
		}

		/// <summary>
		/// <c>AtmosphereHelper.GetConvectionHeat</c>: W flowing FROM <paramref name="fromK"/>
		/// TO <paramref name="toK"/> across <paramref name="areaM2"/>. Negative when the flow runs
		/// the other way. Scale the area by both sides' <see cref="HeatExchangeRatio"/> before
		/// calling, as Stationeers does.
		/// </summary>
		public static float ConvectionHeatW(float fromK, float toK, float areaM2)
		{
			return ConvectionCoefficientWPerM2K * areaM2 * (fromK - toK);
		}

		/// <summary>
		/// <c>GasMixture.TransferEnergyTo</c>'s clamp. <paramref name="joules"/> is the energy
		/// asked to move from the first body to the second (negative for the reverse); the result
		/// is the energy that may actually move without carrying either body past the temperature
		/// both would reach if they shared their energy. Zero when either heat capacity is not
		/// positive.
		/// </summary>
		public static float ClampToEquilibriumJ(float joules, float fromK, float fromHeatCapacityJPerK,
			float toK, float toHeatCapacityJPerK)
		{
			if (joules == 0f || fromHeatCapacityJPerK <= 0f || toHeatCapacityJPerK <= 0f)
			{
				return 0f;
			}

			float totalHeatCapacity = fromHeatCapacityJPerK + toHeatCapacityJPerK;
			float equilibriumK = (fromHeatCapacityJPerK * fromK + toHeatCapacityJPerK * toK)
				/ totalHeatCapacity;

			// The two sides' distances to equilibrium are the same number in exact arithmetic;
			// Stationeers takes the smaller of the two, and so does this, so float rounding can
			// only ever stop a transfer short, never push it past.
			float fromLimit = Mathf.Abs(fromHeatCapacityJPerK * (fromK - equilibriumK));
			float toLimit = Mathf.Abs(toHeatCapacityJPerK * (equilibriumK - toK));
			float limit = Mathf.Min(fromLimit, toLimit);
			if (Mathf.Abs(joules) <= limit)
			{
				return joules;
			}
			return Mathf.Sign(joules) * limit;
		}

		/// <summary>
		/// The isothermal work of lifting <paramref name="moles"/> of ideal gas at
		/// <paramref name="temperatureK"/> from <paramref name="fromPa"/> to
		/// <paramref name="toPa"/>: n R T ln(P2 / P1), in J. The least work any compressor can
		/// do for that lift, so a device that caps its throughput at what its power affords under
		/// this never beats Carnot. Zero when there is no lift.
		///
		/// Stationeers charges its regulators a flat <c>UsedPower</c> whatever they move and
		/// bills no work into the gas; that is not an option in a project whose first rule is
		/// that energy is conserved, and a flat charge with no throughput cap lets a high-ratio
		/// compressor pump heat for less than the second law allows.
		/// </summary>
		public static float IsothermalCompressionWorkJ(float moles, float temperatureK, float fromPa,
			float toPa)
		{
			if (moles <= 0f || temperatureK <= 0f || fromPa <= 0f || toPa <= fromPa)
			{
				return 0f;
			}
			return moles * GasConstantJPerMolK * temperatureK * Mathf.Log(toPa / fromPa);
		}

		/// <summary>
		/// Moles that <paramref name="joules"/> of work can lift isothermally from
		/// <paramref name="fromPa"/> to <paramref name="toPa"/> at <paramref name="temperatureK"/>:
		/// the inverse of <see cref="IsothermalCompressionWorkJ"/>. Positive infinity when there
		/// is no lift, because then the work is not the limit.
		/// </summary>
		public static float MolesAffordable(float joules, float temperatureK, float fromPa, float toPa)
		{
			float workPerMole = IsothermalCompressionWorkJ(1f, temperatureK, fromPa, toPa);
			if (workPerMole <= 0f)
			{
				return float.PositiveInfinity;
			}
			return Mathf.Max(joules, 0f) / workPerMole;
		}

		/// <summary>
		/// A pipe network seen as the single thermal body Stationeers treats it as.
		/// </summary>
		public struct NetworkBody
		{
			/// <summary>Every tile in the run, BORROWED from <see cref="ConduitNetworks"/> on the
			/// same terms as <see cref="PipeNetworkFacade.TryGetNetworkCells"/>.</summary>
			public int[] Cells;

			public PipeContentType ContentType;

			/// <summary>Conduit contents plus standing trapped matter, J/K.</summary>
			public float HeatCapacityJPerK;

			/// <summary>Heat-capacity-weighted temperature of everything in the run, K.</summary>
			public float TemperatureK;

			/// <summary>Gas pressure of a gas run, or the headspace pressure of a liquid run, Pa.</summary>
			public float PressurePa;

			/// <summary>Liquid volume over run volume: standing condensate in a gas run, the
			/// liquid itself in a liquid run.</summary>
			public float LiquidVolumeRatio;

			/// <summary>This side's <see cref="PipeHeatExchange.HeatExchangeRatio"/>.</summary>
			public float HeatExchangeRatio;
		}

		/// <summary>
		/// Reads the network at <paramref name="cell"/> as one body. False when there is no
		/// conduit there or nothing in the run with any heat capacity.
		/// </summary>
		public static bool TryReadNetworkBody(int cell, PipeContentType contentType, out NetworkBody body)
		{
			body = default(NetworkBody);
			body.ContentType = contentType;

			bool gasConduit = contentType == PipeContentType.Gas;
			ConduitFlow flow = gasConduit ? Game.Instance?.gasConduitFlow : Game.Instance?.liquidConduitFlow;
			if (flow == null)
			{
				return false;
			}
			if (!PipeNetworkFacade.TryGetNetworkCells(cell, contentType, out int[] cells))
			{
				return false;
			}
			if (!PipeNetworkFacade.TryReadNetwork(cell, contentType, out PipeNetworkReading reading))
			{
				return false;
			}

			float heatCapacity = 0f;
			float heatCapacityTimesK = 0f;
			for (int i = 0; i < cells.Length; i++)
			{
				int tile = cells[i];
				ConduitFlow.ConduitContents contents = flow.GetContents(tile);
				if (contents.mass > 0f && contents.element != SimHashes.Vacuum)
				{
					Element element = ElementLoader.FindElementByHash(contents.element);
					if (element != null)
					{
						float c = contents.mass * element.specificHeatCapacity * GramsPerKilogram;
						heatCapacity += c;
						heatCapacityTimesK += c * contents.temperature;
					}
				}
				if (PipeMatterFacade.TryGet(gasConduit, tile, out PipeMatterFacade.TrappedMatter matter)
					&& matter.MassKg > 0f)
				{
					float c = PipeMatterFacade.HeatCapacityJPerK(gasConduit, tile);
					heatCapacity += c;
					heatCapacityTimesK += c * matter.TemperatureK;
				}
			}
			if (heatCapacity <= 0f)
			{
				return false;
			}

			float volumeLitres = reading.VolumeLitres;
			float liquidLitres = gasConduit
				? PipeMatterFacade.TrappedLiquidLitres(true, cells)
				: reading.LiquidVolumeLitres;
			float pressurePa = reading.PressurePa;
			if (!gasConduit && PipeMatterFacade.TryGetLiquidHeadspacePressurePa(reading,
				out float headspacePa))
			{
				pressurePa = headspacePa;
			}

			body.Cells = cells;
			body.HeatCapacityJPerK = heatCapacity;
			body.TemperatureK = heatCapacityTimesK / heatCapacity;
			body.PressurePa = pressurePa;
			body.LiquidVolumeRatio = volumeLitres > 0f ? liquidLitres / volumeLitres : 0f;
			body.HeatExchangeRatio = HeatExchangeRatio(body.PressurePa, body.LiquidVolumeRatio);
			return true;
		}

		/// <summary>
		/// Adds <paramref name="joules"/> to the body read by <see cref="TryReadNetworkBody"/>
		/// (negative removes), as one uniform temperature change across the run. False when the
		/// run could not take it, so the caller must not book its own half of the transfer.
		/// </summary>
		public static bool AddHeatToNetwork(NetworkBody body, float joules)
		{
			if (joules == 0f)
			{
				return true;
			}
			if (body.Cells == null || body.Cells.Length == 0)
			{
				return false;
			}
			bool gasConduit = body.ContentType == PipeContentType.Gas;
			ConduitFlow flow = gasConduit ? Game.Instance?.gasConduitFlow : Game.Instance?.liquidConduitFlow;
			return PipeMatterFacade.BillLatentHeatToNetwork(body.Cells, gasConduit, flow, joules);
		}

		private const float GramsPerKilogram = 1000f;
	}
}
