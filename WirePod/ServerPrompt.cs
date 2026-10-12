using System;
using System.Net.Http;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Widget;
using core.Services;
using Xamarin.Android.Net;

namespace WirePod
{
	/// <summary>
	/// Asks for the wire-pod address during sign-in, checks that something answers there, and saves it.
	/// </summary>
	internal static class ServerPrompt
	{
		/// <summary>
		/// Calls onDone(true) once a server is saved, or onDone(false) if the user cancels.
		/// onDone runs on a background thread.
		/// </summary>
		public static void Run(Action<bool> onDone)
		{
			// Sign-in shows its "logging in" overlay (a DialogFragment) at the same moment it sends the request.
			// Give it time to appear so this dialog stacks above it rather than underneath.
			Task.Delay(ServerPrompt.kOverlaySettleMs).ContinueWith(delegate(Task _)
			{
				ServerPrompt.Ask(WirePodConfig.Server, null, onDone);
			});
		}

		private static void Ask(string current, string error, Action<bool> onDone)
		{
			Activity activity = Ui.TopActivity;
			if (activity == null)
			{
				DAS.Warn("WirePod.ServerPrompt.Ask", "No activity to show the server prompt on; keeping " + current);
				onDone(WirePodConfig.HasServer);
				return;
			}
			activity.RunOnUiThread(delegate
			{
				EditText input = new EditText(activity);
				// A non-multiline input type also makes the field single-line.
				input.InputType = (InputTypes.ClassText | InputTypes.TextVariationUri);
				input.Hint = WirePodConfig.kDefaultHost;
				input.Text = current;
				input.SetSelection(input.Text.Length);
				int num = (int)(20f * activity.Resources.DisplayMetrics.Density);
				FrameLayout frameLayout = new FrameLayout(activity);
				frameLayout.SetPadding(num, num / 2, num, 0);
				frameLayout.AddView(input);
				Android.Support.V7.App.AlertDialog.Builder builder = new Android.Support.V7.App.AlertDialog.Builder(activity);
				builder.SetTitle("wire-pod server");
				builder.SetMessage(error ?? "Enter the IP address of the computer running wire-pod. Add :port if its web page isn't on port 8080.");
				builder.SetView(frameLayout);
				builder.SetCancelable(false);
				builder.SetPositiveButton("Connect", delegate(object sender, DialogClickEventArgs e)
				{
					ServerPrompt.Check(input.Text, onDone);
				});
				if (error != null)
				{
					builder.SetNeutralButton("Use anyway", delegate(object sender, DialogClickEventArgs e)
					{
						ServerPrompt.Save(input.Text, onDone);
					});
				}
				builder.SetNegativeButton("Cancel", delegate(object sender, DialogClickEventArgs e)
				{
					onDone(false);
				});
				builder.Create().Show();
			});
		}

		private static void Check(string input, Action<bool> onDone)
		{
			string server;
			if (!WirePodConfig.TryNormalizeServer(input, out server))
			{
				ServerPrompt.Ask(input, "\"" + input + "\" isn't a valid address. Enter an IP address like 192.168.1.20 or a hostname.", onDone);
				return;
			}
			Task.Run(delegate
			{
				string text = ServerPrompt.Probe(server);
				if (text != null)
				{
					ServerPrompt.Ask(server, string.Format("Couldn't reach wire-pod at {0} ({1}). Check that wire-pod is running and the phone is on the same network.", server, text), onDone);
					return;
				}
				WirePodConfig.SaveServer(server, WirePodConfig.ResolveIPv4(WirePodConfig.GetHost(server)));
				onDone(true);
			});
		}

		private static void Save(string input, Action<bool> onDone)
		{
			string server;
			if (!WirePodConfig.TryNormalizeServer(input, out server))
			{
				ServerPrompt.Ask(input, "\"" + input + "\" isn't a valid address. Enter an IP address like 192.168.1.20 or a hostname.", onDone);
				return;
			}
			Task.Run(delegate
			{
				WirePodConfig.SaveServer(server, WirePodConfig.ResolveIPv4(WirePodConfig.GetHost(server)));
				onDone(true);
			});
		}

		/// <summary>Returns null if wire-pod's web server answered, otherwise a short reason.</summary>
		private static string Probe(string server)
		{
			try
			{
				// Pass the handler explicitly: the default one is chosen by reflection, which the linker can't see.
				using (HttpClient httpClient = new HttpClient(new AndroidClientHandler()))
				{
					httpClient.Timeout = TimeSpan.FromSeconds(6.0);
					using (HttpResponseMessage result = httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://" + server + "/api/get_version_info")).Result)
					{
						// Any HTTP answer means the web server is there; older wire-pods return 404 for this path.
						DAS.Event("WirePod.ServerPrompt.Probe", string.Format("{0} -> {1}", server, (int)result.StatusCode));
						return null;
					}
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = (ex is AggregateException && ex.InnerException != null) ? ex.InnerException : ex;
				DAS.Warn("WirePod.ServerPrompt.Probe", server + ": " + ex2);
				if (ex2 is TaskCanceledException || ex2 is OperationCanceledException)
				{
					return "timed out";
				}
				while (ex2.InnerException != null)
				{
					ex2 = ex2.InnerException;
				}
				return ex2.Message;
			}
		}

		private const int kOverlaySettleMs = 600;
	}
}
