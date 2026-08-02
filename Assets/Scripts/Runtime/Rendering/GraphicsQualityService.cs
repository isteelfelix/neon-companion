using System;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NeonCompanion.Runtime.Rendering
{
    /// <summary>
    /// Single point that turns <see cref="AvatarGraphicsSettings"/> into engine state.
    /// Both processes (the main window and the pet window) call <see cref="Apply"/> with
    /// the same settings block, so quality stays identical in both places.
    ///
    /// Everything that can be driven per-camera or per-light is left to
    /// <c>Avatar3DRenderer</c>, which subscribes to <see cref="Changed"/>. This class only
    /// owns the genuinely global state: frame pacing, texture streaming and the handful of
    /// URP asset fields that have no per-camera equivalent.
    /// </summary>
    public static class GraphicsQualityService
    {
        private static AvatarGraphicsSettings _current = new AvatarGraphicsSettings();
        private static bool _applied;

        // -2 = never probed, -1 = no UniversalRenderer registered on the URP asset.
        private static int _avatarRendererIndex = -2;
        private static bool _warnedMissingRenderer;

        /// <summary>The settings currently in force. Never null.</summary>
        public static AvatarGraphicsSettings Current
        {
            get { return _current; }
        }

        /// <summary>True once <see cref="Apply"/> has run at least once.</summary>
        public static bool HasApplied
        {
            get { return _applied; }
        }

        /// <summary>Raised after every <see cref="Apply"/>, so renderers can re-read the settings.</summary>
        public static event Action<AvatarGraphicsSettings> Changed;

        public static void Apply(AvatarGraphicsSettings settings)
        {
            if (settings == null)
                return;

            settings.Normalize();
            _current = settings;

            try
            {
                ApplyFramePacing(settings);
                ApplyTextureQuality(settings);
                ApplyPipelineAsset(settings);
                AvatarPostFxVolume.Apply(settings);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("[Graphics] Failed to apply quality settings: " + ex);
            }

            _applied = true;

            Action<AvatarGraphicsSettings> handler = Changed;
            if (handler != null)
                handler(settings);
        }

        // ============================================================
        // Frame pacing
        // ============================================================

        private static void ApplyFramePacing(AvatarGraphicsSettings settings)
        {
            if (settings.vSync)
            {
                // Unity ignores targetFrameRate whenever vSyncCount > 0, so don't pretend
                // the cap is active — the UI hides the slider in this state.
                QualitySettings.vSyncCount = 1;
                Application.targetFrameRate = -1;
                return;
            }

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate =
                settings.targetFrameRate > 0 ? settings.targetFrameRate : -1;
        }

        // ============================================================
        // Textures
        // ============================================================

        private static void ApplyTextureQuality(AvatarGraphicsSettings settings)
        {
            QualitySettings.globalTextureMipmapLimit = settings.textureQuality;
            QualitySettings.anisotropicFiltering = settings.anisotropicFiltering
                ? AnisotropicFiltering.ForceEnable
                : AnisotropicFiltering.Disable;
        }

        // ============================================================
        // URP asset
        // ============================================================

        /// <summary>
        /// The few knobs URP only exposes on the pipeline asset. Writes are guarded by an
        /// equality check because in the Editor an assignment dirties the asset on disk.
        ///
        /// MSAA has to live here: for a camera that renders into a RenderTexture, URP
        /// overwrites the target descriptor's sample count with the asset's value, so
        /// setting <c>RenderTexture.antiAliasing</c> alone would be ignored.
        /// </summary>
        private static void ApplyPipelineAsset(AvatarGraphicsSettings settings)
        {
            UniversalRenderPipelineAsset urp = UniversalRenderPipeline.asset;
            if (urp == null)
                return;

            int msaa = settings.MsaaSamples;
            if (urp.msaaSampleCount != msaa)
                urp.msaaSampleCount = msaa;

            if (urp.supportsHDR != settings.hdr)
                urp.supportsHDR = settings.hdr;

            // supportsMainLightShadows has an internal setter, so shadows are switched off
            // by collapsing the shadow distance instead — URP skips the shadow passes when
            // it reaches zero. Per-light LightShadows is handled by Avatar3DRenderer.
            float shadowDistance = settings.ShadowsEnabled ? 12f : 0f;
            if (!Mathf.Approximately(urp.shadowDistance, shadowDistance))
                urp.shadowDistance = shadowDistance;

            if (settings.ShadowsEnabled && urp.mainLightShadowmapResolution != settings.shadowResolution)
                urp.mainLightShadowmapResolution = settings.shadowResolution;
        }

        // ============================================================
        // Renderer lookup
        // ============================================================

        /// <summary>
        /// Index of the UniversalRenderer inside the URP asset's renderer list, or -1 when
        /// the project still only has the 2D renderer. Looked up by type rather than by a
        /// hard-coded index, so it survives someone reordering the list.
        /// </summary>
        public static int AvatarRendererIndex
        {
            get
            {
                if (_avatarRendererIndex != -2)
                    return _avatarRendererIndex;

                _avatarRendererIndex = ProbeAvatarRendererIndex();
                if (_avatarRendererIndex < 0 && !_warnedMissingRenderer)
                {
                    _warnedMissingRenderer = true;
                    NeonLogger.LogWarning(
                        "[Graphics] No UniversalRenderer registered on the URP asset. The " +
                        "avatar falls back to the 2D renderer: shadows and post-processing " +
                        "will be skipped. Run Neon > Graphics > Repair avatar 3D renderer.");
                }
                return _avatarRendererIndex;
            }
        }

        private static int ProbeAvatarRendererIndex()
        {
            UniversalRenderPipelineAsset urp = UniversalRenderPipeline.asset;
            if (urp == null)
                return -1;

            ReadOnlySpan<ScriptableRendererData> list = urp.rendererDataList;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is UniversalRendererData)
                    return i;
            }
            return -1;
        }

        /// <summary>Forgets the cached renderer index. Called after the Editor repairs the URP asset.</summary>
        public static void InvalidateRendererIndex()
        {
            _avatarRendererIndex = -2;
            _warnedMissingRenderer = false;
        }
    }
}
