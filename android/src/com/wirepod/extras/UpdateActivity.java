package com.wirepod.extras;

import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import android.view.View;
import android.widget.Button;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;

/**
 * Settings > About > Check for updates: compares this build with the latest Vector++ release on GitHub, shows its
 * release notes, and installs the release's APK through Android's package installer (the user confirms).
 * Releases must be signed with the same key as this build, or Android refuses the update.
 */
public class UpdateActivity extends Screen {
    /** This build's version. Keep it in step with the release tag (v1.2.1 etc.). */
    static final String VERSION = "1.2.3";
    static final String RELEASES = "https://api.github.com/repos/VectorTechnologiesDBA/vector-plus-plus/releases/latest";
    private static final String ACTION_INSTALLED = "com.wirepod.extras.UPDATE_STATUS";

    private TextView versions, notes, status;
    private Button update;
    private volatile String apkUrl;

    @Override
    protected String title() {
        return "Check for updates";
    }

    @Override
    protected void build() {
        versions = heading("Vector++ " + VERSION);
        status = status();
        update = button("Update", new View.OnClickListener() {
            public void onClick(View v) {
                install();
            }
        });
        update.setVisibility(View.GONE);
        heading("Release notes");
        notes = note("");
        check();
    }

    private void check() {
        background(status, "Checking GitHub for updates…", new Task() {
            public String run() throws Exception {
                JSONObject release = new JSONObject(fetch(RELEASES));
                final String tag = release.optString("tag_name", "").replaceFirst("^[vV]", "");
                final String body = plain(release.optString("body", ""));
                String url = null;
                JSONArray assets = release.optJSONArray("assets");
                for (int i = 0; assets != null && i < assets.length(); i++) {
                    JSONObject a = assets.getJSONObject(i);
                    if (a.optString("name", "").toLowerCase().endsWith(".apk")) {
                        url = a.optString("browser_download_url", null);
                        break;
                    }
                }
                apkUrl = url;
                final boolean newer = compare(tag, VERSION) > 0;
                final boolean canInstall = newer && url != null;
                onUi(new Runnable() {
                    public void run() {
                        versions.setText("Installed: " + VERSION + "    Latest: " + tag);
                        notes.setText(body.length() == 0 ? "No release notes." : body);
                        update.setText("Update to " + tag);
                        update.setVisibility(canInstall ? View.VISIBLE : View.GONE);
                    }
                });
                if (!newer) {
                    return "You're up to date.";
                }
                return url == null ? "Version " + tag + " is out, but its release has no APK attached yet." : "Version " + tag + " is available.";
            }
        });
    }

    private void install() {
        if (Build.VERSION.SDK_INT >= 26 && !getPackageManager().canRequestPackageInstalls()) {
            status.setTextColor(GOLD);
            status.setText("Allow Vector++ to install updates, then come back and tap Update again.");
            startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName())));
            return;
        }
        final String url = apkUrl;
        update.setEnabled(false);
        background(status, "Downloading update…", new Task() {
            public String run() throws Exception {
                PackageInstaller installer = getPackageManager().getPackageInstaller();
                PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
                int id = installer.createSession(params);
                PackageInstaller.Session session = installer.openSession(id);
                try {
                    HttpURLConnection conn = (HttpURLConnection) new URL(url).openConnection();
                    conn.setInstanceFollowRedirects(true);
                    conn.setConnectTimeout(15000);
                    conn.setReadTimeout(30000);
                    final int total = conn.getContentLength();
                    InputStream in = conn.getInputStream();
                    OutputStream out = session.openWrite("vectorpp.apk", 0, total > 0 ? total : -1);
                    byte[] buf = new byte[65536];
                    int n, done = 0, lastPct = -1;
                    while ((n = in.read(buf)) > 0) {
                        out.write(buf, 0, n);
                        done += n;
                        final int pct = total > 0 ? (int) (100L * done / total) : -1;
                        if (pct != lastPct && pct % 5 == 0) {
                            lastPct = pct;
                            onUi(new Runnable() {
                                public void run() {
                                    status.setText("Downloading update… " + pct + "%");
                                }
                            });
                        }
                    }
                    session.fsync(out);
                    out.close();
                    in.close();
                    // Android reports the result (including "needs the user to confirm") back to this screen.
                    Intent result = new Intent(UpdateActivity.this, UpdateActivity.class).setAction(ACTION_INSTALLED);
                    PendingIntent pi = PendingIntent.getActivity(UpdateActivity.this, 0, result,
                            PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0));
                    session.commit(pi.getIntentSender());
                } catch (Exception e) {
                    session.abandon();
                    throw e;
                } finally {
                    session.close();
                }
                return "Installing… confirm when Android asks.";
            }
        });
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        if (!ACTION_INSTALLED.equals(intent.getAction())) {
            return;
        }
        int code = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        if (code == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirm = intent.getParcelableExtra(Intent.EXTRA_INTENT);
            if (confirm != null) {
                startActivity(confirm);
            }
            return;
        }
        update.setEnabled(true);
        if (code != PackageInstaller.STATUS_SUCCESS) {
            String msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
            status.setTextColor(RED);
            status.setText("Update failed: " + (msg == null ? "code " + code : msg)
                    + (code == PackageInstaller.STATUS_FAILURE_CONFLICT ? "\nThe release is signed with a different key than this build." : ""));
        }
    }

    private static String fetch(String url) throws Exception {
        HttpURLConnection conn = (HttpURLConnection) new URL(url).openConnection();
        conn.setConnectTimeout(10000);
        conn.setReadTimeout(15000);
        conn.setRequestProperty("Accept", "application/vnd.github+json");
        int code = conn.getResponseCode();
        if (code == 404) {
            throw new Exception("No Vector++ releases are published yet.");
        }
        if (code >= 400) {
            throw new Exception("GitHub answered " + code + ". Try again later.");
        }
        return Wp.read(conn.getInputStream());
    }

    /** Release notes are Markdown; show them as plain text. */
    private static String plain(String md) {
        return md.replace("\r", "")
                .replaceAll("(?m)^```.*$\\n?", "")
                .replaceAll("(?m)^#+\\s*", "")
                .replaceAll("\\*\\*(.+?)\\*\\*", "$1")
                .replaceAll("(?m)^[-*]\\s+", "•  ")
                .replaceAll("\\[([^\\]]+)\\]\\([^)]+\\)", "$1")
                .trim();
    }

    /** Compares dotted versions numerically: 1.10.0 > 1.9.2. */
    static int compare(String a, String b) {
        String[] x = a.split("[.\\-]"), y = b.split("[.\\-]");
        for (int i = 0; i < Math.max(x.length, y.length); i++) {
            int p = i < x.length ? num(x[i]) : 0, q = i < y.length ? num(y[i]) : 0;
            if (p != q) {
                return p < q ? -1 : 1;
            }
        }
        return 0;
    }

    private static int num(String s) {
        try {
            return Integer.parseInt(s.replaceAll("\\D.*", ""));
        } catch (Exception e) {
            return 0;
        }
    }
}
