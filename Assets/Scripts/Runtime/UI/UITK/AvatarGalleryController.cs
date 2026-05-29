using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.UI.Avatars;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal enum AvatarFilter { All, Standard, Gradient, Minimal, Custom }

    internal sealed class AvatarGalleryController
    {
        public struct Deps
        {
            // Root element for Init queries
            public VisualElement Root;
            // Cross-controller callbacks
            public Action SaveSettings;
            public Func<CompanionApp, Task> SyncActiveAvatarSystemPromptAsync;
            public Action ShowChat;
            public Func<string> GetChatSubtitle;
            public Action<string> SetChatSubtitle;
            public Action<string> SetTopbarSubtitle;
            public Action<string> SetSubtitleRole;
            public Action<string> AddSystemMessage;
            // Motion state inputs
            public Func<bool> IsChatSending;
            public Func<bool> IsChatStreamingResponse;
            public Func<bool> GetIsVoicePlaying;
            public Func<bool> GetIsVoiceRecording;
            // Services
            public Func<Task<CompanionApp>> GetAppAsync;
            public Func<CompanionApp> GetAppSync;
            public Func<bool> IsBound;
            // MonoBehaviour component access
            public Func<SpriteSheetAnimator> GetOrCreateAnimator;
            public Func<Avatar3DRenderer> GetOrCreateAvatar3DRenderer;
            public Transform ModelParent;
        }

        // ---- Static data ----

        internal static readonly string[] BuiltInAvatarIds =
            { "neon", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" };

        private static readonly Dictionary<string, BuiltInAvatarMeta> BuiltInAvatarMetaById = new Dictionary<string, BuiltInAvatarMeta>
        {
            ["neon"]   = new BuiltInAvatarMeta("avatar.builtin.neon.name", "Неон", "avatar.builtin.neon.style", "стандартный", "avatar.builtin.neon.persona", "Неон — спокойный и практичный AI-компаньон разработчика. Отвечает кратко, структурно и по делу.", AvatarFilter.Standard),
            ["aurora"] = new BuiltInAvatarMeta("avatar.builtin.aurora.name", "Аврора", "avatar.builtin.aurora.style", "прохладный · градиент", "avatar.builtin.aurora.persona", "Аврора — спокойная и аналитичная. Объясняет ясно и сначала обдумывает ответ.", AvatarFilter.Gradient),
            ["ember"]  = new BuiltInAvatarMeta("avatar.builtin.ember.name", "Эмбер", "avatar.builtin.ember.style", "тёплый · градиент", "avatar.builtin.ember.persona", "Эмбер — тёплая и эмпатичная. Улавливает настроение и отвечает бережно.", AvatarFilter.Gradient),
            ["glass"]  = new BuiltInAvatarMeta("avatar.builtin.glass.name", "Гласс", "avatar.builtin.glass.style", "минимал · тёмный", "avatar.builtin.glass.persona", "Гласс — энергичная и смелая. Любит сложные задачи и быстрый темп.", AvatarFilter.Minimal),
            ["flora"]  = new BuiltInAvatarMeta("avatar.builtin.flora.name", "Флора", "avatar.builtin.flora.style", "природный · градиент", "avatar.builtin.flora.persona", "Флора — вдумчивая и спокойная. Даёт нюансированные и сбалансированные ответы.", AvatarFilter.Gradient),
            ["mono"]   = new BuiltInAvatarMeta("avatar.builtin.mono.name", "Моно", "avatar.builtin.mono.style", "минимал · монохром", "avatar.builtin.mono.persona", "Моно — точная и эффективная. Ценит корректность и краткость.", AvatarFilter.Minimal),
            ["cobalt"] = new BuiltInAvatarMeta("avatar.builtin.cobalt.name", "Кобальт", "avatar.builtin.cobalt.style", "смелый · градиент", "avatar.builtin.cobalt.persona", "Кобальт — креативная и изобретательная. Любит исследовать идеи и связи.", AvatarFilter.Gradient),
            ["rose"]   = new BuiltInAvatarMeta("avatar.builtin.rose.name", "Роуз", "avatar.builtin.rose.style", "мягкий · градиент", "avatar.builtin.rose.persona", "Роуз — обаятельная и общительная. Делает диалог более личным.", AvatarFilter.Gradient)
        };

        // ---- State ----

        private Deps _d;

        private string _activeAvatarId = "neon";
        private AvatarFilter _activeAvatarFilter = AvatarFilter.All;
        private AvatarViewMode _avatarViewMode = AvatarViewMode.Static;
        private Image _avatarArtImage;
        private Image _avatar3DImage;
        private SpriteSheetAnimator _avatarAnimator;
        private Avatar3DRenderer _avatar3DRenderer;
        private IAvatar3DService _avatar3DService;
        private AvatarMotionState _avatarMotionState = AvatarMotionState.Idle;
        private List<AvatarProfile> _cachedCustomProfiles = new List<AvatarProfile>();
        private readonly Dictionary<string, AvatarProfile> _cachedProfilesById = new Dictionary<string, AvatarProfile>();
        private readonly Dictionary<string, VisualElement> _customAvatarTiles = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, Texture2D> _customTextures = new Dictionary<string, Texture2D>();
        private AvatarCustomizationPanel _avatarCustomizationPanel;
        private AvatarCustomizationData _activeCustomizationBaseline;

        // Typing animation (header dots)
        private VisualElement _typingDot1;
        private VisualElement _typingDot2;
        private VisualElement _typingDot3;
        private IVisualElementScheduledItem _typingSchedule;
        private int _typingFrame;

        // ---- UI fields (queried in Init) ----

        private VisualElement _avatarArt;
        private VisualElement _avatarCircle;
        private VisualElement _avatarStageHero;
        private VisualElement _avatarGlow;
        private VisualElement _avatarShade;
        private Label _avatarLetter;
        private VisualElement _previewHero;
        private Label _previewTitle;
        private Label _previewTag;
        private Label _previewPersona;
        private Label _previewAnimationInfo;
        private Label _previewPersonaStateBadge;
        private Label _previewPersonaStateHelp;
        private VisualElement _previewPersonaStateRow;
        private Button _previewApplyBtn;
        private Button _previewEditPersonaBtn;
        private Button _previewResetPersonaBtn;
        private Button _previewDeleteAvatarBtn;
        private VisualElement _galleryContainer;
        private Label _navAvatarsCount;
        private Label _previewPersonaLabel;
        private VisualElement _previewActionsRow;
        private VisualElement _personaEditorPanel;
        private TextField _personaEditField;
        private Button _personaSaveBtn;
        private Button _personaCancelBtn;
        private Button _viewModeStaticBtn;
        private Button _viewModeAnimatedBtn;
        private Button _viewMode3DBtn;
        private VisualElement _avatarFilterRow;
        private VisualElement _galleryStatic;
        private VisualElement _galleryAnimated;
        private VisualElement _gallery3D;
        private VisualElement _avtileNeonAnimated;
        private Button _avatarFilterAllBtn;
        private Button _avatarFilterStandardBtn;
        private Button _avatarFilterGradientBtn;
        private Button _avatarFilterMinimalBtn;
        private Button _avatarFilterCustomBtn;
        private Label _avatarFilterAllCount;
        private Label _avatarFilterStandardCount;
        private Label _avatarFilterGradientCount;
        private Label _avatarFilterMinimalCount;
        private Label _avatarFilterCustomCount;
        private Button _avatarUploadBtn;
        private Button _avatarOpenFolderBtn;
        private VisualElement _avatarUploadTile;
        private Label _avatarEmojiOverlay;
        private Label _previewEmojiOverlay;

        // ---- Public properties ----

        public string ActiveAvatarId
        {
            get { return _activeAvatarId; }
            set { _activeAvatarId = value; }
        }

        public VisualElement AvatarCircle { get { return _avatarCircle; } }

        public SpriteSheetAnimator GetAvatarAnimatorInstance() { return _avatarAnimator; }

        public int GetAvatarTotalCount() { return BuiltInAvatarIds.Length + (_cachedCustomProfiles != null ? _cachedCustomProfiles.Count : 0); }

        // ---- Lifecycle ----

        public void SetDeps(Deps deps) { _d = deps; }

        public void Init()
        {
            var root = _d.Root;
            if (root == null) return;

            _avatarArt        = root.Q<VisualElement>("avatar-art");
            _avatarCircle     = root.Q<VisualElement>("avatar-circle");
            _avatarStageHero  = root.Q<VisualElement>("avatar-stage-hero");
            _avatarGlow       = root.Q<VisualElement>("avatar-glow");
            _avatarShade      = _avatarCircle?.Q<VisualElement>(className: "avatar__shade");
            _avatarLetter     = _avatarCircle?.Q<Label>(className: "avatar__letter");
            _previewHero      = root.Q<VisualElement>("preview-hero");
            _previewTitle     = root.Q<Label>("preview-title");
            _previewTag       = root.Q<Label>("preview-tag");
            _previewPersona   = root.Q<Label>("preview-persona");
            _previewAnimationInfo = root.Q<Label>("preview-animation-info");
            _previewPersonaStateBadge = root.Q<Label>("preview-persona-state-badge");
            _previewPersonaStateHelp  = root.Q<Label>("preview-persona-state-help");
            _previewPersonaStateRow   = root.Q<VisualElement>("preview-persona-state-row");
            _previewApplyBtn      = root.Q<Button>("preview-apply-btn");
            _previewEditPersonaBtn = root.Q<Button>("preview-edit-persona-btn");
            _previewResetPersonaBtn = root.Q<Button>("preview-reset-persona-btn");
            _previewDeleteAvatarBtn = root.Q<Button>("preview-delete-avatar-btn");
            _previewPersonaLabel  = root.Q<Label>("preview-persona-label");
            _previewActionsRow    = root.Q<VisualElement>("preview-actions-row");
            _personaEditorPanel   = root.Q<VisualElement>("persona-editor-panel");
            _personaEditField     = root.Q<TextField>("persona-edit-field");
            _personaSaveBtn       = root.Q<Button>("persona-save-btn");
            _personaCancelBtn     = root.Q<Button>("persona-cancel-btn");
            _viewModeStaticBtn    = root.Q<Button>("viewmode-static-btn");
            _viewModeAnimatedBtn  = root.Q<Button>("viewmode-animated-btn");
            _viewMode3DBtn        = root.Q<Button>("viewmode-3d-btn");
            _avatarFilterRow      = root.Q<VisualElement>("avatar-filterrow");
            _galleryStatic        = root.Q<VisualElement>("gallery-static");
            _galleryAnimated      = root.Q<VisualElement>("gallery-animated");
            _gallery3D            = root.Q<VisualElement>("gallery-3d");
            _avtileNeonAnimated   = root.Q<VisualElement>("avtile-neon-animated");
            _avatarFilterAllBtn       = root.Q<Button>("avatar-filter-all-btn");
            _avatarFilterStandardBtn  = root.Q<Button>("avatar-filter-standard-btn");
            _avatarFilterGradientBtn  = root.Q<Button>("avatar-filter-gradient-btn");
            _avatarFilterMinimalBtn   = root.Q<Button>("avatar-filter-minimal-btn");
            _avatarFilterCustomBtn    = root.Q<Button>("avatar-filter-custom-btn");
            _avatarFilterAllCount     = root.Q<Label>("avatar-filter-all-count");
            _avatarFilterStandardCount = root.Q<Label>("avatar-filter-standard-count");
            _avatarFilterGradientCount = root.Q<Label>("avatar-filter-gradient-count");
            _avatarFilterMinimalCount  = root.Q<Label>("avatar-filter-minimal-count");
            _avatarFilterCustomCount   = root.Q<Label>("avatar-filter-custom-count");
            _avatarUploadBtn      = root.Q<Button>("avatar-upload-btn");
            _avatarOpenFolderBtn  = root.Q<Button>("avatar-open-folder-btn");
            _avatarUploadTile     = root.Q<VisualElement>("avtile-upload");
            _galleryContainer     = root.Q<VisualElement>(className: "gallery");

            var avatarsPanel = root.Q<VisualElement>("avatars-panel");
            if (avatarsPanel != null)
                _galleryContainer = avatarsPanel.Q<VisualElement>(className: "gallery");

            _navAvatarsCount = root.Q<VisualElement>("nav-avatars")?.Q<Label>(className: "nav__count");

            EnsureCustomizationOverlayElements();
            if (_avatarCustomizationPanel == null)
            {
                var customizationRoot = root.Q<VisualElement>("avatar-customization-foldout");
                if (customizationRoot != null)
                {
                    _avatarCustomizationPanel = new AvatarCustomizationPanel(customizationRoot);
                    _avatarCustomizationPanel.Changed += OnAvatarCustomizationChanged;
                    _avatarCustomizationPanel.Saved += OnAvatarCustomizationSaved;
                    _avatarCustomizationPanel.Canceled += OnAvatarCustomizationCanceled;
                }
            }

            EnsureAvatarAnimationImage();
            SetDisplay(_personaEditorPanel, DisplayStyle.None);
            SetDisplay(_previewDeleteAvatarBtn, DisplayStyle.None);

            var typingEl = root.Q<VisualElement>("typing-indicator");
            if (typingEl != null)
            {
                var dots = typingEl.Query<VisualElement>(className: "typing__dot").ToList();
                _typingDot1 = dots.Count > 0 ? dots[0] : null;
                _typingDot2 = dots.Count > 1 ? dots[1] : null;
                _typingDot3 = dots.Count > 2 ? dots[2] : null;
            }
        }

        public void RegisterCallbacks()
        {
            RegisterClick(_previewApplyBtn, OnPreviewApplyClicked);
            RegisterClick(_previewEditPersonaBtn, OnPreviewEditPersonaClicked);
            RegisterClick(_previewResetPersonaBtn, OnPreviewResetPersonaClicked);
            RegisterClick(_previewDeleteAvatarBtn, OnPreviewDeleteAvatarClicked);
            RegisterClick(_personaSaveBtn, OnPersonaSaveClicked);
            RegisterClick(_personaCancelBtn, OnPersonaCancelClicked);
            RegisterClick(_viewModeStaticBtn,   OnViewModeStaticClicked);
            RegisterClick(_viewModeAnimatedBtn, OnViewModeAnimatedClicked);
            RegisterClick(_viewMode3DBtn,       OnViewMode3DClicked);
            RegisterClick(_avtileNeonAnimated,  OnNeonAnimatedTileClicked);
            RegisterClick(_avatarFilterAllBtn,      OnAvatarFilterAllClicked);
            RegisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            RegisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            RegisterClick(_avatarFilterMinimalBtn,  OnAvatarFilterMinimalClicked);
            RegisterClick(_avatarFilterCustomBtn,   OnAvatarFilterCustomClicked);
            RegisterClick(_avatarUploadBtn, OnAvatarUploadClicked);
            RegisterClick(_avatarOpenFolderBtn, OnAvatarOpenFolderClicked);
            if (_avatarUploadTile != null)
                _avatarUploadTile.RegisterCallback<ClickEvent>(_ => OnAvatarUploadClicked());

            RegisterAvatarGalleryCallbacks();
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_previewApplyBtn, OnPreviewApplyClicked);
            UnregisterClick(_previewEditPersonaBtn, OnPreviewEditPersonaClicked);
            UnregisterClick(_previewResetPersonaBtn, OnPreviewResetPersonaClicked);
            UnregisterClick(_previewDeleteAvatarBtn, OnPreviewDeleteAvatarClicked);
            UnregisterClick(_personaSaveBtn, OnPersonaSaveClicked);
            UnregisterClick(_personaCancelBtn, OnPersonaCancelClicked);
            UnregisterClick(_viewModeStaticBtn,   OnViewModeStaticClicked);
            UnregisterClick(_viewModeAnimatedBtn, OnViewModeAnimatedClicked);
            UnregisterClick(_viewMode3DBtn,       OnViewMode3DClicked);
            UnregisterClick(_avtileNeonAnimated,  OnNeonAnimatedTileClicked);
            UnregisterClick(_avatarFilterAllBtn,      OnAvatarFilterAllClicked);
            UnregisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            UnregisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            UnregisterClick(_avatarFilterMinimalBtn,  OnAvatarFilterMinimalClicked);
            UnregisterClick(_avatarFilterCustomBtn,   OnAvatarFilterCustomClicked);
            UnregisterClick(_avatarUploadBtn, OnAvatarUploadClicked);
            UnregisterClick(_avatarOpenFolderBtn, OnAvatarOpenFolderClicked);
        }

        public void OnDisable()
        {
            _avatarMotionState = AvatarMotionState.Idle;
            _avatarAnimator?.Stop();
            _avatar3DService?.Unload();
            _typingSchedule?.Pause();
            foreach (var tex in _customTextures.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            _customTextures.Clear();
        }

        // ---- Gallery refresh (called from SettingsController deps) ----

        public void RefreshCustomAvatarGallery(CompanionApp app)
        {
            if (_galleryContainer == null || app == null)
                return;

            foreach (var tile in _customAvatarTiles.Values)
                _galleryContainer.Remove(tile);

            _customAvatarTiles.Clear();
            UpdateAvatarProfileCaches(app.Avatars.GetAll());

            foreach (var profile in _cachedCustomProfiles)
            {
                var tile = CreateCustomAvatarTile(profile);
                if (_avatarUploadTile != null)
                {
                    int uploadIndex = _galleryContainer.IndexOf(_avatarUploadTile);
                    if (uploadIndex >= 0)
                        _galleryContainer.Insert(uploadIndex, tile);
                    else
                        _galleryContainer.Add(tile);
                }
                else
                {
                    _galleryContainer.Add(tile);
                }

                _customAvatarTiles[profile.id] = tile;
            }

            int total = BuiltInAvatarIds.Length + _cachedCustomProfiles.Count;
            if (_navAvatarsCount != null)
                _navAvatarsCount.text = total.ToString();

            RefreshBuiltInAvatarTileLabels();
            ApplyAvatarFilter();
        }

        public void RefreshBuiltInAvatarTileLabels()
        {
            if (_d.Root == null) return;
            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _d.Root.Q<VisualElement>($"avtile-{id}");
                var nameLabel = tile?.Q<Label>(className: "avtile__name");
                if (nameLabel != null)
                    nameLabel.text = AvatarDisplayName(id);
            }
        }

        public void ApplyAvatarFilter()
        {
            UpdateAvatarFilterChipState();
            UpdateAvatarFilterCounts();

            if (_d.Root == null)
                return;

            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _d.Root.Q<VisualElement>($"avtile-{id}");
                if (tile != null)
                    SetDisplay(tile, MatchesFilter(id) ? DisplayStyle.Flex : DisplayStyle.None);
            }

            foreach (var kvp in _customAvatarTiles)
                SetDisplay(kvp.Value, MatchesFilter(kvp.Key) ? DisplayStyle.Flex : DisplayStyle.None);

            if (_avatarUploadTile != null)
                SetDisplay(_avatarUploadTile, _activeAvatarFilter == AvatarFilter.All || _activeAvatarFilter == AvatarFilter.Custom
                    ? DisplayStyle.Flex
                    : DisplayStyle.None);

            if (!MatchesFilter(_activeAvatarId))
            {
                var fallback = GetFirstAvatarForActiveFilter();
                if (!string.IsNullOrEmpty(fallback))
                    SelectAvatar(fallback);
            }
        }

        public void ApplyAvatarArt(string avatarId)
        {
            bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, avatarId) >= 0;
            var profile = GetStoredProfile(avatarId);
            NeonLogger.Log("[AvatarArt] ApplyAvatarArt id='" + avatarId +
                "' isBuiltIn=" + isBuiltIn +
                " storedProfile=" + (profile != null ? "found (clips=" + (profile.animationClips?.Count ?? 0) + ")" : "null") +
                " _avatarArt=" + (_avatarArt != null ? "ok" : "NULL"));
            if (profile == null && isBuiltIn)
                profile = new AvatarProfile { id = avatarId, isBuiltIn = true };
            bool is3D = profile != null && profile.is3D && !string.IsNullOrWhiteSpace(profile.modelPath);
            bool hasAnimation = !is3D && ConfigureAvatarAnimation(profile);
            NeonLogger.Log("[AvatarArt] hasAnimation=" + hasAnimation + " is3D=" + is3D);

            if (is3D)
                _ = ConfigureAvatar3DAsync(profile);
            else
                Disable3DAvatarRender();

            if (_avatarArt != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _avatarArt.EnableInClassList($"avatar__art--{id}", isBuiltIn && id == avatarId && !hasAnimation);

                if (!isBuiltIn && !hasAnimation && !is3D)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _avatarArt.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                }
                else
                {
                    _avatarArt.style.backgroundImage = StyleKeyword.Null;
                }
            }

            SetDisplay(_avatarShade,  hasAnimation ? DisplayStyle.None : DisplayStyle.Flex);
            SetDisplay(_avatarLetter, hasAnimation ? DisplayStyle.None : DisplayStyle.Flex);

            ApplyAvatarLayout(hasAnimation);

            if (_previewHero != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _previewHero.EnableInClassList($"preview-hero--{id}", isBuiltIn && id == avatarId);

                if (!isBuiltIn && !is3D)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _previewHero.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                    _previewHero.style.backgroundColor = new StyleColor(new Color(0.18f, 0.18f, 0.22f));
                }
                else
                {
                    _previewHero.style.backgroundImage = StyleKeyword.Null;
                    _previewHero.style.backgroundColor = StyleKeyword.Null;
                }
            }

            string name = AvatarDisplayName(avatarId);
            if (_previewTitle != null)
                _previewTitle.text = name;
            if (_previewTag != null)
                _previewTag.text = AvatarStyleTag(avatarId);
            if (_previewPersona != null)
                _previewPersona.text = AvatarPersonaText(avatarId);
            if (_previewAnimationInfo != null)
                _previewAnimationInfo.text = (_avatarViewMode == AvatarViewMode.Animated)
                    ? BuildAnimationInfoText(profile)
                    : LocalizationExtensions.Get("avatar.animation.static", "Статичное изображение");

            UpdatePersonaStateUi(avatarId);
            UpdateAvatarActionButtons(avatarId);

            var customData = GetStoredProfile(avatarId)?.customization;
            _activeCustomizationBaseline = CloneCustomization(customData);
            _avatarCustomizationPanel?.Bind(_activeCustomizationBaseline);
            ApplyAvatarCustomizationVisual(_activeCustomizationBaseline);
        }

        public void SyncGallerySelection(string avatarId)
        {
            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _d.Root?.Q<VisualElement>($"avtile-{id}");
                tile?.EnableInClassList("avtile--selected", id == avatarId);
            }

            foreach (var kvp in _customAvatarTiles)
                kvp.Value.EnableInClassList("avtile--selected", kvp.Key == avatarId);

            _avtileNeonAnimated?.EnableInClassList("avtile--selected",
                _avatarViewMode == AvatarViewMode.Animated && avatarId == "neon");
        }

        // ---- Motion state (called from ChatController and voice events) ----

        public void SetAvatarMotionState(AvatarMotionState state)
        {
            _avatarMotionState = state;

            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                string clip3D = StateToClipName(state);
                if (!_avatar3DService.SetAnimation(clip3D))
                    _avatar3DService.SetAnimation("idle");
                return;
            }

            if (_avatarAnimator == null || !_avatarAnimator.HasAnyClips)
                return;

            string clip2D = ResolveAvailableClipForState(state);
            _avatarAnimator.Play(clip2D);
        }

        public void RefreshAvatarMotionState()
        {
            if (_avatarAnimator != null && _avatarAnimator.IsPlayingOneShot)
                return;

            if (_d.GetIsVoiceRecording != null && _d.GetIsVoiceRecording())
            {
                SetAvatarMotionState(AvatarMotionState.Listening);
                return;
            }

            if (_d.GetIsVoicePlaying != null && _d.GetIsVoicePlaying())
            {
                SetAvatarMotionState(AvatarMotionState.Talking);
                return;
            }

            if (_d.IsChatSending != null && _d.IsChatSending())
            {
                SetAvatarMotionState(_d.IsChatStreamingResponse != null && _d.IsChatStreamingResponse()
                    ? AvatarMotionState.Talking
                    : AvatarMotionState.Thinking);
                return;
            }

            SetAvatarMotionState(AvatarMotionState.Idle);
        }

        public void TriggerAvatarSmile()  { PlayAvatarReaction("smile"); }
        public void TriggerAvatarConfused() { PlayAvatarReaction("confused"); }

        // ---- Display text helpers (public for MainViewController wrappers) ----

        public string AvatarDisplayName(string avatarId)
        {
            var custom = GetCustomProfile(avatarId);
            if (custom != null && !string.IsNullOrWhiteSpace(custom.name))
                return custom.name;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.DisplayNameKey, meta.DisplayNameFallback);
            var fallbackMeta = BuiltInAvatarMetaById["neon"];
            return LocalizationExtensions.Get(fallbackMeta.DisplayNameKey, fallbackMeta.DisplayNameFallback);
        }

        public void UpdatePersonaStateUi(string avatarId)
        {
            if (_previewPersonaStateBadge == null || _previewPersonaStateHelp == null)
                return;

            bool isCustom = GetCustomProfile(avatarId) != null;
            bool hasOverride = HasPersonaOverride(avatarId);

            if (hasOverride)
            {
                _previewPersonaStateBadge.text = LocalizationExtensions.Get("avatar.persona.state.local.badge", "Локальные инструкции");
                _previewPersonaStateHelp.text = LocalizationExtensions.Get("avatar.persona.state.local.help", "Сейчас используется сохранённый локально system prompt для этого аватара.");
                return;
            }

            if (isCustom)
            {
                _previewPersonaStateBadge.text = LocalizationExtensions.Get("avatar.persona.state.missing.badge", "Инструкции не заданы");
                _previewPersonaStateHelp.text = LocalizationExtensions.Get("avatar.persona.state.missing.help", "Для этого пользовательского аватара system prompt сейчас не применяется.");
                return;
            }

            _previewPersonaStateBadge.text = LocalizationExtensions.Get("avatar.persona.state.builtin.badge", "Встроенные инструкции");
            _previewPersonaStateHelp.text = LocalizationExtensions.Get("avatar.persona.state.builtin.help", "Сейчас используются встроенные инструкции по умолчанию.");
        }

        public void UpdateAvatarActionButtons(string avatarId)
        {
            bool isCustom = GetCustomProfile(avatarId) != null;
            bool hasOverride = HasPersonaOverride(avatarId);
            SetDisplay(_previewResetPersonaBtn, hasOverride ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_previewDeleteAvatarBtn, isCustom ? DisplayStyle.Flex : DisplayStyle.None);
        }

        public void UpdateAvatarFilterCounts()
        {
            int customCount  = _cachedCustomProfiles != null ? _cachedCustomProfiles.Count : 0;
            int totalBuiltIn = BuiltInAvatarIds.Length;
            int standardCount = 0;
            int gradientCount = 0;
            int minimalCount  = 0;
            for (int i = 0; i < BuiltInAvatarIds.Length; i++)
            {
                var f = GetBuiltInFilter(BuiltInAvatarIds[i]);
                if (f == AvatarFilter.Standard) standardCount++;
                else if (f == AvatarFilter.Gradient) gradientCount++;
                else if (f == AvatarFilter.Minimal) minimalCount++;
            }

            SetCountLabel(_avatarFilterAllCount, totalBuiltIn + customCount);
            SetCountLabel(_avatarFilterStandardCount, standardCount);
            SetCountLabel(_avatarFilterGradientCount, gradientCount);
            SetCountLabel(_avatarFilterMinimalCount, minimalCount);
            SetCountLabel(_avatarFilterCustomCount, customCount);
        }

        // ---- Typing animation (header dots) ----

        public void StartTypingAnimation()
        {
            if (_typingDot1 == null) return;
            _typingFrame = 0;
            _typingSchedule?.Pause();
            _typingSchedule = _typingDot1.schedule.Execute(TickTyping).Every(380);
        }

        public void StopTypingAnimation()
        {
            _typingSchedule?.Pause();
            SetDotOpacity(_typingDot1, 1f);
            SetDotOpacity(_typingDot2, 1f);
            SetDotOpacity(_typingDot3, 1f);
        }

        // ---- Private methods ----

        private void RegisterAvatarGalleryCallbacks()
        {
            if (_d.Root == null) return;
            foreach (var id in BuiltInAvatarIds)
            {
                string capturedId = id;
                var tile = _d.Root.Q<VisualElement>($"avtile-{capturedId}");
                if (tile != null)
                    tile.RegisterCallback<ClickEvent>(_ => SelectAvatar(capturedId));
            }
        }

        private void OnViewModeStaticClicked()   { SetAvatarViewMode(AvatarViewMode.Static); }
        private void OnViewModeAnimatedClicked() { SetAvatarViewMode(AvatarViewMode.Animated); }
        private void OnViewMode3DClicked()        { SetAvatarViewMode(AvatarViewMode.Volume3D); }

        private void OnNeonAnimatedTileClicked(ClickEvent _)
        {
            if (_activeAvatarId == "neon")
            {
                SyncGallerySelection("neon");
                ApplyAvatarArt("neon");
                _d.SaveSettings?.Invoke();
            }
            else
            {
                SelectAvatar("neon");
            }
        }

        private void OnAvatarFilterAllClicked()      { SetAvatarFilter(AvatarFilter.All); }
        private void OnAvatarFilterStandardClicked() { SetAvatarFilter(AvatarFilter.Standard); }
        private void OnAvatarFilterGradientClicked() { SetAvatarFilter(AvatarFilter.Gradient); }
        private void OnAvatarFilterMinimalClicked()  { SetAvatarFilter(AvatarFilter.Minimal); }
        private void OnAvatarFilterCustomClicked()   { SetAvatarFilter(AvatarFilter.Custom); }

        private void SetAvatarFilter(AvatarFilter filter)
        {
            _activeAvatarFilter = filter;
            ApplyAvatarFilter();
        }

        private void SetAvatarViewMode(AvatarViewMode mode)
        {
            if (_avatarViewMode == mode) return;
            _avatarViewMode = mode;
            ApplyAvatarViewMode();
            ApplyAvatarArt(_activeAvatarId);
        }

        private void ApplyAvatarViewMode()
        {
            bool isStatic   = _avatarViewMode == AvatarViewMode.Static;
            bool isAnimated = _avatarViewMode == AvatarViewMode.Animated;
            bool is3D       = _avatarViewMode == AvatarViewMode.Volume3D;

            _viewModeStaticBtn?.EnableInClassList("viewmode-btn--active", isStatic);
            _viewModeAnimatedBtn?.EnableInClassList("viewmode-btn--active", isAnimated);
            _viewMode3DBtn?.EnableInClassList("viewmode-btn--active", is3D);

            SetDisplay(_avatarFilterRow, isStatic ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_galleryStatic,   isStatic ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_galleryAnimated, isAnimated ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_gallery3D,       is3D ? DisplayStyle.Flex : DisplayStyle.None);

            _avtileNeonAnimated?.EnableInClassList("avtile--selected", isAnimated && _activeAvatarId == "neon");
        }

        private void SelectAvatar(string avatarId)
        {
            if (_activeAvatarId == avatarId) return;
            ClosePersonaEditor();
            CancelCustomizationEdits();
            _activeAvatarId = avatarId;
            SyncGallerySelection(avatarId);
            ApplyAvatarArt(avatarId);
            string name = AvatarDisplayName(avatarId);
            _d.SetSubtitleRole?.Invoke(name);
            string chatSub = _d.GetChatSubtitle != null ? _d.GetChatSubtitle() : string.Empty;
            string updatedSub = chatSub.Contains("·")
                ? chatSub.Substring(0, chatSub.LastIndexOf('·') + 2) + name
                : chatSub;
            _d.SetChatSubtitle?.Invoke(updatedSub);
            _d.SetTopbarSubtitle?.Invoke(updatedSub);
            _d.SaveSettings?.Invoke();
        }

        private void OnPreviewApplyClicked() { _ = ApplyAvatarToSessionAsync(); }

        private async Task ApplyAvatarToSessionAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app == null) return;

                if (_d.SyncActiveAvatarSystemPromptAsync != null)
                    await _d.SyncActiveAvatarSystemPromptAsync(app);

                _d.SaveSettings?.Invoke();
                _d.ShowChat?.Invoke();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnPreviewEditPersonaClicked() { OpenPersonaEditor(); }
        private void OnPersonaCancelClicked()      { ClosePersonaEditor(); }
        private void OnPersonaSaveClicked()        { _ = SavePersonaAsync(); }
        private void OnPreviewResetPersonaClicked() { _ = ResetPersonaOverrideAsync(); }
        private void OnPreviewDeleteAvatarClicked() { _ = DeleteSelectedAvatarAsync(); }

        private void OpenPersonaEditor()
        {
            string current = PersonaEditorText(_activeAvatarId);
            if (_personaEditField != null) _personaEditField.value = current;
            SetDisplay(_previewPersonaStateRow, DisplayStyle.None);
            SetDisplay(_previewPersonaLabel, DisplayStyle.None);
            SetDisplay(_previewPersona, DisplayStyle.None);
            SetDisplay(_previewActionsRow, DisplayStyle.None);
            SetDisplay(_personaEditorPanel, DisplayStyle.Flex);
        }

        private void ClosePersonaEditor()
        {
            SetDisplay(_personaEditorPanel, DisplayStyle.None);
            SetDisplay(_previewPersonaStateRow, DisplayStyle.Flex);
            SetDisplay(_previewPersonaLabel, DisplayStyle.Flex);
            SetDisplay(_previewPersona, DisplayStyle.Flex);
            SetDisplay(_previewActionsRow, DisplayStyle.Flex);
            UpdateAvatarActionButtons(_activeAvatarId);
        }

        private async Task SavePersonaAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app == null) return;

                string newPrompt = _personaEditField?.value?.Trim() ?? string.Empty;
                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId);
                if (profile == null)
                {
                    bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0;
                    if (!isBuiltIn) return;

                    profile = new AvatarProfile
                    {
                        id = _activeAvatarId,
                        isBuiltIn = true,
                        name = string.Empty,
                        imagePath = string.Empty,
                        systemPrompt = newPrompt
                    };
                    profiles.Add(profile);
                }
                else
                {
                    profile.systemPrompt = newPrompt;
                }

                app.Avatars.SaveAll(profiles);
                UpdateAvatarProfileCaches(profiles);
                ClosePersonaEditor();

                if (_previewPersona != null)
                    _previewPersona.text = AvatarPersonaText(_activeAvatarId);
                UpdatePersonaStateUi(_activeAvatarId);
                UpdateAvatarActionButtons(_activeAvatarId);

                if (_d.SyncActiveAvatarSystemPromptAsync != null)
                    await _d.SyncActiveAvatarSystemPromptAsync(app);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task DeleteSelectedAvatarAsync()
        {
            try
            {
                if (Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0)
                    return;

                var app = await _d.GetAppAsync();
                if (app == null) return;

                var all = app.Avatars.GetAll();
                var profile = all.Find(a => a != null && a.id == _activeAvatarId);
                if (profile == null) return;

                string imagePath = profile.imagePath;
                string modelPath = profile.modelPath;
                all.RemoveAll(a => a != null && a.id == _activeAvatarId);
                app.Avatars.SaveAll(all);

                ReleaseCustomTexture(imagePath);
                DeleteCustomAvatarFileIfUnused(imagePath, all);
                DeleteCustomAvatarFileIfUnused(modelPath, all);

                UpdateAvatarProfileCaches(all);
                _activeAvatarId = string.Empty;
                SelectAvatar(BuiltInAvatarIds[0]);
                RefreshCustomAvatarGallery(app);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task ResetPersonaOverrideAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app == null) return;

                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId);
                if (profile == null || string.IsNullOrWhiteSpace(profile.systemPrompt))
                {
                    UpdateAvatarActionButtons(_activeAvatarId);
                    return;
                }

                bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0;
                if (isBuiltIn)
                {
                    profiles.RemoveAll(a => a.id == _activeAvatarId && a.isBuiltIn);
                }
                else
                {
                    profile.systemPrompt = string.Empty;
                }

                app.Avatars.SaveAll(profiles);
                UpdateAvatarProfileCaches(profiles);

                if (_previewPersona != null)
                    _previewPersona.text = AvatarPersonaText(_activeAvatarId);
                UpdatePersonaStateUi(_activeAvatarId);
                UpdateAvatarActionButtons(_activeAvatarId);

                if (_d.SyncActiveAvatarSystemPromptAsync != null)
                    await _d.SyncActiveAvatarSystemPromptAsync(app);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void ApplyAvatarLayout(bool animated)
        {
            if (_avatarCircle != null)
            {
                if (animated)
                {
                    _avatarCircle.style.width          = new StyleLength(new Length(100, LengthUnit.Percent));
                    _avatarCircle.style.height         = new StyleLength(new Length(100, LengthUnit.Percent));
                    _avatarCircle.style.alignSelf      = Align.Stretch;
                    _avatarCircle.style.borderTopLeftRadius     = 0;
                    _avatarCircle.style.borderTopRightRadius    = 0;
                    _avatarCircle.style.borderBottomLeftRadius  = 0;
                    _avatarCircle.style.borderBottomRightRadius = 0;
                    _avatarCircle.style.borderTopWidth    = 0;
                    _avatarCircle.style.borderBottomWidth = 0;
                    _avatarCircle.style.borderLeftWidth   = 0;
                    _avatarCircle.style.borderRightWidth  = 0;
                    _avatarCircle.style.backgroundColor  = new StyleColor(Color.clear);
                }
                else
                {
                    _avatarCircle.style.width          = StyleKeyword.Null;
                    _avatarCircle.style.height         = StyleKeyword.Null;
                    _avatarCircle.style.alignSelf      = StyleKeyword.Null;
                    _avatarCircle.style.borderTopLeftRadius     = StyleKeyword.Null;
                    _avatarCircle.style.borderTopRightRadius    = StyleKeyword.Null;
                    _avatarCircle.style.borderBottomLeftRadius  = StyleKeyword.Null;
                    _avatarCircle.style.borderBottomRightRadius = StyleKeyword.Null;
                    _avatarCircle.style.borderTopWidth    = StyleKeyword.Null;
                    _avatarCircle.style.borderBottomWidth = StyleKeyword.Null;
                    _avatarCircle.style.borderLeftWidth   = StyleKeyword.Null;
                    _avatarCircle.style.borderRightWidth  = StyleKeyword.Null;
                    _avatarCircle.style.backgroundColor  = StyleKeyword.Null;
                }
            }

            if (_avatarStageHero != null)
            {
                if (animated)
                {
                    _avatarStageHero.style.paddingTop    = 0;
                    _avatarStageHero.style.paddingBottom = 0;
                    _avatarStageHero.style.paddingLeft   = 0;
                    _avatarStageHero.style.paddingRight  = 0;
                }
                else
                {
                    _avatarStageHero.style.paddingTop    = StyleKeyword.Null;
                    _avatarStageHero.style.paddingBottom = StyleKeyword.Null;
                    _avatarStageHero.style.paddingLeft   = StyleKeyword.Null;
                    _avatarStageHero.style.paddingRight  = StyleKeyword.Null;
                }
            }

            SetDisplay(_avatarGlow, animated ? DisplayStyle.None : DisplayStyle.Flex);

            if (_avatarArtImage != null)
                _avatarArtImage.scaleMode = animated ? ScaleMode.ScaleToFit : ScaleMode.ScaleAndCrop;
        }

        private void EnsureCustomizationOverlayElements()
        {
            if (_avatarArt != null && _avatarEmojiOverlay == null)
            {
                _avatarEmojiOverlay = new Label { name = "avatar-emoji-overlay" };
                _avatarEmojiOverlay.AddToClassList("avatar-emoji-overlay");
                _avatarEmojiOverlay.pickingMode = PickingMode.Ignore;
                _avatarArt.Add(_avatarEmojiOverlay);
            }

            if (_previewHero != null && _previewEmojiOverlay == null)
            {
                _previewEmojiOverlay = new Label { name = "preview-emoji-overlay" };
                _previewEmojiOverlay.AddToClassList("preview-emoji-overlay");
                _previewEmojiOverlay.pickingMode = PickingMode.Ignore;
                _previewHero.Add(_previewEmojiOverlay);
            }
        }

        private void OnAvatarCustomizationChanged(AvatarCustomizationData data) { ApplyAvatarCustomizationVisual(data); }
        private void OnAvatarCustomizationSaved()    { _ = SaveAvatarCustomizationAsync(); }
        private void OnAvatarCustomizationCanceled() { CancelCustomizationEdits(); }

        private void CancelCustomizationEdits()
        {
            _avatarCustomizationPanel?.Bind(_activeCustomizationBaseline);
            ApplyAvatarCustomizationVisual(_activeCustomizationBaseline);
        }

        private async Task SaveAvatarCustomizationAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app == null || _avatarCustomizationPanel == null)
                    return;

                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId);
                var data = CloneCustomization(GetPanelCustomizationData());
                bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0;
                bool shouldStore = !IsCustomizationEffectivelyDefault(data);

                if (profile == null && !isBuiltIn)
                    return;

                if (profile == null && shouldStore)
                {
                    profile = new AvatarProfile
                    {
                        id = _activeAvatarId,
                        isBuiltIn = true,
                        name = string.Empty,
                        imagePath = string.Empty,
                        systemPrompt = string.Empty
                    };
                    profiles.Add(profile);
                }

                if (profile != null)
                {
                    profile.customization = shouldStore ? data : null;
                    if (isBuiltIn && string.IsNullOrWhiteSpace(profile.systemPrompt) && profile.customization == null)
                        profiles.RemoveAll(a => a.id == _activeAvatarId && a.isBuiltIn);
                }

                app.Avatars.SaveAll(profiles);
                UpdateAvatarProfileCaches(profiles);
                _activeCustomizationBaseline = CloneCustomization(shouldStore ? data : null);
                _avatarCustomizationPanel.Bind(_activeCustomizationBaseline);
                ApplyAvatarCustomizationVisual(_activeCustomizationBaseline);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private AvatarCustomizationData GetPanelCustomizationData()
        {
            return _avatarCustomizationPanel?.CurrentData ?? _activeCustomizationBaseline;
        }

        private void ApplyAvatarCustomizationVisual(AvatarCustomizationData data)
        {
            var effective = CloneCustomization(data) ?? new AvatarCustomizationData();
            if (_avatarArt != null)
                _avatarArt.style.unityBackgroundImageTintColor = new StyleColor(BuildTintColor(effective.PrimaryColor, effective.Saturation, effective.Brightness));
            if (_previewHero != null)
                _previewHero.style.unityBackgroundImageTintColor = new StyleColor(BuildTintColor(effective.PrimaryColor, effective.Saturation, effective.Brightness));

            if (_avatarCircle != null)
            {
                _avatarCircle.style.borderBottomColor = new StyleColor(ParseHtmlColor(effective.SecondaryColor, new Color(0.486f, 0.478f, 0.929f)));
                _avatarCircle.style.borderTopColor    = _avatarCircle.style.borderBottomColor;
                _avatarCircle.style.borderLeftColor   = _avatarCircle.style.borderBottomColor;
                _avatarCircle.style.borderRightColor  = _avatarCircle.style.borderBottomColor;
            }

            var halo = _d.Root?.Q<VisualElement>("avatar-glow");
            if (halo != null)
            {
                var haloColor = ParseHtmlColor(effective.HaloColor, new Color(0.486f, 0.478f, 0.929f));
                haloColor.a = Mathf.Clamp01(effective.HaloIntensity) * 0.55f;
                halo.style.backgroundColor = new StyleColor(haloColor);
                halo.style.opacity = Mathf.Clamp(0.15f + effective.HaloIntensity, 0f, 1f);
            }

            string emoji = effective.OverlayEmoji ?? string.Empty;
            if (_avatarEmojiOverlay != null)
            {
                _avatarEmojiOverlay.text = emoji;
                SetDisplay(_avatarEmojiOverlay, string.IsNullOrEmpty(emoji) ? DisplayStyle.None : DisplayStyle.Flex);
            }
            if (_previewEmojiOverlay != null)
            {
                _previewEmojiOverlay.text = emoji;
                SetDisplay(_previewEmojiOverlay, string.IsNullOrEmpty(emoji) ? DisplayStyle.None : DisplayStyle.Flex);
            }

            SetFrameClass(_avatarCircle, effective.CustomFrame, "avatar-frame");
            SetFrameClass(_previewHero, effective.CustomFrame, "preview-frame");
        }

        private void EnsureAvatarAnimationImage()
        {
            if (_avatarArt == null || _avatarArtImage != null)
                return;

            _avatarArtImage = new Image { name = "avatar-art-image" };
            _avatarArtImage.pickingMode = PickingMode.Ignore;
            _avatarArtImage.style.position = Position.Absolute;
            _avatarArtImage.style.left = 0;
            _avatarArtImage.style.right = 0;
            _avatarArtImage.style.top = 0;
            _avatarArtImage.style.bottom = 0;
            _avatarArtImage.scaleMode = ScaleMode.ScaleAndCrop;
            _avatarArt.Add(_avatarArtImage);

            if (_d.GetOrCreateAnimator != null)
                _avatarAnimator = _d.GetOrCreateAnimator();
        }

        private void EnsureAvatar3DImage()
        {
            if (_avatarArt == null || _avatar3DImage != null)
                return;

            _avatar3DImage = new Image { name = "avatar-art-3d-image" };
            _avatar3DImage.pickingMode = PickingMode.Position;
            _avatar3DImage.style.position = Position.Absolute;
            _avatar3DImage.style.left = 0;
            _avatar3DImage.style.right = 0;
            _avatar3DImage.style.top = 0;
            _avatar3DImage.style.bottom = 0;
            _avatar3DImage.scaleMode = ScaleMode.ScaleAndCrop;
            _avatarArt.Add(_avatar3DImage);

            if (_d.GetOrCreateAvatar3DRenderer != null)
            {
                _avatar3DRenderer = _d.GetOrCreateAvatar3DRenderer();
                _avatar3DRenderer.AttachTargetImage(_avatar3DImage);
            }

            if (_avatar3DService == null)
            {
                var app = _d.GetAppSync != null ? _d.GetAppSync() : null;
                if (app != null && app.Services.TryGet<IAvatar3DService>(out var sharedService))
                    _avatar3DService = sharedService;
                else
                    _avatar3DService = new Avatar3DService();
            }

            SetDisplay(_avatar3DImage, DisplayStyle.None);
        }

        private bool ConfigureAvatarAnimation(AvatarProfile profile)
        {
            EnsureAvatarAnimationImage();
            EnsureAvatar3DImage();

            if (_avatarAnimator == null || _avatarArtImage == null)
            {
                NeonLogger.LogWarning("[AvatarAnim] EnsureAvatarAnimationImage failed: " +
                    "_avatarAnimator=" + (_avatarAnimator == null ? "null" : "ok") +
                    " _avatarArtImage=" + (_avatarArtImage == null ? "null" : "ok") +
                    " _avatarArt=" + (_avatarArt == null ? "null" : "ok"));
                return false;
            }

            if (_avatarViewMode != AvatarViewMode.Animated)
            {
                _avatarAnimator.Stop();
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
                return false;
            }

            var resolvedMotion = AvatarMotionPackLoader.ResolveProfileMotion(profile) ?? new AvatarProfileMotionResolution();
            var clips = resolvedMotion.animationClips;
            NeonLogger.Log("[AvatarAnim] ResolveProfileMotion for '" + (profile?.id ?? "null") +
                "' -> clips=" + (clips?.Count.ToString() ?? "null") +
                " manifest=" + (resolvedMotion.sourceManifestPath ?? "none"));
            if (clips == null || clips.Count == 0)
            {
                _avatarAnimator.Stop();
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
                if (_avatar3DImage != null)
                    SetDisplay(_avatar3DImage, DisplayStyle.None);
                return false;
            }

            _avatarAnimator.Configure(clips, _avatarArtImage);
            if (resolvedMotion.lipsyncClip != null)
                _avatarAnimator.RegisterClip(resolvedMotion.lipsyncClip);
            NeonLogger.Log("[AvatarAnim] After Configure: HasAnyClips=" + _avatarAnimator.HasAnyClips);
            if (!_avatarAnimator.HasAnyClips)
            {
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
                return false;
            }

            SetDisplay(_avatarArtImage, DisplayStyle.Flex);
            if (_avatar3DImage != null)
                SetDisplay(_avatar3DImage, DisplayStyle.None);
            RefreshAvatarMotionState();

            return true;
        }

        private async Task ConfigureAvatar3DAsync(AvatarProfile profile)
        {
            Disable2DAvatarAnimation();
            EnsureAvatar3DImage();
            if (_avatar3DService == null || _avatar3DRenderer == null || _avatar3DImage == null)
                return;

            if (profile == null || string.IsNullOrWhiteSpace(profile.modelPath))
            {
                Disable3DAvatarRender();
                return;
            }

            bool loaded = await _avatar3DService.LoadAvatar(profile.modelPath);
            if (!loaded)
            {
                Disable3DAvatarRender();
                return;
            }

            var runtimeRoot = _avatar3DService.GetRuntimeRoot();
            if (runtimeRoot == null)
            {
                Disable3DAvatarRender();
                return;
            }

            Transform parent = _d.ModelParent;
            if (parent != null)
            {
                runtimeRoot.transform.SetParent(parent, false);
                runtimeRoot.transform.localPosition = Vector3.zero;
                runtimeRoot.transform.localRotation = Quaternion.identity;
                runtimeRoot.transform.localScale = Vector3.one;
            }

            _avatar3DRenderer.SetModelRoot(_avatar3DService.GetRuntimeTransform());
            SetDisplay(_avatar3DImage, DisplayStyle.Flex);
            RefreshAvatarMotionState();
        }

        private void Disable2DAvatarAnimation()
        {
            if (_avatarAnimator != null)
                _avatarAnimator.Stop();

            if (_avatarArtImage != null)
            {
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
            }
        }

        private void Disable3DAvatarRender()
        {
            _avatar3DService?.Unload();
            _avatar3DRenderer?.ClearModel();
            if (_avatar3DImage != null)
                SetDisplay(_avatar3DImage, DisplayStyle.None);
        }

        private string ResolveAvailableClipForState(AvatarMotionState state)
        {
            string preferred = StateToClipName(state);
            if (_avatarAnimator != null && _avatarAnimator.HasClip(preferred))
                return preferred;

            return "idle";
        }

        private static string StateToClipName(AvatarMotionState state)
        {
            switch (state)
            {
                case AvatarMotionState.Thinking:  return "thinking";
                case AvatarMotionState.Talking:   return "talking";
                case AvatarMotionState.Listening: return "listening";
                default:                          return "idle";
            }
        }

        private void PlayAvatarReaction(string reactionClipName)
        {
            if (string.IsNullOrWhiteSpace(reactionClipName))
                return;

            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                NeonLogger.LogWarning("3D avatar reaction is not implemented for clip '" + reactionClipName + "'.");
                return;
            }

            if (_avatarAnimator == null || !_avatarAnimator.HasAnyClips)
                return;

            if (!_avatarAnimator.HasClip(reactionClipName))
                return;

            _avatarAnimator.PlayOneShot(reactionClipName, RefreshAvatarMotionState);
        }

        private static string BuildAnimationInfoText(AvatarProfile profile)
        {
            if (profile != null && profile.is3D)
                return LocalizationExtensions.Get("avatar.animation.3d", "3D модель");

            var resolved = AvatarMotionPackLoader.ResolveProfileMotion(profile);
            if (resolved == null || resolved.animationClips == null || resolved.animationClips.Count == 0)
                return LocalizationExtensions.Get("avatar.animation.none", "Анимация недоступна");

            return LocalizationExtensions.GetFormat("avatar.animation.clips", "{0} клипов", resolved.animationClips.Count);
        }

        private AvatarProfile GetCustomProfile(string avatarId)
        {
            return _cachedCustomProfiles?.FirstOrDefault(a => a.id == avatarId);
        }

        private AvatarProfile GetStoredProfile(string avatarId)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                return null;

            return _cachedProfilesById.TryGetValue(avatarId, out var profile) ? profile : null;
        }

        private void UpdateAvatarProfileCaches(List<AvatarProfile> profiles)
        {
            _cachedProfilesById.Clear();
            if (profiles == null)
            {
                _cachedCustomProfiles = new List<AvatarProfile>();
                return;
            }

            foreach (var profile in profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.id))
                    continue;

                _cachedProfilesById[profile.id] = profile;
            }

            _cachedCustomProfiles = profiles.Where(a => a != null && !a.isBuiltIn).ToList();
        }

        private VisualElement CreateCustomAvatarTile(AvatarProfile profile)
        {
            var tile = new VisualElement();
            tile.name = $"avtile-{profile.id}";
            tile.AddToClassList("avtile");

            var texture = GetOrLoadTexture(profile.imagePath);
            if (texture != null)
            {
                tile.style.backgroundImage = new StyleBackground(texture);
            }
            else
            {
                tile.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.30f));
            }

            var nameLabel = new Label(string.IsNullOrWhiteSpace(profile.name) ? profile.id : profile.name);
            nameLabel.AddToClassList("avtile__name");
            tile.Add(nameLabel);

            var badge = new VisualElement();
            badge.AddToClassList("avtile__badge");
            var checkIcon = new VisualElement();
            checkIcon.AddToClassList("icon");
            checkIcon.AddToClassList("icon--check");
            badge.Add(checkIcon);
            tile.Add(badge);

            string capturedId = profile.id;
            tile.RegisterCallback<ClickEvent>(_ => SelectAvatar(capturedId));
            return tile;
        }

        private void UpdateAvatarFilterChipState()
        {
            SetFilterChipActive(_avatarFilterAllBtn,      _activeAvatarFilter == AvatarFilter.All);
            SetFilterChipActive(_avatarFilterStandardBtn, _activeAvatarFilter == AvatarFilter.Standard);
            SetFilterChipActive(_avatarFilterGradientBtn, _activeAvatarFilter == AvatarFilter.Gradient);
            SetFilterChipActive(_avatarFilterMinimalBtn,  _activeAvatarFilter == AvatarFilter.Minimal);
            SetFilterChipActive(_avatarFilterCustomBtn,   _activeAvatarFilter == AvatarFilter.Custom);
        }

        private static void SetFilterChipActive(Button button, bool active)
        {
            if (button != null)
                button.EnableInClassList("filterchip--active", active);
        }

        private static void SetCountLabel(Label label, int value)
        {
            if (label != null)
                label.text = value.ToString();
        }

        private bool MatchesFilter(string avatarId)
        {
            if (_activeAvatarFilter == AvatarFilter.All)
                return true;

            if (GetCustomProfile(avatarId) != null)
                return _activeAvatarFilter == AvatarFilter.Custom;

            return GetBuiltInFilter(avatarId) == _activeAvatarFilter;
        }

        private static AvatarFilter GetBuiltInFilter(string avatarId)
        {
            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return meta.Filter;

            return AvatarFilter.Standard;
        }

        private string GetFirstAvatarForActiveFilter()
        {
            foreach (var id in BuiltInAvatarIds)
            {
                if (MatchesFilter(id))
                    return id;
            }

            var custom = _cachedCustomProfiles?.FirstOrDefault();
            return custom?.id;
        }

        private Texture2D GetOrLoadTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (_customTextures.TryGetValue(path, out var cached))
                return cached;

            var texture = LoadTextureFromFile(path);
            if (texture != null)
                _customTextures[path] = texture;

            return texture;
        }

        private void ReleaseCustomTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (_customTextures.TryGetValue(path, out var texture))
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
                _customTextures.Remove(path);
            }
        }

        private string PersonaEditorText(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            if (stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt))
                return stored.systemPrompt;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.PersonaKey, meta.PersonaFallback);

            return string.Empty;
        }

        private bool HasPersonaOverride(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            return stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt);
        }

        private string AvatarStyleTag(string avatarId)
        {
            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.StyleTagKey, meta.StyleTagFallback);

            return LocalizationExtensions.Get("avatar.style.custom", "пользовательский");
        }

        public string AvatarPersonaText(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            if (stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt))
                return stored.systemPrompt;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.PersonaKey, meta.PersonaFallback);

            if (GetCustomProfile(avatarId) != null)
                return LocalizationExtensions.Get("avatar.persona.custom.missing", "Инструкции не заданы. Нажми «Изменить», чтобы добавить текст для system prompt.");

            var fallbackMeta = BuiltInAvatarMetaById["neon"];
            return LocalizationExtensions.Get(fallbackMeta.PersonaKey, fallbackMeta.PersonaFallback);
        }

        private void OnAvatarUploadClicked() { _ = UploadAvatarAsync(); }

        private async Task UploadAvatarAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickFileAsync("png,glb,gltf");
                if (string.IsNullOrEmpty(path)) return;

                string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                bool is3D = extension == ".glb" || extension == ".gltf";
                string destDir  = System.IO.Path.Combine(Application.persistentDataPath, "Avatars");
                System.IO.Directory.CreateDirectory(destDir);
                string destPath = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(path));
                System.IO.File.Copy(path, destPath, overwrite: true);

                var all = app.Avatars.GetAll();
                string profileId = ResolveCustomAvatarId(fileName, destPath, all);
                var modelAnimations = new List<string>();

                if (is3D)
                {
                    var loadResult = await Avatar3DLoader.LoadAsync(destPath);
                    if (loadResult.Success && loadResult.Instance != null)
                    {
                        modelAnimations.AddRange(loadResult.AnimationNames);
                        UnityEngine.Object.Destroy(loadResult.Instance);
                    }
                }

                var profile = new AvatarProfile
                {
                    id        = profileId,
                    name      = fileName,
                    imagePath = is3D ? string.Empty : destPath,
                    modelPath = is3D ? destPath : string.Empty,
                    isBuiltIn = false,
                    is3D = is3D,
                    modelAnimationClips = modelAnimations
                };

                int existing = all.FindIndex(a => a != null && a.id == profile.id);
                if (existing >= 0) all[existing] = profile;
                else all.Add(profile);
                app.Avatars.SaveAll(all);

                RefreshCustomAvatarGallery(app);

                if (_activeAvatarId == profile.id) _activeAvatarId = string.Empty;
                SelectAvatar(profile.id);

                _d.AddSystemMessage?.Invoke(is3D
                    ? LocalizationExtensions.GetFormat("avatar.upload.success.3d", "3D аватар «{0}» загружен.", fileName)
                    : LocalizationExtensions.GetFormat("avatar.upload.success", "Аватар «{0}» загружен.", fileName));
            }
            catch (Exception ex)
            {
                _d.AddSystemMessage?.Invoke(LocalizationExtensions.Get("avatar.upload.failed", "Не удалось загрузить аватар."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        private string ResolveCustomAvatarId(string fileName, string imagePath, List<AvatarProfile> allProfiles)
        {
            string existingByPath = allProfiles?
                .FirstOrDefault(a =>
                    a != null &&
                    !a.isBuiltIn &&
                    (string.Equals(a.imagePath, imagePath, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a.modelPath, imagePath, StringComparison.OrdinalIgnoreCase)))
                ?.id;
            if (!string.IsNullOrWhiteSpace(existingByPath))
                return existingByPath;

            string baseId = BuildCustomAvatarBaseId(fileName);
            string candidate = baseId;
            int suffix = 2;

            while (ContainsAvatarId(candidate, allProfiles))
            {
                candidate = $"{baseId}_{suffix}";
                suffix++;
            }

            return candidate;
        }

        private static bool ContainsAvatarId(string candidateId, List<AvatarProfile> allProfiles)
        {
            if (string.IsNullOrWhiteSpace(candidateId))
                return false;

            if (Array.IndexOf(BuiltInAvatarIds, candidateId) >= 0)
                return true;

            return allProfiles != null && allProfiles.Any(a => a != null && string.Equals(a.id, candidateId, StringComparison.Ordinal));
        }

        private static string BuildCustomAvatarBaseId(string fileName)
        {
            string source = (fileName ?? string.Empty).Trim().ToLowerInvariant();
            var sb = new StringBuilder(source.Length);
            bool previousWasSeparator = false;

            foreach (char ch in source)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    previousWasSeparator = false;
                    continue;
                }

                if (!previousWasSeparator)
                {
                    sb.Append('_');
                    previousWasSeparator = true;
                }
            }

            string normalized = sb.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "avatar";

            return $"custom_{normalized}";
        }

        private static void DeleteCustomAvatarFileIfUnused(string path, List<AvatarProfile> profiles)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            bool isStillReferenced = profiles.Any(a =>
                a != null &&
                !a.isBuiltIn &&
                (string.Equals(a.imagePath, path, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.modelPath, path, StringComparison.OrdinalIgnoreCase)));
            if (isStillReferenced || !System.IO.File.Exists(path))
                return;

            System.IO.File.Delete(path);
        }

        private void OnAvatarOpenFolderClicked()
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "Avatars");
            System.IO.Directory.CreateDirectory(dir);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            System.Diagnostics.Process.Start("explorer.exe", dir.Replace('/', '\\'));
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            System.Diagnostics.Process.Start("open", dir);
#else
            Application.OpenURL("file://" + dir);
