package com.wirepod.extras;

import android.view.View;
import android.widget.EditText;
import android.widget.TextView;

/** Makes Vector say a line of text through wire-pod's SDK API. */
public class TalkActivity extends Screen {
    private EditText line;
    private TextView status;

    @Override
    protected String title() {
        return "Talk";
    }

    @Override
    protected void build() {
        note("Type something and Vector will say it out loud.");
        line = field("Text", "", true);
        button("Say it", new View.OnClickListener() {
            public void onClick(View v) {
                final String t = line.getText().toString().trim();
                if (t.length() == 0) {
                    return;
                }
                background(status, "Sending…", new Task() {
                    public String run() throws Exception {
                        Wp.sdk(TalkActivity.this, "assume_behavior_control", "priority=high");
                        try {
                            Wp.sdk(TalkActivity.this, "say_text", "text=" + Wp.enc(t));
                        } finally {
                            Wp.sdk(TalkActivity.this, "release_behavior_control", null);
                        }
                        return "Said it.";
                    }
                });
            }
        });
        status = status();
    }
}
