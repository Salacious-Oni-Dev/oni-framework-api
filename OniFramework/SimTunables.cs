using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OniFramework
{
	/// <summary>The type of one <see cref="SimTunable"/>'s value. <c>OniTunableType</c>.</summary>
	public enum SimTunableType
	{
		/// <summary>A 32-bit float. Set with <see cref="SimTunables.Set(SimTunable, float, ExtOwner)"/>.</summary>
		Float = 0,
		/// <summary>A 32-bit integer. Set with <see cref="SimTunables.Set(SimTunable, int, ExtOwner)"/>.</summary>
		Int = 1,
		/// <summary>A 64-bit double. Set with <see cref="SimTunables.Set(SimTunable, double, ExtOwner)"/>.</summary>
		Double = 2,
	}

	/// <summary>What the sim says about a row beyond its range. <c>ONI_TUNABLE_FLAG_*</c>.</summary>
	[Flags]
	public enum SimTunableFlags
	{
		/// <summary>An ordinary row.</summary>
		None = 0,

		/// <summary>
		/// The row's maximum IS its stock value: it may be lowered, never raised. These are the
		/// sim's validity ceilings (the 10000 K temperature cap, the radiation cap, the reach and
		/// rate caps). Managed code and the sim's own buffers assume them, so a larger value could
		/// produce states the game rejects.
		/// </summary>
		Ceiling = 0x1,

		/// <summary>
		/// Refused while a world is allocated (<see cref="SimTunableResult.WorldLoaded"/>).
		/// <see cref="SimTunable.SubstepSeconds"/> is the only one: it is the clock contract with
		/// the managed game, not a physics coefficient. Set it in
		/// <see cref="SimExtRegistrar.Opening"/>, which runs before the world exists.
		/// </summary>
		NoWorld = 0x2,
	}

	/// <summary>
	/// The outcome of a <see cref="SimTunables"/> write. Values 0-8 are the sim's own answer
	/// (0 applied, 1-8 <c>OniExtRefusalReason</c>); 100 and up are decided on this side, before
	/// anything is sent.
	/// </summary>
	public enum SimTunableResult
	{
		/// <summary>The value is live. It holds until it is set again or reset, across loads,
		/// but is not saved.</summary>
		Applied = 0,
		/// <summary>The sim received fewer bytes than the message holds. A framework bug.</summary>
		ShortPayload = 1,
		/// <summary>The sim does not know the message. Not reachable through this class, which
		/// checks <see cref="SimTunables.Available"/> first.</summary>
		UnknownMessage = 2,
		/// <summary>No row has this id in the loaded SimDLL.</summary>
		NoSuchTunable = 3,
		/// <summary>A NaN or an infinity.</summary>
		NotFinite = 4,
		/// <summary>Outside the row's [min, max]. A <see cref="SimTunableFlags.Ceiling"/> row's
		/// max is its stock value.</summary>
		OutOfRange = 5,
		/// <summary>A <see cref="SimTunableFlags.NoWorld"/> row, changed while a world is
		/// allocated. Setting it to the value it already has is accepted.</summary>
		WorldLoaded = 6,
		/// <summary>The message's reserved bits were not zero. A framework bug.</summary>
		ReservedBits = 7,
		/// <summary>In range on its own, but it breaks a rule between two rows (a floor above
		/// its ceiling; a random roll that would pass the reroll sentinel).</summary>
		CrossField = 8,

		/// <summary>
		/// The SimDLL has no sim to apply it to: before the first <c>SIM_Initialize</c>, or
		/// between a quit to the menu and the next load. Nothing was changed. Set from
		/// <see cref="SimExtRegistrar.Opening"/> to apply at the start of every load.
		/// </summary>
		NoSim = 100,
		/// <summary>The loaded SimDLL has no tunable table: the stock DLL, or a custom one older
		/// than the table. Nothing was sent.</summary>
		Unavailable = 101,
		/// <summary>The value's type is not the row's (<see cref="SimTunableInfo.Type"/>).
		/// Nothing was sent.</summary>
		WrongType = 102,
		/// <summary>The <see cref="ExtOwner"/> was not valid. Every write names who made it,
		/// because two mods writing one row are told apart by it. Nothing was sent.</summary>
		InvalidOwner = 103,
	}

	/// <summary>One row of the sim's tunable table, as the loaded SimDLL describes it.</summary>
	public struct SimTunableInfo
	{
		/// <summary>The row.</summary>
		public SimTunable Id;
		/// <summary>The row's name in the sim (<c>GasPressureCap</c>); the enum member's name.</summary>
		public string Name;
		/// <summary>The subsystem it belongs to: Heat, Phase, Gas, Liquid, Rng, Mixture,
		/// Building, Disease, Radiation, Texture, Fizz.</summary>
		public string Group;
		/// <summary>Where the stock value comes from: KLEI (the stock game's), STN (Stationeers'),
		/// PHYS (a physical constant) or OURS (this project's).</summary>
		public string Origin;
		/// <summary>One line: what it does, and its stock value.</summary>
		public string Doc;
		/// <summary>The value's type.</summary>
		public SimTunableType Type;
		/// <summary>Ceiling and no-world flags.</summary>
		public SimTunableFlags Flags;
		/// <summary>What a reset restores: this SimDLL build's default. Equal to
		/// <see cref="Stock"/> unless the DLL was built with a tunables override.</summary>
		public double Default;
		/// <summary>The row's own value, as the SimDLL's table writes it.</summary>
		public double Stock;
		/// <summary>The smallest accepted value, inclusive.</summary>
		public double Min;
		/// <summary>The largest accepted value, inclusive.</summary>
		public double Max;
		/// <summary><see cref="Default"/>'s exact bits, as the wire carries them.</summary>
		public ulong DefaultBits;
		/// <summary><see cref="Stock"/>'s exact bits.</summary>
		public ulong StockBits;
	}

	/// <summary>
	/// The SimDLL's live-tunable numbers: every fixed number a simulation kernel uses, Klei's
	/// included, readable and settable at runtime (SimDLL <c>kSetTunable</c> and the
	/// <c>SIM_ExtTunable*</c> exports). The rows are <see cref="SimTunable"/>, generated from the
	/// sim's own table.
	///
	/// <b>What a value does, and does not do.</b> A set value is live from the next substep. It
	/// holds until it is set again or reset, across every load until the game exits, and it is
	/// <b>not saved</b>: a save stays vanilla-shaped and opens the same without the mod, and a mod
	/// that wants a value re-sends it after each launch. Every row's default is the stock
	/// game's value (or Stationeers', or a physical constant's), so nothing changes until someone
	/// sets something.
	///
	/// <b>When to set.</b> The sim drops every message until <c>SIM_Initialize</c> has built it,
	/// so a write from <c>OnLoad</c> answers <see cref="SimTunableResult.NoSim"/>. Subscribe to
	/// <see cref="SimExtRegistrar.Opening"/> and set there: it runs on every load, after the sim
	/// exists and before the world does, which is also the only moment
	/// <see cref="SimTunable.SubstepSeconds"/> may change. Setting the same value again is
	/// harmless, so re-sending on every load is the intended pattern:
	/// <code>
	/// SimExtRegistrar.Install(harmony);
	/// SimExtRegistrar.Opening += () =&gt;
	///     SimTunables.Set(SimTunable.GasPressureCap, 0.25f, owner);
	/// </code>
	/// A write while a world is running is fine for every other row: it takes effect between
	/// frames, because the message waits for the sim's worker to finish the frame in flight.
	///
	/// <b>Two mods, one row: the last writer wins</b>, and the framework logs the overwritten
	/// writer by name. There is no ownership or priority model, and there are no per-element
	/// or per-region values: one global table.
	///
	/// <b>Refusals are answers, not exceptions.</b> Every write returns a
	/// <see cref="SimTunableResult"/>; nothing here throws except a read of a row as the wrong
	/// type, which is a bug in the caller. The sim also reports each refusal on
	/// <c>sim.message_refused</c>.
	///
	/// <b>Reads are cached</b>, so reading a row in a loop costs an array lookup. The cache is
	/// updated by every write made through this class. A write made some other way (a raw
	/// <c>SIM_HandleMessage</c>) is not seen until <see cref="Refresh"/>.
	///
	/// <b>On a stock SimDLL</b> <see cref="Available"/> is false, every write answers
	/// <see cref="SimTunableResult.Unavailable"/>, and every read returns the fallback the
	/// caller passed, which should be the stock value -- the value that DLL actually uses.
	/// </summary>
	public static class SimTunables
	{
		// ------------------------------------------------------------------ exports

		[DllImport("SimDLL")]
		private static extern int SIM_ExtTunableCount();

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtTunableDescribe(int tunableId, byte* outDesc);

		[DllImport("SimDLL")]
		private static extern unsafe int SIM_ExtTunableGet(int tunableId, ulong* outBits);

		// OniExtTunableDesc, abi/sim_ext_api.h, static_assert'ed at 312 bytes native-side. Decoded
		// by offset, as SimExtMessages decodes its descriptor, so a drift reads as a failed name
		// check rather than as four wrong bytes.
		private const int DescBytes = 312;
		private const int DescNameBytes = 48;
		private const int DescOffsetGroup = 48;
		private const int DescGroupBytes = 16;
		private const int DescOffsetOrigin = 64;
		private const int DescOriginBytes = 8;
		private const int DescOffsetDoc = 72;
		private const int DescDocBytes = 192;
		private const int DescOffsetId = 264;
		private const int DescOffsetType = 268;
		private const int DescOffsetFlags = 272;
		private const int DescOffsetDefault = 280;
		private const int DescOffsetStock = 288;
		private const int DescOffsetMin = 296;
		private const int DescOffsetMax = 304;

		// OniSetTunableMessage: { int32 tunableId; uint32 reserved; uint64 valueBits }.
		private const int MessageBytes = 16;
		private const int ResetAllId = -1;

		// ------------------------------------------------------------------ state

		private static SimTunableInfo[] table;
		// Per enum id: false when the loaded DLL has no row there, or a row of another name.
		private static bool[] usable;
		private static ulong[] live;
		private static string[] writer;
		private static bool absent;

		/// <summary>
		/// Raised after a write through this class changes a row's live value, once per row
		/// changed (a <see cref="ResetAll"/> raises it for every row it moved). Not raised for a
		/// write that set the value the row already had, nor for a refused one. Runs on the
		/// thread that wrote, which for the game is the main thread.
		///
		/// A managed copy of a sim number belongs here: read it through <see cref="GetFloat"/>
		/// and let this say when to read it again. A hardcoded copy is wrong the moment anyone
		/// tunes the row, and nothing would say so.
		/// </summary>
		public static event Action<SimTunable> Changed;

		/// <summary>
		/// Whether the loaded SimDLL has the tunable table. False on the stock DLL and on a custom
		/// one older than it. Probing calls the DLL, so the first read of this loads it.
		/// </summary>
		public static bool Available
		{
			get { return EnsureTable(); }
		}

		/// <summary>How many rows the loaded SimDLL has; 0 when <see cref="Available"/> is
		/// false. A newer DLL may have rows past the last <see cref="SimTunable"/> member.</summary>
		public static int Count
		{
			get { return EnsureTable() ? table.Length : 0; }
		}

		/// <summary>
		/// Whether this framework's <paramref name="id"/> names the same row in the loaded DLL:
		/// the DLL has a row at that id and it has the member's name. False for every row when
		/// <see cref="Available"/> is false. A row this answers false for is refused on write and
		/// read as the caller's fallback, rather than written to whatever row sits at that id.
		/// </summary>
		public static bool IsKnown(SimTunable id)
		{
			int i = (int)id;
			return EnsureTable() && i >= 0 && i < usable.Length && usable[i];
		}

		/// <summary>The loaded DLL's description of a row. False when
		/// <see cref="IsKnown"/> is.</summary>
		public static bool TryDescribe(SimTunable id, out SimTunableInfo info)
		{
			if (!IsKnown(id))
			{
				info = default(SimTunableInfo);
				return false;
			}
			info = table[(int)id];
			return true;
		}

		/// <summary>
		/// Every row the loaded DLL describes, in id order, including any past the last
		/// <see cref="SimTunable"/> member (their <see cref="SimTunableInfo.Id"/> is still the
		/// wire id). Empty when <see cref="Available"/> is false. A copy: editing it changes
		/// nothing.
		/// </summary>
		public static SimTunableInfo[] All()
		{
			return EnsureTable() ? (SimTunableInfo[])table.Clone() : new SimTunableInfo[0];
		}

		/// <summary>The row with this exact name (case-sensitive), as the DLL spells it.</summary>
		public static bool TryFind(string name, out SimTunable id)
		{
			id = default(SimTunable);
			if (string.IsNullOrEmpty(name) || !EnsureTable())
			{
				return false;
			}
			for (int i = 0; i < table.Length; i++)
			{
				if (table[i].Name == name)
				{
					id = (SimTunable)i;
					return true;
				}
			}
			return false;
		}

		// ------------------------------------------------------------------ reads

		/// <summary>
		/// A float row's live value, or <paramref name="fallback"/> when the row is not
		/// <see cref="IsKnown"/> -- so pass the stock value, which is what a DLL without the
		/// table uses. Throws <see cref="ArgumentException"/> for a row that is not a float.
		/// </summary>
		public static float GetFloat(SimTunable id, float fallback)
		{
			ulong bits;
			if (!TryLive(id, SimTunableType.Float, out bits))
			{
				return fallback;
			}
			return FloatOf(bits);
		}

		/// <summary>An int row's live value; see <see cref="GetFloat"/>.</summary>
		public static int GetInt(SimTunable id, int fallback)
		{
			ulong bits;
			if (!TryLive(id, SimTunableType.Int, out bits))
			{
				return fallback;
			}
			return unchecked((int)(uint)bits);
		}

		/// <summary>A double row's live value; see <see cref="GetFloat"/>.</summary>
		public static double GetDouble(SimTunable id, double fallback)
		{
			ulong bits;
			if (!TryLive(id, SimTunableType.Double, out bits))
			{
				return fallback;
			}
			return DoubleOf(bits);
		}

		/// <summary>Any row's live value widened to a double (exact for all three types), for
		/// display and logging. False when the row is not <see cref="IsKnown"/>.</summary>
		public static bool TryGet(SimTunable id, out double value)
		{
			value = 0.0;
			if (!IsKnown(id))
			{
				return false;
			}
			value = ValueOf(table[(int)id].Type, live[(int)id]);
			return true;
		}

		/// <summary>
		/// Re-reads every row from the DLL and raises <see cref="Changed"/> for each that moved.
		/// Only needed after a write that did not go through this class.
		/// </summary>
		public static void Refresh()
		{
			if (!EnsureTable())
			{
				return;
			}
			ReloadLive();
		}

		// ------------------------------------------------------------------ writes

		/// <summary>Sets a float row. See <see cref="SimTunables"/> for when a write can land.</summary>
		public static SimTunableResult Set(SimTunable id, float value, ExtOwner owner)
		{
			return Write(id, SimTunableType.Float, BitsOf(value), owner);
		}

		/// <summary>Sets an int row.</summary>
		public static SimTunableResult Set(SimTunable id, int value, ExtOwner owner)
		{
			return Write(id, SimTunableType.Int, unchecked((uint)value), owner);
		}

		/// <summary>Sets a double row.</summary>
		public static SimTunableResult Set(SimTunable id, double value, ExtOwner owner)
		{
			return Write(id, SimTunableType.Double, BitsOf(value), owner);
		}

		/// <summary>Restores one row to <see cref="SimTunableInfo.Default"/>.</summary>
		public static SimTunableResult Reset(SimTunable id, ExtOwner owner)
		{
			if (!IsKnown(id))
			{
				return EnsureTable() ? SimTunableResult.NoSuchTunable : SimTunableResult.Unavailable;
			}
			SimTunableInfo info = table[(int)id];
			return Write(id, info.Type, info.DefaultBits, owner);
		}

		/// <summary>
		/// Restores every row to its default in one message. Refused as a whole
		/// (<see cref="SimTunableResult.WorldLoaded"/>) while a world is allocated and
		/// <see cref="SimTunable.SubstepSeconds"/> is not at its default; reset the other rows one
		/// at a time then.
		/// </summary>
		public static SimTunableResult ResetAll(ExtOwner owner)
		{
			if (!EnsureTable())
			{
				return SimTunableResult.Unavailable;
			}
			if (!owner.IsValid)
			{
				return SimTunableResult.InvalidOwner;
			}
			SimTunableResult result = Send(ResetAllId, 0UL);
			if (result != SimTunableResult.Applied)
			{
				FrameworkLog.Warn("SimTunables: " + owner + "'s reset of every row was refused: "
					+ result);
				return result;
			}
			string[] before = (string[])writer.Clone();
			for (int i = 0; i < writer.Length; i++)
			{
				writer[i] = null;
			}
			FrameworkLog.Info("SimTunables: " + owner + " reset every row to its default"
				+ DescribeWriters(before));
			ReloadLive();
			return result;
		}

		private static SimTunableResult Write(SimTunable id, SimTunableType type, ulong bits,
			ExtOwner owner)
		{
			if (!EnsureTable())
			{
				return SimTunableResult.Unavailable;
			}
			int i = (int)id;
			if (!IsKnown(id))
			{
				FrameworkLog.Warn("SimTunables: " + owner + " wrote " + id + ", which the loaded "
					+ "SimDLL does not have under that id; nothing was sent.");
				return SimTunableResult.NoSuchTunable;
			}
			if (!owner.IsValid)
			{
				FrameworkLog.Warn("SimTunables: a write of " + id + " named no valid owner; "
					+ "nothing was sent.");
				return SimTunableResult.InvalidOwner;
			}
			SimTunableInfo info = table[i];
			if (info.Type != type)
			{
				FrameworkLog.Warn("SimTunables: " + owner + " wrote " + id + " as " + type
					+ ", but the row is " + info.Type + "; nothing was sent.");
				return SimTunableResult.WrongType;
			}

			ulong old = live[i];
			SimTunableResult result = Send(i, bits);
			if (result != SimTunableResult.Applied)
			{
				FrameworkLog.Warn("SimTunables: " + owner + "'s " + id + " = "
					+ Format(type, bits) + " was refused: " + result
					+ (result == SimTunableResult.OutOfRange
						? " (range " + Format(type, BitsOfValue(type, info.Min)) + ".."
							+ Format(type, BitsOfValue(type, info.Max)) + ")"
						: ""));
				return result;
			}

			string who = owner.ToString();
			string previous = writer[i];
			if (previous != null && previous != who && old != bits)
			{
				// The last writer wins, and the one it overwrote is named.
				FrameworkLog.Warn("SimTunables: " + who + " set " + id + " to "
					+ Format(type, bits) + ", overwriting " + previous + "'s "
					+ Format(type, old) + ". The last writer wins.");
			}
			else if (old != bits)
			{
				FrameworkLog.Info("SimTunables: " + who + " set " + id + " to "
					+ Format(type, bits) + " (was " + Format(type, old) + ")");
			}
			writer[i] = bits == info.DefaultBits ? null : who;

			// Read back rather than trusting the bits sent: the sim is the authority, and a
			// readback that disagrees is a defect worth one line.
			ulong now;
			if (ReadBits(i, out now) && now != bits)
			{
				FrameworkLog.Warn("SimTunables: " + id + " was applied but reads back "
					+ Format(type, now) + ", not " + Format(type, bits));
			}
			else
			{
				now = bits;
			}
			live[i] = now;
			if (now != old)
			{
				Raise((SimTunable)i);
			}
			return result;
		}

		private static unsafe SimTunableResult Send(int id, ulong bits)
		{
			byte* msg = stackalloc byte[MessageBytes];
			*(int*)msg = id;
			*(uint*)(msg + 4) = 0u;
			*(ulong*)(msg + 8) = bits;
			IntPtr reply = global::Sim.SIM_HandleMessage(OniExtMessages.SetTunable, MessageBytes,
				msg);
			if (reply == IntPtr.Zero)
			{
				return SimTunableResult.NoSim;
			}
			return (SimTunableResult)Marshal.ReadInt32(reply);
		}

		// ------------------------------------------------------------------ table

		private static bool TryLive(SimTunable id, SimTunableType type, out ulong bits)
		{
			bits = 0UL;
			if (!IsKnown(id))
			{
				return false;
			}
			int i = (int)id;
			if (table[i].Type != type)
			{
				throw new ArgumentException("SimTunables: " + id + " is a " + table[i].Type
					+ " row, not a " + type + " one.", "id");
			}
			bits = live[i];
			return true;
		}

		private static unsafe bool EnsureTable()
		{
			if (table != null)
			{
				return true;
			}
			if (absent)
			{
				return false;
			}
			int count;
			try
			{
				count = SIM_ExtTunableCount();
			}
			catch (EntryPointNotFoundException)
			{
				absent = true;
				return false;
			}
			catch (DllNotFoundException)
			{
				absent = true;
				return false;
			}
			if (count <= 0)
			{
				absent = true;
				return false;
			}

			SimTunableInfo[] rows = new SimTunableInfo[count];
			byte* raw = stackalloc byte[DescBytes];
			for (int i = 0; i < count; i++)
			{
				int ok;
				try
				{
					ok = SIM_ExtTunableDescribe(i, raw);
				}
				catch (EntryPointNotFoundException)
				{
					FrameworkLog.Warn("SimTunables: SIM_ExtTunableCount is exported but "
						+ "SIM_ExtTunableDescribe is not; treating the table as unavailable.");
					absent = true;
					return false;
				}
				IntPtr p = new IntPtr(raw);
				if (ok == 0 || Marshal.ReadInt32(p, DescOffsetId) != i)
				{
					FrameworkLog.Warn("SimTunables: SIM_ExtTunableDescribe refused or misreported "
						+ "row " + i + " of " + count + "; treating the table as unavailable.");
					absent = true;
					return false;
				}
				SimTunableType type = (SimTunableType)Marshal.ReadInt32(p, DescOffsetType);
				rows[i].Id = (SimTunable)i;
				rows[i].Name = SimExtRegistry.ReadName(p, 0, DescNameBytes);
				rows[i].Group = SimExtRegistry.ReadName(p, DescOffsetGroup, DescGroupBytes);
				rows[i].Origin = SimExtRegistry.ReadName(p, DescOffsetOrigin, DescOriginBytes);
				rows[i].Doc = SimExtRegistry.ReadName(p, DescOffsetDoc, DescDocBytes);
				rows[i].Type = type;
				rows[i].Flags = (SimTunableFlags)Marshal.ReadInt32(p, DescOffsetFlags);
				rows[i].DefaultBits = (ulong)Marshal.ReadInt64(p, DescOffsetDefault);
				rows[i].StockBits = (ulong)Marshal.ReadInt64(p, DescOffsetStock);
				rows[i].Default = ValueOf(type, rows[i].DefaultBits);
				rows[i].Stock = ValueOf(type, rows[i].StockBits);
				rows[i].Min = ValueOf(type, (ulong)Marshal.ReadInt64(p, DescOffsetMin));
				rows[i].Max = ValueOf(type, (ulong)Marshal.ReadInt64(p, DescOffsetMax));
			}

			// The enum is generated from the same rows, so every member should name its row.
			// One that does not is a DLL built from a different table (older, or a branch that
			// appended other rows): that member is made unusable rather than aimed at whatever
			// row holds its id.
			string[] names = Enum.GetNames(typeof(SimTunable));
			Array values = Enum.GetValues(typeof(SimTunable));
			int top = 0;
			foreach (object v in values)
			{
				top = Math.Max(top, (int)v + 1);
			}
			bool[] ok2 = new bool[Math.Max(top, count)];
			int mismatched = 0;
			for (int k = 0; k < names.Length; k++)
			{
				int id = (int)values.GetValue(k);
				if (id < count && rows[id].Name == names[k])
				{
					ok2[id] = true;
				}
				else
				{
					mismatched++;
					FrameworkLog.Warn("SimTunables: SimTunable." + names[k] + " (id " + id + ") is "
						+ (id < count ? "row \"" + rows[id].Name + "\"" : "past the end")
						+ " in the loaded SimDLL; it will read as its fallback and refuse writes.");
				}
			}

			table = rows;
			usable = ok2;
			writer = new string[count];
			live = new ulong[count];
			for (int i = 0; i < count; i++)
			{
				// The first read is a baseline, not a change: Changed stays quiet.
				ReadBits(i, out live[i]);
			}
			FrameworkLog.Info("SimTunables: " + count + " rows in the loaded SimDLL"
				+ (mismatched == 0 ? "" : ", " + mismatched + " SimTunable member(s) unusable"));
			return true;
		}

		private static void ReloadLive()
		{
			for (int i = 0; i < table.Length; i++)
			{
				ulong bits;
				if (!ReadBits(i, out bits))
				{
					continue;
				}
				ulong old = live[i];
				live[i] = bits;
				if (bits != old && i < usable.Length && usable[i])
				{
					Raise((SimTunable)i);
				}
			}
		}

		private static unsafe bool ReadBits(int id, out ulong bits)
		{
			ulong b = 0UL;
			int ok = SIM_ExtTunableGet(id, &b);
			bits = b;
			return ok != 0;
		}

		private static void Raise(SimTunable id)
		{
			Action<SimTunable> handler = Changed;
			if (handler == null)
			{
				return;
			}
			try
			{
				handler(id);
			}
			catch (Exception e)
			{
				FrameworkLog.Warn("SimTunables: a Changed handler threw for " + id + ": " + e);
			}
		}

		// ------------------------------------------------------------------ bits

		private static unsafe float FloatOf(ulong bits)
		{
			uint u = (uint)bits;
			return *(float*)&u;
		}

		private static unsafe double DoubleOf(ulong bits)
		{
			return *(double*)&bits;
		}

		private static unsafe ulong BitsOf(float value)
		{
			return *(uint*)&value;
		}

		private static unsafe ulong BitsOf(double value)
		{
			return *(ulong*)&value;
		}

		private static double ValueOf(SimTunableType type, ulong bits)
		{
			switch (type)
			{
				case SimTunableType.Float: return FloatOf(bits);
				case SimTunableType.Int: return unchecked((int)(uint)bits);
				default: return DoubleOf(bits);
			}
		}

		private static ulong BitsOfValue(SimTunableType type, double value)
		{
			switch (type)
			{
				case SimTunableType.Float: return BitsOf((float)value);
				case SimTunableType.Int: return unchecked((uint)(int)value);
				default: return BitsOf(value);
			}
		}

		internal static string Format(SimTunableType type, ulong bits)
		{
			switch (type)
			{
				case SimTunableType.Float: return FloatOf(bits).ToString("R");
				case SimTunableType.Int: return unchecked((int)(uint)bits).ToString();
				default: return DoubleOf(bits).ToString("R");
			}
		}

		private static string DescribeWriters(string[] before)
		{
			List<string> held = new List<string>();
			for (int i = 0; i < before.Length; i++)
			{
				if (before[i] != null)
				{
					held.Add(table[i].Name + " (" + before[i] + ")");
				}
			}
			return held.Count == 0 ? "" : "; that undid " + string.Join(", ", held.ToArray());
		}
	}
}
