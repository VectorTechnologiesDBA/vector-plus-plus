package com.wirepod.extras;

import android.view.Gravity;
import android.view.View;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * Quick actions: one-tap commands for Vector, sent through wire-pod's /api-sdk/cloud_intent (the same intents its
 * web page's buttons and the matching voice commands use). Icons are the app's own Home icons.
 */
public class QuickActionsActivity extends Screen {
    private static final String[][] ACTIONS = {
            {"Start exploring", "nav_icon_exploration_sml", "explore_start", "Vector is off exploring."},
            {"Return to charger", "nav_icon_returntocharger_sml", "intent_system_charger", "Vector is heading home."},
            {"Find cube", "nav_icon_cube_sml", "intent_imperative_findcube", "Vector is looking for his cube."},
            {"Go to sleep", "nav_icon_sleep_sml", "intent_system_sleep", "Vector is going to sleep."},
    };

    private TextView status;

    @Override
    protected String title() {
        return "Quick actions";
    }

    @Override
    protected void build() {
        for (int i = 0; i < ACTIONS.length; i += 2) {
            LinearLayout row = new LinearLayout(this);
            LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(-1, -2);
            rp.topMargin = dp(12);
            body.addView(row, rp);
            for (int j = i; j < i + 2 && j < ACTIONS.length; j++) {
                LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(0, dp(170), 1f);
                if (j == i) {
                    cp.rightMargin = dp(6);
                } else {
                    cp.leftMargin = dp(6);
                }
                row.addView(card(ACTIONS[j]), cp);
            }
        }
        status = status();
    }

    private View card(final String[] action) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setGravity(Gravity.CENTER);
        card.setBackground(box(DARK_GREY, 0));
        card.setPadding(dp(8), dp(12), dp(8), dp(12));

        ImageView icon = new ImageView(this);
        int id = getResources().getIdentifier(action[1], "drawable", getPackageName());
        if (id != 0) {
            icon.setImageResource(id);
        }
        card.addView(icon, new LinearLayout.LayoutParams(dp(88), dp(88)));

        TextView label = text(action[0], 15, WHITE, false);
        label.setGravity(Gravity.CENTER);
        label.setPadding(0, dp(10), 0, 0);
        card.addView(label);

        card.setOnClickListener(new View.OnClickListener() {
            public void onClick(final View v) {
                v.setAlpha(0.6f);
                background(status, action[0] + "…", new Task() {
                    public String run() throws Exception {
                        try {
                            Wp.sdk(QuickActionsActivity.this, "cloud_intent", "intent=" + Wp.enc(action[2]));
                            return action[3];
                        } finally {
                            onUi(new Runnable() {
                                public void run() {
                                    v.setAlpha(1f);
                                }
                            });
                        }
                    }
                });
            }
        });
        return card;
    }
}
