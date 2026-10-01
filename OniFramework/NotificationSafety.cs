using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Keeps vanilla's <c>NotificationManager.notificationRemoved</c> event from ever being null,
	/// because Klei invokes it without a null check.
	///
	/// THE VANILLA DEFECT, in game build 744825. The two halves of that class are
	/// not written the same way:
	///
	/// <code>
	/// private void DoAddNotification(Notification notification) {
	///     notifications.Add(notification);
	///     if (this.notificationAdded != null) {      // guarded
	///         this.notificationAdded(notification);
	///     }
	/// }
	///
	/// public void RemoveNotification(Notification notification) {
	///     ...
	///     if (notifications.Remove(notification)) {
	///         this.notificationRemoved(notification);   // NOT guarded
	///     }
	/// }
	/// </code>
	///
	/// The only subscriber is <c>NotificationDisplayer.OnSpawn</c>. Wherever that has not run --
	/// a headless or rig boot, a world brought up before the notification UI, a screen torn down
	/// early -- removing any status item that carries a notification throws
	/// <c>NullReferenceException</c> from deep inside <c>KSelectable.RemoveStatusItem</c>.
	///
	/// For example, a monitor that reconciles pipe-stress markers once a second on a rig canvas
	/// dies on every pass with:
	///
	/// <code>
	/// NotificationManager.RemoveNotification
	/// Notifier.Remove
	/// StatusItemGroup.RemoveStatusItemInternal
	/// KSelectable.RemoveStatusItem
	/// Mod1ThermoFluid.PipeStressMonitor.Reconcile
	/// </code>
	///
	/// The visible symptom was a console flashing a warning a second. The real cost was that the
	/// exception unwound the whole scan, so the monitor silently stopped doing its job on every
	/// tick it fired -- a caller cannot defend against this by checking anything of its own,
	/// because nothing it owns is null.
	///
	/// WHY A NO-OP SUBSCRIBER RATHER THAN A PATCH ON THE METHOD. Subscribing costs one delegate
	/// for the life of the manager and leaves Klei's own code path running exactly as written; a
	/// transpiler or a prefix around <c>RemoveNotification</c> would have to reproduce its body to
	/// re-guard one line. A C# event with one subscriber is never null, so the unguarded invoke
	/// becomes correct rather than being worked around.
	///
	/// This belongs to the framework rather than to the mod that found it: any mod attaching a
	/// status item that carries a notification hits the same hole.
	/// </summary>
	public static class NotificationSafety
	{
		private static bool installed;

		/// <summary>Whether <see cref="Install"/> has run.</summary>
		public static bool IsInstalled => installed;

		/// <summary>
		/// Patches <c>NotificationManager.OnPrefabInit</c> so every manager instance is born with
		/// a subscriber on <c>notificationRemoved</c>.
		///
		/// <c>OnPrefabInit</c> is the hook rather than <c>OnSpawn</c> because it is where Klei
		/// assigns <c>Instance</c>, so it is the earliest moment the event exists and the last
		/// moment before anything can remove a notification. Re-entering a world builds a new
		/// manager and runs it again, so this does not need to survive a reload.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (installed)
			{
				return;
			}

			MethodInfo onPrefabInit = AccessTools.Method(typeof(NotificationManager),
				"OnPrefabInit", Type.EmptyTypes);
			if (onPrefabInit == null)
			{
				throw new InvalidOperationException(
					"NotificationSafety.Install could not find NotificationManager.OnPrefabInit(). "
					+ "Without it every status item carrying a notification can throw on removal "
					+ "whenever the notification UI is not up, so this throws rather than "
					+ "degrading silently.");
			}

			harmony.Patch(onPrefabInit, null, new HarmonyMethod(
				AccessTools.Method(typeof(NotificationSafety), "OnPrefabInitPostfix")));
			installed = true;
		}

		private static void OnPrefabInitPostfix(NotificationManager __instance)
		{
			if (__instance == null)
			{
				return;
			}
			__instance.notificationRemoved += KeepEventNonNull;
		}

		/// <summary>
		/// Deliberately does nothing. Its only job is to exist, so that
		/// <c>this.notificationRemoved(notification)</c> has something to call.
		/// </summary>
		private static void KeepEventNonNull(Notification notification)
		{
		}
	}
}
