using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// SUPERSATURATED LIQUID FIZZES: the managed half of the SimDLL's <c>kSetEffervescence</c>.
	///
	/// <b>WHAT IT DOES.</b> Water holds dissolved gas in proportion to the pressure on it (Henry's
	/// law), and holds less when it is warmer. When a cell ends up holding more than that -- its
	/// headspace was vented, it warmed, it was pumped up out of a deep tank -- the excess comes back
	/// out as BUBBLES, which rise through the liquid like any other (<see cref="Bubbles"/>) and on
	/// the way strip more gas from any supersaturated water they cross. Before this, nothing but a
	/// passing bubble could take gas back out of water, and a carbonated pond whose pressure
	/// dropped simply stayed supersaturated.
	///
	/// <b>THE CRITERION</b> is the cell's total gas tension against the pressure at its centre: the
	/// cell fizzes when the sum over every dissolved gas of <c>kg / capacity</c> passes
	/// <c>1 + margin</c>, where capacity is <see cref="Solubility.EquilibriumKg"/> at the local
	/// pressure (<see cref="Bubbles.PressureAtPa"/> -- the gas above the surface plus the solution
	/// above the cell). So depth holds gas in, as it does in a real bottle, and a deep tank fizzes
	/// from the top down. A column with an EMPTY cell over it -- the gap ONI's liquid kernel
	/// leaves for a substep as water moves -- takes the pressure of the gas beside or above that
	/// gap; only liquid with no gas anywhere around its surface is under vacuum.
	/// <see cref="TryReadCensus"/> says which case every fizz was. Each fizz scales every gas towards exactly saturated, by
	/// <c>1 - exp(-rate * period)</c> of the excess.
	///
	/// <b>WHERE THE BUBBLES COME FROM.</b> The sim decides and removes the gas and publishes it on
	/// <c>sim.liquid_payload_released</c> with reason
	/// <see cref="LiquidPayloadReleaseReason.Effervescence"/>; <see cref="DissolvedGas"/>'s one
	/// release path sees a cell that is still liquid and spawns bubbles there. Nothing is created
	/// or lost between the two: what the sim takes out is exactly what the bubbles carry, and
	/// <see cref="FizzedKg"/> counts it.
	///
	/// <b>THE DATA IS THIS FRAMEWORK'S, sent to the sim.</b> The Henry constants, the van 't Hoff
	/// terms, the partial molar volumes and the solvent table are <see cref="Solubility"/>'s, and
	/// the molar masses are <see cref="AtmosphereFacade.MolarMassGPerMol"/>'s -- the same numbers
	/// the managed bubble exchange (<see cref="Bubbles"/>) uses, so the two sides agree about what "saturated" means.
	/// A <c>Solubility.Register*</c> call re-sends them while the pass is on.
	///
	/// <b>OFF UNTIL <see cref="Push"/>.</b> Not saved: a mod that wants it calls <see cref="Push"/>
	/// once per session, from <c>Game.OnSpawn</c> or later. Inert on a SimDLL without the
	/// message, whose dispatcher refuses the id.
	///
	/// <b>THE FREE SURFACE OUTGASSES TOO</b> (<see cref="SetSurfaceExchange"/>). A
	/// bubble needs the dissolved gas to beat the TOTAL pressure, but at the liquid's surface gas
	/// leaves by diffusion against only the PARTIAL pressure of that same gas above. So the top
	/// cell of a column with gas resting on it gives up what it holds beyond that, straight into
	/// the gas above (<see cref="LiquidPayloadReleaseReason.Surface"/>, counted in
	/// <see cref="OutgassedKg"/>): carbonated water under air goes flat without fizzing. And
	/// the other way: a top cell holding LESS than that absorbs the difference
	/// out of the cell above it (<see cref="LiquidPayloadReleaseReason.Absorbed"/>, counted in
	/// <see cref="AbsorbedKg"/>), at most half of what that cell holds of the gas per pass, the
	/// heat the gas carried above the liquid's temperature paid into the liquid. A CO2 headspace
	/// carbonates the water under it.
	///
	/// <b>STATIONEERS HAS NO EQUIVALENT</b> -- no dissolved state, no nucleation and no surface
	/// exchange code -- so nothing here matches or diverges from a Stationeers site. The surface
	/// rate and drawing uptake from the cap cell alone are this SDK's own choices.
	/// </summary>
	public static class Effervescence
	{
		/// <summary>Fizz once a cell holds 10% more than its pressure keeps in solution.</summary>
		public const float DefaultMargin = 0.10f;

		/// <summary>The fraction of the excess released per second: a 50 s time constant.</summary>
		public const float DefaultRatePerSecond = 0.02f;

		/// <summary>Sim seconds between passes: one group of bubbles per fizzing cell per second.</summary>
		public const float DefaultPeriodSeconds = 1.0f;

		/// <summary>A cell that would release less than this in one pass releases nothing (0.5 g).</summary>
		public const float DefaultMinReleaseKg = 5.0e-4f;

		/// <summary>How many solvent liquids the sim accepts in one message.</summary>
		public const int MaxSolvents = 8;

		/// <summary>
		/// The surface exchange's default rate: the fraction of a free surface's excess -- what
		/// its top cell holds beyond what the PARTIAL pressure of that gas above keeps in
		/// solution -- that crosses into the gas per second. A game rate, 10-100x a real still
		/// pond's (~45% of the top cell's excess per 600 s cycle), so a carbonated pond under air
		/// goes flat within a cycle or two rather than over days.
		/// </summary>
		public const float DefaultSurfaceRatePerSecond = 1.0e-3f;

		/// <summary>A surface releasing less than this in one pass releases nothing (10 mg).</summary>
		public const float DefaultSurfaceMinReleaseKg = 1.0e-5f;

		/// <summary>Whether the last message sent switched the pass on.</summary>
		public static bool Enabled { get; private set; }

		/// <summary>The margin last sent.</summary>
		public static float Margin { get; private set; } = DefaultMargin;

		/// <summary>The rate last sent, per second.</summary>
		public static float RatePerSecond { get; private set; } = DefaultRatePerSecond;

		/// <summary>The period last sent, sim seconds.</summary>
		public static float PeriodSeconds { get; private set; } = DefaultPeriodSeconds;

		/// <summary>The minimum release last sent, kg.</summary>
		public static float MinReleaseKg { get; private set; } = DefaultMinReleaseKg;

		/// <summary>The surface exchange's rate, per second; 0 is off. Sent with every
		/// <see cref="Push"/>; change it with <see cref="SetSurfaceExchange"/>.</summary>
		public static float SurfaceRatePerSecond { get; private set; } = DefaultSurfaceRatePerSecond;

		/// <summary>The surface exchange's minimum release, kg.</summary>
		public static float SurfaceMinReleaseKg { get; private set; } = DefaultSurfaceMinReleaseKg;

		/// <summary>
		/// Kilograms of gas that crossed a free liquid surface into the gas above since
		/// <see cref="ResetAccounting"/>, every lane summed. Counted where the release is routed,
		/// so it is exactly what was put into the world (<see cref="DissolvedGas.ReleaseToWorld"/>).
		/// Not part of <see cref="FizzedKg"/>.
		/// </summary>
		public static double OutgassedKg { get; private set; }

		/// <summary>The number of surface records routed since <see cref="ResetAccounting"/>.</summary>
		public static long OutgassedRecords { get; private set; }

		/// <summary>
		/// Kilograms of gas a free liquid surface DISSOLVED out of the gas above it since
		/// <see cref="ResetAccounting"/>, every lane summed. The sim takes the gas
		/// out of the grid itself; this counts its records. Per gas:
		/// <see cref="AbsorbedKgOf"/>.
		/// </summary>
		public static double AbsorbedKg { get; private set; }

		/// <summary>The number of absorbed records seen since <see cref="ResetAccounting"/>.</summary>
		public static long AbsorbedRecords { get; private set; }

		private static readonly double[] absorbedByLane = new double[DissolvedGas.Lanes];

		/// <summary>Kilograms of <paramref name="gas"/> a free surface has dissolved since
		/// <see cref="ResetAccounting"/>; 0 for a gas with no dissolved lane.</summary>
		public static double AbsorbedKgOf(SimHashes gas)
		{
			int lane = DissolvedGas.LaneOf(gas);
			return lane >= 0 && lane < absorbedByLane.Length ? absorbedByLane[lane] : 0.0;
		}

		/// <summary>
		/// Kilograms of gas that have fizzed out of liquid since <see cref="ResetAccounting"/>,
		/// every lane summed. Counted where the release is routed, so it is exactly what the
		/// bubbles were spawned with (or, where no bubble could be spawned, put into the world as
		/// gas -- <see cref="DissolvedGas.ReleaseToWorld"/>).
		/// </summary>
		public static double FizzedKg { get; private set; }

		/// <summary>The number of fizz records routed since <see cref="ResetAccounting"/>.</summary>
		public static long FizzRecords { get; private set; }

		/// <summary>
		/// Switch the pass on, or re-send it with new numbers. Builds the lane and solvent tables
		/// from <see cref="Solubility"/> at the moment of the call.
		/// </summary>
		public static void Push(float margin = DefaultMargin, float ratePerSecond = DefaultRatePerSecond,
			float periodSeconds = DefaultPeriodSeconds, float minReleaseKg = DefaultMinReleaseKg)
		{
			Send(true, margin, ratePerSecond, periodSeconds, minReleaseKg);
		}

		/// <summary>
		/// Set the SURFACE EXCHANGE: at the top liquid cell of every column with gas resting on
		/// it, each dissolved gas holding more than the partial pressure of that same gas above
		/// keeps in solution crosses into the gas, <c>(excess) * (1 - exp(-rate * period))</c> per
		/// pass -- how carbonated water under air goes flat without a single bubble -- and each
		/// holding less absorbs the deficit the same way out of the cell above
		/// (<see cref="AbsorbedKg"/>). One rate, both directions; 0 switches both off. On at
		/// <see cref="DefaultSurfaceRatePerSecond"/> unless set; re-sends if the pass is on.
		/// <paramref name="minReleaseKg"/> is each direction's threshold per surface per pass.
		/// A SimDLL without the surface exchange ignores the appended fields.
		/// </summary>
		public static void SetSurfaceExchange(float ratePerSecond = DefaultSurfaceRatePerSecond,
			float minReleaseKg = DefaultSurfaceMinReleaseKg)
		{
			SurfaceRatePerSecond = Mathf.Max(ratePerSecond, 0f);
			SurfaceMinReleaseKg = Mathf.Max(minReleaseKg, 0f);
			RefreshIfEnabled();
		}

		/// <summary>Switch the pass off. Gas already dissolved stays dissolved.</summary>
		public static void Disable()
		{
			Send(false, Margin, RatePerSecond, PeriodSeconds, MinReleaseKg);
		}

		/// <summary>Zero <see cref="FizzedKg"/>, <see cref="FizzRecords"/>,
		/// <see cref="OutgassedKg"/>, <see cref="OutgassedRecords"/>, <see cref="AbsorbedKg"/>,
		/// <see cref="AbsorbedRecords"/> and <see cref="AbsorbedKgOf"/>. For rigs.</summary>
		public static void ResetAccounting()
		{
			FizzedKg = 0.0;
			FizzRecords = 0;
			OutgassedKg = 0.0;
			OutgassedRecords = 0;
			AbsorbedKg = 0.0;
			AbsorbedRecords = 0;
			Array.Clear(absorbedByLane, 0, absorbedByLane.Length);
		}

		/// <summary>
		/// What the sim's pass has done, by what capped each liquid column it walked. Cumulative
		/// since the sim was initialised; take deltas. See <see cref="TryReadCensus"/>.
		/// </summary>
		public struct Census
		{
			/// <summary>Passes run.</summary>
			public double Passes;
			/// <summary>Liquid columns walked with gas directly above them.</summary>
			public double ColumnsGas;
			/// <summary>Liquid columns whose cap held no gas but had a gas neighbour -- the gap a
			/// sloshing pond leaves -- weighed at that neighbour's pressure.</summary>
			public double ColumnsBorrowed;
			/// <summary>Liquid columns under genuine vacuum: no gas in the cap or beside or above it.</summary>
			public double ColumnsVacuum;
			/// <summary>Liquid columns capped by a solid, which never fizz.</summary>
			public double ColumnsConfined;
			/// <summary>Cells that fizzed under a gas cap.</summary>
			public double FizzGas;
			/// <summary>Cells that fizzed under a borrowed cap.</summary>
			public double FizzBorrowed;
			/// <summary>Cells that fizzed under genuine vacuum.</summary>
			public double FizzVacuum;
			/// <summary>Kilograms released under a gas cap.</summary>
			public double KgGas;
			/// <summary>Kilograms released under a borrowed cap.</summary>
			public double KgBorrowed;
			/// <summary>Kilograms released under genuine vacuum.</summary>
			public double KgVacuum;
			/// <summary>The highest S any fizz has had, as the sim computed it; the fields below
			/// are that fizz, read inside the substep that decided it.</summary>
			public double WorstS;
			/// <summary>Its cell (a game cell index).</summary>
			public int WorstCell;
			/// <summary>The pressure the sim used at the cell's centre, Pa.</summary>
			public double WorstPressurePa;
			/// <summary>The surface term of that pressure, Pa.</summary>
			public double WorstSurfacePa;
			/// <summary>The liquid's temperature, K.</summary>
			public double WorstTemperatureK;
			/// <summary>The solution volume the sim used, m3.</summary>
			public double WorstVolumeM3;
			/// <summary>All gas dissolved in the cell before the fizz, kg.</summary>
			public double WorstDissolvedKg;
			/// <summary>The cell the surface pressure was read from; -1 for vacuum.</summary>
			public int WorstCapCell;
			/// <summary>That cell's element as a table index; -1 for vacuum.</summary>
			public int WorstCapElementIdx;
			/// <summary>That cell's mass as the sim held it, kg.</summary>
			public double WorstCapMassKg;
			/// <summary>That cell's temperature as the sim held it, K.</summary>
			public double WorstCapTemperatureK;
			/// <summary>Free surfaces the surface exchange examined (top cells under a gas cap,
			/// once per pass). 0 on a SimDLL without the surface exchange.</summary>
			public double Surfaces;
			/// <summary>Surfaces that released. 0 on a SimDLL without the surface exchange.</summary>
			public double SurfaceReleases;
			/// <summary>Kilograms the surfaces released. 0 on a SimDLL without the surface exchange.</summary>
			public double SurfaceKg;
			/// <summary>Surfaces that absorbed gas from above. 0 on a SimDLL without surface uptake.</summary>
			public double SurfaceUptakes;
			/// <summary>Kilograms the surfaces absorbed. 0 on a SimDLL without surface uptake.</summary>
			public double SurfaceUptakeKg;
			/// <summary>Kilojoules the absorbed gas carried above the liquid's temperature and paid
			/// into it; negative for gas colder than the liquid. 0 on a SimDLL that does
			/// not report it.</summary>
			public double SurfaceUptakeHeatKJ;
		}

		// ext::kEffervescenceCensusFields. Older SimDLLs report 25 or 22, and the export copies
		// min(capacity, fields), so a shorter census reads the same prefix.
		private const int CensusFields = 28;
		private const int CensusFieldsNoUptake = 25;
		private const int CensusFieldsMinimum = 22;

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugEffervescenceCensus(double* buffer, int capacity);

		private static bool censusUnavailable;

		/// <summary>
		/// Read the sim's <see cref="Census"/>. False on a SimDLL without the export (the game's
		/// own, or an older replacement) or with no sim running; latched after a missing export
		/// so a poller does not throw repeatedly. Takes the sim's barrier.
		/// </summary>
		public static unsafe bool TryReadCensus(out Census census)
		{
			census = default(Census);
			if (censusUnavailable)
			{
				return false;
			}
			double[] v = new double[CensusFields];
			int n;
			try
			{
				fixed (double* p = v)
				{
					n = SIM_DebugEffervescenceCensus(p, v.Length);
				}
			}
			catch (EntryPointNotFoundException)
			{
				censusUnavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				censusUnavailable = true;
				return false;
			}
			if (n < CensusFieldsMinimum)
			{
				return false;
			}
			census.Passes = v[0];
			census.ColumnsGas = v[1];
			census.ColumnsBorrowed = v[2];
			census.ColumnsVacuum = v[3];
			census.ColumnsConfined = v[4];
			census.FizzGas = v[5];
			census.FizzBorrowed = v[6];
			census.FizzVacuum = v[7];
			census.KgGas = v[8];
			census.KgBorrowed = v[9];
			census.KgVacuum = v[10];
			census.WorstS = v[11];
			census.WorstCell = (int)v[12];
			census.WorstPressurePa = v[13];
			census.WorstSurfacePa = v[14];
			census.WorstTemperatureK = v[15];
			census.WorstVolumeM3 = v[16];
			census.WorstDissolvedKg = v[17];
			census.WorstCapCell = (int)v[18];
			census.WorstCapElementIdx = (int)v[19];
			census.WorstCapMassKg = v[20];
			census.WorstCapTemperatureK = v[21];
			if (n >= CensusFieldsNoUptake)
			{
				census.Surfaces = v[22];
				census.SurfaceReleases = v[23];
				census.SurfaceKg = v[24];
			}
			if (n >= CensusFields)
			{
				census.SurfaceUptakes = v[25];
				census.SurfaceUptakeKg = v[26];
				census.SurfaceUptakeHeatKJ = v[27];
			}
			return true;
		}

		// Called by DissolvedGas for every effervescence record it routes.
		internal static void NoteFizzed(float kg)
		{
			FizzedKg += kg;
			FizzRecords++;
		}

		// Called by DissolvedGas for every surface record it routes.
		internal static void NoteOutgassed(float kg)
		{
			OutgassedKg += kg;
			OutgassedRecords++;
		}

		// Called by DissolvedGas for every absorbed record, with the kilograms as a positive number.
		internal static void NoteAbsorbed(int lane, float kg)
		{
			AbsorbedKg += kg;
			AbsorbedRecords++;
			if (lane >= 0 && lane < absorbedByLane.Length)
			{
				absorbedByLane[lane] += kg;
			}
		}

		// Called by Solubility whenever one of its tables changes, so the sim's copy follows.
		internal static void RefreshIfEnabled()
		{
			if (Enabled)
			{
				Send(true, Margin, RatePerSecond, PeriodSeconds, MinReleaseKg);
			}
		}

		private static unsafe void Send(bool enabled, float margin, float ratePerSecond,
			float periodSeconds, float minReleaseKg)
		{
			int lanes = DissolvedGas.Lanes;
			// oni_sim::ext::SetEffervescenceMessageV2: the 248-byte SetEffervescenceMessage (five
			// scalars, four per-lane float arrays, the solvent count and three per-solvent arrays)
			// and then the surface exchange's two scalars and per-lane element index -- 288 bytes
			// at 8 lanes and 8 solvents, static_assert'd on the sim side. An older sim reads
			// the first 248 and ignores the rest.
			byte[] payload = new byte[20 + lanes * 16 + 4 + MaxSolvents * 12 + 8 + lanes * 4];
			int at = 0;
			Action<int> putInt = v =>
			{
				Buffer.BlockCopy(BitConverter.GetBytes(v), 0, payload, at, 4);
				at += 4;
			};
			Action<float> putFloat = v =>
			{
				Buffer.BlockCopy(BitConverter.GetBytes(v), 0, payload, at, 4);
				at += 4;
			};
			putInt(enabled ? 1 : 0);
			putFloat(Mathf.Max(margin, 0f));
			putFloat(Mathf.Max(ratePerSecond, 0f));
			putFloat(periodSeconds > 0f ? periodSeconds : DefaultPeriodSeconds);
			putFloat(Mathf.Max(minReleaseKg, 0f));

			float[] henry = new float[lanes];
			float[] vantHoff = new float[lanes];
			float[] molarKg = new float[lanes];
			float[] pmv = new float[lanes];
			for (int lane = 0; lane < lanes; lane++)
			{
				SimHashes gas = DissolvedGas.LaneElement(lane);
				Solubility.Entry entry;
				if (gas == (SimHashes)0 || !Solubility.TryGetGas(gas, out entry))
				{
					continue;
				}
				int index = ElementLoader.GetElementIndex(gas);
				if (index < 0)
				{
					continue;
				}
				float molarGPerMol = AtmosphereFacade.MolarMassGPerMol((ushort)index);
				if (!(molarGPerMol > 0f))
				{
					continue;
				}
				henry[lane] = entry.HenryMolPerM3Pa;
				vantHoff[lane] = entry.VantHoffK;
				molarKg[lane] = molarGPerMol * 0.001f;
				pmv[lane] = Solubility.PartialMolarVolumeM3PerMol(gas);
			}
			foreach (float v in henry) putFloat(v);
			foreach (float v in vantHoff) putFloat(v);
			foreach (float v in molarKg) putFloat(v);
			foreach (float v in pmv) putFloat(v);

			var solvents = new List<KeyValuePair<int, KeyValuePair<float, float>>>();
			foreach (KeyValuePair<SimHashes, float> kv in Solubility.SolventTable())
			{
				if (solvents.Count >= MaxSolvents)
				{
					Debug.LogWarning("[OniFramework] Effervescence: more than " + MaxSolvents
						+ " solvents are registered; " + kv.Key + " and any after it will not fizz");
					break;
				}
				Element element = ElementLoader.FindElementByHash(kv.Key);
				int index = ElementLoader.GetElementIndex(kv.Key);
				if (element == null || index < 0)
				{
					continue;
				}
				solvents.Add(new KeyValuePair<int, KeyValuePair<float, float>>(index,
					new KeyValuePair<float, float>(kv.Value, Bubbles.LiquidDensityKgPerM3(element))));
			}
			putInt(solvents.Count);
			for (int s = 0; s < MaxSolvents; s++)
			{
				putInt(s < solvents.Count ? solvents[s].Key : 0);
			}
			for (int s = 0; s < MaxSolvents; s++)
			{
				putFloat(s < solvents.Count ? solvents[s].Value.Key : 0f);
			}
			for (int s = 0; s < MaxSolvents; s++)
			{
				putFloat(s < solvents.Count ? solvents[s].Value.Value : 0f);
			}
			putFloat(SurfaceRatePerSecond);
			putFloat(SurfaceMinReleaseKg);
			for (int lane = 0; lane < lanes; lane++)
			{
				// Only a lane the sim can compute a capacity for needs an element; the rest are -1.
				SimHashes gas = DissolvedGas.LaneElement(lane);
				putInt(henry[lane] > 0f && gas != (SimHashes)0 ? ElementLoader.GetElementIndex(gas) : -1);
			}

			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetEffervescence, payload.Length, msg);
			}

			// One line per send, so a run's log says what the sim was actually told.
			var described = new System.Text.StringBuilder();
			for (int lane = 0; lane < lanes; lane++)
			{
				if (henry[lane] > 0f)
				{
					described.Append($" lane {lane} {DissolvedGas.LaneElement(lane)} H {henry[lane]:G3} "
						+ $"vH {vantHoff[lane]:F0} M {molarKg[lane]:G4} pmv {pmv[lane]:G3};");
				}
			}
			foreach (KeyValuePair<int, KeyValuePair<float, float>> sv in solvents)
			{
				described.Append($" solvent {ElementLoader.elements[sv.Key].id} x{sv.Value.Key:F2} "
					+ $"{sv.Value.Value:F0} kg/m3;");
			}
			Debug.Log($"[OniFramework] Effervescence: sent enabled={enabled} margin {margin:F2} "
				+ $"rate {ratePerSecond:F3}/s period {periodSeconds:F2} s min {minReleaseKg * 1000f:F2} g; "
				+ $"surface {SurfaceRatePerSecond:G3}/s min {SurfaceMinReleaseKg * 1000f:F2} g;"
				+ described);

			Enabled = enabled;
			Margin = margin;
			RatePerSecond = ratePerSecond;
			PeriodSeconds = periodSeconds > 0f ? periodSeconds : DefaultPeriodSeconds;
			MinReleaseKg = minReleaseKg;
		}
	}
}
