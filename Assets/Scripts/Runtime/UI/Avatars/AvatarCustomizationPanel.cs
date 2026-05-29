using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.Avatars
{
    public sealed class AvatarCustomizationPanel
    {
        private static readonly string[] PresetColors =
        {
            "#FFFFFF", "#7C7AED", "#4F46E5", "#22D3EE", "#22C55E", "#EAB308", "#F97316", "#EC4899", "#EF4444", "#111827"
        };

        private static readonly string[] EmojiOptions =
        {
            "", "✨", "🔥", "💖", "💙", "🌟", "😊", "😎", "🤖", "💫", "⚡", "🎯"
        };

        private static readonly string[] FrameStyles = { "none", "neon", "gold", "holographic" };

        private readonly VisualElement _root;
        private readonly TextField _primaryColorField;
        private readonly TextField _secondaryColorField;
        private readonly TextField _haloColorField;
        private readonly Slider _haloIntensitySlider;
        private readonly Slider _saturationSlider;
        private readonly Slider _brightnessSlider;
        private readonly Button _saveButton;
        private readonly Button _cancelButton;
        private readonly VisualElement _emojiGrid;
        private readonly VisualElement _frameSegment;
        private readonly VisualElement _swatchPrimary;
        private readonly VisualElement _swatchSecondary;
        private readonly VisualElement _swatchHalo;

        private readonly Dictionary<string, Button> _emojiButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Button> _frameButtons = new Dictionary<string, Button>();

        private AvatarCustomizationData _data = new AvatarCustomizationData();
        private bool _isBinding;

        public event Action<AvatarCustomizationData> Changed;
        public event Action Saved;
        public event Action Canceled;
        public AvatarCustomizationData CurrentData => CloneOrDefault(_data);

        public AvatarCustomizationPanel(VisualElement root)
        {
            _root = root;
            _primaryColorField = root.Q<TextField>("customize-primary-color");
            _secondaryColorField = root.Q<TextField>("customize-secondary-color");
            _haloColorField = root.Q<TextField>("customize-halo-color");
            _haloIntensitySlider = root.Q<Slider>("customize-halo-intensity");
            _saturationSlider = root.Q<Slider>("customize-saturation");
            _brightnessSlider = root.Q<Slider>("customize-brightness");
            _saveButton = root.Q<Button>("customize-save-btn");
            _cancelButton = root.Q<Button>("customize-cancel-btn");
            _emojiGrid = root.Q<VisualElement>("customize-emoji-grid");
            _frameSegment = root.Q<VisualElement>("customize-frame-segment");
            _swatchPrimary = root.Q<VisualElement>("customize-swatches-primary");
            _swatchSecondary = root.Q<VisualElement>("customize-swatches-secondary");
            _swatchHalo = root.Q<VisualElement>("customize-swatches-halo");

            BuildSwatches(_swatchPrimary, value => SetColorField(_primaryColorField, value));
            BuildSwatches(_swatchSecondary, value => SetColorField(_secondaryColorField, value));
            BuildSwatches(_swatchHalo, value => SetColorField(_haloColorField, value));
            BuildEmojiButtons();
            BuildFrameButtons();
            RegisterCallbacks();
            Bind(null);
        }

        public void Bind(AvatarCustomizationData source)
        {
            _isBinding = true;
            _data = CloneOrDefault(source);
            _primaryColorField.value = _data.PrimaryColor;
            _secondaryColorField.value = _data.SecondaryColor;
            _haloColorField.value = _data.HaloColor;
            _haloIntensitySlider.value = _data.HaloIntensity;
            _saturationSlider.value = _data.Saturation;
            _brightnessSlider.value = _data.Brightness;
            HighlightEmoji(_data.OverlayEmoji);
            HighlightFrame(_data.CustomFrame);
            _isBinding = false;
        }

        private void RegisterCallbacks()
        {
            _primaryColorField?.RegisterValueChangedCallback(_ =>
            {
                _data.PrimaryColor = NormalizeHex(_primaryColorField.value, "#FFFFFF");
                _primaryColorField.SetValueWithoutNotify(_data.PrimaryColor);
                EmitChanged();
            });
            _secondaryColorField?.RegisterValueChangedCallback(_ =>
            {
                _data.SecondaryColor = NormalizeHex(_secondaryColorField.value, "#7C7AED");
                _secondaryColorField.SetValueWithoutNotify(_data.SecondaryColor);
                EmitChanged();
            });
            _haloColorField?.RegisterValueChangedCallback(_ =>
            {
                _data.HaloColor = NormalizeHex(_haloColorField.value, "#7C7AED");
                _haloColorField.SetValueWithoutNotify(_data.HaloColor);
                EmitChanged();
            });
            _haloIntensitySlider?.RegisterValueChangedCallback(_ =>
            {
                _data.HaloIntensity = Mathf.Clamp01(_haloIntensitySlider.value);
                EmitChanged();
            });
            _saturationSlider?.RegisterValueChangedCallback(_ =>
            {
                _data.Saturation = Mathf.Clamp(_saturationSlider.value, 0f, 2f);
                EmitChanged();
            });
            _brightnessSlider?.RegisterValueChangedCallback(_ =>
            {
                _data.Brightness = Mathf.Clamp(_brightnessSlider.value, 0f, 2f);
                EmitChanged();
            });
            if (_saveButton != null) _saveButton.clicked += () => Saved?.Invoke();
            if (_cancelButton != null) _cancelButton.clicked += () => Canceled?.Invoke();
        }

        private void BuildSwatches(VisualElement container, Action<string> onPick)
        {
            if (container == null) return;
            container.Clear();
            foreach (var color in PresetColors)
            {
                var button = new Button(() => onPick(color));
                button.AddToClassList("customize__swatch");
                if (ColorUtility.TryParseHtmlString(color, out var parsed))
                    button.style.backgroundColor = new StyleColor(parsed);
                container.Add(button);
            }
        }

        private void SetColorField(TextField field, string value)
        {
            if (field == null) return;
            field.value = value;
        }

        private void BuildEmojiButtons()
        {
            if (_emojiGrid == null) return;
            _emojiGrid.Clear();
            _emojiButtons.Clear();
            foreach (var emoji in EmojiOptions)
            {
                string value = emoji;
                string labelText = string.IsNullOrEmpty(value) ? "∅" : value;
                var button = new Button(() =>
                {
                    _data.OverlayEmoji = value;
                    HighlightEmoji(value);
                    EmitChanged();
                })
                {
                    text = labelText
                };
                button.AddToClassList("customize__emoji-btn");
                _emojiGrid.Add(button);
                _emojiButtons[value] = button;
            }
        }

        private void BuildFrameButtons()
        {
            if (_frameSegment == null) return;
            _frameSegment.Clear();
            _frameButtons.Clear();
            foreach (var frame in FrameStyles)
            {
                string value = frame;
                var button = new Button(() =>
                {
                    _data.CustomFrame = value;
                    HighlightFrame(value);
                    EmitChanged();
                })
                {
                    text = value
                };
                button.AddToClassList("customize__frame-btn");
                if (value == FrameStyles[FrameStyles.Length - 1])
                    button.AddToClassList("customize__frame-btn--last");
                _frameSegment.Add(button);
                _frameButtons[value] = button;
            }
        }

        private void HighlightEmoji(string value)
        {
            foreach (var kvp in _emojiButtons)
                kvp.Value.EnableInClassList("is-active", kvp.Key == (value ?? string.Empty));
        }

        private void HighlightFrame(string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "none" : value.ToLowerInvariant();
            foreach (var kvp in _frameButtons)
                kvp.Value.EnableInClassList("is-active", kvp.Key == normalized);
        }

        private void EmitChanged()
        {
            if (_isBinding) return;
            Changed?.Invoke(CloneOrDefault(_data));
        }

        private static string NormalizeHex(string raw, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal))
                value = "#" + value;
            if (ColorUtility.TryParseHtmlString(value, out _))
                return value.ToUpperInvariant();
            return fallback;
        }

        private static AvatarCustomizationData CloneOrDefault(AvatarCustomizationData source)
        {
            var output = new AvatarCustomizationData();
            if (source == null)
                return output;

            output.PrimaryColor = NormalizeHex(source.PrimaryColor, output.PrimaryColor);
            output.SecondaryColor = NormalizeHex(source.SecondaryColor, output.SecondaryColor);
            output.HaloColor = NormalizeHex(source.HaloColor, output.HaloColor);
            output.HaloIntensity = Mathf.Clamp01(source.HaloIntensity);
            output.Saturation = Mathf.Clamp(source.Saturation <= 0f ? 1f : source.Saturation, 0f, 2f);
            output.Brightness = Mathf.Clamp(source.Brightness <= 0f ? 1f : source.Brightness, 0f, 2f);
            output.OverlayEmoji = source.OverlayEmoji ?? string.Empty;
            output.CustomFrame = string.IsNullOrWhiteSpace(source.CustomFrame) ? "none" : source.CustomFrame.ToLowerInvariant();
            return output;
        }
    }
}
