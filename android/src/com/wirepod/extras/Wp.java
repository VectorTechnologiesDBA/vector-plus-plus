package com.wirepod.extras;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;

/**
 * Shared state and wire-pod calls for the extra screens. The wire-pod address is the one the user entered at
 * sign-in; WirePod.dll (C#) stores it in the "wirepod" shared preferences, which these Java screens read too.
 */
final class Wp {
    static final String PREFS = "wirepod";
    static final String KEY_SERVER = "server";
    static final String KEY_EXTRAS = "extras_enabled";

    private Wp() {
    }

    static SharedPreferences prefs(Context c) {
        return c.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    static String server(Context c) {
        String s = prefs(c).getString(KEY_SERVER, null);
        return (s == null || s.length() == 0) ? "escapepod.local:8080" : s;
    }

    static boolean extrasEnabled(Context c) {
        return "1".equals(prefs(c).getString(KEY_EXTRAS, "0"));
    }

    static void setExtrasEnabled(Context c, boolean on) {
        prefs(c).edit().putString(KEY_EXTRAS, on ? "1" : "0").apply();
    }

    static String enc(String s) {
        try {
            return URLEncoder.encode(s == null ? "" : s, "UTF-8");
        } catch (Exception e) {
            return "";
        }
    }

    /** GET http://server{path}; throws with wire-pod's message on a non-2xx answer. Call off the UI thread. */
    static String get(Context c, String path) throws Exception {
        return send(c, "GET", path, null);
    }

    static String postJson(Context c, String path, JSONObject body) throws Exception {
        return send(c, "POST", path, body.toString());
    }

    static JSONObject getJson(Context c, String path) throws Exception {
        return new JSONObject(get(c, path));
    }

    /** The first robot wire-pod knows: {"esn": ..., "ip_address": ...}. */
    static JSONObject robot(Context c) throws Exception {
        JSONArray robots = getJson(c, "/api-sdk/get_sdk_info").optJSONArray("robots");
        if (robots == null || robots.length() == 0) {
            throw new Exception("wire-pod doesn't know any robots yet. Finish setting Vector up first.");
        }
        return robots.getJSONObject(0);
    }

    /** Calls a wire-pod SDK endpoint for the robot, e.g. sdk(c, "say_text", "text=hi"). */
    static String sdk(Context c, String endpoint, String query) throws Exception {
        String esn = robot(c).getString("esn");
        return get(c, "/api-sdk/" + endpoint + "?serial=" + enc(esn) + (query == null ? "" : "&" + query));
    }

    private static String send(Context c, String method, String path, String json) throws Exception {
        HttpURLConnection conn = (HttpURLConnection) new URL("http://" + server(c) + path).openConnection();
        conn.setConnectTimeout(8000);
        conn.setReadTimeout(20000);
        conn.setRequestMethod(method);
        if (json != null) {
            conn.setDoOutput(true);
            conn.setRequestProperty("Content-Type", "application/json");
            OutputStream out = conn.getOutputStream();
            out.write(json.getBytes("UTF-8"));
            out.close();
        }
        int code = conn.getResponseCode();
        InputStream in = code >= 400 ? conn.getErrorStream() : conn.getInputStream();
        String text = in == null ? "" : read(in);
        conn.disconnect();
        if (code >= 400) {
            throw new Exception("wire-pod answered " + code + ": " + text.trim());
        }
        return text;
    }

    static String read(InputStream in) throws Exception {
        ByteArrayOutputStream buf = new ByteArrayOutputStream();
        byte[] b = new byte[8192];
        int n;
        while ((n = in.read(b)) > 0) {
            buf.write(b, 0, n);
        }
        in.close();
        return buf.toString("UTF-8");
    }
}
