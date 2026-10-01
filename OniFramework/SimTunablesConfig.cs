using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;

namespace OniFramework
{
	/// <summary>
	/// <c>sim-tunables.json</c>: tuning the SimDLL without writing a mod. The file sits in the
	/// framework's own mod folder and holds one flat object, a row name to a value:
	/// <code>
	/// {
	///   "GasPressureCap": 0.25,
	///   "RoomMixingEveryNTicks": 2,
	///   "RandomScale": "0x38000100"
	/// }
	/// </code>
	/// A name is a <see cref="SimTunable"/> member, spelled exactly. A value is a JSON number of
	/// the row's type (an int row takes an integer), or a string <c>"0x..."</c> holding the
	/// value's raw bits, as the build-time override (<c>ONI_TUNABLES_CFG</c>) takes them.
	///
	/// Applied at the start of every load, through <see cref="SimExtRegistrar.Opening"/>, which
	/// is also the only moment <c>SubstepSeconds</c> can change. Written as the owner
	/// <c>sim_tunables_json</c>, so a mod that later sets the same row is logged as overwriting it
	/// (and the reverse). A row the sim refuses is logged with its reason and skipped; the rest
	/// still apply. A file that is not valid JSON of this shape applies nothing, and says where
	/// it stopped.
	///
	/// No file, no effect: nothing is patched and nothing is sent. The native DLL reads no
	/// files; this is the only reader.
	/// </summary>
	internal static class SimTunablesConfig
	{
		internal const string FileName = "sim-tunables.json";

		private static List<KeyValuePair<string, string>> entries;
		private static string path;
		private static ExtOwner owner;

		internal static void Load(Harmony harmony, string modFolder)
		{
			if (string.IsNullOrEmpty(modFolder))
			{
				return;
			}
			path = Path.Combine(modFolder, FileName);
			if (!File.Exists(path))
			{
				return;
			}
			string error;
			entries = Parse(File.ReadAllText(path), out error);
			if (entries == null)
			{
				FrameworkLog.Warn("SimTunables: " + path + " was not applied: " + error);
				return;
			}
			owner = SimExtRegistry.ClaimOwner("sim-tunables.json");
			SimExtRegistrar.Install(harmony);
			SimExtRegistrar.Opening += Apply;
			FrameworkLog.Info("SimTunables: " + path + " holds " + entries.Count
				+ " row(s); they apply at the start of every load.");
		}

		private static void Apply()
		{
			if (!SimTunables.Available)
			{
				FrameworkLog.Warn("SimTunables: " + FileName + " is present but the loaded SimDLL "
					+ "has no tunable table; nothing applied.");
				return;
			}
			int applied = 0, refused = 0;
			foreach (KeyValuePair<string, string> e in entries)
			{
				SimTunable id;
				SimTunableInfo info;
				if (!SimTunables.TryFind(e.Key, out id) || !SimTunables.TryDescribe(id, out info))
				{
					FrameworkLog.Warn("SimTunables: " + FileName + ": \"" + e.Key
						+ "\" is not a row of the loaded SimDLL; skipped.");
					refused++;
					continue;
				}
				SimTunableResult r;
				string why;
				if (!TryApply(id, info.Type, e.Value, out r, out why))
				{
					FrameworkLog.Warn("SimTunables: " + FileName + ": " + e.Key + " = " + e.Value
						+ " is not a valid " + info.Type + " value (" + why + "); skipped.");
					refused++;
					continue;
				}
				if (r == SimTunableResult.Applied)
				{
					applied++;
				}
				else
				{
					// SimTunables has already logged the reason and the row's range.
					refused++;
				}
			}
			FrameworkLog.Info("SimTunables: " + FileName + ": " + applied + " applied, " + refused
				+ " refused.");
		}

