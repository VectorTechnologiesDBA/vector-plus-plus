using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using core.Services;
using core.Services.AppInfo;
using core.Services.Http;
using MvvmCross.Platform;
using Newtonsoft.Json.Linq;

namespace WirePod
{
	/// <summary>
	/// Answers requests meant for Anki's (now offline) cloud on the phone.
	/// wire-pod accepts any session token when the robot associates a user, so the "account" here is purely
	/// local: signing in only records the email for display and asks where wire-pod is.
	/// </summary>
	internal static class AnkiCloudEmulator
	{
		public static bool Handles(Uri uri)
		{
			return uri != null && (AnkiCloudEmulator.IsHostOrSubdomain(uri.Host, "anki.com") || AnkiCloudEmulator.IsHostOrSubdomain(uri.Host, "anki-services.com"));
		}

		public static void Get(Uri uri, HttpResponseDelegate callback)
		{
			AnkiCloudEmulator.Handle(HttpMethod.Get, uri, new JObject(), callback);
		}

		public static void Post(Uri uri, JObject body, HttpResponseDelegate callback)
		{
			AnkiCloudEmulator.Handle(HttpMethod.Post, uri, body ?? new JObject(), callback);
		}

		private static void Handle(HttpMethod method, Uri uri, JObject body, HttpResponseDelegate callback)
		{
			DAS.Event("WirePod.AnkiCloudEmulator.Handle", method + " " + uri.Host + uri.AbsolutePath);
			string text = uri.Host.ToLowerInvariant();
			if (text == "accounts.api.anki.com")
			{
				AnkiCloudEmulator.HandleAccounts(method, uri, body, callback);
				return;
			}
			if (text == "locations.api.anki.com")
			{
				AnkiCloudEmulator.HandleLocations(method, uri, callback);
				return;
			}
			// Email subscriptions and anything else: nothing to do, report success.
			AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.OK, new JObject(), callback);
		}

		private static void HandleAccounts(HttpMethod method, Uri uri, JObject body, HttpResponseDelegate callback)
		{
			string[] array = uri.AbsolutePath.Trim('/').Split('/');
			bool flag = method == HttpMethod.Post;
			// POST /1/sessions (sign in) and POST /1/users (create account) both start a session.
			if (flag && array.Length == 2 && (array[1] == "sessions" || array[1] == "users"))
			{
				string email = (string)(body["username"] ?? body["email"]) ?? string.Empty;
				string dob = (string)body["dob"];
				ServerPrompt.Run(delegate(bool configured)
				{
					if (!configured)
					{
						AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.ServiceUnavailable, AnkiCloudEmulator.Error("wirepod_not_configured", "No wire-pod server was chosen."), callback);
						return;
					}
					AnkiCloudEmulator.SaveUser(email, dob);
					AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.OK, AnkiCloudEmulator.SessionJson(), callback);
				});
				return;
			}
			// GET /1/users/{id} and POST /1/users/{id}/patch return the user.
			if (array.Length >= 3 && array[1] == "users" && (!flag || (array.Length == 4 && array[3] == "patch")))
			{
				AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.OK, AnkiCloudEmulator.UserJson(), callback);
				return;
			}
			// POST /1/sessions/delete, /1/users/{id}/verify_email, /1/reset_user_password: nothing to do locally.
			if (flag)
			{
				AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.OK, new JObject(), callback);
				return;
			}
			AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.NotFound, AnkiCloudEmulator.Error("not_found", "Not available with wire-pod."), callback);
		}

		/// <summary>
		/// Anki's place search is gone, so offer exactly what was typed. wire-pod reads the location
		/// from the robot's settings for weather.
		/// </summary>
		private static void HandleLocations(HttpMethod method, Uri uri, HttpResponseDelegate callback)
		{
			JArray jarray = new JArray();
			string text = AnkiCloudEmulator.GetQueryValue(uri, "query");
			if (!string.IsNullOrWhiteSpace(text))
			{
				jarray.Add(new JObject { { "address", text.Trim() } });
			}
			AnkiCloudEmulator.Respond(method, uri, HttpStatusCode.OK, new JObject { { "locations", jarray } }, callback);
		}

		private static JObject SessionJson()
		{
			DateTime utcNow = DateTime.UtcNow;
			JObject jobject = new JObject();
			jobject.Add("session_token", "wirepod-" + Guid.NewGuid().ToString("N"));
			jobject.Add("user_id", AnkiCloudEmulator.kUserId);
			jobject.Add("scope", "user");
			jobject.Add("time_created", utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
			jobject.Add("time_expires", utcNow.AddYears(20).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
			return new JObject
			{
				{ "session", jobject },
				{ "user", AnkiCloudEmulator.UserJson() }
			};
		}

		private static JObject UserJson()
		{
			Android.Content.ISharedPreferences prefs = WirePodConfig.Prefs;
			JObject jobject = new JObject();
			jobject.Add("user_id", AnkiCloudEmulator.kUserId);
			jobject.Add("email", prefs.GetString(AnkiCloudEmulator.kEmailKey, string.Empty));
			jobject.Add("email_is_verified", true);
			jobject.Add("dob", prefs.GetString(AnkiCloudEmulator.kDobKey, AnkiCloudEmulator.kDefaultDob));
			jobject.Add("status", "active");
			// LoginFlowModel treats accounts not created by this app as legacy accounts and, among other checks,
			// forces a password change unless password_is_complex is set.
			jobject.Add("created_by_app_name", "chewie");
			jobject.Add("password_is_complex", true);
			return jobject;
		}

		private static void SaveUser(string email, string dob)
		{
			Android.Content.ISharedPreferencesEditor sharedPreferencesEditor = WirePodConfig.Prefs.Edit().PutString(AnkiCloudEmulator.kEmailKey, email.Trim());
			if (!string.IsNullOrEmpty(dob))
			{
				sharedPreferencesEditor.PutString(AnkiCloudEmulator.kDobKey, dob);
			}
			sharedPreferencesEditor.Apply();
		}

		private static JObject Error(string code, string message)
		{
			return new JObject
			{
				{ "code", code },
				{ "message", message }
			};
		}

		/// <summary>Delivers the response on the main thread, as core.Services.Http.HttpService does.</summary>
		private static void Respond(HttpMethod method, Uri uri, HttpStatusCode statusCode, JObject content, HttpResponseDelegate callback)
		{
			LocalHttpResponse response = new LocalHttpResponse(method, uri, statusCode, content);
			if (callback == null)
			{
				return;
			}
			Mvx.Resolve<IUIThreadInvocationService>().InvokeOnMainThread(delegate
			{
				callback(response);
			});
		}

		private static string GetQueryValue(Uri uri, string name)
		{
			foreach (string text in uri.Query.TrimStart('?').Split('&'))
			{
				int num = text.IndexOf('=');
				if (num > 0 && Uri.UnescapeDataString(text.Substring(0, num)) == name)
				{
					return Uri.UnescapeDataString(text.Substring(num + 1).Replace('+', ' '));
				}
			}
			return null;
		}

		private static bool IsHostOrSubdomain(string host, string domain)
		{
			return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
		}

		// Matches the user id wire-pod puts in the robot's token.
		private const string kUserId = "wirepod";

		private const string kDefaultDob = "1990-01-01";

		private const string kEmailKey = "account_email";

		private const string kDobKey = "account_dob";
	}
}
