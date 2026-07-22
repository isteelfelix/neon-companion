// HermesRemoteAuth.cs - Desktop-style OAuth/basic-auth remote session for Companion.
//
// Production Hermes gates the dashboard/gateway behind an OAuth (or basic-auth)
// session cookie, exactly like the Desktop app. Browsers/native clients cannot set
// Authorization on a WebSocket upgrade, so the contract (mirrors
// hermes_cli/dashboard_auth) is:
//
//   1. Authenticate over HTTP -> server sets an HttpOnly session cookie.
//        * basic-auth provider: POST /auth/password-login {provider,username,password}
//        * full OAuth: user signs in via browser and pastes the session cookie
//          (SetSessionCookie) - the interactive IDP redirect is out of scope here.
//   2. REST calls carry that cookie (Cookie header).
//   3. POST /api/auth/ws-ticket (cookie-authenticated) -> single-use 30s ticket.
//   4. Connect the WebSocket with ?ticket=<ticket>.
//
// The session cookie lives in memory only. Tickets are single-use and MUST NOT be
// persisted (they are minted fresh per connect). Passwords are never logged.
//
// C# 9 (Unity 6) compatible: no switch expressions, no target-typed new,
// no `is not`, no tuple deconstruction. HTTP via UnityWebRequest.

using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

        private string _baseUrl;
        // In-memory only. Never written to disk / secret store — a session cookie is a bearer
        // credential and tickets minted from it are single-use.
        private string _cookieHeader;

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
        /// Adopt a session cookie obtained out-of-band (e.g. copied from a browser after a full
        /// OAuth sign-in). Accepts either a raw Set-Cookie string or a plain "name=value; name=value"
        /// Cookie header; session cookies are extracted either way. Not persisted.
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
            }
        }

        /// <summary>Forget the session entirely (logout / mode switch).</summary>
        public void Clear()
        {
            _cookieHeader = null;
            State = HermesAuthState.NoSession;
            LastAuthError = null;
        }

        /// <summary>
        /// Drop the cookie and flag that the user must sign in again. Called on any 401 from a
        /// cookie-authenticated call. <paramref name="reason"/> is a stable key: "no_cookie",
        /// "expired", or "invalid_credentials".
        /// </summary>
        public void MarkReauthRequired(string reason)
        {
            _cookieHeader = null;
            State = HermesAuthState.ReauthRequired;
            LastAuthError = reason;
        }

        // === Password (basic-auth) login ===

        /// <summary>
        /// Authenticate against a password-capable dashboard-auth provider and capture the
        /// session cookie. Mirrors POST /auth/password-login (hermes_cli/dashboard_auth/routes.py).
        /// Throws <see cref="HermesReauthRequiredException"/>("invalid_credentials") on 401.
        /// The password is never logged.
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

                string ticket = ParseTicket(request.downloadHandler != null ? request.downloadHandler.text : "");
                if (string.IsNullOrEmpty(ticket))
                    throw new Exception("Hermes ws-ticket response missing 'ticket'.");

                return ticket;
            }
        }

        // === Pure helpers (unit-verifiable) ===

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
