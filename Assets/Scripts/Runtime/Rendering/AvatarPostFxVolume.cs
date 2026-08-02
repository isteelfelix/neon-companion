using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NeonCompanion.Runtime.Rendering
{
    /// <summary>
    /// Owns the single global <see cref="Volume"/> that drives the avatar's post-processing.
    /// The profile is built in code rather than shipped as an asset so the settings card can
    /// move sliders straight into it with no asset churn.
    ///
    /// Only the avatar camera has <c>renderPostProcessing</c> enabled, so this volume never
    /// touches the rest of the app even though it is global.
    ///
    /// Transparency note: URP only keeps the alpha channel through post-processing when the
    /// pipeline asset has "Allow Post Process Alpha Output" enabled. Without it the avatar's
    /// transparent background turns opaque, so <see cref="AvatarGraphicsSettings.postProcessing"/>
    /// is only honoured while that flag is on — see <see cref="AlphaOutputAllowed"/>.
    /// </summary>
    internal static class AvatarPostFxVolume
    {
        private static GameObject _host;
        private static Volume _volume;
        private static VolumeProfile _profile;
        private static Bloom _bloom;
        private static Tonemapping _tonemapping;
        private static ColorAdjustments _colorAdjustments;
        private static Vignette _vignette;

        /// <summary>
        /// True when post-processing can run without flattening the avatar's transparent
        /// background. Drives the warning shown next to the post-processing switch.
        /// </summary>
        internal static bool AlphaOutputAllowed
        {
            get
            {
                UniversalRenderPipelineAsset urp = UniversalRenderPipeline.asset;
                return urp != null && urp.allowPostProcessAlphaOutput;
            }
        }

        internal static void Apply(AvatarGraphicsSettings settings)
        {
            if (settings == null)
                return;

            bool enabled = settings.postProcessing && AlphaOutputAllowed;
            if (!enabled)
            {
                if (_volume != null)
                    _volume.enabled = false;
                return;
            }

            EnsureVolume();
            if (_volume == null)
                return;

            _volume.enabled = true;
            ApplyBloom(settings);
            ApplyTonemapping(settings);
            ApplyColorAdjustments(settings);
            ApplyVignette(settings);
        }

        private static void EnsureVolume()
        {
            if (_volume != null)
                return;

            _host = new GameObject("Avatar3D_PostFxVolume");
            _host.hideFlags = HideFlags.DontSave;
            // Layer 0 (Default) matches UniversalAdditionalCameraData's default volume mask.
            _host.layer = 0;
            Object.DontDestroyOnLoad(_host);

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "Avatar3D_PostFxProfile";
            _profile.hideFlags = HideFlags.DontSave;

            _bloom = _profile.Add<Bloom>(true);
            _tonemapping = _profile.Add<Tonemapping>(true);
            _colorAdjustments = _profile.Add<ColorAdjustments>(true);
            _vignette = _profile.Add<Vignette>(true);

            _volume = _host.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 1f;
            _volume.weight = 1f;
            _volume.profile = _profile;
        }

        private static void ApplyBloom(AvatarGraphicsSettings settings)
        {
            if (_bloom == null)
                return;

            _bloom.active = settings.bloom > 0.001f;
            _bloom.intensity.Override(settings.bloom);
            // Measured against the built-in VRM at the default key light: the render never
            // exceeds ~0.63 luminance, so URP's default threshold of 0.9 would make the
            // bloom slider do nothing at all. At 0.45 only the genuine highlights — eye
            // catchlights and hair specular, well under 1% of the avatar — pick it up.
            _bloom.threshold.Override(0.45f);
            _bloom.scatter.Override(0.7f);
        }

        private static void ApplyTonemapping(AvatarGraphicsSettings settings)
        {
            if (_tonemapping == null)
                return;

            if (string.Equals(settings.tonemapping, GraphicsOptions.TonemapAces, System.StringComparison.Ordinal))
            {
                _tonemapping.active = true;
                _tonemapping.mode.Override(TonemappingMode.ACES);
            }
            else if (string.Equals(settings.tonemapping, GraphicsOptions.TonemapNeutral, System.StringComparison.Ordinal))
            {
                _tonemapping.active = true;
                _tonemapping.mode.Override(TonemappingMode.Neutral);
            }
            else
            {
                _tonemapping.active = false;
                _tonemapping.mode.Override(TonemappingMode.None);
            }
        }

        private static void ApplyColorAdjustments(AvatarGraphicsSettings settings)
        {
            if (_colorAdjustments == null)
                return;

            bool needed = Mathf.Abs(settings.saturation) > 0.01f ||
                          Mathf.Abs(settings.contrast) > 0.01f;
            _colorAdjustments.active = needed;
            _colorAdjustments.saturation.Override(settings.saturation);
            _colorAdjustments.contrast.Override(settings.contrast);
        }

        private static void ApplyVignette(AvatarGraphicsSettings settings)
        {
            if (_vignette == null)
                return;

            _vignette.active = settings.vignette > 0.001f;
            _vignette.intensity.Override(settings.vignette);
            _vignette.smoothness.Override(0.5f);
            _vignette.rounded.Override(true);
        }
    }
}
