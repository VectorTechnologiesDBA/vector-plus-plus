using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using Xamarin.Android.Net;

namespace WirePod
{
	/// <summary>Blocking calls to wire-pod's web API. Call from a background thread.</summary>
	internal static class WirePodApi
	{
		public static string Get(string pathAndQuery)
		{
			return WirePodApi.Send(new HttpRequestMessage(HttpMethod.Get, WirePodConfig.WebBaseUrl + pathAndQuery));
		}

		public static JToken GetJson(string pathAndQuery)
		{
			return JToken.Parse(WirePodApi.Get(pathAndQuery));
		}

		public static string PostJson(string path, JToken body)
		{
			HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, WirePodConfig.WebBaseUrl + path);
			ByteArrayContent byteArrayContent = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None, Array.Empty<Newtonsoft.Json.JsonConverter>())));
			byteArrayContent.Headers.TryAddWithoutValidation("Content-Type", "application/json");
			httpRequestMessage.Content = byteArrayContent;
			return WirePodApi.Send(httpRequestMessage);
		}

		/// <summary>The ESN wire-pod uses for the robot (wire-pod's /api-sdk calls take it as "serial").</summary>
		public static string RobotSerial()
		{
			JArray jarray = WirePodApi.GetJson("/api-sdk/get_sdk_info").Value<JArray>("robots");
			if (jarray == null || jarray.Count == 0)
			{
				throw new InvalidOperationException("wire-pod doesn't know any robots yet. Finish setting Vector up first.");
			}
			return jarray[0].Value<string>("esn");
		}

		public static string Escape(string value)
		{
			return Uri.EscapeDataString(value ?? string.Empty);
		}

		private static string Send(HttpRequestMessage request)
		{
			string text;
			// Pass the handler explicitly: the default one is chosen by reflection, which the linker can't see.
			using (HttpClient httpClient = new HttpClient(new AndroidClientHandler()))
			{
				httpClient.Timeout = TimeSpan.FromSeconds(15.0);
				using (HttpResponseMessage result = httpClient.SendAsync(request).Result)
				{
					text = result.Content.ReadAsStringAsync().Result;
					if (!result.IsSuccessStatusCode)
					{
						throw new InvalidOperationException(string.Format("wire-pod answered {0}: {1}", (int)result.StatusCode, text.Trim()));
					}
				}
			}
			return text;
		}
	}
}
