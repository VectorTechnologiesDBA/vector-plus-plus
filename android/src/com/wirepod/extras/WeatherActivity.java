package com.wirepod.extras;

import android.view.View;
import android.widget.EditText;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.TextView;

import org.json.JSONObject;

/** Weather provider setup for wire-pod (replaces its web page). */
public class WeatherActivity extends Screen {
    private static final String[] NAMES = {"None", "OpenWeatherMap", "WeatherAPI"};
    private static final String[] IDS = {"", "openweathermap.org", "weatherapi.com"};

    private RadioGroup group;
    private EditText key;
    private TextView status;

    @Override
    protected String title() {
        return "Weather";
    }

    @Override
    protected void build() {
        heading("Provider");
        group = new RadioGroup(this);
        for (int i = 0; i < NAMES.length; i++) {
            RadioButton r = new RadioButton(this);
            r.setId(100 + i);
            r.setText(NAMES[i]);
            r.setTextColor(WHITE);
            group.addView(r);
        }
        group.check(100);
        body.addView(group);
        key = field("API key", "", false);
        button("Save", new View.OnClickListener() {
            public void onClick(View v) {
                save();
            }
        });
        status = status();
        load();
    }

    private int selected() {
        int i = group.getCheckedRadioButtonId() - 100;
        return i < 0 || i >= IDS.length ? 0 : i;
    }

    private void load() {
        background(status, "Loading…", new Task() {
            public String run() throws Exception {
                final JSONObject j = Wp.getJson(WeatherActivity.this, "/api/get_weather_api");
                onUi(new Runnable() {
                    public void run() {
                        String p = j.optString("weatherProvider", j.optString("provider", ""));
                        for (int i = 0; i < IDS.length; i++) {
                            if (IDS[i].equals(p)) {
                                group.check(100 + i);
                            }
                        }
                        key.setText(j.optString("weatherKey", j.optString("key", "")));
                    }
                });
                return "Loaded from wire-pod.";
            }
        });
    }

    private void save() {
        final String provider = IDS[selected()];
        final String k = key.getText().toString().trim();
        background(status, "Saving…", new Task() {
            public String run() throws Exception {
                JSONObject b = new JSONObject();
                b.put("provider", provider);
                b.put("key", k);
                Wp.postJson(WeatherActivity.this, "/api/set_weather_api", b);
                return "Saved.";
            }
        });
    }
}
