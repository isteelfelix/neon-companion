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

        // antialiasing — the MSAA level is part of the mode so the UI needs one control
        // instead of a mode dropdown plus a dependent level dropdown.
        public const string AaOff = "off";
        public const string AaFxaa = "fxaa";
        public const string AaSmaa = "smaa";
        public const string AaMsaa2 = "msaa2";
        public const string AaMsaa4 = "msaa4";
        public const string AaMsaa8 = "msaa8";

        // shadows
        public const string ShadowsOff = "off";
        public const string ShadowsHard = "hard";
        public const string ShadowsSoft = "soft";

        public static readonly string[] Presets =
        {
            PresetLow, PresetMedium, PresetHigh, PresetUltra, PresetCustom
        };

        public static readonly string[] AntialiasingModes =
        {
            AaOff, AaFxaa, AaSmaa, AaMsaa2, AaMsaa4, AaMsaa8
        };

        public static readonly string[] ShadowModes =
        {
            ShadowsOff, ShadowsHard, ShadowsSoft
        };
    }

    /// <summary>
    /// The avatar's render quality. Deliberately small: only knobs a user can hear
    /// themselves ask for. Everything else — HDR, texture mip limit, shadow map size,
    /// resolution ceiling — is derived from the preset and never shown, because those are
    /// consequences of a quality choice rather than choices of their own.
    ///
    /// Applied by <c>GraphicsQualityService</c>; persisted inside <see cref="AppSettings"/>
    /// so the pet-window process reads the same values from the same file.
    /// </summary>
    [Serializable]
    public class AvatarGraphicsSettings
    {
        public const int CurrentVersion = 2;

        public int version = CurrentVersion;

        /// <summary>One of <see cref="GraphicsOptions.Presets"/>. Becomes "custom" as soon as a knob is touched.</summary>
        public string preset = GraphicsOptions.PresetHigh;

        // ===== Shown in the settings UI =====

        /// <summary>Render resolution relative to the avatar view's on-screen size. Above 1 supersamples.</summary>
        public float renderScale = 1f;

        /// <summary>One of <see cref="GraphicsOptions.AntialiasingModes"/>.</summary>
        public string antialiasing = GraphicsOptions.AaMsaa4;

        public bool vSync = true;

        /// <summary>App frame cap. 0 = uncapped (or driven by vSync).</summary>
        public int targetFrameRate = 0;

        /// <summary>How often the avatar itself is re-rendered, independent of the UI frame rate.</summary>
        public int avatarFrameRate = 60;

        /// <summary>Master multiplier over the whole three-point light rig and the ambient term.</summary>
        public float brightness = 1f;

        /// <summary>One of <see cref="GraphicsOptions.ShadowModes"/>.</summary>
        public string shadows = GraphicsOptions.ShadowsSoft;

        public bool postProcessing = true;

        /// <summary>Bloom intensity. 0 disables the effect entirely.</summary>
        public float bloom = 0.35f;

        // ===== Derived from the preset, never shown =====

        /// <summary>Ceiling on the render target's longest side, so a 4K panel cannot run away.</summary>
        public int maxRenderSize = 2048;

        /// <summary>Main light shadowmap resolution.</summary>
        public int shadowResolution = 1024;

        /// <summary>Half-float render target, so bloom has headroom above 1.0. Follows post-processing.</summary>
        public bool hdr = true;

        /// <summary>Mipmap limit: 0 = full resolution, 1 = half, 2 = quarter.</summary>
        public int textureQuality = 0;

        public bool anisotropicFiltering = true;

        // ===== Derived lighting =====
        //
        // The rig ratios are fixed: a portrait wants a dominant key, a soft fill that keeps
        // the shadow side readable, and a rim that lifts the silhouette off the background.
        // Exposing all three as separate sliders only lets the user break a good default.

        public float KeyLightIntensity
        {
            get { return 1.1f * brightness; }
        }

        public float FillLightIntensity
        {
            get { return 0.35f * brightness; }
        }

        public float RimLightIntensity
        {
            get { return 0.9f * brightness; }
        }

        public float AmbientIntensity
        {
            get { return 0.35f * brightness; }
        }

        // ===== Antialiasing helpers =====

        /// <summary>MSAA sample count for the current mode, or 1 when MSAA is not selected.</summary>
        public int MsaaSamples
        {
            get
            {
                if (string.Equals(antialiasing, GraphicsOptions.AaMsaa2, StringComparison.Ordinal))
                    return 2;
                if (string.Equals(antialiasing, GraphicsOptions.AaMsaa4, StringComparison.Ordinal))
                    return 4;
                if (string.Equals(antialiasing, GraphicsOptions.AaMsaa8, StringComparison.Ordinal))
                    return 8;
                return 1;
            }
        }

        public bool UsesFxaa
        {
            get { return string.Equals(antialiasing, GraphicsOptions.AaFxaa, StringComparison.Ordinal); }
        }

        public bool UsesSmaa
        {
            get { return string.Equals(antialiasing, GraphicsOptions.AaSmaa, StringComparison.Ordinal); }
        }

        public bool ShadowsEnabled
        {
            get { return !string.Equals(shadows, GraphicsOptions.ShadowsOff, StringComparison.Ordinal); }
        }

        public bool SoftShadows
        {
            get { return string.Equals(shadows, GraphicsOptions.ShadowsSoft, StringComparison.Ordinal); }
        }

        /// <summary>Deep copy — used to diff against the presets and to hand a snapshot to the pet window.</summary>
        public AvatarGraphicsSettings Clone()
        {
            var copy = new AvatarGraphicsSettings();
            copy.version = version;
            copy.preset = preset;
            copy.renderScale = renderScale;
            copy.antialiasing = antialiasing;
            copy.vSync = vSync;
            copy.targetFrameRate = targetFrameRate;
            copy.avatarFrameRate = avatarFrameRate;
            copy.brightness = brightness;
            copy.shadows = shadows;
            copy.postProcessing = postProcessing;
            copy.bloom = bloom;
            copy.maxRenderSize = maxRenderSize;
            copy.shadowResolution = shadowResolution;
            copy.hdr = hdr;
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
            preset = NormalizeChoice(preset, GraphicsOptions.Presets, GraphicsOptions.PresetHigh);
            antialiasing = NormalizeChoice(
                antialiasing, GraphicsOptions.AntialiasingModes, GraphicsOptions.AaMsaa4);
            shadows = NormalizeChoice(
                shadows, GraphicsOptions.ShadowModes, GraphicsOptions.ShadowsSoft);

            renderScale = Clamp(renderScale, 0.5f, 2f);
            brightness = Clamp(brightness, 0.4f, 1.8f);
            bloom = Clamp(bloom, 0f, 2f);

            targetFrameRate = targetFrameRate <= 0 ? 0 : ClampInt(targetFrameRate, 15, 360);
            avatarFrameRate = ClampInt(avatarFrameRate, 15, 240);

            maxRenderSize = NearestOf(maxRenderSize, 1024, 1536, 2048, 3072, 4096);
            shadowResolution = NearestOf(shadowResolution, 256, 512, 1024, 2048, 4096);
            textureQuality = ClampInt(textureQuality, 0, 2);

            // A settings file written before version 2 has the old field layout; the fields
            // that survived are still valid, and the ones that did not are simply absent,
            // which leaves them at the defaults above.
            version = CurrentVersion;
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
                    antialiasing = GraphicsOptions.AaOff;
                    avatarFrameRate = 30;
                    brightness = 1f;
                    shadows = GraphicsOptions.ShadowsOff;
                    postProcessing = false;
                    bloom = 0f;
                    maxRenderSize = 1024;
                    shadowResolution = 512;
                    hdr = false;
                    textureQuality = 1;
                    anisotropicFiltering = false;
                    break;

                case GraphicsOptions.PresetMedium:
                    renderScale = 1f;
                    antialiasing = GraphicsOptions.AaFxaa;
                    avatarFrameRate = 60;
                    brightness = 1f;
                    shadows = GraphicsOptions.ShadowsHard;
                    postProcessing = true;
                    bloom = 0.2f;
                    maxRenderSize = 1536;
                    shadowResolution = 512;
                    hdr = true;
                    textureQuality = 0;
                    anisotropicFiltering = true;
                    break;

                case GraphicsOptions.PresetUltra:
                    renderScale = 1.5f;
                    antialiasing = GraphicsOptions.AaMsaa8;
                    avatarFrameRate = 120;
                    brightness = 1f;
                    shadows = GraphicsOptions.ShadowsSoft;
                    postProcessing = true;
                    bloom = 0.45f;
                    maxRenderSize = 3072;
                    shadowResolution = 2048;
                    hdr = true;
                    textureQuality = 0;
                    anisotropicFiltering = true;
                    break;

                default: // high
                    renderScale = 1f;
                    antialiasing = GraphicsOptions.AaMsaa4;
                    avatarFrameRate = 60;
                    brightness = 1f;
                    shadows = GraphicsOptions.ShadowsSoft;
                    postProcessing = true;
                    bloom = 0.35f;
                    maxRenderSize = 2048;
                    shadowResolution = 1024;
                    hdr = true;
                    textureQuality = 0;
                    anisotropicFiltering = true;
                    break;
            }

            preset = id;
            Normalize();
        }

        /// <summary>
        /// True when every visible knob still matches the named preset. Lets the UI show a
        /// real preset name instead of dropping to "custom" the moment a slider is nudged
        /// back to its preset value.
        /// </summary>
        public bool MatchesPreset(string presetId)
        {
            if (string.IsNullOrEmpty(presetId) ||
                string.Equals(presetId, GraphicsOptions.PresetCustom, StringComparison.Ordinal))
                return false;

            var reference = new AvatarGraphicsSettings();
            reference.ApplyPreset(presetId);

            return Approximately(renderScale, reference.renderScale) &&
                   string.Equals(antialiasing, reference.antialiasing, StringComparison.Ordinal) &&
                   avatarFrameRate == reference.avatarFrameRate &&
                   Approximately(brightness, reference.brightness) &&
                   string.Equals(shadows, reference.shadows, StringComparison.Ordinal) &&
                   postProcessing == reference.postProcessing &&
                   Approximately(bloom, reference.bloom);
        }

        /// <summary>
        /// Re-derives <see cref="preset"/> after a knob changed: a named preset if it still
        /// matches, "custom" otherwise. A named match also pulls in that preset's hidden
        /// fields, so the derived settings never drift out of step with the visible ones.
        /// </summary>
        public void RefreshPresetLabel()
        {
            for (int i = 0; i < GraphicsOptions.Presets.Length; i++)
            {
                string candidate = GraphicsOptions.Presets[i];
                if (string.Equals(candidate, GraphicsOptions.PresetCustom, StringComparison.Ordinal))
                    continue;
                if (MatchesPreset(candidate))
                {
                    ApplyPreset(candidate);
                    return;
                }
            }

            preset = GraphicsOptions.PresetCustom;
            // Custom keeps the user's visible choices but still needs sane hidden values.
            hdr = postProcessing;
            Normalize();
        }

        private static bool Approximately(float a, float b)
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
