using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using core.Services;

namespace WirePod
{
	/// <summary>
	/// Where the wire-pod server lives, and the wire-pod URLs that replace Anki cloud URLs.
	/// The address is entered by the user at sign-in and kept in the "wirepod" shared preferences.
	/// </summary>
	public static class WirePodConfig
	{
		public static string Server
		{
			get
			{
				string text = WirePodConfig.Prefs.GetString(WirePodConfig.kServerKey, null);
				if (string.IsNullOrEmpty(text))
				{
					return WirePodConfig.kDefaultHost + ":" + WirePodConfig.kDefaultWebPort;
				}
				return text;
			}
		}

		public static bool HasServer
		{
			get
			{
				return !string.IsNullOrEmpty(WirePodConfig.Prefs.GetString(WirePodConfig.kServerKey, null));
			}
		}

		/// <summary>wire-pod's web server, e.g. http://192.168.1.20:8080</summary>
		public static string WebBaseUrl
		{
			get
			{
				return "http://" + WirePodConfig.Server;
			}
		}

		/// <summary>The "Extra features" switch (set by the Java ExtrasActivity, same preferences).</summary>
		public static bool ExtrasEnabled
		{
			get
			{
				return WirePodConfig.Prefs.GetString("extras_enabled", "0") == "1";
			}
		}

		/// <summary>One extra feature's own switch (prefs "feat_drive", "feat_ssh", "feat_quick"); on unless switched off.</summary>
		public static bool FeatureEnabled(string name)
		{
			return WirePodConfig.Prefs.GetString("feat_" + name, "1") == "1";
		}

		public static void SaveServer(string server, string resolvedIp)
		{
			WirePodConfig.Prefs.Edit().PutString(WirePodConfig.kServerKey, server).PutString(WirePodConfig.kServerIpKey, resolvedIp).Apply();
			DAS.Event("WirePod.WirePodConfig.SaveServer", string.Format("server: {0} ip: {1}", server, resolvedIp));
		}

		/// <summary>
		/// Replaces the string.Concat that built "https://session-certs.token.global.anki-services.com/vic/" + esn.
		/// wire-pod serves the certificate it captured while pairing at /session-certs/{esn}.
		/// </summary>
		public static string SessionCertUrl(string ankiCertAddress, string esn)
		{
			string text = WirePodConfig.WebBaseUrl + "/session-certs/" + (esn ?? string.Empty).Trim().ToLowerInvariant();
			DAS.Event("WirePod.WirePodConfig.SessionCertUrl", text);
			return text;
		}

		/// <summary>
		/// Replaces RobotCommunicationService.OtaUrl: the firmware Vector downloads (himself) when the app updates him,
		/// e.g. from recovery mode. Escape-pod firmware 2.0.1.6086ep from the Internet Archive, over plain HTTP: the
		/// robot's updater may not have a correct clock or current certificates for HTTPS.
		/// </summary>
		public static string OtaUrl(string ankiOtaUrl)
		{
			return WirePodConfig.kOtaUrl;
		}

		/// <summary>
		/// Accepts "host", "host:port" or a pasted URL such as "http://host:8080/" and returns "host:port".
		/// </summary>
		public static bool TryNormalizeServer(string input, out string server)
		{
			server = null;
			if (string.IsNullOrWhiteSpace(input))
			{
				return false;
			}
			string text = input.Trim();
			if (!text.Contains("://"))
			{
				text = "http://" + text;
			}
			Uri uri;
			if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host))
			{
				return false;
			}
			int num = (uri.IsDefaultPort && !WirePodConfig.HasExplicitPort(input)) ? WirePodConfig.kDefaultWebPort : uri.Port;
			server = uri.Host + ":" + num;
			return true;
		}

		public static string GetHost(string server)
		{
			int num = server.LastIndexOf(':');
			if (num <= 0)
			{
				return server;
			}
			return server.Substring(0, num);
		}

		public static int GetPort(string server)
		{
			int num = server.LastIndexOf(':');
			int num2;
			if (num <= 0 || !int.TryParse(server.Substring(num + 1), out num2))
			{
				return WirePodConfig.kDefaultWebPort;
			}
			return num2;
		}

		/// <summary>Returns the first IPv4 address for host, or null if the phone can't resolve it.</summary>
		public static string ResolveIPv4(string host)
		{
			IPAddress ipaddress;
			if (IPAddress.TryParse(host.Trim('[', ']'), out ipaddress))
			{
				return (ipaddress.AddressFamily == AddressFamily.InterNetwork) ? ipaddress.ToString() : null;
			}
			try
			{
				IPAddress ipaddress2 = Dns.GetHostAddresses(host).FirstOrDefault((IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork);
				return (ipaddress2 != null) ? ipaddress2.ToString() : null;
			}
			catch (Exception ex)
			{
				DAS.Warn("WirePod.WirePodConfig.ResolveIPv4", host + ": " + ex.Message);
				return null;
			}
		}

		private static bool HasExplicitPort(string input)
		{
			string text = input.Trim();
			int num = text.IndexOf("://", StringComparison.Ordinal);
			if (num >= 0)
			{
				text = text.Substring(num + 3);
			}
			int num2 = text.IndexOf('/');
			if (num2 >= 0)
			{
				text = text.Substring(0, num2);
			}
			int num3 = text.LastIndexOf(':');
			return num3 > 0 && num3 > text.LastIndexOf(']');
		}

		internal static ISharedPreferences Prefs
		{
			get
			{
				return Application.Context.GetSharedPreferences(WirePodConfig.kPrefsName, FileCreationMode.Private);
			}
		}

		public const string kDefaultHost = "escapepod.local";

		public const int kDefaultWebPort = 8080;

		private const string kOtaUrl = "http://archive.org/download/Anki-proto-prod-Vector-firmware/prod/2.0.1.6086ep.ota";

		private const string kPrefsName = "wirepod";

		private const string kServerKey = "server";

		private const string kServerIpKey = "server_ip";
	}
}
