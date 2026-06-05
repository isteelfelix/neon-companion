using System;
using System.Collections;
using NeonCompanion.Runtime.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Voice
{
    public sealed class HermesVoiceService : MonoBehaviour, IVoiceService
    {
        private string _hermesRestUrl;
        private string _inputDeviceName;

        private AudioSource _audioSource;
        private AudioClip _recordingClip;
        private string _activeDevice;
        private bool _isRecording;
        private bool _isSpeaking;
        private Coroutine _playbackCoroutine;
        private string _tempFilePath;

        public bool IsRecording => _isRecording;
        public bool IsSpeaking => _isSpeaking;
        public bool IsAvailable => true;

        public event Action<string> OnSpeechRecognized;
        public event Action OnPlaybackComplete;

        public void Initialize(string hermesRestUrl, string inputDeviceName, float outputVolume)
        {
            _hermesRestUrl = hermesRestUrl != null ? hermesRestUrl.TrimEnd('/') : "";
            _inputDeviceName = inputDeviceName;

            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
                if (_audioSource == null)
                    _audioSource = gameObject.AddComponent<AudioSource>();
            }
            _audioSource.volume = outputVolume;
        }

        private void OnDestroy()
        {
            if (!string.IsNullOrEmpty(_tempFilePath) && System.IO.File.Exists(_tempFilePath))
                System.IO.File.Delete(_tempFilePath);
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
            string url = _hermesRestUrl + "/api/audio/transcribe";
            string base64 = System.Convert.ToBase64String(wavBytes);
            string json = "{\"data_url\":\"data:audio/wav;base64," + base64 + "\",\"mime_type\":\"audio/wav\"}";
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(json);

            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.uploadHandler.contentType = "application/json";
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    HermesTranscribeResponse resp = JsonUtility.FromJson<HermesTranscribeResponse>(request.downloadHandler.text);
                    string text = (resp != null && resp.ok && resp.transcript != null) ? resp.transcript : "";
                    OnSpeechRecognized?.Invoke(text);
                }
                else
                {
                    NeonLogger.LogWarning("HermesVoiceService STT error: " + request.error);
                    OnSpeechRecognized?.Invoke("");
                }
            }
        }

        private IEnumerator SpeakCoroutine(string text)
        {
            string url = _hermesRestUrl + "/api/audio/speak";
            string json = "{\"text\":" + JsonQuote(text) + "}";
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(json);

            HermesSpeakResponse resp = null;

            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.uploadHandler.contentType = "application/json";
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                    resp = JsonUtility.FromJson<HermesSpeakResponse>(request.downloadHandler.text);
                else
                    NeonLogger.LogWarning("HermesVoiceService TTS error: " + request.error);
            }

            if (resp != null && resp.ok && !string.IsNullOrEmpty(resp.data_url))
            {
                int commaIdx = resp.data_url.IndexOf(',');
                if (commaIdx >= 0)
                {
                    byte[] audioBytes = null;
                    try
                    {
                        audioBytes = System.Convert.FromBase64String(resp.data_url.Substring(commaIdx + 1));
                    }
                    catch (Exception ex)
                    {
                        NeonLogger.LogWarning("HermesVoiceService TTS base64 decode error: " + ex.Message);
                    }

                    if (audioBytes != null)
                    {
                        _tempFilePath = System.IO.Path.Combine(Application.temporaryCachePath, "neon_hermes_tts.mp3");
                        System.IO.File.WriteAllBytes(_tempFilePath, audioBytes);

                        string fileUri = "file://" + _tempFilePath;
                        using (UnityWebRequest fileReq = new UnityWebRequest(fileUri, UnityWebRequest.kHttpVerbGET))
                        {
                            fileReq.downloadHandler = new DownloadHandlerAudioClip(fileUri, AudioType.MPEG);
                            yield return fileReq.SendWebRequest();

                            if (fileReq.result == UnityWebRequest.Result.Success)
                            {
                                AudioClip clip = DownloadHandlerAudioClip.GetContent(fileReq);
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
                                NeonLogger.LogWarning("HermesVoiceService TTS file load error: " + fileReq.error);
                            }
                        }
                    }
                }
                else
                {
                    NeonLogger.LogWarning("HermesVoiceService TTS: malformed data_url");
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

            WriteStr(wav, o, "RIFF");            o += 4;
            WriteI32(wav, o, 36 + dataSize);     o += 4;
            WriteStr(wav, o, "WAVE");            o += 4;
            WriteStr(wav, o, "fmt ");            o += 4;
            WriteI32(wav, o, 16);                o += 4;
            WriteI16(wav, o, 1);                 o += 2;  // PCM format
            WriteI16(wav, o, (short)channels);   o += 2;
            WriteI32(wav, o, sampleRate);        o += 4;
            WriteI32(wav, o, byteRate);          o += 4;
            WriteI16(wav, o, (short)blockAlign); o += 2;
            WriteI16(wav, o, (short)bitsPerSample); o += 2;
            WriteStr(wav, o, "data");            o += 4;
            WriteI32(wav, o, dataSize);          o += 4;

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
        private class HermesTranscribeResponse
        {
            public bool ok;
            public string transcript;
        }

        [Serializable]
        private class HermesSpeakResponse
        {
            public bool ok;
            public string data_url;
        }
    }
}
