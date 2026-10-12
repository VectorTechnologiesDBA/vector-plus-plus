package com.wirepod.extras;

import android.app.Activity;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;

/** Landscape driving: live camera from wire-pod's /cam-stream, joystick for wheels, hold buttons for head/lift. */
public class DriveActivity extends Activity {
    private final Handler ui = new Handler(Looper.getMainLooper());
    private ImageView video;
    private TextView status;
    private volatile boolean running;
    private volatile String esn;
    // Latest wheel speeds wanted by the joystick; the sender thread forwards changes.
    private volatile int wantL, wantR;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Screen.BLACK);
        video = new ImageView(this);
        video.setScaleType(ImageView.ScaleType.FIT_CENTER);
        root.addView(video, new FrameLayout.LayoutParams(-1, -1));

        status = label("Connecting…", 14);
        root.addView(status, new FrameLayout.LayoutParams(-2, -2, Gravity.TOP | Gravity.CENTER_HORIZONTAL));

        TextView back = label("‹", 34);
        back.setPadding(dp(16), dp(4), dp(24), dp(8));
        back.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                finish();
            }
        });
        root.addView(back, new FrameLayout.LayoutParams(-2, -2, Gravity.TOP | Gravity.LEFT));

        Joystick stick = new Joystick(this);
        FrameLayout.LayoutParams sp = new FrameLayout.LayoutParams(dp(180), dp(180), Gravity.BOTTOM | Gravity.LEFT);
        sp.setMargins(dp(24), 0, 0, dp(24));
        root.addView(stick, sp);

        LinearLayout right = new LinearLayout(this);
        right.setOrientation(LinearLayout.HORIZONTAL);
        right.addView(column("Head", "move_head"));
        right.addView(column("Lift", "move_lift"));
        FrameLayout.LayoutParams rp = new FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM | Gravity.RIGHT);
        rp.setMargins(0, 0, dp(24), dp(24));
        root.addView(right, rp);

        root.addView(sayBar(), new FrameLayout.LayoutParams(dp(320), -2, Gravity.TOP | Gravity.RIGHT));

        setContentView(root);
    }

    /** Text box + Say button: Vector speaks while you drive. */
    private LinearLayout sayBar() {
        LinearLayout bar = new LinearLayout(this);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        bar.setPadding(dp(8), dp(8), dp(16), dp(8));
        final android.widget.EditText text = new android.widget.EditText(this);
        text.setHint("Say something…");
        text.setSingleLine(true);
        text.setTextColor(Screen.WHITE);
        text.setHintTextColor(Screen.GREY);
        text.setTextSize(15);
        text.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_SEND
                | android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI);
        android.graphics.drawable.GradientDrawable bg = new android.graphics.drawable.GradientDrawable();
        bg.setColor(0x99000000);
        bg.setStroke(dp(1), Screen.GREY);
        bg.setCornerRadius(dp(20));
        text.setBackground(bg);
        text.setPadding(dp(14), dp(8), dp(14), dp(8));
        bar.addView(text, new LinearLayout.LayoutParams(0, -2, 1f));
        final TextView say = label("Say", 15);
        say.setTextColor(Screen.BLACK);
        android.graphics.drawable.GradientDrawable sbg = new android.graphics.drawable.GradientDrawable();
        sbg.setColor(Screen.GOLD);
        sbg.setCornerRadius(dp(20));
        say.setBackground(sbg);
        say.setShadowLayer(0, 0, 0, 0);
        say.setPadding(dp(16), dp(9), dp(16), dp(9));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
        lp.leftMargin = dp(8);
        bar.addView(say, lp);
        final Runnable send = new Runnable() {
            public void run() {
                final String t = text.getText().toString().trim();
                if (t.length() == 0) {
                    return;
                }
                text.setText("");
                new Thread(new Runnable() {
                    public void run() {
                        try {
                            show("Saying…");
                            call(serial(), "say_text", "text=" + Wp.enc(t));
                            show("");
                        } catch (Exception e) {
                            show(e.getMessage());
                        }
                    }
                }).start();
            }
        };
        say.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                send.run();
            }
        });
        text.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            public boolean onEditorAction(TextView v, int actionId, android.view.KeyEvent e) {
                send.run();
                return true;
            }
        });
        return bar;
    }

    @Override
    protected void onResume() {
        super.onResume();
        running = true;
        new Thread(new Runnable() {
            public void run() {
                stream();
            }
        }).start();
        new Thread(new Runnable() {
            public void run() {
                sendWheels();
            }
        }).start();
    }

    @Override
    protected void onPause() {
        super.onPause();
        running = false;
        final String serial = esn;
        if (serial != null) {
            new Thread(new Runnable() {
                public void run() {
                    try {
                        call(serial, "move_wheels", "lw=0&rw=0");
                        call(serial, "stop_cam_stream", null);
                        call(serial, "release_behavior_control", null);
                    } catch (Exception ignored) {
                    }
                }
            }).start();
        }
    }

    // ---- network ----

    private String serial() throws Exception {
        if (esn == null) {
            esn = Wp.robot(this).getString("esn");
            call(esn, "assume_behavior_control", "priority=high");
        }
        return esn;
    }

    private void call(String serial, String endpoint, String query) throws Exception {
        Wp.get(this, "/api-sdk/" + endpoint + "?serial=" + Wp.enc(serial) + (query == null ? "" : "&" + query));
    }

    /** Reads the MJPEG stream and shows each JPEG (FFD8 … FFD9) as it arrives; reconnects on errors. */
    private void stream() {
        while (running) {
            HttpURLConnection conn = null;
            try {
                String s = serial();
                conn = (HttpURLConnection) new URL("http://" + Wp.server(this) + "/cam-stream?serial=" + Wp.enc(s)).openConnection();
                conn.setConnectTimeout(8000);
                conn.setReadTimeout(10000);
                InputStream in = conn.getInputStream();
                show("");
                ByteArrayOutputStream frame = new ByteArrayOutputStream();
                boolean inFrame = false;
                int prev = -1;
                int b;
                while (running && (b = in.read()) != -1) {
                    if (!inFrame) {
                        if (prev == 0xFF && b == 0xD8) {
                            inFrame = true;
                            frame.reset();
                            frame.write(0xFF);
                            frame.write(0xD8);
                        }
                    } else {
                        frame.write(b);
                        if (prev == 0xFF && b == 0xD9) {
                            inFrame = false;
                            byte[] jpg = frame.toByteArray();
                            final Bitmap bmp = BitmapFactory.decodeByteArray(jpg, 0, jpg.length);
                            if (bmp != null) {
                                ui.post(new Runnable() {
                                    public void run() {
                                        video.setImageBitmap(bmp);
                                    }
                                });
                            }
                        }
                    }
                    prev = b;
                }
            } catch (Exception e) {
                show("Camera: " + (e.getMessage() == null ? e.toString() : e.getMessage()));
                sleep(2000);
            } finally {
                if (conn != null) {
                    conn.disconnect();
                }
            }
        }
    }

    private void sendWheels() {
        int sentL = 0, sentR = 0;
        while (running) {
            int l = wantL, r = wantR;
            if (l != sentL || r != sentR) {
                try {
                    call(serial(), "move_wheels", "lw=" + l + "&rw=" + r);
                    sentL = l;
                    sentR = r;
                } catch (Exception e) {
                    show(e.getMessage());
                }
            }
            sleep(100);
        }
    }

    private void hold(final String endpoint, final String speed) {
        new Thread(new Runnable() {
            public void run() {
                try {
                    call(serial(), endpoint, "speed=" + speed);
                } catch (Exception e) {
                    show(e.getMessage());
                }
            }
        }).start();
    }

    // ---- views ----

    private LinearLayout column(String title, final String endpoint) {
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setGravity(Gravity.CENTER_HORIZONTAL);
        col.setPadding(dp(8), 0, dp(8), 0);
        col.addView(holdButton("▲", endpoint, "2"));
        TextView t = label(title, 13);
        t.setPadding(0, dp(6), 0, dp(6));
        col.addView(t);
        col.addView(holdButton("▼", endpoint, "-2"));
        return col;
    }

    private TextView holdButton(String text, final String endpoint, final String speed) {
        TextView b = label(text, 26);
        b.setGravity(Gravity.CENTER);
        android.graphics.drawable.GradientDrawable bg = new android.graphics.drawable.GradientDrawable();
        bg.setColor(0x99000000);
        bg.setStroke(dp(2), Screen.GOLD);
        bg.setCornerRadius(dp(36));
        b.setBackground(bg);
        b.setLayoutParams(new LinearLayout.LayoutParams(dp(72), dp(72)));
        b.setOnTouchListener(new View.OnTouchListener() {
            public boolean onTouch(View v, MotionEvent e) {
                int a = e.getActionMasked();
                if (a == MotionEvent.ACTION_DOWN) {
                    v.setAlpha(0.6f);
                    hold(endpoint, speed);
                } else if (a == MotionEvent.ACTION_UP || a == MotionEvent.ACTION_CANCEL) {
                    v.setAlpha(1f);
                    hold(endpoint, "0");
                }
                return true;
            }
        });
        return b;
    }

    private TextView label(String s, int sp) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(Screen.WHITE);
        t.setShadowLayer(4, 0, 0, 0xff000000);
        return t;
    }

    private void show(final String s) {
        ui.post(new Runnable() {
            public void run() {
                status.setText(s == null ? "" : s);
            }
        });
    }

    private int dp(int v) {
        return (int) (v * getResources().getDisplayMetrics().density);
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException ignored) {
        }
    }

    /** Drag the knob: up/down drives, left/right turns. Releasing stops. */
    private class Joystick extends View {
        private final Paint ring = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint knob = new Paint(Paint.ANTI_ALIAS_FLAG);
        private float kx, ky;

        Joystick(Context c) {
            super(c);
            ring.setStyle(Paint.Style.STROKE);
            ring.setStrokeWidth(dp(2));
            ring.setColor(Screen.GOLD);
            knob.setColor(0xcce6d19e);
        }

        @Override
        protected void onDraw(Canvas c) {
            float cx = getWidth() / 2f, cy = getHeight() / 2f, r = Math.min(cx, cy) - dp(4);
            Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
            fill.setColor(0x66000000);
            c.drawCircle(cx, cy, r, fill);
            c.drawCircle(cx, cy, r, ring);
            c.drawCircle(cx + kx * r, cy + ky * r, r * 0.35f, knob);
        }

        @Override
        public boolean onTouchEvent(MotionEvent e) {
            float cx = getWidth() / 2f, cy = getHeight() / 2f, r = Math.min(cx, cy) - dp(4);
            int a = e.getActionMasked();
            if (a == MotionEvent.ACTION_UP || a == MotionEvent.ACTION_CANCEL) {
                kx = 0;
                ky = 0;
            } else {
                kx = (e.getX() - cx) / r;
                ky = (e.getY() - cy) / r;
                float len = (float) Math.sqrt(kx * kx + ky * ky);
                if (len > 1) {
                    kx /= len;
                    ky /= len;
                }
            }
            float fwd = -ky, turn = kx;
            wantL = Math.round((fwd + turn * 0.7f) * 200);
            wantR = Math.round((fwd - turn * 0.7f) * 200);
            invalidate();
            return true;
        }
    }
}
