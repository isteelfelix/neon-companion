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

        /// <summary>
        /// True when post-processing can run without flattening the avatar's transparent
        /// background.
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

            if (_bloom != null)
            {
                _bloom.active = settings.bloom > 0.001f;
                _bloom.intensity.Override(settings.bloom);
                // Measured against the built-in VRM at the default key light: the render
                // never exceeds ~0.63 luminance, so URP's default threshold of 0.9 would
                // make the bloom slider do nothing at all. At 0.45 only the genuine
                // highlights — eye catchlights and hair specular, well under 1% of the
                // avatar — pick it up.
                _bloom.threshold.Override(0.45f);
                _bloom.scatter.Override(0.7f);
            }

            // Neutral rather than a user choice: it maps the HDR range onto the display
            // without shifting hue, which is what a character portrait wants. ACES would
            // push the palette around for no benefit here.
            if (_tonemapping != null)
            {
                _tonemapping.active = true;
                _tonemapping.mode.Override(TonemappingMode.Neutral);
            }
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

            _volume = _host.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 1f;
            _volume.weight = 1f;
            _volume.profile = _profile;
        }
    }
}
