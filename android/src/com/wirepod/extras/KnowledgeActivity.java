package com.wirepod.extras;

import android.view.View;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.TextView;

import org.json.JSONObject;

/** Knowledge graph (question answering) setup for wire-pod. */
public class KnowledgeActivity extends Screen {
    private static final String[] NAMES = {"Off", "OpenAI", "Together AI", "Custom (OpenAI-compatible)", "Houndify"};
    private static final String[] IDS = {"", "openai", "together", "custom", "houndify"};

    private RadioGroup group;
    private EditText key, id, model, endpoint, robotName, prompt;
    private CheckBox intentGraph, saveChat, commands;
    private TextView status;
    private JSONObject current = new JSONObject();

    @Override
    protected String title() {
        return "Knowledge";
    }

    @Override
    protected void build() {
        heading("Provider");
        group = new RadioGroup(this);
        for (int i = 0; i < NAMES.length; i++) {
            RadioButton r = new RadioButton(this);
            r.setId(200 + i);
            r.setText(NAMES[i]);
            r.setTextColor(WHITE);
            group.addView(r);
        }
        group.check(200);
        body.addView(group);
        key = field("API key", "", false);
        id = field("Client ID (Houndify only)", "", false);
        model = field("Model", "", false);
        endpoint = field("Endpoint (Custom only)", "", false);
        robotName = field("Robot name", "Vector", false);
        prompt = field("Prompt", "", true);
        intentGraph = check("Answer when no command matches");
        saveChat = check("Remember conversation");
        commands = check("Allow actions from answers");
        button("Save", new View.OnClickListener() {
            public void onClick(View v) {
                save();
            }
        });
        status = status();
        load();
    }

    private CheckBox check(String label) {
        CheckBox c = new CheckBox(this);
        c.setText(label);
        c.setTextColor(WHITE);
        body.addView(c);
        return c;
    }

    private void load() {
        background(status, "Loading…", new Task() {
            public String run() throws Exception {
                final JSONObject j = Wp.getJson(KnowledgeActivity.this, "/api/get_kg_api");
                onUi(new Runnable() {
                    public void run() {
                        current = j;
                        String p = j.optBoolean("kgEnable", j.optBoolean("enable", false))
                                ? j.optString("kgProvider", j.optString("provider", "")) : "";
                        for (int i = 0; i < IDS.length; i++) {
                            if (IDS[i].equals(p)) {
                                group.check(200 + i);
                            }
                        }
                        key.setText(j.optString("kgApiKey", j.optString("key", "")));
                        id.setText(j.optString("kgApiID", j.optString("id", "")));
                        model.setText(j.optString("kgModel", j.optString("model", "")));
                        endpoint.setText(j.optString("kgEndpoint", j.optString("endpoint", "")));
                        robotName.setText(j.optString("kgRobotName", j.optString("robotName", "Vector")));
                        prompt.setText(j.optString("kgOpenAIPrompt", j.optString("openai_prompt", "")));
                        intentGraph.setChecked(j.optBoolean("kgIntentGraph", j.optBoolean("intentgraph", false)));
                        saveChat.setChecked(j.optBoolean("kgSaveChat", j.optBoolean("save_chat", false)));
                        commands.setChecked(j.optBoolean("kgCommandsEnable", j.optBoolean("commands_enable", false)));
                    }
                });
                return "Loaded from wire-pod.";
            }
        });
    }

    private static String s(EditText e) {
        return e.getText().toString().trim();
    }

    private void save() {
        int i = group.getCheckedRadioButtonId() - 200;
        final String provider = i < 0 || i >= IDS.length ? "" : IDS[i];
        try {
            final JSONObject b = new JSONObject();
            b.put("enable", provider.length() > 0);
            b.put("provider", provider);
            b.put("key", s(key));
            b.put("id", s(id));
            b.put("model", s(model));
            b.put("intentgraph", intentGraph.isChecked());
            b.put("robotName", s(robotName));
            b.put("openai_prompt", s(prompt));
            b.put("openai_voice", current.optString("kgOpenAIVoice", current.optString("openai_voice", "")));
            b.put("openai_voice_with_english", current.optBoolean("kgOpenAIVoiceWithEnglish", false));
            b.put("save_chat", saveChat.isChecked());
            b.put("commands_enable", commands.isChecked());
            b.put("endpoint", s(endpoint));
            b.put("top_p", current.optDouble("kgTopP", 1.0));
            b.put("temp", current.optDouble("kgTemperature", 0.7));
            background(status, "Saving…", new Task() {
                public String run() throws Exception {
                    Wp.postJson(KnowledgeActivity.this, "/api/set_kg_api", b);
                    return "Saved.";
                }
            });
        } catch (Exception e) {
            status.setText(e.toString());
        }
    }
}