#endif
        }

        private void TickTyping()
        {
            int step = _typingFrame % 3;
            SetDotOpacity(_typingDot1, step == 0 ? 1f : 0.25f);
            SetDotOpacity(_typingDot2, step == 1 ? 1f : 0.25f);
            SetDotOpacity(_typingDot3, step == 2 ? 1f : 0.25f);
            _typingFrame++;
        }

        private static void SetDotOpacity(VisualElement dot, float opacity)
        {
            if (dot != null) dot.style.opacity = opacity;
        }

        private static Texture2D LoadTextureFromFile(string path)
        {
            if (!System.IO.File.Exists(path))
                return null;

            var bytes = System.IO.File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2);
            if (texture.LoadImage(bytes))
                return texture;

            UnityEngine.Object.Destroy(texture);
            return null;
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }

        private static void RegisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }

        private static void UnregisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked -= handler;
        }

        private static void RegisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.RegisterCallback<ClickEvent>(handler);
        }

        private static void UnregisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.UnregisterCallback<ClickEvent>(handler);
        }

        private static void SetFrameClass(VisualElement element, string frame, string prefix)
        {
            if (element == null) return;
            element.EnableInClassList($"{prefix}--none", false);
            element.EnableInClassList($"{prefix}--neon", false);
            element.EnableInClassList($"{prefix}--gold", false);
            element.EnableInClassList($"{prefix}--holographic", false);
            string normalized = string.IsNullOrWhiteSpace(frame) ? "none" : frame.ToLowerInvariant();
            element.EnableInClassList($"{prefix}--{normalized}", true);
        }

        private static Color BuildTintColor(string hex, float saturation, float brightness)
        {
            var baseColor = ParseHtmlColor(hex, Color.white);
            float gray = baseColor.r * 0.299f + baseColor.g * 0.587f + baseColor.b * 0.114f;
            var saturated = new Color(
                Mathf.Clamp01(gray + (baseColor.r - gray) * Mathf.Clamp(saturation, 0f, 2f)),
                Mathf.Clamp01(gray + (baseColor.g - gray) * Mathf.Clamp(saturation, 0f, 2f)),
                Mathf.Clamp01(gray + (baseColor.b - gray) * Mathf.Clamp(saturation, 0f, 2f)),
                1f);
            float b = Mathf.Clamp(brightness, 0f, 2f);
            return new Color(
                Mathf.Clamp01(saturated.r * b),
                Mathf.Clamp01(saturated.g * b),
                Mathf.Clamp01(saturated.b * b),
                1f);
        }

        private static Color ParseHtmlColor(string hex, Color fallback)
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex, out var parsed))
                return parsed;
            return fallback;
        }

        private static AvatarCustomizationData CloneCustomization(AvatarCustomizationData source)
        {
            if (source == null) return null;
            return new AvatarCustomizationData
            {
                PrimaryColor   = source.PrimaryColor,
                SecondaryColor = source.SecondaryColor,
                HaloColor      = source.HaloColor,
                HaloIntensity  = source.HaloIntensity,
                Saturation     = source.Saturation,
                Brightness     = source.Brightness,
                OverlayEmoji   = source.OverlayEmoji,
                CustomFrame    = source.CustomFrame
            };
        }

        private static bool IsCustomizationEffectivelyDefault(AvatarCustomizationData data)
        {
            if (data == null) return true;
            bool defaultColors = string.Equals((data.PrimaryColor ?? string.Empty).ToUpperInvariant(), "#FFFFFF", StringComparison.Ordinal)
                && string.Equals((data.SecondaryColor ?? string.Empty).ToUpperInvariant(), "#7C7AED", StringComparison.Ordinal)
                && string.Equals((data.HaloColor ?? string.Empty).ToUpperInvariant(), "#7C7AED", StringComparison.Ordinal);
            bool defaultScalars = Mathf.Abs(data.HaloIntensity - 0.6f) < 0.0001f
                && Mathf.Abs(data.Saturation - 1f) < 0.0001f
                && Mathf.Abs(data.Brightness - 1f) < 0.0001f;
            bool defaultOverlay = string.IsNullOrEmpty(data.OverlayEmoji)
                && (string.IsNullOrWhiteSpace(data.CustomFrame) || string.Equals(data.CustomFrame, "none", StringComparison.OrdinalIgnoreCase));
            return defaultColors && defaultScalars && defaultOverlay;
        }

        // ---- Nested types ----

        private enum AvatarViewMode { Static, Animated, Volume3D }

        private readonly struct BuiltInAvatarMeta
        {
            public BuiltInAvatarMeta(
                string displayNameKey,
                string displayNameFallback,
                string styleTagKey,
                string styleTagFallback,
                string personaKey,
                string personaFallback,
                AvatarFilter filter)
            {
                DisplayNameKey      = displayNameKey;
                DisplayNameFallback = displayNameFallback;
                StyleTagKey         = styleTagKey;
                StyleTagFallback    = styleTagFallback;
                PersonaKey          = personaKey;
                PersonaFallback     = personaFallback;
                Filter              = filter;
            }

            public string DisplayNameKey      { get; }
            public string DisplayNameFallback { get; }
            public string StyleTagKey         { get; }
            public string StyleTagFallback    { get; }
            public string PersonaKey          { get; }
            public string PersonaFallback     { get; }
            public AvatarFilter Filter        { get; }
        }
    }
}
