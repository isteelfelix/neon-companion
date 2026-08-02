using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// The "Graphics" card on the settings page. Deliberately short: a preset plus the
    /// handful of knobs a user would actually go looking for. Everything a preset can
    /// decide on its own — HDR, shadow map size, texture mips, resolution ceiling — is
    /// derived in <see cref="AvatarGraphicsSettings"/> and never shown.
    ///
    /// Built in C# rather than UXML so dependent rows can be removed from layout outright;
    /// UXML's display handling does not reliably do that.
    ///
    /// The card owns the live settings instance: every change applies immediately through
    /// <see cref="GraphicsQualityService"/> and then asks the host to persist it.
    /// </summary>
    internal sealed class SettingsGraphicsCard
    {
        internal sealed class Deps
        {
            /// <summary>Persists the current settings — wired to SettingsController.SaveSettings.</summary>
            public Action Save;
        }

        private Deps _deps;
        private VisualElement _card;
        private AvatarGraphicsSettings _settings = new AvatarGraphicsSettings();
        private bool _suppressCallbacks;

        private readonly List<Action> _refreshActions = new List<Action>();
        private readonly List<OptionSet> _optionSets = new List<OptionSet>();

        private OptionSet _presetOptions;
        private NeonDropdown _presetDropdown;
        private VisualElement _fpsCapRow;
        private VisualElement _bloomRow;

        /// <summary>The live settings block. The host copies this into AppSettings when saving.</summary>
        internal AvatarGraphicsSettings Settings
        {
            get { return _settings; }
        }

        /// <summary>The card element, so the tab bar can show and hide it.</summary>
        internal VisualElement Card
        {
            get { return _card; }
        }

        // ============================================================
        // Build
        // ============================================================

        internal void Init(VisualElement root, Deps deps)
        {
            _deps = deps;
            if (root == null)
                return;

            var scroll = root.Q<ScrollView>(className: "settings-scroll");
            if (scroll == null)
                return;

            var existing = root.Q<VisualElement>("settings-graphics-card");
            if (existing != null)
                existing.RemoveFromHierarchy();

            _refreshActions.Clear();
            _optionSets.Clear();

            _card = new VisualElement();
            _card.name = "settings-graphics-card";
            _card.AddToClassList("settings-card");
            _card.Add(BuildHead());

            BuildPresetRow(_card);
            BuildImageGroup(_card);
            BuildFrameGroup(_card);
            BuildSceneGroup(_card);

            // Right after "General", so the tab bar can present it as its own page.
            var content = scroll.contentContainer;
            content.Insert(content.childCount > 0 ? 1 : 0, _card);
        }

        private VisualElement BuildHead()
        {
            var head = new VisualElement();
            head.AddToClassList("settings-card__head");

            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList("icon--monitor");
            icon.AddToClassList("settings-card__icon");
            head.Add(icon);

            var title = new Label(Get("settings.graphics.title", "Графика"));
            title.AddToClassList("settings-card__title");
            head.Add(title);
            return head;
        }

        private void BuildPresetRow(VisualElement parent)
        {
            _presetOptions = new OptionSet(
                GraphicsOptions.Presets,
                new string[]
                {
                    "settings.graphics.preset.low",
                    "settings.graphics.preset.medium",
                    "settings.graphics.preset.high",
                    "settings.graphics.preset.ultra",
                    "settings.graphics.preset.custom"
                },
                new string[] { "Низкое", "Среднее", "Высокое", "Ультра", "Своё" });
            _optionSets.Add(_presetOptions);

            _presetDropdown = AddDropdownRow(
                parent,
                "settings.graphics.preset", "Качество",
                "settings.graphics.preset.sub",
                "Задаёт всё разом. Правка любого параметра ниже переключает пресет на «Своё».",
                _presetOptions,
                () => _settings.preset,
                id =>
                {
                    if (string.Equals(id, GraphicsOptions.PresetCustom, StringComparison.Ordinal))
                        return;
                    _settings.ApplyPreset(id);
                    ApplyAndSave(refreshAll: true);
                });

            _refreshActions.Add(() =>
                _presetDropdown.SetValueWithoutNotify(_presetOptions.LabelFor(_settings.preset)));
        }

        private void BuildImageGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.image", "Изображение");

            AddSliderRow(
                parent,
                "settings.graphics.render_scale", "Масштаб рендера",
                "settings.graphics.render_scale.sub",
                "Разрешение аватара относительно его размера на экране. Выше 100% сглаживает за счёт сверхсэмплинга.",
                0.5f, 2f, 0.05f,
                () => _settings.renderScale,
                v => { _settings.renderScale = v; },
                v => Mathf.RoundToInt(v * 100f) + "%");

            var aaOptions = new OptionSet(
                GraphicsOptions.AntialiasingModes,
                new string[]
                {
                    "settings.graphics.aa.off",
                    "settings.graphics.aa.fxaa",
                    "settings.graphics.aa.smaa",
                    "settings.graphics.aa.msaa2",
                    "settings.graphics.aa.msaa4",
                    "settings.graphics.aa.msaa8"
                },
                new string[] { "Выключено", "FXAA", "SMAA", "MSAA 2x", "MSAA 4x", "MSAA 8x" });
            _optionSets.Add(aaOptions);

            NeonDropdown aa = AddDropdownRow(
                parent,
                "settings.graphics.aa", "Сглаживание",
                "settings.graphics.aa.sub",
                "FXAA дешевле всех и слегка мылит. SMAA чище. MSAA даёт лучшие кромки и стоит дороже всего.",
                aaOptions,
                () => _settings.antialiasing,
                id => { _settings.antialiasing = id; OnKnobChanged(); });

            _refreshActions.Add(() =>
                aa.SetValueWithoutNotify(aaOptions.LabelFor(_settings.antialiasing)));
        }

        private void BuildFrameGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.frames", "Кадры");

            Toggle vsync = AddToggleRow(
                parent,
                "settings.graphics.vsync", "Вертикальная синхронизация",
                "settings.graphics.vsync.sub",
                "Синхронизирует кадры с монитором и убирает разрывы. Пока включена, ограничение FPS не действует.",
                () => _settings.vSync,
                v => { _settings.vSync = v; OnKnobChanged(); });

            Slider fpsCap = AddSliderRow(
                parent,
                "settings.graphics.fps_cap", "Ограничение FPS",
                null, null,
                14f, 240f, 1f,
                () => _settings.targetFrameRate <= 0 ? 14f : _settings.targetFrameRate,
                v => { _settings.targetFrameRate = v < 15f ? 0 : Mathf.RoundToInt(v); },
                v => v < 15f
                    ? Get("settings.graphics.fps_cap.none", "без предела")
                    : Mathf.RoundToInt(v).ToString());
            _fpsCapRow = RowOf(fpsCap);

            AddSliderRow(
                parent,
                "settings.graphics.avatar_fps", "Частота рендера аватара",
                "settings.graphics.avatar_fps.sub",
                "Как часто перерисовывается 3D-аватар. Не связано с частотой интерфейса — снижение заметно экономит GPU.",
                15f, 120f, 1f,
                () => _settings.avatarFrameRate,
                v => { _settings.avatarFrameRate = Mathf.RoundToInt(v); },
                v => Mathf.RoundToInt(v) + " FPS");

            _refreshActions.Add(() =>
            {
                vsync.SetValueWithoutNotify(_settings.vSync);
                SetRowVisible(_fpsCapRow, !_settings.vSync);
            });
        }

        private void BuildSceneGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.scene", "Сцена");

            AddSliderRow(
                parent,
                "settings.graphics.brightness", "Яркость",
                "settings.graphics.brightness.sub",
                "Общая яркость освещения аватара.",
                0.4f, 1.8f, 0.05f,
                () => _settings.brightness,
                v => { _settings.brightness = v; },
                v => Mathf.RoundToInt(v * 100f) + "%");

            var shadowOptions = new OptionSet(
                GraphicsOptions.ShadowModes,
                new string[]
                {
                    "settings.graphics.shadows.off",
                    "settings.graphics.shadows.hard",
                    "settings.graphics.shadows.soft"
                },
                new string[] { "Выключены", "Жёсткие", "Мягкие" });
            _optionSets.Add(shadowOptions);

            NeonDropdown shadows = AddDropdownRow(
                parent,
                "settings.graphics.shadows", "Тени",
                "settings.graphics.shadows.sub",
                "Собственные тени модели: от носа, чёлки, воротника.",
                shadowOptions,
                () => _settings.shadows,
                id => { _settings.shadows = id; OnKnobChanged(); });

            Toggle postFx = AddToggleRow(
                parent,
                "settings.graphics.postfx", "Пост-обработка",
                "settings.graphics.postfx.sub",
                "Тонемаппинг и свечение бликов.",
                () => _settings.postProcessing,
                v => { _settings.postProcessing = v; OnKnobChanged(); });

            Slider bloom = AddSliderRow(
                parent,
                "settings.graphics.bloom", "Свечение бликов",
                null, null,
                0f, 1f, 0.05f,
                () => _settings.bloom,
                v => { _settings.bloom = v; },
                v => v < 0.001f
                    ? Get("settings.graphics.bloom.off", "выкл")
                    : Mathf.RoundToInt(v * 100f) + "%");
            _bloomRow = RowOf(bloom);
            if (_bloomRow != null)
                _bloomRow.AddToClassList("settings-row--last");

            _refreshActions.Add(() =>
            {
                shadows.SetValueWithoutNotify(shadowOptions.LabelFor(_settings.shadows));
                postFx.SetValueWithoutNotify(_settings.postProcessing);
                SetRowVisible(_bloomRow, _settings.postProcessing);
            });
        }

        // ============================================================
        // Population / persistence
        // ============================================================

        /// <summary>Adopts the settings loaded from disk and pushes them into the engine.</summary>
        internal void SetSettings(AvatarGraphicsSettings settings)
        {
            _settings = settings != null ? settings : new AvatarGraphicsSettings();
            _settings.Normalize();
            GraphicsQualityService.Apply(_settings);
            RefreshAll();
        }

        private void OnKnobChanged()
        {
            if (_suppressCallbacks)
                return;
            _settings.RefreshPresetLabel();
            ApplyAndSave(refreshAll: true);
        }

        /// <summary>
        /// Slider drags call this on every frame of the drag, so it skips the full control
        /// refresh — re-populating a slider mid-drag fights the user's pointer. Only the
        /// preset label, which the drag can change, is updated.
        /// </summary>
        private void OnSliderChanged()
        {
            if (_suppressCallbacks)
                return;
            _settings.RefreshPresetLabel();
            ApplyAndSave(refreshAll: false);

            if (_presetDropdown != null && _presetOptions != null)
                _presetDropdown.SetValueWithoutNotify(_presetOptions.LabelFor(_settings.preset));
        }

        private void ApplyAndSave(bool refreshAll)
        {
            GraphicsQualityService.Apply(_settings);
            if (refreshAll)
                RefreshAll();
            if (_deps != null && _deps.Save != null)
                _deps.Save();
        }

        private void RefreshAll()
        {
            _suppressCallbacks = true;
            try
            {
                for (int i = 0; i < _refreshActions.Count; i++)
                    _refreshActions[i]();
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        /// <summary>Re-labels every dropdown after a language switch.</summary>
        internal void RefreshLocalization()
        {
            for (int i = 0; i < _optionSets.Count; i++)
                _optionSets[i].RefreshLabels();
            RefreshAll();
        }

        // ============================================================
        // Row builders
        // ============================================================

        private void AddGroupHeader(VisualElement parent, string key, string fallback)
        {
            var header = new Label(Get(key, fallback));
            header.AddToClassList("settings-group");
            parent.Add(header);
        }

        private VisualElement CreateRow(
            VisualElement parent, string nameKey, string nameFallback,
            string subKey, string subFallback)
        {
            var row = new VisualElement();
            row.AddToClassList("settings-row");

            var copy = new VisualElement();
            copy.AddToClassList("settings-row__copy");

            var name = new Label(Get(nameKey, nameFallback));
            name.AddToClassList("settings-row__name");
            copy.Add(name);

            if (!string.IsNullOrEmpty(subFallback))
            {
                var sub = new Label(Get(subKey, subFallback));
                sub.AddToClassList("settings-row__sub");
                copy.Add(sub);
            }

            row.Add(copy);
            parent.Add(row);
            return row;
        }

        private Toggle AddToggleRow(
            VisualElement parent, string nameKey, string nameFallback,
            string subKey, string subFallback,
            Func<bool> read, Action<bool> write)
        {
            VisualElement row = CreateRow(parent, nameKey, nameFallback, subKey, subFallback);

            var toggle = new Toggle();
            toggle.AddToClassList("settings-toggle");
            toggle.SetValueWithoutNotify(read());
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (_suppressCallbacks)
                    return;
                write(evt.newValue);
            });
            row.Add(toggle);
            return toggle;
        }

        private NeonDropdown AddDropdownRow(
            VisualElement parent, string nameKey, string nameFallback,
            string subKey, string subFallback,
            OptionSet options, Func<string> read, Action<string> write)
        {
            VisualElement row = CreateRow(parent, nameKey, nameFallback, subKey, subFallback);

            var dropdown = new NeonDropdown();
            dropdown.AddToClassList("settings-dropdown");
            dropdown.choices = options.Labels;
            dropdown.SetValueWithoutNotify(options.LabelFor(read()));
            options.Bind(dropdown);
            dropdown.RegisterCallback<ChangeEvent<string>>(evt =>
            {
                if (_suppressCallbacks)
                    return;
                string id = options.IdFor(evt.newValue);
                if (id != null)
                    write(id);
            });
            row.Add(dropdown);
            return dropdown;
        }

        private Slider AddSliderRow(
            VisualElement parent, string nameKey, string nameFallback,
            string subKey, string subFallback,
            float low, float high, float step,
            Func<float> read, Action<float> write, Func<float, string> format)
        {
            VisualElement row = CreateRow(parent, nameKey, nameFallback, subKey, subFallback);

            var control = new VisualElement();
            control.AddToClassList("settings-slider-row");

            var slider = new Slider(low, high);
            slider.AddToClassList("slider-input");
            slider.showInputField = false;
            slider.SetValueWithoutNotify(read());

            var value = new Label(format(read()));
            value.AddToClassList("settings-row__value-mono");
            value.AddToClassList("settings-slider-value");

            slider.RegisterValueChangedCallback(evt =>
            {
                float snapped = step > 0f ? Mathf.Round(evt.newValue / step) * step : evt.newValue;
                value.text = format(snapped);
                if (_suppressCallbacks)
                    return;
                write(snapped);
                OnSliderChanged();
            });

            _refreshActions.Add(() =>
            {
                slider.SetValueWithoutNotify(read());
                value.text = format(read());
            });

            control.Add(slider);
            control.Add(value);
            row.Add(control);
            return slider;
        }

        /// <summary>The settings row a slider lives in: slider → control container → row.</summary>
        private static VisualElement RowOf(Slider slider)
        {
            if (slider == null || slider.parent == null)
                return null;
            return slider.parent.parent;
        }

        private static void SetRowVisible(VisualElement row, bool visible)
        {
            if (row == null)
                return;
            row.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string Get(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key))
                return fallback;
            return LocalizationExtensions.Get(key, fallback);
        }

        /// <summary>
        /// Maps stable option ids to the localized labels a <see cref="NeonDropdown"/>
        /// shows, so a language switch never changes what gets written to settings.
        /// </summary>
        private sealed class OptionSet
        {
            private readonly string[] _ids;
            private readonly string[] _keys;
            private readonly string[] _fallbacks;
            private readonly List<string> _labels = new List<string>();
            private NeonDropdown _boundDropdown;

            internal OptionSet(string[] ids, string[] keys, string[] fallbacks)
            {
                _ids = ids;
                _keys = keys;
                _fallbacks = fallbacks;
                RefreshLabels();
            }

            internal List<string> Labels
            {
                get { return _labels; }
            }

            internal void Bind(NeonDropdown dropdown)
            {
                _boundDropdown = dropdown;
            }

            internal void RefreshLabels()
            {
                _labels.Clear();
                for (int i = 0; i < _ids.Length; i++)
                {
                    string key = _keys != null && i < _keys.Length ? _keys[i] : null;
                    string fallback = _fallbacks != null && i < _fallbacks.Length
                        ? _fallbacks[i]
                        : _ids[i];
                    _labels.Add(Get(key, fallback));
                }

                if (_boundDropdown != null)
                    _boundDropdown.choices = _labels;
            }

            internal string LabelFor(string id)
            {
                for (int i = 0; i < _ids.Length; i++)
                {
                    if (string.Equals(_ids[i], id, StringComparison.Ordinal))
                        return _labels[i];
                }
                return _labels.Count > 0 ? _labels[0] : string.Empty;
            }

            internal string IdFor(string label)
            {
                for (int i = 0; i < _labels.Count; i++)
                {
                    if (string.Equals(_labels[i], label, StringComparison.Ordinal))
                        return _ids[i];
                }
                return null;
            }
        }
    }
}
