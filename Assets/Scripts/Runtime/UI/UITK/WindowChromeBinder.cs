using System;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Связывает безрамочное окно с UITK: перетаскивание за фон топбара,
    /// разворот/восстановление по двойному клику и кнопки управления окном
    /// (свернуть / развернуть / закрыть).
    ///
    /// Кнопки и поля в топбаре остаются кликабельными — drag запускается только
    /// на "пустой" части (фон, заголовок-Label, разделитель). Кнопки окна
    /// добавляются только когда сервис доступен (Windows-borderless); на других
    /// платформах биндер простаивает.
    ///
    /// Добавляется к тому же GameObject, что и UIDocument (см. RuntimeUiInstaller).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class WindowChromeBinder : MonoBehaviour
    {
        private static readonly Color GlyphColor = new Color(0.82f, 0.84f, 0.9f, 1f);

        private IWindowChromeService _chrome;
        private VisualElement _root;
        private VisualElement _topbar;
        private bool _controlsBuilt;
        private IVisualElementScheduledItem _buildSchedule;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                return;

            _root = document.rootVisualElement;
            _topbar = _root.Q<VisualElement>(className: "topbar");
            if (_topbar == null)
                return;

            _topbar.RegisterCallback<PointerDownEvent>(OnTopbarPointerDown);

            // Сервис регистрируется в AppBootstrap; к моменту первого кадра UI он
            // обычно готов, но на всякий случай ретраим, пока не построим кнопки.
            _buildSchedule = _topbar.schedule.Execute(BuildWindowControlsIfReady).Every(200);
        }

        private void OnDisable()
        {
            if (_topbar != null)
                _topbar.UnregisterCallback<PointerDownEvent>(OnTopbarPointerDown);
            _buildSchedule?.Pause();
            _buildSchedule = null;
            _controlsBuilt = false;
            _topbar = null;
            _root = null;
        }

        private void OnTopbarPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0)
                return;

            var target = evt.target as VisualElement;
            if (IsInteractive(target))
                return;

            var chrome = ResolveChrome();
            if (chrome == null || !chrome.IsAvailable)
                return;

            if (evt.clickCount >= 2)
            {
                chrome.ToggleMaximize();
                return;
            }

            // Развёрнутое окно перетаскивать не даём (как в стандартном поведении).
            if (chrome.IsMaximized)
                return;

            chrome.BeginDrag();
        }

        /// <summary>
        /// true, если нажатие пришлось на интерактивный элемент (кнопку, поле,
        /// дропдаун) — такие клики нельзя перехватывать под drag.
        /// </summary>
        private bool IsInteractive(VisualElement element)
        {
            while (element != null && element != _topbar)
            {
                if (element is Button || element is TextField || element is Toggle || element is Slider)
                    return true;

                if (element.ClassListContains("btn") ||
                    element.ClassListContains("iconbtn") ||
                    element.ClassListContains("topbar__model-picker"))
                    return true;

                element = element.parent;
            }

            return false;
        }

        private void BuildWindowControlsIfReady()
        {
            if (_controlsBuilt)
            {
                _buildSchedule?.Pause();
                return;
            }

            var chrome = ResolveChrome();
            if (chrome == null)
                return; // App ещё не готов — ждём следующего тика.

            if (!chrome.IsAvailable)
            {
                // Не Windows-borderless — кнопки окна не нужны.
                _buildSchedule?.Pause();
                _controlsBuilt = true;
                return;
            }

            var rightBar = _topbar.Q<VisualElement>(className: "topbar__right") ?? _topbar;

            var minimize = CreateControlButton("Свернуть", BuildMinimizeGlyph, () => chrome.Minimize());
            var maximize = CreateControlButton("Развернуть", BuildMaximizeGlyph, () => chrome.ToggleMaximize());
            var close = CreateControlButton("Закрыть", BuildCloseGlyph, RequestClose);
            close.AddToClassList("winctrl--close");

            rightBar.Add(minimize);
            rightBar.Add(maximize);
            rightBar.Add(close);

            _controlsBuilt = true;
            _buildSchedule?.Pause();
        }

        private Button CreateControlButton(string tooltip, Action<VisualElement> glyphBuilder, Action onClick)
        {
            var button = new Button(onClick);
            button.AddToClassList("iconbtn");
            button.AddToClassList("winctrl");
            button.tooltip = tooltip;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;

            var glyph = new VisualElement();
            glyph.pickingMode = PickingMode.Ignore;
            glyphBuilder(glyph);
            button.Add(glyph);

            return button;
        }

        private static void BuildMinimizeGlyph(VisualElement glyph)
        {
            glyph.style.width = 11;
            glyph.style.height = 2;
            glyph.style.backgroundColor = GlyphColor;
        }

        private static void BuildMaximizeGlyph(VisualElement glyph)
        {
            glyph.style.width = 11;
            glyph.style.height = 11;
            glyph.style.backgroundColor = Color.clear;
            glyph.style.borderTopWidth = 1.5f;
            glyph.style.borderRightWidth = 1.5f;
            glyph.style.borderBottomWidth = 1.5f;
            glyph.style.borderLeftWidth = 1.5f;
            glyph.style.borderTopColor = GlyphColor;
            glyph.style.borderRightColor = GlyphColor;
            glyph.style.borderBottomColor = GlyphColor;
            glyph.style.borderLeftColor = GlyphColor;
        }

        private static void BuildCloseGlyph(VisualElement glyph)
        {
            glyph.style.width = 14;
            glyph.style.height = 14;
            glyph.style.position = Position.Relative;

            glyph.Add(CreateCrossBar(45f));
            glyph.Add(CreateCrossBar(-45f));
        }

        private static VisualElement CreateCrossBar(float angle)
        {
            var bar = new VisualElement();
            bar.pickingMode = PickingMode.Ignore;
            bar.style.position = Position.Absolute;
            bar.style.top = 6;
            bar.style.left = -1;
            bar.style.width = 16;
            bar.style.height = 1.5f;
            bar.style.backgroundColor = GlyphColor;
            bar.style.rotate = new Rotate(new Angle(angle, AngleUnit.Degree));
            return bar;
        }

        private void RequestClose()
        {
            // Переиспользуем существующий диалог подтверждения (кнопка nav-close
            // открывает его через SettingsController). Если его нет — закрываем напрямую.
            var navClose = _root?.Q<Button>("nav-close");
            if (navClose != null)
            {
                using (var click = ClickEvent.GetPooled())
                {
                    click.target = navClose;
                    navClose.SendEvent(click);
                }
                return;
            }

            Application.Quit();
        }

        private IWindowChromeService ResolveChrome()
        {
            if (_chrome != null)
                return _chrome;

            var bootstrap = UnityEngine.Object.FindAnyObjectByType<AppBootstrap>();
            if (bootstrap == null || bootstrap.App == null)
                return null;

            IWindowChromeService svc;
            if (bootstrap.App.Services.TryGet(out svc))
                _chrome = svc;

            return _chrome;
        }
    }
}
