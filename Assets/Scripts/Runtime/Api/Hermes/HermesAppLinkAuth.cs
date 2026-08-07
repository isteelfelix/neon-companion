// HermesAppLinkAuth.cs — Android native OAuth flow (RFC 8252) for Companion.
//
// Uses the gateway's built-in /auth/native/authorize + /auth/native/token endpoints.
// No App Links needed — the gateway redirects the browser to a loopback URL
// (http://127.0.0.1:port/callback?code=...), and the app catches it via TcpListener.
//
// Flow:
//   1. App generates PKCE pair (code_verifier + code_challenge S256) + random state.
//   2. App starts TcpListener on 127.0.0.1:random_port.
//   3. App opens /auth/native/authorize?code_challenge=...&redirect_uri=http://127.0.0.1:port/callback
//      in system browser (Custom Tab). Gateway starts IdP login, sets PKCE cookie in browser.
//   4. User authenticates at IdP (Nous → Google/GitHub). System browser = OAuth not blocked.
//   5. IdP redirects to gateway /auth/callback. Gateway validates, mints a one-time gateway code,
//      redirects browser to http://127.0.0.1:port/callback?code=gw_code&state=client_state.
//   6. Browser navigates to loopback → app's TcpListener catches the HTTP request → extracts code.
//   7. App POSTs /auth/native/token {code, code_verifier} → gets access_token + refresh_token in JSON.
//   8. App builds session cookie header from tokens → SetSessionCookie → done.
//
// The PKCE cookie stays in the browser's cookie jar throughout — the app never needs it.
// The app only receives the gateway-brokered code via loopback, then exchanges it for tokens.
//
// Zero server-side changes. Both /auth/native/authorize and /auth/native/token already exist.
//
// C# 9 (Unity 6) compatible.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>Result of a native OAuth attempt.</summary>
    public class HermesAppLinkResult
    {
        public bool Ok;
        public string CookieHeader;
        public string Error;
    }

    /// <summary>
    /// Android OAuth flow using RFC 8252 native app authorization.
    /// System browser + loopback redirect + PKCE code exchange.
    /// </summary>
    public static class HermesAppLinkAuth
    {
        private const int HttpTimeout = 15;
        private const int CallbackWaitMs = 180000; // 3 min for user to complete IdP login

        /// <summary>
        /// Run the full native OAuth flow. Android-only.
        /// </summary>
        public static async Task<HermesAppLinkResult> LoginAsync(
            string rawBaseUrl,
            string providerName = "")
        {
            HermesAppLinkResult result = new HermesAppLinkResult();

#if !UNITY_ANDROID || UNITY_EDITOR
            result.Error = "Native OAuth auth is only available on Android.";
            await Task.CompletedTask; // keep method truly async in Editor compile (silences CS1998)
            return result;
#else
            string baseUrl = HermesRemoteAuth.NormalizeBaseUrl(rawBaseUrl);
            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Error = "Gateway URL is empty.";
                return result;
            }

            // --- Step 1: Generate PKCE pair + state ---
            string codeVerifier = GenerateCodeVerifier();
            string codeChallenge = GenerateCodeChallenge(codeVerifier);
            string state = GenerateRandomState();

            // --- Step 2: Find free port + start TcpListener ---
            int port = FindFreePort();
            if (port <= 0)
            {
                result.Error = "Could not allocate a local port for OAuth callback.";
                return result;
            }

            string redirectUri = "http://127.0.0.1:" + port + "/callback";
            Debug.Log("[NeonCompanion] Native OAuth: listening on 127.0.0.1:" + port);

            // --- Step 3: Build authorize URL ---
            StringBuilder authUrl = new StringBuilder();
            authUrl.Append(baseUrl);
            authUrl.Append("/auth/native/authorize");
            authUrl.Append("?code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
            authUrl.Append("&code_challenge_method=S256");
            authUrl.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            authUrl.Append("&state=").Append(Uri.EscapeDataString(state));
            if (!string.IsNullOrEmpty(providerName))
                authUrl.Append("&provider=").Append(Uri.EscapeDataString(providerName));

            // --- Step 4-6: Open browser + wait for loopback callback ---
            string authCode = null;
            string receivedState = null;

            // TcpListener is not IDisposable on Unity's .NET Standard 2.0 API level,
            // so manage its lifetime with try/finally instead of a using block.
            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    result.Error = "Failed to start callback listener: " + ex.Message;
                    return result;
                }

                // Open the authorize URL in system browser (Custom Tab).
                Debug.Log("[NeonCompanion] Native OAuth: opening " + authUrl.ToString());
                OpenSystemBrowser(authUrl.ToString());

                // Wait for the callback (browser → loopback redirect).
                try
                {
                    string callbackResult = await AcceptCallbackAsync(listener, CallbackWaitMs);
                    if (callbackResult == null)
                    {
                        result.Error = "Timed out waiting for OAuth callback.";
                        return result;
                    }

                    // Parse query string from the callback.
                    var queryParams = ParseQueryString(callbackResult);
                    queryParams.TryGetValue("code", out authCode);
                    queryParams.TryGetValue("state", out receivedState);

                    string errCode;
                    if (queryParams.TryGetValue("error", out errCode) && !string.IsNullOrEmpty(errCode))
                    {
                        string errDesc;
                        if (!queryParams.TryGetValue("error_description", out errDesc)
                            || string.IsNullOrEmpty(errDesc))
                        {
                            errDesc = errCode;
                        }
                        result.Error = "OAuth error: " + errDesc;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    result.Error = "Callback listener error: " + ex.Message;
                    return result;
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            // --- Validate state ---
            if (string.IsNullOrEmpty(authCode))
            {
                result.Error = "OAuth callback did not include an authorization code.";
                return result;
            }

            if (!string.Equals(receivedState, state, StringComparison.Ordinal))
            {
                result.Error = "OAuth state mismatch (possible CSRF).";
                return result;
            }

            // --- Step 7: Exchange code for tokens ---
            NativeTokenResponse tokens;
            try
            {
                tokens = await ExchangeCodeForTokensAsync(baseUrl, authCode, codeVerifier);
            }
            catch (Exception ex)
            {
                result.Error = "Token exchange failed: " + ex.Message;
                return result;
            }

            if (string.IsNullOrEmpty(tokens.AccessToken))
            {
                result.Error = "Token exchange returned no access token.";
                return result;
            }

            // --- Step 8: Build session cookie header from tokens ---
            StringBuilder cookie = new StringBuilder();
            cookie.Append("hermes_session_at=").Append(tokens.AccessToken);
            if (!string.IsNullOrEmpty(tokens.RefreshToken))
                cookie.Append("; hermes_session_rt=").Append(tokens.RefreshToken);
            if (!string.IsNullOrEmpty(tokens.Provider))
                cookie.Append("; hermes_session_provider=").Append(tokens.Provider);

            result.Ok = true;
            result.CookieHeader = cookie.ToString();
            Debug.Log("[NeonCompanion] Native OAuth: success, session obtained.");
            return result;
#endif
        }

        // ------------------------------------------------------------------
        // PKCE helpers
        // ------------------------------------------------------------------

        private static string GenerateCodeVerifier()
        {
            // RFC 7636: 43-128 chars from [A-Z][a-z][0-9]-._~
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string GenerateCodeChallenge(string verifier)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
                return Base64UrlEncode(hash);
            }
        }

        private static string GenerateRandomState()
        {
            byte[] bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        // ------------------------------------------------------------------
        // Loopback TCP listener
        // ------------------------------------------------------------------

        private static int FindFreePort()
        {
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Accept one HTTP request on the TcpListener, extract the query string,
        /// send a simple response to the browser, and return the raw query string.
        /// </summary>
        private static async Task<string> AcceptCallbackAsync(TcpListener listener, int timeoutMs)
        {
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            Task timeoutTask = Task.Delay(timeoutMs);

            if (await Task.WhenAny(acceptTask, timeoutTask) != acceptTask)
                return null; // timed out

            using (TcpClient client = await acceptTask)
            using (NetworkStream stream = client.GetStream())
            {
                // Read the HTTP request line (we only need the first line for the URL).
                byte[] buffer = new byte[4096];
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                    return null;

                string request = Encoding.UTF8.GetString(buffer, 0, read);

                // Parse: "GET /callback?code=...&state=... HTTP/1.1"
                string queryString = null;
                int spaceIdx = request.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    int nextSpace = request.IndexOf(' ', spaceIdx + 1);
                    if (nextSpace > 0)
                    {
                        string path = request.Substring(spaceIdx + 1, nextSpace - spaceIdx - 1);
                        int qIdx = path.IndexOf('?');
                        if (qIdx >= 0)
                            queryString = path.Substring(qIdx + 1);
                    }
                }

                // Send a simple HTML response so the browser shows something nice.
                string body = "<html><body style=\"background:#091019;color:#6cf;font-family:sans-serif;"
                    + "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">"
                    + "<p>✅ Authentication complete. You can close this tab.</p></body></html>";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                string response = "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/html; charset=utf-8\r\n"
                    + "Content-Length: " + bodyBytes.Length + "\r\n"
                    + "Connection: close\r\n"
                    + "\r\n";
                byte[] headerBytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);

                return queryString ?? "";
            }
        }

        // ------------------------------------------------------------------
        // Token exchange
        // ------------------------------------------------------------------

        private class NativeTokenResponse
        {
            public string AccessToken;
            public string RefreshToken;
            public string Provider;
        }

        private static async Task<NativeTokenResponse> ExchangeCodeForTokensAsync(
            string baseUrl, string code, string codeVerifier)
        {
            string url = baseUrl + "/auth/native/token";
            string bodyJson = JsonConvert.SerializeObject(new
            {
                code = code,
                code_verifier = codeVerifier
            });
            byte[] data = Encoding.UTF8.GetBytes(bodyJson);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = HttpTimeout * 1000;
            req.ContentLength = data.Length;

            using (Stream s = await Task.Factory.FromAsync(
                req.BeginGetRequestStream, req.EndGetRequestStream, null))
            {
                await s.WriteAsync(data, 0, data.Length);
            }

            WebResponse wr = await Task.Factory.FromAsync(
                req.BeginGetResponse, req.EndGetResponse, null);

            using (Stream rs = wr.GetResponseStream())
            using (StreamReader r = new StreamReader(rs, Encoding.UTF8))
            {
                string json = await r.ReadToEndAsync();
                wr.Close();

                JObject parsed = JObject.Parse(json);
                return new NativeTokenResponse
                {
                    AccessToken = parsed.Value<string>("access_token"),
                    RefreshToken = parsed.Value<string>("refresh_token"),
                    Provider = parsed.Value<string>("provider")
                };
            }
        }

        // ------------------------------------------------------------------
        // Utility
        // ------------------------------------------------------------------

        private static System.Collections.Generic.Dictionary<string, string> ParseQueryString(string qs)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string>();
            if (string.IsNullOrEmpty(qs))
                return dict;

            string[] pairs = qs.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string key = Uri.UnescapeDataString(pair.Substring(0, eq));
                    string val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    dict[key] = val;
                }
                else if (pair.Length > 0)
                {
                    dict[Uri.UnescapeDataString(pair)] = "";
                }
            }
            return dict;
        }

        /// <summary>
        /// Open a URL in a Custom Tab pinned to ONE browser package via the
        /// NeonCustomTabsLauncher plugin, so the whole OAuth redirect chain
        /// (native/authorize → IdP → /auth/callback → loopback) stays in a single
        /// cookie jar and the gateway's "hermes_session_pkce" cookie survives.
        /// </summary>
        private static void OpenSystemBrowser(string url)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaClass launcher =
                    new AndroidJavaClass("com.neoncompanion.customtabs.NeonCustomTabsLauncher"))
                {
                    string chosen = launcher.CallStatic<string>("open", url);
                    if (!string.IsNullOrEmpty(chosen) && chosen.StartsWith("ERROR:"))
                    {
                        Debug.LogError("[NeonCompanion] Custom Tab launcher error: " + chosen
                            + " — falling back to Application.OpenURL");
                        Application.OpenURL(url);
                    }
                    else
                    {
                        Debug.Log("[NeonCompanion] Native OAuth: browser pinned to '"
                            + (string.IsNullOrEmpty(chosen) ? "(default, unpinned)" : chosen) + "'");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[NeonCompanion] Failed to open browser: " + ex.Message);
                Application.OpenURL(url);
            }
#endif
        }
    }
}
