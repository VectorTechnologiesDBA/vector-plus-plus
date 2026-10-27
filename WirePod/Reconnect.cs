using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using chewie.Services;
using core.Services;

namespace WirePod
{
	/// <summary>
	/// When the app goes to the background Android cuts its connection to Vector, and the app immediately tries to
	/// reconnect. Android doesn't let a backgrounded app reach the network, so that attempt always times out and the
	/// app parks on its "can't connect to Vector" screen, which the user then finds on return and has to Try Again.
	/// Instead, a reconnect that fails while the app is in the background is held until the app is in front again,
	/// then retried.
	/// </summary>
	public static class Reconnect
	{
		/// <summary>
		/// Called first in RobotCommunicationConnectionModule.HandleAttemptReconnectToGeneralNetworkFinished (patched
		/// in by the Patcher). Returns true when it has taken over the failure; the patched code then clears the
		/// module's "reconnecting" flag and returns without reporting the failure.
		/// </summary>
		public static bool DeferFailure(RobotCommunicationConnectionModule module, bool success, bool cancelled)
		{
			if (success || cancelled || Reconnect.IsForeground())
			{
				return false;
			}
			if (Interlocked.Exchange(ref Reconnect._Waiting, 1) == 1)
			{
				return true;
			}
			DAS.Event("WirePod.Reconnect.DeferFailure", "reconnect failed while the app is in the background; retrying once it's in front");
			Task.Run(delegate
			{
				try
				{
					while (!Reconnect.IsForeground())
					{
						Thread.Sleep(500);
					}
					// Give Android a moment to restore the app's network access.
					Thread.Sleep(750);
				}
				finally
				{
					Interlocked.Exchange(ref Reconnect._Waiting, 0);
				}
				DAS.Event("WirePod.Reconnect", "app is in front again, reconnecting to Vector");
				module.AttemptReconnectToGeneralNetwork();
			});
			return true;
		}

		/// <summary>
		/// ActivityManager.getMyMemoryState(info).importance == IMPORTANCE_FOREGROUND (100), through JNI: the app's
		/// trimmed Android bindings don't include these APIs. Errs on "foreground" so failures are never swallowed.
		/// </summary>
		private static bool IsForeground()
		{
			IntPtr info = IntPtr.Zero;
			try
			{
				// JNIEnv.FindClass returns a GLOBAL reference in this runtime; deleting it as a local reference is a
				// fatal JNI error that kills the process. So the classes and IDs are looked up once and kept.
				lock (Reconnect.kJniLock)
				{
					if (Reconnect._InfoClass == IntPtr.Zero)
					{
						IntPtr infoClass = JNIEnv.FindClass("android/app/ActivityManager$RunningAppProcessInfo");
						IntPtr managerClass = JNIEnv.FindClass("android/app/ActivityManager");
						Reconnect._InfoCtor = JNIEnv.GetMethodID(infoClass, "<init>", "()V");
						Reconnect._ImportanceField = JNIEnv.GetFieldID(infoClass, "importance", "I");
						Reconnect._GetMyMemoryState = JNIEnv.GetStaticMethodID(managerClass, "getMyMemoryState", "(Landroid/app/ActivityManager$RunningAppProcessInfo;)V");
						Reconnect._ManagerClass = managerClass;
						Reconnect._InfoClass = infoClass;
					}
				}
				// NewObject returns a local reference: this one is freed below.
				info = JNIEnv.NewObject(Reconnect._InfoClass, Reconnect._InfoCtor);
				JNIEnv.CallStaticVoidMethod(Reconnect._ManagerClass, Reconnect._GetMyMemoryState, new JValue[] { new JValue(info) });
				return JNIEnv.GetIntField(info, Reconnect._ImportanceField) <= Reconnect.kImportanceForeground;
			}
			catch (Exception ex)
			{
				DAS.Warn("WirePod.Reconnect.IsForeground", ex.ToString());
				return true;
			}
			finally
			{
				if (info != IntPtr.Zero)
				{
					JNIEnv.DeleteLocalRef(info);
				}
			}
		}

		private const int kImportanceForeground = 100;

		private static readonly object kJniLock = new object();

		private static IntPtr _InfoClass, _ManagerClass, _InfoCtor, _ImportanceField, _GetMyMemoryState;

		private static int _Waiting;
	}
}
