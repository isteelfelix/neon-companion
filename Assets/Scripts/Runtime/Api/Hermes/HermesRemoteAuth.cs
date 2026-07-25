// HermesRemoteAuth.cs - Desktop-style OAuth/basic-auth remote session for Companion.
//
// Production Hermes gates the dashboard/gateway behind an OAuth (or basic-auth)
// session cookie, exactly like the Desktop app. Browsers/native clients cannot set
// Authorization on a WebSocket upgrade, so the contract (mirrors
// hermes_cli/dashboard_auth) is:
//
//   1. Authenticate over HTTP -> server sets an HttpOnly session cookie.
//        * All gated gateways (password form + OAuth IDP): HermesBrowserOAuthLogin
//          opens {base}/login in a dedicated Chromium/Edge profile and captures
//          HttpOnly cookies via CDP (Desktop openOauthLoginWindow equivalent).
//          Password form posts to /auth/password-login on the gateway page itself;
//          Companion never collects username/password.
//   2. REST calls carry that cookie (Cookie header).
//   3. POST /api/auth/ws-ticket (cookie-authenticated) -> single-use 30s ticket.
//   4. Connect the WebSocket with ?ticket=<ticket>.
//
// Auth mode is discovered the same way as Desktop (probeRemoteAuthMode in main.ts):
// public GET /api/status reports auth_required; when gated, GET /api/auth/providers
// lists providers (supports_password) for the sign-in path.
//
// The session cookie is held in memory and — when a secret store is attached via
// ConfigureSessionPersistence — mirrored into the platform secure store (DPAPI / Android
// Keystore / device-obfuscation fallback) under an endpoint-scoped key, so a Companion
// restart resumes the session instead of forcing a fresh browser sign-in. Every state
// transition writes through: adopt/refresh saves, Clear/MarkReauthRequired deletes.
// Tickets are single-use and MUST NOT be persisted (they are minted fresh per connect).
// Passwords are never logged; neither is the cookie or its secret key.
//
// C# 9 (Unity 6) compatible: no switch expressions, no target-typed new,
// no `is not`, no tuple deconstruction. HTTP via UnityWebRequest.

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Secrets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>Lifecycle state of the remote (cookie) session.</summary>
    public enum HermesAuthState
    {
        NoSession,      // never authenticated / cleared
        Authenticated,  // holds a session cookie
        ReauthRequired  // server rejected the session (401) — user must sign in again
    }

    /// <summary>
    /// Raised when the gateway rejects the current session (401) or there is no cookie to
    /// authenticate with. Carries a stable <see cref="Reason"/> the UI can key off:
    /// "no_cookie", "expired", or "invalid_credentials".
    /// </summary>
    public class HermesReauthRequiredException : Exception
    {
        public readonly string Reason;

        public HermesReauthRequiredException(string reason, string message) : base(message)
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// One dashboard-auth provider advertised by GET /api/auth/providers (Desktop probe shape).
    /// </summary>
    public class HermesAuthProviderInfo
    {
        public string Name;
        public string DisplayName;
        public bool SupportsPassword;
    }

    /// <summary>
    /// Result of a Desktop-style public probe of a remote gateway (no credentials sent).
    /// Mirrors electron <c>probeRemoteAuthMode</c>: reachable, authMode, providers.
    /// </summary>
    public class HermesAuthProbeResult
    {
        public string BaseUrl;
        public bool Reachable;
        /// <summary>"oauth" | "token" | "unknown"</summary>
        public string AuthMode;
        public System.Collections.Generic.List<HermesAuthProviderInfo> Providers;
        public string Error;
        public string Version;

        public HermesAuthProbeResult()
        {
            AuthMode = "unknown";
            Providers = new System.Collections.Generic.List<HermesAuthProviderInfo>();
        }

        /// <summary>
        /// True when every advertised provider supports password login (Desktop
        /// isPasswordProvider). Mixed deployments keep the generic browser OAuth path.
        /// </summary>
        public bool IsPasswordProvider
        {
            get
            {
                if (Providers == null || Providers.Count == 0)
                    return false;
                for (int i = 0; i < Providers.Count; i++)
                {
                    if (Providers[i] == null || !Providers[i].SupportsPassword)
                        return false;
                }
                return true;
            }
        }

        /// <summary>First password-capable provider name, or null.</summary>
        public string FirstPasswordProviderName
        {
            get
            {
                if (Providers == null)
                    return null;
                for (int i = 0; i < Providers.Count; i++)
                {
                    if (Providers[i] != null && Providers[i].SupportsPassword
                        && !string.IsNullOrEmpty(Providers[i].Name))
                        return Providers[i].Name;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Desktop-parity remote auth: cookie session + ws-ticket minting. Standalone and mostly
    /// pure so the string-shaping bits (cookie extraction, ticket parse, WS-URL build) are
    /// deterministically verifiable without a Unity runtime.
    /// </summary>
    public class HermesRemoteAuth
    {
        // Session cookie names the gateway may set, bare + prefixed variants
        // (mirrors hermes_cli/dashboard_auth/cookies.py). Prefixed first so the
        // strictest variant wins when extracting.
        private static readonly string[] SessionCookieNames =
        {
            "__Host-hermes_session_at", "__Secure-hermes_session_at", "hermes_session_at",
            "__Host-hermes_session_rt", "__Secure-hermes_session_rt", "hermes_session_rt",
            "__Host-hermes_session_provider", "__Secure-hermes_session_provider", "hermes_session_provider"
        };

        private const int LoginTimeoutSeconds = 30;
        private const int TicketTimeoutSeconds = 15;
        private const int ProbeTimeoutSeconds = 8;

        // Secret-store id prefix for a persisted session cookie. The suffix is a hash of the
        // provider identity + endpoint so multiple gateways/providers never collide. Built on the
        // app-scoped prefix so a provider-list rewrite does not prune the session (SecretIds).
        private const string SessionSecretKeyPrefix = SecretIds.AppScopedPrefix + "session_";

        private string _baseUrl;
        // The live session cookie. Mirrored to _sessionStore (secure storage) when persistence
        // is configured; never written to settings JSON, PlayerPrefs, URLs or logs.
        private string _cookieHeader;

        // Secure persistence target. Null => memory-only (token mode / no store attached).
        private ISecretStore _sessionStore;
        private string _sessionSecretKey;

        public HermesAuthState State { get; private set; } = HermesAuthState.NoSession;
        public string LastAuthError { get; private set; }

        public HermesRemoteAuth(string baseUrl)
        {
            Configure(baseUrl);
        }

        public void Configure(string baseUrl)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
        }

        /// <summary>True when a session cookie is held (an authenticated request can be made).</summary>
        public bool HasSession
        {
            get { return !string.IsNullOrEmpty(_cookieHeader); }
        }

        /// <summary>The Cookie header value the REST client attaches in OAuth mode, or null.</summary>
        public string CookieHeader
        {
            get { return _cookieHeader; }
        }

        /// <summary>
        /// Adopt a session cookie obtained out-of-band (e.g. captured from the browser login window
        /// after a full OAuth sign-in). Accepts either a raw Set-Cookie string or a plain
        /// "name=value; name=value" Cookie header; session cookies are extracted either way.
        /// Written through to secure storage when persistence is configured, replacing any session
        /// previously stored for that endpoint.
        /// </summary>
        public void SetSessionCookie(string cookie)
        {
            string extracted = ExtractSessionCookies(cookie);
            // Fall back to the raw value when it is already a bare Cookie header with no
            // recognised names (defensive — lets a caller inject a custom cookie).
            _cookieHeader = !string.IsNullOrEmpty(extracted) ? extracted : (cookie ?? "").Trim();
            if (!string.IsNullOrEmpty(_cookieHeader))
            {
                State = HermesAuthState.Authenticated;
                LastAuthError = null;
                // A new sign-in replaces whatever was stored for this endpoint.
                PersistSession();
            }
        }

        /// <summary>Forget the session entirely (logout / mode switch), including the stored copy.</summary>
        public void Clear()
        {
            _cookieHeader = null;
            State = HermesAuthState.NoSession;
            LastAuthError = null;
            ForgetPersistedSession();
        }

        /// <summary>
        /// Drop the cookie and flag that the user must sign in again. Called on any 401 from a
        /// cookie-authenticated call. <paramref name="reason"/> is a stable key: "no_cookie",
        /// "expired", or "invalid_credentials". The persisted copy is deleted too — a rejected
        /// session must not be restored on the next start (that would loop the reauth prompt).
        /// </summary>
        public void MarkReauthRequired(string reason)
        {
            _cookieHeader = null;
            State = HermesAuthState.ReauthRequired;
            LastAuthError = reason;
            ForgetPersistedSession();
        }

        // === Secure persistence ===

        /// <summary>
        /// Attach (or detach) secure persistence for the session cookie. Pass the app's
        /// <see cref="ISecretStore"/> and an endpoint-scoped key from
        /// <see cref="BuildSessionSecretKey"/>; pass nulls to go memory-only (token mode).
        /// Changing the target does not touch either the live cookie or previously stored data.
        /// </summary>
        public void ConfigureSessionPersistence(ISecretStore store, string secretKey)
        {
            _sessionStore = !string.IsNullOrEmpty(secretKey) ? store : null;
            _sessionSecretKey = _sessionStore != null ? secretKey : null;
        }

        /// <summary>True when a secret store + key are attached.</summary>
        public bool HasSessionPersistence
        {
            get { return _sessionStore != null && !string.IsNullOrEmpty(_sessionSecretKey); }
        }

        /// <summary>
        /// Load a previously persisted session cookie into memory (app start). No-op when a
        /// session is already held or nothing usable is stored. Returns true when a session is
        /// available afterwards, in which case <see cref="State"/> is Authenticated and
        /// <see cref="HasSession"/> is true — callers can connect without a fresh sign-in.
        /// </summary>
        public bool TryRestoreSession()
        {
            if (!string.IsNullOrEmpty(_cookieHeader))
                return true;
            if (!HasSessionPersistence)
                return false;

            string stored = _sessionStore.GetSecret(_sessionSecretKey);
            string extracted = ExtractSessionCookies(stored);
            if (string.IsNullOrEmpty(extracted))
            {
                // Present but unreadable (rotated device key / corrupt payload) — drop it so we
                // do not retry a dead value forever. Never log the value itself.
                if (!string.IsNullOrEmpty(stored))
                    ForgetPersistedSession();
                return false;
            }

            _cookieHeader = extracted;
            State = HermesAuthState.Authenticated;
            LastAuthError = null;
            return true;
        }

        /// <summary>
        /// Adopt refreshed session cookies from a response's Set-Cookie header. Hermes rotates the
        /// refresh cookie (and re-issues the access cookie) when it renews a session, so the stored
        /// bundle has to follow. No-op without a live session or when nothing changed. Returns true
        /// when the held cookie was updated.
        /// </summary>
        public bool AdoptRefreshedCookies(string setCookieHeader)
        {
            if (string.IsNullOrEmpty(_cookieHeader) || string.IsNullOrEmpty(setCookieHeader))
                return false;

            string merged = MergeCookieHeaders(_cookieHeader, setCookieHeader);
            if (string.IsNullOrEmpty(merged) || string.Equals(merged, _cookieHeader, StringComparison.Ordinal))
                return false;

            _cookieHeader = merged;
            State = HermesAuthState.Authenticated;
            LastAuthError = null;
            PersistSession();
            return true;
        }

        /// <summary>
        /// Stable secret-store id for one provider+endpoint pair. The identity is hashed so the
        /// gateway host never lands in the secrets file as readable text and the id stays a short
        /// fixed-length token. Same provider id + same normalized base URL => same key.
        /// </summary>
        public static string BuildSessionSecretKey(string providerId, string rawBaseUrl)
        {
            string normalized = NormalizeBaseUrl(rawBaseUrl);
            string identity = (providerId ?? "").Trim() + "|" +
                              (normalized != null ? normalized.ToLowerInvariant() : "");

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var sb = new StringBuilder(SessionSecretKeyPrefix, SessionSecretKeyPrefix.Length + 32);
                // 16 bytes of SHA-256 is plenty to keep distinct providers/hosts apart.
                for (int i = 0; i < 16; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // Mirror the live cookie into secure storage (or delete it when there is none).
        private void PersistSession()
        {
            if (!HasSessionPersistence)
                return;

            if (string.IsNullOrEmpty(_cookieHeader))
            {
                _sessionStore.DeleteSecret(_sessionSecretKey);
                return;
            }

            _sessionStore.SetSecret(_sessionSecretKey, _cookieHeader);
        }

        private void ForgetPersistedSession()
        {
            if (!HasSessionPersistence)
                return;
            _sessionStore.DeleteSecret(_sessionSecretKey);
        }

        // === Public probe (Desktop probeRemoteAuthMode) ===

        /// <summary>
        /// Probe a remote gateway without credentials. Uses public
        /// <c>GET /api/status</c> (auth_required) and, when gated,
        /// <c>GET /api/auth/providers</c>. Network failures return
        /// <see cref="HermesAuthProbeResult.Reachable"/> = false rather than throwing.
        /// </summary>
        public static async Task<HermesAuthProbeResult> ProbeAsync(string rawBaseUrl)
        {
            var result = new HermesAuthProbeResult();
            string baseUrl = NormalizeBaseUrl(rawBaseUrl);
            result.BaseUrl = baseUrl;

            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Error = "Gateway URL is required.";
                return result;
            }

            try
            {
                string statusJson = await GetPublicJsonAsync(baseUrl + "/api/status", ProbeTimeoutSeconds);
                bool authRequired = ParseAuthRequired(statusJson);
                result.Version = ParseJsonStringField(statusJson, "version");
                result.Reachable = true;
                result.AuthMode = authRequired ? "oauth" : "token";

                if (authRequired)
                {
                    try
                    {
                        string providersJson = await GetPublicJsonAsync(
                            baseUrl + "/api/auth/providers", ProbeTimeoutSeconds);
                        ParseProviders(providersJson, result.Providers);
                    }
                    catch
                    {
                        // Provider listing is optional metadata; auth mode is already known.
                    }
                }
            }
            catch (Exception ex)
            {
                result.Reachable = false;
                result.AuthMode = "unknown";
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Build the gateway login page URL Desktop opens in its OAuth window
        /// (<c>{base}/login</c>).
        /// </summary>
        public static string BuildLoginUrl(string rawBaseUrl)
        {
            string baseUrl = NormalizeBaseUrl(rawBaseUrl);
            if (string.IsNullOrEmpty(baseUrl))
                return null;
            return baseUrl + "/login";
        }

        public static string NormalizeBaseUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return null;
            string value = rawUrl.Trim();
            // Drop trailing slash (keep scheme/host/path prefix).
            while (value.Length > 0 && value[value.Length - 1] == '/')
                value = value.Substring(0, value.Length - 1);
            return value;
        }

        // === Password (basic-auth) login ===

        /// <summary>
        /// Authenticate against a password-capable dashboard-auth provider and capture the
        /// session cookie. Mirrors POST /auth/password-login (hermes_cli/dashboard_auth/routes.py).
        /// Throws <see cref="HermesReauthRequiredException"/>("invalid_credentials") on 401.
        /// The password is never logged. <paramref name="provider"/> is the dashboard-auth
        /// provider name from the probe (e.g. "basic") — never a user-facing field.
        /// </summary>
        public async Task PasswordLoginAsync(string provider, string username, string password)
        {
            if (string.IsNullOrEmpty(_baseUrl))
                throw new InvalidOperationException("HermesRemoteAuth base URL is not configured.");

            string url = _baseUrl + "/auth/password-login";
            // Anonymous body -> {provider,username,password}. Serialized, never logged.
            string bodyJson = JsonConvert.SerializeObject(new
            {
                provider = provider ?? "",
                username = username ?? "",
                password = password ?? ""
            });
            byte[] data = Encoding.UTF8.GetBytes(bodyJson);

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(data);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = LoginTimeoutSeconds;

                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                long code = request.responseCode;
                if (request.result != UnityWebRequest.Result.Success || code < 200 || code >= 300)
                {
                    if (code == 401)
                    {
                        MarkReauthRequired("invalid_credentials");
                        throw new HermesReauthRequiredException(
                            "invalid_credentials",
                            "Hermes login failed: invalid credentials.");
                    }
                    // Never echo the response body wholesale — it is short and generic, but the
                    // request body (with the password) is what we must never surface.
                    string detail = SafeError(request);
                    throw new Exception("Hermes login failed (" + code + "): " + detail);
                }

                string setCookie = request.GetResponseHeader("Set-Cookie");
                string extracted = ExtractSessionCookies(setCookie);
                if (string.IsNullOrEmpty(extracted))
                {
                    MarkReauthRequired("no_cookie");
                    throw new HermesReauthRequiredException(
                        "no_cookie",
                        "Hermes login succeeded but no session cookie was returned.");
                }

                _cookieHeader = extracted;
                State = HermesAuthState.Authenticated;
                LastAuthError = null;
                PersistSession();
            }
        }

        // === WS ticket ===

        /// <summary>
        /// Mint a single-use WS-upgrade ticket for the held session. Mirrors
        /// POST /api/auth/ws-ticket. The ticket is returned to the caller and never stored.
        /// Throws <see cref="HermesReauthRequiredException"/> when there is no cookie
        /// ("no_cookie") or the server rejects it ("expired").
        /// </summary>
        public async Task<string> MintWsTicketAsync()
        {
            if (string.IsNullOrEmpty(_baseUrl))
                throw new InvalidOperationException("HermesRemoteAuth base URL is not configured.");

            if (string.IsNullOrEmpty(_cookieHeader))
            {
                MarkReauthRequired("no_cookie");
                throw new HermesReauthRequiredException(
                    "no_cookie",
                    "No Hermes session — sign in before connecting.");
            }

            string url = _baseUrl + "/api/auth/ws-ticket";
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                // POST with an empty body — the server reads identity from the cookie only.
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Cookie", _cookieHeader);
                request.timeout = TicketTimeoutSeconds;

                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                long code = request.responseCode;
                if (code == 401 || code == 403)
                {
                    MarkReauthRequired("expired");
                    throw new HermesReauthRequiredException(
                        "expired",
                        "Hermes session expired — sign in again.");
                }

                if (request.result != UnityWebRequest.Result.Success || code < 200 || code >= 300)
                    throw new Exception("Hermes ws-ticket request failed (" + code + "): " + SafeError(request));

                // Minting is the first cookie-authenticated call of every connect/reconnect, so it
                // is where the server most often hands back a renewed access + rotated refresh
                // cookie. Keep the stored bundle in step. The ticket itself is never stored.
                AdoptRefreshedCookies(request.GetResponseHeader("Set-Cookie"));

                string ticket = ParseTicket(request.downloadHandler != null ? request.downloadHandler.text : "");
                if (string.IsNullOrEmpty(ticket))
                    throw new Exception("Hermes ws-ticket response missing 'ticket'.");

                return ticket;
            }
        }

        // === Pure helpers (unit-verifiable) ===

        /// <summary>
        /// Parse <c>auth_required</c> from a /api/status JSON body (Desktop authModeFromStatus).
        /// </summary>
        public static bool ParseAuthRequired(string statusJson)
        {
            if (string.IsNullOrEmpty(statusJson))
                return false;
            try
            {
                JObject obj = JObject.Parse(statusJson);
                JToken t = obj["auth_required"];
                if (t == null)
                    return false;
                if (t.Type == JTokenType.Boolean)
                    return t.Value<bool>();
                if (t.Type == JTokenType.String)
                {
                    string s = t.Value<string>();
                    return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s, "1", StringComparison.OrdinalIgnoreCase);
                }
                if (t.Type == JTokenType.Integer)
                    return t.Value<int>() != 0;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parse the providers array from GET /api/auth/providers into the probe list.
        /// </summary>
        public static void ParseProviders(string providersJson, System.Collections.Generic.List<HermesAuthProviderInfo> into)
        {
            if (into == null || string.IsNullOrEmpty(providersJson))
                return;
            try
            {
                JObject obj = JObject.Parse(providersJson);
                JToken arr = obj["providers"];
                if (arr == null || arr.Type != JTokenType.Array)
                    return;
                foreach (JToken item in arr)
                {
                    if (item == null || item.Type != JTokenType.Object)
                        continue;
                    string name = item["name"] != null ? item["name"].Value<string>() : null;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    string display = item["display_name"] != null
                        ? item["display_name"].Value<string>()
                        : null;
                    if (string.IsNullOrEmpty(display))
                        display = name;
                    bool supportsPassword = false;
                    JToken sp = item["supports_password"];
                    if (sp != null && sp.Type == JTokenType.Boolean)
                        supportsPassword = sp.Value<bool>();
                    into.Add(new HermesAuthProviderInfo
                    {
                        Name = name,
                        DisplayName = display,
                        SupportsPassword = supportsPassword
                    });
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static string ParseJsonStringField(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field))
                return null;
            try
            {
                JObject obj = JObject.Parse(json);
                JToken t = obj[field];
                return t != null ? t.Value<string>() : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> GetPublicJsonAsync(string url, int timeoutSeconds)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = timeoutSeconds;
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                long code = request.responseCode;
                if (request.result != UnityWebRequest.Result.Success || code < 200 || code >= 300)
                    throw new Exception("HTTP " + code + ": " + SafeError(request));

                return request.downloadHandler != null ? request.downloadHandler.text : "";
            }
        }

        /// <summary>
        /// Extract the known session cookies from a Set-Cookie (or Cookie) header and return a
        /// "name=value; name=value" Cookie header, or null when none are present.
        ///
        /// Unity collapses multiple Set-Cookie response headers into one comma-joined string; we
        /// pull only the session cookie name=value pairs and drop all attributes (Path/Expires/
        /// HttpOnly/Secure/SameSite). Session cookie values are token_urlsafe / JWT text with no
        /// ';', ',' or whitespace, so a boundary-anchored scan is unambiguous. A boundary before
        /// the name prevents a bare name matching inside a "__Host-" prefixed one.
        /// </summary>
        public static string ExtractSessionCookies(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue))
                return null;

            var sb = new StringBuilder();
            int i = 0;
            while (i < SessionCookieNames.Length)
            {
                string name = SessionCookieNames[i];
                i++;

                // (start | , | ; | whitespace) name = value(no ; , or space)
                var match = Regex.Match(
                    headerValue,
                    "(?:^|[,;\\s])" + Regex.Escape(name) + "=([^;,\\s]+)");
                if (!match.Success)
                    continue;

                string value = match.Groups[1].Value;
                if (string.IsNullOrEmpty(value))
                    continue;

                // Skip a bare-name hit when its prefixed variant was already captured
                // (same base cookie, only one should be sent).
                if (AlreadyHasBaseCookie(sb, name))
                    continue;

                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(name).Append('=').Append(value);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Merge session cookies from a Set-Cookie (or Cookie) header into an existing Cookie
        /// header, keyed on the cookie base name so a rotated value replaces the old one in place
        /// (and a prefixed variant replaces its bare form rather than being sent alongside it).
        /// Cookies the response does not mention are kept — a refresh may re-issue only some of
        /// them. Returns the existing header unchanged when the incoming header has no session
        /// cookies.
        /// </summary>
        public static string MergeCookieHeaders(string existingCookieHeader, string incomingHeader)
        {
            string incoming = ExtractSessionCookies(incomingHeader);
            if (string.IsNullOrEmpty(incoming))
                return existingCookieHeader;
            if (string.IsNullOrEmpty(existingCookieHeader))
                return incoming;

            var names = new System.Collections.Generic.List<string>();
            var values = new System.Collections.Generic.List<string>();
            ParseCookiePairs(existingCookieHeader, names, values);

            var freshNames = new System.Collections.Generic.List<string>();
            var freshValues = new System.Collections.Generic.List<string>();
            ParseCookiePairs(incoming, freshNames, freshValues);

            for (int i = 0; i < freshNames.Count; i++)
            {
                string freshBase = StripCookiePrefix(freshNames[i]);
                int existingIndex = -1;
                for (int j = 0; j < names.Count; j++)
                {
                    if (string.Equals(StripCookiePrefix(names[j]), freshBase, StringComparison.Ordinal))
                    {
                        existingIndex = j;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    names[existingIndex] = freshNames[i];
                    values[existingIndex] = freshValues[i];
                }
                else
                {
                    names.Add(freshNames[i]);
                    values.Add(freshValues[i]);
                }
            }

            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(names[i]).Append('=').Append(values[i]);
            }
            return sb.ToString();
        }

        // Split a "name=value; name=value" Cookie header into parallel name/value lists.
        private static void ParseCookiePairs(
            string cookieHeader,
            System.Collections.Generic.List<string> names,
            System.Collections.Generic.List<string> values)
        {
            if (string.IsNullOrEmpty(cookieHeader))
                return;

            string[] parts = cookieHeader.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                    continue;
                int eq = part.IndexOf('=');
                if (eq <= 0 || eq == part.Length - 1)
                    continue;
                names.Add(part.Substring(0, eq).Trim());
                values.Add(part.Substring(eq + 1).Trim());
            }
        }

        // True if a cookie whose base (name without __Host-/__Secure- prefix) matches the base of
        // `candidate` was already appended. Keeps us from emitting both prefixed and bare forms.
        private static bool AlreadyHasBaseCookie(StringBuilder built, string candidate)
        {
            string baseName = StripCookiePrefix(candidate);
            string existing = built.ToString();
            // Match "<...>baseName=" at a cookie boundary.
            return Regex.IsMatch(existing, "(?:^|[;\\s])(?:__Host-|__Secure-)?" + Regex.Escape(baseName) + "=");
        }

        private static string StripCookiePrefix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            if (name.StartsWith("__Host-", StringComparison.Ordinal))
                return name.Substring("__Host-".Length);
            if (name.StartsWith("__Secure-", StringComparison.Ordinal))
                return name.Substring("__Secure-".Length);
            return name;
        }

        /// <summary>Parse the <c>ticket</c> field out of a ws-ticket JSON response.</summary>
        public static string ParseTicket(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                JObject obj = JObject.Parse(json);
                JToken t = obj["ticket"];
                return t != null ? t.Value<string>() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Append <c>?ticket=</c> (or <c>&amp;ticket=</c>) to a WebSocket URL, URL-encoding the
        /// ticket. Mirrors Desktop buildGatewayWsUrlWithTicket (connection-config.ts).
        /// </summary>
        public static string BuildTicketWsUrl(string wsUrl, string ticket)
        {
            if (string.IsNullOrEmpty(wsUrl))
                return wsUrl;
            if (string.IsNullOrEmpty(ticket))
                return wsUrl;
            string separator = wsUrl.Contains("?") ? "&" : "?";
            return wsUrl + separator + "ticket=" + Uri.EscapeDataString(ticket);
        }

        // Error text safe to surface: prefers UnityWebRequest.error, never the request body.
        private static string SafeError(UnityWebRequest request)
        {
            if (request == null)
                return "unknown error";
            if (!string.IsNullOrEmpty(request.error))
                return request.error;
            string body = request.downloadHandler != null ? request.downloadHandler.text : null;
            return string.IsNullOrEmpty(body) ? "unknown error" : body;
        }
    }
}
