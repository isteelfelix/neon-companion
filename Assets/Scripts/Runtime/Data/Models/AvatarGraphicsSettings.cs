using System;

namespace NeonCompanion.Runtime.Data.Models
{
    /// <summary>
    /// Named values for the string-typed fields of <see cref="AvatarGraphicsSettings"/>.
    /// Strings keep settings.json readable and hand-editable, matching how the rest of
    /// <see cref="AppSettings"/> stores its modes.
    /// </summary>
    public static class GraphicsOptions
    {
        // preset
        public const string PresetLow = "low";
        public const string PresetMedium = "medium";
        public const string PresetHigh = "high";
        public const string PresetUltra = "ultra";
        public const string PresetCustom = "custom";

        // antialiasing
        public const string AaOff = "off";
        public const string AaMsaa = "msaa";
        public const string AaFxaa = "fxaa";
        public const string AaSmaa = "smaa";

        // tonemapping
        public const string TonemapOff = "off";
        public const string TonemapNeutral = "neutral";
        public const string TonemapAces = "aces";

        // quality tiers used by SMAA
        public const string QualityLow = "low";
        public const string QualityMedium = "medium";
        public const string QualityHigh = "high";

        public static readonly string[] Presets =
        {
            PresetLow, PresetMedium, PresetHigh, PresetUltra, PresetCustom
        };

        public static readonly string[] AntialiasingModes =
        {
            AaOff, AaMsaa, AaFxaa, AaSmaa
        };

        public static readonly string[] TonemappingModes =
        {
            TonemapOff, TonemapNeutral, TonemapAces
        };
    }

    /// <summary>
    /// Everything the avatar's render path exposes to the user. Applied by
    /// <c>GraphicsQualityService</c>; persisted inside <see cref="AppSettings"/> so the
    /// pet-window process reads the same values from the same file.
    /// </summary>
    [Serializable]
    public class AvatarGraphicsSettings
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;

        /// <summary>One of <see cref="GraphicsOptions.Presets"/>. Set to "custom" as soon as a single knob is touched.</summary>
        public string preset = GraphicsOptions.PresetHigh;

        // ===== Resolution =====

        /// <summary>Multiplier on the avatar view's own pixel size. 0.5 halves it, 2.0 supersamples.</summary>
        public float renderScale = 1f;

        /// <summary>Hard ceiling on the render texture's longest side. Guards against 4K panels.</summary>
        public int maxRenderSize = 2048;

        // ===== Anti-aliasing =====

        /// <summary>One of <see cref="GraphicsOptions.AntialiasingModes"/>.</summary>
        public string antialiasing = GraphicsOptions.AaMsaa;

        /// <summary>MSAA samples on the avatar render texture: 2, 4 or 8. Only read when <see cref="antialiasing"/> is "msaa".</summary>
        public int msaaSamples = 4;

        /// <summary>SMAA tier, one of the quality tiers. Only read when <see cref="antialiasing"/> is "smaa".</summary>
        public string smaaQuality = GraphicsOptions.QualityHigh;

        // ===== Frame pacing =====

        public bool vSync = true;

        /// <summary>App frame cap. 0 = uncapped (or driven by vSync).</summary>
        public int targetFrameRate = 0;

        /// <summary>How often the avatar itself is re-rendered, independent of the UI frame rate.</summary>
        public int avatarFrameRate = 60;

        /// <summary>Stop re-rendering the avatar while its view is off-screen or the panel is hidden.</summary>
        public bool pauseAvatarWhenHidden = true;

        // ===== Lighting rig =====

        public float keyLightIntensity = 1.1f;
        public float fillLightIntensity = 0.35f;
        public float rimLightIntensity = 0.9f;

        /// <summary>Key light colour temperature in Kelvin. 6500 is neutral white.</summary>
        public float lightTemperature = 6500f;

        public float ambientIntensity = 0.35f;

        // ===== Shadows =====

