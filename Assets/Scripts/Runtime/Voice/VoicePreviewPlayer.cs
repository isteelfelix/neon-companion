using System;
using System.Collections;
using NeonCompanion.Runtime.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Voice
{
    /// <summary>
    /// Lightweight MonoBehaviour that loads a WAV file from disk and plays it through its own
    /// AudioSource. Used both for composer voice preview and chat-bubble replay.
    /// </summary>
    public sealed class VoicePreviewPlayer : MonoBehaviour
    {
        private AudioSource _src;
        private AudioClip _clip;
        private Coroutine _loadCoroutine;

        public bool IsPlaying => _src != null && _src.isPlaying;

        public event Action OnPlaybackComplete;

        private void Awake()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
        }

        private void OnDestroy()
        {
            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                _loadCoroutine = null;
            }
            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }
        }

        /// <summary>Play the WAV at <paramref name="wavPath"/> (absolute file path).</summary>
        public void Play(string wavPath, float volume = 1f)
        {
            if (string.IsNullOrEmpty(wavPath) || !System.IO.File.Exists(wavPath))
            {
                NeonLogger.LogWarning("VoicePreviewPlayer: file not found: " + wavPath);
                OnPlaybackComplete?.Invoke();
                return;
            }

            if (_loadCoroutine != null)
                StopCoroutine(_loadCoroutine);
            _src.Stop();

            _loadCoroutine = StartCoroutine(LoadAndPlay(wavPath, volume));
        }

        public void Stop()
        {
            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                _loadCoroutine = null;
            }
            _src.Stop();
        }

        private IEnumerator LoadAndPlay(string wavPath, float volume)
        {
            string uri = "file://" + wavPath;
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    if (_clip != null)
                        Destroy(_clip);

                    _clip = DownloadHandlerAudioClip.GetContent(req);
                    if (_clip != null)
                    {
                        _src.volume = volume;
                        _src.clip   = _clip;
                        _src.Play();

                        while (_src.isPlaying)
                            yield return null;
                    }
                }
                else
                {
                    NeonLogger.LogWarning("VoicePreviewPlayer: failed to load " + wavPath + " — " + req.error);
                }
            }

            _loadCoroutine = null;
            OnPlaybackComplete?.Invoke();
        }
    }
}
