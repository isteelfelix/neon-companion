// GlobalBackendSelector.cs - Global backend mode selector
// Determines whether the app runs in Hermes or OpenAI mode.
// Controls feature gating, transport selection, and connection lifecycle.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Data.Secrets;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public enum BackendMode
    {
        OpenAI,  // HTTP REST, pure chat
        Hermes   // WebSocket JSON-RPC, agent, sessions, tools
    }

    /// <summary>
    /// Global backend mode selector. Singleton MonoBehaviour.
    /// Determines which transport is active and which features are available.
    /// </summary>
    public class GlobalBackendSelector : MonoBehaviour
    {
        private static GlobalBackendSelector _instance;
        public static GlobalBackendSelector Instance => _instance;

        // === Config ===

        [Header("Hermes Backend")]
        public string HermesWsUrl = "";
        public string HermesRestUrl = "";
        public string HermesToken;

        // === State ===

        public BackendMode CurrentMode { get; private set; } = BackendMode.OpenAI;
        public string LastConnectionError { get; private set; }

        // === Events ===

        public event Action<BackendMode> OnModeChanged;
        public event Action<string> OnError;
        public event Action<TransportState> OnConnectionStateChanged;

        // === Transport Access ===

        public IChatTransport ActiveTransport { get; private set; }
        public HermesRestClient RestClient { get; private set; }
        public HermesGateway Gateway { get; private set; }
        public HermesSessionManager SessionManager { get; private set; }

        // === Remote (OAuth/basic-auth) auth ===

        // Desktop-style cookie session + ws-ticket auth. Non-null once SetupHermes ran; only
        // exercised when the active Hermes provider has authMode == "oauth".
        private HermesRemoteAuth _remoteAuth;

        /// <summary>Current remote-session state (NoSession/Authenticated/ReauthRequired).</summary>
        public HermesAuthState RemoteAuthState
        {
            get { return _remoteAuth != null ? _remoteAuth.State : HermesAuthState.NoSession; }
        }

        /// <summary>True when a remote (cookie) session is currently held.</summary>
        public bool HasRemoteSession
        {
            get { return _remoteAuth != null && _remoteAuth.HasSession; }
        }

        /// <summary>
        /// Stable reason key for the last remote-auth failure ("no_cookie" / "expired" /
        /// "invalid_credentials"), or null. The UI keys reauth messaging off this.
        /// </summary>
        public string RemoteAuthError
        {
            get { return _remoteAuth != null ? _remoteAuth.LastAuthError : null; }
        }

        // Secret-store key for a provider's cached password (basic-auth login). Kept out of the
        // provider config JSON; used to re-login transparently on reconnect.
        private static string PasswordSecretKey(string providerId)
        {
            return "hermes_pw_" + (providerId ?? "");
        }

        private static bool IsOAuthProvider(ProviderConfig provider)
        {
            return provider != null
                && string.Equals(provider.authMode, "oauth", StringComparison.OrdinalIgnoreCase);
        }

        // === Reconnect ===

        private int _reconnectAttempt;
        private float _reconnectDelay = 1f;
        private const float MaxReconnectDelay = 30f;
        private bool _shouldReconnect;
        private Coroutine _reconnectCoroutine;
        private bool _isSwitchingMode;

        // === Dependencies (set from AppBootstrap) ===

        private IAppSettingsRepository _settingsRepo;
        private ISecretStore _secretStore;
        private Func<ProviderConfig> _activeProviderResolver;

        /// <summary>
        /// Supplies the currently active provider so the Hermes transport can derive its
        /// WS URL/token from it. Set once from AppBootstrap.
        /// </summary>
        public void SetActiveProviderResolver(Func<ProviderConfig> resolver)
        {
            _activeProviderResolver = resolver;
        }

        private static bool IsHermesProviderConfig(ProviderConfig provider)
        {
            return provider != null
                && !string.IsNullOrWhiteSpace(provider.baseUrl)
                && string.Equals(provider.backendType, "hermes", StringComparison.OrdinalIgnoreCase);
        }

        // === Lifecycle ===

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void Initialize(
            string hermesWsUrl,
            string hermesRestUrl,
            IAppSettingsRepository settingsRepo,
            ISecretStore secretStore)
        {
            HermesWsUrl = hermesWsUrl;
            HermesRestUrl = hermesRestUrl;
            _settingsRepo = settingsRepo;
            _secretStore = secretStore;

            // Load saved token
            HermesToken = _secretStore?.GetSecret("hermes_token") ?? "";
        }

        /// <summary>
        /// Switch the backend mode. Disconnects the current transport and sets up the new one.
        /// Connection itself is driven by activating a provider (see ConnectHermes), so switching
        /// to Hermes without a configured provider does not spam failed-connect attempts.
        /// </summary>
        public async Task SetMode(BackendMode mode)
        {
            if (CurrentMode == mode)
                return;

            // Guard against re-entrancy: the dropdown can fire ChangeEvent again while we are
            // awaiting the disconnect below, which previously nulled ActiveTransport mid-flight.
            if (_isSwitchingMode)
                return;
            _isSwitchingMode = true;

            try
            {
                NeonLogger.Log("[Backend] Switching from " + CurrentMode + " to " + mode);

                // Stop reconnect attempts for old mode
                StopReconnect();

                // Disconnect current transport. Capture it locally so a concurrent path nulling
                // the field during the await can't turn Dispose() into a NullReferenceException.
                var transport = ActiveTransport;
                if (transport != null)
                {
                    transport.OnStateChanged -= HandleTransportStateChanged;
                    try { await transport.Disconnect(); }
                    catch (Exception ex) { Debug.LogWarning("[Backend] Disconnect error: " + ex.Message); }
                    transport.Dispose();
                    if (ReferenceEquals(ActiveTransport, transport))
                        ActiveTransport = null;
                }

                CleanupHermes();
                CurrentMode = mode;

                if (mode == BackendMode.Hermes)
                    SetupHermes();

                SaveSettings();
                OnModeChanged?.Invoke(mode);
                NeonLogger.Log("[Backend] Mode set to " + mode);
            }
            finally
            {
                _isSwitchingMode = false;
            }
        }

        /// <summary>
        /// Connect the Hermes backend. Called after SetMode(Hermes) or on app start.
        /// </summary>
        public async System.Threading.Tasks.Task ConnectHermes()
        {
            if (CurrentMode != BackendMode.Hermes || SessionManager == null)
                return;
            if (SessionManager.IsConnected)
                return;

            // Pull the WS URL/token from the active Hermes provider so every connect uses the
            // currently selected provider — not a stale default.
            var activeProvider = _activeProviderResolver != null ? _activeProviderResolver() : null;
            if (IsHermesProviderConfig(activeProvider))
            {
                ConfigureHermesEndpoint(activeProvider.baseUrl, activeProvider.apiKey);
            }
            else
            {
                LastConnectionError = null;
                NeonLogger.Log("[Backend] No active Hermes provider — connect skipped.");
                return;
            }

            _shouldReconnect = true;

            bool oauth = IsOAuthProvider(activeProvider);

            try
            {
                if (oauth)
                {
                    // Desktop-style: ensure a cookie session, mint a single-use ws-ticket, then
                    // connect the WS with ?ticket=. The ticket is never stored.
                    string ticket = await EnsureOAuthTicketAsync(activeProvider);
                    await SessionManager.Connect(HermesWsUrl, null, ticket);
                }
                else
                {
                    await SessionManager.Connect(HermesWsUrl, HermesToken);
                }
                LastConnectionError = null;
                _reconnectAttempt = 0;
                _reconnectDelay = 1f;
                NeonLogger.Log("[Backend] Hermes connected");
            }
            catch (HermesReauthRequiredException reauth)
            {
                // Credential problem, not a transport blip. Surface a clear, stable message and do
                // NOT spin the reconnect loop — nothing will change until the user signs in again.
                LastConnectionError = "Hermes sign-in required (" + reauth.Reason + ")";
                Debug.LogWarning("[Backend] Hermes reauth required: " + reauth.Reason);
                OnError?.Invoke(LastConnectionError);
            }
            catch (Exception ex)
            {
                LastConnectionError = ex.Message;
                Debug.LogError("[Backend] Hermes connect failed: " + ex.Message);
                OnError?.Invoke("Hermes connection failed: " + ex.Message);
                ScheduleReconnect();
            }
        }

        /// <summary>
        /// Ensure a live cookie session for an OAuth provider (logging in with stored credentials
        /// when needed) and mint a fresh single-use ws-ticket. Throws
        /// <see cref="HermesReauthRequiredException"/> when no session can be established.
        /// </summary>
        private async Task<string> EnsureOAuthTicketAsync(ProviderConfig provider)
        {
            if (_remoteAuth == null)
                throw new HermesReauthRequiredException("no_cookie", "Remote auth not initialized.");

            if (!_remoteAuth.HasSession)
            {
                // Try a stored-credential (basic-auth) login. A full-OAuth session that was pasted
                // in via SetSessionCookie already set HasSession, so we only reach here when there
                // is genuinely nothing to authenticate with.
                string password = _secretStore != null ? _secretStore.GetSecret(PasswordSecretKey(provider.id)) : null;
                if (!string.IsNullOrEmpty(provider.authProvider)
                    && !string.IsNullOrEmpty(provider.authUsername)
                    && !string.IsNullOrEmpty(password))
                {
                    await _remoteAuth.PasswordLoginAsync(provider.authProvider, provider.authUsername, password);
                }
                else
                {
                    _remoteAuth.MarkReauthRequired("no_cookie");
                    throw new HermesReauthRequiredException(
                        "no_cookie",
                        "No Hermes session — sign in to the remote gateway first.");
                }
            }

            return await _remoteAuth.MintWsTicketAsync();
        }

        /// <summary>
        /// Programmatic sign-in for an OAuth/basic-auth Hermes provider: authenticate with a
        /// username/password, cache the password for transparent reconnects, and (re)connect.
        /// The password is stored only in the secret store — never in provider config, never
        /// logged. Returns true on a successful session; false surfaces via LastConnectionError.
        /// </summary>
        public async Task<bool> HermesPasswordLoginAsync(string username, string password)
        {
            if (CurrentMode != BackendMode.Hermes || _remoteAuth == null)
            {
                LastConnectionError = "Hermes backend is not active.";
                return false;
            }

            var activeProvider = _activeProviderResolver != null ? _activeProviderResolver() : null;
            if (!IsHermesProviderConfig(activeProvider) || !IsOAuthProvider(activeProvider))
            {
                LastConnectionError = "Active provider is not an OAuth Hermes provider.";
                return false;
            }

            // Ensure the remote auth targets the active provider's endpoint before logging in.
            ConfigureHermesEndpoint(activeProvider.baseUrl, activeProvider.apiKey);

            try
            {
                await _remoteAuth.PasswordLoginAsync(activeProvider.authProvider, username, password);
            }
            catch (Exception ex)
            {
                LastConnectionError = ex.Message;
                Debug.LogWarning("[Backend] Hermes login failed: " + ex.Message);
                OnError?.Invoke(LastConnectionError);
                return false;
            }

            // Cache the password (secret store only) so ConnectHermes can re-login on reconnect,
            // and remember the username in config for the same reason.
            _secretStore?.SetSecret(PasswordSecretKey(activeProvider.id), password);
            activeProvider.authUsername = username;

            await ReconnectHermes();
            return _remoteAuth.State == HermesAuthState.Authenticated;
        }

        /// <summary>
        /// Adopt a session cookie obtained from a full browser OAuth sign-in (out of scope to run
        /// the interactive IDP redirect inside the app). Not persisted. Follow with ConnectHermes.
        /// </summary>
        public void SetHermesSessionCookie(string cookie)
        {
            _remoteAuth?.SetSessionCookie(cookie);
        }

        /// <summary>
        /// Sign out of the remote (cookie) session: forget the in-memory cookie, drop the cached
        /// password for the active provider, and disconnect the transport. Token mode is untouched.
        /// Safe to call in any mode. Stops the reconnect loop so a deliberate sign-out does not
        /// immediately reconnect.
        /// </summary>
        public async System.Threading.Tasks.Task ClearHermesRemoteSession()
        {
            StopReconnect();

            _remoteAuth?.Clear();

            var activeProvider = _activeProviderResolver != null ? _activeProviderResolver() : null;
            if (activeProvider != null && _secretStore != null)
                _secretStore.DeleteSecret(PasswordSecretKey(activeProvider.id));

            LastConnectionError = null;

            if (SessionManager != null && SessionManager.IsConnected)
            {
                try { await SessionManager.Disconnect(); }
                catch (Exception ex) { Debug.LogWarning("[Backend] Clear-session disconnect error: " + ex.Message); }
            }
        }

        /// <summary>
        /// Disconnect (if connected) and connect again — used after the active Hermes provider's
        /// URL or key changes so the new endpoint takes effect immediately.
        /// </summary>
        public async System.Threading.Tasks.Task ReconnectHermes()
        {
            if (CurrentMode != BackendMode.Hermes || SessionManager == null)
                return;

            if (SessionManager.IsConnected)
            {
                try { await SessionManager.Disconnect(); }
                catch (Exception ex) { Debug.LogWarning("[Backend] Reconnect disconnect error: " + ex.Message); }
            }

            await ConnectHermes();
        }

        /// <summary>
        /// Save the Hermes API token to secrets store.
        /// </summary>
        public void SetToken(string token)
        {
            HermesToken = token ?? "";
            _secretStore?.SetSecret("hermes_token", HermesToken);

            // Update REST client
            if (RestClient != null)
                RestClient.Configure(HermesRestUrl, HermesToken);
        }

        /// <summary>
        /// Derive the Hermes WS/REST endpoint and token from the active Hermes provider.
        /// The provider's baseUrl becomes the WebSocket URL; its apiKey becomes the token.
        /// Call this before connecting so the transport targets the right server.
        /// </summary>
        public void ConfigureHermesEndpoint(string baseUrl, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                HermesWsUrl = BuildHermesWsUrl(baseUrl);
                HermesRestUrl = NormalizeRestUrl(baseUrl);
                if (RestClient != null)
                    RestClient.Configure(HermesRestUrl, HermesToken);
                if (_remoteAuth != null)
                    _remoteAuth.Configure(HermesRestUrl);
            }

            if (apiKey != null)
                SetToken(apiKey);
        }

        /// <summary>
        /// Convert a provider base URL (e.g. https://example.com) into a Hermes
        /// WebSocket URL (wss://example.com/api/ws).
        /// </summary>
        public static string BuildHermesWsUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "";

            string url = baseUrl.Trim();
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "wss://" + url.Substring("https://".Length);
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "ws://" + url.Substring("http://".Length);
            else if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                  && !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                url = "wss://" + url;

            url = url.TrimEnd('/');
            if (url.IndexOf("/api/ws", StringComparison.OrdinalIgnoreCase) < 0)
                url = url + "/api/ws";

            return url;
        }

        private static string NormalizeRestUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "";
            return baseUrl.Trim().TrimEnd('/');
        }

        /// <summary>
        /// Load backend mode from saved settings and apply it.
        /// </summary>
        public void LoadFromSettings(AppSettings settings)
        {
            if (settings == null)
                return;

            if (!string.IsNullOrEmpty(settings.hermesWsUrl))
                HermesWsUrl = settings.hermesWsUrl;
            if (!string.IsNullOrEmpty(settings.hermesRestUrl))
                HermesRestUrl = settings.hermesRestUrl;

            BackendMode mode = BackendMode.OpenAI;
            if (string.Equals(settings.backendMode, "hermes", StringComparison.OrdinalIgnoreCase))
                mode = BackendMode.Hermes;

            // Apply without triggering events (initial load)
            CurrentMode = mode;
            if (mode == BackendMode.Hermes)
                SetupHermes();
        }

        // === Reconnect ===

        private void ScheduleReconnect()
        {
            if (!_shouldReconnect || CurrentMode != BackendMode.Hermes)
                return;
            if (_reconnectCoroutine != null)
                return;

            _reconnectCoroutine = StartCoroutine(ReconnectLoop());
        }

        private void StopReconnect()
        {
            _shouldReconnect = false;
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
        }

        private IEnumerator ReconnectLoop()
        {
            while (_shouldReconnect && CurrentMode == BackendMode.Hermes)
            {
                yield return new WaitForSeconds(_reconnectDelay);

                if (SessionManager != null && !SessionManager.IsConnected)
                {
                    NeonLogger.Log("[Backend] Reconnect attempt " + (_reconnectAttempt + 1));
                    _ = ConnectHermes();

                    _reconnectAttempt++;
                    _reconnectDelay = Mathf.Min(_reconnectDelay * 2f, MaxReconnectDelay);
                }

                _reconnectCoroutine = null;
                yield break;
            }
            _reconnectCoroutine = null;
        }

        private void HandleTransportStateChanged(TransportState state)
        {
            OnConnectionStateChanged?.Invoke(state);

            if (state == TransportState.Disconnected || state == TransportState.Error)
            {
                if (_shouldReconnect && CurrentMode == BackendMode.Hermes)
                    ScheduleReconnect();
            }
        }

        // === Internal ===

        private void SetupHermes()
        {
            Gateway = new HermesGateway
            {
                RequestTimeoutMs = 30000,
                RequestIdPrefix = "r"
            };

            SessionManager = new HermesSessionManager(Gateway);
            ActiveTransport = SessionManager;
            ActiveTransport.OnStateChanged += HandleTransportStateChanged;

            _remoteAuth = new HermesRemoteAuth(HermesRestUrl);
            RestClient = new HermesRestClient(HermesRestUrl, HermesToken, _remoteAuth);
        }

        private void CleanupHermes()
        {
            Gateway?.Dispose();
            Gateway = null;
            SessionManager = null;
            RestClient = null;
            _remoteAuth = null;
        }

        private void SaveSettings()
        {
            if (_settingsRepo == null)
                return;

            var settings = _settingsRepo.Load();
            if (settings == null)
                return;

            settings.backendMode = CurrentMode == BackendMode.Hermes ? "hermes" : "openai";
            settings.hermesWsUrl = HermesWsUrl;
            settings.hermesRestUrl = HermesRestUrl;
            _settingsRepo.Save(settings);
        }

        private void OnDestroy()
        {
            StopReconnect();
            CleanupHermes();
            ActiveTransport?.Dispose();
            ActiveTransport = null;
            _instance = null;
        }
    }
}
