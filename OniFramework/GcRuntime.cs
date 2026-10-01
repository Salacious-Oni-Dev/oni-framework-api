using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Which Mono runtime (and so which garbage collector) this process loaded, and, as a
	/// separate question, which GC timing helper is loaded.
	///
	/// <b>Why the framework asks.</b> A replacement GC runtime can swap
	/// <c>MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll</c> for a rebuild that marks in
	/// parallel. Like a SimDLL, a runtime is invisible from inside the game, and a screenshot or
	/// a run archive has to say which one produced it. <see cref="BuildStamp"/> puts
	/// <see cref="Label"/> on the watermark and <see cref="Detail"/> in its tooltip.
	///
	/// <b>No dependency either way.</b> A replacement runtime must work without the framework and
	/// the framework without it, so this only probes exports by name:
	/// <list type="bullet">
	/// <item><c>oni_gc_version</c> in the runtime: present only in a build that exports it, and returns
	/// <c>VERSION-set+commit</c>, with <c>*</c> for a dirty tree.</item>
	/// <item><c>gcstats_version</c> in <c>oni_gcstats.dll</c>, a GC timing helper.
	/// Tooltip only: it describes a diagnostic tool, not the collector.</item>
	/// </list>
	///
	/// <b>Stock is decided by hash, not by the missing export.</b> A runtime without
	/// <c>oni_gc_version</c> is not necessarily Unity's: other mods ship modified runtimes too.
	/// So without the export the file's SHA-256 is compared with the one ONI shipped, and anything
	/// else shows as unknown with its hash. A game update that ships a new runtime therefore shows
	/// as unknown until <see cref="ShippedSha256"/> is updated, which is the honest answer.
	/// </summary>
	public static class GcRuntime
	{
		/// <summary>
		/// SHA-256 of the <c>mono-2.0-bdwgc.dll</c> ONI shipped with changelist 744825 (md5
		/// <c>2228ab08758aede23c069e769abe8a7e</c>).
		/// </summary>
		public const string ShippedSha256 = "ea73d85a6ee0d776ebed64d101e6e8b20f132b658d969c137454725d94561092";

		private const string RuntimeModule = "mono-2.0-bdwgc.dll";
		private const string HelperModule = "oni_gcstats.dll";

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW(string name);

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] path, uint size);

		[DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr VersionFn();

		private static bool queried;
		private static string version;
		private static string sha256;
		private static string path;
		private static string helperVersion;

		/// <summary>
		/// The replacement runtime's self-reported version, or <c>null</c> when the loaded runtime
		/// has no <c>oni_gc_version</c> export.
		/// </summary>
		public static string Version
		{
			get { Query(); return version; }
		}

		/// <summary>
		/// True when the loaded runtime's file is byte-identical to the one ONI shipped.
		/// </summary>
		public static bool IsStock
		{
			get { Query(); return version == null && sha256 == ShippedSha256; }
		}

		/// <summary>
		/// Full SHA-256 of the loaded runtime file, or <c>null</c> if it could not be read.
		/// </summary>
		public static string FileSha256
		{
			get { Query(); return sha256; }
		}

		/// <summary>
		/// The GC timing helper's <c>gcstats_version()</c>, or <c>null</c> when no helper is
		/// loaded (or it is too old to say).
		/// </summary>
		public static string HelperVersion
		{
			get { Query(); return helperVersion; }
		}

		/// <summary>
		/// The short form for a one-line label: <c>STOCK</c>, the replacement's version without its
		/// <c>+commit</c> (keeping a dirty <c>*</c>), or <c>?#</c> plus 7 hex of the file hash.
		/// </summary>
		public static string Label
		{
			get
			{
				Query();
				if (version != null)
				{
					int plus = version.IndexOf('+');
					return plus < 0 ? version
						: version.Substring(0, plus) + (version.EndsWith("*") ? "*" : "");
				}
				if (sha256 == null)
				{
					return "?";
				}
				return sha256 == ShippedSha256 ? "STOCK" : "?#" + sha256.Substring(0, 7);
			}
		}

		/// <summary>
		/// The long form, one item per line, for a tooltip.
		/// </summary>
		public static string Detail
		{
			get
			{
				Query();
				string runtime = version != null
					? "GC runtime: custom " + version
					: sha256 == ShippedSha256
						? "GC runtime: STOCK (Unity's, as ONI shipped it)"
						: "GC runtime: UNKNOWN (no oni_gc_version export, and not the shipped file)";
				string file = "  sha256: " + (sha256 != null ? sha256.Substring(0, 16) + "..." : "unreadable")
					+ "  " + (path ?? "?");
				string helper = "GC helper: " + (helperVersion != null
					? "oni_gcstats " + helperVersion
					: "not loaded");
				return runtime + "\n" + file + "\n" + helper;
			}
		}

		private static void Query()
		{
			if (queried)
			{
				return;
			}
			queried = true;
			try
			{
				IntPtr runtime = GetModuleHandleW(RuntimeModule);
				if (runtime == IntPtr.Zero)
				{
					return;
				}
				version = CallVersion(runtime, "oni_gc_version");
				path = ModulePath(runtime);
				if (path != null && File.Exists(path))
				{
					// Read-share: the process has the file mapped.
					using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					using (var sha = SHA256.Create())
					{
						sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty)
							.ToLowerInvariant();
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] GcRuntime: could not identify the runtime: " + e.Message);
			}

			try
			{
				// Loaded by GCProbe from its native/ folder, if at all. Only looked up, never
				// loaded here: loading it would install nothing, but it is not ours to load.
				IntPtr helper = GetModuleHandleW(HelperModule);
				if (helper != IntPtr.Zero)
				{
					helperVersion = CallVersion(helper, "gcstats_version");
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] GcRuntime: could not query the GC helper: " + e.Message);
			}
		}

		private static string CallVersion(IntPtr module, string export)
		{
			IntPtr fn = GetProcAddress(module, export);
			if (fn == IntPtr.Zero)
			{
				return null;
			}
			var call = (VersionFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(VersionFn));
			IntPtr s = call();
			// A string literal the DLL owns for the life of the process: copy, never free.
			return s == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(s);
		}

		private static string ModulePath(IntPtr module)
		{
			var buffer = new char[1024];
			uint n = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
			return n == 0 || n >= buffer.Length ? null : new string(buffer, 0, (int)n);
		}
	}
}
