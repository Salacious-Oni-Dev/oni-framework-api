using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The scalar a registered extension property or element attribute stores per component.
	/// Mirrors <c>oni_sim::ext::ExtScalarType</c> in <c>abi/sim_abi_ext.h</c>; the native
	/// values are the wire contract and these must not be renumbered.
	/// </summary>
	public enum ExtScalarType
	{
		Float32 = 0,
		UInt8 = 1,
		UInt16 = 2,
		Int32 = 3,

		/// <summary>
		/// An element-table index, stored as two bytes like <see cref="UInt16"/>
		/// The difference is what a load does with it: the game sorts its element
		/// table, so an index saved under one set of mods names a different element under
		/// another, and a <see cref="ExtPersistence.Saved"/> property of this type is rewritten
		/// through the save's element palette so it still names the element it did. That
		/// includes the bytes the sim carries for a mod that is not installed. An element the
		/// loading table does not hold comes back as <see cref="SimExtCellProperties.NoElement"/>,
		/// which itself passes through untouched. Value 4 is deliberately unused. An older SimDLL
		/// refuses the registration as an unknown type.
		/// </summary>
		ElementIndex = 5,
	}

	/// <summary>
	/// What a load owes a registered per-cell property. REQUIRED at registration; the sim has
	/// no default and refuses a registration that does not choose one. Mirrors
	/// <c>oni_sim::ext::ExtPersistence</c>.
	///
	/// <b>This is a promise, not a hint.</b> <see cref="Rehydrated"/> says "the sim must not
	/// carry this, I re-push it after every load", and the sim counts writes since the last
	/// allocate so it can answer which properties were promised and have not arrived --
	/// <see cref="SimExtCellProperties.OutstandingRehydration"/> is that answer.
	/// </summary>
	public enum ExtPersistence
	{
		/// <summary>Written into the save blob and restored from it.</summary>
		Saved = 0,

		/// <summary>The registering mod re-pushes it after every load.</summary>
		Rehydrated = 1,

		/// <summary>
		/// Absent from an ordinary save, but carried in the checkpoint blob a replay harness
		/// restores. Declare this when there will be no owner present to re-push -- a
		/// standalone consumer driving the DLL loads no mods.
		/// </summary>
		CheckpointOnly = 2,
	}

	/// <summary>
	/// How a registered cell property moves when the matter in its cell moves. Mirrors
	/// <c>oni_sim::ext::CellPropertyTransport</c>; set with
	/// <see cref="SimExtCellProperties.SetTransport"/>.
	/// </summary>
	public enum CellPropertyTransport
	{
		/// <summary>The value belongs to the cell and stays put whatever flows through it.
		/// Every property's default.</summary>
		Static = 0,

		/// <summary>The value is an amount carried by the liquid in the cell. It moves in
		/// proportion to the liquid the sim moves, and when that liquid leaves the grid its
		/// share leaves on a <see cref="LiquidPayload"/> stream.</summary>
		FollowsLiquidMass = 1,
	}

	/// <summary>
	/// Why a registration was refused. The sim returns these NEGATED, so a registration call
	/// that comes back negative carries <c>-(int)reason</c>. Mirrors
	/// <c>oni_sim::ext::ExtRegisterResult</c>.
	///
	/// Distinct values because "registration failed" is not actionable and "that name is
	/// already taken by mod X" is -- and the sim's own log line names both owners.
	/// </summary>
	public enum ExtRegisterResult
	{
		BadName = 1,
		Duplicate = 2,
		Reserved = 3,
		BadType = 4,
		Closed = 5,
		Full = 6,
		Unsupported = 7,
		BadArity = 8,
		// The three a kRegisterField can return. Three codes rather than one
		// because "registration failed" is not actionable and "that property is arity 4" is.
		BadProperty = 9,
		BadAttribute = 10,
		BadRule = 11,
	}

	/// <summary>
	/// Why the sim threw a message away. Mirrors <c>oni_sim::ext::ExtRefusalReason</c>; these
	/// arrive as records on the <c>sim.message_refused</c> event stream. See
	/// <see cref="SimExtEventStreams"/>.
	/// </summary>
	public enum ExtRefusalReason
	{
		ShortPayload = 1,
		UnknownMessage = 2,
		BadTarget = 3,
	}

	/// <summary>
	/// A mod's claim on one <c>&lt;owner&gt;</c> prefix in the extension name space, and the
	/// only thing that can compose a registrable property or attribute name.
	///
	/// <b>Why a token rather than a string parameter.</b> Extension names are
	/// <c>&lt;owner&gt;.&lt;property&gt;</c> and the owner half is a permanent on-disk key: a
	/// saved property's name is how its bytes are found again in every future save. The sim
	/// enforces the SHAPE of a name and nothing else -- it cannot tell which mod sent a
	/// message, so from native's point of view any mod may register under any prefix. That
	/// check has to live here or nowhere, which is exactly what <c>abi/sim_abi_ext.h</c> says:
	/// "the composition belongs in the managed wrapper".
	///
	/// <b>Where a token comes from.</b> <see cref="FrameworkVersion.Require(int, int, string)"/>
	/// mints one from the consumer name a mod already declares, so there is no second identity
	/// call to remember and a mod that never declared a floor cannot register at all. The token
	/// is minted at <c>OnLoad</c>; the REGISTRATION itself happens later, in the window
	/// <see cref="SimExtRegistrar"/> opens:
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
	///
	/// <b>The segment is derived, not free text.</b> A consumer name is lower-cased and any
	/// character outside <c>[a-z0-9_]</c> becomes <c>_</c>, so "My Mod!" and "my_mod" mint the
	/// same owner -- which is why minting warns when two DIFFERENT consumer names collapse
	/// onto one segment. That warning is the whole point: two mods sharing a prefix is how one
	/// silently registers under the other's name.
	/// </summary>
	public struct ExtOwner
	{
		private readonly string segment;

		internal ExtOwner(string segment)
		{
			this.segment = segment;
		}

		/// <summary>
		/// The <c>&lt;owner&gt;</c> segment itself, or null for a token that could not be
		/// minted. Never uppercase, never containing a '.'.
		/// </summary>
		public string Segment
		{
			get { return segment; }
		}

		/// <summary>
		/// False for the default-constructed token and for a consumer name nothing legal could
		/// be derived from. Every registration call refuses an invalid token rather than
		/// composing a name from null.
		/// </summary>
		public bool IsValid
		{
			get { return !string.IsNullOrEmpty(segment); }
		}

		/// <summary>
		/// The full <c>&lt;owner&gt;.&lt;leaf&gt;</c> name this token would register
		/// <paramref name="leaf"/> under, or null if the token is invalid or the composed name
		/// would not pass <see cref="SimExtRegistry.ValidName"/>. Exposed so a caller can log
		/// or look up the exact name the sim will hold without registering anything.
		/// </summary>
		public string Compose(string leaf)
		{
			if (!IsValid || string.IsNullOrEmpty(leaf))
			{
				return null;
			}
			string name = segment + "." + leaf;
			string why;
			return SimExtRegistry.ValidName(name, out why) ? name : null;
		}

		public override string ToString()
		{
			return IsValid ? segment : "<invalid owner>";
		}
	}

	/// <summary>
	/// Shared machinery for the three extension registries -- per-cell properties
	/// (<see cref="SimExtCellProperties"/>), per-element attributes
	/// (<see cref="SimExtElementAttributes"/>) and per-frame event streams
	/// (<see cref="SimExtEventStreams"/>). Naming, owner minting, and the vocabulary for
	/// describing a refusal.
	///
	/// <b>All three registries share ONE name space.</b> The sim validates a cell property's
	/// name and an element attribute's name with the same function, so an owner prefix means
	/// the same thing in both. They are separate registries, though: <c>mymod.density</c> may
	/// exist as a cell property and as an element attribute at once, and the two are unrelated
	/// stores.
	///
	/// <b>What is deliberately NOT here.</b> The extension checkpoint blob
	/// (<c>ext::kSetExtCellState</c>) is not a registry surface, it is the seventh component of
	/// a save checkpoint, and its read half already ships as <see cref="SimExtCellState"/>.
	/// Its setter belongs with <see cref="SimRegistryState"/>'s family of checkpoint carriers
	/// rather than here.
	/// </summary>
	public static class SimExtRegistry
	{
		/// <summary>Longest full name the wire field can carry, <c>name[48]</c> minus its terminator.</summary>
		public const int MaxNameLength = 47;

		/// <summary>Longest owner segment, matching the sim's validator.</summary>
		public const int MaxOwnerLength = 31;

		/// <summary>Longest leaf segment, matching the sim's validator.</summary>
		public const int MaxLeafLength = 31;

		/// <summary>Components per cell or element a registration may ask for, <c>ext::kExtMaxArity</c>.</summary>
		public const int MaxArity = 64;

		/// <summary>The name field's width on the wire, <c>char name[48]</c>.</summary>
		internal const int NameBytes = 48;

		// Owner tokens already minted, keyed by the consumer name that minted them, so a
		// consumer that calls Require twice gets the same token and a second consumer
		// collapsing onto an existing segment can be told about it.
		private static readonly Dictionary<string, string> Minted =
			new Dictionary<string, string>(StringComparer.Ordinal);

		/// <summary>
		/// Mint (or return) the owner token for <paramref name="consumer"/>. Normally reached
		/// through <see cref="FrameworkVersion.Require(int, int, string, out ExtOwner)"/> rather
		/// than called directly; it is public so a consumer that has already declared its floor
		/// elsewhere can still get its token without declaring one twice.
		///
		/// Returns an invalid token, having logged an error, when nothing legal can be derived
		/// from <paramref name="consumer"/> -- a name that is empty, or that contains no
		/// <c>[a-z0-9_]</c> character at all after folding, or that folds to fewer than two
		/// characters. Refusing beats inventing a prefix, because a prefix is on-disk forever.
		/// </summary>
		public static ExtOwner ClaimOwner(string consumer)
		{
			if (string.IsNullOrEmpty(consumer))
			{
				FrameworkLog.Error("SimExtRegistry.ClaimOwner: an empty consumer name "
					+ "has no owner segment to derive. Extension registration needs an identity, "
					+ "because <owner> is a permanent key in every save that holds the property.");
				return new ExtOwner(null);
			}

			string existing;
			if (Minted.TryGetValue(consumer, out existing))
			{
				return new ExtOwner(existing);
			}

			string segment = FoldOwner(consumer);
			if (segment == null)
			{
				FrameworkLog.Error("SimExtRegistry.ClaimOwner: no legal owner segment "
					+ "could be derived from consumer name \"" + consumer + "\". An owner segment "
					+ "is 2-31 characters of [a-z0-9_]; every other character folds to '_' and a "
					+ "name that folds to nothing is refused rather than replaced with a made-up "
					+ "prefix.");
				return new ExtOwner(null);
			}

			// A collision is not refused -- two mods may legitimately want to co-own a prefix,
			// and refusing the second would break whichever loaded later for a reason it cannot
			// see. It is LOGGED, loudly, naming both, because the failure it precedes (one mod
			// registering under another's name, or a duplicate refusal from the sim) is
			// otherwise very hard to attribute.
			foreach (KeyValuePair<string, string> entry in Minted)
			{
				if (string.Equals(entry.Value, segment, StringComparison.Ordinal))
				{
					Debug.LogWarning("[OniFramework] SimExtRegistry: consumer \"" + consumer
						+ "\" and consumer \"" + entry.Key + "\" both fold to the owner segment \""
						+ segment + "\". They will share one extension prefix, so a name either "
						+ "registers is a name the other cannot, and a save cannot tell their "
						+ "data apart. Give one of them a more distinct mod name.");
					break;
				}
			}

			Minted[consumer] = segment;
			return new ExtOwner(segment);
		}

		/// <summary>
		/// The owner segment <paramref name="consumer"/> folds to, or null if nothing legal can
		/// be derived. Lower-cases, maps every character outside <c>[a-z0-9_]</c> to <c>_</c>,
		/// collapses runs of <c>_</c>, trims leading and trailing <c>_</c>, and truncates to
		/// <see cref="MaxOwnerLength"/>.
		/// </summary>
		private static string FoldOwner(string consumer)
		{
			StringBuilder sb = new StringBuilder(consumer.Length);
			bool lastWasUnderscore = false;
			foreach (char raw in consumer)
			{
				char c = char.ToLowerInvariant(raw);
				bool plain = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
				if (plain)
				{
					sb.Append(c);
					lastWasUnderscore = false;
				}
				else
				{
					if (!lastWasUnderscore && sb.Length > 0)
					{
						sb.Append('_');
						lastWasUnderscore = true;
					}
				}
			}

			string folded = sb.ToString().TrimEnd('_');
			if (folded.Length > MaxOwnerLength)
			{
				folded = folded.Substring(0, MaxOwnerLength).TrimEnd('_');
			}
			return folded.Length >= 2 ? folded : null;
		}

		/// <summary>
		/// Whether <paramref name="name"/> is a legal extension name, with
		/// <paramref name="why"/> naming the first rule it breaks. An exact mirror of the sim's
		/// <c>ext::ValidPropertyName</c> (sim/ext_registry.h), kept here so a caller learns what
		/// is wrong with a name at the call site instead of reading a refusal code back out of a
		/// message return.
		///
		/// <b>The mirror is a convenience and not the authority.</b> The sim validates every
		/// name it is sent regardless, so a drift between the two costs a caller a confusing
		/// refusal, never a name the sim did not agree to.
		/// </summary>
		public static bool ValidName(string name, out string why)
		{
			why = null;
			if (string.IsNullOrEmpty(name))
			{
				why = "empty name";
				return false;
			}
			if (name.Length > MaxNameLength)
			{
				why = "name longer than " + MaxNameLength + " characters";
				return false;
			}
			int dot = name.IndexOf('.');
			if (dot < 0)
			{
				why = "no '.' separator; names are <owner>.<property>";
				return false;
			}
			if (name.IndexOf('.', dot + 1) >= 0)
			{
				why = "more than one '.' separator";
				return false;
			}
			int ownerLength = dot;
			int leafLength = name.Length - dot - 1;
			if (ownerLength < 2 || ownerLength > MaxOwnerLength)
			{
				why = "owner segment must be 2-" + MaxOwnerLength + " chars";
				return false;
			}
			if (leafLength < 1 || leafLength > MaxLeafLength)
			{
				why = "property segment must be 1-" + MaxLeafLength + " chars";
				return false;
			}
			foreach (char c in name)
			{
				if (c == '.')
				{
					continue;
				}
				bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
				if (!ok)
				{
					why = (c >= 'A' && c <= 'Z')
						? "uppercase is rejected, not down-cased"
						: "illegal character; segments are [a-z0-9_]";
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Whether <paramref name="name"/> claims one of the prefixes the sim reserves for
		/// first-party properties. A message-driven registration under either is refused by the
		/// sim (<see cref="ExtRegisterResult.Reserved"/>), so this is checked here first to give
		/// the caller a reason rather than a code.
		/// </summary>
		public static bool IsReservedName(string name)
		{
			return !string.IsNullOrEmpty(name)
				&& (name.StartsWith("sim.", StringComparison.Ordinal)
					|| name.StartsWith("oni.", StringComparison.Ordinal));
		}

		/// <summary>Bytes one component of <paramref name="type"/> occupies, or 0 for an unknown type.</summary>
		public static int StrideOf(ExtScalarType type)
		{
			switch (type)
			{
				case ExtScalarType.Float32: return 4;
				case ExtScalarType.UInt8: return 1;
				case ExtScalarType.UInt16: return 2;
				case ExtScalarType.Int32: return 4;
				case ExtScalarType.ElementIndex: return 2;
				default: return 0;
			}
		}

		/// <summary>
		/// A human sentence for a registration return that came back negative. Returns null for
		/// a non-negative value, which is an index and not a refusal.
		/// </summary>
		public static string DescribeRefusal(int registerReturn)
		{
			if (registerReturn >= 0)
			{
				return null;
			}
			switch ((ExtRegisterResult)(-registerReturn))
			{
				case ExtRegisterResult.BadName:
					return "the name broke a shape, charset, case or length rule (see SimExtRegistry.ValidName)";
				case ExtRegisterResult.Duplicate:
					return "that name is already registered; the sim's log line names both owners";
				case ExtRegisterResult.Reserved:
					return "'sim.' and 'oni.' are reserved for first-party properties";
				case ExtRegisterResult.BadType:
					return "the scalar type or the persistence value is outside the enum";
				case ExtRegisterResult.Closed:
					return "registration closed at the first world allocation; register from OnLoad, "
						+ "before a world exists";
				case ExtRegisterResult.Full:
					return "the registry is at its cap";
				case ExtRegisterResult.Unsupported:
					return "that persistence class has no serialiser in this SimDLL";
				case ExtRegisterResult.BadArity:
					return "arity must be 1.." + MaxArity;
				case ExtRegisterResult.BadProperty:
					return "no such cell property, or it is not Float32 with arity 1 -- a field "
						+ "lives in one float per cell";
				case ExtRegisterResult.BadAttribute:
					return "the attenuation attribute is neither -1 (no attenuation) nor a "
						+ "registered element attribute";
				case ExtRegisterResult.BadRule:
					return "the rule is not self-consistent: a law, combine mode or decay mode "
						+ "outside its enum, a decay factor outside [0,1] or NaN, or a clamp "
						+ "range with its ends swapped";
				default:
					return "unknown refusal code " + (-registerReturn);
			}
		}

		/// <summary>
		/// Decodes a NUL-terminated ASCII name out of a published descriptor's 48-byte field.
		/// </summary>
		internal static string ReadName(IntPtr baseAddress, int offset)
		{
			return ReadName(baseAddress, offset, NameBytes);
		}

		/// <summary>
		/// <see cref="ReadName(IntPtr, int)"/> for a descriptor whose name field is not 48 bytes.
		/// <c>ExtPhaseDesc</c> spells its names in 32 (<see cref="SimExtPhases"/>), and reading
		/// 48 out of it would run into the three int32 fields that follow -- a NUL usually stops
		/// that in practice, which is exactly why the width belongs in the contract rather than
		/// in a hope.
		/// </summary>
		internal static string ReadName(IntPtr baseAddress, int offset, int maxBytes)
		{
			StringBuilder sb = new StringBuilder(maxBytes);
			for (int i = 0; i < maxBytes; i++)
			{
				byte b = System.Runtime.InteropServices.Marshal.ReadByte(baseAddress, offset + i);
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
