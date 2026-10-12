using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Anki.Victor.ExternalComms;
using chewie.Services;
using core.Services;
using Xamarin.Android.Net;

namespace WirePod
{
	/// <summary>
	/// Points a robot at wire-pod when it can't reach its server, e.g. a WireOS robot after a wipe still uses
	/// its built-in default server. wire-pod does the actual work over SSH (its /api-ssh/setup API): it installs
	/// its server config and certificate on the robot and restarts the robot's services.
	/// </summary>
	public static class RobotSetup
	{
		/// <summary>Called at the end of RobotCommunicationService.Initialize (patched in by the Patcher).</summary>
		public static void Attach(RobotCommunicationService service)
		{
			RobotSetup._Service = service;
			service.Messaging.ReceivedRobotRtsCloudSessionResponse += RobotSetup.OnCloudSessionResponse;
			service.Messaging.ReceivedRobotMessageRtsWifiIpResponse += RobotSetup.OnRobotIp;
		}

		/// <summary>
		/// The robot answers the app's cloud session request with ConnectionError when its cloud service can't reach
		/// its token server. The robot is still connected over Bluetooth here, so its IP can be asked for.
		/// </summary>
		private static void OnCloudSessionResponse(RtsCloudSessionResponse response)
		{
			if (response == null || response.statusCode != RtsCloudStatus.ConnectionError || !WirePodConfig.HasServer)
			{
				return;
			}
			if (Interlocked.Exchange(ref RobotSetup._Running, 1) == 1)
			{
				return;
			}
			DAS.Event("WirePod.RobotSetup.OnCloudSessionResponse", "robot can't reach its server; checking whether wire-pod should set it up");
			Task.Run(delegate
			{
				try
				{
					RobotSetup.Run();
				}
				catch (Exception ex)
				{
					DAS.Warn("WirePod.RobotSetup.Run", ex.ToString());
					RobotSetup.Finish();
				}
			});
		}

		private static void OnRobotIp(string robotIp)
		{
			TaskCompletionSource<string> pendingIp = RobotSetup._PendingIp;
			if (pendingIp != null)
			{
				pendingIp.TrySetResult(robotIp);
			}
		}

		/// <summary>
		/// Asks the robot for its IP over Bluetooth. The app's own handler for the answer stores it alongside the Wi-Fi
		/// name from RobotLastStatusResponse and crashes if that's missing, so make sure a status response came first.
		/// </summary>
		private static string RequestRobotIp()
		{
			if (RobotSetup._Service.RobotLastStatusResponse == null)
			{
				RobotSetup._Service.Messaging.SendRobotRtsStatusRequest();
				DateTime dateTime = DateTime.UtcNow.AddMilliseconds((double)RobotSetup.kIpTimeoutMs);
				while (RobotSetup._Service.RobotLastStatusResponse == null && DateTime.UtcNow < dateTime)
				{
					Thread.Sleep(100);
				}
				if (RobotSetup._Service.RobotLastStatusResponse == null)
				{
					DAS.Warn("WirePod.RobotSetup.RequestRobotIp", "no status response from the robot, not asking for its IP");
					return null;
				}
			}
			RobotSetup._PendingIp = new TaskCompletionSource<string>();
			RobotSetup._Service.Messaging.SendRobotIpRequest();
			string result = RobotSetup._PendingIp.Task.Wait(RobotSetup.kIpTimeoutMs, CancellationToken.None) ? RobotSetup._PendingIp.Task.Result : null;
			RobotSetup._PendingIp = null;
			return result;
		}

		private static void Run()
		{
			string text = RobotSetup.RequestRobotIp();
			if (string.IsNullOrEmpty(text))
			{
				text = RobotSetup._Service.RobotIp;
			}
			if (string.IsNullOrEmpty(text))
			{
				// No IP means the robot isn't on Wi-Fi; the app's own Wi-Fi help is the right answer.
				DAS.Event("WirePod.RobotSetup.Run", "robot has no IP, leaving it to the app");
				RobotSetup.Finish();
				return;
			}
			if (RobotSetup._ConfiguredIps.Contains(text))
			{
				// Already set up during this session and still unreachable: setting it up again won't help.
				Ui.Alert("Vector still can't reach wire-pod", string.Format("wire-pod already set up Vector ({0}), but he still can't reach it. Wait until he's fully restarted and showing his setup screen, then tap Try Again. If it keeps happening, check wire-pod's log at {1}.", text, WirePodConfig.WebBaseUrl));
				RobotSetup.Finish();
				return;
			}
			string robotIp = text;
			SshKeyStore.Get(delegate(byte[] key)
			{
				if (key == null)
				{
					RobotSetup.Finish();
					return;
				}
				Task.Run(delegate
				{
					RobotSetup.Configure(robotIp, key);
				});
			});
		}

