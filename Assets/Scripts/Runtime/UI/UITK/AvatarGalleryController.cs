using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public Action<string> AddSystemMessage;
            // Motion state inputs
            public Func<bool> IsChatSending;
            public Func<bool> IsChatStreamingResponse;
            public Func<bool> GetIsVoicePlaying;
            public Func<bool> GetIsVoiceRecording;
            public Action<string> AvatarChanged;
            public Action<AvatarMotionState> AvatarMotionStateChanged;
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
            { "neon", "yorha-2b", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" };

        private static readonly Dictionary<string, BuiltInAvatarMeta> BuiltInAvatarMetaById = new Dictionary<string, BuiltInAvatarMeta>
        {
            ["neon"]   = new BuiltInAvatarMeta("avatar.builtin.neon.name", "Неон", "avatar.builtin.neon.style", "стандартный", "avatar.builtin.neon.persona", "Неон — спокойный и практичный AI-компаньон разработчика. Отвечает кратко, структурно и по делу.", AvatarFilter.Standard),
            ["yorha-2b"] = new BuiltInAvatarMeta("avatar.builtin.yorha2b.name", "YoRHa 2B", "avatar.builtin.yorha2b.style", "пиксельный · анимированный", "avatar.builtin.yorha2b.persona", "2B — сдержанный и собранный AI-компаньон. Отвечает точно, кратко и по существу.", AvatarFilter.Minimal),
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
        private Avatar3DStateClipMapping _active3DClipMapping;
        private AvatarMotionState _avatarMotionState = AvatarMotionState.Idle;
        private List<AvatarProfile> _cachedCustomProfiles = new List<AvatarProfile>();
        private readonly Dictionary<string, AvatarProfile> _cachedProfilesById = new Dictionary<string, AvatarProfile>();
        private readonly Dictionary<string, VisualElement> _customAvatarTiles = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, Texture2D> _customTextures = new Dictionary<string, Texture2D>();
        private AvatarCustomizationPanel _avatarCustomizationPanel;
        private AvatarCustomizationData _activeCustomizationBaseline;
        private string _catalogOnlyPreviewId;

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
        private VisualElement _avtileYorha2bAnimated;
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
        private Foldout _avatarPersonaFoldout;
        private Foldout _avatarCustomizationFoldout;
        private Label _avatarCapabilityType;
        private Label _avatarCapabilityRender;
        private Label _avatarCapabilityAnimation;
        private Label _avatarCapabilityHumanoid;
        private Label _avatarCapabilityBlink;
        private Label _avatarCapabilityGaze;
        private Label _avatarCapabilityExpressions;
        private Label _avatarCapabilityLipsync;
        private Label _avatarCapabilityEvidence;
        private Label _avatarCapabilityDiagnostic;

        // Avatar import flow
        private VisualElement _avatarImportOverlay;
        private Button _avatarImportCloseBtn;
        private Button _avatarImportCancelBtn;
        private Button _avatarImportSaveBtn;
        private Button _avatarImportChooseBtn;
        private Button _avatarImportStaticBtn;
        private Button _avatarImportSpriteBtn;
        private Button _avatarImport3DBtn;
        private Button _avatarImportVrmBtn;
        private Label _avatarImportTypeHelp;
        private Label _avatarImportSource;
        private Image _avatarImportPreviewImage;
        private Label _avatarImportPreviewPlaceholder;
        private Label _avatarImportCapabilityRender;
        private Label _avatarImportCapabilityAnimation;
        private Label _avatarImportCapabilityHumanoid;
        private Label _avatarImportCapabilityBlink;
        private Label _avatarImportCapabilityGaze;
        private Label _avatarImportCapabilityExpressions;
        private Label _avatarImportCapabilityLipsync;
        private Label _avatarImportCapabilityScene;
        private Label _avatarImportDiagnostic;
        private VisualElement _avatarImportMapping;
        private TextField _avatarImportClipIdle;
        private TextField _avatarImportClipThinking;
        private TextField _avatarImportClipTalking;
        private TextField _avatarImportClipListening;
        private TextField _avatarImportClipSmile;
        private TextField _avatarImportClipConfused;
        private string _avatarImportType = AvatarProfileTypes.Static2D;
        private AvatarAssetInspection _avatarImportInspection;
        private Texture2D _avatarImportPreviewTexture;
        private GameObject _avatarImportPreviewModel;
        private int _avatarImportRequestVersion;
        private int _avatarSelectionRequestVersion;

        // ---- Public properties ----

        public string ActiveAvatarId
        {
            get { return _activeAvatarId; }
            set
            {
                _activeAvatarId = value;
                _d.AvatarChanged?.Invoke(value);
            }
        }

        public SpriteSheetAnimator GetAvatarAnimatorInstance() { return _avatarAnimator; }
        public IAvatar3DService GetAvatar3DServiceInstance() { return _avatar3DService; }

        public string AvatarViewModeSetting
        {
            get { return AvatarViewModeToSetting(_avatarViewMode); }
        }

        public void SetAvatarViewModeFromSetting(string value)
        {
            AvatarViewMode mode = ParseAvatarViewMode(value);
            _avatarViewMode = mode;
            ApplyAvatarViewMode();
        }

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
            _avtileYorha2bAnimated = root.Q<VisualElement>("avtile-yorha-2b-animated");
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
            _avatarCapabilityType = root.Q<Label>("avatar-capability-type");
            _avatarCapabilityRender = root.Q<Label>("avatar-capability-render");
            _avatarCapabilityAnimation = root.Q<Label>("avatar-capability-animation");
            _avatarCapabilityHumanoid = root.Q<Label>("avatar-capability-humanoid");
            _avatarCapabilityBlink = root.Q<Label>("avatar-capability-blink");
            _avatarCapabilityGaze = root.Q<Label>("avatar-capability-gaze");
            _avatarCapabilityExpressions = root.Q<Label>("avatar-capability-expressions");
            _avatarCapabilityLipsync = root.Q<Label>("avatar-capability-lipsync");
            _avatarCapabilityEvidence = root.Q<Label>("avatar-capability-evidence");
            _avatarCapabilityDiagnostic = root.Q<Label>("avatar-capability-diagnostic");
            _avatarPersonaFoldout = root.Q<Foldout>("avatar-persona-foldout");
            _avatarCustomizationFoldout = root.Q<Foldout>("avatar-customization-foldout");
            _avatarImportOverlay = root.Q<VisualElement>("avatar-import-overlay");
            _avatarImportCloseBtn = root.Q<Button>("avatar-import-close-btn");
            _avatarImportCancelBtn = root.Q<Button>("avatar-import-cancel-btn");
            _avatarImportSaveBtn = root.Q<Button>("avatar-import-save-btn");
            _avatarImportChooseBtn = root.Q<Button>("avatar-import-choose-btn");
            _avatarImportStaticBtn = root.Q<Button>("avatar-import-static-btn");
            _avatarImportSpriteBtn = root.Q<Button>("avatar-import-sprite-btn");
            _avatarImport3DBtn = root.Q<Button>("avatar-import-3d-btn");
            _avatarImportVrmBtn = root.Q<Button>("avatar-import-vrm-btn");
            _avatarImportTypeHelp = root.Q<Label>("avatar-import-type-help");
            _avatarImportSource = root.Q<Label>("avatar-import-source");
            _avatarImportPreviewImage = root.Q<Image>("avatar-import-preview-image");
            _avatarImportPreviewPlaceholder = root.Q<Label>("avatar-import-preview-placeholder");
            _avatarImportCapabilityRender = root.Q<Label>("avatar-import-capability-render");
            _avatarImportCapabilityAnimation = root.Q<Label>("avatar-import-capability-animation");
            _avatarImportCapabilityHumanoid = root.Q<Label>("avatar-import-capability-humanoid");
            _avatarImportCapabilityBlink = root.Q<Label>("avatar-import-capability-blink");
            _avatarImportCapabilityGaze = root.Q<Label>("avatar-import-capability-gaze");
            _avatarImportCapabilityExpressions = root.Q<Label>("avatar-import-capability-expressions");
            _avatarImportCapabilityLipsync = root.Q<Label>("avatar-import-capability-lipsync");
            _avatarImportCapabilityScene = root.Q<Label>("avatar-import-capability-scene");
            _avatarImportDiagnostic = root.Q<Label>("avatar-import-diagnostic");
            _avatarImportMapping = root.Q<VisualElement>("avatar-import-mapping");
            _avatarImportClipIdle = root.Q<TextField>("avatar-import-clip-idle");
            _avatarImportClipThinking = root.Q<TextField>("avatar-import-clip-thinking");
            _avatarImportClipTalking = root.Q<TextField>("avatar-import-clip-talking");
            _avatarImportClipListening = root.Q<TextField>("avatar-import-clip-listening");
            _avatarImportClipSmile = root.Q<TextField>("avatar-import-clip-smile");
            _avatarImportClipConfused = root.Q<TextField>("avatar-import-clip-confused");
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
            SetDisplay(_avatarImportOverlay, DisplayStyle.None);
            SetDisplay(_avatarImportMapping, DisplayStyle.None);
            if (_avatarImportPreviewImage != null)
                _avatarImportPreviewImage.scaleMode = ScaleMode.ScaleToFit;
            LocalizeImportUi(root);
            ResetImportInspectionUi();

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
            RegisterClick(_avtileYorha2bAnimated, OnYorha2bAnimatedTileClicked);
            RegisterClick(_avatarFilterAllBtn,      OnAvatarFilterAllClicked);
            RegisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            RegisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            RegisterClick(_avatarFilterMinimalBtn,  OnAvatarFilterMinimalClicked);
            RegisterClick(_avatarFilterCustomBtn,   OnAvatarFilterCustomClicked);
            RegisterClick(_avatarUploadBtn, OnAvatarUploadClicked);
            RegisterClick(_avatarOpenFolderBtn, OnAvatarOpenFolderClicked);
            RegisterClick(_avatarUploadTile, OnAvatarUploadTileClicked);
            RegisterClick(_avatarImportCloseBtn, CloseAvatarImport);
            RegisterClick(_avatarImportCancelBtn, CloseAvatarImport);
            RegisterClick(_avatarImportSaveBtn, OnAvatarImportSaveClicked);
            RegisterClick(_avatarImportChooseBtn, OnAvatarImportChooseClicked);
            RegisterClick(_avatarImportStaticBtn, OnAvatarImportStaticClicked);
            RegisterClick(_avatarImportSpriteBtn, OnAvatarImportSpriteClicked);
            RegisterClick(_avatarImport3DBtn, OnAvatarImport3DClicked);
            RegisterClick(_avatarImportVrmBtn, OnAvatarImportVrmClicked);

            RegisterAvatarGalleryCallbacks();

            // Apply initial gallery visibility so gallery-animated / gallery-3d are
            // hidden from the start (their UXML default is Flex, not None).
            ApplyAvatarViewMode();
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
            UnregisterClick(_avtileYorha2bAnimated, OnYorha2bAnimatedTileClicked);
            UnregisterClick(_avatarFilterAllBtn,      OnAvatarFilterAllClicked);
            UnregisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            UnregisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            UnregisterClick(_avatarFilterMinimalBtn,  OnAvatarFilterMinimalClicked);
            UnregisterClick(_avatarFilterCustomBtn,   OnAvatarFilterCustomClicked);
            UnregisterClick(_avatarUploadBtn, OnAvatarUploadClicked);
            UnregisterClick(_avatarOpenFolderBtn, OnAvatarOpenFolderClicked);
            UnregisterClick(_avatarUploadTile, OnAvatarUploadTileClicked);
            UnregisterClick(_avatarImportCloseBtn, CloseAvatarImport);
            UnregisterClick(_avatarImportCancelBtn, CloseAvatarImport);
            UnregisterClick(_avatarImportSaveBtn, OnAvatarImportSaveClicked);
            UnregisterClick(_avatarImportChooseBtn, OnAvatarImportChooseClicked);
            UnregisterClick(_avatarImportStaticBtn, OnAvatarImportStaticClicked);
            UnregisterClick(_avatarImportSpriteBtn, OnAvatarImportSpriteClicked);
            UnregisterClick(_avatarImport3DBtn, OnAvatarImport3DClicked);
            UnregisterClick(_avatarImportVrmBtn, OnAvatarImportVrmClicked);
        }

        public void OnDisable()
        {
            _avatarImportRequestVersion++;
            _avatarMotionState = AvatarMotionState.Idle;
            _avatarAnimator?.Stop();
            _avatar3DService?.Unload();
            _typingSchedule?.Pause();
            foreach (var tex in _customTextures.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            _customTextures.Clear();
            ReleaseImportPreviewTexture();
            ReleaseImportPreviewModel();
            SetDisplay(_avatarImportOverlay, DisplayStyle.None);
        }

        // ---- Gallery refresh (called from SettingsController deps) ----

        public void RefreshCustomAvatarGallery(CompanionApp app)
        {
            if (_galleryContainer == null || app == null)
                return;

            foreach (var tile in _customAvatarTiles.Values)
                tile.RemoveFromHierarchy();

            _customAvatarTiles.Clear();
            UpdateAvatarProfileCaches(app.Avatars.GetAll());

            foreach (var profile in _cachedCustomProfiles)
            {
                var tile = CreateCustomAvatarTile(profile);
                VisualElement targetGallery = GalleryForProfile(profile);
                if (targetGallery == _galleryStatic && _avatarUploadTile != null)
                {
                    int uploadIndex = targetGallery.IndexOf(_avatarUploadTile);
                    if (uploadIndex >= 0)
                        targetGallery.Insert(uploadIndex, tile);
                    else
                        targetGallery.Add(tile);
                }
                else
                {
                    targetGallery?.Add(tile);
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
            {
                AvatarProfile profile = GetStoredProfile(kvp.Key);
                bool isStatic = profile == null ||
                    profile.avatarType == AvatarProfileTypes.Static2D;
                SetDisplay(kvp.Value,
                    !isStatic || MatchesFilter(kvp.Key) ? DisplayStyle.Flex : DisplayStyle.None);
            }

            if (_avatarUploadTile != null)
                SetDisplay(_avatarUploadTile, _activeAvatarFilter == AvatarFilter.All || _activeAvatarFilter == AvatarFilter.Custom
                    ? DisplayStyle.Flex
                    : DisplayStyle.None);

            if (_avatarViewMode == AvatarViewMode.Static && !MatchesFilter(_activeAvatarId))
            {
                var fallback = GetFirstAvatarForActiveFilter();
                if (!string.IsNullOrEmpty(fallback))
                    SelectAvatar(fallback);
            }
        }

        public void ApplyAvatarArt(string avatarId)
        {
            _catalogOnlyPreviewId = null;
            _avatarPersonaFoldout?.SetEnabled(true);
            _avatarCustomizationFoldout?.SetEnabled(true);
            bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, avatarId) >= 0;
            var profile = GetStoredProfile(avatarId);
            NeonLogger.Log("[AvatarArt] ApplyAvatarArt id='" + avatarId +
                "' isBuiltIn=" + isBuiltIn +
                " storedProfile=" + (profile != null ? "found (clips=" + (profile.animationClips?.Count ?? 0) + ")" : "null") +
                " _avatarArt=" + (_avatarArt != null ? "ok" : "NULL"));
            if (profile == null && isBuiltIn)
                profile = new AvatarProfile { id = avatarId, isBuiltIn = true };
            bool hasUnsupportedType = profile != null && !isBuiltIn &&
                (!AvatarProfile.IsKnownType(profile.avatarType) ||
                 profile.contractVersion > AvatarProfile.CurrentContractVersion);
            bool is3D = profile != null &&
                (profile.is3D ||
                 profile.avatarType == AvatarProfileTypes.Generic3D ||
                 profile.avatarType == AvatarProfileTypes.Vrm) &&
                !string.IsNullOrWhiteSpace(profile.modelPath);
            bool hasAnimation = !is3D && !hasUnsupportedType && ConfigureAvatarAnimation(profile);
            NeonLogger.Log("[AvatarArt] hasAnimation=" + hasAnimation + " is3D=" + is3D);

            if (is3D)
                _ = ConfigureAvatar3DAsync(profile);
            else
                Disable3DAvatarRender();
            if (hasUnsupportedType)
                Disable2DAvatarAnimation();

            if (_avatarArt != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _avatarArt.EnableInClassList($"avatar__art--{id}", isBuiltIn && id == avatarId && !hasAnimation);

                if (!isBuiltIn && !hasAnimation && !is3D && !hasUnsupportedType)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _avatarArt.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                    ApplyCustomAvatarTransform(_avatarArt, GetCustomProfile(avatarId));
                }
                else
                {
                    _avatarArt.style.backgroundImage = StyleKeyword.Null;
                    ResetCustomAvatarTransform(_avatarArt);
                }
            }

            SetDisplay(_avatarShade,  hasAnimation ? DisplayStyle.None : DisplayStyle.Flex);
            SetDisplay(_avatarLetter, hasAnimation ? DisplayStyle.None : DisplayStyle.Flex);

            ApplyAvatarLayout(hasAnimation);

            if (_previewHero != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _previewHero.EnableInClassList($"preview-hero--{id}", isBuiltIn && id == avatarId);

                if (!isBuiltIn && !is3D && !hasUnsupportedType)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _previewHero.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                    _previewHero.style.backgroundColor = new StyleColor(new Color(0.18f, 0.18f, 0.22f));
                    ApplyCustomAvatarTransform(_previewHero, GetCustomProfile(avatarId));
                }
                else
                {
                    _previewHero.style.backgroundImage = StyleKeyword.Null;
                    _previewHero.style.backgroundColor = StyleKeyword.Null;
                    ResetCustomAvatarTransform(_previewHero);
                }
            }

            string name = AvatarDisplayName(avatarId);
            if (_previewTitle != null)
                _previewTitle.text = name;
            if (_previewTag != null)
                _previewTag.text = AvatarStyleTag(avatarId);
            if (_previewPersona != null)
                _previewPersona.text = AvatarPersonaText(avatarId);

            UpdatePersonaStateUi(avatarId);
            UpdateAvatarActionButtons(avatarId);
            UpdateCapabilityCard(profile, isBuiltIn);

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
            _avtileYorha2bAnimated?.EnableInClassList("avtile--selected",
                _avatarViewMode == AvatarViewMode.Animated && avatarId == "yorha-2b");
        }

        // ---- Motion state (called from ChatController and voice events) ----

        public void SetAvatarMotionState(AvatarMotionState state)
        {
            _avatarMotionState = state;
            _d.AvatarMotionStateChanged?.Invoke(state);

            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                string stateName = StateToClipName(state);
                string clip3D = _active3DClipMapping != null
                    ? _active3DClipMapping.GetClip(stateName)
                    : stateName;
                if (!_avatar3DService.SetAnimation(clip3D))
                {
                    string idleClip = _active3DClipMapping != null
                        ? _active3DClipMapping.idle
                        : "idle";
                    if (!string.IsNullOrWhiteSpace(idleClip))
                        _avatar3DService.SetAnimation(idleClip);
                }
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

        public string CaptureBuiltInPreview(string avatarId)
        {
            if (_d.Root == null || string.IsNullOrWhiteSpace(avatarId))
                return null;

            VisualElement tile = _d.Root.Q<VisualElement>("avtile-" + avatarId);
            Texture2D source = tile != null ? tile.resolvedStyle.backgroundImage.texture : null;
            if (source == null)
                return null;

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32);
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                readable.Apply();
                return Convert.ToBase64String(readable.EncodeToPNG());
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("[CompanionWindow] Preview snapshot failed: " + ex.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (readable != null)
                    UnityEngine.Object.Destroy(readable);
            }
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

        public void RefreshPreviewPersonaText(string avatarId)
        {
            if (_previewPersona != null)
                _previewPersona.text = AvatarPersonaText(avatarId);
        }

        public void UpdateAvatarActionButtons(string avatarId)
        {
            bool isCustom = GetCustomProfile(avatarId) != null;
            bool hasOverride = HasPersonaOverride(avatarId);
            SetDisplay(_previewResetPersonaBtn, hasOverride ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_previewDeleteAvatarBtn, isCustom ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void UpdateCapabilityCard(AvatarProfile profile, bool isBuiltIn)
        {
            if (_previewApplyBtn != null)
                _previewApplyBtn.SetEnabled(profile == null || profile.isBuiltIn ||
                    (AvatarProfile.IsKnownType(profile.avatarType) &&
                     profile.contractVersion <= AvatarProfile.CurrentContractVersion &&
                     profile.capabilities != null &&
                     profile.capabilities.canRender &&
                     profile.capabilities.isRuntimeSupported));

            AvatarCapabilities capabilities = profile != null ? profile.capabilities : null;
            string avatarType = profile != null ? profile.avatarType : AvatarProfileTypes.Static2D;
            bool canRender = isBuiltIn || (capabilities != null && capabilities.canRender);
            bool canAnimate = capabilities != null && capabilities.canAnimate;
            bool hasLipsync = capabilities != null && capabilities.hasLipsync;
            string evidenceCode = FirstEvidence(capabilities);

            if (isBuiltIn && profile != null &&
                (profile.id == "neon" || profile.id == "yorha-2b"))
            {
                canAnimate = true;
                evidenceCode = "built_in_motion_pack";
            }
            else if (isBuiltIn)
            {
                evidenceCode = "built_in_profile";
            }

            if (_avatarCapabilityType != null)
                _avatarCapabilityType.text = LocalizationExtensions.GetFormat(
                    "avatar.capability.type",
                    "Type: {0}",
                    AvatarTypeDisplayName(avatarType));
            bool factsVerified = isBuiltIn ||
                (capabilities != null && capabilities.isVerified);
            if (factsVerified)
            {
                SetCapabilityFact(_avatarCapabilityRender, "avatar.capability.render",
                    "Rendering: {0}", canRender);
                SetCapabilityFact(_avatarCapabilityAnimation, "avatar.capability.animation",
                    "Animation: {0}", canAnimate);
                SetCapabilityFact(_avatarCapabilityHumanoid, "avatar.capability.humanoid",
                    "Humanoid: {0}", capabilities != null && capabilities.hasHumanoid);
                SetCapabilityFact(_avatarCapabilityBlink, "avatar.capability.blink",
                    "Blink: {0}", capabilities != null && capabilities.hasBlink);
                SetCapabilityFact(_avatarCapabilityGaze, "avatar.capability.gaze",
                    "Gaze: {0}", capabilities != null && capabilities.hasGaze);
                SetCapabilityFact(_avatarCapabilityExpressions, "avatar.capability.expressions",
                    "Expressions: {0}", capabilities != null && capabilities.hasExpressions);
                SetCapabilityFact(_avatarCapabilityLipsync, "avatar.capability.lipsync",
                    "Lipsync: {0}", hasLipsync);
            }
            else
            {
                SetCapabilityUnknown(_avatarCapabilityRender, "avatar.capability.render", "Rendering: {0}");
                SetCapabilityUnknown(_avatarCapabilityAnimation, "avatar.capability.animation", "Animation: {0}");
                SetCapabilityUnknown(_avatarCapabilityHumanoid, "avatar.capability.humanoid", "Humanoid: {0}");
                SetCapabilityUnknown(_avatarCapabilityBlink, "avatar.capability.blink", "Blink: {0}");
                SetCapabilityUnknown(_avatarCapabilityGaze, "avatar.capability.gaze", "Gaze: {0}");
                SetCapabilityUnknown(_avatarCapabilityExpressions, "avatar.capability.expressions", "Expressions: {0}");
                SetCapabilityUnknown(_avatarCapabilityLipsync, "avatar.capability.lipsync", "Lipsync: {0}");
            }
            if (_avatarCapabilityEvidence != null)
            {
                string evidence = EvidenceDisplayName(evidenceCode);
                if (capabilities != null && avatarType == AvatarProfileTypes.Vrm)
                {
                    evidence += " · " + LocalizationExtensions.GetFormat(
                        "avatar.capability.scene.vrm.compact",
                        "{0} nodes, {1} renderers, {2} triangles",
                        capabilities.sceneNodeCount,
                        capabilities.rendererCount,
                        capabilities.triangleCount);
                }
                else if (capabilities != null &&
                         avatarType == AvatarProfileTypes.Generic3D)
                {
                    evidence += " · " + LocalizationExtensions.GetFormat(
                        "avatar.capability.scene.compact",
                        "{0} nodes, {1} renderers, {2} triangles",
                        capabilities.sceneNodeCount,
                        capabilities.rendererCount,
                        capabilities.triangleCount);
                }
                _avatarCapabilityEvidence.text = LocalizationExtensions.GetFormat(
                    "avatar.capability.evidence",
                    "Evidence: {0}",
                    evidence);
            }

            string diagnostic = string.Empty;
            if (profile != null &&
                profile.contractVersion > AvatarProfile.CurrentContractVersion)
            {
                diagnostic = LocalizationExtensions.Get(
                    "avatar.version.unsupported",
                    "This avatar profile uses a newer unsupported contract version. The active avatar was preserved.");
            }
            else if (profile != null && !AvatarProfile.IsKnownType(profile.avatarType))
            {
                diagnostic = LocalizationExtensions.Get(
                    "avatar.type.unsupported",
                    "This avatar profile uses a newer unsupported backend. The active avatar was preserved.");
            }
            else if (profile != null && profile.diagnostic == "vrm_restricted_features")
            {
                diagnostic = LocalizationExtensions.Get(
                    "avatar.vrm.restricted",
                    "This VRM can render, but only the verified features above are enabled.");
            }
            else if (profile != null && !isBuiltIn)
            {
                string sourcePath = !string.IsNullOrWhiteSpace(profile.modelPath)
                    ? profile.modelPath
                    : (!string.IsNullOrWhiteSpace(profile.motionPackManifestPath)
                        ? profile.motionPackManifestPath
                        : profile.imagePath);
                if (!string.IsNullOrWhiteSpace(sourcePath) && !File.Exists(sourcePath))
                {
                    diagnostic = LocalizationExtensions.Get(
                        "avatar.capability.error.source_missing",
                        "The local source file is missing.");
                }
            }

            SetCapabilityDiagnostic(diagnostic);
        }

        private void ShowCatalogOnlyProfile(AvatarProfile profile)
        {
            if (profile == null)
                return;
            _catalogOnlyPreviewId = profile.id;
            _avatarPersonaFoldout?.SetEnabled(false);
            _avatarCustomizationFoldout?.SetEnabled(false);
            if (_previewTitle != null)
                _previewTitle.text = string.IsNullOrWhiteSpace(profile.name) ? profile.id : profile.name;
            if (_previewTag != null)
                _previewTag.text = AvatarTypeDisplayName(profile.avatarType);
            UpdateCapabilityCard(profile, false);
            SetDisplay(_previewDeleteAvatarBtn, DisplayStyle.Flex);
        }

        private void SetCapabilityDiagnostic(string text)
        {
            if (_avatarCapabilityDiagnostic == null)
                return;
            _avatarCapabilityDiagnostic.text = text ?? string.Empty;
            SetDisplay(_avatarCapabilityDiagnostic,
                string.IsNullOrWhiteSpace(text) ? DisplayStyle.None : DisplayStyle.Flex);
        }

        private static void SetCapabilityFact(
            Label label,
            string key,
            string fallback,
            bool available)
        {
            if (label == null)
                return;
            string value = available
                ? LocalizationExtensions.Get("avatar.capability.available", "Available")
                : LocalizationExtensions.Get("avatar.capability.unavailable", "Unavailable");
            label.text = LocalizationExtensions.GetFormat(key, fallback, value);
        }

        private static void SetCapabilityUnknown(Label label, string key, string fallback)
        {
            if (label != null)
                label.text = LocalizationExtensions.GetFormat(key, fallback, "—");
        }

        private static string FirstEvidence(AvatarCapabilities capabilities)
        {
            return capabilities != null && capabilities.evidence != null &&
                   capabilities.evidence.Count > 0
                ? capabilities.evidence[0]
                : "legacy_profile_fields";
        }

        private static string EvidenceDisplayName(string evidenceCode)
        {
            return LocalizationExtensions.Get(
                "avatar.capability.evidence." + (evidenceCode ?? "legacy_profile_fields"),
                evidenceCode ?? "legacy_profile_fields");
        }

        private static string AvatarTypeDisplayName(string avatarType)
        {
            if (avatarType == AvatarProfileTypes.SpriteSheet)
                return LocalizationExtensions.Get("avatar.type.sprite", "Sprite-sheet");
            if (avatarType == AvatarProfileTypes.Generic3D)
                return LocalizationExtensions.Get("avatar.type.generic3d", "Generic 3D");
            if (avatarType == AvatarProfileTypes.Vrm)
                return LocalizationExtensions.Get("avatar.type.vrm", "VRM");
            if (!string.IsNullOrWhiteSpace(avatarType) &&
                avatarType != AvatarProfileTypes.Static2D)
                return LocalizationExtensions.GetFormat(
                    "avatar.type.unknown",
                    "Unsupported ({0})",
                    avatarType);
            return LocalizationExtensions.Get("avatar.type.static2d", "Static 2D");
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
            SelectAnimatedAvatar("neon");
        }

        private void OnYorha2bAnimatedTileClicked(ClickEvent _)
        {
            SelectAnimatedAvatar("yorha-2b");
        }

        private void SelectAnimatedAvatar(string avatarId)
        {
            if (_activeAvatarId == avatarId)
            {
                SyncGallerySelection(avatarId);
                ApplyAvatarArt(avatarId);
                _d.SaveSettings?.Invoke();
            }
            else
            {
                SelectAvatar(avatarId);
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
            _d.SaveSettings?.Invoke();
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
            _avtileYorha2bAnimated?.EnableInClassList("avtile--selected", isAnimated && _activeAvatarId == "yorha-2b");
        }

        private static AvatarViewMode ParseAvatarViewMode(string value)
        {
            if (string.Equals(value, "animated", StringComparison.OrdinalIgnoreCase))
                return AvatarViewMode.Animated;
            if (string.Equals(value, "3d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "volume3d", StringComparison.OrdinalIgnoreCase))
                return AvatarViewMode.Volume3D;
            return AvatarViewMode.Static;
        }

        private static string AvatarViewModeToSetting(AvatarViewMode mode)
        {
            if (mode == AvatarViewMode.Animated)
                return "animated";
            if (mode == AvatarViewMode.Volume3D)
                return "3d";
            return "static";
        }

        private void SelectAvatar(string avatarId)
        {
            AvatarProfile selectedProfile = GetStoredProfile(avatarId);
            if (selectedProfile != null &&
                selectedProfile.contractVersion > AvatarProfile.CurrentContractVersion)
            {
                ShowCatalogOnlyProfile(selectedProfile);
                _d.AddSystemMessage?.Invoke(LocalizationExtensions.Get(
                    "avatar.version.unsupported",
                    "This avatar profile uses a newer unsupported contract version. The active avatar was preserved."));
                return;
            }
            if (selectedProfile != null && !AvatarProfile.IsKnownType(selectedProfile.avatarType))
            {
                ShowCatalogOnlyProfile(selectedProfile);
                _d.AddSystemMessage?.Invoke(LocalizationExtensions.Get(
                    "avatar.type.unsupported",
                    "This avatar profile uses a newer unsupported backend. The active avatar was preserved."));
                return;
            }
            if (selectedProfile != null && selectedProfile.avatarType == AvatarProfileTypes.Vrm)
            {
                int requestVersion = ++_avatarSelectionRequestVersion;
                _ = SelectVrmAvatarAsync(avatarId, selectedProfile, requestVersion);
                return;
            }

            _avatarSelectionRequestVersion++;
            CommitAvatarSelection(avatarId);
        }

        private async Task SelectVrmAvatarAsync(
            string avatarId,
            AvatarProfile profile,
            int requestVersion)
        {
            Avatar3DLoadResult validation = await Avatar3DLoader.LoadAsync(profile.modelPath);
            if (requestVersion != _avatarSelectionRequestVersion)
            {
                if (validation.Instance != null)
                    UnityEngine.Object.Destroy(validation.Instance);
                return;
            }

            if (!validation.Success || validation.Instance == null)
            {
                ShowCatalogOnlyProfile(profile);
                _d.AddSystemMessage?.Invoke(LocalizationExtensions.Get(
                    "avatar.vrm.invalid.preserved",
                    "The VRM could not be loaded. The current avatar was preserved."));
                return;
            }

            UnityEngine.Object.Destroy(validation.Instance);
            profile.capabilities = validation.Capabilities;
            profile.modelAnimationClips = new List<string>(validation.AnimationNames);
            profile.diagnostic = validation.Capabilities.isRestricted
                ? "vrm_restricted_features"
                : string.Empty;
            CommitAvatarSelection(avatarId);
        }

        private void CommitAvatarSelection(string avatarId)
        {
            if (_activeAvatarId == avatarId) return;
            ClosePersonaEditor();
            CancelCustomizationEdits();
            _activeAvatarId = avatarId;
            SyncGallerySelection(avatarId);
            ApplyAvatarArt(avatarId);
            string name = AvatarDisplayName(avatarId);
            string chatSub = _d.GetChatSubtitle != null ? _d.GetChatSubtitle() : string.Empty;
            string updatedSub = chatSub.Contains("·")
                ? chatSub.Substring(0, chatSub.LastIndexOf('·') + 2) + name
                : chatSub;
            _d.SetChatSubtitle?.Invoke(updatedSub);
            _d.SetTopbarSubtitle?.Invoke(updatedSub);
            _d.SaveSettings?.Invoke();
            _d.AvatarChanged?.Invoke(avatarId);
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
                string deleteId = !string.IsNullOrWhiteSpace(_catalogOnlyPreviewId)
                    ? _catalogOnlyPreviewId
                    : _activeAvatarId;
                if (Array.IndexOf(BuiltInAvatarIds, deleteId) >= 0)
                    return;

                var app = await _d.GetAppAsync();
                if (app == null) return;

                var all = app.Avatars.GetAll();
                var profile = all.Find(a => a != null && a.id == deleteId);
                if (profile == null) return;

                string imagePath = profile.imagePath;
                string modelPath = profile.modelPath;
                string motionPath = profile.motionPackManifestPath;
                all.RemoveAll(a => a != null && a.id == deleteId);
                app.Avatars.SaveAll(all);

                ReleaseCustomTexture(imagePath);
                DeleteCustomAvatarFileIfUnused(imagePath, all);
                DeleteCustomAvatarFileIfUnused(modelPath, all);
                DeleteCustomAvatarFileIfUnused(motionPath, all);
                AvatarAssetImporter.DeleteImportedProfileAssets(profile);

                UpdateAvatarProfileCaches(all);
                if (!string.IsNullOrWhiteSpace(_catalogOnlyPreviewId))
                {
                    _catalogOnlyPreviewId = null;
                    RefreshCustomAvatarGallery(app);
                    ApplyAvatarArt(_activeAvatarId);
                    SyncGallerySelection(_activeAvatarId);
                    return;
                }
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

        private void HideAllAvatarImageOverlays()
        {
            if (_avatarArt == null) return;
            var imgs = _avatarArt.Query<Image>().ToList();
            for (int k = 0; k < imgs.Count; k++)
                SetDisplay(imgs[k], DisplayStyle.None);
        }

        private bool ConfigureAvatarAnimation(AvatarProfile profile)
        {
            EnsureAvatarAnimationImage();
            EnsureAvatar3DImage();

            // Check mode BEFORE null guard so we always clean up image overlays from
            // other controllers (e.g. MainViewController also adds an Image to _avatarArt).
            if (_avatarViewMode != AvatarViewMode.Animated)
            {
                if (_avatarAnimator != null) _avatarAnimator.Stop();
                if (_avatarArtImage != null) _avatarArtImage.sprite = null;
                HideAllAvatarImageOverlays();
                return false;
            }

            if (_avatarAnimator == null || _avatarArtImage == null)
            {
                NeonLogger.LogWarning("[AvatarAnim] EnsureAvatarAnimationImage failed: " +
                    "_avatarAnimator=" + (_avatarAnimator == null ? "null" : "ok") +
                    " _avatarArtImage=" + (_avatarArtImage == null ? "null" : "ok") +
                    " _avatarArt=" + (_avatarArt == null ? "null" : "ok"));
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
            {
                SetCapabilityDiagnostic(LocalizationExtensions.Get(
                    "avatar.capability.error.3d_unavailable",
                    "3D renderer is unavailable in this build."));
                return;
            }

            if (profile == null || string.IsNullOrWhiteSpace(profile.modelPath))
            {
                Disable3DAvatarRender();
                SetCapabilityDiagnostic(LocalizationExtensions.Get(
                    "avatar.capability.error.source_missing",
                    "The local source file is missing."));
                return;
            }

            bool loaded = await _avatar3DService.LoadAvatar(profile.modelPath);
            if (!loaded)
            {
                Disable3DAvatarRender();
                SetCapabilityDiagnostic(LocalizationExtensions.Get(
                    "avatar.capability.error.load_failed",
                    "The saved 3D model could not be loaded. The active avatar was not replaced."));
                return;
            }

            var runtimeRoot = _avatar3DService.GetRuntimeRoot();
            if (runtimeRoot == null)
            {
                Disable3DAvatarRender();
                SetCapabilityDiagnostic(LocalizationExtensions.Get(
                    "avatar.capability.error.load_failed",
                    "The saved 3D model could not be loaded. The active avatar was not replaced."));
                return;
            }

            _active3DClipMapping = profile.stateClipMapping;
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
            _active3DClipMapping = null;
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
                if (!_avatar3DService.SetAnimation(reactionClipName))
                    _avatar3DService.SetExpression(reactionClipName, 1f);
                return;
            }

            if (_avatarAnimator == null || !_avatarAnimator.HasAnyClips)
                return;

            if (!_avatarAnimator.HasClip(reactionClipName))
                return;

            _avatarAnimator.PlayOneShot(reactionClipName, RefreshAvatarMotionState, true);
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
                ApplyCustomAvatarTransform(tile, profile);
            }
            else
            {
                tile.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.30f));
            }

            var typeBadge = new VisualElement();
            typeBadge.AddToClassList("avtile__anim-badge");
            typeBadge.Add(new Label(AvatarTypeDisplayName(profile.avatarType)));
            tile.Add(typeBadge);

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

        private VisualElement GalleryForProfile(AvatarProfile profile)
        {
            if (profile != null && profile.avatarType == AvatarProfileTypes.SpriteSheet)
                return _galleryAnimated ?? _galleryStatic;
            if (profile != null &&
                (profile.avatarType == AvatarProfileTypes.Generic3D ||
                 profile.avatarType == AvatarProfileTypes.Vrm))
                return _gallery3D ?? _galleryStatic;
            return _galleryStatic;
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

        private void OnAvatarUploadClicked() { OpenAvatarImport(); }
        private void OnAvatarUploadTileClicked(ClickEvent _) { OpenAvatarImport(); }

        private static void LocalizeImportUi(VisualElement root)
        {
            if (root == null)
                return;

            root.Q<Label>("avatar-import-tile-label")?.Localize("avatar.import.tile");
            root.Q<Label>("avatars-page-title")?.Localize("avatars.page.title");
            root.Q<Label>("avatars-page-subtitle")?.Localize("avatars.page.subtitle");
            root.Q<Label>("avatar-gallery-3d-hint")?.Localize("avatar.gallery.3d.hint");
            root.Q<Label>("avatar-import-title")?.Localize("avatar.import.title");
            root.Q<Label>("avatar-import-subtitle")?.Localize("avatar.import.subtitle");
            root.Q<Button>("avatar-import-static-btn")?.Localize("avatar.type.static2d");
            root.Q<Button>("avatar-import-sprite-btn")?.Localize("avatar.type.sprite");
            root.Q<Button>("avatar-import-3d-btn")?.Localize("avatar.type.generic3d");
            root.Q<Button>("avatar-import-vrm-btn")?.Localize("avatar.type.vrm");
            root.Q<Button>("avatar-import-choose-btn")?.Localize("avatar.import.choose");
            root.Q<Label>("avatar-import-preview-placeholder")?.Localize("avatar.import.preview.empty");
            root.Q<Label>("avatar-import-capability-title")?.Localize("avatar.capability.verified");
            root.Q<Label>("avatar-import-mapping-title")?.Localize("avatar.import.mapping.title");
            root.Q<Label>("avatar-import-mapping-help")?.Localize("avatar.import.mapping.help");
            root.Q<Button>("avatar-import-cancel-btn")?.Localize("common.cancel");
            root.Q<Button>("avatar-import-save-btn")?.Localize("avatar.import.save");

            Foldout capabilities = root.Q<Foldout>("avatar-capabilities-foldout");
            if (capabilities != null)
                capabilities.text = LocalizationExtensions.Get(
                    "avatar.capability.title", "Capabilities");
        }

        private void OpenAvatarImport()
        {
            SetAvatarImportType(AvatarProfileTypes.Static2D);
            SetDisplay(_avatarImportOverlay, DisplayStyle.Flex);
            _avatarImportOverlay?.BringToFront();
        }

        private void CloseAvatarImport()
        {
            _avatarImportRequestVersion++;
            ReleaseImportPreviewTexture();
            ReleaseImportPreviewModel();
            _avatarImportInspection = null;
            SetDisplay(_avatarImportOverlay, DisplayStyle.None);
        }

        private void OnAvatarImportStaticClicked() { SetAvatarImportType(AvatarProfileTypes.Static2D); }
        private void OnAvatarImportSpriteClicked() { SetAvatarImportType(AvatarProfileTypes.SpriteSheet); }
        private void OnAvatarImport3DClicked() { SetAvatarImportType(AvatarProfileTypes.Generic3D); }
        private void OnAvatarImportVrmClicked() { SetAvatarImportType(AvatarProfileTypes.Vrm); }
        private void OnAvatarImportChooseClicked() { _ = ChooseAvatarImportFileAsync(); }
        private void OnAvatarImportSaveClicked() { _ = SaveAvatarImportAsync(); }

        private void SetAvatarImportType(string avatarType)
        {
            _avatarImportRequestVersion++;
            _avatarImportType = avatarType;
            ReleaseImportPreviewTexture();
            ReleaseImportPreviewModel();
            _avatarImportInspection = null;

            _avatarImportStaticBtn?.EnableInClassList(
                "avatar-import-type--active", avatarType == AvatarProfileTypes.Static2D);
            _avatarImportSpriteBtn?.EnableInClassList(
                "avatar-import-type--active", avatarType == AvatarProfileTypes.SpriteSheet);
            _avatarImport3DBtn?.EnableInClassList(
                "avatar-import-type--active", avatarType == AvatarProfileTypes.Generic3D);
            _avatarImportVrmBtn?.EnableInClassList(
                "avatar-import-type--active", avatarType == AvatarProfileTypes.Vrm);
            SetDisplay(_avatarImportMapping,
                avatarType == AvatarProfileTypes.Generic3D ||
                avatarType == AvatarProfileTypes.Vrm
                    ? DisplayStyle.Flex
                    : DisplayStyle.None);

            if (_avatarImportTypeHelp != null)
            {
                if (avatarType == AvatarProfileTypes.SpriteSheet)
                    _avatarImportTypeHelp.text = LocalizationExtensions.Get(
                        "avatar.import.help.sprite",
                        "Choose motion_pack.json with its local PNG sprite sheets.");
                else if (avatarType == AvatarProfileTypes.Generic3D)
                    _avatarImportTypeHelp.text = LocalizationExtensions.Get(
                        "avatar.import.help.3d",
                        "GLB or glTF, including local sidecars, up to 100 MB.");
                else if (avatarType == AvatarProfileTypes.Vrm)
                    _avatarImportTypeHelp.text = LocalizationExtensions.Get(
                        "avatar.import.help.vrm",
                        "VRM is imported with UniVRM; only verified model features are enabled.");
                else
                    _avatarImportTypeHelp.text = LocalizationExtensions.Get(
                        "avatar.import.help.static",
                        "PNG/JPG up to 20 MB and 8192 x 8192.");
            }

            ResetImportInspectionUi();
        }

        private async Task ChooseAvatarImportFileAsync()
        {
            try
            {
                string requestedType = _avatarImportType;
                int requestVersion = ++_avatarImportRequestVersion;
                CompanionApp app = await _d.GetAppAsync();
                if (app == null || requestVersion != _avatarImportRequestVersion ||
                    requestedType != _avatarImportType)
                    return;

                string extensions = "png,jpg,jpeg";
                if (requestedType == AvatarProfileTypes.SpriteSheet)
                    extensions = "json";
                else if (requestedType == AvatarProfileTypes.Generic3D)
                    extensions = "glb,gltf";
                else if (requestedType == AvatarProfileTypes.Vrm)
                    extensions = "vrm";

                IFilePickerService picker = app.Services.GetRequired<IFilePickerService>();
                string path = await picker.PickFileAsync(extensions);
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (requestVersion != _avatarImportRequestVersion ||
                    requestedType != _avatarImportType)
                    return;
                if (_avatarImportSource != null)
                    _avatarImportSource.text = path;
                if (_avatarImportDiagnostic != null)
                    _avatarImportDiagnostic.text = LocalizationExtensions.Get(
                        "avatar.import.validating", "Validating local asset...");
                _avatarImportSaveBtn?.SetEnabled(false);

                AvatarAssetInspection inspection =
                    await AvatarAssetImporter.InspectAsync(path, requestedType);
                if (requestVersion != _avatarImportRequestVersion ||
                    requestedType != _avatarImportType)
                {
                    if (inspection.previewInstance != null)
                        UnityEngine.Object.Destroy(inspection.previewInstance);
                    return;
                }
                ReleaseImportPreviewModel();
                _avatarImportInspection = inspection;
                UpdateImportInspectionUi(inspection);
            }
            catch (Exception ex)
            {
                _avatarImportInspection = null;
                _avatarImportSaveBtn?.SetEnabled(false);
                if (_avatarImportDiagnostic != null)
                    _avatarImportDiagnostic.text = LocalizationExtensions.Get(
                        "avatar.import.error.inspection_failed", "The asset could not be inspected.");
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void UpdateImportInspectionUi(AvatarAssetInspection inspection)
        {
            ReleaseImportPreviewTexture();
            if (inspection == null || !inspection.success)
            {
                _avatarImportSaveBtn?.SetEnabled(false);
                if (_avatarImportDiagnostic != null)
                    _avatarImportDiagnostic.text = ImportErrorText(inspection);
                return;
            }

            AvatarCapabilities capabilities = inspection.capabilities;
            SetCapabilityFact(_avatarImportCapabilityRender, "avatar.capability.render",
                "Rendering: {0}", capabilities.canRender);
            SetCapabilityFact(_avatarImportCapabilityAnimation, "avatar.capability.animation",
                "Animation: {0}", capabilities.canAnimate);
            SetCapabilityFact(_avatarImportCapabilityHumanoid, "avatar.capability.humanoid",
                "Humanoid: {0}", capabilities.hasHumanoid);
            SetCapabilityFact(_avatarImportCapabilityBlink, "avatar.capability.blink",
                "Blink: {0}", capabilities.hasBlink);
            SetCapabilityFact(_avatarImportCapabilityGaze, "avatar.capability.gaze",
                "Gaze: {0}", capabilities.hasGaze);
            SetCapabilityFact(_avatarImportCapabilityExpressions, "avatar.capability.expressions",
                "Expressions: {0}", capabilities.hasExpressions);
            SetCapabilityFact(_avatarImportCapabilityLipsync, "avatar.capability.lipsync",
                "Lipsync: {0}", capabilities.hasLipsync);
            if (_avatarImportCapabilityScene != null)
            {
                if (inspection.avatarType == AvatarProfileTypes.Vrm)
                {
                    _avatarImportCapabilityScene.text = LocalizationExtensions.GetFormat(
                        "avatar.capability.scene.vrm",
                        "VRM scene: {0} nodes, {1} renderers, {2} triangles",
                        capabilities.sceneNodeCount,
                        capabilities.rendererCount,
                        capabilities.triangleCount);
                }
                else if (inspection.avatarType == AvatarProfileTypes.Generic3D)
                {
                    _avatarImportCapabilityScene.text = LocalizationExtensions.GetFormat(
                        "avatar.capability.scene",
                        "Scene: {0} nodes, {1} renderers, {2} triangles",
                        capabilities.sceneNodeCount,
                        capabilities.rendererCount,
                        capabilities.triangleCount);
                }
                else
                {
                    _avatarImportCapabilityScene.text = LocalizationExtensions.GetFormat(
                        "avatar.capability.image",
                        "Image: {0} x {1}",
                        inspection.imageWidth,
                        inspection.imageHeight);
                }
            }

            if (_avatarImportDiagnostic != null)
            {
                _avatarImportDiagnostic.text = inspection.avatarType == AvatarProfileTypes.Vrm &&
                                               capabilities.isRestricted
                    ? LocalizationExtensions.Get(
                        "avatar.import.valid.vrm.restricted",
                        "Valid VRM. Missing optional features stay disabled.")
                    : LocalizationExtensions.Get(
                        "avatar.import.valid",
                        "Validation passed. Saving creates a private local copy.");
            }

            if (!string.IsNullOrWhiteSpace(inspection.previewImagePath))
            {
                _avatarImportPreviewTexture = LoadTextureFromFile(inspection.previewImagePath);
                if (_avatarImportPreviewImage != null)
                    _avatarImportPreviewImage.image = _avatarImportPreviewTexture;
                SetDisplay(_avatarImportPreviewImage,
                    _avatarImportPreviewTexture != null ? DisplayStyle.Flex : DisplayStyle.None);
                SetDisplay(_avatarImportPreviewPlaceholder,
                    _avatarImportPreviewTexture != null ? DisplayStyle.None : DisplayStyle.Flex);
            }
            else
            {
                if ((inspection.avatarType == AvatarProfileTypes.Generic3D ||
                     inspection.avatarType == AvatarProfileTypes.Vrm) &&
                    inspection.previewInstance != null)
                {
                    ShowImport3DPreview(inspection);
                }
                else
                {
                    SetDisplay(_avatarImportPreviewImage, DisplayStyle.None);
                    SetDisplay(_avatarImportPreviewPlaceholder, DisplayStyle.Flex);
                    if (_avatarImportPreviewPlaceholder != null)
                    {
                        _avatarImportPreviewPlaceholder.text =
                            LocalizationExtensions.GetFormat(
                                "avatar.import.preview.3d",
                                "3D scene validated\n{0} nodes · {1} triangles",
                                capabilities.sceneNodeCount,
                                capabilities.triangleCount);
                    }
                }
            }

            PrefillClipMapping(inspection.animationClips);
            _avatarImportSaveBtn?.SetEnabled(true);
        }

        private async Task SaveAvatarImportAsync()
        {
            AvatarAssetInspection inspection = _avatarImportInspection;
            if (inspection == null || !inspection.success)
                return;

            Avatar3DStateClipMapping mapping;
            string mappingError;
            if (!TryBuildClipMapping(inspection, out mapping, out mappingError))
            {
                if (_avatarImportDiagnostic != null)
                    _avatarImportDiagnostic.text = mappingError;
                return;
            }

            _avatarImportSaveBtn?.SetEnabled(false);
            AvatarAssetImportResult imported = AvatarAssetImporter.Import(inspection, mapping);
            if (!imported.success || imported.profile == null)
            {
                if (_avatarImportDiagnostic != null)
                    _avatarImportDiagnostic.text = LocalizationExtensions.Get(
                        "avatar.import.error." + (imported.errorCode ?? "copy_failed"),
                        LocalizationExtensions.Get(
                            "avatar.import.error.copy_failed",
                            "The validated asset could not be copied to local storage."));
                _avatarImportSaveBtn?.SetEnabled(true);
                return;
            }

            bool profilePersisted = false;
            try
            {
                if (imported.profile.avatarType == AvatarProfileTypes.Generic3D ||
                    imported.profile.avatarType == AvatarProfileTypes.Vrm)
                {
                    Avatar3DLoadResult copiedModel =
                        await Avatar3DLoader.LoadAsync(imported.profile.modelPath);
                    if (!copiedModel.Success || copiedModel.Instance == null)
                    {
                        throw new InvalidDataException(
                            copiedModel.Error ?? "Copied 3D model failed validation.");
                    }
                    UnityEngine.Object.Destroy(copiedModel.Instance);
                }

                if (imported.profile.avatarType == AvatarProfileTypes.Static2D)
                {
                    AvatarCropResult crop = await ShowCropEditorAsync(
                        imported.profile.imagePath, 1f, 0f, 0f);
                    if (!AvatarCropBaker.TryWriteBakedAvatar(
                        imported.profile.imagePath, crop, 512, out string bakeError))
                    {
                        throw new InvalidDataException("Avatar crop bake failed: " + bakeError);
                    }
                }

                CompanionApp app = await _d.GetAppAsync();
                if (app == null)
                    throw new InvalidOperationException("Companion app is unavailable.");

                List<AvatarProfile> all = app.Avatars.GetAll();
                all.Add(imported.profile);
                app.Avatars.SaveAll(all);
                profilePersisted = true;
                RefreshCustomAvatarGallery(app);

                string importedType = imported.profile.avatarType;
                CloseAvatarImport();
                {
                    if (importedType == AvatarProfileTypes.SpriteSheet)
                        SetAvatarViewMode(AvatarViewMode.Animated);
                    else if (importedType == AvatarProfileTypes.Generic3D ||
                             importedType == AvatarProfileTypes.Vrm)
                        SetAvatarViewMode(AvatarViewMode.Volume3D);
                    else
                        SetAvatarViewMode(AvatarViewMode.Static);
                    SelectAvatar(imported.profile.id);
                }

                _d.AddSystemMessage?.Invoke(LocalizationExtensions.GetFormat(
                    "avatar.import.success",
                    "Avatar \"{0}\" was saved to the local catalog.",
                    imported.profile.name));
            }
            catch (TaskCanceledException)
            {
                AvatarAssetImporter.DeleteImportDirectory(imported.assetDirectory);
                _avatarImportSaveBtn?.SetEnabled(true);
            }
            catch (Exception ex)
            {
                if (!profilePersisted)
                    AvatarAssetImporter.DeleteImportDirectory(imported.assetDirectory);
                if (_avatarImportDiagnostic != null)
                    _avatarImportDiagnostic.text = profilePersisted
                        ? LocalizationExtensions.Get(
                            "avatar.import.error.refresh_failed",
                            "The avatar was saved, but the catalog could not refresh. Reopen avatar settings.")
                        : LocalizationExtensions.Get(
                            "avatar.import.error.save_failed",
                            "The avatar was not saved. The previous active avatar is unchanged.");
                _avatarImportSaveBtn?.SetEnabled(true);
                NeonLogger.LogError(ex.ToString());
            }
        }

        private bool TryBuildClipMapping(
            AvatarAssetInspection inspection,
            out Avatar3DStateClipMapping mapping,
            out string error)
        {
            mapping = null;
            error = null;
            if (inspection.avatarType != AvatarProfileTypes.Generic3D &&
                inspection.avatarType != AvatarProfileTypes.Vrm)
                return true;

            mapping = new Avatar3DStateClipMapping
            {
                idle = FieldValue(_avatarImportClipIdle),
                thinking = FieldValue(_avatarImportClipThinking),
                talking = FieldValue(_avatarImportClipTalking),
                listening = FieldValue(_avatarImportClipListening),
                smile = FieldValue(_avatarImportClipSmile),
                confused = FieldValue(_avatarImportClipConfused)
            };

            string[] values =
            {
                mapping.idle, mapping.thinking, mapping.talking,
                mapping.listening, mapping.smile, mapping.confused
            };
            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                    continue;
                if (!inspection.animationClips.Any(
                    clip => string.Equals(clip, values[i], StringComparison.Ordinal)))
                {
                    error = LocalizationExtensions.GetFormat(
                        "avatar.import.error.clip_missing",
                        "Animation clip \"{0}\" was not found in the model.",
                        values[i]);
                    return false;
                }
            }

            inspection.capabilities.hasStateAnimations = values.Any(
                value => !string.IsNullOrWhiteSpace(value));
            return true;
        }

        private void PrefillClipMapping(List<string> clips)
        {
            SetFieldValue(_avatarImportClipIdle, FindClip(clips, "idle"));
            SetFieldValue(_avatarImportClipThinking, FindClip(clips, "thinking"));
            SetFieldValue(_avatarImportClipTalking, FindClip(clips, "talking"));
            SetFieldValue(_avatarImportClipListening, FindClip(clips, "listening"));
            SetFieldValue(_avatarImportClipSmile, FindClip(clips, "smile"));
            SetFieldValue(_avatarImportClipConfused, FindClip(clips, "confused"));
        }

        private static string FindClip(List<string> clips, string state)
        {
            if (clips == null)
                return string.Empty;
            return clips.FirstOrDefault(
                clip => string.Equals(clip, state, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        private static string FieldValue(TextField field)
        {
            return field != null ? (field.value ?? string.Empty).Trim() : string.Empty;
        }

        private static void SetFieldValue(TextField field, string value)
        {
            if (field != null)
                field.SetValueWithoutNotify(value ?? string.Empty);
        }

        private void ResetImportInspectionUi()
        {
            _avatarImportInspection = null;
            _avatarImportSaveBtn?.SetEnabled(false);
            if (_avatarImportSource != null)
                _avatarImportSource.text = LocalizationExtensions.Get(
                    "avatar.import.no_file", "No file selected");
            if (_avatarImportDiagnostic != null)
                _avatarImportDiagnostic.text = LocalizationExtensions.Get(
                    "avatar.import.choose_prompt", "Choose a file to validate.");
            SetCapabilityUnknown(_avatarImportCapabilityRender, "avatar.capability.render", "Rendering: {0}");
            SetCapabilityUnknown(_avatarImportCapabilityAnimation, "avatar.capability.animation", "Animation: {0}");
            SetCapabilityUnknown(_avatarImportCapabilityHumanoid, "avatar.capability.humanoid", "Humanoid: {0}");
            SetCapabilityUnknown(_avatarImportCapabilityBlink, "avatar.capability.blink", "Blink: {0}");
            SetCapabilityUnknown(_avatarImportCapabilityGaze, "avatar.capability.gaze", "Gaze: {0}");
            SetCapabilityUnknown(_avatarImportCapabilityExpressions, "avatar.capability.expressions", "Expressions: {0}");
            SetCapabilityUnknown(_avatarImportCapabilityLipsync, "avatar.capability.lipsync", "Lipsync: {0}");
            if (_avatarImportCapabilityScene != null)
                _avatarImportCapabilityScene.text = LocalizationExtensions.Get(
                    "avatar.capability.scene.unknown", "Scene: —");
            SetDisplay(_avatarImportPreviewImage, DisplayStyle.None);
            SetDisplay(_avatarImportPreviewPlaceholder, DisplayStyle.Flex);
            if (_avatarImportPreviewPlaceholder != null)
                _avatarImportPreviewPlaceholder.text = LocalizationExtensions.Get(
                    "avatar.import.preview.empty", "Preview");
            PrefillClipMapping(null);
        }

        private void ReleaseImportPreviewTexture()
        {
            if (_avatarImportPreviewImage != null)
                _avatarImportPreviewImage.image = null;
            if (_avatarImportPreviewTexture != null)
                UnityEngine.Object.Destroy(_avatarImportPreviewTexture);
            _avatarImportPreviewTexture = null;
        }

        private void ShowImport3DPreview(AvatarAssetInspection inspection)
        {
            EnsureAvatar3DImage();
            if (_avatar3DRenderer == null || _avatarImportPreviewImage == null ||
                inspection == null || inspection.previewInstance == null)
                return;

            _avatarImportPreviewModel = inspection.previewInstance;
            inspection.previewInstance = null;
            Transform parent = _d.ModelParent;
            if (parent != null)
            {
                _avatarImportPreviewModel.transform.SetParent(parent, false);
                _avatarImportPreviewModel.transform.localPosition = Vector3.zero;
                _avatarImportPreviewModel.transform.localRotation = Quaternion.identity;
                _avatarImportPreviewModel.transform.localScale = Vector3.one;
            }

            _avatar3DRenderer.AttachTargetImage(_avatarImportPreviewImage);
            _avatar3DRenderer.SetModelRoot(_avatarImportPreviewModel.transform);
            SetDisplay(_avatarImportPreviewImage, DisplayStyle.Flex);
            SetDisplay(_avatarImportPreviewPlaceholder, DisplayStyle.None);
        }

        private void ReleaseImportPreviewModel()
        {
            if (_avatarImportInspection != null &&
                _avatarImportInspection.previewInstance != null)
            {
                UnityEngine.Object.Destroy(_avatarImportInspection.previewInstance);
                _avatarImportInspection.previewInstance = null;
            }
            if (_avatarImportPreviewModel != null)
                UnityEngine.Object.Destroy(_avatarImportPreviewModel);
            _avatarImportPreviewModel = null;

            if (_avatar3DRenderer != null)
            {
                _avatar3DRenderer.ClearModel();
                _avatar3DRenderer.AttachTargetImage(_avatar3DImage);
                if (_avatar3DService != null && _avatar3DService.IsLoaded)
                    _avatar3DRenderer.SetModelRoot(_avatar3DService.GetRuntimeTransform());
            }
        }

        private string ImportErrorText(AvatarAssetInspection inspection)
        {
            string code = inspection != null ? inspection.errorCode : "inspection_failed";
            return LocalizationExtensions.Get(
                "avatar.import.error." + code,
                LocalizationExtensions.Get(
                    "avatar.import.error.inspection_failed",
                    "The selected asset failed validation."));
        }

        private static void DeleteCustomAvatarFileIfUnused(string path, List<AvatarProfile> profiles)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            bool isStillReferenced = profiles.Any(a =>
                a != null &&
                !a.isBuiltIn &&
                (string.Equals(a.imagePath, path, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.modelPath, path, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.motionPackManifestPath, path, StringComparison.OrdinalIgnoreCase)));
            if (isStillReferenced || !System.IO.File.Exists(path))
                return;

            System.IO.File.Delete(path);
        }

        private Task<AvatarCropResult> ShowCropEditorAsync(
            string imagePath,
            float existingScale,
            float existingOffsetX,
            float existingOffsetY)
        {
            var tcs = new TaskCompletionSource<AvatarCropResult>();

            var bytes = System.IO.File.ReadAllBytes(imagePath);
            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(tex);
                tcs.TrySetResult(new AvatarCropResult { scale = 1f });
                return tcs.Task;
            }

            VisualElement host = _d.Root != null && _d.Root.panel != null
                ? _d.Root.panel.visualTree
                : _d.Root;

            var root = host != null ? host.Q<VisualElement>("avatar-crop-root") : null;
            if (root == null && host != null)
            {
                root = new VisualElement();
                root.name = "avatar-crop-root";
                root.style.position = Position.Absolute;
                root.style.left = 0;
                root.style.top = 0;
                root.style.right = 0;
                root.style.bottom = 0;
                root.pickingMode = PickingMode.Position;
                host.Add(root);
            }
            else if (root != null)
            {
                root.Clear();
            }

            if (root == null)
            {
                UnityEngine.Object.Destroy(tex);
                tcs.TrySetResult(new AvatarCropResult { scale = existingScale > 0f ? existingScale : 1f });
                return tcs.Task;
            }

            root.BringToFront();

            var editor = new AvatarCropEditor(root, tex, existingScale, existingOffsetX, existingOffsetY);

            editor.Confirmed += result =>
            {
                UnityEngine.Object.Destroy(tex);
                tcs.TrySetResult(result);
            };

            editor.Cancelled += () =>
            {
                UnityEngine.Object.Destroy(tex);
                tcs.TrySetCanceled();
            };

            editor.Show();
            return tcs.Task;
        }

        private void OnAvatarOpenFolderClicked()
        {
            string dir = AppPaths.AvatarAssetsDirectory;
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

        private static AvatarCustomizationData CloneCustomization(AvatarCustomizationData source)
        {
            if (source == null) return null;
            return new AvatarCustomizationData
            {
                OverlayEmoji = source.OverlayEmoji
            };
        }

        private static bool IsCustomizationEffectivelyDefault(AvatarCustomizationData data)
        {
            return data == null || string.IsNullOrEmpty(data.OverlayEmoji);
        }

        // ---- Crop / transform helpers ----

        private static void ApplyCustomAvatarTransform(VisualElement element, AvatarProfile profile)
        {
            if (element == null)
                return;

            float scale = profile != null && profile.avatarScale > 0f ? profile.avatarScale : 1f;
            float offX = profile != null ? profile.avatarOffsetX : 0f;
            float offY = profile != null ? profile.avatarOffsetY : 0f;
            bool hasLegacyTransform = Mathf.Abs(scale - 1f) > 0.001f
                || Mathf.Abs(offX) > 0.001f
                || Mathf.Abs(offY) > 0.001f;

            if (!hasLegacyTransform)
            {
                ResetCustomAvatarTransform(element);
                return;
            }

            element.style.backgroundSize = new BackgroundSize(
                new Length(scale * 100f, LengthUnit.Percent),
                new Length(scale * 100f, LengthUnit.Percent)
            );
            element.style.backgroundPositionX = new BackgroundPosition(
                BackgroundPositionKeyword.Center,
                new Length(offX, LengthUnit.Percent)
            );
            element.style.backgroundPositionY = new BackgroundPosition(
                BackgroundPositionKeyword.Center,
                new Length(offY, LengthUnit.Percent)
            );
        }

        private static void ResetCustomAvatarTransform(VisualElement element)
        {
            if (element == null)
                return;

            element.style.backgroundSize      = StyleKeyword.Null;
            element.style.backgroundPositionX = StyleKeyword.Null;
            element.style.backgroundPositionY = StyleKeyword.Null;
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
