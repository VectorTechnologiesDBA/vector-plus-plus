package com.wirepod.extras;

import android.view.View;
import android.widget.EditText;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.TextView;

import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * Remote access: reach Vector through another address when his LAN address isn't reachable, e.g. a port forward on
 * the wire-pod machine's Tailscale IP. WirePod.Remote (C#) reads these settings when the app builds Vector's URLs.
 */
public class RemoteActivity extends Screen {
    private static final String[] NAMES = {"Off", "Automatic (use it when Vector isn't reachable directly)", "Always"};
    private static final String[] MODES = {"off", "auto", "always"};

    private RadioGroup group;
    private EditText host, port;
    private TextView status;

    @Override
    protected String title() {
        return "Remote access";
    }

    @Override
    protected void build() {
        note("Lets the app reach Vector away from home through another address, such as a port forward on your "
                + "wire-pod machine's Tailscale IP. Vector's certificate is still checked against the one saved at setup.");
        heading("Mode");
        group = new RadioGroup(this);
        String mode = Wp.prefs(this).getString("remote_mode", "off");
        for (int i = 0; i < NAMES.length; i++) {
            RadioButton r = new RadioButton(this);
            r.setId(200 + i);
            r.setText(NAMES[i]);
            r.setTextColor(WHITE);
            group.addView(r);
            if (MODES[i].equals(mode)) {
                group.check(200 + i);
            }
        }
        if (group.getCheckedRadioButtonId() == -1) {
            group.check(200);
        }
        body.addView(group);

        heading("Remote address");
        host = field("Host (e.g. your wire-pod machine's Tailscale IP)", Wp.prefs(this).getString("remote_host", suggestedHost()), false);
        port = field("Port", Wp.prefs(this).getString("remote_port", "8443"), false);
        button("Save", new View.OnClickListener() {
            public void onClick(View v) {
                save();
            }
        });
        button("Test remote address", new View.OnClickListener() {
            public void onClick(View v) {
                test();
            }
        });
        status = status();
        note("On the wire-pod machine, forward the port to Vector's port 443, for example:\n"
                + "socat TCP-LISTEN:8443,bind=<Tailscale IP>,fork,reuseaddr TCP:<Vector's IP>:443\n\n"
                + "Extra features (Drive, Talk, …) go through wire-pod, so sign in with wire-pod's Tailscale address "
                + "if you want those away from home too. Reopen the app after changing these settings.");
    }

    /** wire-pod's own host if it is a Tailscale (100.x) address, otherwise empty. */
    private String suggestedHost() {
        String server = Wp.server(this);
        int colon = server.lastIndexOf(':');
        String h = colon > 0 ? server.substring(0, colon) : server;
        return h.startsWith("100.") ? h : "";
    }

    private String mode() {
        int i = group.getCheckedRadioButtonId() - 200;
        return i < 0 || i >= MODES.length ? "off" : MODES[i];
    }

    private void save() {
        String h = host.getText().toString().trim();
        String p = port.getText().toString().trim();
        int n;
        try {
            n = Integer.parseInt(p);
        } catch (NumberFormatException e) {
            n = -1;
        }
        if (!"off".equals(mode()) && (h.length() == 0 || n <= 0 || n > 65535)) {
            status.setTextColor(RED);
            status.setText("Enter a host and a port between 1 and 65535.");
            return;
        }
        Wp.prefs(this).edit().putString("remote_mode", mode()).putString("remote_host", h)
                .putString("remote_port", n > 0 ? String.valueOf(n) : "8443").apply();
        status.setTextColor(GREEN);
        status.setText("Saved. Reopen the app to use it.");
    }

    private void test() {
        final String h = host.getText().toString().trim();
        final String p = port.getText().toString().trim();
        background(status, "Connecting to " + h + ":" + p + "…", new Task() {
            public String run() throws Exception {
                Socket s = new Socket();
                try {
                    s.connect(new InetSocketAddress(h, Integer.parseInt(p)), 5000);
                } finally {
                    s.close();
                }
                return "Reached " + h + ":" + p + ". If Vector still doesn't connect, check that it forwards to his port 443.";
            }
        });
    }
}
