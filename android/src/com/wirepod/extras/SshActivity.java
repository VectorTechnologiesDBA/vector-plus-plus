package com.wirepod.extras;

import android.app.Activity;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.jcraft.jsch.ChannelShell;
import com.jcraft.jsch.JSch;
import com.jcraft.jsch.Session;

import java.io.InputStream;
import java.io.OutputStream;

/** Interactive root shell on Vector over SSH (key bundled in the APK), landscape, with a row of terminal keys. */
public class SshActivity extends Activity {
    static final String KEY_ASSET = "wirepod/ssh_root_key";
    private static final int MAX_CHARS = 60000;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final StringBuilder screen = new StringBuilder();
    private TextView output;
    private ScrollView scroll;
    private EditText input;
    private volatile Session session;
    private volatile OutputStream toShell;
    private boolean ctrl;
    private TextView ctrlKey;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(0xff0d0d0d);
        root.setFitsSystemWindows(true);

        scroll = new ScrollView(this);
        output = new TextView(this);
        output.setTypeface(Typeface.MONOSPACE);
        output.setTextSize(12);
        output.setTextColor(Screen.GREEN);
        output.setTextIsSelectable(true);
        output.setPadding(dp(8), dp(6), dp(8), dp(6));
        scroll.addView(output);
        scroll.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                input.requestFocus();
            }
        });
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

        root.addView(keyRow());

        LinearLayout line = new LinearLayout(this);
        line.setGravity(Gravity.CENTER_VERTICAL);
        line.setBackgroundColor(0xff1a1a1a);
        TextView prompt = mono("$", Screen.GOLD);
        prompt.setPadding(dp(8), 0, dp(4), 0);
        line.addView(prompt);
        input = new EditText(this);
        input.setTypeface(Typeface.MONOSPACE);
        input.setTextSize(14);
        input.setTextColor(Screen.WHITE);
        input.setBackgroundColor(0);
        input.setSingleLine(true);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
                | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        input.setImeOptions(EditorInfo.IME_ACTION_SEND | EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_FLAG_NO_FULLSCREEN);
        input.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            public boolean onEditorAction(TextView v, int id, KeyEvent e) {
                sendLine();
                return true;
            }
        });
        line.addView(input, new LinearLayout.LayoutParams(0, -2, 1f));
        root.addView(line);

        setContentView(root);
        append("Connecting to Vector…\n");
        new Thread(new Runnable() {
            public void run() {
                connect();
            }
        }).start();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        Session s = session;
        if (s != null) {
            s.disconnect();
        }
    }

    private View keyRow() {
        LinearLayout row = new LinearLayout(this);
        row.setBackgroundColor(0xff262626);
        ctrlKey = key(row, "Ctrl", null);
        ctrlKey.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                ctrl = !ctrl;
                ctrlKey.setTextColor(ctrl ? Screen.BLACK : Screen.WHITE);
                ctrlKey.setBackgroundColor(ctrl ? Screen.GOLD : 0);
            }
        });
        key(row, "Esc", "\u001b");
        key(row, "Tab", "\t");
        key(row, "↑", "\u001b[A");
        key(row, "↓", "\u001b[B");
        key(row, "←", "\u001b[D");
        key(row, "→", "\u001b[C");
        key(row, "^C", "\u0003");
        key(row, "^D", "\u0004");
        key(row, "^Z", "\u001a");
        key(row, "|", "|");
        key(row, "/", "/");
        key(row, "-", "-");
        key(row, "~", "~");
        key(row, "Clear", null).setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                screen.setLength(0);
                output.setText("");
            }
        });
        key(row, "Exit", null).setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                finish();
            }
        });
        HorizontalScrollView h = new HorizontalScrollView(this);
        h.setHorizontalScrollBarEnabled(false);
        h.addView(row);
        return h;
    }

    private TextView key(LinearLayout row, String label, final String bytes) {
        TextView k = mono(label, Screen.WHITE);
        k.setTextSize(14);
        k.setGravity(Gravity.CENTER);
        k.setPadding(dp(14), dp(10), dp(14), dp(10));
        if (bytes != null) {
            k.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    // Send whatever is typed first, so "ls" + Tab completes.
                    String typed = input.getText().toString();
                    input.setText("");
                    write(typed + bytes);
                }
            });
        }
        row.addView(k);
        return k;
    }

    private TextView mono(String s, int color) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTypeface(Typeface.MONOSPACE);
        t.setTextColor(color);
        return t;
    }

    private void sendLine() {
        String typed = input.getText().toString();
        input.setText("");
        if (ctrl && typed.length() > 0) {
            // Ctrl + letter -> control character (Ctrl+C = 0x03 …).
            char c = Character.toUpperCase(typed.charAt(0));
            ctrl = false;
            ctrlKey.setTextColor(Screen.WHITE);
            ctrlKey.setBackgroundColor(0);
            if (c >= '@' && c <= '_') {
                write(String.valueOf((char) (c - '@')));
                return;
            }
        }
        write(typed + "\n");
    }

    private void write(final String s) {
        final OutputStream out = toShell;
        if (out == null) {
            return;
        }
        new Thread(new Runnable() {
            public void run() {
                try {
                    out.write(s.getBytes("UTF-8"));
                    out.flush();
                } catch (Exception e) {
                    append("\n[" + e.getMessage() + "]\n");
                }
            }
        }).start();
    }

    private void connect() {
        try {
            String host = Wp.robot(this).optString("ip_address", "");
            if (host.length() == 0) {
                throw new Exception("wire-pod doesn't know Vector's IP address.");
            }
            append("root@" + host + "\n");
            byte[] key = Wp.read(getAssets().open(KEY_ASSET)).getBytes("UTF-8");
            JSch jsch = new JSch();
            jsch.addIdentity("vector", key, null, null);
            Session s = jsch.getSession("root", host, 22);
            s.setConfig("StrictHostKeyChecking", "no");
            s.setConfig("PubkeyAcceptedAlgorithms", "ssh-rsa,rsa-sha2-256,rsa-sha2-512,ssh-ed25519");
            s.setConfig("server_host_key", "ssh-rsa,rsa-sha2-256,rsa-sha2-512,ssh-ed25519,ecdsa-sha2-nistp256");
            s.connect(10000);
            session = s;
            ChannelShell ch = (ChannelShell) s.openChannel("shell");
            // "dumb": the shell sends plain text, which this view shows as-is (no full-screen apps like vi/top).
            ch.setPtyType("dumb", 160, 40, 0, 0);
            InputStream in = ch.getInputStream();
            toShell = ch.getOutputStream();
            ch.connect(10000);
            byte[] buf = new byte[4096];
            int n;
            while ((n = in.read(buf)) > 0) {
                append(clean(new String(buf, 0, n, "UTF-8")));
            }
            append("\n[connection closed]\n");
        } catch (Exception e) {
            append("\n[" + (e.getMessage() == null ? e.toString() : e.getMessage()) + "]\n");
        } finally {
            toShell = null;
        }
    }

    /** Drops ANSI escape sequences and carriage returns that a dumb terminal can't draw. */
    private static String clean(String s) {
        return s.replaceAll("\u001b\\[[0-9;?]*[ -/]*[@-~]", "")
                .replaceAll("\u001b\\][^\u0007]*\u0007", "")
                .replace("\r", "");
    }

    private void append(final String s) {
        ui.post(new Runnable() {
            public void run() {
                for (int i = 0; i < s.length(); i++) {
                    char c = s.charAt(i);
                    if (c == '\b' || c == 0x7f) {
                        if (screen.length() > 0) {
                            screen.setLength(screen.length() - 1);
                        }
                    } else if (c == '\u0007') {
                        continue;
                    } else {
                        screen.append(c);
                    }
                }
                if (screen.length() > MAX_CHARS) {
                    screen.delete(0, screen.length() - MAX_CHARS);
                }
                output.setText(screen);
                scroll.post(new Runnable() {
                    public void run() {
                        scroll.fullScroll(View.FOCUS_DOWN);
                        input.requestFocus();
                    }
                });
            }
        });
    }

    private int dp(int v) {
        return (int) (v * getResources().getDisplayMetrics().density);
    }
}
