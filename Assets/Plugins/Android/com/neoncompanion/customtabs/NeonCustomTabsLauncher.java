package com.neoncompanion.customtabs;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.net.Uri;
import android.os.Bundle;
import java.util.ArrayList;
import java.util.List;
import com.unity3d.player.UnityPlayer;

/**
 * Opens a URL in a Custom Tab pinned to ONE browser package so the whole OAuth
 * redirect chain (native/authorize -> IdP -> /auth/callback -> loopback) stays in
 * a single cookie jar. Without package pinning Android can route different hops to
 * different browsers, dropping the gateway's SameSite=Lax "hermes_session_pkce"
 * cookie and yielding "Missing PKCE state cookie".
 *
 * Pure Android framework only (no androidx.browser) to match the project's plugin
 * setup and avoid a Gradle dependency. The Custom Tabs "session" extra with a null
 * binder is the standard marker that asks the browser to render as a Custom Tab;
 * we do not need warmup/prerender, only the single-jar guarantee from setPackage.
 */
public class NeonCustomTabsLauncher {

    // Custom Tabs protocol constants (framework-level string keys; no androidx needed).
    private static final String ACTION_CUSTOM_TABS_SERVICE =
        "android.support.customtabs.action.CustomTabsService";
    private static final String EXTRA_SESSION =
        "android.support.customtabs.extra.SESSION";
    private static final String EXTRA_TITLE_VISIBILITY =
        "android.support.customtabs.extra.TITLE_VISIBILITY";
    private static final int SHOW_PAGE_TITLE = 1;

    // Preferred Custom-Tabs-capable browsers, in order (Chrome first).
    private static final String[] PREFERRED = {
        "com.android.chrome",
        "com.chrome.beta",
        "com.chrome.dev",
        "com.chrome.canary",
        "com.brave.browser",
        "com.microsoft.emmx",
        "org.mozilla.firefox",
        "com.sec.android.app.sbrowser",
        "com.opera.browser"
    };

    /**
     * Launch {@code url} in a Custom Tab pinned to a single browser.
     * Returns the chosen package name, "" if none could be pinned (opened without
     * a package), or "ERROR:&lt;msg&gt;" on failure. Safe to call from any thread.
     */
    public static String open(final String url) {
        try {
            final Activity activity = UnityPlayer.currentActivity;
            if (activity == null) {
                return "ERROR:no-activity";
            }
            final Uri uri = Uri.parse(url);
            final String pkg = resolveCustomTabsPackage(activity);

            final Intent intent = new Intent(Intent.ACTION_VIEW, uri);
            if (pkg != null) {
                intent.setPackage(pkg);
            }
            // Present as a Custom Tab: SESSION extra must exist (null binder = no session).
            Bundle extras = new Bundle();
            extras.putBinder(EXTRA_SESSION, null);
            intent.putExtras(extras);
            intent.putExtra(EXTRA_TITLE_VISIBILITY, SHOW_PAGE_TITLE);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);

            activity.runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    try {
                        activity.startActivity(intent);
                    } catch (Exception primary) {
                        // Last resort: plain VIEW without a package pin.
                        try {
                            Intent fallback = new Intent(Intent.ACTION_VIEW, uri);
                            fallback.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                            activity.startActivity(fallback);
                        } catch (Exception ignored) {
                        }
                    }
                }
            });

            return pkg != null ? pkg : "";
        } catch (Exception e) {
            return "ERROR:" + e.getMessage();
        }
    }

    /**
     * Pick a single browser to own the whole chain: a Custom-Tabs-capable browser
     * (Chrome preferred), else any CT browser, else the system default browser.
     * Requires the &lt;queries&gt; block in the manifest for Android 11+ visibility.
     */
    private static String resolveCustomTabsPackage(Activity activity) {
        PackageManager pm = activity.getPackageManager();

        Intent viewIntent = new Intent(Intent.ACTION_VIEW, Uri.parse("https://www.example.com"));
        List<ResolveInfo> viewers = pm.queryIntentActivities(viewIntent, 0);

        List<String> ctPkgs = new ArrayList<String>();
        for (int i = 0; i < viewers.size(); i++) {
            ResolveInfo ri = viewers.get(i);
            if (ri.activityInfo == null || ri.activityInfo.packageName == null) {
                continue;
            }
            String p = ri.activityInfo.packageName;
            Intent svc = new Intent(ACTION_CUSTOM_TABS_SERVICE);
            svc.setPackage(p);
            if (pm.resolveService(svc, 0) != null && !ctPkgs.contains(p)) {
                ctPkgs.add(p);
            }
        }

        // Prefer a known-good Custom Tabs browser (Chrome first).
        for (int i = 0; i < PREFERRED.length; i++) {
            if (ctPkgs.contains(PREFERRED[i])) {
                return PREFERRED[i];
            }
        }
        // Otherwise any Custom-Tabs-capable browser.
        if (!ctPkgs.isEmpty()) {
            return ctPkgs.get(0);
        }
        // Otherwise the default browser (no CT) — still pins ONE app for single-jar.
        ResolveInfo def = pm.resolveActivity(viewIntent, PackageManager.MATCH_DEFAULT_ONLY);
        if (def != null && def.activityInfo != null && def.activityInfo.packageName != null
            && !"android".equals(def.activityInfo.packageName)) {
            return def.activityInfo.packageName;
        }
        return null;
    }
}