		private static void Configure(string robotIp, byte[] key)
		{
			DAS.Event("WirePod.RobotSetup.Configure", "asking wire-pod to set up " + robotIp);
			Progress progress = new Progress(string.Format("Asking wire-pod to set up Vector ({0})...", robotIp));
			try
			{
				string text = RobotSetup.StartSshSetup(robotIp, key);
				if (text != null)
				{
					RobotSetup.Failed(progress, text);
					return;
				}
				DateTime dateTime = DateTime.UtcNow.AddMilliseconds((double)RobotSetup.kSetupTimeoutMs);
				for (;;)
				{
					Thread.Sleep(1000);
					string setupStatus = RobotSetup.GetSshSetupStatus();
					DAS.Event("WirePod.RobotSetup.Status", setupStatus);
					if (setupStatus.Contains("done"))
					{
						break;
					}
					if (setupStatus.Contains("error"))
					{
						RobotSetup.Failed(progress, setupStatus);
						return;
					}
					if (DateTime.UtcNow > dateTime)
					{
						RobotSetup.Failed(progress, "wire-pod didn't finish within " + RobotSetup.kSetupTimeoutMs / 60000 + " minutes.");
						return;
					}
					progress.Update(setupStatus == "not running" ? "Starting..." : setupStatus);
				}
				RobotSetup._ConfiguredIps.Add(robotIp);
				progress.Close();
				Ui.Alert("Vector is set up for wire-pod", "wire-pod has configured Vector. He's restarting now, which takes about a minute (his screen may go blank).\n\nWhen he shows his setup screen again, tap Try Again in the app to finish connecting.", "OK", null);
			}
			catch (Exception ex)
			{
				Exception ex2 = (ex is AggregateException && ex.InnerException != null) ? ex.InnerException : ex;
				DAS.Warn("WirePod.RobotSetup.Configure", ex2.ToString());
				RobotSetup.Failed(progress, "Couldn't talk to wire-pod at " + WirePodConfig.WebBaseUrl + " (" + ex2.Message + ").");
			}
			finally
			{
				RobotSetup.Finish();
			}
		}

		private static void Failed(Progress progress, string reason)
		{
			progress.Close();
			string text = reason;
			if (reason.Contains("ssh dial") || reason.Contains("no route to host") || reason.Contains("connection refused"))
			{
				text = reason + "\n\nwire-pod couldn't open an SSH connection to Vector. This only works for OSKR/dev-unlocked robots (such as WireOS) on the same network as wire-pod.";
			}
			else if (reason.Contains("priv key") || reason.Contains("unable to authenticate") || reason.Contains("handshake"))
			{
				// The key was rejected; let the user pick another one next time.
				SshKeyStore.Forget();
				text = reason + "\n\nVector didn't accept the SSH key. You'll be asked for the key file again next time.";
			}
			DAS.Warn("WirePod.RobotSetup.Failed", reason);
			Ui.Alert("wire-pod couldn't set up Vector", text);
		}

		private static void Finish()
		{
			RobotSetup._PendingIp = null;
			Interlocked.Exchange(ref RobotSetup._Running, 0);
		}

		/// <summary>POST /api-ssh/setup (multipart: ip, key). Returns null when wire-pod started, otherwise its error.</summary>
		private static string StartSshSetup(string robotIp, byte[] key)
		{
			using (HttpClient httpClient = RobotSetup.CreateClient())
			{
				HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, WirePodConfig.WebBaseUrl + "/api-ssh/setup");
				httpRequestMessage.Content = RobotSetup.MultipartForm(robotIp, key);
				using (HttpResponseMessage result = httpClient.SendAsync(httpRequestMessage).Result)
				{
					string result2 = result.Content.ReadAsStringAsync().Result;
					DAS.Event("WirePod.RobotSetup.StartSshSetup", string.Format("{0}: {1}", (int)result.StatusCode, result2));
					if (result2.Contains("running"))
					{
						return null;
					}
					if (!result.IsSuccessStatusCode)
					{
						return string.Format("wire-pod answered {0} {1}. It may be too old to set up robots over SSH; update wire-pod.", (int)result.StatusCode, result2.Trim());
					}
					return result2.Trim();
				}
			}
		}