		private static bool TryApply(SimTunable id, SimTunableType type, string text,
			out SimTunableResult result, out string why)
		{
			result = SimTunableResult.Unavailable;
			why = null;
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				ulong bits;
				if (!ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier,
					CultureInfo.InvariantCulture, out bits))
				{
					why = "not hexadecimal";
					return false;
				}
				if (type != SimTunableType.Double && bits > uint.MaxValue)
				{
					why = "more than 32 bits for a 32-bit row";
					return false;
				}
				result = SetBits(id, type, bits);
				return true;
			}
			switch (type)
			{
				case SimTunableType.Float:
					float f;
					if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
						out f))
					{
						why = "not a number";
						return false;
					}
					result = SimTunables.Set(id, f, owner);
					return true;
				case SimTunableType.Int:
					int n;
					if (!int.TryParse(text, NumberStyles.AllowLeadingSign,
						CultureInfo.InvariantCulture, out n))
					{
						why = "not an integer";
						return false;
					}
					result = SimTunables.Set(id, n, owner);
					return true;
				default:
					double d;
					if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
						out d))
					{
						why = "not a number";
						return false;
					}
					result = SimTunables.Set(id, d, owner);
					return true;
			}
		}

		private static unsafe SimTunableResult SetBits(SimTunable id, SimTunableType type,
			ulong bits)
		{
			switch (type)
			{
				case SimTunableType.Float:
					uint u = (uint)bits;
					return SimTunables.Set(id, *(float*)&u, owner);
				case SimTunableType.Int:
					return SimTunables.Set(id, unchecked((int)(uint)bits), owner);
				default:
					return SimTunables.Set(id, *(double*)&bits, owner);
			}
		}

		// ------------------------------------------------------------------ parser

		/// <summary>
		/// Parses one flat JSON object of name to number-or-string, keeping each value's literal
		/// text so a float is parsed once, as a float. Returns null, with the reason and offset,
		/// for anything else -- nesting, arrays, a repeated name, trailing content. Internal so
		/// the self-test can exercise it.
		/// </summary>
		internal static List<KeyValuePair<string, string>> Parse(string text, out string error)
		{
			error = null;
			List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
			int i = 0;
			Skip(text, ref i);
			if (i >= text.Length || text[i] != '{')
			{
				error = "expected '{' at offset " + i;
				return null;
			}
			i++;
			Skip(text, ref i);
			if (i < text.Length && text[i] == '}')
			{
				i++;
			}
			else
			{
				while (true)
				{
					string name;
					if (!ReadString(text, ref i, out name))
					{
						error = "expected a quoted row name at offset " + i;
						return null;
					}
					if (!seen.Add(name))
					{
						error = "\"" + name + "\" is given twice";
						return null;
					}
					Skip(text, ref i);
					if (i >= text.Length || text[i] != ':')
					{
						error = "expected ':' after \"" + name + "\" at offset " + i;
						return null;
					}
					i++;
					Skip(text, ref i);
					string value;
					if (i < text.Length && text[i] == '"')
					{
						if (!ReadString(text, ref i, out value))
						{
							error = "unterminated string for \"" + name + "\"";
							return null;
						}
					}
					else
					{
						int start = i;
						while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '-'
							|| text[i] == '+' || text[i] == '.' || text[i] == 'e' || text[i] == 'E'))
						{
							i++;
						}
						if (i == start)
						{
							error = "\"" + name + "\" needs a number or a \"0x...\" string, at "
								+ "offset " + i;
							return null;
						}
						value = text.Substring(start, i - start);
					}
					result.Add(new KeyValuePair<string, string>(name, value));
					Skip(text, ref i);
					if (i < text.Length && text[i] == ',')
					{
						i++;
						Skip(text, ref i);
						continue;
					}
					if (i < text.Length && text[i] == '}')
					{
						i++;
						break;
					}
					error = "expected ',' or '}' at offset " + i;
					return null;
				}
			}
			Skip(text, ref i);
			if (i != text.Length)
			{
				error = "unexpected content after the object at offset " + i;
				return null;
			}
			return result;
		}

		private static void Skip(string text, ref int i)
		{
			while (i < text.Length && char.IsWhiteSpace(text[i]))
			{
				i++;
			}
		}

		private static bool ReadString(string text, ref int i, out string value)
		{
			value = null;
			if (i >= text.Length || text[i] != '"')
			{
				return false;
			}
			StringBuilder sb = new StringBuilder();
			i++;
			while (i < text.Length && text[i] != '"')
			{
				if (text[i] == '\\')
				{
					// Row names and hex strings need no escapes; refusing them keeps this small.
					return false;
				}
				sb.Append(text[i]);
				i++;
			}
			if (i >= text.Length)
			{
				return false;
			}
			i++;
			value = sb.ToString();
			return true;
		}
	}
}
