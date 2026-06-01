// GlobalBackendSelector.cs - Global backend mode selector
// Determines whether the app runs in Hermes or OpenAI mode.
// Controls feature gating, transport selection, and connection lifecycle.

using System;
using System.Collections;
using System.Collections.Generic;
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
        public string HermesWsUrl = "wss://neon-dev.top/api/ws";
        public string HermesRestUrl = "https://neon-dev.top";
        public string HermesToken;

        // === State ===

        public BackendMode CurrentMode { get; private set; } = BackendMode.OpenAI;

        // === Events ===

        public event Action<BackendMode> OnModeChanged;
        public event Action<string> OnError;
        public event Action<TransportState> OnConnectionStateChanged;

        // === Feature Gate ===

        private static readonly HashSet<string> HermesOnlyFeatures = new HashSet<string>
        {
            "sessions", "tools", "kanban", "cron", "skills",
            "reasoning", "approval", "shell",
        };

        public bool IsFeatureAvailable(string feature)
        {
            if (CurrentMode == BackendMode.Hermes)
                return true;
            return !HermesOnlyFeatures.Contains(feature);
        }

        public bool IsHermesOnly(string feature)
        {
            return HermesOnlyFeatures.Contains(feature);
        }

        // === Transport Access ===

        public IChatTransport ActiveTransport { get; private set; }
        public HermesRestClient RestClient { get; private set; }
        public HermesGateway Gateway { get; private set; }
        public HermesSessionManager SessionManager { get; private set; }

        // === Reconnect ===

        private int _reconnectAttempt;
        private float _reconnectDelay = 1f;
        private const float MaxReconnectDelay = 30f;
        private bool _shouldReconnect;
        private Coroutine _reconnectCoroutine;

        // === Dependencies (set from AppBootstrap) ===

        private IAppSettingsRepository _settingsRepo;
        private ISecretStore _secretStore;

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
        /// Switch the backend mode. Disconnects current transport, creates new one, auto-connects.
        /// </summary>
        public async void SetMode(BackendMode mode)
        {
            if (CurrentMode == mode)
                return;

            NeonLogger.Log("[Backend] Switching from " + CurrentMode + " to " + mode);

            // Stop reconnect attempts for old mode
            StopReconnect();

            // Disconnect current transport
            if (ActiveTransport != null)
            {
                ActiveTransport.OnStateChanged -= HandleTransportStateChanged;
                try { await ActiveTransport.Disconnect(); }
                catch (Exception ex) { Debug.LogWarning("[Backend] Disconnect error: " + ex.Message); }
                ActiveTransport.Dispose();
                ActiveTransport = null;
            }

            CleanupHermes();
            CurrentMode = mode;

            if (mode == BackendMode.Hermes)
            {
                SetupHermes();
                await ConnectHermes();
            }

            SaveSettings();
            OnModeChanged?.Invoke(mode);
            NeonLogger.Log("[Backend] Mode set to " + mode);
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

            _shouldReconnect = true;

            try
            {
                await SessionManager.Connect(HermesWsUrl, HermesToken);
                _reconnectAttempt = 0;
                _reconnectDelay = 1f;
                NeonLogger.Log("[Backend] Hermes connected");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Backend] Hermes connect failed: " + ex.Message);
                OnError?.Invoke("Hermes connection failed: " + ex.Message);
                ScheduleReconnect();
            }
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

            RestClient = new HermesRestClient(HermesRestUrl, HermesToken);
        }

        private void CleanupHermes()
        {
            Gateway?.Dispose();
            Gateway = null;
            SessionManager = null;
            RestClient = null;
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
