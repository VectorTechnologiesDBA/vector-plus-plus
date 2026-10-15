using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Runtime;
using chewie.Settings;
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

		public const string EyeColorNavKey = "wirepod_eyecolor";

		public const string UpdateNavKey = "wirepod_update";

		/// <summary>Hook at the start of AboutSettingsViewModel.OnRowSelected: true when the row was ours (Check for updates).</summary>
		public static bool OpenAboutRow(string navKey)
		{
			if (navKey != Extras.UpdateNavKey)
			{
				return false;
			}
			Extras.Start("UpdateActivity");
			return true;
		}

		/// <summary>Hook for MainSettingsViewModel.NetworkIndex: the Custom eye color row sits above the Wi-Fi row.</summary>
		public static int ShiftSettingsIndex(int index)
		{
			return index + 1;
		}

		/// <summary>
		/// use teal if eye color is out of range instead of crashing
		/// </summary>
		public static EyeColorCollectionData EyeColorOrDefault(EyeColorCollectionData current)
		{
			if (current != null)
			{
				return current;
			}
			if (Extras._fallbackEyeColor == null)
			{
				EyeColorCollectionData data = new EyeColorCollectionData();
				data.Id = 0;
				data.Name = "EyeColor.colorName.0";
				data.Enabled = true;
				data.EyeColor.R = 0.21f;
				data.EyeColor.G = 0.76f;
				data.EyeColor.B = 0.44f;
				data.EyeColor.A = 1f;
				Extras._fallbackEyeColor = data;
			}
			return Extras._fallbackEyeColor;
		}

		private static EyeColorCollectionData _fallbackEyeColor;

		/// <summary>
		/// Hook at the start of RobotSettingsService.SetEyeColor (a preset picked in Settings). A custom eye color
		/// (backported from app 1.8) overrides presets on the robot, so ask wire-pod to switch it off.
		/// </summary>
		public static void PresetEyeColorPicked(int eyeColor)
		{
			Task.Run(delegate
			{
				try
				{
					WirePodApi.Get("/api-sdk/eye_color?serial=" + WirePodApi.Escape(WirePodApi.RobotSerial()) + "&color=" + eyeColor);
				}
				catch (Exception ex)
				{
					DAS.Warn("WirePod.Extras.PresetEyeColorPicked", ex.Message);
				}
			});
		}

		/// <summary>Hook for MainSettingsViewModel.OnRowSelected: a finished task when handled, otherwise null.</summary>
		public static Task OpenSetting(string navKey)
		{
			if (navKey == Extras.SettingsNavKey)
			{
				Extras.Start("ExtrasActivity");
			}
			else if (navKey == Extras.EyeColorNavKey)
			{
				Extras.Start("EyeColorActivity");
			}
			else
			{
				return null;
			}
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
