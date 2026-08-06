// AndroidDeepLinkReceiver.cs — Intercepts App Link / deep link callbacks on Android.
//
// Unity's Application.deepLinkActivated fires when an intent-filter catches a URL.
// This MonoBehaviour subscribes to that event and caches the URL so the auth flow
// (HermesAppLinkAuth) can pick it up. Mounted on a persistent GameObject.
//
// On non-Android platforms, IsAvailable is false and nothing happens.

using System;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>
    /// Captures Android deep-link / App Link callbacks (intent-filter VIEW).
    /// Singleton-style: one persistent instance, poll via TryConsumeDeepLink.
    /// </summary>
    public class AndroidDeepLinkReceiver : MonoBehaviour
    {
        private static AndroidDeepLinkReceiver _instance;

        // The deep-link URL that was captured, or null.
        private string _capturedUrl;
        private bool _captured;

        /// <summary>True on Android (deep links are the transport for App Link OAuth).</summary>
        public static bool IsPlatformSupported
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Get the singleton instance, creating a persistent GameObject if needed.
        /// Returns null on non-Android platforms.
        /// </summary>
        public static AndroidDeepLinkReceiver Instance
        {
            get
            {
                if (!IsPlatformSupported)
                    return null;

                if (_instance != null)
                    return _instance;

                var go = GameObject.Find("__DeepLinkReceiver");
                if (go == null)
                {
                    go = new GameObject("__DeepLinkReceiver");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<AndroidDeepLinkReceiver>();
                }
                else
                {
                    _instance = go.GetComponent<AndroidDeepLinkReceiver>();
                    if (_instance == null)
                        _instance = go.AddComponent<AndroidDeepLinkReceiver>();
                }

                return _instance;
            }
        }

        /// <summary>True if a deep-link URL has been captured and not yet consumed.</summary>
        public bool HasPendingDeepLink => _captured && !string.IsNullOrEmpty(_capturedUrl);

        /// <summary>
        /// Consume and return the captured deep-link URL, clearing the cache.
        /// Returns null if nothing is pending.
        /// </summary>
        public string TryConsumeDeepLink()
        {
            if (!_captured || string.IsNullOrEmpty(_capturedUrl))
                return null;

            string url = _capturedUrl;
            _capturedUrl = null;
            _captured = false;
            return url;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void OnEnable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Subscribe to deep-link activation. Also check if the app was launched
            // via a deep link (URL already present at startup).
            Application.deepLinkActivated += OnDeepLinkActivated;

            if (!string.IsNullOrEmpty(Application.absoluteURL))
            {
                // App was cold-started from a deep link.
                OnDeepLinkActivated(Application.absoluteURL);
            }
#endif
        }

        private void OnDisable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Application.deepLinkActivated -= OnDeepLinkActivated;
#endif
        }

        private void OnDeepLinkActivated(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;

            Debug.Log($"[NeonCompanion] Deep link received: {url}");
            _capturedUrl = url;
            _captured = true;
        }
    }
}
