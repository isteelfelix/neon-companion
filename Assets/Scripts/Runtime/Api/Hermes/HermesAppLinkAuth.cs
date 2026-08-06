// HermesAppLinkAuth.cs — Android App Link OAuth flow for Companion.
//
// Implements the App Link / Custom Tab auth strategy designed 2026-08-06:
//
//   1. App calls GET {base}/auth/login?provider=N via HttpWebRequest (no redirect
//      following). Captures pre-auth Set-Cookie (PKCE cookie with state + verifier)
//      and the 302 Location (IdP authorize URL).
//   2. Opens the IdP URL in a system browser Custom Tab. For Google/GitHub this
//      is a real browser → OAuth is not blocked.
//   3. User authenticates. IdP redirects to {base}/auth/callback?code=…&state=…
//   4. Android intercepts the redirect via the intent-filter (App Link on the
//      gateway domain) and delivers it to the app via AndroidDeepLinkReceiver.
//   5. App replays GET /auth/callback?code&state via HttpWebRequest, carrying
//      the PKCE cookie from step 1. Gateway validates state, mints session,
//      responds with Set-Cookie: hermes_session_at=…
//   6. Read Set-Cookie from the response → HermesRemoteAuth.SetSessionCookie → done.
//
// Uses System.Net.HttpWebRequest (not UnityWebRequest) for steps 1 and 5 because
// HttpWebRequest.AllowAutoRedirect=false reliably exposes 302 headers, while
// UnityWebRequest.redirectLimit=0 does not. This mirrors the existing
// CaptureViaNativeHandoffAsync code (HermesBrowserOAuthLogin.cs:822+).
//
// Zero server-side changes. Only needs /.well-known/assetlinks.json on the gateway
// for verified App Link (silent interception without disambiguation dialog).
//
// C# 9 (Unity 6) compatible.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>Result of an App Link OAuth attempt.</summary>
    public class HermesAppLinkResult
    {
        public bool Ok;
        public string CookieHeader;
        public string Error;
    }

    /// <summary>
    /// Android-only OAuth flow using system browser (Custom Tab) + App Link callback
    /// interception. The app never embeds a browser and never reads a foreign cookie
    /// jar — it replays the gateway callback via its own HttpWebRequest.
    /// </summary>
    public static class HermesAppLinkAuth
    {
        private const int HttpTimeoutStep1 = 10;
        private const int HttpTimeoutStep5 = 15;
        private const int DeepLinkWaitMs = 180000; // 3 min for user to complete IdP login

        /// <summary>
        /// Run the full App Link OAuth flow. Android-only.
        /// Returns a session cookie header on success, or an error.
        /// </summary>
        public static async Task<HermesAppLinkResult> LoginAsync(
            string rawBaseUrl,
            string providerName = "")
        {
            HermesAppLinkResult result = new HermesAppLinkResult();

#if !UNITY_ANDROID || UNITY_EDITOR
            result.Error = "App Link auth is only available on Android.";
            return result;
#else
            string baseUrl = HermesRemoteAuth.NormalizeBaseUrl(rawBaseUrl);
            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Error = "Gateway URL is empty.";
                return result;
            }

            // If no provider given, probe /api/auth/providers for the first one.
            if (string.IsNullOrEmpty(providerName))
            {
                string firstProvider = await ProbeFirstProviderNameAsync(baseUrl);
                if (string.IsNullOrEmpty(firstProvider))
                {
                    result.Error = "No auth providers found on the gateway.";
                    return result;
                }
                providerName = firstProvider;
            }

            string loginUrl = baseUrl + "/auth/login?provider=" + Uri.EscapeDataString(providerName);

            // --- Step 1: GET /auth/login (no redirect) → capture PKCE cookie + IdP URL ---
            HttpCapture step1;
            try
            {
                step1 = await HttpGetNoRedirectAsync(loginUrl, HttpTimeoutStep1);
            }
            catch (Exception ex)
            {
                result.Error = "Failed to start login: " + ex.Message;
                return result;
            }

            if (string.IsNullOrEmpty(step1.Location))
            {
                result.Error = "Login response did not redirect to an identity provider.";
                return result;
            }

            // Build the Cookie header from ALL Set-Cookie values (there may be several).
            string pkceCookieHeader = BuildCookieHeader(step1.SetCookies);
            if (string.IsNullOrEmpty(pkceCookieHeader))
            {
                result.Error = "Login response did not include a PKCE cookie.";
                return result;
            }

            string idpUrl = step1.Location;

            // --- Step 2: Open IdP URL in system browser Custom Tab ---
            AndroidDeepLinkReceiver receiver = AndroidDeepLinkReceiver.Instance;
            if (receiver == null)
            {
                result.Error = "Deep link receiver unavailable.";
                return result;
            }

            // Clear any stale deep link.
            receiver.TryConsumeDeepLink();

            Debug.Log("[NeonCompanion] App Link OAuth: opening IdP URL in Custom Tab: " + idpUrl);
            OpenCustomTab(idpUrl);

            // --- Steps 3-4: Wait for App Link callback ---
            string callbackUrl = await WaitForDeepLinkAsync(receiver, DeepLinkWaitMs);
            if (string.IsNullOrEmpty(callbackUrl))
            {
                result.Error = "Timed out waiting for login callback.";
                return result;
            }

            Debug.Log("[NeonCompanion] App Link OAuth: callback intercepted: " + callbackUrl);

            // --- Step 5: Replay GET /auth/callback with PKCE cookie ---
            string replayUrl = BuildReplayUrl(callbackUrl, baseUrl);

            HttpCapture step5;
            try
            {
                step5 = await HttpGetWithCookieAsync(replayUrl, pkceCookieHeader, HttpTimeoutStep5, followRedirect: true);
            }
            catch (Exception ex)
            {
                result.Error = "Callback replay failed: " + ex.Message;
                return result;
            }

            // --- Step 6: Extract session cookies ---
            string sessionCookie = HermesRemoteAuth.ExtractSessionCookies(BuildCookieHeader(step5.SetCookies));
            if (string.IsNullOrEmpty(sessionCookie) && step5.SetCookies.Count > 0)
            {
                // Try raw concatenation as fallback.
                sessionCookie = HermesRemoteAuth.ExtractSessionCookies(string.Join("; ", step5.SetCookies));
            }

            if (string.IsNullOrEmpty(sessionCookie))
            {
                result.Error = "Callback succeeded but no session cookie was returned.";
                return result;
            }

            result.Ok = true;
            result.CookieHeader = sessionCookie;
            return result;
#endif
        }

        // ------------------------------------------------------------------
        // HTTP helpers (HttpWebRequest for reliable 302 handling)
        // ------------------------------------------------------------------

        /// <summary>Captured HTTP response: redirect target + all Set-Cookie values.</summary>
        private struct HttpCapture
        {
            public int StatusCode;
            public string Location;
            public List<string> SetCookies;
        }

        private static async Task<HttpCapture> HttpGetNoRedirectAsync(string url, int timeoutSec)
        {
            return await HttpGetAsync(url, null, timeoutSec, followRedirect: false);
        }

        private static async Task<HttpCapture> HttpGetWithCookieAsync(
            string url, string cookieHeader, int timeoutSec, bool followRedirect)
        {
            return await HttpGetAsync(url, cookieHeader, timeoutSec, followRedirect);
        }

        private static async Task<HttpCapture> HttpGetAsync(
            string url, string cookieHeader, int timeoutSec, bool followRedirect)
        {
            HttpCapture capture = new HttpCapture();
            capture.SetCookies = new List<string>();

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = followRedirect;
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;

            if (!string.IsNullOrEmpty(cookieHeader))
                req.Headers["Cookie"] = cookieHeader;

            // We need to capture Set-Cookie from both the redirect response and the final.
            // With AllowAutoRedirect=false, we get the 302 directly.
            HttpWebResponse resp;
            try
            {
                WebResponse wr = await Task.Factory.FromAsync(
                    req.BeginGetResponse, req.EndGetResponse, null);
                resp = (HttpWebResponse)wr;
            }
            catch (WebException wex)
            {
                // 302 with AllowAutoRedirect=false raises a WebException.
                resp = wex.Response as HttpWebResponse;
                if (resp == null)
                    throw;
            }

            capture.StatusCode = (int)resp.StatusCode;

            // Location header
            capture.Location = resp.Headers["Location"];

            // Set-Cookie: HttpWebResponse.Headers["Set-Cookie"] returns ALL values
            // joined by ", " in .NET — same UnityWebRequest issue. But we can iterate
            // resp.Cookies for properly separated values.
            if (resp.Cookies != null && resp.Cookies.Count > 0)
            {
                for (int i = 0; i < resp.Cookies.Count; i++)
                {
                    Cookie c = resp.Cookies[i];
                    capture.SetCookies.Add(c.Name + "=" + c.Value);
                }
            }
            else
            {
                // Fallback: parse from raw header.
                string raw = resp.Headers["Set-Cookie"];
                if (!string.IsNullOrEmpty(raw))
                {
                    string[] parts = raw.Split(new[] { ", " }, StringSplitOptions.None);
                    for (int i = 0; i < parts.Length; i++)
                        capture.SetCookies.Add(parts[i]);
                }
            }

            resp.Close();
            return capture;
        }

        // ------------------------------------------------------------------
        // Deep link waiting
        // ------------------------------------------------------------------

        private static async Task<string> WaitForDeepLinkAsync(
            AndroidDeepLinkReceiver receiver, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string url = receiver.TryConsumeDeepLink();
                if (!string.IsNullOrEmpty(url))
                {
                    if (url.IndexOf("/auth/callback", StringComparison.OrdinalIgnoreCase) >= 0)
                        return url;
                    Debug.LogWarning("[NeonCompanion] Ignoring non-auth deep link: " + url);
                }

                await Task.Delay(200);
                await Task.Yield();
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Cookie helpers
        // ------------------------------------------------------------------

        /// <summary>Build a Cookie header from a list of "name=value" strings.</summary>
        private static string BuildCookieHeader(List<string> cookies)
        {
            if (cookies == null || cookies.Count == 0)
                return null;

            // Extract just the name=value part (strip attributes after ';').
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cookies.Count; i++)
            {
                string c = cookies[i];
                int semi = c.IndexOf(';');
                if (semi >= 0)
                    c = c.Substring(0, semi);

                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(c.Trim());
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // ------------------------------------------------------------------
        // URL helpers
        // ------------------------------------------------------------------

        private static string BuildReplayUrl(string callbackUrl, string baseUrl)
        {
            try
            {
                Uri cb = new Uri(callbackUrl);
                Uri baseUri = new Uri(baseUrl);

                if (string.Equals(cb.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                    return callbackUrl;

                return baseUrl + cb.AbsolutePath + cb.Query;
            }
            catch
            {
                return callbackUrl;
            }
        }

        // ------------------------------------------------------------------
        // Provider probe
        // ------------------------------------------------------------------

        private static async Task<string> ProbeFirstProviderNameAsync(string baseUrl)
        {
            try
            {
                string url = baseUrl + "/api/auth/providers";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = HttpTimeoutStep1 * 1000;

                WebResponse wr = await Task.Factory.FromAsync(
                    req.BeginGetResponse, req.EndGetResponse, null);
                using (Stream s = wr.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    string json = await r.ReadToEndAsync();
                    wr.Close();

                    JObject parsed = JObject.Parse(json);
                    JArray providers = parsed["providers"] as JArray;
                    if (providers == null || providers.Count == 0)
                        return null;

                    return providers[0].Value<string>("name");
                }
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Custom Tab launcher
        // ------------------------------------------------------------------

        /// <summary>Open a URL in a Chrome Custom Tab (or system browser as fallback).</summary>
        private static void OpenCustomTab(string url)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                // Try Chrome Custom Tab via androidx.browser.
                bool opened = false;
                try
                {
                    // Build a CustomTabsIntent
                    var builder = new AndroidJavaObject("androidx.browser.customtabs.CustomTabsIntent$Builder");
                    var customTabsIntent = builder.Call<AndroidJavaObject>("build");
                    var intent = customTabsIntent.Get<AndroidJavaObject>("intent");
                    var uri = new AndroidJavaObject("android.net.Uri", url);
                    intent.Call<AndroidJavaObject>("setData", uri);
                    activity.Call("startActivity", intent);
                    opened = true;
                }
                catch
                {
                    // androidx.browser not in classpath — fall through.
                }

                if (!opened)
                {
                    // Fallback: ACTION_VIEW intent (opens system browser).
                    var intentClass = new AndroidJavaClass("android.content.Intent");
                    var intent = new AndroidJavaObject("android.content.Intent",
                        intentClass.GetStatic<string>("ACTION_VIEW"));
                    var uri = new AndroidJavaObject("android.net.Uri", url);
                    intent.Call<AndroidJavaObject>("setData", uri);
                    activity.Call("startActivity", intent);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[NeonCompanion] Failed to open Custom Tab: " + ex.Message);
                Application.OpenURL(url);
            }
#endif
        }
    }
}
