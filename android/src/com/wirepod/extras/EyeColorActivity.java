package com.wirepod.extras;

import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

import org.json.JSONObject;

/**
 * Custom eye color, backported from Vector app 1.8: any hue and saturation instead of the seven presets.
 * The robot stores it as settings.custom_eye_color {enabled, hue 0-1, saturation 0-1}; wire-pod sets it for us.
 */
public class EyeColorActivity extends Screen {
    private View preview;
    private SeekBar hue, sat;
    private TextView hueLabel, satLabel, status;

    @Override
    protected String title() {
        return "Custom eye color";
    }

    @Override
    protected void build() {
        preview = new View(this);
        LinearLayout.LayoutParams pp = new LinearLayout.LayoutParams(dp(140), dp(140));
        pp.gravity = android.view.Gravity.CENTER_HORIZONTAL;
        pp.topMargin = dp(16);
        pp.bottomMargin = dp(8);
        body.addView(preview, pp);

        hueLabel = heading("Hue");
        hue = slider(360);
        GradientDrawable rainbow = new GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, new int[] {
                0xffff0000, 0xffffff00, 0xff00ff00, 0xff00ffff, 0xff0000ff, 0xffff00ff, 0xffff0000});
        rainbow.setCornerRadius(dp(6));
        rainbow.setSize(1, dp(10));
        hue.setProgressDrawable(rainbow);

        satLabel = heading("Saturation");
        sat = slider(100);
        sat.setProgress(100);

        button("Apply to Vector", new View.OnClickListener() {
            public void onClick(View v) {
                final float h = hue.getProgress() / 360f, s = sat.getProgress() / 100f;
                background(status, "Sending…", new Task() {
                    public String run() throws Exception {
                        Wp.sdk(EyeColorActivity.this, "custom_eye_color", "hue=" + h + "&sat=" + s);
                        return "Vector's eyes are updated.";
                    }
                });
            }
        });
        note("To go back to a standard color, pick one at the top of Settings. That turns the custom color off.");
        status = status();
        update();
        load();
    }

    private SeekBar slider(int max) {
        SeekBar b = new SeekBar(this);
        b.setMax(max);
        b.setPadding(dp(12), dp(14), dp(12), dp(14));
        b.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar s, int p, boolean user) {
                update();
            }

            public void onStartTrackingTouch(SeekBar s) {
            }

            public void onStopTrackingTouch(SeekBar s) {
            }
        });
        body.addView(b, new LinearLayout.LayoutParams(-1, -2));
        return b;
    }

    private void update() {
        if (hue == null || sat == null) {
            return;
        }
        int color = Color.HSVToColor(new float[] {hue.getProgress(), sat.getProgress() / 100f, 1f});
        GradientDrawable eye = new GradientDrawable();
        eye.setColor(color);
        eye.setCornerRadius(dp(28));
        preview.setBackground(eye);
        hueLabel.setText("Hue  " + hue.getProgress() + "°");
        satLabel.setText("Saturation  " + sat.getProgress() + "%");
    }

    /** custom_eye_color anywhere in wire-pod's answer (the settings jdoc may be nested or a JSON string). */
    private static JSONObject find(JSONObject o, int depth) {
        if (o.optJSONObject("custom_eye_color") != null) {
            return o.optJSONObject("custom_eye_color");
        }
        if (depth > 4) {
            return null;
        }
        java.util.Iterator<String> keys = o.keys();
        while (keys.hasNext()) {
            Object v = o.opt(keys.next());
            JSONObject child = v instanceof JSONObject ? (JSONObject) v : null;
            if (v instanceof String && ((String) v).trim().startsWith("{")) {
                try {
                    child = new JSONObject((String) v);
                } catch (Exception ignored) {
                }
            }
            JSONObject found = child == null ? null : find(child, depth + 1);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    /** Starts from the robot's current custom color, if it has one. */
    private void load() {
        background(status, "Reading Vector's settings…", new Task() {
            public String run() throws Exception {
                final JSONObject custom = find(new JSONObject(Wp.sdk(EyeColorActivity.this, "get_sdk_settings", null)), 0);
                if (custom == null) {
                    return "";
                }
                onUi(new Runnable() {
                    public void run() {
                        hue.setProgress(Math.round((float) custom.optDouble("hue", 0) * 360));
                        sat.setProgress(Math.round((float) custom.optDouble("saturation", 1) * 100));
                        update();
                    }
                });
                return custom.optBoolean("enabled", false) ? "Vector is using this custom color." : "";
            }
        });
    }
}
