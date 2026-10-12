using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using core.Services.Http;
using Newtonsoft.Json.Linq;

namespace WirePod
{
	/// <summary>
	/// A response produced on the phone instead of by an Anki server. JSON is parsed the same way
	/// core.Services.Http.HttpService does it, so the app's response handling sees identical objects.
	/// </summary>
	internal class LocalHttpResponse : IHttpResponse
	{
		public LocalHttpResponse(HttpMethod method, Uri uri, HttpStatusCode statusCode, JToken content)
		{
			this.OriginalRequest = new HttpRequestMessage(method, uri);
			this.StatusCode = statusCode;
			this.ContentAsString = content.ToString(Newtonsoft.Json.Formatting.None, Array.Empty<Newtonsoft.Json.JsonConverter>());
			this.ContentAsJson = JToken.Parse(this.ContentAsString);
		}

		public IEnumerable<KeyValuePair<string, IEnumerable<string>>> Headers
		{
			get
			{
				return LocalHttpResponse._NoHeaders;
			}
		}

		public string ContentAsString { get; private set; }

		public JToken ContentAsJson { get; private set; }

		public HttpStatusCode StatusCode { get; private set; }

		public bool Success
		{
			get
			{
				return this.StatusCode >= HttpStatusCode.OK && this.StatusCode < HttpStatusCode.MultipleChoices;
			}
		}

		public HttpRequestMessage OriginalRequest { get; private set; }

		public Exception Exception
		{
			get
			{
				return null;
			}
		}

		private static readonly KeyValuePair<string, IEnumerable<string>>[] _NoHeaders = new KeyValuePair<string, IEnumerable<string>>[0];
	}
}
