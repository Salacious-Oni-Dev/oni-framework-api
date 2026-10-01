using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OniFramework
{
	/// <summary>Attenuation laws a field can use. Mirrors <c>OniExtFieldAttenuationLaw</c>.</summary>
	public enum FieldAttenuationLaw
	{
		/// <summary>The attribute alone, mass-free.</summary>
		Flat = 0,

		/// <summary>The sim's own radiation law: a constructed tile takes
		/// <c>attribute * RADIATION_CONSTRUCTED_FACTOR</c>, everything else mixes the cell's mass
		/// against the world's radiation tuning.</summary>
		RadiationMass = 1,

		/// <summary>The sim's own sunlight law: a SOLID takes the whole attribute whatever it
		/// weighs, and a fluid takes <c>min(mass, maxMass) / maxMass</c> of it.</summary>
		LightMass = 2,
	}

	/// <summary>How a walk folds one cell's absorption into what it is carrying.
	/// Mirrors <c>OniExtFieldCombine</c>. The two are NOT interchangeable — radiation
	/// multiplies a transmission, sunlight subtracts from an exposure.</summary>
	public enum FieldCombine
	{
		/// <summary><c>carrier *= 1 - absorbed</c>.</summary>
		Transmission = 0,

		/// <summary><c>carrier -= absorbed</c>, floored at zero.</summary>
		Exposure = 1,
	}

	/// <summary>What a field does to its own values each substep, before its sources run.
	/// Mirrors <c>OniExtFieldDecay</c>.</summary>
	public enum FieldDecay
	{
		/// <summary>Nothing — and with no source either, no whole-grid pass at all.</summary>
		None = 0,

		/// <summary><c>value *= decayKeep</c>, then snapped to zero at or below the floor.</summary>
		Factor = 1,
	}

	/// <summary>What feeds a field. Mirrors <c>OniExtFieldSourceKind</c>.</summary>
	public enum FieldSourceKind
	{
		/// <summary>An ellipse with Klei's diamond falloff and an optional cone, attenuated along
		/// a ray to every cell it covers. The expensive one.</summary>
		Point = 0,

		/// <summary>A parallel source swept in lanes across one world — the sun's shape. O(cells)
		/// for the whole world however far it reaches.</summary>
		Directional = 1,

		/// <summary>Every cell of one element emits per kilogram of its own mass, into its own
		/// cell. No stencil.</summary>
		Element = 2,
	}

	/// <summary>
	/// A FIELD: a per-cell property that has been given a propagation rule.
	///
	/// <b>A field owns no storage.</b> It is a cell property you registered with
	/// <see cref="SimExtCellProperties.RegisterFloat"/> — <see cref="ExtScalarType.Float32"/>,
	/// arity 1 — plus a rule attached to it by index. So persistence, the save blob, the
	/// checkpoint, the zero-copy publish, <see cref="SimExtCellProperties.ReadFloat"/> and
	/// rehydration accounting are registry 1's, unchanged, and there is nothing new to learn
	/// about any of them. <b>There is deliberately no read path here</b>: a field's values ARE
	/// the property's values, and a second way to ask the same question is a second way to get
	/// it wrong.
	///
	/// <b>What it buys you.</b> The three walks the sim already performs — the radiation
	/// emitter's attenuated ray, the sun beam's lane sweep, the per-element emission — over a
	/// property of your own, with the attenuation read from an element attribute of your own.
	/// Three walks that disagreed about the attenuation law and about how to combine it, which
	/// is why both are parameters rather than assumptions.
	///
	/// <b>Sources are yours to re-push.</b> A field's VALUES persist with the property. Its
	/// rule and its sources do not: they are content, like an element attribute, and their
	/// owner pushes them again after every load. <see cref="SetSource"/> is keyed by an id you
	/// choose rather than a handle the sim hands back, precisely so that re-pushing is
	/// idempotent instead of being a bookkeeping problem.
	///
	/// <b>Latency.</b> <see cref="Register"/> is immediate and returns the index. Every
	/// <see cref="SetSource"/> is queued like any other parameter, so a source pushed this tick
	/// first contributes on the next one.
	///
	/// <b>On a stock SimDLL this reports unavailable rather than throwing</b>, with the same
	/// latch every facade since <see cref="GasMixtureFacade"/> uses.
	/// </summary>
	public static class SimFields
	{
		/// <summary>Returned by <see cref="Register"/> when the DLL has no field solver at all
		/// (a stock SimDLL, or a custom build without one). Distinct from a refusal: the
		/// solver is absent, not disagreeing.</summary>
		public const int NotAvailable = -1000;

		/// <summary>Fields one world can hold, <c>ONI_EXT_MAX_FIELDS</c>.</summary>
		public const int MaxFields = 32;

		/// <summary>Sources one field can hold, <c>ONI_EXT_MAX_FIELD_SOURCES</c>.</summary>
		public const int MaxSources = 64;

		/// <summary>No attenuation at all: every cell is perfectly transparent. Distinct from an
		/// attribute whose fallback happens to be zero.</summary>
		public const int NoAttribute = -1;

		// ------------------------------------------------------------------ descriptor

		/// <summary>One registered field, as <c>SIM_ExtFieldDescribe</c> reports it.</summary>
		public struct Descriptor
		{
			/// <summary>The cell property this field lives in, by name.</summary>
			public string Property;

			public int FieldIndex;
			public int PropertyIndex;

			/// <summary>The attenuation attribute, or <see cref="NoAttribute"/>.</summary>
			public int AttributeIndex;

			/// <summary>Used for an element the attribute is unset for.</summary>
			public float AttributeFallback;

			public FieldAttenuationLaw Law;
			public FieldCombine Combine;
			public FieldDecay DecayMode;
			public float DecayKeep;
			public float FloorValue;
			public float ClampLow;
			public float ClampHigh;

			/// <summary>Sources registered right now. This is the field a caller checks to answer
			/// "did somebody re-push after the load", which nothing else can answer because
			/// sources are never saved.</summary>
			public int SourceCount;

			public override string ToString()
			{
				return "field " + FieldIndex + " \"" + Property + "\" law " + Law + " combine "
					+ Combine + " decay " + DecayMode + " sources " + SourceCount;
			}
		}

		// OniExtFieldDesc, abi/sim_ext_api.h, 96 bytes with a static assert on it there.
		private const int DescSize = 96;
		private const int DescOffsetProperty = 0;   // char[48]
		private const int DescOffsetFieldIdx = 48;
		private const int DescOffsetPropertyIdx = 52;
		private const int DescOffsetAttributeIdx = 56;
		private const int DescOffsetAttributeFallback = 60;
		private const int DescOffsetLaw = 64;
		private const int DescOffsetCombine = 68;
		private const int DescOffsetDecayMode = 72;
		private const int DescOffsetDecayKeep = 76;
		private const int DescOffsetFloorValue = 80;
		private const int DescOffsetClampLo = 84;
		private const int DescOffsetClampHi = 88;
		private const int DescOffsetSourceCount = 92;

		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern int SIM_ExtFieldCount();

		[DllImport("SimDLL")]
		private static extern int SIM_ExtFieldIndex([MarshalAs(UnmanagedType.LPStr)] string propertyName);

		[DllImport("SimDLL")]
		private static extern int SIM_ExtFieldDescribe(int fieldIdx, IntPtr outDesc);

		// ------------------------------------------------------------------ availability

		private static bool unavailable;
		private static bool warned;

		public static bool Available
		{
			get { return !unavailable; }
		}

		private static void MarkUnavailable(string what)
		{
			unavailable = true;
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL does not export " + what + " -- the generic field solver "
					+ "is not available from this DLL (the game's own SimDLL, or an older "
					+ "replacement build). Every call returns unavailable from here on; nothing will throw.");
			}
		}

		private static void ReportMissingLibrary(DllNotFoundException e)
		{
			// Not latched, for the reason SimRooms gives: a DLL that could not be found may be
			// found later, while a missing export in a mapped DLL is permanent.
			if (!warned)
			{
				warned = true;
				FrameworkLog.Warn("SimDLL could not be loaded for a field query: " + e.Message
					+ ". Not latching -- a later call may still succeed.");
			}
		}

		// ------------------------------------------------------------------ registration

		/// <summary>
		/// Attaches a propagation rule to <paramref name="propertyIndex"/>, which must ALREADY be
		/// registered as <see cref="ExtScalarType.Float32"/> with arity 1. Returns the field
		/// index, <see cref="NotAvailable"/>, or a negated <see cref="ExtRegisterResult"/> —
		/// which <see cref="SimExtRegistry.DescribeRefusal"/> puts into words.
		///
		/// <b>One field per property</b>, refused as <see cref="ExtRegisterResult.Duplicate"/>.
		/// There is no name of its own to collide over: the property registry already refused the
		/// duplicate name, and a second naming registry would be a second place to collide.
		/// </summary>
		/// <param name="propertyIndex">From <see cref="SimExtCellProperties.RegisterFloat"/>.</param>
		/// <param name="attributeIndex">The element attribute carrying the per-material
		/// attenuation, from <see cref="SimExtElementAttributes"/>, or <see cref="NoAttribute"/>
		/// for a field nothing attenuates.</param>
		/// <param name="attributeFallback">What an element the attribute is UNSET for absorbs.
		/// Unset is a real answer in registry 2, so this is a declaration and not a default.
		/// Ignored entirely when <paramref name="attributeIndex"/> is
		/// <see cref="NoAttribute"/>.</param>
		/// <param name="clampHigh">The ceiling every write is held under. It has no sensible
		/// default — a field of rads and a field of lux do not share one — so it is required.</param>
		public static unsafe int Register(int propertyIndex, int attributeIndex,
			float attributeFallback, FieldAttenuationLaw law, FieldCombine combine,
			float clampHigh, FieldDecay decayMode = FieldDecay.None, float decayKeep = 1f,
			float floorValue = 0f, float clampLow = 0f)
		{
			if (unavailable)
			{
				return NotAvailable;
			}

			byte[] payload = new byte[40];
			Buffer.BlockCopy(BitConverter.GetBytes(propertyIndex), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(attributeIndex), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(attributeFallback), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)law), 0, payload, 12, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)combine), 0, payload, 16, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)decayMode), 0, payload, 20, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(decayKeep), 0, payload, 24, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(floorValue), 0, payload, 28, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(clampLow), 0, payload, 32, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(clampHigh), 0, payload, 36, 4);

			IntPtr result;
			fixed (byte* msg = payload)
			{
				result = global::Sim.SIM_HandleMessage(OniExtMessages.RegisterField,
					payload.Length, msg);
			}

			// A stock SimDLL does not know the id and returns null. Not a refusal -- the solver is
			// absent, not disagreeing -- so it gets its own return value, as
			// SimExtCellProperties.Register does.
			if (result == IntPtr.Zero)
			{
				return NotAvailable;
			}

			int index = Marshal.ReadInt32(result);
			if (index < 0)
			{
				// A REFUSAL IS NOT A CRASH -- see the same site in SimExtCellProperties.Register
				// for why this is FrameworkLog.Error rather than a throw, and for the live rig
				// that found it.
				FrameworkLog.Error("SimFields.Register(property " + propertyIndex + ") refused: "
					+ SimExtRegistry.DescribeRefusal(index)
					+ ". The SimDLL's own log line carries the detail.");
			}
			return index;
		}

		// ------------------------------------------------------------------ sources

		private static unsafe bool SendSource(int fieldIndex, int sourceId, FieldSourceKind kind,
			float strength, int target, int radiusX, int radiusY, float coneDirection,
			float coneAngle, float dirX, float dirY)
		{
			if (unavailable)
			{
				return false;
			}
			byte[] payload = new byte[44];
			Buffer.BlockCopy(BitConverter.GetBytes(fieldIndex), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(sourceId), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)kind), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(strength), 0, payload, 12, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(target), 0, payload, 16, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(radiusX), 0, payload, 20, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(radiusY), 0, payload, 24, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(coneDirection), 0, payload, 28, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(coneAngle), 0, payload, 32, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(dirX), 0, payload, 36, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(dirY), 0, payload, 40, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetFieldSource, payload.Length, msg);
			}
			return true;
		}

		/// <summary>
		/// A POINT SOURCE: an ellipse of <paramref name="radiusX"/> by <paramref name="radiusY"/>
		/// cells around <paramref name="cell"/>, with Klei's own diamond falloff, attenuated along
		/// a ray from the centre to each cell it covers. This is the emitter's shape, so a field
		/// built this way looks like the radiation the player already understands.
		///
		/// <b>This is the expensive rule</b> — a ray per covered cell, per substep — which is why
		/// <see cref="MaxSources"/> exists.
		///
		/// Re-sending the same <paramref name="sourceId"/> REPLACES that source.
		/// <see cref="RemoveSource"/> takes it away.
		/// </summary>
		/// <param name="coneAngle">Degrees. 360 means no cone at all, and short-circuits before
		/// any trigonometry, as <c>inRadialRange</c> does.</param>
		/// <param name="coneDirection">Degrees; 0 is +x.</param>
		public static bool SetPointSource(int fieldIndex, int sourceId, int cell, float strength,
			int radiusX, int radiusY, float coneAngle = 360f, float coneDirection = 0f)
		{
			return SendSource(fieldIndex, sourceId, FieldSourceKind.Point, strength, cell,
				radiusX, radiusY, coneDirection, coneAngle, 0f, 0f);
		}

		/// <summary>
		/// A DIRECTIONAL SOURCE: a parallel beam arriving at one world from
		/// (<paramref name="dirX"/>, <paramref name="dirY"/>), swept in lanes. The sun's shape,
		/// and the cheap one — O(cells) for the whole world however far it reaches.
		///
		/// The direction is where the source arrives FROM, the same convention
		/// <see cref="SunPath"/> uses. <paramref name="dirY"/> at or below zero is at or below the
		/// horizon and nothing arrives; it need not be normalised.
		/// </summary>
		public static bool SetDirectionalSource(int fieldIndex, int sourceId, int worldIndex,
			float strength, float dirX, float dirY)
		{
			return SendSource(fieldIndex, sourceId, FieldSourceKind.Directional, strength,
				worldIndex, 0, 0, 0f, 360f, dirX, dirY);
		}

		/// <summary>
		/// AN ELEMENT SOURCE: every cell holding <paramref name="elementHash"/> emits
		/// <paramref name="strength"/> per kilogram of its own mass into its own cell.
		///
		/// <b>No stencil.</b> Klei's radioactive elements spray a 5x5 weight map; that is
		/// radiation's shape rather than a general one, and a solver that inherited it would be
		/// spraying a shape nobody asked for. Pair this with a point source if you want spread.
		/// </summary>
		public static bool SetElementSource(int fieldIndex, int sourceId, SimHashes elementHash,
			float strength)
		{
			return SendSource(fieldIndex, sourceId, FieldSourceKind.Element, strength,
				(int)elementHash, 0, 0, 0f, 360f, 0f, 0f);
		}

		/// <summary>
		/// Takes one source away. Removing one that is not there is not an error — a source
		/// contributing nothing and a source that is not there are the same thing to the solver.
		/// </summary>
		public static bool RemoveSource(int fieldIndex, int sourceId)
		{
			return SendSource(fieldIndex, sourceId, FieldSourceKind.Point, 0f, 0, 0, 0, 0f, 360f,
				0f, 0f);
		}

		// ------------------------------------------------------------------ discovery

		/// <summary>How many fields this world has registered, or 0 when the solver is absent.</summary>
		public static int Count()
		{
			if (unavailable)
			{
				return 0;
			}
			try
			{
				return SIM_ExtFieldCount();
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ExtFieldCount");
				return 0;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return 0;
			}
		}

		/// <summary>
		/// The field index attached to a cell property, by the property's FULL
		/// <c>&lt;owner&gt;.&lt;property&gt;</c> name, or -1 if that property has no field (or
		/// does not exist, or the solver is absent).
		///
		/// It takes the property's name because a field has no name of its own — which is the
		/// whole design restated as a lookup.
		/// </summary>
		public static int Find(string propertyName)
		{
			if (unavailable || string.IsNullOrEmpty(propertyName))
			{
				return -1;
			}
			try
			{
				return SIM_ExtFieldIndex(propertyName);
			}
			catch (EntryPointNotFoundException)
			{
				MarkUnavailable("SIM_ExtFieldIndex");
				return -1;
			}
			catch (DllNotFoundException e)
			{
				ReportMissingLibrary(e);
				return -1;
			}
		}

		/// <summary>Reads one field's rule back. False for an unregistered index or an absent
		/// solver, leaving <paramref name="descriptor"/> at its default.</summary>
		public static bool TryDescribe(int fieldIndex, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			if (unavailable)
			{
				return false;
			}
			IntPtr buffer = Marshal.AllocHGlobal(DescSize);
			try
			{
				int ok;
				try
				{
					ok = SIM_ExtFieldDescribe(fieldIndex, buffer);
				}
				catch (EntryPointNotFoundException)
				{
					MarkUnavailable("SIM_ExtFieldDescribe");
					return false;
				}
				catch (DllNotFoundException e)
				{
					ReportMissingLibrary(e);
					return false;
				}
				if (ok == 0)
				{
					return false;
				}
				descriptor.Property = ReadName(buffer);
				descriptor.FieldIndex = Marshal.ReadInt32(buffer, DescOffsetFieldIdx);
				descriptor.PropertyIndex = Marshal.ReadInt32(buffer, DescOffsetPropertyIdx);
				descriptor.AttributeIndex = Marshal.ReadInt32(buffer, DescOffsetAttributeIdx);
				descriptor.AttributeFallback = ReadFloat(buffer, DescOffsetAttributeFallback);
				descriptor.Law = (FieldAttenuationLaw)Marshal.ReadInt32(buffer, DescOffsetLaw);
				descriptor.Combine = (FieldCombine)Marshal.ReadInt32(buffer, DescOffsetCombine);
				descriptor.DecayMode = (FieldDecay)Marshal.ReadInt32(buffer, DescOffsetDecayMode);
				descriptor.DecayKeep = ReadFloat(buffer, DescOffsetDecayKeep);
				descriptor.FloorValue = ReadFloat(buffer, DescOffsetFloorValue);
				descriptor.ClampLow = ReadFloat(buffer, DescOffsetClampLo);
				descriptor.ClampHigh = ReadFloat(buffer, DescOffsetClampHi);
				descriptor.SourceCount = Marshal.ReadInt32(buffer, DescOffsetSourceCount);
				return true;
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}

		private static float ReadFloat(IntPtr p, int offset)
		{
			return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, offset)), 0);
		}

		// The name field is a fixed 48-byte NUL-terminated buffer, so it is read to the first NUL
		// rather than by PtrToStringAnsi over the whole struct.
		private static string ReadName(IntPtr p)
		{
			StringBuilder sb = new StringBuilder(48);
			for (int i = 0; i < 48; ++i)
			{
				byte b = Marshal.ReadByte(p, DescOffsetProperty + i);
				if (b == 0)
				{
					break;
				}
				sb.Append((char)b);
			}
			return sb.ToString();
		}
	}
}
