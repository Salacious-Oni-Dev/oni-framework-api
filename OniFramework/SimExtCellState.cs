using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>
	/// Reads the sim's per-cell extension registry -- every property any mod (or the sim
	/// itself) registered through <c>ext::CellPropertyRegistry</c> -- as one self-describing
	/// blob.
	///
	/// <b>Why this is here rather than in a facade.</b> The registry is the one piece of
	/// expanded simulation state that has no fixed shape: a property is registered by name at
	/// runtime, so there is no managed struct that could carry "the extension state" and no
	/// list of getters that would stay complete. <see cref="GasMixtureFacade"/> exposes the
	/// four properties the sim itself registers, and will always be the right thing to call
	/// for those; nothing but this can answer "what does this world hold that I was never
	/// told about", which is exactly the question a third-party mod's property poses.
	///
	/// <b>The blob is self-describing, and that is the whole point.</b> Every record carries
	/// its own name, type, arity, stride, cell count and registered default before its bytes,
	/// so a reader needs no list of property names to make sense of one -- see
	/// <see cref="Describe"/> for the layout, and the SimDLL's <c>sim/saveblob.h</c>
	/// <c>EncodeExtSection</c> for the authority on it. A client that wants the values should decode the bytes with the sim's own header-only
	/// decoder rather than a second parser.
	///
	/// <b>Read-only, by construction.</b> There is no setter here and there should not be
	/// one. The blob this returns spans ALL three persistence classes, and the checkpoint
	/// message <c>ext::kSetExtCellState</c> deliberately refuses the <c>kSaved</c> class by
	/// name -- the save blob already carries those, so restoring them twice would let the
	/// second restore win. Feeding this blob back would be exactly that mistake, so nothing
	/// accepts it: it is an inspection route and not half of a round trip. Use
	/// <see cref="SimRegistryState"/>'s pattern for state that IS meant to round-trip.
	///
	/// <b>Indexing.</b> Every record is indexed on the sim's PADDED grid, not the game's.
	/// <c>World::Allocate</c> sizes extension storage with the padded count, so a record's
	/// <c>CellCount</c> is <c>(Grid.WidthInCells + 2) * (Grid.HeightInCells + 2)</c> and the
	/// element for game cell (x, y) sits at <c>(y + 1) * (width + 2) + (x + 1)</c>. Reading
	/// with a game cell index does not fail -- it returns a neighbour's value.
	///
	/// <b>All-default properties are absent.</b> The sim omits a record whose every cell is
	/// still at the registered default, so "registered but untouched" and "never registered"
	/// look the same from here. A registry with nothing written in it produces a 12-byte
	/// blob (8-byte header plus a zero count), never a zero-length one.
	/// </summary>
	public static class SimExtCellState
	{
		/// <summary>
		/// The whole registry, every persistence class, as bytes. Sizing call when
		/// <paramref name="outBlob"/> is null; returns 0 with no allocated world.
		/// </summary>
		[DllImport("SimDLL")]
		private static extern unsafe int SIM_DebugExtCellStateAll(byte* outBlob, int capacity);

		private static bool unavailable;

		/// <summary>
		/// Whether the export is present -- i.e. whether this is a custom SimDLL new enough to
		/// have it. False only after a call has actually failed, since the export cannot be
		/// probed without calling it.
		/// </summary>
		public static bool Available
		{
			get { return !unavailable; }
		}

		// ------------------------------------------------------------------
		// The wire format. Mirrors the SimDLL's sim/ext_state.h and sim/saveblob.h; those
		// files are the authority and these are a copy that must follow them.
		// ------------------------------------------------------------------

		/// <summary>"ONIE" read little-endian -- the first four bytes of every blob.</summary>
		public const uint Magic = 0x45494E4F;

		/// <summary>Blob layout version, <c>ext_state::kVersion</c>.</summary>
		public const uint Version = 1;

		/// <summary>Magic plus version, before the extension section itself.</summary>
		public const int HeaderBytes = 8;

		private const int NameBytes = 48;
		private const int RecordHeaderBytes = NameBytes + 4 + 4 + 4 + 4 + 4;
		private const int MaxProperties = 64;

		/// <summary><c>ext::kExtF32</c>, <c>abi/sim_abi_ext.h</c>.</summary>
		public const int TypeF32 = 0;
		public const int TypeU8 = 1;
		public const int TypeU16 = 2;
		public const int TypeI32 = 3;
		public const int TypeElementIdx = 5;

		// ------------------------------------------------------------------
		// Reading the blob.
		// ------------------------------------------------------------------

		/// <summary>
		/// How many bytes the blob currently occupies, or 0 if there is no allocated world or
		/// the export is missing. The size is not derivable from the grid -- it depends on
		/// which properties are registered and which of them any cell has written -- so this
		/// call is the only way to learn it, and the sim builds the blob for both calls.
		/// </summary>
		public static unsafe int StateSize()
		{
			if (unavailable)
			{
				return 0;
			}
			try
			{
				return SIM_DebugExtCellStateAll(null, 0);
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL, or a custom one older than this export. Latched so a caller
				// polling this does not throw repeatedly.
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
		/// Fills the first <paramref name="length"/> bytes of <paramref name="destination"/>
		/// with the blob, where <paramref name="length"/> is what <see cref="StateSize"/> just
		/// returned. The allocation-free half of this API, for a caller serving the blob out of
		/// a pooled buffer every frame; <see cref="TryGetState"/> is the convenient one.
		///
		/// Returns false if the sim declined to fill it -- including the case where the world
		/// changed size between the two calls, which shows up as a different returned length.
		/// </summary>
		public static unsafe bool TryFillState(byte[] destination, int length)
		{
			if (unavailable || destination == null || length <= 0 || destination.Length < length)
			{
				return false;
			}
			fixed (byte* p = destination)
			{
				return SIM_DebugExtCellStateAll(p, length) == length;
			}
		}

		/// <summary>
		/// The whole registry as a freshly allocated array, or false with a null blob on a
		/// SimDLL without the export and on a sim with no allocated world.
		///
		/// Allocates on every call, so a caller reading this per frame should size with
		/// <see cref="StateSize"/> and fill with <see cref="TryFillState"/> into a buffer it
		/// keeps: a world with an active gas mixture puts about 13 MB through here.
		/// </summary>
		public static bool TryGetState(out byte[] blob)
		{
			blob = null;
			int count = StateSize();
			if (count <= 0)
			{
				return false;
			}
			byte[] b = new byte[count];
			if (!TryFillState(b, count))
			{
				return false;
			}
			blob = b;
			return true;
		}

		// ------------------------------------------------------------------
		// The listing. A SECOND reader of the format, and deliberately a shallow one: it walks
		// the record headers and never interprets the payload except to count how many cells
		// differ from the default. A client that wants values must decode the bytes with the
		// sim's own decoder (sim/saveblob.h is header-only and depends on nothing but std),
		// not with this -- this exists so a human with curl can see what a world holds.
		// ------------------------------------------------------------------

		/// <summary>One record's header, plus where its bytes are in the blob.</summary>
		public sealed class Property
		{
			public string Name;
			public int Type;
			public int Arity;
			/// <summary>Bytes per component: 4 for f32/i32, 1 for u8, 2 for u16.</summary>
			public int Stride;
			/// <summary>PADDED cells, so grid width+2 by height+2. See the class remarks.</summary>
			public int CellCount;
			/// <summary>The registered default, as the raw 32-bit pattern.</summary>
			public uint DefaultBits;
			/// <summary>Offset of this record's payload within the blob.</summary>
			public int ByteOffset;
			/// <summary>Length of that payload, <c>CellCount * Arity * Stride</c>.</summary>
			public int ByteLength;

			/// <summary>The type's short name, as the ABI spells it.</summary>
			public string TypeName
			{
				get
				{
					switch (Type)
					{
						case TypeF32: return "f32";
						case TypeU8: return "u8";
						case TypeU16: return "u16";
						case TypeI32: return "i32";
						case TypeElementIdx: return "element";
						default: return "?";
					}
				}
			}

			/// <summary>
			/// The registered default as a number. The bit pattern is reinterpreted per type
			/// exactly as <c>sim_abi_ext.h</c> documents for <c>valueBits</c>: bit-cast for
			/// f32, the low byte / low two bytes for u8 / u16, verbatim for i32.
			/// </summary>
			public double DefaultValue
			{
				get
				{
					switch (Type)
					{
						case TypeF32: return BitConverter.ToSingle(BitConverter.GetBytes(DefaultBits), 0);
						case TypeU8: return DefaultBits & 0xFFu;
						case TypeU16:
						case TypeElementIdx: return DefaultBits & 0xFFFFu;
						case TypeI32: return unchecked((int)DefaultBits);
						default: return 0.0;
					}
				}
			}
		}

		private static uint ReadU32(byte[] b, int offset)
		{
			// Explicit little-endian, matching the sim's own memcpy of a little-endian host.
			// The wire order is defined by the format, not by whatever this machine is.
			return (uint)b[offset] | ((uint)b[offset + 1] << 8) | ((uint)b[offset + 2] << 16) |
				((uint)b[offset + 3] << 24);
		}

		/// <summary>
		/// Walks a blob's record headers. Returns false with <paramref name="error"/> filled on
		/// anything it cannot account for -- a wrong magic, an unknown version, a count past
		/// what the registry can hold, a length that runs off the end, or trailing bytes.
		/// Bounds-checked against the remaining bytes before any length is used, because the
		/// extension section is the one part of the format whose size comes out of the blob
		/// rather than out of the grid.
		///
		/// A blob with no records is a success with an empty list: that is a world where
		/// nothing has been written, which is not an error.
		/// </summary>
		public static bool Describe(byte[] blob, out List<Property> properties, out string error)
		{
			properties = new List<Property>();
			error = null;
			if (blob == null || blob.Length < HeaderBytes + 4)
			{
				error = "the extension blob is shorter than its own header";
				return false;
			}
			if (ReadU32(blob, 0) != Magic)
			{
				error = "the extension blob does not open with the ONIE magic";
				return false;
			}
			uint version = ReadU32(blob, 4);
			if (version != Version)
			{
				error = "the extension blob is version " + version.ToString() + ", this build reads " +
					Version.ToString();
				return false;
			}

			int cursor = HeaderBytes;
			int count = unchecked((int)ReadU32(blob, cursor));
			cursor += 4;
			if (count < 0 || count > MaxProperties)
			{
				error = "the extension blob claims " + count.ToString() +
					" properties, which the registry cannot hold";
				return false;
			}

			for (int i = 0; i < count; i++)
			{
				if (blob.Length - cursor < RecordHeaderBytes)
				{
					error = "the extension blob is truncated inside record " + i.ToString();
					return false;
				}
				var p = new Property();
				int nameEnd = cursor;
				while (nameEnd < cursor + NameBytes && blob[nameEnd] != 0)
				{
					nameEnd++;
				}
				p.Name = System.Text.Encoding.ASCII.GetString(blob, cursor, nameEnd - cursor);
				p.Type = unchecked((int)ReadU32(blob, cursor + NameBytes));
				p.Arity = unchecked((int)ReadU32(blob, cursor + NameBytes + 4));
				p.Stride = unchecked((int)ReadU32(blob, cursor + NameBytes + 8));
				p.CellCount = unchecked((int)ReadU32(blob, cursor + NameBytes + 12));
				p.DefaultBits = ReadU32(blob, cursor + NameBytes + 16);
				cursor += RecordHeaderBytes;

				if (p.Arity <= 0 || p.Stride <= 0 || p.CellCount < 0)
				{
					error = "record '" + p.Name + "' has an impossible shape";
					return false;
				}
				long span = (long)p.CellCount * p.Arity * p.Stride;
				if (span > blob.Length - cursor)
				{
					error = "record '" + p.Name + "' claims " + span.ToString() +
						" bytes and the blob has " + (blob.Length - cursor).ToString() + " left";
					return false;
				}
				p.ByteOffset = cursor;
				p.ByteLength = (int)span;
				cursor += p.ByteLength;
				properties.Add(p);
			}

			if (cursor != blob.Length)
			{
				error = "the extension blob has " + (blob.Length - cursor).ToString() +
					" bytes after its last record";
				return false;
			}
			return true;
		}

		/// <summary>
		/// How many cells of <paramref name="p"/> hold something other than the registered
		/// default in at least one component. This is the number that says whether a property
		/// is actually doing anything: a record is in the blob because SOME cell differs, not
		/// because any particular one does.
		///
		/// Layout is cell-major, <c>(cell * arity + component) * stride</c> -- the layout
		/// <c>CellPropertyRegistry</c> stores in and <c>SaveExtProperty</c> documents.
		///
		/// The payload reads go through <see cref="BitConverter"/>, which is host-endian
		/// rather than explicitly little-endian like <see cref="ReadU32"/>. That is safe here
		/// and nowhere near the wire: the sim writes these bytes with a <c>memcpy</c> on the
		/// same process's own machine, so payload byte order is the host's by definition.
		/// </summary>
		public static int CountNonDefault(byte[] blob, Property p)
		{
			if (blob == null || p == null || p.Stride <= 0 || p.Arity <= 0)
			{
				return 0;
			}
			double def = p.DefaultValue;
			int found = 0;
			for (int cell = 0; cell < p.CellCount; cell++)
			{
				for (int comp = 0; comp < p.Arity; comp++)
				{
					int off = p.ByteOffset + (cell * p.Arity + comp) * p.Stride;
					if (off + p.Stride > blob.Length)
					{
						return found;
					}
					double v;
					switch (p.Type)
					{
						case TypeF32: v = BitConverter.ToSingle(blob, off); break;
						case TypeU8: v = blob[off]; break;
						case TypeU16:
						case TypeElementIdx: v = BitConverter.ToUInt16(blob, off); break;
						case TypeI32: v = BitConverter.ToInt32(blob, off); break;
						default: v = def; break;
					}
					if (v != def)
					{
						found++;
						break;
					}
				}
			}
			return found;
		}
	}
}
