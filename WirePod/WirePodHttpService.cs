using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using core.Services.Http;
using Newtonsoft.Json.Linq;

namespace WirePod
{
	/// <summary>
	/// Wraps the app's HTTP service: requests to Anki's cloud are answered by <see cref="AnkiCloudEmulator"/>,
	/// everything else (robot traffic, etc.) goes to the original service untouched.
	/// </summary>
	public class WirePodHttpService : IHttpService
	{
		/// <summary>
		/// Called from the start of AccountService.Configure, EmailSubscriptionService.Configure and
		/// LocationAutoCompletionService.Configure (patched in by the Patcher) to replace their httpService argument.
		/// </summary>
		public static IHttpService Wrap(IHttpService httpService)
		{
			if (httpService == null || httpService is WirePodHttpService)
			{
				return httpService;
			}
			return new WirePodHttpService(httpService);
		}

		private WirePodHttpService(IHttpService inner)
		{
			this._Inner = inner;
		}

		public void Get(Uri uri, HttpResponseDelegate responseCallback, IEnumerable<KeyValuePair<string, string>> headers = null, Tuple<string, string> authorization = null)
		{
			if (AnkiCloudEmulator.Handles(uri))
			{
				AnkiCloudEmulator.Get(uri, responseCallback);
				return;
			}
			this._Inner.Get(uri, responseCallback, headers, authorization);
		}

		public void Post(Uri uri, IEnumerable<KeyValuePair<string, string>> request, HttpResponseDelegate responseCallback, IEnumerable<KeyValuePair<string, string>> headers = null, Tuple<string, string> authorization = null)
		{
			if (AnkiCloudEmulator.Handles(uri))
			{
				JObject jobject = new JObject();
				if (request != null)
				{
					foreach (KeyValuePair<string, string> keyValuePair in request)
					{
						jobject[keyValuePair.Key] = keyValuePair.Value;
					}
				}
				AnkiCloudEmulator.Post(uri, jobject, responseCallback);
				return;
			}
			this._Inner.Post(uri, request, responseCallback, headers, authorization);
		}

		public void Post(Uri uri, JToken request, HttpResponseDelegate responseCallback, IEnumerable<KeyValuePair<string, string>> headers = null, Tuple<string, string> authorization = null)
		{
			if (AnkiCloudEmulator.Handles(uri))
			{
				AnkiCloudEmulator.Post(uri, request as JObject, responseCallback);
				return;
			}
			this._Inner.Post(uri, request, responseCallback, headers, authorization);
		}

		public Task<string> GetStringAsync(string url)
		{
			return this._Inner.GetStringAsync(url);
		}

		public void RegisterServerCertificateValidationCallback(string host, ServerCertificateValidationCallback callback)
		{
			this._Inner.RegisterServerCertificateValidationCallback(host, callback);
		}

		public void CreatePostStream(Uri uri, HttpContent postContent, PostStreamResponseDelegate callback, Tuple<string, string> authHeader = null)
		{
			this._Inner.CreatePostStream(uri, postContent, callback, authHeader);
		}

		public void KillStream(Uri uri, StreamKilledReasons streamKilledReason, Exception exception, PostStreamResponseDelegate callback)
		{
			this._Inner.KillStream(uri, streamKilledReason, exception, callback);
		}

		private readonly IHttpService _Inner;
	}
}
