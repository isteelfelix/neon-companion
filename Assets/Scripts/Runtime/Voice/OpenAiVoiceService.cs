using System;
using System.Collections;
using NeonCompanion.Runtime.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Voice
{
    public sealed class OpenAiVoiceService : MonoBehaviour, IVoiceService
    {
        private string _baseUrl;
        private string _apiKey;
        private string _sttLanguage;
        private string _ttsVoice;
        private string _ttsModel;
        private float _ttsSpeed;
        private string _inputDeviceName;

        private AudioSource _audioSource;
        private AudioClip _recordingClip;
        private string _activeDevice;
        private bool _isRecording;
        private bool _isSpeaking;
        private Coroutine _playbackCoroutine;

        public bool IsRecording => _isRecording;
        public bool IsSpeaking => _isSpeaking;
        public bool IsAvailable => true;

        public event Action<string> OnSpeechRecognized;
        public event Action OnPlaybackComplete;

        public void Initialize(
            string baseUrl,
            string apiKey,
            string sttLanguage,
            string ttsVoice,
            string ttsModel,
            float ttsSpeed,
            string inputDeviceName,
            float outputVolume)
        {
            _baseUrl = baseUrl != null ? baseUrl.TrimEnd('/') : "";
            _apiKey = apiKey ?? "";
            _sttLanguage = sttLanguage;
            _ttsVoice = ttsVoice;
            _ttsModel = ttsModel;
            _ttsSpeed = ttsSpeed > 0f ? ttsSpeed : 1f;
            _inputDeviceName = inputDeviceName;

            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
                if (_audioSource == null)
                    _audioSource = gameObject.AddComponent<AudioSource>();
            }
            _audioSource.volume = outputVolume;
        }

        public void StartRecording()
        {
            if (_isRecording)
                return;
            _activeDevice = FindInputDevice(_inputDeviceName);
            _recordingClip = Microphone.Start(_activeDevice, true, 60, 16000);
            _isRecording = true;
        }

        public byte[] StopRecording()
        {
            if (!_isRecording)
                return new byte[0];

            int pos = Microphone.GetPosition(_activeDevice);
            Microphone.End(_activeDevice);
            _isRecording = false;

            if (_recordingClip == null || pos <= 0)
                return new byte[0];

            float[] samples = new float[pos * _recordingClip.channels];
            _recordingClip.GetData(samples, 0);
            byte[] wav = BuildWav(samples, 1, 16000, 16);
            StartCoroutine(TranscribeCoroutine(wav));
            return wav;
        }

        public void Speak(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (_playbackCoroutine != null)
                StopCoroutine(_playbackCoroutine);
            _playbackCoroutine = StartCoroutine(SpeakCoroutine(text));
        }

        public void StopSpeaking()
        {
            if (_playbackCoroutine != null)
            {
                StopCoroutine(_playbackCoroutine);
                _playbackCoroutine = null;
            }
            if (_audioSource != null)
                _audioSource.Stop();
            if (_isSpeaking)
            {
                _isSpeaking = false;
                OnPlaybackComplete?.Invoke();
            }
        }

        // ============================================================
        // Coroutines
        // ============================================================

        private IEnumerator TranscribeCoroutine(byte[] wavBytes)
        {
            string url = _baseUrl + "/v1/audio/transcriptions";
            var form = new WWWForm();
            form.AddBinaryData("file", wavBytes, "audio.wav", "audio/wav");
            form.AddField("model", "whisper-1");
            if (!string.IsNullOrEmpty(_sttLanguage))
                form.AddField("language", _sttLanguage);

            using (UnityWebRequest request = UnityWebRequest.Post(url, form))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    TranscriptionResponse resp = JsonUtility.FromJson<TranscriptionResponse>(request.downloadHandler.text);
                    string text = (resp != null && resp.text != null) ? resp.text : "";
                    OnSpeechRecognized?.Invoke(text);
                }
                else
                {
                    NeonLogger.LogWarning("OpenAiVoiceService STT error: " + request.error);
                    OnSpeechRecognized?.Invoke("");
                }
            }
        }

        private IEnumerator SpeakCoroutine(string text)
        {
            string url = _baseUrl + "/v1/audio/speech";
            string model = string.IsNullOrEmpty(_ttsModel) ? "tts-1" : _ttsModel;
            string voice = string.IsNullOrEmpty(_ttsVoice) ? "nova" : _ttsVoice;
            string speedStr = _ttsSpeed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string json = "{\"model\":" + JsonQuote(model) + ",\"voice\":" + JsonQuote(voice)
                + ",\"input\":" + JsonQuote(text) + ",\"speed\":" + speedStr + "}";
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(json);

            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.uploadHandler.contentType = "application/json";
                request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.MPEG);
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        _isSpeaking = true;
                        _audioSource.PlayOneShot(clip);
                        yield return null;
                        while (_audioSource != null && _audioSource.isPlaying)
                            yield return null;
                    }
                }
                else
                {
                    NeonLogger.LogWarning("OpenAiVoiceService TTS error: " + request.error);
                }
            }

            _isSpeaking = false;
            _playbackCoroutine = null;
            OnPlaybackComplete?.Invoke();
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static string FindInputDevice(string deviceName)
        {
            string[] devices = Microphone.devices;
            if (!string.IsNullOrEmpty(deviceName))
            {
                foreach (string d in devices)
                {
                    if (string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
            return devices.Length > 0 ? devices[0] : null;
        }

        private static string JsonQuote(string s)
        {
            if (s == null) return "null";
            s = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                 .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
            return "\"" + s + "\"";
        }

        private static byte[] BuildWav(float[] samples, int channels, int sampleRate, int bitsPerSample)
        {
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            int dataSize = samples.Length * (bitsPerSample / 8);
            byte[] wav = new byte[44 + dataSize];
            int o = 0;

            WriteStr(wav, o, "RIFF");           o += 4;
            WriteI32(wav, o, 36 + dataSize);    o += 4;
            WriteStr(wav, o, "WAVE");           o += 4;
            WriteStr(wav, o, "fmt ");           o += 4;
            WriteI32(wav, o, 16);               o += 4;
            WriteI16(wav, o, 1);                o += 2;  // PCM format
            WriteI16(wav, o, (short)channels);  o += 2;
            WriteI32(wav, o, sampleRate);       o += 4;
            WriteI32(wav, o, byteRate);         o += 4;
            WriteI16(wav, o, (short)blockAlign); o += 2;
            WriteI16(wav, o, (short)bitsPerSample); o += 2;
            WriteStr(wav, o, "data");           o += 4;
            WriteI32(wav, o, dataSize);         o += 4;

            for (int i = 0; i < samples.Length; i++)
            {
                short pcm = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767);
                wav[o++] = (byte)(pcm & 0xFF);
                wav[o++] = (byte)((pcm >> 8) & 0xFF);
            }

            return wav;
        }

        private static void WriteStr(byte[] b, int o, string s)
        {
            for (int i = 0; i < s.Length; i++)
                b[o + i] = (byte)s[i];
        }

        private static void WriteI32(byte[] b, int o, int v)
        {
            b[o]     = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void WriteI16(byte[] b, int o, short v)
        {
            b[o]     = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        // ============================================================
        // Response types
        // ============================================================

        [Serializable]
        private class TranscriptionResponse
        {
            public string text;
        }
    }
}