		/// <summary>
		/// multipart/form-data with an "ip" field and a "key" file, built by hand because the app's trimmed
		/// System.Net.Http has no usable MultipartFormDataContent.Add.
		/// </summary>
		private static HttpContent MultipartForm(string robotIp, byte[] key)
		{
			string text = "wirepod" + Guid.NewGuid().ToString("N");
			MemoryStream memoryStream = new MemoryStream();
			RobotSetup.WriteAscii(memoryStream, "--" + text + "\r\nContent-Disposition: form-data; name=\"ip\"\r\n\r\n" + robotIp + "\r\n");
			RobotSetup.WriteAscii(memoryStream, "--" + text + "\r\nContent-Disposition: form-data; name=\"key\"; filename=\"ssh_key\"\r\nContent-Type: application/octet-stream\r\n\r\n");
			memoryStream.Write(key, 0, key.Length);
			RobotSetup.WriteAscii(memoryStream, "\r\n--" + text + "--\r\n");
			ByteArrayContent byteArrayContent = new ByteArrayContent(memoryStream.ToArray());
			byteArrayContent.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + text);
			return byteArrayContent;
		}

		private static void WriteAscii(Stream stream, string text)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(text);
			stream.Write(bytes, 0, bytes.Length);
		}

		private static string GetSshSetupStatus()
		{
			string text;
			using (HttpClient httpClient = RobotSetup.CreateClient())
			{
				using (HttpResponseMessage result = httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, WirePodConfig.WebBaseUrl + "/api-ssh/get_setup_status")).Result)
				{
					text = result.Content.ReadAsStringAsync().Result.Trim();
				}
			}
			return text;
		}

		private static HttpClient CreateClient()
		{
			// Pass the handler explicitly: the default one is chosen by reflection, which the linker can't see.
			HttpClient httpClient = new HttpClient(new AndroidClientHandler());
			httpClient.Timeout = TimeSpan.FromSeconds(15.0);
			return httpClient;
		}

		/// <summary>A non-blocking progress dialog that can be updated from any thread.</summary>
		private class Progress
		{
			public Progress(string message)
			{
				Activity activity = Ui.TopActivity;
				if (activity == null)
				{
					return;
				}
				this._Activity = activity;
				activity.RunOnUiThread(delegate
				{
					Android.Support.V7.App.AlertDialog.Builder builder = new Android.Support.V7.App.AlertDialog.Builder(activity);
					builder.SetTitle("Setting up Vector for wire-pod");
					builder.SetMessage(message);
					builder.SetCancelable(false);
					builder.SetNegativeButton("Hide", delegate(object sender, DialogClickEventArgs e)
					{
						this._Showing = false;
					});
					this._Dialog = builder.Create();
					this._Dialog.Show();
					this._Showing = true;
				});
			}

			public void Update(string message)
			{
				if (this._Activity == null)
				{
					return;
				}
				this._Activity.RunOnUiThread(delegate
				{
					if (this._Dialog != null && this._Showing)
					{
						this._Dialog.SetMessage(message);
					}
				});
			}

			public void Close()
			{
				if (this._Activity == null)
				{
					return;
				}
				this._Activity.RunOnUiThread(delegate
				{
					if (this._Dialog != null && this._Showing)
					{
						this._Showing = false;
						this._Dialog.Dismiss();
					}
				});
			}

			private Activity _Activity;

			private Android.Support.V7.App.AlertDialog _Dialog;

			// Dialog.IsShowing is trimmed from the app's bindings.
			private bool _Showing;
		}

		private static RobotCommunicationService _Service;

		private static int _Running;

		private static TaskCompletionSource<string> _PendingIp;

		// Robots wire-pod set up during this run of the app.
		private static readonly HashSet<string> _ConfiguredIps = new HashSet<string>();

		private const int kIpTimeoutMs = 5000;

		private const int kSetupTimeoutMs = 240000;
	}
}
