package com.wirepod.extras;

import android.content.Intent;
import android.view.View;
import android.widget.CompoundButton;
import android.widget.Switch;

/** "Extra features" menu: the on/off switch plus entries for each wire-pod screen. */
public class ExtrasActivity extends Screen {
    @Override
    protected String title() {
        return "Extra features";
    }

    @Override
    protected void build() {
        Switch sw = new Switch(this);
        sw.setText("Enable extra features");
        sw.setTextColor(WHITE);
        sw.setChecked(Wp.extrasEnabled(this));
        sw.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            public void onCheckedChanged(CompoundButton b, boolean on) {
                Wp.setExtrasEnabled(ExtrasActivity.this, on);
                recreate();
            }
        });
        body.addView(sw);
        note("Server: " + Wp.server(this));

        heading("wire-pod");
        open("Weather", WeatherActivity.class);
        open("Knowledge", KnowledgeActivity.class);

        if (Wp.extrasEnabled(this)) {
            heading("Vector");
            open("Talk", TalkActivity.class);
            open("Drive", DriveActivity.class);
            open("SSH console", SshActivity.class);
        }
    }

    private void open(String label, final Class<?> screen) {
        row(label, new View.OnClickListener() {
            public void onClick(View v) {
                startActivity(new Intent(ExtrasActivity.this, screen));
            }
        });
    }
}
