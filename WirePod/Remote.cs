using System;
using System.Net.Sockets;
using System.Threading;
using core.Services;

namespace WirePod
{
	/// <summary>
	/// Remote access: reach Vector through another address (e.g. a port forward on the wire-pod machine's Tailscale IP)
	/// when his LAN address isn't reachable. Settings live in the "wirepod" preferences, written by the Java
	/// RemoteActivity: remote_mode ("off", "auto", "always"), remote_host, remote_port.
	///
	/// The app talks to Vector at "https://" + Host + path (WifiService.GetUri), and accepts his TLS certificate only
	/// through a check registered under that host name, which pins his public key to the one saved at setup. Both are
	/// hooked by the Patcher: GetUri passes Host through MapHost, and ConnectToHost also registers the same pinned-key
	/// check under CertHost, so a remote connection is held to exactly the same check as a local one.
	/// </summary>
	public static class Remote
	{
		/// <summary>The host (with port, if not 443) to put in Vector's URLs instead of his LAN address.</summary>
		public static string MapHost(string lanHost)
		{
			try
			{
				string mode = Remote.Mode;
				string remoteHost = Remote.RemoteHost;
				if (mode == "off" || string.IsNullOrEmpty(remoteHost) || string.IsNullOrEmpty(lanHost))
				{
					return lanHost;
				}
				if (mode == "auto" && Remote.LanReachable(lanHost))
				{
					return lanHost;
				}
				int port = Remote.RemotePort;
				string mapped = port == 443 ? remoteHost : remoteHost + ":" + port;
				if (mapped != Remote._LastLogged)
				{
					Remote._LastLogged = mapped;
					DAS.Event("WirePod.Remote.MapHost", lanHost + " -> " + mapped + " (mode " + mode + ")");
				}
				return mapped;
			}
			catch (Exception ex)
			{
				DAS.Warn("WirePod.Remote.MapHost", ex.ToString());
				return lanHost;
			}
		}

		/// <summary>The host name the remote certificate check is registered under (no port: TLS sees the bare host).</summary>
		public static string CertHost(string lanHost)
		{
			string remoteHost = Remote.RemoteHost;
			return (Remote.Mode == "off" || string.IsNullOrEmpty(remoteHost)) ? lanHost : remoteHost;
		}

		/// <summary>Whether Vector's LAN address answers on 443, cached for a short while so URL building stays fast.</summary>
		private static bool LanReachable(string lanHost)
		{
			long now = DateTime.UtcNow.Ticks;
			if (lanHost == Remote._ProbedHost && now - Interlocked.Read(ref Remote._ProbedAt) < TimeSpan.FromSeconds(Remote.kProbeCacheSeconds).Ticks)
			{
				return Remote._ProbeResult;
			}
			bool reachable = false;
			try
			{
				using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
				{
					IAsyncResult connect = socket.BeginConnect(lanHost, 443, null, null);
					reachable = connect.AsyncWaitHandle.WaitOne(Remote.kProbeTimeoutMs) && socket.Connected;
					if (reachable)
					{
						socket.EndConnect(connect);
					}
				}
			}
			catch (Exception)
			{
				reachable = false;
			}
			Remote._ProbedHost = lanHost;
			Remote._ProbeResult = reachable;
			Interlocked.Exchange(ref Remote._ProbedAt, now);
			DAS.Event("WirePod.Remote.LanReachable", lanHost + ": " + reachable);
			return reachable;
		}

		private static string Mode
		{
			get
			{
				string mode = WirePodConfig.Prefs.GetString("remote_mode", "off");
				return (mode == "auto" || mode == "always") ? mode : "off";
			}
		}

		private static string RemoteHost
		{
			get
			{
				return (WirePodConfig.Prefs.GetString("remote_host", "") ?? "").Trim();
			}
		}

		private static int RemotePort
		{
			get
			{
				int port;
				return int.TryParse(WirePodConfig.Prefs.GetString("remote_port", "8443"), out port) && port > 0 && port < 65536 ? port : 8443;
			}
		}

		private const int kProbeTimeoutMs = 1200;

		private const int kProbeCacheSeconds = 20;

		private static string _ProbedHost;

		private static bool _ProbeResult;

		private static long _ProbedAt;

		private static string _LastLogged;
	}
}
