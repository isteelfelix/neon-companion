#if UNITY_EDITOR
using UnityEngine.PathTracing.Core;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace NeonCompanion.Editor.Rendering
{
    /// <summary>
    /// neon-companion does not use path tracing or unified ray tracing (2D UI + URP forward).
    /// Unity's built-in strippers for these settings are compiled only with SURFACE_CACHE, so we strip them on every player build.
    /// </summary>
    sealed class NeonPathTracingResourcesStripper : IRenderPipelineGraphicsSettingsStripper<WorldRenderPipelineResources>
    {
        public bool active => true;

        public bool CanRemoveSettings(WorldRenderPipelineResources settings) => true;
    }

    sealed class NeonRayTracingResourcesStripper : IRenderPipelineGraphicsSettingsStripper<RayTracingRenderPipelineResources>
    {
        public bool active => true;

        public bool CanRemoveSettings(RayTracingRenderPipelineResources settings) => true;
    }
}
#endif