using System;
using System.Collections;
using NeonCompanion.Runtime.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Voice
{
    public struct VoicePlaybackState
    {
        public bool IsCurrent;
        public bool IsPlaying;
        public bool IsPaused;
        public bool IsLoading;
        public float PositionSecs;
        public float DurationSecs;
        /// <summary>Loudness of the moment being played, 0..1. Drives the bubble footer droplets.</summary>
        public float Level;
    }

    /// <summary>
    /// Lightweight MonoBehaviour that loads a WAV file from disk and plays it through its own
    /// AudioSource. Used both for composer voice preview and chat-bubble replay.
    /// </summary>
    public sealed class VoicePreviewPlayer : MonoBehaviour
    {
        private AudioSource _src;
        private AudioClip _clip;
        private Coroutine _loadCoroutine;
        private string _activePath;
        private bool _paused;
        private float _pendingSeekNormalized = -1f;
        // Reused so sampling the level never allocates; 128 samples is plenty for an envelope.
        private readonly float[] _levelBuffer = new float[128];

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
            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }
            _paused = false;
            _activePath = wavPath;

            _loadCoroutine = StartCoroutine(LoadAndPlay(wavPath, volume));
        }

        public void Toggle(string wavPath, float volume = 1f)
        {
            if (!string.Equals(_activePath, wavPath, StringComparison.Ordinal) || _clip == null)
            {
                Play(wavPath, volume);
                return;
            }

            if (_paused)
            {
                _paused = false;
                _src.UnPause();
                return;
            }

            if (_src.isPlaying)
            {
                _src.Pause();
                _paused = true;
                return;
            }

            Play(wavPath, volume);
        }

        public void SeekNormalized(string wavPath, float normalized)
        {
            float clamped = Mathf.Clamp01(normalized);
            if (!string.Equals(_activePath, wavPath, StringComparison.Ordinal) || _clip == null)
            {
                _pendingSeekNormalized = clamped;
                Play(wavPath);
                return;
            }

            if (_loadCoroutine == null && !_src.isPlaying && !_paused)
            {
                _pendingSeekNormalized = clamped;
                Play(wavPath);
                return;
            }

            _src.time = _clip.length * clamped;
            if (!_src.isPlaying && !_paused)
                _src.Play();
        }

        public VoicePlaybackState GetState(string wavPath)
        {
            bool isCurrent = !string.IsNullOrEmpty(wavPath) &&
                             string.Equals(_activePath, wavPath, StringComparison.Ordinal);
            return new VoicePlaybackState
            {
                IsCurrent = isCurrent,
                IsPlaying = isCurrent && _src != null && _src.isPlaying,
                IsPaused = isCurrent && _paused,
                IsLoading = isCurrent && _loadCoroutine != null && _clip == null,
                PositionSecs = isCurrent && _src != null ? _src.time : 0f,
                DurationSecs = isCurrent && _clip != null ? _clip.length : 0f,
                Level = isCurrent ? SampleLevel() : 0f
            };
        }

        /// <summary>
        /// RMS of the output buffer, boosted into a usable 0..1 range — speech sits far below full
        /// scale, so raw RMS would barely move anything. Sampled on demand from GetState rather than
        /// polled, so it costs nothing while no clip is playing.
        /// </summary>
        private float SampleLevel()
        {
            if (_src == null || !_src.isPlaying)
                return 0f;

            float sum = 0f;
            _src.GetOutputData(_levelBuffer, 0);
            for (int i = 0; i < _levelBuffer.Length; i++)
                sum += _levelBuffer[i] * _levelBuffer[i];

            float rms = Mathf.Sqrt(sum / _levelBuffer.Length);
            return Mathf.Clamp01(rms * 4.5f);
        }

        public void Stop()
        {
            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                _loadCoroutine = null;
            }
            _src.Stop();
            _paused = false;
            _activePath = null;
            _pendingSeekNormalized = -1f;
        }

        private IEnumerator LoadAndPlay(string wavPath, float volume)
        {
            string uri = "file://" + wavPath;
            // User recordings are WAV; assistant TTS clips are MP3 — pick the decoder by extension.
            AudioType audioType = wavPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? AudioType.MPEG
                : AudioType.WAV;
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
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
                        if (_pendingSeekNormalized >= 0f)
                        {
                            _src.time = _clip.length * _pendingSeekNormalized;
                            _pendingSeekNormalized = -1f;
                        }
                        _src.Play();

                        while (_src.isPlaying || _paused)
                            yield return null;
                    }
                }
                else
                {
                    NeonLogger.LogWarning("VoicePreviewPlayer: failed to load " + wavPath + " — " + req.error);
                }
            }

            _loadCoroutine = null;
            _paused = false;
            OnPlaybackComplete?.Invoke();
        }
    }
}
