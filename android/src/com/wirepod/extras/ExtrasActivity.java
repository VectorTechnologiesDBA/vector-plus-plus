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

        heading("Connection");
        open("Remote access", RemoteActivity.class);

        if (Wp.extrasEnabled(this)) {
            heading("Vector");
            open("Talk", TalkActivity.class);
            feature("Drive", "drive", DriveActivity.class);
            feature("SSH console", "ssh", SshActivity.class);
            feature("Quick actions", "quick", QuickActionsActivity.class);
            note("Each switch also shows or hides that feature's tile on the Home screen. Reopen the app after changing them.");
        }
    }

    /** A feature with its own on/off switch; when on, a row opens it. */
    private void feature(String label, final String key, Class<?> screen) {
        Switch sw = new Switch(this);
        sw.setText(label);
        sw.setTextColor(WHITE);
        sw.setTextSize(16);
        sw.setPadding(dp(4), dp(12), dp(4), dp(4));
        sw.setChecked(Wp.featureEnabled(this, key));
        sw.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            public void onCheckedChanged(CompoundButton b, boolean on) {
                Wp.setFeatureEnabled(ExtrasActivity.this, key, on);
                recreate();
            }
        });
        body.addView(sw);
        if (Wp.featureEnabled(this, key)) {
            open("Open " + label.toLowerCase(), screen);
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
