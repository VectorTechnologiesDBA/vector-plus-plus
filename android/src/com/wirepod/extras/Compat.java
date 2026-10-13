package com.wirepod.extras;

import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Bundle;
import android.os.Handler;

/**
 * Stand-ins for PendingIntent's factory methods. The build rewrites every call in the app's original dex files
 * (Appboy, Firebase, support library) to these. Android 12+ (targetSdk 31+) throws unless a PendingIntent says
 * whether it's mutable; those libraries predate that, so default to FLAG_IMMUTABLE when neither flag is given.
 */
public final class Compat {
    private static final int FLAG_IMMUTABLE = 0x04000000;
    private static final int FLAG_MUTABLE = 0x02000000;

    private Compat() {
    }

    static int flags(int flags) {
        return (flags & (FLAG_IMMUTABLE | FLAG_MUTABLE)) != 0 ? flags : flags | FLAG_IMMUTABLE;
    }

    public static PendingIntent getActivity(Context c, int code, Intent i, int flags) {
        return PendingIntent.getActivity(c, code, i, flags(flags));
    }

    public static PendingIntent getActivity(Context c, int code, Intent i, int flags, Bundle options) {
        return PendingIntent.getActivity(c, code, i, flags(flags), options);
    }

    public static PendingIntent getActivities(Context c, int code, Intent[] i, int flags) {
        return PendingIntent.getActivities(c, code, i, flags(flags));
    }

    public static PendingIntent getActivities(Context c, int code, Intent[] i, int flags, Bundle options) {
        return PendingIntent.getActivities(c, code, i, flags(flags), options);
    }

    public static PendingIntent getBroadcast(Context c, int code, Intent i, int flags) {
        return PendingIntent.getBroadcast(c, code, i, flags(flags));
    }

    public static PendingIntent getService(Context c, int code, Intent i, int flags) {
        return PendingIntent.getService(c, code, i, flags(flags));
    }

    // Android 14+ (targetSdk 34+) throws if a runtime-registered receiver doesn't say whether other apps may send
    // to it. Before 14 every such receiver was exported, so keep that (RECEIVER_EXPORTED). The flag overloads are
    // API 26+, called by reflection because the API stubs this is compiled against are older.
    private static final int RECEIVER_EXPORTED = 0x2;

    public static Intent registerReceiver(Context c, BroadcastReceiver r, IntentFilter f) {
        if (android.os.Build.VERSION.SDK_INT < 26) {
            return c.registerReceiver(r, f);
        }
        try {
            return (Intent) Context.class.getMethod("registerReceiver", BroadcastReceiver.class, IntentFilter.class, int.class)
                    .invoke(c, r, f, RECEIVER_EXPORTED);
        } catch (Exception e) {
            throw unwrap(e);
        }
    }

    public static Intent registerReceiver(Context c, BroadcastReceiver r, IntentFilter f, String permission, Handler h) {
        if (android.os.Build.VERSION.SDK_INT < 26) {
            return c.registerReceiver(r, f, permission, h);
        }
        try {
            return (Intent) Context.class.getMethod("registerReceiver", BroadcastReceiver.class, IntentFilter.class,
                    String.class, Handler.class, int.class).invoke(c, r, f, permission, h, RECEIVER_EXPORTED);
        } catch (Exception e) {
            throw unwrap(e);
        }
    }

    private static RuntimeException unwrap(Exception e) {
        Throwable t = e instanceof java.lang.reflect.InvocationTargetException ? e.getCause() : e;
        return t instanceof RuntimeException ? (RuntimeException) t : new RuntimeException(t);
    }
}
