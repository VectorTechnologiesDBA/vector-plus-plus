using System;
using System.IO;
using System.Text;
using Android.App;
using Android.Content;
using Android.Runtime;
using core.Services;
using MvvmCross.Platform;
using MvvmCross.Platform.Droid.Platform;
using MvvmCross.Platform.Droid.Views;

namespace WirePod
{
	/// <summary>
	/// The SSH private key wire-pod needs to configure an OSKR/dev-unlocked (e.g. WireOS) robot.
	/// The user picks the file once; it's kept in the app's private storage and only ever sent to wire-pod.
	/// </summary>
	internal static class SshKeyStore
	{
		/// <summary>Calls onKey with the key, asking the user to pick it first if needed. onKey(null) means none was chosen.</summary>
		public static void Get(Action<byte[]> onKey)
		{
			if (File.Exists(SshKeyStore.KeyPath))
			{
				onKey(File.ReadAllBytes(SshKeyStore.KeyPath));
				return;
			}
			byte[] bundled = SshKeyStore.ReadBundled();
			if (bundled != null)
			{
				onKey(bundled);
				return;
			}
			Activity activity = Ui.TopActivity;
			if (activity == null)
			{
				onKey(null);
				return;
			}
			activity.RunOnUiThread(delegate
			{
				Android.Support.V7.App.AlertDialog.Builder builder = new Android.Support.V7.App.AlertDialog.Builder(activity);
				builder.SetTitle("Set up Vector for wire-pod");
				builder.SetMessage("Vector can't reach wire-pod yet. wire-pod can set him up, but it needs Vector's SSH key: the same key file you gave wire-pod's \"OSKR/dev-unlocked robot\" setup.\n\nChoose the key file on this phone. It's kept inside this app and only sent to your wire-pod server.");
				builder.SetCancelable(false);
				builder.SetPositiveButton("Choose key file", delegate(object sender, DialogClickEventArgs e)
				{
					SshKeyStore.Pick(activity, onKey);
				});
				builder.SetNegativeButton("Not now", delegate(object sender, DialogClickEventArgs e)
				{
					onKey(null);
				});
				builder.Create().Show();
			});
		}

		/// <summary>Deletes the saved key, e.g. after wire-pod reports it doesn't work.</summary>
		public static void Forget()
		{
			if (File.Exists(SshKeyStore.KeyPath))
			{
				File.Delete(SshKeyStore.KeyPath);
			}
		}

		private static void Pick(Activity activity, Action<byte[]> onKey)
		{
			// The app's activities hand activity results to MvvmCross's intent result sink, so listen there
			// instead of overriding OnActivityResult (which would need new Java glue classes).
			IMvxIntentResultSource source = Mvx.Resolve<IMvxIntentResultSource>();
			EventHandler<MvxIntentResultEventArgs> handler = null;
			handler = delegate(object sender, MvxIntentResultEventArgs e)
			{
				if (e.RequestCode != SshKeyStore.kPickKeyRequestCode)
				{
					return;
				}
				source.Result -= handler;
				onKey(SshKeyStore.ReadPicked(activity, e));
			};
			source.Result += handler;
			// The app's trimmed Android bindings lack addCategory/setType, so call them through JNI.
			Intent intent = new Intent("android.intent.action.OPEN_DOCUMENT");
			SshKeyStore.CallIntentWithString(intent, "addCategory", "android.intent.category.OPENABLE");
			SshKeyStore.CallIntentWithString(intent, "setType", "*/*");
			activity.StartActivityForResult(intent, SshKeyStore.kPickKeyRequestCode);
		}

		private static void CallIntentWithString(Intent intent, string method, string value)
		{
			IntPtr intPtr = JNIEnv.NewString(value);
			try
			{
				JNIEnv.DeleteLocalRef(SshKeyStore.CallObjectMethod(intent.Handle, "android/content/Intent", method, "(Ljava/lang/String;)Landroid/content/Intent;", new JValue[] { new JValue(intPtr) }));
			}
			finally
			{
				JNIEnv.DeleteLocalRef(intPtr);
			}
		}

		private static IntPtr CallObjectMethod(IntPtr instance, string className, string method, string signature, JValue[] args)
		{
			IntPtr intPtr = JNIEnv.FindClass(className);
			return JNIEnv.CallObjectMethod(instance, JNIEnv.GetMethodID(intPtr, method, signature), args);
		}

		private static byte[] ReadPicked(Activity activity, MvxIntentResultEventArgs e)
		{
			if (e.ResultCode != Result.Ok || e.Data == null)
			{
				return null;
			}
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				// intent.getData() and contentResolver.openInputStream(uri), through JNI (trimmed from the bindings).
				intPtr = SshKeyStore.CallObjectMethod(e.Data.Handle, "android/content/Intent", "getData", "()Landroid/net/Uri;", new JValue[0]);
				if (intPtr == IntPtr.Zero)
				{
					return null;
				}
				IntPtr handle = SshKeyStore.CallObjectMethod(activity.ContentResolver.Handle, "android/content/ContentResolver", "openInputStream", "(Landroid/net/Uri;)Ljava/io/InputStream;", new JValue[] { new JValue(intPtr) });
				byte[] array;
				using (Stream stream = InputStreamInvoker.FromJniHandle(handle, JniHandleOwnership.TransferLocalRef))
				{
					using (MemoryStream memoryStream = new MemoryStream())
					{
						stream.CopyTo(memoryStream);
						array = memoryStream.ToArray();
					}
				}
				if (!Encoding.ASCII.GetString(array).Contains("PRIVATE KEY"))
				{
					Ui.Alert("Not an SSH key", "That file doesn't look like an SSH private key (it should contain \"PRIVATE KEY\"). The setup will ask again next time.");
					return null;
				}
				using (FileStream fileStream = new FileStream(SshKeyStore.KeyPath, FileMode.Create, FileAccess.Write))
				{
					fileStream.Write(array, 0, array.Length);
				}
				DAS.Event("WirePod.SshKeyStore.ReadPicked", "saved key, " + array.Length + " bytes");
				return array;
			}
			catch (Exception ex)
			{
				DAS.Warn("WirePod.SshKeyStore.ReadPicked", ex.ToString());
				return null;
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					JNIEnv.DeleteLocalRef(intPtr);
				}
			}
		}

		/// <summary>The key built into this APK (assets/wirepod/ssh_root_key), if the build bundled one.</summary>
		private static byte[] ReadBundled()
		{
			try
			{
				using (Stream stream = Application.Context.Assets.Open(SshKeyStore.kBundledKeyAsset))
				{
					using (MemoryStream memoryStream = new MemoryStream())
					{
						stream.CopyTo(memoryStream);
						return memoryStream.ToArray();
					}
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		private const string kBundledKeyAsset = "wirepod/ssh_root_key";

		private static string KeyPath
		{
			get
			{
				return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "wirepod_robot_ssh_key");
			}
		}

		private const int kPickKeyRequestCode = 0x5750;
	}
}
