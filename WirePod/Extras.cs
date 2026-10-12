using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Runtime;
using core.Services;

namespace WirePod
{
	/// <summary>
	/// Opens the extra screens (com.wirepod.extras.*, plain Java activities in classes3.dex) from the app's own
	/// Settings row and Home tiles. The patched handlers call in here first and skip their own logic when handled.
	/// </summary>
	public static class Extras
	{
		public const string SettingsNavKey = "wirepod_extras";

		/// <summary>Hook for MainSettingsViewModel.OnRowSelected: a finished task when handled, otherwise null.</summary>
		public static Task OpenSetting(string navKey)
		{
			if (navKey != Extras.SettingsNavKey)
			{
				return null;
			}
			Extras.Start("ExtrasActivity");
			return Task.Run(delegate
			{
			});
		}

		/// <summary>Hook for HomeFlowModel.HandleCellSelected: true when the tile was one of ours.</summary>
		public static bool OpenTile(string categoryId)
		{
			string screen;
			if (categoryId == "wirepod_drive")
			{
				screen = "DriveActivity";
			}
			else if (categoryId == "wirepod_ssh")
			{
				screen = "SshActivity";
			}
			else
			{
				return false;
			}
			// With extra features switched off, show the menu (and its switch) instead.
			Extras.Start(WirePodConfig.ExtrasEnabled ? screen : "ExtrasActivity");
			return true;
		}

		public const string FeatureFlagId = "wirepod_extras";

		/// <summary>Hook for ChewieFeatureFlagService.IsFeatureEnabled: our flag follows the Extra features switch.</summary>
		public static bool IsExtrasFlag(string featureId)
		{
			return featureId == Extras.FeatureFlagId;
		}

		public static bool ExtrasOn()
		{
			return WirePodConfig.ExtrasEnabled;
		}

		/// <summary>
		/// Home's Explore grid doesn't scroll; its cell is 1.45 screens tall and each tile is rows * 0.055 of that, which
		/// fits four rows. With the Drive/SSH tiles shown there are five, so use a taller cell and a smaller factor
		/// (same tile size). With extras off, the original values are kept.
		/// </summary>
		public static float HomeCellHeight(float height)
		{
			return (WirePodConfig.ExtrasEnabled && Math.Abs(height - 1.45f) < 0.001f) ? 2.0f : height;
		}

		public static float ExploreTileFactor()
		{
			return WirePodConfig.ExtrasEnabled ? 0.0319f : 0.055f;
		}

		/// <summary>
		/// Hook for ResourceHelper.GetResourceIdForIdentifier, which looks names up in the compiled C# Resource class.
		/// Resources added by this build (e.g. the Drive/SSH tile icons) aren't in it, so ask Android instead.
		/// </summary>
		public static int ResolveResource(int id, string identifier, Type resourceType)
		{
			if (id != -1 || string.IsNullOrEmpty(identifier) || resourceType == null)
			{
				return id;
			}
			Context context = Application.Context;
			int num = context.Resources.GetIdentifier(identifier, resourceType.Name.ToLowerInvariant(), context.PackageName);
			return (num != 0) ? num : -1;
		}

		/// <summary>intent.setClassName(package, cls) through JNI: the trimmed Android bindings don't include it.</summary>
		private static void SetClassName(Intent intent, string package, string cls)
		{
			IntPtr intPtr = JNIEnv.NewString(package);
			IntPtr intPtr2 = JNIEnv.NewString(cls);
			try
			{
				IntPtr method = JNIEnv.GetMethodID(JNIEnv.FindClass("android/content/Intent"), "setClassName", "(Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;");
				JNIEnv.DeleteLocalRef(JNIEnv.CallObjectMethod(intent.Handle, method, new JValue[] { new JValue(intPtr), new JValue(intPtr2) }));
			}
			finally
			{
				JNIEnv.DeleteLocalRef(intPtr);
				JNIEnv.DeleteLocalRef(intPtr2);
			}
		}

		private static void Start(string activity)
		{
			try
			{
				Context context = (Context)Ui.TopActivity ?? Application.Context;
				Intent intent = new Intent();
				Extras.SetClassName(intent, context.PackageName, "com.wirepod.extras." + activity);
				if (Ui.TopActivity == null)
				{
					intent.AddFlags(ActivityFlags.NewTask);
				}
				context.StartActivity(intent);
			}
			catch (Exception ex)
			{
				DAS.Warn("WirePod.Extras.Start", ex.ToString());
			}
		}
	}
}
