using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NeonCompanion.Editor
{
    /// <summary>
    /// The project's URP asset came from the 2D template, so its only renderer is a
    /// <c>Renderer2D</c>. The 3D avatar camera needs a <see cref="UniversalRendererData"/>
    /// to get shadows, post-processing and post-AA, so this creates one next to the URP
    /// asset and appends it to the renderer list. The runtime then picks it by type — see
    /// <c>GraphicsQualityService.AvatarRendererIndex</c> — so the index it lands on does
    /// not matter, and the 2D renderer stays the default for every other camera.
    ///
    /// Idempotent: it does nothing once a UniversalRendererData is already registered.
    /// </summary>
    public static class AvatarRenderPipelineSetup
    {
        private const string RendererAssetPath = "Assets/Settings/AvatarRenderer3D.asset";
        private const string UrpPackagePath = "Packages/com.unity.render-pipelines.universal";
        private const string RendererTemplatePath =
            UrpPackagePath + "/Runtime/Data/UniversalRendererData.asset";
        private const string PostProcessDataPath =
            UrpPackagePath + "/Runtime/Data/PostProcessData.asset";

        [MenuItem("Neon/Graphics/Repair avatar 3D renderer")]
        public static void RepairFromMenu()
        {
            int changed = EnsureRenderer(true);
            if (changed == 0)
                Debug.Log("[Neon] Avatar 3D renderer is already registered — nothing to do.");
        }

        [InitializeOnLoadMethod]
        private static void EnsureOnLoad()
        {
            // Deferred: asset creation during a domain reload races the import pipeline.
            EditorApplication.delayCall += EnsureOnLoadDeferred;
        }

        private static void EnsureOnLoadDeferred()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;
            EnsureRenderer(false);
        }

        /// <summary>
        /// Makes sure every URP asset in the project carries a UniversalRendererData.
        /// Returns how many assets were modified.
        /// </summary>
        public static int EnsureRenderer(bool verbose)
        {
            List<UniversalRenderPipelineAsset> pipelines = FindPipelineAssets();
            if (pipelines.Count == 0)
            {
                if (verbose)
                    Debug.LogWarning("[Neon] No UniversalRenderPipelineAsset found in the project.");
                return 0;
            }

            UniversalRendererData rendererData = null;
            int changed = 0;

            for (int i = 0; i < pipelines.Count; i++)
            {
                UniversalRenderPipelineAsset pipeline = pipelines[i];
                if (HasUniversalRenderer(pipeline))
                    continue;

                if (rendererData == null)
                {
                    rendererData = LoadOrCreateRendererData();
                    if (rendererData == null)
                        return changed;
                }

                if (AppendRenderer(pipeline, rendererData))
                {
                    changed++;
                    Debug.Log(
                        "[Neon] Registered " + RendererAssetPath + " on " +
                        AssetDatabase.GetAssetPath(pipeline) +
                        " — the avatar camera can now use shadows and post-processing.");
                }
            }

            if (changed > 0)
                AssetDatabase.SaveAssets();

            return changed;
        }

        private static List<UniversalRenderPipelineAsset> FindPipelineAssets()
        {
            var result = new List<UniversalRenderPipelineAsset>();
            string[] guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (asset != null)
                    result.Add(asset);
            }
            return result;
        }

        private static bool HasUniversalRenderer(UniversalRenderPipelineAsset pipeline)
        {
            System.ReadOnlySpan<ScriptableRendererData> list = pipeline.rendererDataList;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is UniversalRendererData)
                    return true;
            }
            return false;
        }

        private static UniversalRendererData LoadOrCreateRendererData()
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            if (existing != null)
                return existing;

            // Copy URP's own default renderer data instead of instantiating a blank one:
            // the template already carries every shader reference the renderer needs, and
            // URP 17 no longer exposes ResourceReloader to fill them in afterwards.
            UniversalRendererData data = null;
            if (AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererTemplatePath) != null &&
                AssetDatabase.CopyAsset(RendererTemplatePath, RendererAssetPath))
            {
                data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            }

            if (data == null)
            {
                // Fallback: a blank instance still renders, it just may miss optional
                // shader resources that the package template would have supplied.
                data = ScriptableObject.CreateInstance<UniversalRendererData>();
                if (data == null)
                {
                    Debug.LogError("[Neon] Could not create a UniversalRendererData asset.");
                    return null;
                }
                AssetDatabase.CreateAsset(data, RendererAssetPath);
                Debug.LogWarning(
                    "[Neon] Could not copy the URP renderer template from " +
                    RendererTemplatePath + "; created a blank renderer instead.");
            }

            if (data.postProcessData == null)
            {
                data.postProcessData =
                    AssetDatabase.LoadAssetAtPath<PostProcessData>(PostProcessDataPath);
                if (data.postProcessData == null)
                    Debug.LogWarning(
                        "[Neon] PostProcessData not found at " + PostProcessDataPath +
                        " — post-processing on the avatar camera may be skipped.");
            }

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return data;
        }

        private static bool AppendRenderer(
            UniversalRenderPipelineAsset pipeline,
            UniversalRendererData rendererData)
        {
            var serialized = new SerializedObject(pipeline);
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            if (list == null || !list.isArray)
            {
                Debug.LogError(
                    "[Neon] m_RendererDataList not found on " +
                    AssetDatabase.GetAssetPath(pipeline) +
                    " — this URP version stores its renderers differently.");
                return false;
            }

            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            list.GetArrayElementAtIndex(index).objectReferenceValue = rendererData;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(pipeline);
            return true;
        }
    }
}
