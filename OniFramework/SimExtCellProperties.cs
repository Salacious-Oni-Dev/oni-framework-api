using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// REGISTRY 1 OF THREE: per-cell extension properties. Lets a mod add its own array over
	/// the simulation grid -- one scalar (or a fixed number of them) per cell, allocated,
	/// saved, checkpointed and published by the sim itself -- without a SimDLL change, a save
	/// version bump, or a collision with any other mod that wants the same thing.
	///
	/// <b>What this replaces.</b> Before the registry, adding one per-cell array to the sim
	/// meant six edits in the SimDLL (a <c>World</c> member, an <c>Allocate()</c> line, a
	/// <c>saveblob.h</c> field, a message id, an export and a facade) plus a global save-version
	/// increment that two mods cannot both take. Registration replaces all of it, which is the
	/// point: a third-party mod can now hold simulation-side state it invented.
	///
	/// <b>THERE IS EXACTLY ONE MOMENT WHEN A PROPERTY CAN BE REGISTERED, AND IT IS NOT
	/// <c>OnLoad</c>.</b> The sim does not exist until <c>Sim.SIM_Initialize</c> builds it, and
	/// registration closes at the world allocation that follows a few lines later. Registering
	/// too early is silently dropped -- the DLL has nothing to apply the message to -- and too
	/// late is refused with <see cref="ExtRegisterResult.Closed"/>. The window also REOPENS on
	/// every load, because <c>SIM_Initialize</c> destroys the previous sim and everything
	/// registered in it. <see cref="SimExtRegistrar"/> is the hook for that window and explains
	/// the whole rule; subscribe to it and register from there.
	///
	/// A property index is therefore valid for ONE sim instance, not for the process.
	/// <see cref="Find"/> looks one up by name within the current instance, for a caller that did
	/// not keep it.
	///
	/// <b>Names are permanent on-disk keys.</b> A <see cref="ExtPersistence.Saved"/> property's
	/// name is how its bytes are found again in every save that holds them, so renaming one
	/// orphans data. Names are composed from an <see cref="ExtOwner"/> token rather than passed
	/// whole -- see that type for why.
	///
	/// <b>Cell indices are GAME cells</b>, the same space <c>Grid.PosToCell</c> and every other
	/// sim message uses. The sim's own storage is on the padded grid and converts on the way in
	/// and out, so a caller never has to know the padding exists. (The one place it does show
	/// through is <see cref="SimExtCellState"/>'s raw blob, which is padded-indexed.)
	///
	/// <b>On a vanilla SimDLL every call here is a safe no-op.</b> The messages are dropped as
	/// unknown ids and the exports latch as missing, so a mod built against this degrades
	/// rather than faulting. Registration returns <see cref="NotAvailable"/> in that case, which
	/// is distinguishable from every refusal code the sim itself produces.
	/// </summary>
	public static class SimExtCellProperties
	{
		/// <summary>
		/// Returned by <see cref="Register"/> when the SimDLL has no extension registry at all
		/// -- a stock SimDLL, or a custom one too old to have it. Deliberately outside
		/// the negated <see cref="ExtRegisterResult"/> range, because "the sim refused this
		/// registration" and "there is no registry to refuse it" are different facts and a
		/// caller that cannot tell them apart will report the wrong one.
		/// </summary>
		public const int NotAvailable = -100;

		// ------------------------------------------------------------------ first-party names
		//
		// Registered by the sim itself under the reserved `sim.` prefix, so they are readable
		// and publishable through this class but cannot be registered through it. Spelled here
		// so a caller resolving one with Find() is not retyping a literal the ABI owns.

		/// <summary><c>ext::kThermalMassBonusProperty</c> -- f32, arity 1. See <see cref="ThermalMassBonus"/>.</summary>
		public const string ThermalMassBonusProperty = "sim.thermal_mass_bonus";

		/// <summary><c>ext::kGasOccupiedMaskProperty</c> -- u8, arity 1.</summary>
		public const string GasOccupiedMaskProperty = "sim.gas_occupied_mask";

		/// <summary><c>ext::kGasSpeciesProperty</c> -- u16, arity <c>kMaxSpeciesPerCell</c>.</summary>
		public const string GasSpeciesProperty = "sim.gas_species";

		/// <summary><c>ext::kGasMassProperty</c> -- f32, arity <c>kMaxSpeciesPerCell</c>.</summary>
		public const string GasMassProperty = "sim.gas_mass";

		/// <summary><c>ext::kRoomPromotedProperty</c> -- u8, arity 1.</summary>
		public const string RoomPromotedProperty = "sim.room_promoted";

		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL", CharSet = CharSet.Ansi)]
		private static extern int SIM_ExtCellPropertyIndex(string name);

		[DllImport("SimDLL")]
		private static extern int SIM_ExtReadCellProperty(int cellIdx, int propertyIdx,
			int component, out uint outBits);

		[DllImport("SimDLL")]
		private static extern int SIM_ExtOutstandingRehydration(int[] outIndices, int max);

		[DllImport("SimDLL")]
		private static extern int SIM_ExtCellPropertyCount();

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtCellPropertyDescribe(int propertyIdx, byte* outDesc);

		private static bool unavailable;

		/// <summary>
		/// Whether the read-side exports are present -- i.e. whether this is a custom SimDLL new
		/// enough to have the registry. False only after a call has actually failed, since an
		/// export cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		// ------------------------------------------------------------------ registration

		/// <summary>
		/// Register a per-cell property and return its index, or a negative value on refusal
		/// (<see cref="SimExtRegistry.DescribeRefusal"/> turns that into a sentence, and
		/// <see cref="NotAvailable"/> means there is no registry at all).
		///
		/// The property's storage is allocated with the next world, every component of every
		/// cell starting at <paramref name="defaultBits"/>, and stays that length for the life
		/// of that world.
		/// </summary>
		/// <param name="owner">The caller's prefix token, from
		/// <see cref="FrameworkVersion.Require(int, int, string, out ExtOwner)"/>.</param>
		/// <param name="property">The leaf half of the name, 1-31 characters of
		/// <c>[a-z0-9_]</c>.</param>
		/// <param name="type">The scalar each component stores.</param>
		/// <param name="persist">What a load owes this property. There is no default and the
		/// sim refuses a registration that does not choose one -- see
		/// <see cref="ExtPersistence"/>.</param>
		/// <param name="arity">Components per cell, 1..<see cref="SimExtRegistry.MaxArity"/>.
		/// Layout is cell-major: component <c>c</c> of cell <c>p</c> is at
		/// <c>(p * arity + c)</c>.</param>
		/// <param name="defaultBits">The starting value of every component of every cell,
		/// reinterpreted per <paramref name="type"/>.</param>
		public static unsafe int Register(ExtOwner owner, string property, ExtScalarType type,
			ExtPersistence persist, int arity, uint defaultBits)
		{
			if (!owner.IsValid)
			{
				FrameworkLog.Error("SimExtCellProperties.Register(\"" + property
					+ "\"): the owner token is invalid, so there is no prefix to register under. "
					+ "Mint one with FrameworkVersion.Require(major, minor, consumer, out owner).");
				return -(int)ExtRegisterResult.BadName;
			}

			string name = owner.Compose(property);
			if (name == null)
			{
				string why;
				SimExtRegistry.ValidName(owner.Segment + "." + property, out why);
				FrameworkLog.Error("SimExtCellProperties.Register: \"" + owner.Segment
					+ "." + property + "\" is not a legal extension name: " + (why ?? "unknown"));
				return -(int)ExtRegisterResult.BadName;
			}
			if (SimExtRegistry.IsReservedName(name))
			{
				FrameworkLog.Error("SimExtCellProperties.Register: \"" + name
					+ "\" claims a reserved prefix. 'sim.' and 'oni.' belong to the simulation "
					+ "itself so that a first-party key is visibly first-party in a save a third "
					+ "party is reading; the sim would refuse this anyway.");
				return -(int)ExtRegisterResult.Reserved;
			}
			if (arity < 1 || arity > SimExtRegistry.MaxArity)
			{
				FrameworkLog.Error("SimExtCellProperties.Register(\"" + name
					+ "\"): arity must be 1.." + SimExtRegistry.MaxArity + ", not " + arity + ".");
				return -(int)ExtRegisterResult.BadArity;
			}

			byte[] payload = new byte[64];
			WriteName(payload, name);
			Buffer.BlockCopy(BitConverter.GetBytes((int)type), 0, payload, 48, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)persist), 0, payload, 52, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(arity), 0, payload, 56, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(defaultBits), 0, payload, 60, 4);

			IntPtr result;
			fixed (byte* msg = payload)
			{
				result = global::Sim.SIM_HandleMessage(OniExtMessages.RegisterCellProperty,
					payload.Length, msg);
			}

			// A stock SimDLL does not know the id and returns null. That is not a refusal -- the
			// registry is absent, not disagreeing -- so it gets its own return value.
			if (result == IntPtr.Zero)
			{
				return NotAvailable;
			}

			int index = Marshal.ReadInt32(result);
			if (index < 0)
			{
				// A REFUSAL IS NOT A CRASH. The sim declined a registration and this method
				// has already returned a negative code the caller is required to check.
				// FrameworkLog.Error is what makes that a warning-level line with the word
				// ERROR in it rather than ONI's quit-only crash modal -- see FrameworkLog for
				// the KCrashReporter path that makes the distinction load-bearing.
				//
				// This is the site that found it, live: --ext-selftest ended at
				// that dialog on every run, because SimExtSelfTest ASSERTS this path -- it
				// registers "selftest.probe" twice on purpose, since a registry that silently
				// reset an existing property would be far worse than one that refuses. The rig
				// reported 36 PASS / 0 FAIL behind a screen saying the game had crashed.
				FrameworkLog.Error("SimExtCellProperties.Register(\"" + name
					+ "\") refused: " + SimExtRegistry.DescribeRefusal(index)
					+ ". The SimDLL's own log line carries the detail.");
			}
			return index;
		}

		/// <summary>
		/// <see cref="Register"/> for the common float case: one <see cref="ExtScalarType.Float32"/>
		/// per cell, starting at <paramref name="defaultValue"/>.
		/// </summary>
		public static int RegisterFloat(ExtOwner owner, string property, ExtPersistence persist,
			float defaultValue, int arity = 1)
		{
			return Register(owner, property, ExtScalarType.Float32, persist, arity,
				BitConverter.ToUInt32(BitConverter.GetBytes(defaultValue), 0));
		}

		/// <summary>
		/// The index of an already-registered property, or -1 if no property of that name is
		/// registered (including on a SimDLL with no registry). Takes the FULL
		/// <c>&lt;owner&gt;.&lt;property&gt;</c> name, so it can find the sim's own first-party
		/// properties as well as a mod's.
		/// </summary>
		public static int Find(string fullName)
		{
			if (unavailable || string.IsNullOrEmpty(fullName))
			{
				return -1;
			}
			try
			{
				return SIM_ExtCellPropertyIndex(fullName);
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return -1;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return -1;
			}
		}

		// ------------------------------------------------------------------ writing

		/// <summary>
		/// Write one component of one cell. QUEUED, like every other cell-addressed message:
		/// the value lands at the top of the next frame, ahead of that frame's physics, which
		/// is the same latency <c>SetInsulationValue</c> has.
		///
		/// The value REPLACES what the component held; it does not accumulate. An out-of-range
		/// cell, property index or component is refused rather than applied, and the refusal is
		/// reported on the <c>sim.message_refused</c> stream (see
		/// <see cref="SimExtEventStreams"/>) rather than thrown here -- the sim is the only side
		/// that knows what is registered.
		/// </summary>
		public static unsafe void Set(int propertyIdx, int cell, int component, uint valueBits)
		{
			byte[] payload = new byte[16];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(propertyIdx), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(component), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(valueBits), 0, payload, 12, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetCellProperty, payload.Length, msg);
			}
		}

		/// <summary>
		/// "No element" in an <see cref="ExtScalarType.ElementIndex"/> component: the sim's own
		/// 0xFFFF, and what a load writes where the saved element is not in the loading table.
		/// </summary>
		public const ushort NoElement = 0xFFFF;

		/// <summary><see cref="Set"/> for a <see cref="ExtScalarType.Float32"/> property.</summary>
		public static void SetFloat(int propertyIdx, int cell, float value, int component = 0)
		{
			Set(propertyIdx, cell, component,
				BitConverter.ToUInt32(BitConverter.GetBytes(value), 0));
		}

		/// <summary>
		/// ADD <paramref name="amount"/> to one component of a <see cref="ExtScalarType.Float32"/>
		/// property, rather than replacing it as <see cref="SetFloat"/> does. The read, the add
		/// and the write all happen on the sim's side of the queue, so two adds sent in the same
		/// tick both land -- which a read here followed by a <see cref="SetFloat"/> would not
		/// guarantee.
		///
		/// A negative amount takes away. A result below zero, a non-finite amount, a property
		/// that is not F32, an out-of-range cell or component: each is REFUSED, whole, and the
		/// refusal is reported on <c>sim.message_refused</c>. Queued, like <see cref="Set"/>.
		/// A SimDLL without it reports it as an unknown message.
		/// </summary>
		public static unsafe void AddFloat(int propertyIdx, int cell, float amount, int component = 0)
		{
			byte[] payload = new byte[16];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(propertyIdx), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(component), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(amount), 0, payload, 12, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.AddCellPropertyAmount, payload.Length, msg);
			}
		}

		/// <summary>
		/// Say how a property moves when the matter in its cell moves. The default is
		/// <see cref="CellPropertyTransport.Static"/>: the value belongs to the CELL.
		/// <see cref="CellPropertyTransport.FollowsLiquidMass"/> makes it an amount carried by
		/// the LIQUID in the cell -- a dissolved gas, a salinity, a dye -- and every liquid
		/// kernel in the SimDLL then moves it in proportion to the liquid it moves. When the
		/// liquid leaves the grid, its share leaves on one of <see cref="LiquidPayload"/>'s two
		/// streams, so nothing is lost without a record.
		///
		/// F32 properties only: an amount carried by mass has to be divisible. Anything else is
		/// refused on <c>sim.message_refused</c>. Queued, and NOT saved: send it once per session,
		/// after registering, from the same place the registration is made.
		/// </summary>
		public static unsafe void SetTransport(int propertyIdx, CellPropertyTransport transport)
		{
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(propertyIdx), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)transport), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetCellPropertyTransport, payload.Length, msg);
			}
		}

		/// <summary>
		/// Say how fast a liquid-carried property EVENS OUT across the water holding it.
		/// <see cref="SetTransport"/> above makes an amount ride the liquid's mass, and that is
		/// transport and nothing else -- an amount goes where its water goes, so water that does
		/// not move never gains any. This is the other half: <paramref name="share"/> is the
		/// fraction of a neighbour pair's CONCENTRATION gap the sim's mixing sweep closes per
		/// pair per substep, which is what spreads a dissolved gas through a still pond.
		///
		/// Zero switches mixing off for the property and is every property's default; the sim
		/// gives its own <c>sim.dissolved_mass</c> 0.125 and gives a mod's property nothing,
		/// because whether somebody else's tracer is stirred is not the sim's decision. A value
		/// outside 0..1 is CLAMPED rather than refused -- above 1 a pair would overshoot its own
		/// equilibrium and ring, and a caller asking for "as fast as possible" means 1. A
		/// <paramref name="propertyIdx"/> nothing registered IS refused, on
		/// <c>sim.message_refused</c>. Pass <see cref="AllProperties"/> to set every
		/// liquid-following property at once.
		///
		/// Queued, and NOT saved: a mixing rate describes the property, which is re-registered
		/// every session, so send it from the same place the registration is made.
		/// </summary>
		public static unsafe void SetMixing(int propertyIdx, float share)
		{
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(propertyIdx), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(share), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetPayloadMixing, payload.Length, msg);
			}
		}

		/// <summary>
		/// <see cref="SetMixing"/>'s "every liquid-following property" target. The sim's own
		/// <c>kPayloadMixingAllProperties</c>.
		/// </summary>
		public const int AllProperties = -1;

		/// <summary>
		/// What the SimDLL gives <c>sim.dissolved_mass</c> at registration, so a session that
		/// sends no <see cref="SetMixing"/> at all still mixes. The sim's own
		/// <c>kPayloadMixingDefaultShare</c>, and the same 0.125 three other cell-to-cell
		/// spreading sweeps in ONI already use.
		/// </summary>
		public const float DefaultMixingShare = 0.125f;

		/// <summary><see cref="Set"/> for a <see cref="ExtScalarType.Int32"/> property.</summary>
		public static void SetInt(int propertyIdx, int cell, int value, int component = 0)
		{
			Set(propertyIdx, cell, component, unchecked((uint)value));
		}

		/// <summary>
		/// <see cref="Set"/> for a <see cref="ExtScalarType.UInt8"/>,
		/// <see cref="ExtScalarType.UInt16"/> or <see cref="ExtScalarType.ElementIndex"/>
		/// property. The sim truncates to the registered width, so a value that does not fit is
		/// narrowed rather than refused.
		/// </summary>
		public static void SetUInt(int propertyIdx, int cell, uint value, int component = 0)
		{
			Set(propertyIdx, cell, component, value);
		}

		// ------------------------------------------------------------------ reading

		/// <summary>
		/// Read one component of one cell back as raw bits. Returns false for an unregistered
		/// property, an out-of-range cell or component, a world that has not been allocated, or
		/// a SimDLL with no registry.
		///
		/// <b>This is the per-call route and it takes a worker-thread barrier.</b> That is right
		/// for a probe reading a handful of cells and wrong for anything sweeping a region every
		/// frame -- for that, subscribe with <see cref="Publish"/> and read the tick's span
		/// through <see cref="SimExtFrame"/>, which costs one bind per tick instead of one
		/// barrier per cell.
		/// </summary>
		// A float and its bits in one place. `BitConverter.ToSingle(BitConverter.GetBytes(x), 0)`
		// ALLOCATES a four-byte array on every call, and TryReadFloat is called per cell per lane
		// down a whole liquid column for every bubble in flight -- hundreds of thousands of
		// four-byte arrays a second once Solubility started walking columns. This reinterprets
		// instead, and allocates nothing.
		[System.Runtime.InteropServices.StructLayout(
			System.Runtime.InteropServices.LayoutKind.Explicit)]
		private struct FloatBits
		{
			[System.Runtime.InteropServices.FieldOffset(0)] public uint Bits;
			[System.Runtime.InteropServices.FieldOffset(0)] public float Value;

			public static float ToSingle(uint bits)
			{
				FloatBits u = default(FloatBits);
				u.Bits = bits;
				return u.Value;
			}
		}

		public static bool TryRead(int propertyIdx, int cell, int component, out uint bits)
		{
			bits = 0u;
			if (unavailable)
			{
				return false;
			}
			try
			{
				return SIM_ExtReadCellProperty(cell, propertyIdx, component, out bits) != 0;
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return false;
			}
		}

		/// <summary><see cref="TryRead"/> for a <see cref="ExtScalarType.Float32"/> property.</summary>
		public static bool TryReadFloat(int propertyIdx, int cell, out float value, int component = 0)
		{
			uint bits;
			if (!TryRead(propertyIdx, cell, component, out bits))
			{
				value = 0f;
				return false;
			}
			value = FloatBits.ToSingle(bits);
			return true;
		}

		/// <summary><see cref="TryRead"/> for a <see cref="ExtScalarType.Int32"/> property.</summary>
		public static bool TryReadInt(int propertyIdx, int cell, out int value, int component = 0)
		{
			uint bits;
			if (!TryRead(propertyIdx, cell, component, out bits))
			{
				value = 0;
				return false;
			}
			value = unchecked((int)bits);
			return true;
		}

		/// <summary>
		/// The properties whose owner declared <see cref="ExtPersistence.Rehydrated"/> -- "the
		/// sim must not carry this, I re-push it after every load" -- and that have not had a
		/// single cell written since the world was allocated. Empty array when there is nothing
		/// outstanding, when there is no world, or on a SimDLL with no registry.
		///
		/// <b>This is what turns that declaration into a claim somebody can check.</b> A caller
		/// that restores all seven checkpoint components sees this empty; a caller that restored
		/// only the save blob sees exactly the set somebody owes a re-push for. Returns property
		/// INDICES -- pair them with <see cref="SimExtFrame"/> or your own registration record
		/// to get names.
		/// </summary>
		public static int[] OutstandingRehydration()
		{
			if (unavailable)
			{
				return new int[0];
			}
			try
			{
				int count = SIM_ExtOutstandingRehydration(null, 0);
				if (count <= 0)
				{
					return new int[0];
				}
				int[] buffer = new int[count];
				int filled = SIM_ExtOutstandingRehydration(buffer, count);
				// The count can only shrink between the two calls (a property was written in
				// between), never grow, since nothing registers after a world exists.
				if (filled < count)
				{
					int[] trimmed = new int[filled < 0 ? 0 : filled];
					Array.Copy(buffer, trimmed, trimmed.Length);
					return trimmed;
				}
				return buffer;
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return new int[0];
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return new int[0];
			}
		}

		// ------------------------------------------------------------------ publishing

		/// <summary>
		/// Subscribe (or unsubscribe) <paramref name="propertyIdx"/> to the per-frame published
		/// table, which is the cheap way to read a whole property every tick -- see
		/// <see cref="SimExtFrame"/>.
		///
		/// <b>Opt-in per property, because publishing COPIES.</b> The sim copies a subscribed
		/// property's bytes into the frame it publishes, which is what buys the pointer its
		/// tick-long lifetime while kernels are still writing the live storage. The gas-mixture
		/// triple alone is about 53 bytes per cell, so a build that reads none of it must not
		/// pay to copy it. Nothing is published until somebody asks, and the cost lands on
		/// whoever asked.
		///
		/// QUEUED: a subscription sent during tick N first appears in the table tick N+1
		/// publishes. A caller that binds and finds nothing on the first tick has not hit a bug.
		/// </summary>
		public static unsafe void Publish(int propertyIdx, bool enable)
		{
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(propertyIdx), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(enable ? 1 : 0), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.PublishCellProperty, payload.Length, msg);
			}
		}

		// ------------------------------------------------------------------ describing

		// ext::ExtCellPropertyDesc, abi/sim_abi_ext.h. Field offsets rather than a marshalled
		// struct, for the same reason SimExtFrame decodes its descriptors by offset: the layout
		// is an ABI contract stated in one header, and a [StructLayout] that drifts from it
		// fails by reading the wrong four bytes rather than by not compiling.
		private const int DescBytes = 76;
		private const int DescOffsetType = 48;
		private const int DescOffsetPersist = 52;
		private const int DescOffsetArity = 56;
		private const int DescOffsetStride = 60;
		private const int DescOffsetCellCount = 64;
		private const int DescOffsetDefaultBits = 68;
		private const int DescOffsetPublished = 72;

		/// <summary>
		/// What the registry holds about one property: everything a registration stated, plus
		/// whether it is currently subscribed to the per-frame published table.
		///
		/// This is the REGISTRY's record and not a frame's, so unlike
		/// <see cref="SimExtFrame.PublishedProperty"/> it carries no data pointer, has no
		/// tick-long lifetime, and answers the same for every tick of a world.
		/// </summary>
		public struct Descriptor
		{
			/// <summary>The full <c>&lt;owner&gt;.&lt;property&gt;</c> name.</summary>
			public string Name;

			/// <summary>The scalar each component stores.</summary>
			public ExtScalarType Type;

			/// <summary>What a load owes this property.</summary>
			public ExtPersistence Persist;

			/// <summary>Components per cell.</summary>
			public int Arity;

			/// <summary>Bytes per component. Implied by <see cref="Type"/>, reported anyway so
			/// a reader can skip a record whose type it does not recognise.</summary>
			public int Stride;

			/// <summary>
			/// PADDED cells -- the allocation's, not the game grid's -- matching
			/// <see cref="SimExtFrame.PublishedProperty.CellCount"/> and the save blob's
			/// records. 0 means registered but not yet sized, which is a real state: registration
			/// closes at the first world allocate and this is readable before it.
			/// </summary>
			public int CellCount;

			/// <summary>
			/// The registered default, as a raw 32-bit pattern per component: bit-cast for f32,
			/// the low byte or low two bytes for u8/u16, verbatim for i32.
			///
			/// This is the field the whole export exists for. Zero is a legal value of every
			/// type, so "still at the default" is not a question the bytes answer by themselves,
			/// and a consumer that paints a property cannot tell an untouched cell from a cell
			/// somebody wrote a zero into without it.
			/// </summary>
			public uint DefaultBits;

			/// <summary>
			/// Whether this property is subscribed to the per-frame published table. The one
			/// field here that can change between two calls -- and it changes a frame late,
			/// because <see cref="Publish"/> is queued.
			/// </summary>
			public bool Published;

			/// <summary>
			/// <c>CellCount * Arity * Stride</c> -- the length of the property's storage.
			/// </summary>
			public int ByteCount
			{
				get { return CellCount * Arity * Stride; }
			}
		}

		/// <summary>
		/// How many properties are registered, or 0 on a SimDLL without the registry.
		///
		/// This is what makes the registry ENUMERABLE. <see cref="Find"/> answers only for a
		/// name the caller already had, which is no use to a client trying to discover what a
		/// world registered -- and the names are a mod's to choose at runtime, so no build can
		/// know them in advance.
		/// </summary>
		public static int Count()
		{
			if (unavailable)
			{
				return 0;
			}
			try
			{
				return SIM_ExtCellPropertyCount();
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return 0;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return 0;
			}
		}

		/// <summary>
		/// Fill <paramref name="descriptor"/> for one property index. False for an unregistered
		/// index or a SimDLL without the registry, leaving the descriptor at its default.
		///
		/// CHEAP BUT NOT FREE: the export takes the sim's worker barrier, because registration
		/// can append to the registry until the first allocate closes it. Fetch it once per
		/// world, not once per frame -- the per-frame path is <see cref="SimExtFrame"/>, which
		/// takes no barrier precisely so it can be bound every tick.
		/// </summary>
		public static unsafe bool TryDescribe(int propertyIdx, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			if (unavailable || propertyIdx < 0)
			{
				return false;
			}
			byte* raw = stackalloc byte[DescBytes];
			try
			{
				if (SIM_ExtCellPropertyDescribe(propertyIdx, raw) == 0)
				{
					return false;
				}
			}
			catch (EntryPointNotFoundException)
			{
				unavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				unavailable = true;
				return false;
			}

			IntPtr p = new IntPtr(raw);
			descriptor.Name = SimExtRegistry.ReadName(p, 0);
			descriptor.Type = (ExtScalarType)Marshal.ReadInt32(p, DescOffsetType);
			descriptor.Persist = (ExtPersistence)Marshal.ReadInt32(p, DescOffsetPersist);
			descriptor.Arity = Marshal.ReadInt32(p, DescOffsetArity);
			descriptor.Stride = Marshal.ReadInt32(p, DescOffsetStride);
			descriptor.CellCount = Marshal.ReadInt32(p, DescOffsetCellCount);
			descriptor.DefaultBits = unchecked((uint)Marshal.ReadInt32(p, DescOffsetDefaultBits));
			descriptor.Published = Marshal.ReadInt32(p, DescOffsetPublished) != 0;
			return true;
		}

		/// <summary>
		/// The descriptor for a property named rather than indexed, for a caller that has the
		/// name and not the index. False if the name is not registered.
		/// </summary>
		public static bool TryDescribe(string fullName, out Descriptor descriptor)
		{
			int idx = Find(fullName);
			if (idx < 0)
			{
				descriptor = default(Descriptor);
				return false;
			}
			return TryDescribe(idx, out descriptor);
		}

		// ------------------------------------------------------------------ helpers

		/// <summary>
		/// Copies <paramref name="name"/> into the first 48 bytes of <paramref name="payload"/>
		/// as NUL-terminated ASCII. The caller has already validated the length, and the sim
		/// REFUSES rather than truncates a name that fills the field with no terminator, so
		/// leaving the tail zeroed is load-bearing rather than tidy.
		/// </summary>
		internal static void WriteName(byte[] payload, string name)
		{
			for (int i = 0; i < name.Length && i < SimExtRegistry.NameBytes - 1; i++)
			{
				payload[i] = (byte)name[i];
			}
		}
	}
}
