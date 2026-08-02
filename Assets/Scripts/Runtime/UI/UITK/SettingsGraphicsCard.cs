using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// The "Graphics" card on the settings page: a conventional game-style quality screen
    /// with a preset at the top and grouped knobs below. Built in C# rather than UXML so it
    /// can show and hide dependent rows (MSAA level only under MSAA, the FPS cap only when
    /// vsync is off) — UXML's display handling does not remove elements from layout.
    ///
    /// The card owns the live <see cref="AvatarGraphicsSettings"/> instance; every change
    /// applies immediately through <see cref="GraphicsQualityService"/> and then asks the
    /// host to persist it.
    /// </summary>
    internal sealed class SettingsGraphicsCard
    {
        internal sealed class Deps
        {
            /// <summary>Persists the current settings — wired to SettingsController.SaveSettings.</summary>
            public Action Save;

            /// <summary>The renderer driving the in-app avatar column, for the diagnostics rows.</summary>
            public Func<Avatar3DRenderer> GetRenderer;
        }

        private Deps _deps;
        private VisualElement _card;
        private AvatarGraphicsSettings _settings = new AvatarGraphicsSettings();
        private bool _suppressCallbacks;

        // Controls that need to be re-read or re-shown after a preset change.
        private readonly List<Action> _refreshActions = new List<Action>();
        private readonly List<OptionSet> _optionSets = new List<OptionSet>();

        private OptionSet _presetOptions;
        private VisualElement _msaaRow;
        private VisualElement _smaaRow;
        private VisualElement _fpsCapRow;
        private VisualElement _softShadowRow;
        private VisualElement _shadowResRow;
        private VisualElement _postFxRows;
        private Label _alphaWarning;
        private Label _diagResolution;
        private Label _diagFps;
        private Label _diagRenderer;
        private IVisualElementScheduledItem _diagnosticsSchedule;

        /// <summary>The live settings block. The host copies this into AppSettings when saving.</summary>
        internal AvatarGraphicsSettings Settings
        {
            get { return _settings; }
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
            BuildResolutionGroup(_card);
            BuildAntialiasingGroup(_card);
            BuildFrameGroup(_card);
            BuildLightingGroup(_card);
            BuildPostFxGroup(_card);
            BuildTextureGroup(_card);
            BuildDiagnosticsGroup(_card);

            // Right after "General", before "Security" — quality belongs near the top.
            var content = scroll.contentContainer;
            content.Insert(content.childCount > 0 ? 1 : 0, _card);

            _diagnosticsSchedule = _card.schedule.Execute(RefreshDiagnostics).Every(500);
        }

        private VisualElement BuildHead()
        {
            var head = new VisualElement();
            head.AddToClassList("settings-card__head");

            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList("icon--sparkle");
            icon.AddToClassList("settings-card__icon");
            head.Add(icon);

            var title = new Label(Get("settings.graphics.title", "Графика"));
            title.AddToClassList("settings-card__title");
            head.Add(title);
            return head;
        }

        // ============================================================
        // Groups
        // ============================================================

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
                new string[] { "Низкое", "Среднее", "Высокое", "Ультра", "Пользовательское" });
            _optionSets.Add(_presetOptions);

            NeonDropdown dropdown = AddDropdownRow(
                parent,
                "settings.graphics.preset", "Пресет качества",
                "settings.graphics.preset.sub",
                "Меняет сразу все параметры ниже. Любая ручная правка переводит пресет в «Пользовательское».",
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
                dropdown.SetValueWithoutNotify(_presetOptions.LabelFor(_settings.preset)));
        }

        private void BuildResolutionGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.resolution", "Разрешение");

            AddSliderRow(
                parent,
                "settings.graphics.render_scale", "Масштаб рендера",
                "settings.graphics.render_scale.sub",
                "Разрешение картинки аватара относительно его размера на экране. Выше 100% — сглаживание за счёт сверхсэмплинга.",
                0.5f, 2f, 0.05f,
                () => _settings.renderScale,
                v => { _settings.renderScale = v; },
                v => Mathf.RoundToInt(v * 100f) + "%");

            var maxSizeOptions = new OptionSet(
                new string[] { "1024", "1536", "2048", "3072", "4096" },
                null,
                new string[] { "1024 px", "1536 px", "2048 px", "3072 px", "4096 px" });
            _optionSets.Add(maxSizeOptions);

            NeonDropdown dropdown = AddDropdownRow(
                parent,
                "settings.graphics.max_size", "Предел разрешения",
                "settings.graphics.max_size.sub",
                "Верхняя граница длинной стороны кадра. Защищает от лишней нагрузки на 4K-мониторах.",
                maxSizeOptions,
                () => _settings.maxRenderSize.ToString(),
                id =>
                {
                    int parsed;
                    if (!int.TryParse(id, out parsed))
                        return;
                    _settings.maxRenderSize = parsed;
                    OnKnobChanged();
                });

            _refreshActions.Add(() =>
                dropdown.SetValueWithoutNotify(maxSizeOptions.LabelFor(_settings.maxRenderSize.ToString())));
        }

        private void BuildAntialiasingGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.aa", "Сглаживание");

            var aaOptions = new OptionSet(
                GraphicsOptions.AntialiasingModes,
                new string[]
                {
                    "settings.graphics.aa.off",
                    "settings.graphics.aa.msaa",
                    "settings.graphics.aa.fxaa",
                    "settings.graphics.aa.smaa"
                },
                new string[] { "Выключено", "MSAA", "FXAA", "SMAA" });
            _optionSets.Add(aaOptions);

            NeonDropdown modeDropdown = AddDropdownRow(
                parent,
                "settings.graphics.aa", "Сглаживание",
                "settings.graphics.aa.sub",
                "MSAA — лучшее качество кромок и самая высокая цена. FXAA дешевле всех, но слегка мылит. SMAA — компромисс.",
                aaOptions,
                () => _settings.antialiasing,
                id => { _settings.antialiasing = id; OnKnobChanged(); });

            var msaaOptions = new OptionSet(
                new string[] { "2", "4", "8" },
                null,
                new string[] { "2x", "4x", "8x" });
            _optionSets.Add(msaaOptions);

            NeonDropdown msaaDropdown = AddDropdownRow(
                parent,
                "settings.graphics.msaa_level", "Уровень MSAA",
                null, null,
                msaaOptions,
                () => _settings.msaaSamples.ToString(),
                id =>
                {
                    int parsed;
                    if (!int.TryParse(id, out parsed))
                        return;
                    _settings.msaaSamples = parsed;
                    OnKnobChanged();
                });
            _msaaRow = msaaDropdown.parent;

            var smaaOptions = new OptionSet(
                new string[] { GraphicsOptions.QualityLow, GraphicsOptions.QualityMedium, GraphicsOptions.QualityHigh },
                new string[]
                {
                    "settings.graphics.quality.low",
                    "settings.graphics.quality.medium",
                    "settings.graphics.quality.high"
                },
                new string[] { "Низкое", "Среднее", "Высокое" });
            _optionSets.Add(smaaOptions);

            NeonDropdown smaaDropdown = AddDropdownRow(
                parent,
                "settings.graphics.smaa_quality", "Качество SMAA",
                null, null,
                smaaOptions,
                () => _settings.smaaQuality,
                id => { _settings.smaaQuality = id; OnKnobChanged(); });
            _smaaRow = smaaDropdown.parent;

            _refreshActions.Add(() =>
            {
                modeDropdown.SetValueWithoutNotify(aaOptions.LabelFor(_settings.antialiasing));
                msaaDropdown.SetValueWithoutNotify(msaaOptions.LabelFor(_settings.msaaSamples.ToString()));
                smaaDropdown.SetValueWithoutNotify(smaaOptions.LabelFor(_settings.smaaQuality));

                SetRowVisible(_msaaRow, string.Equals(
                    _settings.antialiasing, GraphicsOptions.AaMsaa, StringComparison.Ordinal));
                SetRowVisible(_smaaRow, string.Equals(
                    _settings.antialiasing, GraphicsOptions.AaSmaa, StringComparison.Ordinal));
            });
        }

        private void BuildFrameGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.frames", "Кадры");

            Toggle vsync = AddToggleRow(
                parent,
                "settings.graphics.vsync", "Вертикальная синхронизация",
                "settings.graphics.vsync.sub",
                "Синхронизирует кадры с частотой монитора и убирает разрывы картинки. Пока включена, ограничение FPS не действует.",
                () => _settings.vSync,
                v => { _settings.vSync = v; OnKnobChanged(); });

            Slider fpsCap = AddSliderRow(
                parent,
                "settings.graphics.fps_cap", "Ограничение FPS",
                "settings.graphics.fps_cap.sub",
                "Верхний предел частоты кадров приложения. Крайнее левое положение снимает ограничение.",
                14f, 240f, 1f,
                () => _settings.targetFrameRate <= 0 ? 14f : _settings.targetFrameRate,
                v => { _settings.targetFrameRate = v < 15f ? 0 : Mathf.RoundToInt(v); },
                v => v < 15f
                    ? Get("settings.graphics.fps_cap.none", "без предела")
                    : Mathf.RoundToInt(v).ToString());
            _fpsCapRow = fpsCap.parent != null ? fpsCap.parent.parent : null;

            AddSliderRow(
                parent,
                "settings.graphics.avatar_fps", "Частота рендера аватара",
                "settings.graphics.avatar_fps.sub",
                "Как часто перерисовывается 3D-аватар. Не связано с частотой интерфейса — снижение заметно экономит GPU.",
                15f, 120f, 1f,
                () => _settings.avatarFrameRate,
                v => { _settings.avatarFrameRate = Mathf.RoundToInt(v); },
                v => Mathf.RoundToInt(v) + " FPS");

            AddToggleRow(
                parent,
                "settings.graphics.pause_hidden", "Пауза, когда аватар не виден",
                "settings.graphics.pause_hidden.sub",
                "Не тратить кадры, пока панель аватара скрыта. Последний кадр остаётся в текстуре, возврат мгновенный.",
                () => _settings.pauseAvatarWhenHidden,
                v => { _settings.pauseAvatarWhenHidden = v; OnKnobChanged(); });

            _refreshActions.Add(() =>
            {
                vsync.SetValueWithoutNotify(_settings.vSync);
                SetRowVisible(_fpsCapRow, !_settings.vSync);
            });
        }

        private void BuildLightingGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.lighting", "Освещение и тени");

            AddSliderRow(
                parent,
                "settings.graphics.key_light", "Ключевой свет",
                "settings.graphics.key_light.sub",
                "Основной источник. Он же отбрасывает тени.",
                0f, 3f, 0.05f,
                () => _settings.keyLightIntensity,
                v => { _settings.keyLightIntensity = v; },
                FormatIntensity);

            AddSliderRow(
                parent,
                "settings.graphics.fill_light", "Заполняющий свет",
                "settings.graphics.fill_light.sub",
                "Подсвечивает теневую сторону лица, чтобы она не проваливалась в черноту.",
                0f, 3f, 0.05f,
                () => _settings.fillLightIntensity,
                v => { _settings.fillLightIntensity = v; },
                FormatIntensity);

            AddSliderRow(
                parent,
                "settings.graphics.rim_light", "Контровой свет",
                "settings.graphics.rim_light.sub",
                "Обводка по контуру сзади — отделяет фигуру от фона.",
                0f, 3f, 0.05f,
                () => _settings.rimLightIntensity,
                v => { _settings.rimLightIntensity = v; },
                FormatIntensity);

            AddSliderRow(
                parent,
                "settings.graphics.temperature", "Температура света",
                "settings.graphics.temperature.sub",
                "Ниже 6500 K — тёплый оттенок, выше — холодный.",
                3000f, 12000f, 100f,
                () => _settings.lightTemperature,
                v => { _settings.lightTemperature = v; },
                v => Mathf.RoundToInt(v) + " K");

            AddSliderRow(
                parent,
                "settings.graphics.ambient", "Общая засветка",
                "settings.graphics.ambient.sub",
                "Равномерный свет со всех сторон. Поднимает глубину теней.",
                0f, 1.5f, 0.05f,
                () => _settings.ambientIntensity,
                v => { _settings.ambientIntensity = v; },
                FormatIntensity);

            Toggle shadows = AddToggleRow(
                parent,
                "settings.graphics.shadows", "Тени",
                "settings.graphics.shadows.sub",
                "Собственные тени модели: от носа, чёлки, воротника. Фона под аватаром нет, так что тень падает только на него самого.",
                () => _settings.shadows,
                v => { _settings.shadows = v; OnKnobChanged(); });

            Toggle softShadows = AddToggleRow(
                parent,
                "settings.graphics.soft_shadows", "Мягкие тени",
                null, null,
                () => _settings.softShadows,
                v => { _settings.softShadows = v; OnKnobChanged(); });
            _softShadowRow = softShadows.parent;

            var shadowResOptions = new OptionSet(
                new string[] { "256", "512", "1024", "2048", "4096" },
                null,
                new string[] { "256", "512", "1024", "2048", "4096" });
            _optionSets.Add(shadowResOptions);

            NeonDropdown shadowRes = AddDropdownRow(
                parent,
                "settings.graphics.shadow_res", "Разрешение теней",
                null, null,
                shadowResOptions,
                () => _settings.shadowResolution.ToString(),
                id =>
                {
                    int parsed;
                    if (!int.TryParse(id, out parsed))
                        return;
                    _settings.shadowResolution = parsed;
                    OnKnobChanged();
                });
            _shadowResRow = shadowRes.parent;

            _refreshActions.Add(() =>
            {
                shadows.SetValueWithoutNotify(_settings.shadows);
                softShadows.SetValueWithoutNotify(_settings.softShadows);
                shadowRes.SetValueWithoutNotify(shadowResOptions.LabelFor(_settings.shadowResolution.ToString()));
                SetRowVisible(_softShadowRow, _settings.shadows);
                SetRowVisible(_shadowResRow, _settings.shadows);
            });
        }

        private void BuildPostFxGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.postfx", "Пост-обработка");

            Toggle postFx = AddToggleRow(
                parent,
                "settings.graphics.postfx", "Пост-обработка",
                "settings.graphics.postfx.sub",
                "Включает Bloom, тонемаппинг и цветокоррекцию для аватара.",
                () => _settings.postProcessing,
                v => { _settings.postProcessing = v; OnKnobChanged(); });

            _alphaWarning = new Label(Get(
                "settings.graphics.postfx.alpha_warning",
                "URP-ассет запрещает альфу после пост-обработки — прозрачный фон аватара станет непрозрачным, поэтому эффекты не применяются."));
            _alphaWarning.AddToClassList("settings-row__sub");
            _alphaWarning.style.color = new StyleColor(new Color(0.89f, 0.44f, 0.28f));
            _alphaWarning.style.paddingBottom = 6;
            _alphaWarning.style.display = DisplayStyle.None;
            parent.Add(_alphaWarning);

            _postFxRows = new VisualElement();
            parent.Add(_postFxRows);

            var tonemapOptions = new OptionSet(
                GraphicsOptions.TonemappingModes,
                new string[]
                {
                    "settings.graphics.tonemap.off",
                    "settings.graphics.tonemap.neutral",
                    "settings.graphics.tonemap.aces"
                },
                new string[] { "Выключен", "Neutral", "ACES" });
            _optionSets.Add(tonemapOptions);

            NeonDropdown tonemap = AddDropdownRow(
                _postFxRows,
                "settings.graphics.tonemap", "Тонемаппинг",
                "settings.graphics.tonemap.sub",
                "Сжимает HDR-диапазон в экранный. Neutral сохраняет цвета, ACES даёт кинематографичный контраст.",
                tonemapOptions,
                () => _settings.tonemapping,
                id => { _settings.tonemapping = id; OnKnobChanged(); });

            Toggle hdr = AddToggleRow(
                _postFxRows,
                "settings.graphics.hdr", "HDR",
                "settings.graphics.hdr.sub",
                "Рендер в half-float. Нужен, чтобы у Bloom был запас яркости выше единицы.",
                () => _settings.hdr,
                v => { _settings.hdr = v; OnKnobChanged(); });

            AddSliderRow(
                _postFxRows,
                "settings.graphics.bloom", "Bloom",
                "settings.graphics.bloom.sub",
                "Свечение ярких участков. Ноль полностью выключает эффект.",
                0f, 2f, 0.05f,
                () => _settings.bloom,
                v => { _settings.bloom = v; },
                FormatIntensity);

            AddSliderRow(
                _postFxRows,
                "settings.graphics.vignette", "Виньетка",
                null, null,
                0f, 1f, 0.05f,
                () => _settings.vignette,
                v => { _settings.vignette = v; },
                FormatIntensity);

            AddSliderRow(
                _postFxRows,
                "settings.graphics.saturation", "Насыщенность",
                null, null,
                -100f, 100f, 1f,
                () => _settings.saturation,
                v => { _settings.saturation = v; },
                FormatSigned);

            AddSliderRow(
                _postFxRows,
                "settings.graphics.contrast", "Контраст",
                null, null,
                -100f, 100f, 1f,
                () => _settings.contrast,
                v => { _settings.contrast = v; },
                FormatSigned);

            _refreshActions.Add(() =>
            {
                postFx.SetValueWithoutNotify(_settings.postProcessing);
                hdr.SetValueWithoutNotify(_settings.hdr);
                tonemap.SetValueWithoutNotify(tonemapOptions.LabelFor(_settings.tonemapping));
                SetRowVisible(_postFxRows, _settings.postProcessing);
                _alphaWarning.style.display =
                    _settings.postProcessing && !AvatarPostFxVolume.AlphaOutputAllowed
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
            });
        }

        private void BuildTextureGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.textures", "Текстуры");

            var textureOptions = new OptionSet(
                new string[] { "0", "1", "2" },
                new string[]
                {
                    "settings.graphics.texture.full",
                    "settings.graphics.texture.half",
                    "settings.graphics.texture.quarter"
                },
                new string[] { "Полное", "Половина", "Четверть" });
            _optionSets.Add(textureOptions);

            NeonDropdown textures = AddDropdownRow(
                parent,
                "settings.graphics.texture_quality", "Качество текстур",
                "settings.graphics.texture_quality.sub",
                "Снижение освобождает видеопамять, но заметно размывает лицо и одежду.",
                textureOptions,
                () => _settings.textureQuality.ToString(),
                id =>
                {
                    int parsed;
                    if (!int.TryParse(id, out parsed))
                        return;
                    _settings.textureQuality = parsed;
                    OnKnobChanged();
                });

            Toggle aniso = AddToggleRow(
                parent,
                "settings.graphics.aniso", "Анизотропная фильтрация",
                "settings.graphics.aniso.sub",
                "Держит текстуры чёткими на поверхностях под острым углом.",
                () => _settings.anisotropicFiltering,
                v => { _settings.anisotropicFiltering = v; OnKnobChanged(); });

            _refreshActions.Add(() =>
            {
                textures.SetValueWithoutNotify(textureOptions.LabelFor(_settings.textureQuality.ToString()));
                aniso.SetValueWithoutNotify(_settings.anisotropicFiltering);
            });
        }

        private void BuildDiagnosticsGroup(VisualElement parent)
        {
            AddGroupHeader(parent, "settings.graphics.group.diagnostics", "Диагностика");

            _diagResolution = AddValueRow(parent, "settings.graphics.diag.resolution", "Разрешение кадра");
            _diagFps = AddValueRow(parent, "settings.graphics.diag.fps", "Реальная частота аватара");
            _diagRenderer = AddValueRow(parent, "settings.graphics.diag.renderer", "Рендерер");

            var row = new VisualElement();
            row.AddToClassList("settings-row");
            row.AddToClassList("settings-row--last");

            var copy = new VisualElement();
            copy.AddToClassList("settings-row__copy");
            var name = new Label(Get("settings.graphics.reset", "Сбросить настройки графики"));
            name.AddToClassList("settings-row__name");
            copy.Add(name);
            row.Add(copy);

            var button = new Button(() =>
            {
                _settings.ApplyPreset(GraphicsOptions.PresetHigh);
                ApplyAndSave(refreshAll: true);
            });
            button.AddToClassList("btn");
            button.text = Get("settings.graphics.reset.action", "Сбросить");
            row.Add(button);
            parent.Add(row);
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
        /// refresh — re-populating a slider mid-drag fights the user's pointer.
        /// </summary>
        private void OnSliderChanged()
        {
            if (_suppressCallbacks)
                return;
            _settings.RefreshPresetLabel();
            ApplyAndSave(refreshAll: false);

            if (_presetOptions != null)
            {
                NeonDropdown presetDropdown = _card != null
                    ? _card.Q<NeonDropdown>("settings-graphics-preset")
                    : null;
                if (presetDropdown != null)
                    presetDropdown.SetValueWithoutNotify(_presetOptions.LabelFor(_settings.preset));
            }
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

        private void RefreshDiagnostics()
        {
            if (_card == null || _card.panel == null)
                return;

            Avatar3DRenderer renderer = _deps != null && _deps.GetRenderer != null
                ? _deps.GetRenderer()
                : null;

            if (_diagResolution != null)
            {
                if (renderer == null || renderer.RenderSize.x <= 0)
                {
                    _diagResolution.text = "—";
                }
                else
                {
                    Vector2Int size = renderer.RenderSize;
                    _diagResolution.text = size.x + " x " + size.y;
                }
            }

            if (_diagFps != null)
                _diagFps.text = renderer == null
                    ? "—"
                    : Mathf.RoundToInt(renderer.MeasuredFps) + " FPS";

            if (_diagRenderer != null)
                _diagRenderer.text = GraphicsQualityService.AvatarRendererIndex >= 0
                    ? Get("settings.graphics.diag.renderer.3d", "3D (Universal)")
                    : Get("settings.graphics.diag.renderer.2d", "2D — запусти Neon > Graphics > Repair");
        }

        // ============================================================
        // Row builders
        // ============================================================

        private void AddGroupHeader(VisualElement parent, string key, string fallback)
        {
            var header = new Label(Get(key, fallback));
            header.AddToClassList("settings-row__name");
            header.style.marginTop = 10;
            header.style.marginBottom = 2;
            header.style.opacity = 0.75f;
            header.style.fontSize = 11;
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
            dropdown.name = "settings-graphics-" + nameKey.Substring(nameKey.LastIndexOf('.') + 1);
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
            control.style.flexDirection = FlexDirection.Row;
            control.style.alignItems = Align.Center;
            control.style.flexShrink = 0;

            var slider = new Slider(low, high);
            slider.AddToClassList("slider-input");
            slider.showInputField = false;
            slider.style.minWidth = 130;
            slider.style.maxWidth = 150;
            slider.SetValueWithoutNotify(read());

            var value = new Label(format(read()));
            value.AddToClassList("settings-row__value-mono");
            value.style.minWidth = 62;
            value.style.marginLeft = 8;
            value.style.unityTextAlign = TextAnchor.MiddleRight;

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

        private Label AddValueRow(VisualElement parent, string nameKey, string nameFallback)
        {
            var row = new VisualElement();
            row.AddToClassList("settings-row");
            row.AddToClassList("settings-row--compact");

            var name = new Label(Get(nameKey, nameFallback));
            name.AddToClassList("settings-row__name");
            row.Add(name);

            var value = new Label("—");
            value.AddToClassList("settings-row__value-mono");
            row.Add(value);

            parent.Add(row);
            return value;
        }

        private static void SetRowVisible(VisualElement row, bool visible)
        {
            if (row == null)
                return;
            row.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ============================================================
        // Formatting / localization helpers
        // ============================================================

        private static string FormatIntensity(float value)
        {
            return value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatSigned(float value)
        {
            int rounded = Mathf.RoundToInt(value);
            return rounded > 0 ? "+" + rounded : rounded.ToString();
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