        public bool shadows = true;
        public bool softShadows = true;

        /// <summary>Main light shadowmap resolution: 256, 512, 1024, 2048 or 4096.</summary>
        public int shadowResolution = 1024;

        // ===== Post-processing =====

        public bool postProcessing = true;

        /// <summary>Render the avatar into a half-float target so bloom has headroom above 1.0.</summary>
        public bool hdr = true;

        /// <summary>One of <see cref="GraphicsOptions.TonemappingModes"/>.</summary>
        public string tonemapping = GraphicsOptions.TonemapNeutral;

        /// <summary>Bloom intensity. 0 disables the effect entirely.</summary>
        public float bloom = 0.35f;

        /// <summary>Vignette intensity, 0..1. 0 disables the effect.</summary>
        public float vignette = 0.15f;

        /// <summary>Colour saturation offset, -100..100.</summary>
        public float saturation = 0f;

        /// <summary>Contrast offset, -100..100.</summary>
        public float contrast = 0f;

        // ===== Textures =====

        /// <summary>Mipmap limit: 0 = full resolution, 1 = half, 2 = quarter.</summary>
        public int textureQuality = 0;

        public bool anisotropicFiltering = true;

        /// <summary>Deep copy — used to diff against the presets and to hand a snapshot to the pet window.</summary>
        public AvatarGraphicsSettings Clone()
        {
            var copy = new AvatarGraphicsSettings();
            copy.version = version;
            copy.preset = preset;
            copy.renderScale = renderScale;
            copy.maxRenderSize = maxRenderSize;
            copy.antialiasing = antialiasing;
            copy.msaaSamples = msaaSamples;
            copy.smaaQuality = smaaQuality;
            copy.vSync = vSync;
            copy.targetFrameRate = targetFrameRate;
            copy.avatarFrameRate = avatarFrameRate;
            copy.pauseAvatarWhenHidden = pauseAvatarWhenHidden;
            copy.keyLightIntensity = keyLightIntensity;
            copy.fillLightIntensity = fillLightIntensity;
            copy.rimLightIntensity = rimLightIntensity;
            copy.lightTemperature = lightTemperature;
            copy.ambientIntensity = ambientIntensity;
            copy.shadows = shadows;
            copy.softShadows = softShadows;
            copy.shadowResolution = shadowResolution;
            copy.postProcessing = postProcessing;
            copy.hdr = hdr;
            copy.tonemapping = tonemapping;
            copy.bloom = bloom;
            copy.vignette = vignette;
            copy.saturation = saturation;
            copy.contrast = contrast;
            copy.textureQuality = textureQuality;
            copy.anisotropicFiltering = anisotropicFiltering;
            return copy;
        }

        /// <summary>
        /// Clamps every field into its supported range and repairs unknown string values.
        /// Called after load, so a hand-edited or older settings file can never feed the
        /// render path something it cannot handle.
        /// </summary>
        public void Normalize()
        {
            version = CurrentVersion;
            preset = NormalizeChoice(preset, GraphicsOptions.Presets, GraphicsOptions.PresetHigh);
            antialiasing = NormalizeChoice(
                antialiasing, GraphicsOptions.AntialiasingModes, GraphicsOptions.AaMsaa);
            tonemapping = NormalizeChoice(
                tonemapping, GraphicsOptions.TonemappingModes, GraphicsOptions.TonemapNeutral);

            if (!string.Equals(smaaQuality, GraphicsOptions.QualityLow, StringComparison.Ordinal) &&
                !string.Equals(smaaQuality, GraphicsOptions.QualityMedium, StringComparison.Ordinal) &&
                !string.Equals(smaaQuality, GraphicsOptions.QualityHigh, StringComparison.Ordinal))
                smaaQuality = GraphicsOptions.QualityHigh;

            renderScale = Clamp(renderScale, 0.5f, 2f);
            maxRenderSize = NearestOf(maxRenderSize, 1024, 1536, 2048, 3072, 4096);
            msaaSamples = NearestOf(msaaSamples, 2, 4, 8);
            shadowResolution = NearestOf(shadowResolution, 256, 512, 1024, 2048, 4096);

            targetFrameRate = targetFrameRate <= 0 ? 0 : ClampInt(targetFrameRate, 15, 360);
            avatarFrameRate = ClampInt(avatarFrameRate, 15, 240);

            keyLightIntensity = Clamp(keyLightIntensity, 0f, 3f);
            fillLightIntensity = Clamp(fillLightIntensity, 0f, 3f);
            rimLightIntensity = Clamp(rimLightIntensity, 0f, 3f);
            lightTemperature = Clamp(lightTemperature, 3000f, 12000f);
            ambientIntensity = Clamp(ambientIntensity, 0f, 1.5f);

            bloom = Clamp(bloom, 0f, 2f);
            vignette = Clamp(vignette, 0f, 1f);
            saturation = Clamp(saturation, -100f, 100f);
            contrast = Clamp(contrast, -100f, 100f);

            textureQuality = ClampInt(textureQuality, 0, 2);
        }

