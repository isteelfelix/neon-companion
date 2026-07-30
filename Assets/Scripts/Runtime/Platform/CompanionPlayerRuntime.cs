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
        private float _voicePositionSecs;
        private float _voiceDurationSecs;
        private bool _voiceHasPlaybackClock;
        private Rect _hoverControlsRect = new Rect(8f, 8f, 348f, 36f);
        private Rect _contextMenuRect = new Rect(8f, 8f, 220f, 248f);
        private bool _contextMenuOpen;
        private bool _pointerInside;
        private string _activeReaction;
        private bool _reactionIsEmotion;
        private float _reactionReturnAt;
        private Texture2D _hitTestReadback;
        private Texture2D _toolbarTexture;
        private Texture2D _buttonTexture;
        private Texture2D _buttonHoverTexture;
        private GUIStyle _toolbarStyle;
        private GUIStyle _toolbarLabelStyle;
        private GUIStyle _toolbarButtonStyle;
        private byte[] _hitTestAlpha;
        private float _nextHitTestCaptureAt;
        private const int HitTestMaskSize = 192;
        private const float HitTestCaptureInterval = 0.1f;

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
            AdvanceReaction();
            UpdateVrmGaze();
            _pointerInside = WindowsCompanionWindowNative.IsCursorInsideWindow();
            UpdateHitTestMask();

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
                case "voice_progress":
                    ApplyVoiceProgress(
                        message.floatValue,
                        message.floatValue2,
                        message.boolValue);
                    break;
                case "reaction":
                    TriggerReaction(message.text);
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
                if (BuiltInAvatarProfiles.IsResourcePath(snapshot.modelPath) ||
                    IsAllowedLocalAsset(snapshot.modelPath))
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
                ReportBackendReady(snapshot);
                return;
            }

            if (IsAllowedLocalAsset(snapshot.imagePath))
                _staticTexture = LoadTexture(snapshot.imagePath);
            else if (!string.IsNullOrWhiteSpace(snapshot.imagePngBase64))
                _staticTexture = LoadTextureBase64(snapshot.imagePngBase64);

            if (_staticTexture != null)
                ReportBackendReady(snapshot);
            else
                ReportBackendFailure(snapshot, "No renderable 2D asset was resolved.");
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
                ReportBackendFailure(_snapshot, "Avatar model could not be loaded.");
                return;
            }

            _avatar3DService = service;
            _avatar3DRenderer = gameObject.AddComponent<Avatar3DRenderer>();
            _avatar3DRenderer.SetModelRoot(service.GetRuntimeTransform());
            ApplyState(_state);
            ReportBackendReady(_snapshot);
        }

        private void UpdateVrmGaze()
        {
            if (_avatar3DService == null || !_avatar3DService.IsLoaded)
                return;

            switch (_avatar3DService.GazeMode)
            {
                case AvatarGazeMode.Camera:
                    if (_avatar3DRenderer != null)
                        _avatar3DService.SetGazeTarget(
                            _avatar3DRenderer.CameraWorldPosition);
                    break;
                case AvatarGazeMode.Cursor:
                    UpdateVrmCursorGaze();
                    break;
            }
        }

        private void UpdateVrmCursorGaze()
        {
            float horizontal;
            float vertical;
            if (!WindowsCompanionWindowNative.TryGetCursorNormalized(
                out horizontal,
                out vertical))
                return;

            // The cursor is normalized to [-0.5, 0.5] over the window the avatar
            // fills; the render viewport is [0, 1] with y up. Resolving it through
            // the camera ray lands the eyes on a real world point at the model's
            // depth; the normalized fallback covers a frame with no camera yet.
            Vector3 world;
            if (_avatar3DRenderer != null &&
                _avatar3DRenderer.TryGetGazePoint(
                    new Vector2(horizontal + 0.5f, vertical + 0.5f),
                    out world))
                _avatar3DService.SetGazeTarget(world);
            else
                _avatar3DService.SetGazeNormalized(horizontal, vertical);
        }

        private void ReportBackendReady(CompanionDisplaySnapshot snapshot)
        {
            if (snapshot == null)
                return;
            string details = "avatarId=" + (snapshot.avatarId ?? string.Empty) +
                ";type=" + (snapshot.avatarType ?? string.Empty);
            NeonLogger.Log("[CompanionPlayer] Backend ready: " + details);
            Send("backend_ready", details);
        }

        private void ReportBackendFailure(
            CompanionDisplaySnapshot snapshot,
            string reason)
        {
            string details = "avatarId=" +
                (snapshot != null ? snapshot.avatarId ?? string.Empty : string.Empty) +
                ";type=" +
                (snapshot != null ? snapshot.avatarType ?? string.Empty : string.Empty) +
                ";reason=" + (reason ?? string.Empty);
            NeonLogger.LogError("[CompanionPlayer] Backend failed: " + details);
            Send("backend_failed", details);
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

            ReleaseInactiveSpriteFrames(clip.clipName);
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
            _voicePositionSecs = 0f;
            _voiceDurationSecs = 0f;
            _voiceHasPlaybackClock = false;
            ApplyVoiceViseme(0);
        }

        private void ApplyVoiceProgress(
            float positionSecs,
            float durationSecs,
            bool isPlaying)
        {
            if (string.IsNullOrEmpty(_voiceText) || durationSecs <= 0f)
                return;
            _voicePositionSecs = Mathf.Clamp(positionSecs, 0f, durationSecs);
            _voiceDurationSecs = durationSecs;
            _voiceHasPlaybackClock = true;
            if (isPlaying)
                ApplyVoiceViseme(Mathf.FloorToInt(
                    Mathf.Clamp01(_voicePositionSecs / _voiceDurationSecs) *
                    _voiceText.Length));
        }

        private void AdvanceVoicePlayback()
        {
            if (string.IsNullOrEmpty(_voiceText))
                return;

            int charIndex = _voiceHasPlaybackClock && _voiceDurationSecs > 0f
                ? Mathf.FloorToInt(
                    Mathf.Clamp01(_voicePositionSecs / _voiceDurationSecs) *
                    _voiceText.Length)
                : Mathf.FloorToInt(
                    (Time.unscaledTime - _voiceStartedAt) *
                    LipsyncController.TextCharsPerSecond);
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
            _voicePositionSecs = 0f;
            _voiceDurationSecs = 0f;
            _voiceHasPlaybackClock = false;
            if (_avatar3DService != null && _avatar3DService.IsLoaded)
                _avatar3DService.ClearMouth();
        }

        private void TriggerReaction(string reaction)
        {
            if (string.IsNullOrWhiteSpace(reaction))
                return;

            _activeReaction = reaction.Trim().ToLowerInvariant();
            _reactionIsEmotion = false;
            _reactionReturnAt = Time.unscaledTime + 1.2f;
            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                // A body clip wins if one exists; otherwise a named emotion, which
                // fades in and resets on its own; a raw blendshape is the last
                // resort, for reaction names that are neither.
                if (_avatar3DService.SetAnimation(_activeReaction))
                    return;
                if (_avatar3DService.SetEmotion(_activeReaction))
                {
                    _reactionIsEmotion = true;
                    return;
                }
                _avatar3DService.SetExpression(_activeReaction, 1f);
                return;
            }

            string currentState = _state;
            ApplyState(_activeReaction);
            _state = currentState;
        }

        private void AdvanceReaction()
        {
            if (string.IsNullOrEmpty(_activeReaction) ||
                Time.unscaledTime < _reactionReturnAt)
                return;

            // An emotion reaction is left to the blender's own hold-and-fade; only
            // a raw blendshape reaction needs winding back down by hand, since it
            // sits pinned on the Manual layer until told otherwise.
            if (_avatar3DService != null && _avatar3DService.IsLoaded &&
                !_reactionIsEmotion)
                _avatar3DService.SetExpression(_activeReaction, 0f);
            _activeReaction = null;
            _reactionIsEmotion = false;
            ApplyState(_state);
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
            EnsureGuiStyles();
            DrawAvatar();

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
                DrawContextMenu();

            if (current != null &&
                current.type == EventType.MouseDown &&
                current.button == 0 &&
                !_contextMenuOpen &&
                !_hoverControlsRect.Contains(current.mousePosition) &&
                GetAvatarRect().Contains(current.mousePosition))
            {
                WindowsCompanionWindowNative.BeginDrag();
                current.Use();
            }
            else if (current != null &&
                current.type == EventType.ScrollWheel &&
                GetAvatarRect().Contains(current.mousePosition))
            {
                SetWindowScale(_preferences.scale - current.delta.y * 0.05f);
                current.Use();
            }

            WindowsCompanionWindowNative.SetControlRects(
                _hoverControlsRect,
                _pointerInside && !_contextMenuOpen,
                _contextMenuOpen,
                _contextMenuRect);
        }

        private void DrawAvatar()
        {
            Rect rect = GetAvatarRect();

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

        private Rect GetAvatarRect()
        {
            float scale = _snapshot != null
                ? Mathf.Clamp(_snapshot.avatarScale, 0.25f, 3f)
                : 1f;
            float width = Screen.width * scale;
            float height = Screen.height * scale;
            float x = (Screen.width - width) * 0.5f +
                (_snapshot != null ? _snapshot.avatarOffsetX * Screen.width : 0f);
            float y = (Screen.height - height) * 0.5f -
                (_snapshot != null ? _snapshot.avatarOffsetY * Screen.height : 0f);
            return new Rect(x, y, width, height);
        }

        private void UpdateHitTestMask()
        {
            WindowsCompanionWindowNative.SetControlRects(
                _hoverControlsRect,
                _pointerInside && !_contextMenuOpen,
                _contextMenuOpen,
                _contextMenuRect);

            if (Time.unscaledTime < _nextHitTestCaptureAt)
                return;
            _nextHitTestCaptureAt = Time.unscaledTime + HitTestCaptureInterval;

            Texture source;
            Rect uv;
            Rect contentRect;
            if (!TryGetHitTestSource(out source, out uv, out contentRect))
            {
                WindowsCompanionWindowNative.SetHitTestMask(
                    null,
                    0,
                    0,
                    new Rect());
                return;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(
                HitTestMaskSize,
                HitTestMaskSize,
                0,
                RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(
                    source,
                    temporary,
                    new Vector2(uv.width, uv.height),
                    new Vector2(uv.x, uv.y));
                if (_hitTestReadback == null)
                {
                    _hitTestReadback = new Texture2D(
                        HitTestMaskSize,
                        HitTestMaskSize,
                        TextureFormat.RGBA32,
                        false);
                }

                RenderTexture.active = temporary;
                _hitTestReadback.ReadPixels(
                    new Rect(0f, 0f, HitTestMaskSize, HitTestMaskSize),
                    0,
                    0,
                    false);
                Unity.Collections.NativeArray<Color32> pixels =
                    _hitTestReadback.GetRawTextureData<Color32>();
                if (_hitTestAlpha == null || _hitTestAlpha.Length != pixels.Length)
                    _hitTestAlpha = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                    _hitTestAlpha[i] = pixels[i].a;
                WindowsCompanionWindowNative.SetHitTestMask(
                    _hitTestAlpha,
                    HitTestMaskSize,
                    HitTestMaskSize,
                    contentRect);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private bool TryGetHitTestSource(out Texture source, out Rect uv, out Rect contentRect)
        {
            source = null;
            uv = new Rect(0f, 0f, 1f, 1f);
            contentRect = GetAvatarRect();

            if (_avatar3DRenderer != null && _avatar3DRenderer.OutputTexture != null)
            {
                source = _avatar3DRenderer.OutputTexture;
                contentRect = AspectFit(contentRect, source.width, source.height);
                return true;
            }

            if (_activeFrames != null && _activeFrames.Length > 0)
            {
                int index = Mathf.Clamp(_frameIndex, 0, _activeFrames.Length - 1);
                Sprite sprite = _activeFrames[index];
                if (sprite != null && sprite.texture != null)
                {
                    Rect sourceRect = sprite.textureRect;
                    source = sprite.texture;
                    uv = new Rect(
                        sourceRect.x / sprite.texture.width,
                        sourceRect.y / sprite.texture.height,
                        sourceRect.width / sprite.texture.width,
                        sourceRect.height / sprite.texture.height);
                    return true;
                }
            }

            if (_staticTexture == null)
                return false;

            source = _staticTexture;
            contentRect = AspectFit(contentRect, source.width, source.height);
            return true;
        }

        private static Rect AspectFit(Rect outer, int textureWidth, int textureHeight)
        {
            if (outer.width <= 0f || outer.height <= 0f ||
                textureWidth <= 0 || textureHeight <= 0)
                return outer;

            float sourceAspect = textureWidth / (float)textureHeight;
            float outerAspect = outer.width / outer.height;
            if (sourceAspect > outerAspect)
            {
                float height = outer.width / sourceAspect;
                return new Rect(
                    outer.x,
                    outer.y + (outer.height - height) * 0.5f,
                    outer.width,
                    height);
            }

            float width = outer.height * sourceAspect;
            return new Rect(
                outer.x + (outer.width - width) * 0.5f,
                outer.y,
                width,
                outer.height);
        }

        private void DrawHoverControls()
        {
            bool compact = Screen.width < 360;
            _hoverControlsRect.width = compact
                ? Mathf.Max(196f, Screen.width - 16f)
                : 348f;
            _hoverControlsRect.x = Mathf.Max(8f, (Screen.width - _hoverControlsRect.width) * 0.5f);
            GUI.Box(_hoverControlsRect, string.Empty, _toolbarStyle);

            float x = _hoverControlsRect.x + 4f;
            float dragWidth = compact ? 28f : 58f;
            Rect dragRect = new Rect(
                x,
                _hoverControlsRect.y + 4f,
                dragWidth,
                28f);
            GUI.Label(
                dragRect,
                compact
                    ? "⋮⋮"
                    : "⋮⋮ " + LocalizationExtensions.Get(
                        "companion.window.short",
                        "Companion"),
                _toolbarLabelStyle);
            Event current = Event.current;
            if (current != null && current.type == EventType.MouseDown &&
                current.button == 0 && dragRect.Contains(current.mousePosition))
            {
                WindowsCompanionWindowNative.BeginDrag();
                current.Use();
            }
            x += dragWidth + 4f;

            if (GUI.Button(
                new Rect(x, _hoverControlsRect.y + 4f, 28f, 28f),
                "−",
                _toolbarButtonStyle))
                SetWindowScale(_preferences.scale - 0.1f);
            x += 30f;
            if (!compact)
            {
                GUI.Label(
                    new Rect(x, _hoverControlsRect.y + 4f, 44f, 28f),
                    Mathf.RoundToInt(_preferences.scale * 100f) + "%",
                    _toolbarLabelStyle);
                x += 46f;
            }
            if (GUI.Button(
                new Rect(x, _hoverControlsRect.y + 4f, 28f, 28f),
                "+",
                _toolbarButtonStyle))
                SetWindowScale(_preferences.scale + 0.1f);
            x += 32f;
            float settingsWidth = compact ? 30f : 34f;
            if (GUI.Button(
                new Rect(x, _hoverControlsRect.y + 4f, settingsWidth, 28f),
                "•••",
                _toolbarButtonStyle))
            {
                _contextMenuRect.x = _hoverControlsRect.x;
                _contextMenuRect.y = _hoverControlsRect.y + _hoverControlsRect.height + 4f;
                _contextMenuOpen = true;
            }
            x += settingsWidth + 4f;
            if (!compact)
            {
                if (GUI.Button(
                    new Rect(x, _hoverControlsRect.y + 4f, 98f, 28f),
                    LocalizationExtensions.Get("companion.player.column", "Column"),
                    _toolbarButtonStyle))
                    Send(new CompanionProcessMessage { type = "return_to_column" });
                x += 102f;
            }
            if (GUI.Button(
                new Rect(x, _hoverControlsRect.y + 4f, 30f, 28f),
                "×",
                _toolbarButtonStyle))
                Application.Quit();
        }

        private void DrawContextMenu()
        {
            GUI.Box(_contextMenuRect, string.Empty, _toolbarStyle);
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
            if (ContextButton(
                _preferences.clickThrough
                    ? LocalizationExtensions.Get(
                        "companion.player.click_through_off",
                        "Disable background click-through")
                    : LocalizationExtensions.Get(
                        "companion.player.click_through_on",
                        "Enable background click-through"),
                width,
                ref y))
            {
                _preferences.clickThrough = !_preferences.clickThrough;
                WindowsCompanionWindowNative.SetClickThrough(_preferences.clickThrough);
                Send(new CompanionProcessMessage
                {
                    type = "click_through",
                    boolValue = _preferences.clickThrough
                });
                _contextMenuOpen = false;
            }

            GUI.Label(
                new Rect(_contextMenuRect.x + 4f, _contextMenuRect.y + y, width, 22f),
                LocalizationExtensions.Get("companion.player.scale", "Scale"),
                _toolbarLabelStyle);
            y += 22f;
            float scaleButtonWidth = (width - 12f) / 4f;
            float[] scales = { 0.75f, 1f, 1.25f, 1.5f };
            for (int i = 0; i < scales.Length; i++)
            {
                float scale = scales[i];
                if (GUI.Button(
                    new Rect(
                        _contextMenuRect.x + 4f + (scaleButtonWidth + 4f) * i,
                        _contextMenuRect.y + y,
                        scaleButtonWidth,
                        24f),
                    Mathf.RoundToInt(scale * 100f) + "%",
                    _toolbarButtonStyle))
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
                !_contextMenuRect.Contains(current.mousePosition))
                _contextMenuOpen = false;
        }

        private bool ContextButton(string text, float width, ref float y)
        {
            bool clicked = GUI.Button(
                new Rect(
                    _contextMenuRect.x + 4f,
                    _contextMenuRect.y + y,
                    width,
                    26f),
                text,
                _toolbarButtonStyle);
            y += 30f;
            return clicked;
        }

        private void EnsureGuiStyles()
        {
            if (_toolbarStyle != null)
                return;

            _toolbarTexture = CreateSolidTexture(new Color32(15, 17, 24, 242));
            _buttonTexture = CreateSolidTexture(new Color32(31, 34, 46, 245));
            _buttonHoverTexture = CreateSolidTexture(new Color32(91, 82, 214, 250));
            _toolbarStyle = new GUIStyle(GUI.skin.box);
            _toolbarStyle.normal.background = _toolbarTexture;
            _toolbarStyle.border = new RectOffset(0, 0, 0, 0);
            _toolbarLabelStyle = new GUIStyle(GUI.skin.label);
            _toolbarLabelStyle.normal.textColor = new Color32(220, 223, 234, 255);
            _toolbarLabelStyle.alignment = TextAnchor.MiddleCenter;
            _toolbarLabelStyle.fontSize = 11;
            _toolbarButtonStyle = new GUIStyle(GUI.skin.button);
            _toolbarButtonStyle.normal.background = _buttonTexture;
            _toolbarButtonStyle.hover.background = _buttonHoverTexture;
            _toolbarButtonStyle.active.background = _buttonHoverTexture;
            _toolbarButtonStyle.normal.textColor = new Color32(232, 234, 243, 255);
            _toolbarButtonStyle.hover.textColor = Color.white;
            _toolbarButtonStyle.active.textColor = Color.white;
            _toolbarButtonStyle.alignment = TextAnchor.MiddleCenter;
            _toolbarButtonStyle.fontSize = 11;
            _toolbarButtonStyle.border = new RectOffset(0, 0, 0, 0);
        }

        private static Texture2D CreateSolidTexture(Color32 color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);
            return texture;
        }

        private void ReleaseGuiStyles()
        {
            if (_toolbarTexture != null)
                Destroy(_toolbarTexture);
            if (_buttonTexture != null)
                Destroy(_buttonTexture);
            if (_buttonHoverTexture != null)
                Destroy(_buttonHoverTexture);
            _toolbarTexture = null;
            _buttonTexture = null;
            _buttonHoverTexture = null;
            _toolbarStyle = null;
            _toolbarLabelStyle = null;
            _toolbarButtonStyle = null;
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
            // Must match WindowsCompanionWindowNative.TransparencyColorKey.
            // Keep the key near black so antialiased sprite edges stay neutral,
            // but do not remove genuine black pixels from the avatar.
            camera.backgroundColor = new Color32(1, 0, 1, 255);
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
            ReleaseInactiveSpriteFrames(null);
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
            if (_hitTestReadback != null)
            {
                Destroy(_hitTestReadback);
                _hitTestReadback = null;
            }
            _hitTestAlpha = null;
            WindowsCompanionWindowNative.SetHitTestMask(null, 0, 0, new Rect());
        }

        private void ReleaseInactiveSpriteFrames(string keepClipName)
        {
            var releaseNames = new List<string>();
            foreach (KeyValuePair<string, Sprite[]> pair in _frames)
            {
                if (!string.Equals(
                    pair.Key,
                    keepClipName,
                    StringComparison.OrdinalIgnoreCase))
                    releaseNames.Add(pair.Key);
            }

            for (int i = 0; i < releaseNames.Count; i++)
            {
                string clipName = releaseNames[i];
                SpriteSheetAnimation clip;
                Sprite[] frames;
                if (_clips.TryGetValue(clipName, out clip) &&
                    _frames.TryGetValue(clipName, out frames))
                {
                    SpriteSheetAnimationLoader.ReleaseFrames(
                        clip.spriteSheetPath,
                        clip.columns,
                        clip.rows,
                        clip.frameCount,
                        frames);
                }
                _frames.Remove(clipName);
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
            ReleaseGuiStyles();
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
        private const int GwlWndProc = -4;
        private const long WsExLayered = 0x00080000L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;
        private const uint LwaColorKey = 0x00000001;
        private const uint TransparencyColorKey = 0x00010001;
        private const uint WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpFrameChanged = 0x0020;
        private const int SwHide = 0;
        private const int SwShow = 5;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private static IntPtr _window;
        private static IntPtr _oldWndProc;
        private static WndProcDelegate _wndProcDelegate;
        private static bool _clickThrough;
        private static HitTestSnapshot _hitTestSnapshot = new HitTestSnapshot();
        private static Rect _hoverControlsRect;
        private static bool _hoverControlsVisible;
        private static Rect _contextMenuRect;
        private static bool _contextMenuOpen;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect32
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Point32
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect32 Monitor;
            public Rect32 Work;
            public uint Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect32 rect, IntPtr data);
        private delegate IntPtr WndProcDelegate(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

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
        private static extern bool GetCursorPos(out Point32 point);

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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(
            IntPtr previous,
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(
            IntPtr hWnd,
            uint colorKey,
            byte alpha,
            uint flags);

        public static bool Apply(CompanionWindowPreferences preferences)
        {
            if (!Resolve())
                return false;

            long style = GetWindowLongPtr(_window, GwlExStyle).ToInt64();
            style |= WsExLayered | WsExToolWindow;
            style &= ~WsExNoActivate;
            SetWindowLongPtr(_window, GwlExStyle, new IntPtr(style));
            SetLayeredWindowAttributes(
                _window,
                TransparencyColorKey,
                byte.MaxValue,
                LwaColorKey);
            EnsureHitTestSubclass();

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
            _clickThrough = enabled;
            long style = GetWindowLongPtr(_window, GwlExStyle).ToInt64();
            // Whole-window WS_EX_TRANSPARENT makes the visible avatar and its
            // controls unclickable. WM_NCHITTEST below passes through only pixels
            // whose rendered alpha is effectively zero.
            style &= ~WsExNoActivate;
            SetWindowLongPtr(_window, GwlExStyle, new IntPtr(style));
            SetWindowPos(
                _window,
                IntPtr.Zero,
                0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | SwpNoActivate | SwpFrameChanged);
        }

        public static void SetHitTestMask(
            byte[] alpha,
            int width,
            int height,
            Rect contentRect)
        {
            _hitTestSnapshot = new HitTestSnapshot
            {
                Alpha = alpha,
                Width = width,
                Height = height,
                X = contentRect.x,
                Y = contentRect.y,
                RectWidth = contentRect.width,
                RectHeight = contentRect.height
            };
        }

        public static void SetControlRects(
            Rect hoverControlsRect,
            bool hoverControlsVisible,
            bool contextMenuOpen,
            Rect contextMenuRect)
        {
            _hoverControlsRect = hoverControlsRect;
            _hoverControlsVisible = hoverControlsVisible;
            _contextMenuOpen = contextMenuOpen;
            _contextMenuRect = contextMenuRect;
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

        public static bool TryGetCursorNormalized(
            out float horizontal,
            out float vertical)
        {
            horizontal = 0f;
            vertical = 0f;
            Rect32 rect;
            Point32 point;
            if (!Resolve() ||
                !GetWindowRect(_window, out rect) ||
                !GetCursorPos(out point))
                return false;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return false;

            horizontal = Mathf.Clamp(
                ((point.X - rect.Left) / (float)width) - 0.5f,
                -0.5f,
                0.5f);
            vertical = Mathf.Clamp(
                0.5f - ((point.Y - rect.Top) / (float)height),
                -0.5f,
                0.5f);
            return true;
        }

        public static bool IsCursorInsideWindow()
        {
            Rect32 rect;
            Point32 point;
            if (!Resolve() ||
                !GetWindowRect(_window, out rect) ||
                !GetCursorPos(out point))
                return false;
            return point.X >= rect.Left &&
                point.X < rect.Right &&
                point.Y >= rect.Top &&
                point.Y < rect.Bottom;
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

        private static void EnsureHitTestSubclass()
        {
            if (_window == IntPtr.Zero || _wndProcDelegate != null)
                return;

            _wndProcDelegate = WindowProc;
            IntPtr pointer =
                System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtrWithResult(_window, GwlWndProc, pointer);
            if (_oldWndProc == IntPtr.Zero)
                _wndProcDelegate = null;
        }

        private static IntPtr WindowProc(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam)
        {
            if (message == WmNcHitTest && _clickThrough)
            {
                long packed = lParam.ToInt64();
                int screenX = unchecked((short)(packed & 0xFFFF));
                int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
                Rect32 windowRect;
                if (GetWindowRect(hWnd, out windowRect))
                {
                    float clientX = screenX - windowRect.Left;
                    float clientY = screenY - windowRect.Top;
                    if (!IsInteractive(clientX, clientY))
                        return new IntPtr(HtTransparent);
                }
            }

            return CallWindowProc(_oldWndProc, hWnd, message, wParam, lParam);
        }

        private static bool IsInteractive(float x, float y)
        {
            if (_hoverControlsVisible &&
                _hoverControlsRect.Contains(new Vector2(x, y)))
                return true;
            if (_contextMenuOpen && _contextMenuRect.Contains(new Vector2(x, y)))
                return true;

            HitTestSnapshot snapshot = _hitTestSnapshot;
            if (snapshot == null || snapshot.Alpha == null ||
                snapshot.Width <= 0 || snapshot.Height <= 0 ||
                snapshot.RectWidth <= 0f || snapshot.RectHeight <= 0f)
                return false;
            if (x < snapshot.X || y < snapshot.Y ||
                x >= snapshot.X + snapshot.RectWidth ||
                y >= snapshot.Y + snapshot.RectHeight)
                return false;

            float normalizedX = (x - snapshot.X) / snapshot.RectWidth;
            float normalizedY = 1f - ((y - snapshot.Y) / snapshot.RectHeight);
            int pixelX = Mathf.Clamp(
                Mathf.FloorToInt(normalizedX * snapshot.Width),
                0,
                snapshot.Width - 1);
            int pixelY = Mathf.Clamp(
                Mathf.FloorToInt(normalizedY * snapshot.Height),
                0,
                snapshot.Height - 1);
            int index = pixelY * snapshot.Width + pixelX;
            return index >= 0 && index < snapshot.Alpha.Length &&
                snapshot.Alpha[index] >= 16;
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

        private static IntPtr SetWindowLongPtrWithResult(
            IntPtr window,
            int index,
            IntPtr value)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(window, index, value);
            return new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
        }

        private sealed class HitTestSnapshot
        {
            public byte[] Alpha;
            public int Width;
            public int Height;
            public float X;
            public float Y;
            public float RectWidth;
            public float RectHeight;
        }
    }
#endif
}
