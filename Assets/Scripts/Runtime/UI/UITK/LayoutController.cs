using System;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Единый адаптивный контроллер раскладки. По реальной логической ширине
    /// (PanelSettings = ConstantPhysicalSize → ширина в физических поинтах,
    /// аналог CSS px / Android dp) выбирает форм-фактор и вешает РОВНО один класс
    /// на app-root: ff-phone / ff-tablet / ff-desktop. Внутри desktop/tablet
    /// дополнительно работают app--compact / app--narrow.
    ///
    /// Телефон: рейл превращается в off-canvas drawer со скримом, аватар-панель —
    /// в полноэкранный оверлей. Никаких inline-костылей и z-index — порядок
    /// отрисовки решается BringToFront(), состояние — классами с transition.
    /// </summary>
    internal sealed class LayoutController
    {
        public struct Deps
        {
            /// <summary>rootVisualElement документа (родитель app-root).</summary>
            public VisualElement Root;
            /// <summary>Элемент с классом .app (несёт форм-фактор/платформенные классы и safe-area padding).</summary>
            public VisualElement AppRoot;
            public VisualElement RailElement;
            public VisualElement RailResizeHandle;
            public VisualElement AvatarPanel;
            public VisualElement ResizeHandle;
            public VisualElement ChatPanel;
            public VisualElement HistoryPanel;
            public VisualElement ProvidersPanel;
            public VisualElement AvatarsPanel;
            public VisualElement ThemesPanel;
            public VisualElement PlaceholderArea;
            public VisualElement SettingsPanel;
            public PanelResizeHandler PanelResizeHandler;

            /// <summary>
            /// Опционально. Если передан — будет применён Safe Area и платформенные классы.
            /// </summary>
            public IPlatformInfoService PlatformInfo;
        }

        private enum FormFactor
        {
            Unknown,
            Phone,
            Tablet,
            Desktop
        }

        // Брейкпоинты формы (логические поинты ConstantPhysicalSize ≈ физический размер).
        private const float PhoneMaxWidth = 520f;   // < — телефон (drawer + оверлеи)
        private const float TabletMaxWidth = 900f;  // < — планшет/компактный десктоп

        // Доп. брейкпоинты внутри desktop/tablet (декор топбара, авто-скрытие аватара).
        private const float CompactWidth = 1100f;
        private const float NarrowWidth = 900f;
        private const float AvatarHideWidth = 900f;

        private Deps _d;
        private Button _toggleLeftPanelBtn;
        private Button _toggleRightPanelBtn;
        private VisualElement _scrim;

        private FormFactor _formFactor = FormFactor.Unknown;
        private int _railSiblingIndex = -1;
        private IVisualElementScheduledItem _safeAreaPoll;
        private Rect _lastSafeArea = new Rect(-1f, -1f, -1f, -1f);

        private bool _leftPanelVisible = true;   // десктоп: рейл показан
        private bool _rightPanelVisible = true;  // десктоп: аватар-панель показана
        private bool _avatarAutoHidden;
        private bool _companionMode;
        private bool _drawerOpen;                 // телефон: drawer открыт
        private bool _avatarOverlayOpen;          // телефон: аватар-оверлей открыт

        public bool IsPhone => _formFactor == FormFactor.Phone;

        public void SetDeps(Deps deps)
        {
            _d = deps;
        }

        public void Init()
        {
            if (_d.AppRoot == null && _d.Root == null)
                return;

            _toggleLeftPanelBtn = (_d.Root ?? _d.AppRoot).Q<Button>("toggle-left-panel-btn");
            _toggleRightPanelBtn = (_d.Root ?? _d.AppRoot).Q<Button>("toggle-right-panel-btn");

            EnsureScrim();

            if (_d.RailElement != null && _d.AppRoot != null)
                _railSiblingIndex = _d.AppRoot.IndexOf(_d.RailElement);

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.Init(_d.ResizeHandle, _d.AvatarPanel, _d.RailResizeHandle, _d.RailElement);

            UpdatePanelToggleTooltips();
            ApplyPlatformLayout();

            // Первичный расчёт формы, если геометрия уже доступна.
            float w = GeometryHost != null ? GeometryHost.resolvedStyle.width : 0f;
            if (w > 0f)
                ApplyResponsive(w);
        }

        private VisualElement GeometryHost => _d.AppRoot ?? _d.Root;

        public void RegisterCallbacks()
        {
            RegisterClick(_toggleLeftPanelBtn, OnToggleLeftPanel);
            RegisterClick(_toggleRightPanelBtn, OnToggleRightPanel);

            if (GeometryHost != null)
                GeometryHost.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);

            // Screen.safeArea can lag a frame behind an orientation change, so the value
            // read in GeometryChangedEvent is sometimes stale (e.g. a landscape notch inset
            // sticking after rotating back to portrait). Poll and reconcile to be safe.
            if (_d.AppRoot != null)
                _safeAreaPoll = _d.AppRoot.schedule.Execute(ReconcileSafeArea).Every(400);

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.RegisterCallbacks();
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_toggleLeftPanelBtn, OnToggleLeftPanel);
            UnregisterClick(_toggleRightPanelBtn, OnToggleRightPanel);

            if (GeometryHost != null)
                GeometryHost.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);

            _safeAreaPoll?.Pause();
            _safeAreaPoll = null;

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.UnregisterCallbacks();
        }

        private void ReconcileSafeArea()
        {
            if (_d.PlatformInfo == null)
                return;
            Rect sa = _d.PlatformInfo.SafeArea;
            if (sa == _lastSafeArea)
                return;
            _lastSafeArea = sa;
            ApplySafeAreaPadding();
        }

        public void OnDisable()
        {
        }

        // ============================================================
        // Responsive core
        // ============================================================

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            ApplyResponsive(evt.newRect.width);
        }

        private void ApplyResponsive(float width)
        {
            if (width <= 0f)
                return;

            FormFactor next = ResolveFormFactor(width);
            if (next != _formFactor)
            {
                FormFactor prev = _formFactor;
                _formFactor = next;
                EnterFormFactor(prev, next);
            }

            // Десктопные/планшетные суб-брейкпоинты имеют смысл только когда есть
            // классическая многопанельная раскладка (не телефон).
            // Safe area changes on rotation; geometry fires on rotation, so recompute
            // here too — otherwise the top inset goes stale and the topbar drifts.
            ApplySafeAreaPadding();

            // app--compact / app--narrow track width on EVERY form factor (phone too):
            // the per-view USS already ships narrow reflow rules keyed on these classes,
            // and a phone is narrower than both thresholds — so reuse them instead of
            // duplicating. (Previously gated behind multiPane, which left phone unstyled.)
            bool multiPane = next != FormFactor.Phone;
            if (_d.AppRoot != null)
            {
                _d.AppRoot.EnableInClassList("app--compact", width < CompactWidth);
                _d.AppRoot.EnableInClassList("app--narrow", width < NarrowWidth);
            }

            if (multiPane)
            {
                UpdateAvatarAutoHide(width);
                if (_d.PanelResizeHandler != null)
                    _d.PanelResizeHandler.ClampToWindow(width);
            }
        }

        /// <summary>
        /// Форм-фактор НЕ полагается вслепую на логическую ширину: в редакторном
        /// Device Simulator Screen.dpi отдаёт dpi монитора, а не устройства, из-за
        /// чего ConstantPhysicalSize даёт «десктопную» ширину на телефоне. Поэтому
        /// на мобильной платформе решаем явно (телефон по умолчанию, планшет —
        /// только при заведомо устройственном dpi), а ширину окна используем как
        /// источник правды лишь на десктопе, где dpi корректен.
        /// </summary>
        private FormFactor ResolveFormFactor(float width)
        {
            if (IsMobilePlatform())
            {
                float dpi = Screen.dpi;
                // Доверяем dp-расчёту только в диапазоне реальных мобильных экранов
                // (исключаем типичные десктопные 96/120/144, прилетающие в симуляторе).
                if (dpi >= 200f && dpi <= 700f)
                {
                    float minSideDp = Mathf.Min(Screen.width, Screen.height) / (dpi / 160f);
                    if (minSideDp >= 600f)
                        return FormFactor.Tablet;
                }
                return FormFactor.Phone;
            }

            // Десктоп: ширина окна (ConstantPhysicalSize @96dpi = логические поинты) надёжна.
            if (width < PhoneMaxWidth)
                return FormFactor.Phone;
            if (width < TabletMaxWidth)
                return FormFactor.Tablet;
            return FormFactor.Desktop;
        }

        private static bool IsMobilePlatform()
        {
#if UNITY_ANDROID || UNITY_IOS
            // Сборка под мобильную платформу — считаем мобильным и в редакторе/симуляторе.
            return true;
#else
            return Application.isMobilePlatform;
#endif
        }

        private void EnterFormFactor(FormFactor prev, FormFactor next)
        {
            if (_d.AppRoot != null)
            {
                _d.AppRoot.EnableInClassList("ff-phone", next == FormFactor.Phone);
                _d.AppRoot.EnableInClassList("ff-tablet", next == FormFactor.Tablet);
                _d.AppRoot.EnableInClassList("ff-desktop", next == FormFactor.Desktop);
            }

            if (next == FormFactor.Phone)
                EnterPhone();
            else
                EnterMultiPane(prev);
        }

        /// <summary>
        /// Переход в телефонный режим: рейл и аватар-панель управляются классами
        /// (.ff-phone), поэтому снимаем inline-стили, которые мог выставить ресайз
        /// или ручное скрытие на десктопе.
        /// </summary>
        private void EnterPhone()
        {
            _drawerOpen = false;
            _avatarOverlayOpen = false;

            ClearLayoutInline(_d.RailElement);
            if (_d.RailElement != null)
                _d.RailElement.RemoveFromClassList("rail--open");

            ClearLayoutInline(_d.AvatarPanel);
            if (_d.AvatarPanel != null)
                _d.AvatarPanel.RemoveFromClassList("avatar--open");

            HideScrim();
        }

        /// <summary>
        /// Возврат к многопанельной раскладке (планшет/десктоп): восстанавливаем
        /// позицию рейла в потоке и видимость панелей по сохранённому состоянию.
        /// </summary>
        private void EnterMultiPane(FormFactor prev)
        {
            HideScrim();

            if (_d.RailElement != null)
            {
                _d.RailElement.RemoveFromClassList("rail--open");
                ClearLayoutInline(_d.RailElement);

                // Если рейл был вынесен наверх (BringToFront в drawer) — вернуть на место.
                if (prev == FormFactor.Phone && _railSiblingIndex >= 0 && _d.AppRoot != null
                    && _d.AppRoot.IndexOf(_d.RailElement) != _railSiblingIndex)
                {
                    int clamped = Mathf.Min(_railSiblingIndex, _d.AppRoot.childCount - 1);
                    _d.AppRoot.Insert(clamped, _d.RailElement);
                }

                SetDisplay(_d.RailElement, _leftPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            }

            SetDisplay(_d.RailResizeHandle, _leftPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);

            if (_d.AvatarPanel != null)
            {
                _d.AvatarPanel.RemoveFromClassList("avatar--open");
                ClearLayoutInline(_d.AvatarPanel);
            }
            _avatarOverlayOpen = false;

            // Видимость аватара восстановит UpdateAvatarAutoHide на текущей ширине.
            _avatarAutoHidden = false;
            SetDisplay(_d.AvatarPanel, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ResizeHandle, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void UpdateAvatarAutoHide(float width)
        {
            bool shouldAutoHide = width < AvatarHideWidth;

            if (shouldAutoHide && !_avatarAutoHidden)
            {
                _avatarAutoHidden = true;
                if (_rightPanelVisible && !_companionMode)
                {
                    SetDisplay(_d.AvatarPanel, DisplayStyle.None);
                    SetDisplay(_d.ResizeHandle, DisplayStyle.None);
                }
            }
            else if (!shouldAutoHide && _avatarAutoHidden)
            {
                _avatarAutoHidden = false;
                SetDisplay(_d.AvatarPanel, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
                SetDisplay(_d.ResizeHandle, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
            }
        }

        // ============================================================
        // Phone: drawer + avatar overlay
        // ============================================================

        public void ToggleDrawer()
        {
            if (_formFactor != FormFactor.Phone)
                return;
            if (_drawerOpen)
                CloseDrawer();
            else
                OpenDrawer();
        }

        private void OpenDrawer()
        {
            if (_d.RailElement == null)
                return;

            // Аватар-оверлей и drawer взаимоисключающи.
            if (_avatarOverlayOpen)
                CloseAvatarOverlay();

            _drawerOpen = true;

            ShowScrim();
            // Порядок отрисовки без z-index: скрим под рейлом, рейл — поверх всего.
            _scrim?.BringToFront();
            _d.RailElement.BringToFront();
            _d.RailElement.AddToClassList("rail--open");
        }

        private void CloseDrawer()
        {
            _drawerOpen = false;
            _d.RailElement?.RemoveFromClassList("rail--open");
            HideScrim();
        }

        public void ToggleAvatarOverlay()
        {
            if (_formFactor != FormFactor.Phone)
                return;
            if (_avatarOverlayOpen)
                CloseAvatarOverlay();
            else
                OpenAvatarOverlay();
        }

        private void OpenAvatarOverlay()
        {
            if (_d.AvatarPanel == null)
                return;

            if (_drawerOpen)
                CloseDrawer();

            _avatarOverlayOpen = true;
            _d.AvatarPanel.BringToFront();
            _d.AvatarPanel.AddToClassList("avatar--open");
        }

        private void CloseAvatarOverlay()
        {
            _avatarOverlayOpen = false;
            _d.AvatarPanel?.RemoveFromClassList("avatar--open");
        }

        private void EnsureScrim()
        {
            if (_scrim != null || _d.AppRoot == null)
                return;

            _scrim = new VisualElement();
            _scrim.name = "app-scrim";
            _scrim.AddToClassList("app-scrim");
            _scrim.style.display = DisplayStyle.None;
            _scrim.RegisterCallback<PointerDownEvent>(OnScrimPointerDown);
            _d.AppRoot.Add(_scrim);
        }

        private void OnScrimPointerDown(PointerDownEvent evt)
        {
            CloseDrawer();
            evt.StopPropagation();
        }

        private void ShowScrim()
        {
            if (_scrim != null)
                _scrim.style.display = DisplayStyle.Flex;
        }

        private void HideScrim()
        {
            if (_scrim != null)
                _scrim.style.display = DisplayStyle.None;
        }

        // ============================================================
        // Panel toggles (form-factor aware)
        // ============================================================

        public void OnToggleLeftPanel()
        {
            if (_formFactor == FormFactor.Phone)
            {
                ToggleDrawer();
                return;
            }

            _leftPanelVisible = !_leftPanelVisible;
            SetDisplay(_d.RailElement, _leftPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.RailResizeHandle, _leftPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            UpdatePanelToggleTooltips();
        }

        public void OnToggleRightPanel()
        {
            if (_formFactor == FormFactor.Phone)
            {
                ToggleAvatarOverlay();
                return;
            }

            _rightPanelVisible = !_rightPanelVisible;
            // Ручное переключение перехватывает контроль у авто-скрытия.
            _avatarAutoHidden = false;
            SetDisplay(_d.AvatarPanel, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ResizeHandle, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
            UpdatePanelToggleTooltips();
        }

        public void SetCompanionMode(bool enabled)
        {
            _companionMode = enabled;
            if (_formFactor == FormFactor.Phone)
                return;

            SetDisplay(_d.AvatarPanel, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ResizeHandle, ShouldShowAvatarColumn() ? DisplayStyle.Flex : DisplayStyle.None);
            UpdatePanelToggleTooltips();
        }

        private bool ShouldShowAvatarColumn()
        {
            return _rightPanelVisible && !_avatarAutoHidden && !_companionMode;
        }

        public void UpdatePanelToggleTooltips()
        {
            if (_toggleLeftPanelBtn != null)
            {
                _toggleLeftPanelBtn.tooltip = _leftPanelVisible
                    ? LocalizationExtensions.Get("tooltip.panel.left.hide", "Скрыть панель сессий")
                    : LocalizationExtensions.Get("tooltip.panel.left.show", "Показать панель сессий");
            }

            if (_toggleRightPanelBtn != null)
            {
                _toggleRightPanelBtn.tooltip = _rightPanelVisible
                    ? LocalizationExtensions.Get("tooltip.panel.right.hide", "Скрыть панель настроек")
                    : LocalizationExtensions.Get("tooltip.panel.right.show", "Показать панель настроек");
            }
        }

        public void ShowArea(VisualElement visible)
        {
            // На телефоне навигация закрывает открытые оверлеи.
            if (_formFactor == FormFactor.Phone)
            {
                CloseDrawer();
                CloseAvatarOverlay();
            }

            SetDisplay(_d.ChatPanel, visible == _d.ChatPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.HistoryPanel, visible == _d.HistoryPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ProvidersPanel, visible == _d.ProvidersPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.AvatarsPanel, visible == _d.AvatarsPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ThemesPanel, visible == _d.ThemesPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.PlaceholderArea, visible == _d.PlaceholderArea ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.SettingsPanel, visible == _d.SettingsPanel ? DisplayStyle.Flex : DisplayStyle.None);
        }

        // ============================================================
        // Platform: safe area + OS classes (on app-root)
        // ============================================================

        /// <summary>
        /// Применяет Safe Area (padding) и платформенные классы к app-root.
        /// Вызывается в Init() (если PlatformInfo есть в Deps) и повторно из
        /// MainViewController, когда сервис стал доступен после инициализации.
        /// </summary>
        public void ApplyPlatformLayout()
        {
            var target = _d.AppRoot;
            if (target == null || _d.PlatformInfo == null)
                return;

            ApplySafeAreaPadding();

            target.EnableInClassList("platform-android", Application.platform == RuntimePlatform.Android);
            target.EnableInClassList("platform-ios", Application.platform == RuntimePlatform.IPhonePlayer);
        }

        /// <summary>
        /// Safe Area → padding на app-root. На десктопе SafeArea = весь экран → 0.
        /// Пересчитывается на каждом изменении геометрии (в т.ч. поворот экрана).
        /// </summary>
        private void ApplySafeAreaPadding()
        {
            var target = _d.AppRoot;
            if (target == null || _d.PlatformInfo == null)
                return;

            var safeArea = _d.PlatformInfo.SafeArea;

            // Safe-area insets are in physical pixels, but UITK padding is in logical
            // (panel) units. Under ConstantPhysicalSize the panel is scaled
            // (logical = physical · refDpi/dpi), so a raw px inset would render ~dpi/refDpi
            // times too large (huge empty strips top/bottom). Convert via the measured
            // logical/physical ratio of the root.
            float screenW = Screen.width;
            float logicalW = target.resolvedStyle.width;
            float ratio = (screenW > 1f && logicalW > 1f) ? (logicalW / screenW) : 1f;

            target.style.paddingLeft = Mathf.Max(0f, safeArea.xMin) * ratio;
            target.style.paddingRight = Mathf.Max(0f, Screen.width - safeArea.xMax) * ratio;
            target.style.paddingTop = Mathf.Max(0f, Screen.height - safeArea.yMax) * ratio;
            target.style.paddingBottom = Mathf.Max(0f, safeArea.yMin) * ratio;
        }

        public void ApplyPlatformLayout(IPlatformInfoService info)
        {
            if (_d.AppRoot == null || info == null)
                return;

            _d.PlatformInfo = info;
            ApplyPlatformLayout();
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static void ClearLayoutInline(VisualElement element)
        {
            if (element == null)
                return;

            element.style.width = StyleKeyword.Null;
            element.style.position = StyleKeyword.Null;
            element.style.left = StyleKeyword.Null;
            element.style.top = StyleKeyword.Null;
            element.style.right = StyleKeyword.Null;
            element.style.bottom = StyleKeyword.Null;
            element.style.translate = StyleKeyword.Null;
            element.style.display = StyleKeyword.Null;
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
    }
}