        /// <summary>Overwrites every knob with the named preset. "custom" is left untouched.</summary>
        public void ApplyPreset(string presetId)
        {
            string id = NormalizeChoice(presetId, GraphicsOptions.Presets, GraphicsOptions.PresetHigh);
            if (string.Equals(id, GraphicsOptions.PresetCustom, StringComparison.Ordinal))
            {
                preset = id;
                return;
            }

            switch (id)
            {
                case GraphicsOptions.PresetLow:
                    renderScale = 0.75f;
                    maxRenderSize = 1024;
                    antialiasing = GraphicsOptions.AaOff;
                    msaaSamples = 2;
                    avatarFrameRate = 30;
                    shadows = false;
                    softShadows = false;
                    shadowResolution = 512;
                    postProcessing = false;
                    hdr = false;
                    tonemapping = GraphicsOptions.TonemapOff;
                    bloom = 0f;
                    vignette = 0f;
                    keyLightIntensity = 1.1f;
                    fillLightIntensity = 0.3f;
                    rimLightIntensity = 0f;
                    ambientIntensity = 0.4f;
                    textureQuality = 1;
                    anisotropicFiltering = false;
                    break;

                case GraphicsOptions.PresetMedium:
                    renderScale = 1f;
                    maxRenderSize = 1536;
                    antialiasing = GraphicsOptions.AaFxaa;
                    msaaSamples = 2;
                    avatarFrameRate = 60;
                    shadows = false;
                    softShadows = false;
                    shadowResolution = 512;
                    postProcessing = true;
                    hdr = false;
                    tonemapping = GraphicsOptions.TonemapNeutral;
                    bloom = 0.2f;
                    vignette = 0.1f;
                    keyLightIntensity = 1.1f;
                    fillLightIntensity = 0.35f;
                    rimLightIntensity = 0.6f;
                    ambientIntensity = 0.35f;
                    textureQuality = 0;
                    anisotropicFiltering = true;
                    break;

                case GraphicsOptions.PresetUltra:
                    renderScale = 1.5f;
                    maxRenderSize = 3072;
                    antialiasing = GraphicsOptions.AaMsaa;
                    msaaSamples = 8;
                    avatarFrameRate = 120;
                    shadows = true;
                    softShadows = true;
                    shadowResolution = 2048;
                    postProcessing = true;
                    hdr = true;
                    tonemapping = GraphicsOptions.TonemapAces;
                    bloom = 0.45f;
                    vignette = 0.15f;
                    keyLightIntensity = 1.15f;
                    fillLightIntensity = 0.4f;
                    rimLightIntensity = 1f;
                    ambientIntensity = 0.32f;
                    textureQuality = 0;
                    anisotropicFiltering = true;
                    break;

                default: // high
                    renderScale = 1f;
                    maxRenderSize = 2048;
                    antialiasing = GraphicsOptions.AaMsaa;
                    msaaSamples = 4;
                    avatarFrameRate = 60;
                    shadows = true;
                    softShadows = true;
                    shadowResolution = 1024;
                    postProcessing = true;
                    hdr = true;
                    tonemapping = GraphicsOptions.TonemapNeutral;
                    bloom = 0.35f;
                    vignette = 0.15f;
                    keyLightIntensity = 1.1f;
                    fillLightIntensity = 0.35f;
                    rimLightIntensity = 0.9f;
                    ambientIntensity = 0.35f;
                    textureQuality = 0;
                    anisotropicFiltering = true;
                    break;
            }

            // Preset-independent knobs keep their user value.
            preset = id;
            Normalize();
        }

