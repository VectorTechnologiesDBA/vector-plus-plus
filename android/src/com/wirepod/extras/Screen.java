package com.wirepod.extras;

import android.app.Activity;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/** Base for the extra screens: header with back arrow + title, scrolling body, styled like the app. */
abstract class Screen extends Activity {
    static final int BLACK = 0xff1a1a1a;
    static final int DARK_GREY = 0xff3b3b3b;
    static final int GREY = 0xff808080;
    static final int WHITE = 0xffe0dbd9;
    static final int TEXT = 0xffcfc7c4;
    static final int GOLD = 0xffe6d19e;
    static final int GREEN = 0xff4fed94;
    static final int RED = 0xffe24610;

    protected LinearLayout body;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private Typeface regular;
    private Typeface medium;

    protected abstract String title();

    protected abstract void build();

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        regular = font("ttnorms_regular");
        medium = font("ttnorms_medium");

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BLACK);
        root.setFitsSystemWindows(true);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(8), dp(12), dp(16), dp(12));
        TextView back = text("‹", 30, WHITE, false);
        back.setPadding(dp(12), 0, dp(16), 0);
        back.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                finish();
            }
        });
        header.addView(back);
        header.addView(text(title(), 20, WHITE, true));
        root.addView(header);

        ScrollView scroll = new ScrollView(this);
        body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(20), dp(8), dp(20), dp(120));
        scroll.addView(body);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));
        setContentView(root);
        build();
    }

    // ---- building blocks ----

    TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        Typeface f = bold ? medium : regular;
        if (f != null) {
            t.setTypeface(f);
        }
        return t;
    }

    TextView heading(String s) {
        TextView t = text(s, 15, GOLD, true);
        t.setPadding(0, dp(20), 0, dp(6));
        body.addView(t);
        return t;
    }

    TextView note(String s) {
        TextView t = text(s, 14, GREY, false);
        t.setPadding(0, dp(4), 0, dp(4));
        body.addView(t);
        return t;
    }

    EditText field(String label, String value, boolean multiline) {
        body.addView(withPadding(text(label, 14, TEXT, false), 0, dp(10), 0, dp(4)));
        EditText e = new EditText(this);
        e.setText(value == null ? "" : value);
        e.setTextColor(WHITE);
        e.setHintTextColor(GREY);
        e.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        if (regular != null) {
            e.setTypeface(regular);
        }
        e.setSingleLine(!multiline);
        e.setBackground(box(DARK_GREY, 0));
        e.setPadding(dp(12), dp(10), dp(12), dp(10));
        body.addView(e, new LinearLayout.LayoutParams(-1, -2));
        return e;
    }

    Button button(String label, View.OnClickListener click) {
        Button b = new Button(this);
        b.setText(label.toUpperCase());
        b.setTextColor(BLACK);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        if (medium != null) {
            b.setTypeface(medium);
        }
        b.setBackground(box(GOLD, 0));
        b.setOnClickListener(click);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, dp(52));
        lp.topMargin = dp(16);
        body.addView(b, lp);
        return b;
    }

    /** A tappable row like the app's settings rows. */
    TextView row(String label, View.OnClickListener click) {
        TextView t = text(label + "   ›", 18, WHITE, false);
        t.setPadding(dp(4), dp(18), dp(4), dp(18));
        t.setOnClickListener(click);
        body.addView(t);
        View line = new View(this);
        line.setBackgroundColor(DARK_GREY);
        body.addView(line, new LinearLayout.LayoutParams(-1, dp(1)));
        return t;
    }

    TextView status() {
        TextView t = text("", 14, GREY, false);
        t.setPadding(0, dp(14), 0, 0);
        body.addView(t);
        return t;
    }

    // ---- helpers ----

    /** Runs work off the UI thread; result or error is shown in the given status line. */
    void background(final TextView statusLine, final String busy, final Task task) {
        if (statusLine != null) {
            statusLine.setTextColor(GREY);
            statusLine.setText(busy);
        }
        new Thread(new Runnable() {
            public void run() {
                String result;
                boolean ok = true;
                try {
                    result = task.run();
                } catch (Exception e) {
                    ok = false;
                    result = e.getMessage() == null ? e.toString() : e.getMessage();
                }
                final String msg = result;
                final boolean good = ok;
                handler.post(new Runnable() {
                    public void run() {
                        if (statusLine != null) {
                            statusLine.setTextColor(good ? GREEN : RED);
                            statusLine.setText(msg == null ? "" : msg);
                        }
                    }
                });
            }
        }).start();
    }

    void onUi(Runnable r) {
        handler.post(r);
    }

    interface Task {
        String run() throws Exception;
    }

    int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics());
    }

    GradientDrawable box(int fill, int stroke) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(fill);
        d.setCornerRadius(dp(6));
        if (stroke != 0) {
            d.setStroke(dp(1), stroke);
        }
        return d;
    }

    static <T extends View> T withPadding(T v, int l, int t, int r, int b) {
        v.setPadding(l, t, r, b);
        return v;
    }

    /** The app's TT Norms font from res/font (Resources.getFont is API 26, called by reflection). */
    private Typeface font(String name) {
        try {
            int id = getResources().getIdentifier(name, "font", getPackageName());
            return (Typeface) getResources().getClass().getMethod("getFont", int.class).invoke(getResources(), id);
        } catch (Exception e) {
            return null;
        }
    }
}
