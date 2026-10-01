using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Which SimDLL this process actually loaded, and whether it is ours at all.
	///
	/// <b>The problem this exists for.</b> A SimDLL is invisible. It exports the same names as
	/// every other SimDLL, answers the same messages, and differs only in behaviour -- which is
	/// precisely the thing under test whenever it matters. A stale one therefore looks exactly
	/// like a current one right up until a result comes out wrong, and a regression can be
	/// chased through several theories before the deployed DLL turns out to predate the fix.
	/// This class answers the question inside the running game, where
	/// <see cref="BuildStamp"/> puts it on screen.
	///
	/// <b>Two independent answers, on purpose.</b> <see cref="Version"/> is what the DLL says
	/// about itself (a string compiled into it by <c>sim/build.sh</c>); <see cref="FileHash"/>
	/// is what the bytes on disk hash to. The first is readable and the second is unforgeable,
	/// and they fail differently -- a DLL rebuilt from identical source keeps both, a DLL built
	/// from a dirty tree keeps the version and changes the hash. Neither alone answers "is the
	/// thing running the thing I just built"; together they do.
	///
	/// <b>Detecting a stock SimDLL.</b> Klei's does not export <c>SIM_Version</c>, so the call
	/// throws <see cref="EntryPointNotFoundException"/> and <see cref="Version"/> comes back
	/// <c>null</c>. That is the entire mechanism: nothing has to be added to a vanilla DLL, and
	/// there is no negotiation to get wrong. It is also the same probe-by-calling shape
	/// <see cref="SimRandom"/> uses, for the same reason -- an export cannot be tested for
	/// without calling it.
	/// </summary>
	public static class SimVersion
	{
		[DllImport("SimDLL")]
		private static extern IntPtr SIM_Version();

		private static bool queried;
		private static bool hashed;
		private static string version;
		private static string fileHash;
		private static string filePath;

		/// <summary>
		/// The custom SimDLL's self-reported version (<c>MAJOR.MINOR.REV+SHA</c>, with a
		/// trailing <c>*</c> if it was built from a dirty tree), or <c>null</c> on a stock
		/// SimDLL. Queried once and cached; a DLL cannot change under a running process.
		/// </summary>
		public static string Version
		{
			get { Query(); return version; }
		}

		/// <summary>
		/// True when the loaded SimDLL is this project's replacement rather than Klei's.
		/// </summary>
		public static bool IsCustom
		{
			get { return Version != null; }
		}

		/// <summary>
		/// First 7 hex characters of the SHA-256 of the SimDLL file on disk, or <c>null</c> if
		/// it could not be read. Seven because this is a screen label and the point is to
		/// compare it by eye against <c>sha256sum</c> on the build output, not to be
		/// collision-proof.
		/// </summary>
		public static string FileHash
		{
			get { HashSimDll(); return fileHash; }
		}

		/// <summary>
		/// The path <see cref="FileHash"/> was computed from, or <c>null</c>. Worth showing in a
		/// tooltip: a retail install and a separate development install each have their own
		/// SimDLL, and confusing them is an easy mistake.
		/// </summary>
		public static string FilePath
		{
			get { HashSimDll(); return filePath; }
		}

		private static void Query()
		{
			if (queried)
			{
				return;
			}

			try
			{
				IntPtr ptr = SIM_Version();
				queried = true;
				if (ptr != IntPtr.Zero)
				{
					// The DLL owns this string literal for the life of the process, so this
					// copies and frees nothing -- PtrToStringAnsi is the right marshal here and
					// Marshal.FreeHGlobal would be a crash.
					version = Marshal.PtrToStringAnsi(ptr);
				}
			}
			catch (EntryPointNotFoundException)
			{
				// A stock SimDLL. Expected, not an error, and the answer to the question.
				queried = true;
				version = null;
			}
			catch (Exception e)
			{
				// DllNotFoundException and friends. Deliberately does NOT set `queried`: an
				// answer that only means "the native library was not resolvable at the moment I
				// asked" must not latch as "this is a stock SimDLL", which is what a cached null
				// would claim for the rest of the session. Retried on the next call instead.
				Debug.LogWarning("[OniFramework] SimVersion: could not query SIM_Version: " + e.Message);
				version = null;
			}

			HashSimDll();
		}

		/// <summary>Whether the game has loaded SimDLL yet. Off Windows there is no cheap way to
		/// ask, so it is taken as loaded.</summary>
		internal static bool SimLoaded()
		{
			try
			{
				return SimDllDelivery.IsLoaded();
			}
			catch (Exception)
			{
				return true;
			}
		}

		private static void HashSimDll()
		{
			// Its own flag, not `queried`: the version query retries after a transient native
			// failure, and the file does not need re-reading each time it does.
			if (hashed)
			{
				return;
			}
			// Latched only once the game has loaded the library. Before that the file can still
			// be replaced (SimDllDelivery swaps it when a player accepts on the main menu), and
			// reading the hash must never be what loads it.
			hashed = SimLoaded();

			try
			{
				// Application.dataPath is <install>/OxygenNotIncluded_Data; the native plugins
				// sit beside Managed. Derived rather than hard-coded because the retail and dev
				// installs live on different drives.
				string path = Path.Combine(Application.dataPath, "Plugins/x86_64/SimDLL.dll");
				if (!File.Exists(path))
				{
					return;
				}

				// Read-share the file: the process has it loaded and mapped, and an exclusive
				// open would fail.
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				using (var sha = SHA256.Create())
				{
					byte[] hash = sha.ComputeHash(stream);
					fileHash = BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 7);
				}
				filePath = path;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniFramework] SimVersion: could not hash SimDLL: " + e.Message);
			}
		}
	}
}
