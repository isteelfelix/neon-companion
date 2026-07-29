using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Voice;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NeonCompanion.Runtime.Platform
{
    public static class CompanionProcessMode
    {
        private const string PlayerFlag = "--companion-player";

        public static bool IsPlayerProcess
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                return Array.IndexOf(args, PlayerFlag) >= 0;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootstrapPlayer()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!IsPlayerProcess || UnityEngine.Object.FindAnyObjectByType<CompanionPlayerRuntime>() != null)
                return;

            var host = new GameObject("CompanionPlayerRuntime");
            host.AddComponent<CompanionPlayerRuntime>();
            UnityEngine.Object.DontDestroyOnLoad(host);
#endif
        }
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    public sealed class CompanionPlayerRuntime : MonoBehaviour
    {
        private readonly ConcurrentQueue<CompanionProcessMessage> _messages =
            new ConcurrentQueue<CompanionProcessMessage>();
        private readonly ConcurrentQueue<CompanionProcessMessage> _outgoing =
            new ConcurrentQueue<CompanionProcessMessage>();
        private readonly Dictionary<string, SpriteSheetAnimation> _clips =
            new Dictionary<string, SpriteSheetAnimation>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Sprite[]> _frames =
            new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private CancellationTokenSource _pipeCancellation;
        private Process _parent;
        private CompanionDisplaySnapshot _snapshot;
        private CompanionWindowPreferences _preferences = new CompanionWindowPreferences();
        private Sprite[] _activeFrames = Array.Empty<Sprite>();
        private Texture2D _staticTexture;
        private Avatar3DService _avatar3DService;
        private Avatar3DRenderer _avatar3DRenderer;
        private string _state = CompanionDisplayStates.Idle;
        private int _frameIndex;
        private float _nextFrameAt;
        private float _frameRate = 8f;
        private bool _pingPong;
        private bool _frameForward = true;
        private bool _connected;
        private bool _nativeApplied;
        private string _language;
        private bool _f12WasDown;
        private float _nextParentCheck;
        private float _nextBoundsReport;
        private float _nextHeartbeat;
        private int _loadVersion;
        private string _voiceText;
        private float _voiceStartedAt;
        private int _voiceCharIndex = -1;
        private Rect _hoverControlsRect = new Rect(8f, 8f, 264f, 34f);
        private Rect _contextMenuRect = new Rect(8f, 8f, 220f, 248f);
        private bool _contextMenuOpen;
        private bool _pointerInside;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
            ConfigureTransparentCamera();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            string pipeName = ArgumentValue("--companion-pipe");
            int parentPid;
            if (string.IsNullOrWhiteSpace(pipeName) ||
                !int.TryParse(ArgumentValue("--companion-parent-pid"), out parentPid))
            {
                NeonLogger.LogError("[CompanionPlayer] Missing isolated-process arguments.");
                Application.Quit(2);
                return;
            }

            try
            {
                _parent = Process.GetProcessById(parentPid);
            }
            catch
            {
                Application.Quit(3);
                return;
            }

            _ = ConnectAsync(pipeName);
        }

        private async Task ConnectAsync(string pipeName)
        {
            try
            {
                _pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(10000);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, true);
                _writer.AutoFlush = true;
                _connected = true;
                _pipeCancellation = new CancellationTokenSource();
                Task readTask = ReadLoopAsync(_pipeCancellation.Token);
                Task writeTask = WriteLoopAsync(_pipeCancellation.Token);
                Send("runtime_ready", "Display-only runtime active.");
                await readTask;
                _pipeCancellation.Cancel();
                await writeTask;
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("[CompanionPlayer] IPC connection failed: " + ex);
                Application.Quit(4);
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            using (var reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, true))
            {
                while (!token.IsCancellationRequested && _pipe != null && _pipe.IsConnected)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                        break;
                    try
                    {
                        CompanionProcessMessage message =
                            JsonUtility.FromJson<CompanionProcessMessage>(line);
                        if (message != null)
                            _messages.Enqueue(message);
                    }
                    catch (Exception ex)
                    {
                        NeonLogger.LogWarning("[CompanionPlayer] Rejected IPC message: " + ex.Message);
                    }
                }
            }

            if (this != null)
                Application.Quit();
        }

        private async Task WriteLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    CompanionProcessMessage message;
                    if (!_outgoing.TryDequeue(out message))
                    {
                        await Task.Delay(25, token);
                        continue;
                    }
                    await _writer.WriteLineAsync(JsonUtility.ToJson(message));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException ex)
            {
                NeonLogger.LogWarning("[CompanionPlayer] IPC writer closed: " + ex.Message);
            }
        }

        private void Update()
        {
            CompanionProcessMessage message;
            while (_messages.TryDequeue(out message))
                ApplyMessage(message);

            if (_connected && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + 2f;
                Send("heartbeat", null);
            }

            if (!_nativeApplied)
                _nativeApplied = WindowsCompanionWindowNative.Apply(_preferences);

            if (Time.unscaledTime >= _nextParentCheck)
            {
                _nextParentCheck = Time.unscaledTime + 1f;
                try
                {
                    if (_parent == null || _parent.HasExited)
                    {
                        Application.Quit();
                        return;
                    }
                }
                catch
                {
                    Application.Quit();
                    return;
                }
            }

            bool f12Down = WindowsCompanionWindowNative.IsKeyDown(0x7B);
            bool emergencyChord = f12Down &&
                WindowsCompanionWindowNative.IsKeyDown(0x11) &&
                WindowsCompanionWindowNative.IsKeyDown(0x10);
            if (_preferences.clickThrough && emergencyChord && !_f12WasDown)
            {
                _preferences.clickThrough = false;
                WindowsCompanionWindowNative.SetClickThrough(false);
                Send(new CompanionProcessMessage { type = "click_through", boolValue = false });
            }
            _f12WasDown = emergencyChord;

            AdvanceAnimation();
            AdvanceVoicePlayback();

            if (Time.unscaledTime >= _nextBoundsReport)
            {
                _nextBoundsReport = Time.unscaledTime + 1f;
                int x;
                int y;
                if (WindowsCompanionWindowNative.TryGetPosition(out x, out y) &&
                    (x != _preferences.positionX || y != _preferences.positionY))
                {
                    _preferences.positionX = x;
                    _preferences.positionY = y;
                    Send(new CompanionProcessMessage { type = "bounds", x = x, y = y });
                }
            }
        }

        private void ApplyMessage(CompanionProcessMessage message)
        {
            switch (message.type)
            {
                case "profile":
                    ApplyProfile(message.snapshot);
                    break;
                case "state":
                    ApplyState(message.text);
                    break;
                case "voice_start":
                    StartVoicePlayback(message.text);
                    break;
                case "voice_clear":
                    ClearVoicePlayback();
                    break;
                case "preferences":
                    ApplyPreferences(message.preferences);
                    break;
                case "show":
                    _preferences.visible = true;
                    WindowsCompanionWindowNative.SetVisible(true);
                    break;
                case "hide":
                    _preferences.visible = false;
                    WindowsCompanionWindowNative.SetVisible(false);
                    break;
                case "shutdown":
                    Application.Quit();
                    break;
            }
        }

        private void ApplyProfile(CompanionDisplaySnapshot snapshot)
        {
            if (snapshot == null)
                return;

            _snapshot = snapshot;
            _loadVersion++;
            ClearDisplayAssets();

            if (snapshot.avatarType == AvatarProfileTypes.Generic3D ||
                snapshot.avatarType == AvatarProfileTypes.Vrm)
            {
                if (IsAllowedLocalAsset(snapshot.modelPath))
                    _ = Load3DAsync(snapshot.modelPath, _loadVersion);
                return;
            }

            var profile = new AvatarProfile
            {
                id = snapshot.avatarId,
                avatarType = snapshot.avatarType,
                imagePath = snapshot.imagePath,
                motionPackManifestPath = snapshot.motionPackManifestPath,
                animationClips = snapshot.animationClips ?? new List<SpriteSheetAnimation>()
            };
            AvatarProfileMotionResolution resolution = AvatarMotionPackLoader.ResolveProfileMotion(profile);
            if (resolution.animationClips != null)
            {
                for (int i = 0; i < resolution.animationClips.Count; i++)
                {
                    SpriteSheetAnimation clip = resolution.animationClips[i];
                    if (clip != null && !string.IsNullOrWhiteSpace(clip.clipName))
                        _clips[clip.clipName] = clip;
                }
            }

            if (_clips.Count > 0)
            {
                ApplyState(_state);
                return;
            }

            if (IsAllowedLocalAsset(snapshot.imagePath))
                _staticTexture = LoadTexture(snapshot.imagePath);
            else if (!string.IsNullOrWhiteSpace(snapshot.imagePngBase64))
                _staticTexture = LoadTextureBase64(snapshot.imagePngBase64);
        }

        private async Task Load3DAsync(string path, int version)
        {
            var service = new Avatar3DService();
            bool loaded = await service.LoadAvatar(path);
            if (version != _loadVersion)
            {
                service.Unload();
                return;
            }
            if (!loaded)
            {
                Send("diagnostic", "Avatar model could not be loaded.");
                return;
            }

            _avatar3DService = service;
            _avatar3DRenderer = gameObject.AddComponent<Avatar3DRenderer>();
            _avatar3DRenderer.SetModelRoot(service.GetRuntimeTransform());
            ApplyState(_state);
        }

        private void ApplyState(string state)
        {
            if (string.IsNullOrWhiteSpace(state))
                state = CompanionDisplayStates.Idle;

            _state = state == CompanionDisplayStates.Stop ? CompanionDisplayStates.Idle : state;
            if (state == CompanionDisplayStates.Stop)
                ClearVoicePlayback();
            string clipName = _state == CompanionDisplayStates.Speaking ? "talking" : _state;

            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                string mapped = _snapshot != null && _snapshot.stateClipMapping != null
                    ? _snapshot.stateClipMapping.GetClip(clipName)
                    : clipName;
                if (!_avatar3DService.SetAnimation(mapped))
                    _avatar3DService.SetAnimation("idle");
                return;
            }

            SpriteSheetAnimation clip;
            if (!_clips.TryGetValue(clipName, out clip))
                _clips.TryGetValue("idle", out clip);
            if (clip == null)
                return;

            Sprite[] frames;
            if (!_frames.TryGetValue(clip.clipName, out frames))
            {
                frames = SpriteSheetAnimationLoader.LoadFrames(
                    clip.spriteSheetPath,
                    clip.columns,
                    clip.rows,
                    clip.frameCount);
                _frames[clip.clipName] = frames;
            }

            _activeFrames = frames ?? Array.Empty<Sprite>();
            _frameRate = clip.frameRate > 0f ? clip.frameRate : 8f;
            _pingPong = clip.pingPong;
            _frameIndex = 0;
            _frameForward = true;
            _nextFrameAt = Time.unscaledTime + (1f / _frameRate);
        }

        private void StartVoicePlayback(string text)
        {
            _voiceText = text ?? string.Empty;
            _voiceStartedAt = Time.unscaledTime;
            _voiceCharIndex = -1;
            ApplyVoiceViseme(0);
        }

        private void AdvanceVoicePlayback()
        {
            if (string.IsNullOrEmpty(_voiceText))
                return;

            int charIndex = Mathf.FloorToInt(
                (Time.unscaledTime - _voiceStartedAt) * LipsyncController.TextCharsPerSecond);
            if (charIndex == _voiceCharIndex)
                return;
            ApplyVoiceViseme(charIndex);
        }

        private void ApplyVoiceViseme(int charIndex)
        {
            _voiceCharIndex = charIndex;
            Viseme viseme = LipsyncController.GetVisemeAt(_voiceText, charIndex);
            if (_avatar3DService == null || !_avatar3DService.IsLoaded ||
                !_avatar3DService.Capabilities.hasLipsync)
                return;

            if (viseme == Viseme.Silence)
                _avatar3DService.ClearMouth();
            else
                _avatar3DService.SetMouthShape(viseme.ToString());
        }

        private void ClearVoicePlayback()
        {
            _voiceText = null;
            _voiceCharIndex = -1;
            if (_avatar3DService != null && _avatar3DService.IsLoaded)
                _avatar3DService.ClearMouth();
        }

        private void ApplyPreferences(CompanionWindowPreferences preferences)
        {
            if (preferences == null)
                return;
            _preferences = preferences;
            if (!string.IsNullOrWhiteSpace(preferences.language) &&
                !string.Equals(_language, preferences.language, StringComparison.OrdinalIgnoreCase))
            {
                _language = preferences.language;
                LocalizationExtensions.SetLocalizationService(new JsonLocalizationService(_language));
            }
            _nativeApplied = WindowsCompanionWindowNative.Apply(_preferences);
        }

        private void AdvanceAnimation()
        {
            if (_activeFrames == null || _activeFrames.Length <= 1 ||
                Time.unscaledTime < _nextFrameAt)
                return;

            _nextFrameAt = Time.unscaledTime + (1f / Mathf.Max(1f, _frameRate));
            if (_pingPong)
            {
                _frameIndex += _frameForward ? 1 : -1;
                if (_frameIndex >= _activeFrames.Length - 1)
                {
                    _frameIndex = _activeFrames.Length - 1;
                    _frameForward = false;
                }
                else if (_frameIndex <= 0)
                {
                    _frameIndex = 0;
                    _frameForward = true;
                }
            }
            else
            {
                _frameIndex = (_frameIndex + 1) % _activeFrames.Length;
            }
        }

        private void OnGUI()
        {
            DrawAvatar();
            if (_preferences.clickThrough)
                return;

            Event current = Event.current;
            if (current != null && current.type == EventType.MouseEnterWindow)
                _pointerInside = true;
            else if (current != null && current.type == EventType.MouseLeaveWindow)
            {
                _pointerInside = false;
                _contextMenuOpen = false;
            }
            if (current != null && current.type == EventType.MouseDown && current.button == 1)
            {
                _pointerInside = true;
                _contextMenuRect.x = Mathf.Clamp(
                    current.mousePosition.x,
                    4f,
                    Mathf.Max(4f, Screen.width - _contextMenuRect.width - 4f));
                _contextMenuRect.y = Mathf.Clamp(
                    current.mousePosition.y,
                    4f,
                    Mathf.Max(4f, Screen.height - _contextMenuRect.height - 4f));
                _contextMenuOpen = true;
                current.Use();
            }

            if (_pointerInside && !_contextMenuOpen)
                DrawHoverControls();
            if (_contextMenuOpen)
                _contextMenuRect = GUI.Window(7332, _contextMenuRect, DrawContextMenu, string.Empty);
        }

        private void DrawAvatar()
        {
            float scale = _snapshot != null ? Mathf.Clamp(_snapshot.avatarScale, 0.25f, 3f) : 1f;
            float width = Screen.width * scale;
            float height = Screen.height * scale;
            float x = (Screen.width - width) * 0.5f +
                (_snapshot != null ? _snapshot.avatarOffsetX * Screen.width : 0f);
            float y = (Screen.height - height) * 0.5f -
                (_snapshot != null ? _snapshot.avatarOffsetY * Screen.height : 0f);
            var rect = new Rect(x, y, width, height);

            if (_avatar3DRenderer != null && _avatar3DRenderer.OutputTexture != null)
            {
                GUI.DrawTexture(rect, _avatar3DRenderer.OutputTexture, ScaleMode.ScaleToFit, true);
                return;
            }

            if (_activeFrames != null && _activeFrames.Length > 0)
            {
                int index = Mathf.Clamp(_frameIndex, 0, _activeFrames.Length - 1);
                Sprite sprite = _activeFrames[index];
                if (sprite != null && sprite.texture != null)
                {
                    Rect source = sprite.textureRect;
                    var uv = new Rect(
                        source.x / sprite.texture.width,
                        source.y / sprite.texture.height,
                        source.width / sprite.texture.width,
                        source.height / sprite.texture.height);
                    GUI.DrawTextureWithTexCoords(rect, sprite.texture, uv, true);
                    return;
                }
            }

            if (_staticTexture != null)
                GUI.DrawTexture(rect, _staticTexture, ScaleMode.ScaleToFit, true);
        }

        private void DrawHoverControls()
        {
            _hoverControlsRect.x = Mathf.Max(8f, (Screen.width - _hoverControlsRect.width) * 0.5f);
            GUI.Box(_hoverControlsRect, string.Empty);

            Rect dragRect = new Rect(
                _hoverControlsRect.x + 4f,
                _hoverControlsRect.y + 4f,
                132f,
                26f);
            GUI.Label(dragRect, "⋮⋮ " + LocalizationExtensions.Get(
                "companion.window.short",
                "Companion"));
            Event current = Event.current;
            if (current != null && current.type == EventType.MouseDown &&
                current.button == 0 && dragRect.Contains(current.mousePosition))
            {
                WindowsCompanionWindowNative.BeginDrag();
                current.Use();
            }

            if (GUI.Button(
                new Rect(_hoverControlsRect.x + 140f, _hoverControlsRect.y + 4f, 86f, 26f),
                LocalizationExtensions.Get("companion.player.column", "Column")))
                Send(new CompanionProcessMessage { type = "return_to_column" });
            if (GUI.Button(
                new Rect(_hoverControlsRect.x + 230f, _hoverControlsRect.y + 4f, 30f, 26f),
                "×"))
                Application.Quit();
        }

        private void DrawContextMenu(int id)
        {
            float width = _contextMenuRect.width - 8f;
            float y = 4f;
            if (ContextButton(
                _preferences.visible
                    ? LocalizationExtensions.Get("companion.player.hide", "Hide")
                    : LocalizationExtensions.Get("companion.player.show", "Show"),
                width,
                ref y))
            {
                _preferences.visible = !_preferences.visible;
                Send(new CompanionProcessMessage
                {
                    type = "visible",
                    boolValue = _preferences.visible
                });
                WindowsCompanionWindowNative.SetVisible(_preferences.visible);
                _contextMenuOpen = false;
            }
            if (ContextButton(
                _preferences.pinned
                    ? LocalizationExtensions.Get("companion.player.unpin", "Unpin")
                    : LocalizationExtensions.Get("companion.player.pin", "Pin"),
                width,
                ref y))
            {
                _preferences.pinned = !_preferences.pinned;
                WindowsCompanionWindowNative.SetTopmost(_preferences.pinned);
                Send(new CompanionProcessMessage { type = "pinned", boolValue = _preferences.pinned });
                _contextMenuOpen = false;
            }

            GUI.Label(
                new Rect(4f, y, width, 22f),
                LocalizationExtensions.Get("companion.player.scale", "Scale"));
            y += 22f;
            float scaleButtonWidth = (width - 12f) / 4f;
            float[] scales = { 0.75f, 1f, 1.25f, 1.5f };
            for (int i = 0; i < scales.Length; i++)
            {
                float scale = scales[i];
                if (GUI.Button(
                    new Rect(4f + (scaleButtonWidth + 4f) * i, y, scaleButtonWidth, 24f),
                    Mathf.RoundToInt(scale * 100f) + "%"))
                {
                    SetWindowScale(scale);
                    _contextMenuOpen = false;
                }
            }
            y += 28f;

            if (ContextButton(
                LocalizationExtensions.Get("companion.window.avatar_settings", "Avatar settings"),
                width,
                ref y))
            {
                Send(new CompanionProcessMessage { type = "open_avatar_settings" });
                _contextMenuOpen = false;
            }
            if (ContextButton(
                LocalizationExtensions.Get("companion.player.return", "Return to column"),
                width,
                ref y))
            {
                Send(new CompanionProcessMessage { type = "return_to_column" });
                _contextMenuOpen = false;
            }
            if (ContextButton(
                LocalizationExtensions.Get("companion.player.close", "Close"),
                width,
                ref y))
                Application.Quit();

            Event current = Event.current;
            if (current != null && current.type == EventType.MouseDown &&
                current.button == 0 &&
                !new Rect(0f, 0f, _contextMenuRect.width, _contextMenuRect.height)
                    .Contains(current.mousePosition))
                _contextMenuOpen = false;
        }

        private static bool ContextButton(string text, float width, ref float y)
        {
            bool clicked = GUI.Button(new Rect(4f, y, width, 26f), text);
            y += 30f;
            return clicked;
        }

        private void SetWindowScale(float scale)
        {
            _preferences.scale = Mathf.Clamp(scale, 0.5f, 2f);
            _nativeApplied = WindowsCompanionWindowNative.Apply(_preferences);
            Send(new CompanionProcessMessage
            {
                type = "scale",
                floatValue = _preferences.scale
            });
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ConfigureTransparentCamera();
            DisableSceneUi();
        }

        private string DisplayStateLabel()
        {
            switch (_state)
            {
                case CompanionDisplayStates.Listening:
                    return LocalizationExtensions.Get("companion.state.listening", "Listening");
                case CompanionDisplayStates.Thinking:
                    return LocalizationExtensions.Get("companion.state.thinking", "Thinking");
                case CompanionDisplayStates.Speaking:
                    return LocalizationExtensions.Get("companion.state.speaking", "Speaking");
                default:
                    return LocalizationExtensions.Get("companion.state.idle", "Idle");
            }
        }

        private void ConfigureTransparentCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                var cameraObject = new GameObject("CompanionTransparentCamera");
                cameraObject.transform.SetParent(transform, false);
                camera = cameraObject.AddComponent<Camera>();
            }
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 0;
        }

        private static void DisableSceneUi()
        {
            UnityEngine.UIElements.UIDocument[] documents =
                UnityEngine.Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(
                    FindObjectsSortMode.None);
            for (int i = 0; i < documents.Length; i++)
                documents[i].enabled = false;
        }

        private void ClearDisplayAssets()
        {
            _clips.Clear();
            _frames.Clear();
            _activeFrames = Array.Empty<Sprite>();
            if (_staticTexture != null)
            {
                Destroy(_staticTexture);
                _staticTexture = null;
            }
            if (_avatar3DService != null)
            {
                _avatar3DService.Unload();
                _avatar3DService = null;
            }
            if (_avatar3DRenderer != null)
            {
                Destroy(_avatar3DRenderer);
                _avatar3DRenderer = null;
            }
        }

        private static Texture2D LoadTexture(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(bytes))
                    return texture;
                UnityEngine.Object.Destroy(texture);
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("[CompanionPlayer] Image load failed: " + ex.Message);
            }
            return null;
        }

        private static Texture2D LoadTextureBase64(string encoded)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(bytes))
                    return texture;
                UnityEngine.Object.Destroy(texture);
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("[CompanionPlayer] Snapshot image rejected: " + ex.Message);
            }
            return null;
        }

        private static bool IsAllowedLocalAsset(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                string root = Path.GetFullPath(Application.persistentDataPath);
                string candidate = Path.GetFullPath(path);
                string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(candidate);
            }
            catch
            {
                return false;
            }
        }

        private static string ArgumentValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private void Send(string type, string text)
        {
            Send(new CompanionProcessMessage { type = type, text = text });
        }

        private void Send(CompanionProcessMessage message)
        {
            if (!_connected || message == null)
                return;
            _outgoing.Enqueue(message);
        }

        private void OnApplicationQuit()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Send("diagnostic", "Display process shutting down.");
            ClearDisplayAssets();
            _connected = false;
            CancellationTokenSource cancellation = _pipeCancellation;
            _pipeCancellation = null;
            StreamWriter writer = _writer;
            _writer = null;
            NamedPipeClientStream pipe = _pipe;
            _pipe = null;
            try
            {
                if (cancellation != null)
                    cancellation.Cancel();
                if (writer != null)
                    writer.Dispose();
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    if (pipe != null)
                        pipe.Dispose();
                }
                catch (Exception)
                {
                }
                if (cancellation != null)
                    cancellation.Dispose();
                if (_parent != null)
                {
                    _parent.Dispose();
                    _parent = null;
                }
            }
        }
    }

    internal static class WindowsCompanionWindowNative
    {
        private const int GwlExStyle = -20;
        private const long WsExLayered = 0x00080000L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExNoActivate = 0x08000000L;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpFrameChanged = 0x0020;
        private const int SwHide = 0;
        private const int SwShow = 5;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private static IntPtr _window;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect32
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect32 Monitor;
            public Rect32 Work;
            public uint Flags;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect32 rect, IntPtr data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);

        public static bool Apply(CompanionWindowPreferences preferences)
        {
            if (!Resolve())
                return false;

            long style = GetWindowLongPtr(_window, GwlExStyle).ToInt64();
            style |= WsExLayered | WsExToolWindow;
            SetWindowLongPtr(_window, GwlExStyle, new IntPtr(style));

            try
            {
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(_window, ref margins);
            }
            catch
            {
            }

            MoveToMonitor(preferences);
            SetTopmost(preferences.pinned);
            SetClickThrough(preferences.clickThrough);
            SetVisible(preferences.visible);
            return true;
        }

        public static void SetTopmost(bool topmost)
        {
            if (!Resolve())
                return;
            SetWindowPos(
                _window,
                topmost ? HwndTopmost : HwndNotTopmost,
                0, 0, 0, 0,
                0x0001 | 0x0002 | SwpNoActivate);
        }

        public static void SetClickThrough(bool enabled)
        {
            if (!Resolve())
                return;
            long style = GetWindowLongPtr(_window, GwlExStyle).ToInt64();
            if (enabled)
                style |= WsExTransparent | WsExNoActivate;
            else
                style &= ~(WsExTransparent | WsExNoActivate);
            SetWindowLongPtr(_window, GwlExStyle, new IntPtr(style));
            SetWindowPos(
                _window,
                IntPtr.Zero,
                0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | SwpNoActivate | SwpFrameChanged);
        }

        public static void SetVisible(bool visible)
        {
            if (Resolve())
                ShowWindow(_window, visible ? SwShow : SwHide);
        }

        public static void BeginDrag()
        {
            if (!Resolve())
                return;
            ReleaseCapture();
            SendMessage(_window, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }

        public static bool TryGetPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            Rect32 rect;
            if (!Resolve() || !GetWindowRect(_window, out rect))
                return false;
            x = rect.Left;
            y = rect.Top;
            return true;
        }

        public static bool IsKeyDown(int key)
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }

        private static void MoveToMonitor(CompanionWindowPreferences preferences)
        {
            var monitors = new List<Rect32>();
            MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr dc, ref Rect32 rect, IntPtr data)
            {
                var info = new MonitorInfo();
                info.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MonitorInfo));
                if (GetMonitorInfo(monitor, ref info))
                    monitors.Add(info.Work);
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            if (monitors.Count == 0)
                return;

            int monitorIndex = Mathf.Clamp(preferences.monitorIndex, 0, monitors.Count - 1);
            Rect32 work = monitors[monitorIndex];
            float scale = Mathf.Clamp(preferences.scale, 0.5f, 2f);
            int width = Mathf.RoundToInt(420f * scale);
            int height = Mathf.RoundToInt(560f * scale);
            int x = preferences.positionX == int.MinValue
                ? work.Left + ((work.Right - work.Left - width) / 2)
                : preferences.positionX;
            int y = preferences.positionY == int.MinValue
                ? work.Top + ((work.Bottom - work.Top - height) / 2)
                : preferences.positionY;
            x = Mathf.Clamp(x, work.Left, Mathf.Max(work.Left, work.Right - width));
            y = Mathf.Clamp(y, work.Top, Mathf.Max(work.Top, work.Bottom - height));
            SetWindowPos(_window, IntPtr.Zero, x, y, width, height, SwpNoActivate | SwpShowWindow);
        }

        private static bool Resolve()
        {
            if (_window != IntPtr.Zero)
                return true;
            _window = Process.GetCurrentProcess().MainWindowHandle;
            if (_window == IntPtr.Zero)
                _window = GetActiveWindow();
            return _window != IntPtr.Zero;
        }

        private static IntPtr GetWindowLongPtr(IntPtr window, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));
        }

        private static void SetWindowLongPtr(IntPtr window, int index, IntPtr value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(window, index, value);
            else
                SetWindowLong32(window, index, value.ToInt32());
        }
    }
#endif
}
