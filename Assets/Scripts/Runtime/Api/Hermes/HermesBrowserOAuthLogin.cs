// HermesBrowserOAuthLogin.cs - Desktop-equivalent OAuth session capture for Companion.
//
// Desktop (apps/desktop/electron/main.ts openOauthLoginWindow) loads {base}/login in an
// Electron BrowserWindow bound to session partition persist:hermes-remote-oauth, then
// polls session.cookies until hermes_session_at appears after /auth/callback.
//
// Unity has no Electron cookie jar. This class is the Companion equivalent:
//   1. Launch a dedicated Chromium/Edge profile with --app={base}/login and
//      --remote-debugging-port (isolated user-data-dir = partition).
//   2. Connect to Chrome DevTools Protocol (same machine loopback).
//   3. Poll Network.getAllCookies until Hermes session cookies appear for the
//      gateway host (HttpOnly cookies are visible to CDP, as they are to Electron).
//   4. Return a Cookie header string for HermesRemoteAuth.SetSessionCookie.
//
// Optional secondary path: local loopback handoff if the gateway exposes the
// Companion native contract (GET /auth/native/handoff + POST /api/auth/native/redeem).
// That contract is not in stock Hermes today; CDP capture is the primary path.
//
// C# 9 (Unity 6) compatible. No password/cookie paste required for the primary path.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>
    /// Result of an automatic browser OAuth attempt.
    /// </summary>
    public class HermesBrowserOAuthResult
    {
        public bool Ok;
        public string CookieHeader;
        public string Error;
        public string Method; // "cdp" | "native_handoff" | null
    }

    /// <summary>
    /// Captures a Hermes dashboard session cookie after the user completes browser login,
    /// mirroring Desktop openOauthLoginWindow + cookie-jar poll.
    /// </summary>
    public static class HermesBrowserOAuthLogin
    {
        private const int DefaultTimeoutSeconds = 180;
        private const int CdpPollMs = 750;
        private const int BrowserStartGraceMs = 2500;

        /// <summary>
        /// Open gateway login in a dedicated Chromium/Edge window and wait until session
        /// cookies appear (or timeout / window closed). Returns cookie header or error.
        /// </summary>
        public static async Task<HermesBrowserOAuthResult> CaptureSessionAsync(
            string rawBaseUrl,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            HermesBrowserOAuthResult result = new HermesBrowserOAuthResult();
            string baseUrl = HermesRemoteAuth.NormalizeBaseUrl(rawBaseUrl);
            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Error = "Gateway URL is empty.";
                return result;
            }

            string loginUrl = HermesRemoteAuth.BuildLoginUrl(baseUrl);
            string host;
            try
            {
                Uri uri = new Uri(baseUrl);
                host = uri.Host;
            }
            catch (Exception)
            {
                result.Error = "Invalid Gateway URL.";
                return result;
            }

            // Prefer CDP capture (Desktop parity). Fall back to native handoff if the
            // gateway implements it and no local Chromium browser is available.
            HermesBrowserOAuthResult cdp = await CaptureViaChromiumCdpAsync(loginUrl, host, timeoutSeconds);
            if (cdp.Ok)
                return cdp;

            HermesBrowserOAuthResult handoff = await CaptureViaNativeHandoffAsync(baseUrl, loginUrl, timeoutSeconds);
            if (handoff.Ok)
                return handoff;

            // Prefer the more specific CDP error when both failed.
            result.Error = !string.IsNullOrEmpty(cdp.Error) ? cdp.Error : handoff.Error;
            if (string.IsNullOrEmpty(result.Error))
                result.Error = "Browser sign-in did not complete.";
            return result;
        }

        // ------------------------------------------------------------------
        // Chromium / Edge CDP path (primary)
        // ------------------------------------------------------------------

        private static async Task<HermesBrowserOAuthResult> CaptureViaChromiumCdpAsync(
            string loginUrl,
            string gatewayHost,
            int timeoutSeconds)
        {
            HermesBrowserOAuthResult result = new HermesBrowserOAuthResult();
            result.Method = "cdp";

            string browserPath = FindChromiumBrowserPath();
            if (string.IsNullOrEmpty(browserPath))
            {
                result.Error =
                    "No Chromium/Edge browser found for automatic sign-in. Install Microsoft Edge or Google Chrome, or use a password-capable gateway.";
                return result;
            }

            int debugPort = GetFreeLoopbackPort();
            if (debugPort <= 0)
            {
                result.Error = "Could not allocate a local port for the sign-in browser.";
                return result;
            }

            string userDataDir = Path.Combine(Path.GetTempPath(), "neon-companion-hermes-oauth-" + Guid.NewGuid().ToString("N"));
            Process process = null;
            ClientWebSocket ws = null;

            try
            {
                Directory.CreateDirectory(userDataDir);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = browserPath;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false;
                // Dedicated profile = Electron partition. Remote debugging exposes HttpOnly cookies.
                psi.Arguments =
                    "--user-data-dir=\"" + userDataDir + "\" "
                    + "--remote-debugging-port=" + debugPort + " "
                    + "--remote-allow-origins=* "
                    + "--no-first-run --no-default-browser-check --disable-extensions "
                    + "--disable-sync --disable-background-networking "
                    + "--app=\"" + loginUrl + "\"";

                process = Process.Start(psi);
                if (process == null)
                {
                    result.Error = "Failed to start the sign-in browser.";
                    return result;
                }

                // Wait for CDP HTTP endpoint.
                string debuggerUrl = null;
                DateTime start = DateTime.UtcNow;
                DateTime deadline = start.AddSeconds(timeoutSeconds);

                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                    {
                        result.Error = "Sign-in browser closed before authentication completed.";
                        return result;
                    }

                    debuggerUrl = await TryGetBrowserWebSocketDebuggerUrlAsync(debugPort);
                    if (!string.IsNullOrEmpty(debuggerUrl))
                        break;

                    await Task.Delay(200);
                    await Task.Yield();
                }

                if (string.IsNullOrEmpty(debuggerUrl))
                {
                    result.Error = "Sign-in browser did not expose DevTools (CDP).";
                    return result;
                }

                // Brief grace so the first navigation can start.
                int graceLeft = BrowserStartGraceMs;
                while (graceLeft > 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                    graceLeft -= 100;
                    await Task.Yield();
                }

                ws = new ClientWebSocket();
                using (CancellationTokenSource connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await ws.ConnectAsync(new Uri(debuggerUrl), connectCts.Token);
                }

                int nextId = 1;
                await CdpSendAsync(ws, nextId++, "Network.enable", null);

                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                    {
                        // One last cookie read after close — cookies may already be written.
                        string last = await TryReadSessionCookiesViaCdpAsync(ws, ref nextId, gatewayHost);
                        if (!string.IsNullOrEmpty(last))
                        {
                            result.Ok = true;
                            result.CookieHeader = last;
                            result.Error = null;
                            return result;
                        }

                        result.Error = "Sign-in browser closed before authentication completed.";
                        return result;
                    }

                    string cookieHeader = await TryReadSessionCookiesViaCdpAsync(ws, ref nextId, gatewayHost);
                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                        result.Ok = true;
                        result.CookieHeader = cookieHeader;
                        result.Error = null;
                        return result;
                    }

                    await Task.Delay(CdpPollMs);
                    await Task.Yield();
                }

                result.Error = "Timed out waiting for Hermes sign-in in the browser.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Browser sign-in failed: " + ex.Message;
                return result;
            }
            finally
            {
                try
                {
                    if (ws != null)
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            using (CancellationTokenSource cts = new CancellationTokenSource(1000))
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
                            }
                        }
                        ws.Dispose();
                    }
                }
                catch
                {
                    // best effort
                }

                try
                {
                    if (process != null && !process.HasExited)
                        process.Kill();
                }
                catch
                {
                    // best effort
                }

                try
                {
                    if (process != null)
                        process.Dispose();
                }
                catch
                {
                }

                // Best-effort cleanup of the temp profile (may fail if browser is slow to exit).
                try
                {
                    if (Directory.Exists(userDataDir))
                        Directory.Delete(userDataDir, true);
                }
                catch
                {
                    // leave temp dir; OS will clean eventually
                }
            }
        }

        private static async Task<string> TryReadSessionCookiesViaCdpAsync(
            ClientWebSocket ws,
            ref int nextId,
            string gatewayHost)
        {
            if (ws == null || ws.State != WebSocketState.Open)
                return null;

            int id = nextId++;
            JObject response = await CdpSendAsync(ws, id, "Network.getAllCookies", null);
            if (response == null)
                return null;

            JToken resultToken = response["result"];
            if (resultToken == null)
                return null;
            JArray cookies = resultToken["cookies"] as JArray;
            if (cookies == null || cookies.Count == 0)
                return null;

            // Build a synthetic Set-Cookie / Cookie string from matching cookies, then
            // reuse HermesRemoteAuth.ExtractSessionCookies for name filtering.
            StringBuilder raw = new StringBuilder();
            for (int i = 0; i < cookies.Count; i++)
            {
                JToken c = cookies[i];
                if (c == null)
                    continue;
                string name = c.Value<string>("name");
                string value = c.Value<string>("value");
                string domain = c.Value<string>("domain");
                if (string.IsNullOrEmpty(name) || value == null)
                    continue;
                if (!CookieDomainMatchesHost(domain, gatewayHost))
                    continue;

                if (raw.Length > 0)
                    raw.Append("; ");
                raw.Append(name);
                raw.Append('=');
                raw.Append(value);
            }

            if (raw.Length == 0)
                return null;

            return HermesRemoteAuth.ExtractSessionCookies(raw.ToString());
        }

        /// <summary>
        /// Domain match mirroring browser cookie scoping: exact host, or leading-dot parent.
        /// </summary>
        public static bool CookieDomainMatchesHost(string cookieDomain, string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            if (string.IsNullOrEmpty(cookieDomain))
                return true; // host-only / missing — keep and let name filter decide

            string d = cookieDomain.Trim().ToLowerInvariant();
            string h = host.Trim().ToLowerInvariant();
            if (d.StartsWith(".", StringComparison.Ordinal))
                d = d.Substring(1);

            if (string.Equals(d, h, StringComparison.Ordinal))
                return true;
            // parent domain match: cookie domain example.com matches host api.example.com
            if (h.EndsWith("." + d, StringComparison.Ordinal))
                return true;
            return false;
        }

        private static async Task<string> TryGetBrowserWebSocketDebuggerUrlAsync(int debugPort)
        {
            string url = "http://127.0.0.1:" + debugPort + "/json/version";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 1500;
                req.ReadWriteTimeout = 1500;
                using (WebResponse resp = await req.GetResponseAsync())
                using (Stream stream = resp.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string body = await reader.ReadToEndAsync();
                    if (string.IsNullOrEmpty(body))
                        return null;
                    JObject obj = JObject.Parse(body);
                    string wsUrl = obj.Value<string>("webSocketDebuggerUrl");
                    return string.IsNullOrEmpty(wsUrl) ? null : wsUrl;
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task<JObject> CdpSendAsync(
            ClientWebSocket ws,
            int id,
            string method,
            JObject parameters)
        {
            JObject msg = new JObject();
            msg["id"] = id;
            msg["method"] = method;
            if (parameters != null)
                msg["params"] = parameters;

            byte[] bytes = Encoding.UTF8.GetBytes(msg.ToString(Formatting.None));
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);

            // Read until we see our id (CDP may push events interleaved).
            DateTime readDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < readDeadline && ws.State == WebSocketState.Open)
            {
                string text = await CdpReceiveTextAsync(ws, 5000);
                if (string.IsNullOrEmpty(text))
                    continue;

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(text);
                }
                catch
                {
                    continue;
                }

                JToken idToken = parsed["id"];
                if (idToken != null && idToken.Type == JTokenType.Integer && idToken.Value<int>() == id)
                    return parsed;
                // else event — ignore and keep reading
            }

            return null;
        }

        private static async Task<string> CdpReceiveTextAsync(ClientWebSocket ws, int timeoutMs)
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(timeoutMs))
            {
                byte[] buffer = new byte[65536];
                using (MemoryStream ms = new MemoryStream())
                {
                    while (true)
                    {
                        ArraySegment<byte> segment = new ArraySegment<byte>(buffer);
                        WebSocketReceiveResult recv;
                        try
                        {
                            recv = await ws.ReceiveAsync(segment, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return null;
                        }
                        catch (WebSocketException)
                        {
                            return null;
                        }

                        if (recv.MessageType == WebSocketMessageType.Close)
                            return null;

                        ms.Write(buffer, 0, recv.Count);
                        if (recv.EndOfMessage)
                            break;
                    }

                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        /// <summary>
        /// Locate a Chromium-based browser binary (Edge preferred, then Chrome).
        /// Pure helper — unit-verifiable without launching a process.
        /// </summary>
        public static string FindChromiumBrowserPath()
        {
            List<string> candidates = new List<string>();

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(pf, "Chromium", "Application", "chrome.exe"));
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
#else
            // Linux / other
            candidates.Add("/usr/bin/microsoft-edge");
            candidates.Add("/usr/bin/microsoft-edge-stable");
            candidates.Add("/usr/bin/google-chrome");
            candidates.Add("/usr/bin/google-chrome-stable");
            candidates.Add("/usr/bin/chromium");
            candidates.Add("/usr/bin/chromium-browser");
            candidates.Add("/snap/bin/chromium");
#endif

            // PATH lookup for chrome/msedge/chromium
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            char sep = Path.PathSeparator;
            string[] pathParts = pathEnv.Split(sep);
            string[] names =
            {
                "msedge", "msedge.exe",
                "microsoft-edge", "microsoft-edge-stable",
                "google-chrome", "google-chrome-stable", "chrome", "chrome.exe",
                "chromium", "chromium-browser"
            };
            for (int i = 0; i < pathParts.Length; i++)
            {
                string dir = pathParts[i];
                if (string.IsNullOrEmpty(dir))
                    continue;
                for (int n = 0; n < names.Length; n++)
                {
                    candidates.Add(Path.Combine(dir, names[n]));
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string p = candidates[i];
                try
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        return p;
                }
                catch
                {
                    // ignore invalid paths
                }
            }

            return null;
        }

        public static int GetFreeLoopbackPort()
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch
            {
                return -1;
            }
        }

        // ------------------------------------------------------------------
        // Optional native handoff (Hermes contract extension)
        // ------------------------------------------------------------------

        /// <summary>
        /// If the gateway implements Companion native handoff, complete login via loopback:
        /// open /auth/native/handoff?redirect_uri=http://127.0.0.1:port/callback then redeem code.
        /// Stock Hermes does not ship these routes; this is forward-compatible.
        /// </summary>
        private static async Task<HermesBrowserOAuthResult> CaptureViaNativeHandoffAsync(
            string baseUrl,
            string loginUrl,
            int timeoutSeconds)
        {
            HermesBrowserOAuthResult result = new HermesBrowserOAuthResult();
            result.Method = "native_handoff";

            int port = GetFreeLoopbackPort();
            if (port <= 0)
            {
                result.Error = "Could not allocate loopback port for OAuth handoff.";
                return result;
            }

            string redirectUri = "http://127.0.0.1:" + port + "/callback";
            string handoffUrl = baseUrl + "/auth/native/handoff?redirect_uri="
                + Uri.EscapeDataString(redirectUri);

            // Probe handoff route — if 404, skip without opening a useless browser tab.
            bool supported = await ProbeNativeHandoffSupportedAsync(handoffUrl);
            if (!supported)
            {
                result.Error = null; // not an error — route absent; CDP is primary
                return result;
            }

            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                result.Error = "Loopback listener failed: " + ex.Message;
                return result;
            }

            // Accept one request on a background wait.
            Task listenTask = Task.Run(async () =>
            {
                try
                {
                    HttpListenerContext ctx = await listener.GetContextAsync();
                    string code = ctx.Request.QueryString["code"];
                    string cookie = ctx.Request.QueryString["cookie"];
                    string body = "Neon Companion received the Hermes session. You can close this tab.";
                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    ctx.Response.OutputStream.Write(bodyBytes, 0, bodyBytes.Length);
                    ctx.Response.OutputStream.Close();

                    if (!string.IsNullOrEmpty(cookie))
                        tcs.TrySetResult("cookie:" + cookie);
                    else if (!string.IsNullOrEmpty(code))
                        tcs.TrySetResult("code:" + code);
                    else
                        tcs.TrySetResult("");
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                Application.OpenURL(handoffUrl);

                Task delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                Task finished = await Task.WhenAny(tcs.Task, delayTask);
                if (finished != tcs.Task)
                {
                    result.Error = "Timed out waiting for native OAuth handoff.";
                    return result;
                }

                string payload = await tcs.Task;
                if (string.IsNullOrEmpty(payload))
                {
                    result.Error = "Native handoff callback missing code/cookie.";
                    return result;
                }

                if (payload.StartsWith("cookie:", StringComparison.Ordinal))
                {
                    string cookieVal = payload.Substring("cookie:".Length);
                    string extracted = HermesRemoteAuth.ExtractSessionCookies(cookieVal);
                    if (string.IsNullOrEmpty(extracted))
                        extracted = cookieVal.Trim();
                    if (string.IsNullOrEmpty(extracted))
                    {
                        result.Error = "Native handoff cookie was empty.";
                        return result;
                    }
                    result.Ok = true;
                    result.CookieHeader = extracted;
                    return result;
                }

                if (payload.StartsWith("code:", StringComparison.Ordinal))
                {
                    string code = payload.Substring("code:".Length);
                    string redeemed = await RedeemNativeHandoffCodeAsync(baseUrl, code);
                    if (string.IsNullOrEmpty(redeemed))
                    {
                        result.Error = "Native handoff code redeem failed.";
                        return result;
                    }
                    result.Ok = true;
                    result.CookieHeader = redeemed;
                    return result;
                }

                result.Error = "Unknown native handoff payload.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Native handoff failed: " + ex.Message;
                return result;
            }
            finally
            {
                try { listener.Stop(); } catch { }
                try { listener.Close(); } catch { }
                try { await listenTask; } catch { }
            }
        }

        private static async Task<bool> ProbeNativeHandoffSupportedAsync(string handoffUrl)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(handoffUrl);
                req.Method = "GET";
                req.AllowAutoRedirect = false;
                req.Timeout = 4000;
                using (WebResponse resp = await req.GetResponseAsync())
                {
                    HttpWebResponse http = resp as HttpWebResponse;
                    if (http == null)
                        return false;
                    int code = (int)http.StatusCode;
                    // 302 to login or 200 page = supported; 404 = not.
                    return code != 404;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse http = wex.Response as HttpWebResponse;
                if (http == null)
                    return false;
                int code = (int)http.StatusCode;
                // 401/302/400 with body can still mean the route exists (auth gate).
                if (code == 404)
                    return false;
                return code == 301 || code == 302 || code == 303 || code == 307 || code == 308
                    || code == 401 || code == 403 || code == 400 || code == 200;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> RedeemNativeHandoffCodeAsync(string baseUrl, string code)
        {
            string url = baseUrl + "/api/auth/native/redeem";
            string bodyJson = JsonConvert.SerializeObject(new { code = code ?? "" });
            byte[] data = Encoding.UTF8.GetBytes(bodyJson);
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 15000;
                req.ContentLength = data.Length;
                using (Stream s = await req.GetRequestStreamAsync())
                {
                    await s.WriteAsync(data, 0, data.Length);
                }

                using (WebResponse resp = await req.GetResponseAsync())
                using (Stream stream = resp.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string text = await reader.ReadToEndAsync();
                    // Prefer Set-Cookie header if present.
                    string setCookie = resp.Headers["Set-Cookie"];
                    string fromHeader = HermesRemoteAuth.ExtractSessionCookies(setCookie);
                    if (!string.IsNullOrEmpty(fromHeader))
                        return fromHeader;

                    if (string.IsNullOrEmpty(text))
                        return null;
                    JObject obj = JObject.Parse(text);
                    string cookie = obj.Value<string>("cookie");
                    if (string.IsNullOrEmpty(cookie))
                        cookie = obj.Value<string>("cookies");
                    if (string.IsNullOrEmpty(cookie))
                    {
                        string at = obj.Value<string>("access_token");
                        string rt = obj.Value<string>("refresh_token");
                        string provider = obj.Value<string>("provider");
                        if (!string.IsNullOrEmpty(at))
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.Append("hermes_session_at=");
                            sb.Append(at);
                            if (!string.IsNullOrEmpty(rt))
                            {
                                sb.Append("; hermes_session_rt=");
                                sb.Append(rt);
                            }
                            if (!string.IsNullOrEmpty(provider))
                            {
                                sb.Append("; hermes_session_provider=");
                                sb.Append(provider);
                            }
                            cookie = sb.ToString();
                        }
                    }

                    string extracted = HermesRemoteAuth.ExtractSessionCookies(cookie);
                    return !string.IsNullOrEmpty(extracted) ? extracted : cookie;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