        /// <summary>
        /// True when every knob still matches the named preset. Lets the UI show a real
        /// preset name instead of dropping to "custom" the moment a slider is nudged back
        /// to its preset value.
        /// </summary>
        public bool MatchesPreset(string presetId)
        {
            if (string.IsNullOrEmpty(presetId) ||
                string.Equals(presetId, GraphicsOptions.PresetCustom, StringComparison.Ordinal))
                return false;

            var reference = new AvatarGraphicsSettings();
            reference.ApplyPreset(presetId);

            return Mathf01(renderScale, reference.renderScale) &&
                   maxRenderSize == reference.maxRenderSize &&
                   string.Equals(antialiasing, reference.antialiasing, StringComparison.Ordinal) &&
                   msaaSamples == reference.msaaSamples &&
                   avatarFrameRate == reference.avatarFrameRate &&
                   shadows == reference.shadows &&
                   softShadows == reference.softShadows &&
                   shadowResolution == reference.shadowResolution &&
                   postProcessing == reference.postProcessing &&
                   hdr == reference.hdr &&
                   string.Equals(tonemapping, reference.tonemapping, StringComparison.Ordinal) &&
                   Mathf01(bloom, reference.bloom) &&
                   Mathf01(vignette, reference.vignette) &&
                   Mathf01(keyLightIntensity, reference.keyLightIntensity) &&
                   Mathf01(fillLightIntensity, reference.fillLightIntensity) &&
                   Mathf01(rimLightIntensity, reference.rimLightIntensity) &&
                   Mathf01(ambientIntensity, reference.ambientIntensity) &&
                   textureQuality == reference.textureQuality &&
                   anisotropicFiltering == reference.anisotropicFiltering;
        }

        /// <summary>Re-derives <see cref="preset"/> after a knob changed: a named preset if it still matches, "custom" otherwise.</summary>
        public void RefreshPresetLabel()
        {
            for (int i = 0; i < GraphicsOptions.Presets.Length; i++)
            {
                string candidate = GraphicsOptions.Presets[i];
                if (string.Equals(candidate, GraphicsOptions.PresetCustom, StringComparison.Ordinal))
                    continue;
                if (MatchesPreset(candidate))
                {
                    preset = candidate;
                    return;
                }
            }

            preset = GraphicsOptions.PresetCustom;
        }

        private static bool Mathf01(float a, float b)
        {
            float diff = a - b;
            if (diff < 0f)
                diff = -diff;
            return diff < 0.0001f;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value))
                return min;
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static int NearestOf(int value, params int[] allowed)
        {
            int best = allowed[0];
            int bestDistance = int.MaxValue;
            for (int i = 0; i < allowed.Length; i++)
            {
                int distance = value - allowed[i];
                if (distance < 0)
                    distance = -distance;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = allowed[i];
                }
            }
            return best;
        }

        private static string NormalizeChoice(string value, string[] allowed, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            for (int i = 0; i < allowed.Length; i++)
            {
                if (string.Equals(value, allowed[i], StringComparison.OrdinalIgnoreCase))
                    return allowed[i];
            }
            return fallback;
        }
    }
}
