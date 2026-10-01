using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// REGISTRY 2 OF THREE: per-element attributes. Physical properties of a SUBSTANCE that
	/// Klei's element table has no field for -- latent heat, a vapour curve coefficient, a
	/// molar mass Klei got wrong -- stored by the sim, keyed by element, readable back.
	///
	/// <b>What this replaces.</b> The molecular-mass correction used to be a hardcoded side
	/// table inside the sim's <c>ElementTable</c>: one message, one setter, one seeder, no
	/// read-back path at all, and no way for a mod to store an attribute of its own. That is
	/// now the ordinary registered attribute <see cref="MolecularMassAttribute"/>, and this is
	/// the general surface it was a special case of.
	///
	/// <b>Four ways this differs from <see cref="SimExtCellProperties"/></b>, each because
	/// per-element content data is not per-cell world state:
	/// <list type="number">
	/// <item><description><b>Nothing here is ever saved</b>, and there is no persistence to
	/// choose. Klei's element table is content data reloaded from StreamingAssets every
	/// session; an attribute OF an element is the same kind of thing. Writing one into a save
	/// would let a stale value from an old version of a mod outrank that mod's current table on
	/// load. The registering mod re-pushes after every load -- unconditionally, because there
	/// is no alternative to offer.</description></item>
	/// <item><description><b>There is no default, and "unset" is a real answer.</b> Per-cell
	/// storage is dense, so a default is the only way to say what an untouched cell holds.
	/// Per-element storage is SPARSE -- most elements have no opinion about most attributes --
	/// and the fallback for an unset attribute is per-element knowledge this registry does not
	/// have. So <see cref="TryReadFloat"/> reports set-or-not and the caller decides; a stored
	/// zero is a value, and <see cref="Clear"/> is how you get back to unset.</description></item>
	/// <item><description><b>Registration never closes.</b> Nothing here is sized to the world,
	/// so a mod that loads late can still register at any point in a sim's life. It still has to
	/// happen once per SIM INSTANCE -- <c>SIM_Initialize</c> destroys the whole DLL state on
	/// every load, this registry included -- so in practice the same
	/// <see cref="SimExtRegistrar"/> window is the right place for it.</description></item>
	/// <item><description><b>Keys are <c>SimHashes</c>, never element-table indices.</b> An
	/// index is only meaningful against one particular loaded table; a hash survives the table
	/// being reloaded, which is exactly what the sim now guarantees.</description></item>
	/// </list>
	///
	/// <b>Both messages are IMMEDIATE.</b> A registration and a write land before the call
	/// returns, not a tick later, so content pushed at load is readable on the same tick. (The
	/// legacy <c>kSetMolecularMass</c> alias used to be deferred; it is not any more, and the
	/// change was a race fix rather than a convenience -- the store it writes is read by exports
	/// that take no worker barrier.)
	///
	/// <b>On a vanilla SimDLL every call here is a safe no-op</b>, exactly as in
	/// <see cref="SimExtCellProperties"/>.
	/// </summary>
	public static class SimExtElementAttributes
	{
		/// <summary>
		/// Returned by <see cref="Register"/> when the SimDLL has no element-attribute registry
		/// -- a stock SimDLL, or a custom one too old to have it. Outside the negated
		/// <see cref="ExtRegisterResult"/> range on purpose; see
		/// <see cref="SimExtCellProperties.NotAvailable"/>.
		/// </summary>
		public const int NotAvailable = -100;

		/// <summary>
		/// <c>ext::kAttrMolecularMass</c> -- f32, arity 1, grams per mole. The first first-party
		/// attribute, and the number the sim divides by when it counts moles, so it is what
		/// every reported gas pressure depends on.
		///
		/// The sim seeds its own corrections for the diatomic gases (Klei's table stores ATOMIC
		/// mass for those, which made every diatomic pressure exactly 2x high), so this
		/// attribute is already populated for a few elements before any mod says anything.
		/// <see cref="MaterialPropertyRegistry"/> is the managed authority that pushes the rest.
		/// </summary>
		public const string MolecularMassAttribute = "sim.molecular_mass";

		/// <summary>
		/// The sim's second and third first-party attributes: a vapour
		/// curve and a liquid density, which the conduit-run phase change in <c>sim/conduits.h</c>
		/// reads. f32, arity 5 -- A and B of <c>T = (P_kPa / A)^(1/B)</c>, the freezing and critical
		/// temperatures it is clamped between, and the latent heat of vaporization in J/kg -- keyed
		/// by the SOURCE phase's element. <see cref="MaterialPropertyRegistry.PushMolecularMassesToSim"/>
		/// pushes both from the registry; neither is seeded natively.
		/// </summary>
		public const string PhaseCurveAttribute = "sim.phase_curve";

		/// <summary>f32, arity 1: a liquid's density in kg/m^3. See <see cref="PhaseCurveAttribute"/>.</summary>
		public const string LiquidDensityAttribute = "sim.liquid_density";

		/// <summary>
		/// The sim's fourth and fifth first-party attributes (the cell condensation rule):
		/// <c>ext::kAttrMinLiquidPressure</c>, f32 arity 1, in PASCALS, and
		/// <c>ext::kAttrCanCondense</c>, f32 arity 1 used as a bool (non-zero is true).
		///
		/// Together with <see cref="PhaseCurveAttribute"/> they are the three tests Stationeers'
		/// gas-phase state change makes, in its order: <c>CanCondense(Type)</c>, then
		/// <c>pressure &lt; MinLiquidPressure()</c>, then the cell's temperature against the
		/// saturation temperature. A gas carrying all three is governed by that rule in
		/// <c>TransitionCell</c> (sim/physics.h) instead of Klei's fixed
		/// <c>lowTemp - kTransitionMargin</c>; a gas missing any of them keeps Klei's.
		///
		/// THE RULE IS RESTRICTIVE ONLY. It can refuse a condensation vanilla would have made; it
		/// can never create one vanilla would not. That is what makes pushing these safe on a live
		/// save and why every offline scenario in the sim's own suite stays byte-identical.
		///
		/// BOTH ARE GAS-PHASE ATTRIBUTES. The native rule reads them only after it has established
		/// that the source element is a gas and its low-temperature target is a liquid, so a value
		/// written against a liquid or a solid is a value nothing reads.
		/// <see cref="MaterialPropertyRegistry.PushPhaseCurvesToSim"/> therefore writes them
		/// against the gas member of a family and no other -- and the absence of an entry is
		/// load-bearing, because "this element was never described" is exactly how the sim decides
		/// to keep vanilla's threshold.
		///
		/// UNSET IS NOT FALSE. An element explicitly written <c>0</c> is described and refuses to
		/// condense at all (Stationeers' Helium); an element never written keeps Klei's rule.
		/// <see cref="TryGetCanCondense"/> reports the difference.
		/// </summary>
		public const string MinLiquidPressureAttribute = "sim.min_liquid_pressure";

		/// <summary>f32, arity 1 used as a bool. See <see cref="MinLiquidPressureAttribute"/>.</summary>
		public const string CanCondenseAttribute = "sim.can_condense";

		/// <summary>
		/// The sim's sixth first-party attribute (physical latent heats):
		/// <c>ext::kAttrLatentFusion</c>, f32 arity 1, the enthalpy of FUSION in J/kg.
		/// <see cref="PhaseCurveAttribute"/>'s fifth slot is the enthalpy of vaporization and
		/// stays one; fusion is a separate attribute because a curve's arity is a wire constant.
		///
		/// <see cref="MaterialPropertyRegistry.PushMolecularMassesToSim"/> writes it on every phase
		/// of a family. The sim's planetary latent accumulator reads it off the LIQUID for a freeze
		/// or a melt and off the GAS for a direct gas &lt;-&gt; solid step (vaporization + fusion).
		/// An element with no entry contributes nothing to a transition that needs it.
		/// </summary>
		public const string LatentFusionAttribute = "sim.latent_fusion";

		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL", CharSet = CharSet.Ansi)]
		private static extern int SIM_ExtElementAttributeIndex(string name);

		[DllImport("SimDLL")]
		private static extern int SIM_ExtElementAttribute(int attrIdx, int idHash, int component,
			out uint outBits);

		private static bool unavailable;

		/// <summary>
		/// Whether the read-side exports are present. False only after a call has actually
		/// failed, since an export cannot be probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		// ------------------------------------------------------------------ registration

		/// <summary>
		/// Register a per-element attribute and return its index, or a negative value on refusal
		/// (<see cref="SimExtRegistry.DescribeRefusal"/> explains it, and
		/// <see cref="NotAvailable"/> means there is no registry at all).
		///
		/// Registering the same name twice is a refusal, not a reset:
		/// <see cref="ExtRegisterResult.Duplicate"/>, with the sim's log naming both owners. A
		/// mod that may register more than once should <see cref="Find"/> first.
		/// </summary>
		/// <param name="owner">The caller's prefix token, from
		/// <see cref="FrameworkVersion.Require(int, int, string, out ExtOwner)"/>.</param>
		/// <param name="attribute">The leaf half of the name, 1-31 characters of
		/// <c>[a-z0-9_]</c>.</param>
		/// <param name="type">The scalar each component stores.</param>
		/// <param name="arity">Components per element,
		/// 1..<see cref="SimExtRegistry.MaxArity"/>.</param>
		public static unsafe int Register(ExtOwner owner, string attribute, ExtScalarType type,
			int arity = 1)
		{
			if (!owner.IsValid)
			{
				FrameworkLog.Error("SimExtElementAttributes.Register(\"" + attribute
					+ "\"): the owner token is invalid, so there is no prefix to register under. "
					+ "Mint one with FrameworkVersion.Require(major, minor, consumer, out owner).");
				return -(int)ExtRegisterResult.BadName;
			}

			string name = owner.Compose(attribute);
			if (name == null)
			{
				string why;
				SimExtRegistry.ValidName(owner.Segment + "." + attribute, out why);
				FrameworkLog.Error("SimExtElementAttributes.Register: \"" + owner.Segment
					+ "." + attribute + "\" is not a legal extension name: " + (why ?? "unknown"));
				return -(int)ExtRegisterResult.BadName;
			}
			if (SimExtRegistry.IsReservedName(name))
			{
				FrameworkLog.Error("SimExtElementAttributes.Register: \"" + name
					+ "\" claims a reserved prefix. 'sim.' and 'oni.' belong to the simulation "
					+ "itself; the sim would refuse this anyway.");
				return -(int)ExtRegisterResult.Reserved;
			}
			if (arity < 1 || arity > SimExtRegistry.MaxArity)
			{
				FrameworkLog.Error("SimExtElementAttributes.Register(\"" + name
					+ "\"): arity must be 1.." + SimExtRegistry.MaxArity + ", not " + arity + ".");
				return -(int)ExtRegisterResult.BadArity;
			}

			byte[] payload = new byte[56];
			SimExtCellProperties.WriteName(payload, name);
			Buffer.BlockCopy(BitConverter.GetBytes((int)type), 0, payload, 48, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(arity), 0, payload, 52, 4);

			IntPtr result;
			fixed (byte* msg = payload)
			{
				result = global::Sim.SIM_HandleMessage(OniExtMessages.RegisterElementAttribute,
					payload.Length, msg);
			}
			if (result == IntPtr.Zero)
			{
				return NotAvailable;
			}

			int index = Marshal.ReadInt32(result);
			if (index < 0)
			{
				FrameworkLog.Error("SimExtElementAttributes.Register(\"" + name
					+ "\") refused: " + SimExtRegistry.DescribeRefusal(index)
					+ ". The SimDLL's own log line carries the detail.");
			}
			return index;
		}

		/// <summary>
		/// The index of an already-registered attribute, or -1 if no attribute of that name is
		/// registered (including on a SimDLL with no registry). Takes the FULL
		/// <c>&lt;owner&gt;.&lt;attribute&gt;</c> name, so it finds the sim's own first-party
		/// attributes as well as a mod's -- resolving <see cref="MolecularMassAttribute"/> is
		/// the common case.
		///
		/// Stable for the life of the process, so resolve once and keep the index rather than
		/// calling this per element.
		/// </summary>
		public static int Find(string fullName)
		{
			if (unavailable || string.IsNullOrEmpty(fullName))
			{
				return -1;
			}
			try
			{
				return SIM_ExtElementAttributeIndex(fullName);
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
		/// Write one component of one element's attribute. IMMEDIATE -- the value is in place
		/// before this returns.
		///
		/// A bad attribute index or component is refused and reported on the
		/// <c>sim.message_refused</c> stream (see <see cref="SimExtEventStreams"/>). Any
		/// <paramref name="id"/> is accepted, including one no loaded element carries: the
		/// registry is keyed by hash and does not consult the element table, so pushing an
		/// attribute for an element that a later mod adds is legitimate.
		/// </summary>
		public static void Set(int attrIdx, SimHashes id, int component, uint valueBits)
		{
			Send(attrIdx, id, component, valueBits, false);
		}

		/// <summary><see cref="Set"/> for a <see cref="ExtScalarType.Float32"/> attribute.</summary>
		public static void SetFloat(int attrIdx, SimHashes id, float value, int component = 0)
		{
			Send(attrIdx, id, component, BitConverter.ToUInt32(BitConverter.GetBytes(value), 0), false);
		}

		/// <summary><see cref="Set"/> for a <see cref="ExtScalarType.Int32"/> attribute.</summary>
		public static void SetInt(int attrIdx, SimHashes id, int value, int component = 0)
		{
			Send(attrIdx, id, component, unchecked((uint)value), false);
		}

		/// <summary>
		/// Remove <paramref name="id"/>'s WHOLE entry for this attribute -- every component of
		/// it -- so the element reads back as unset again, which is a different answer from
		/// zero.
		///
		/// <b>Clearing is an explicit operation, not a magic value.</b> The legacy molecular-mass
		/// message spells "remove this override" as a non-positive mass, which works only
		/// because no real molar mass is negative; there is no such spare value for a general
		/// attribute, and inventing one per type is how a caller ends up unable to store a
		/// legitimate zero.
		///
		/// Clearing an element that has no entry is not an error and is not reported as a
		/// refusal -- an idempotent teardown loop would otherwise fill the diagnostic stream
		/// with its own success.
		/// </summary>
		public static void Clear(int attrIdx, SimHashes id)
		{
			Send(attrIdx, id, 0, 0u, true);
		}

		private static unsafe void Send(int attrIdx, SimHashes id, int component, uint valueBits,
			bool clear)
		{
			byte[] payload = new byte[20];
			Buffer.BlockCopy(BitConverter.GetBytes(attrIdx), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes((int)id), 0, payload, 4, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(component), 0, payload, 8, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(valueBits), 0, payload, 12, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(clear ? 1 : 0), 0, payload, 16, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetElementAttribute, payload.Length, msg);
			}
		}

		// ------------------------------------------------------------------ reading

		/// <summary>
		/// Read one component of one element's attribute back as raw bits. Returns FALSE when
		/// the element has no entry for this attribute -- which is the normal case for most
		/// elements and is not an error. It also returns false for an unregistered attribute
		/// index, an out-of-range component, or a SimDLL with no registry; if you need to tell
		/// those apart, check <see cref="Find"/> and <see cref="Available"/> first.
		/// </summary>
		public static bool TryRead(int attrIdx, SimHashes id, int component, out uint bits)
		{
			bits = 0u;
			if (unavailable)
			{
				return false;
			}
			try
			{
				return SIM_ExtElementAttribute(attrIdx, (int)id, component, out bits) != 0;
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

		/// <summary><see cref="TryRead"/> for a <see cref="ExtScalarType.Float32"/> attribute.</summary>
		public static bool TryReadFloat(int attrIdx, SimHashes id, out float value, int component = 0)
		{
			uint bits;
			if (!TryRead(attrIdx, id, component, out bits))
			{
				value = 0f;
				return false;
			}
			value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
			return true;
		}

		/// <summary><see cref="TryRead"/> for a <see cref="ExtScalarType.Int32"/> attribute.</summary>
		public static bool TryReadInt(int attrIdx, SimHashes id, out int value, int component = 0)
		{
			uint bits;
			if (!TryRead(attrIdx, id, component, out bits))
			{
				value = 0;
				return false;
			}
			value = unchecked((int)bits);
			return true;
		}

		// ------------------------------------------------------------------ enumeration

		[DllImport("SimDLL")]
		private static extern int SIM_ExtElementAttributeCount();

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtElementAttributeDescribe(int attrIdx, byte* outDesc);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtElementAttributeKeys(int attrIdx, int* outKeys,
			int max);

		// SEPARATE FROM <see cref="unavailable"/> ON PURPOSE. Stage 5 shipped these exports'
		// read side before stage 5b added the enumeration, so "this SimDLL has an element
		// attribute registry" and "this SimDLL can enumerate it" are two different facts about
		// two different builds. Latching them together would make a stage-5 DLL report no
		// registry at all the moment somebody called Count, which is false and would take the
		// molecular-mass read path down with it.
		private static bool enumerateUnavailable;

		/// <summary>
		/// Whether this SimDLL can ENUMERATE its element attributes -- stage 5b's three exports.
		/// A custom SimDLL older than that still answers <see cref="Find"/> and
		/// <see cref="TryRead"/>; it simply cannot say what it holds without being told the name
		/// first. False only after a call has actually failed, since an export cannot be probed
		/// without calling it.
		/// </summary>
		public static bool CanEnumerate
		{
			get { return !enumerateUnavailable; }
		}

		// `ExtElementAttributeDesc`, in `abi/sim_abi_ext.h`. Offsets rather than a
		// [StructLayout] mirror, for the reason SimExtCellProperties.Descriptor gives: the
		// struct is the sim's, and a managed copy of the layout is a second place for it to
		// drift. The static_assert on the native side pins the size; DescBytes is the same
		// number, spelled here once.
		private const int DescBytes = 80;
		private const int DescOffsetType = 48;
		private const int DescOffsetArity = 52;
		private const int DescOffsetStride = 56;
		private const int DescOffsetValueCount = 60;
		private const int DescOffsetDeclaredIdx = 64;
		private const int DescOffsetFirstParty = 68;
		private const int DescOffsetWrites = 72;

		/// <summary>
		/// One registered attribute, as <c>SIM_ExtElementAttributeDescribe</c> reports it.
		///
		/// <b>Two fields the per-cell descriptor has and this one does not</b>, and the absence
		/// is the contract rather than an omission: there is no persistence class, because
		/// nothing in this registry is ever saved; and there is no default, because storage is
		/// sparse and UNSET IS A REAL ANSWER no default may paper over.
		/// </summary>
		public struct Descriptor
		{
			/// <summary>The registered name, e.g. <c>sim.molecular_mass</c>.</summary>
			public string Name;

			/// <summary>The scalar each component stores.</summary>
			public ExtScalarType Type;

			/// <summary>Components per element, 1 or more.</summary>
			public int Arity;

			/// <summary>Bytes per component, implied by <see cref="Type"/>.</summary>
			public int Stride;

			/// <summary>
			/// How many ELEMENTS carry a value -- not how many elements exist. This is how the
			/// sparseness is visible: an attribute two hundred elements have no opinion about
			/// reports 2, and <see cref="Keys"/> says which two.
			/// </summary>
			public int ValueCount;

			/// <summary>
			/// The index to pass to <see cref="TryRead"/> and <see cref="SetFloat"/>. Carried
			/// rather than re-derived, for the reason
			/// <see cref="SimExtEventStreams.Descriptor.DeclaredIndex"/> gives.
			/// </summary>
			public int DeclaredIndex;

			/// <summary>
			/// True when the SIM registered this attribute rather than a mod --
			/// <see cref="MolecularMassAttribute"/> is the one a stock custom build has, and it
			/// is registered natively in the sim's own constructor. A client uses this to tell
			/// whose contract it is reading.
			/// </summary>
			public bool FirstParty;

			/// <summary>
			/// Writes (and clears) this attribute has taken since the process started.
			///
			/// <b>This is what tells "nobody pushed it" from "the pusher cleared it".</b> A
			/// <see cref="ValueCount"/> of zero has those two causes and they mean opposite
			/// things: the first says a mod has not run yet, the second says it ran and decided
			/// there was nothing to override. No other field distinguishes them.
			/// </summary>
			public ulong Writes;
		}

		/// <summary>
		/// How many attributes are registered, so they can be ENUMERATED and not only probed
		/// with <see cref="Find"/> by a name the caller already had.
		///
		/// Takes no barrier: this registry is content data, written on the calling thread
		/// through the immediate message path and read-only for the rest of the frame.
		/// </summary>
		public static int Count()
		{
			if (enumerateUnavailable)
			{
				return 0;
			}
			try
			{
				return SIM_ExtElementAttributeCount();
			}
			catch (EntryPointNotFoundException)
			{
				enumerateUnavailable = true;
				return 0;
			}
			catch (DllNotFoundException)
			{
				enumerateUnavailable = true;
				return 0;
			}
		}

		/// <summary>
		/// Fill <paramref name="descriptor"/> for one attribute index. False for an out-of-range
		/// index or a SimDLL without the export, leaving the descriptor at its default.
		/// </summary>
		public static unsafe bool TryDescribe(int attrIdx, out Descriptor descriptor)
		{
			descriptor = default(Descriptor);
			if (enumerateUnavailable || attrIdx < 0)
			{
				return false;
			}
			byte* raw = stackalloc byte[DescBytes];
			try
			{
				if (SIM_ExtElementAttributeDescribe(attrIdx, raw) == 0)
				{
					return false;
				}
			}
			catch (EntryPointNotFoundException)
			{
				enumerateUnavailable = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				enumerateUnavailable = true;
				return false;
			}

			IntPtr p = new IntPtr(raw);
			descriptor.Name = SimExtRegistry.ReadName(p, 0);
			descriptor.Type = (ExtScalarType)Marshal.ReadInt32(p, DescOffsetType);
			descriptor.Arity = Marshal.ReadInt32(p, DescOffsetArity);
			descriptor.Stride = Marshal.ReadInt32(p, DescOffsetStride);
			descriptor.ValueCount = Marshal.ReadInt32(p, DescOffsetValueCount);
			descriptor.DeclaredIndex = Marshal.ReadInt32(p, DescOffsetDeclaredIdx);
			descriptor.FirstParty = Marshal.ReadInt32(p, DescOffsetFirstParty) != 0;
			descriptor.Writes = unchecked((ulong)Marshal.ReadInt64(p, DescOffsetWrites));
			return true;
		}

		/// <summary>
		/// Every attribute this SimDLL holds, in registration order. Empty when the SimDLL is
		/// older than stage 5b, which a caller should report as "this build cannot enumerate
		/// attributes" (<see cref="CanEnumerate"/>) rather than as "there are none" -- a
		/// distinction that matters here more than anywhere, because a custom SimDLL always has
		/// at least <see cref="MolecularMassAttribute"/>.
		/// </summary>
		public static List<Descriptor> Describe()
		{
			var attributes = new List<Descriptor>();
			int n = Count();
			for (int i = 0; i < n; i++)
			{
				Descriptor d;
				if (TryDescribe(i, out d))
				{
					attributes.Add(d);
				}
			}
			return attributes;
		}

		/// <summary>
		/// The elements that carry a value for one attribute, ASCENDING by id. Empty for an
		/// unregistered index, for an attribute nobody has written, and on a SimDLL older than
		/// stage 5b -- the first two are the same answer on purpose ("there is nothing to
		/// read") and <see cref="CanEnumerate"/> separates out the third.
		///
		/// <b>These are ids, never element-table rows.</b> A mod may legitimately hold a value
		/// for a hash the currently loaded table has no row for, so a caller that walks
		/// <c>ElementLoader</c> and asks about each element would show that value as absent
		/// rather than as what it is.
		/// </summary>
		public static unsafe List<SimHashes> Keys(int attrIdx)
		{
			var keys = new List<SimHashes>();
			if (enumerateUnavailable || attrIdx < 0)
			{
				return keys;
			}
			int total;
			try
			{
				total = SIM_ExtElementAttributeKeys(attrIdx, null, 0);
			}
			catch (EntryPointNotFoundException)
			{
				enumerateUnavailable = true;
				return keys;
			}
			catch (DllNotFoundException)
			{
				enumerateUnavailable = true;
				return keys;
			}
			if (total <= 0)
			{
				return keys;
			}

			// The sizing call and the filling call are two calls, and between them an immediate
			// message could add a key. Asking for what the first call reported and trusting the
			// SECOND return value is what makes that safe: the export copies at most `max` and
			// still reports the true total, so a grown attribute is truncated here rather than
			// overflowing the buffer. A client that must not miss one polls again.
			int[] raw = new int[total];
			int reported;
			fixed (int* p = raw)
			{
				reported = SIM_ExtElementAttributeKeys(attrIdx, p, total);
			}
			int copied = reported < total ? reported : total;
			for (int i = 0; i < copied; i++)
			{
				keys.Add((SimHashes)raw[i]);
			}
			return keys;
		}

		// ------------------------------------------------------------------ molecular mass

		// Resolved once. -1 means "not looked up yet", NotAvailable-style absence is reported by
		// the lookup returning -1 again, which costs one export call per query on a SimDLL that
		// does not have the attribute -- acceptable, because that call is cheap and the
		// alternative is latching an absence that a later custom SimDLL would not clear.
		private static int molecularMassIndex = -1;
		private static int phaseCurveIndex = -1;
		private static int liquidDensityIndex = -1;
		private static int minLiquidPressureIndex = -1;
		private static int canCondenseIndex = -1;
		private static int latentFusionIndex = -1;

		/// <summary>
		/// Drops every cached first-party index. Called by <see cref="SimExtRegistrar"/> when a
		/// new sim instance is built, because <c>SIM_Initialize</c> destroys the registries an
		/// index was resolved against and the next sim may lay them out differently.
		/// </summary>
		internal static void InvalidateCaches()
		{
			molecularMassIndex = -1;
			phaseCurveIndex = -1;
			liquidDensityIndex = -1;
			minLiquidPressureIndex = -1;
			canCondenseIndex = -1;
			latentFusionIndex = -1;
		}

		/// <summary>The index of <see cref="PhaseCurveAttribute"/>, cached like
		/// <see cref="MolecularMassIndex"/>; -1 on a SimDLL without it.</summary>
		public static int PhaseCurveIndex
		{
			get
			{
				if (phaseCurveIndex < 0)
				{
					phaseCurveIndex = Find(PhaseCurveAttribute);
				}
				return phaseCurveIndex;
			}
		}

		/// <summary>The index of <see cref="LiquidDensityAttribute"/>; -1 on a SimDLL without
		/// it.</summary>
		public static int LiquidDensityIndex
		{
			get
			{
				if (liquidDensityIndex < 0)
				{
					liquidDensityIndex = Find(LiquidDensityAttribute);
				}
				return liquidDensityIndex;
			}
		}

		/// <summary>The index of <see cref="MinLiquidPressureAttribute"/>; -1 on a SimDLL
		/// without the cell condensation rule.</summary>
		public static int MinLiquidPressureIndex
		{
			get
			{
				if (minLiquidPressureIndex < 0)
				{
					minLiquidPressureIndex = Find(MinLiquidPressureAttribute);
				}
				return minLiquidPressureIndex;
			}
		}

		/// <summary>The index of <see cref="CanCondenseAttribute"/>; -1 on a SimDLL without
		/// the cell condensation rule.</summary>
		public static int CanCondenseIndex
		{
			get
			{
				if (canCondenseIndex < 0)
				{
					canCondenseIndex = Find(CanCondenseAttribute);
				}
				return canCondenseIndex;
			}
		}

		/// <summary>The index of <see cref="LatentFusionAttribute"/>; -1 on a SimDLL without
		/// physical latent heats.</summary>
		public static int LatentFusionIndex
		{
			get
			{
				if (latentFusionIndex < 0)
				{
					latentFusionIndex = Find(LatentFusionAttribute);
				}
				return latentFusionIndex;
			}
		}

		/// <summary>
		/// Whether the SIM holds an enthalpy of fusion for <paramref name="id"/>, and what it is,
		/// in J/kg. False on a SimDLL without physical latent heats and for an element nobody described.
		/// </summary>
		public static bool TryGetLatentFusion(SimHashes id, out float latentFusionJPerKg)
		{
			latentFusionJPerKg = 0f;
			int idx = LatentFusionIndex;
			return idx >= 0 && TryReadFloat(idx, id, out latentFusionJPerKg);
		}

		/// <summary>
		/// The index of <see cref="MolecularMassAttribute"/>, resolved once per sim instance and
		/// cached, or -1 on a SimDLL that does not have it (or before the sim exists at all).
		/// </summary>
		public static int MolecularMassIndex
		{
			get
			{
				if (molecularMassIndex < 0)
				{
					molecularMassIndex = Find(MolecularMassAttribute);
				}
				return molecularMassIndex;
			}
		}

		/// <summary>
		/// Push <paramref name="gramsPerMole"/> as <paramref name="id"/>'s molecular mass, which
		/// is what the sim divides by when it counts moles and therefore what every reported gas
		/// pressure for that element depends on. Silently does nothing on a SimDLL without the
		/// attribute.
		///
		/// <see cref="MaterialPropertyRegistry.PushMolecularMassesToSim"/> is the route a mod should
		/// normally use -- it pushes the whole registry and keeps the managed record in step.
		/// This is here for a caller that has one number and no material record.
		/// </summary>
		public static void SetMolecularMass(SimHashes id, float gramsPerMole)
		{
			int idx = MolecularMassIndex;
			if (idx < 0)
			{
				return;
			}
			if (gramsPerMole > 0f)
			{
				SetFloat(idx, id, gramsPerMole);
			}
			else
			{
				Clear(idx, id);
			}
		}

		/// <summary>
		/// The molecular mass the SIM is actually using for <paramref name="id"/>, in g/mol.
		/// Returns false when the sim holds no override -- in which case the sim falls back to
		/// Klei's own <c>Element.molarMass</c>, which for the diatomic gases is the ATOMIC
		/// figure and half the truth.
		///
		/// <b>This read-back had no route at all before the registry.</b> The override could be
		/// written and never inspected, so "what molar mass is the sim using" was unanswerable
		/// from managed code; a pressure that looked wrong could not be traced to its input.
		/// </summary>
		public static bool TryGetMolecularMass(SimHashes id, out float gramsPerMole)
		{
			int idx = MolecularMassIndex;
			if (idx < 0)
			{
				gramsPerMole = 0f;
				return false;
			}
			return TryReadFloat(idx, id, out gramsPerMole);
		}

		/// <summary>
		/// The minimum liquid pressure the SIM is holding for <paramref name="id"/>, in Pa, or
		/// false when it holds none. Below this pressure the substance has no liquid phase at all
		/// and the sim refuses to condense it however cold the cell gets.
		///
		/// The read-back exists for the same reason <see cref="TryGetMolecularMass"/> does: a
		/// value that can be written and never inspected is a value a wrong result cannot be
		/// traced back to. This one answers "did the push actually land", which on a SimDLL that
		/// predates the attribute is legitimately "no".
		/// </summary>
		public static bool TryGetMinLiquidPressurePa(SimHashes id, out float pressurePa)
		{
			int idx = MinLiquidPressureIndex;
			if (idx < 0)
			{
				pressurePa = 0f;
				return false;
			}
			return TryReadFloat(idx, id, out pressurePa);
		}

		/// <summary>
		/// Whether the SIM has been told <paramref name="id"/> may condense, and what it was told.
		///
		/// THE RETURN VALUE AND THE OUT PARAMETER ANSWER DIFFERENT QUESTIONS, and conflating them
		/// is the mistake this shape exists to prevent. The return value is "is this element
		/// DESCRIBED" -- whether anything has pushed a condensation rule for it at all. The out
		/// parameter is that rule's verdict. An element that returns false keeps Klei's fixed
		/// <c>lowTemp</c> threshold; an element that returns true with
		/// <paramref name="canCondense"/> false never condenses in a world cell at any temperature
		/// or pressure (Stationeers' Helium, whose 6.3 kPa floor could never have stopped it).
		/// </summary>
		public static bool TryGetCanCondense(SimHashes id, out bool canCondense)
		{
			canCondense = false;
			int idx = CanCondenseIndex;
			if (idx < 0)
			{
				return false;
			}
			if (!TryReadFloat(idx, id, out float value))
			{
				return false;
			}
			canCondense = value != 0f;
			return true;
		}
	}
}
