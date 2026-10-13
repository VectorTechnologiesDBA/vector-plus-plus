using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using core.Services;

namespace WirePod
{
	/// <summary>
	/// The build targets Android 13. From Android 12, finding and talking to Vector over Bluetooth needs the
	/// BLUETOOTH_SCAN and BLUETOOTH_CONNECT runtime permissions, which this 2019 app never asks for. Ask once the
	/// first screen is up (calls go through JNI: the app's trimmed Android bindings predate these APIs).
	/// </summary>
	internal static class Permissions
	{
		public static void RequestWhenReady()
		{
			if (Permissions.SdkInt() < 31 || Interlocked.Exchange(ref Permissions._Started, 1) != 0)
			{
				return;
			}
			Task.Run(delegate
			{
				for (int i = 0; i < 120; i++)
				{
					Activity activity = Ui.TopActivity;
					if (activity != null)
					{
						activity.RunOnUiThread(delegate
						{
							Permissions.Request(activity);
						});
						return;
					}
					Thread.Sleep(500);
				}
			});
		}

		private static void Request(Activity activity)
		{
			try
			{
				string[] wanted = (Permissions.SdkInt() >= 33)
					? new string[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT", "android.permission.POST_NOTIFICATIONS" }
					: new string[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" };
				IntPtr activityClass = JNIEnv.FindClass("android/app/Activity");
				IntPtr check = JNIEnv.GetMethodID(activityClass, "checkSelfPermission", "(Ljava/lang/String;)I");
				bool missing = false;
				foreach (string permission in wanted)
				{
					IntPtr name = JNIEnv.NewString(permission);
					try
					{
						missing |= JNIEnv.CallIntMethod(activity.Handle, check, new JValue[] { new JValue(name) }) != 0;
					}
					finally
					{
						JNIEnv.DeleteLocalRef(name);
					}
				}
				if (!missing)
				{
					return;
				}
				IntPtr array = JNIEnv.NewArray(wanted);
				try
				{
					IntPtr request = JNIEnv.GetMethodID(activityClass, "requestPermissions", "([Ljava/lang/String;I)V");
					JNIEnv.CallVoidMethod(activity.Handle, request, new JValue[] { new JValue(array), new JValue(Permissions.kRequestCode) });
				}
				finally
				{
					JNIEnv.DeleteLocalRef(array);
				}
				DAS.Event("WirePod.Permissions.Request", string.Join(",", wanted));
			}
			catch (Exception ex)
			{
				DAS.Warn("WirePod.Permissions.Request", ex.ToString());
			}
		}

		private static int SdkInt()
		{
			IntPtr version = JNIEnv.FindClass("android/os/Build$VERSION");
			return JNIEnv.GetStaticIntField(version, JNIEnv.GetStaticFieldID(version, "SDK_INT", "I"));
		}

		private const int kRequestCode = 0x5751;

		private static int _Started;
	}
}
