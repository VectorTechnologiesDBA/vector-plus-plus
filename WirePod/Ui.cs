using System;
using Android.App;
using Android.Content;
using core.Services;
using MvvmCross.Platform;
using MvvmCross.Platform.Droid.Platform;

namespace WirePod
{
	/// <summary>Small helpers for showing WirePod's own dialogs on top of whatever screen the app is on.</summary>
	internal static class Ui
	{
		public static Activity TopActivity
		{
			get
			{
				IMvxAndroidCurrentTopActivity mvxAndroidCurrentTopActivity;
				if (!Mvx.TryResolve<IMvxAndroidCurrentTopActivity>(out mvxAndroidCurrentTopActivity) || mvxAndroidCurrentTopActivity.Activity == null || mvxAndroidCurrentTopActivity.Activity.IsFinishing)
				{
					return null;
				}
				return mvxAndroidCurrentTopActivity.Activity;
			}
		}

		/// <summary>Shows a message with a single button. onClosed (optional) runs on the UI thread.</summary>
		public static void Alert(string title, string message, string button = "OK", Action onClosed = null)
		{
			Activity activity = Ui.TopActivity;
			if (activity == null)
			{
				DAS.Warn("WirePod.Ui.Alert", "No activity for: " + message);
				return;
			}
			activity.RunOnUiThread(delegate
			{
				Android.Support.V7.App.AlertDialog.Builder builder = new Android.Support.V7.App.AlertDialog.Builder(activity);
				builder.SetTitle(title);
				builder.SetMessage(message);
				builder.SetCancelable(false);
				builder.SetPositiveButton(button, delegate(object sender, DialogClickEventArgs e)
				{
					if (onClosed != null)
					{
						onClosed();
					}
				});
				builder.Create().Show();
			});
		}
	}
}
