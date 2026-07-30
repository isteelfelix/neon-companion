using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using NeonCompanion.Runtime.Data.Models;
using UniGLTF;
using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    public sealed class Avatar3DLoadResult
    {
        public bool Success;
        public string Error;
        public string ErrorCode;
        public string SourcePath;
        public GameObject Instance;
        public Vrm10Instance VrmInstance;
        public AvatarCapabilities Capabilities = new AvatarCapabilities();
        public long FileSizeBytes;
        public int SceneNodeCount;
        public int RendererCount;
        public long TriangleCount;
        public readonly List<string> AnimationNames = new List<string>();
    }

    public static class Avatar3DLoader
    {
        public const long MaxModelFileBytes = 100L * 1024L * 1024L;
        public const int MaxSceneNodes = 512;
        public const int MaxRenderers = 128;
        public const long MaxTriangles = 500000L;
        public const int MaxAnimationClips = 128;
        private const string BuiltInVrmAnimationPrefix = "Avatars/neon/Neon_";

        private static readonly object CacheLock = new object();
        private static string _cachedPath;
        private static CachedModel _cachedModel;

        public static async Task<Avatar3DLoadResult> LoadAsync(string modelPath)
        {
            var result = new Avatar3DLoadResult
            {
                SourcePath = modelPath
            };

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                result.ErrorCode = "empty_path";
                result.Error = "Model path is empty.";
                return result;
            }

            if (BuiltInAvatarProfiles.IsResourcePath(modelPath))
                return await LoadResourceVrmAsync(modelPath, result);

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(modelPath);
            }
            catch (Exception ex)
            {
                result.ErrorCode = "invalid_path";
                result.Error = "Model path is invalid: " + ex.Message;
                return result;
            }

            if (!File.Exists(fullPath))
            {
                result.ErrorCode = "file_missing";
                result.Error = $"Model file not found: {fullPath}";
                return result;
            }

            result.FileSizeBytes = new FileInfo(fullPath).Length;
            if (result.FileSizeBytes > MaxModelFileBytes)
            {
                result.ErrorCode = "file_too_large";
                result.Error = "Model exceeds the 100 MB file limit.";
                return result;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (ext != ".glb" && ext != ".gltf" && ext != ".vrm")
            {
                result.ErrorCode = "unsupported_format";
                result.Error = $"Unsupported model format: {ext}. Expected .glb, .gltf, or .vrm";
                return result;
            }

            if (ext == ".vrm")
                return await LoadVrmAsync(fullPath, result);

            CachedModel cached;
            lock (CacheLock)
            {
                cached = string.Equals(_cachedPath, fullPath, StringComparison.OrdinalIgnoreCase)
                    ? _cachedModel
                    : null;
            }

            if (cached != null && cached.Template != null)
            {
                var cachedInstance = UnityEngine.Object.Instantiate(cached.Template);
                cachedInstance.name = Path.GetFileNameWithoutExtension(fullPath);
                cachedInstance.SetActive(true);

                result.Instance = cachedInstance;
                result.AnimationNames.AddRange(cached.AnimationNames);
                CollectSceneFacts(cachedInstance, result);
                SetGenericCapabilities(result);
                result.Success = true;
                return result;
            }

            try
            {
                ImportedGltf imported = await TryLoadWithGltfFastAsync(fullPath);
                if (imported == null || imported.Root == null)
                {
                    result.ErrorCode = "import_failed";
                    result.Error = "Unable to load model. glTFast package is not available or import failed.";
                    return result;
                }
                GameObject importedRoot = imported.Root;

                importedRoot.name = Path.GetFileNameWithoutExtension(fullPath);
                importedRoot.SetActive(false);

                var animationNames = CollectAnimationNames(importedRoot);
                CollectSceneFacts(importedRoot, result);
                if (result.RendererCount == 0)
                {
                    result.ErrorCode = "empty_scene";
                    result.Error = "Model scene contains no renderers.";
                    DestroyLoadedObject(importedRoot);
                    imported.Importer.Dispose();
                    return result;
                }
                if (result.SceneNodeCount > MaxSceneNodes ||
                    result.RendererCount > MaxRenderers ||
                    result.TriangleCount > MaxTriangles ||
                    animationNames.Count > MaxAnimationClips)
                {
                    result.ErrorCode = "scene_limit_exceeded";
                    result.Error = "Model scene exceeds limits (512 nodes, 128 renderers, 500,000 triangles, 128 animation clips). " +
                        "Detected " + result.SceneNodeCount + " nodes, " + result.RendererCount +
                        " renderers, " + result.TriangleCount + " triangles, " +
                        animationNames.Count + " animation clips.";
                    DestroyLoadedObject(importedRoot);
                    imported.Importer.Dispose();
                    return result;
                }

                var template = UnityEngine.Object.Instantiate(importedRoot);
                template.name = importedRoot.name + "_Template";
                template.SetActive(false);
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(template);

                CachedModel evicted;
                lock (CacheLock)
                {
                    evicted = _cachedModel;
                    _cachedPath = fullPath;
                    _cachedModel = new CachedModel(
                        template,
                        animationNames,
                        imported.Importer);
                }
                if (evicted != null && evicted.Template != null &&
                    evicted.Template != template)
                {
                    DestroyLoadedObject(evicted.Template);
                    evicted.Importer?.Dispose();
                }

                var liveInstance = UnityEngine.Object.Instantiate(template);
                liveInstance.name = importedRoot.name;
                liveInstance.SetActive(true);

                DestroyLoadedObject(importedRoot);

                result.Instance = liveInstance;
                result.AnimationNames.AddRange(animationNames);
                SetGenericCapabilities(result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorCode = "exception";
                result.Error = ex.Message;
                Debug.LogWarning($"[NeonCompanion] 3D avatar load failed: {ex}");
            }

            return result;
        }

        private static async Task<Avatar3DLoadResult> LoadVrmAsync(
            string fullPath,
            Avatar3DLoadResult result)
        {
            try
            {
                // UniVRM is deliberately called only for the .vrm extension. A GLB that happens
                // to contain VRM metadata is not silently promoted to a VRM avatar.
                Vrm10Instance vrm = await Vrm10.LoadPathAsync(
                    fullPath,
                    true,
                    ControlRigGenerationOption.Generate,
                    true,
                    null,
                    null,
                    MaterialDescriptorGeneratorUtility
                        .GetValidGltfMaterialDescriptorGenerator());
                if (vrm == null)
                {
                    result.ErrorCode = "invalid_vrm";
                    result.Error = "UniVRM could not import this VRM file.";
                    return result;
                }

                GameObject root = vrm.gameObject;
                root.name = Path.GetFileNameWithoutExtension(fullPath);
                CollectSceneFacts(root, result);
                result.AnimationNames.AddRange(CollectAnimationNames(root));
                if (!ValidateSceneLimits(root, result, result.AnimationNames.Count))
                    return result;

                result.Instance = root;
                result.VrmInstance = vrm;
                ExtractVrmCapabilities(vrm, result, false);
                result.Success = true;
            }
            catch (Exception ex)
            {
                if (result.Instance != null)
                {
                    UnityEngine.Object.Destroy(result.Instance);
                    result.Instance = null;
                    result.VrmInstance = null;
                }
                result.ErrorCode = "invalid_vrm";
                result.Error = ex.Message;
                Debug.LogWarning($"[NeonCompanion] VRM avatar load failed: {ex}");
            }

            return result;
        }

        private static async Task<Avatar3DLoadResult> LoadResourceVrmAsync(
            string resourceUri,
            Avatar3DLoadResult result)
        {
            string resourcePath = BuiltInAvatarProfiles.GetResourcePath(resourceUri);
            TextAsset source = Resources.Load<TextAsset>(resourcePath);
            if (source == null)
            {
                result.ErrorCode = "resource_missing";
                result.Error = "Built-in VRM resource is missing: " + resourcePath;
                return result;
            }

            GameObject instance = null;
            try
            {
                result.FileSizeBytes = source.bytes.LongLength;
                if (result.FileSizeBytes > MaxModelFileBytes)
                {
                    result.ErrorCode = "file_too_large";
                    result.Error = "Built-in VRM exceeds the 100 MB file limit.";
                    return result;
                }

                Vrm10Instance vrm = await Vrm10.LoadBytesAsync(
                    source.bytes,
                    true,
                    ControlRigGenerationOption.Generate,
                    true,
                    null,
                    null,
                    MaterialDescriptorGeneratorUtility
                        .GetValidGltfMaterialDescriptorGenerator());
                if (vrm == null)
                {
                    result.ErrorCode = "invalid_vrm";
                    result.Error = "UniVRM could not import the built-in VRM resource.";
                    return result;
                }

                instance = vrm.gameObject;
                instance.name = Path.GetFileName(resourcePath);
                CollectSceneFacts(instance, result);
                result.AnimationNames.AddRange(CollectAnimationNames(instance));
                if (!ValidateSceneLimits(instance, result, result.AnimationNames.Count))
                    return result;

                result.Instance = instance;
                result.VrmInstance = vrm;
                ExtractVrmCapabilities(vrm, result, true);
                result.Capabilities.evidence.Add("built_in_resource");
                result.Success = true;
            }
            catch (Exception ex)
            {
                if (instance != null)
                    DestroyLoadedObject(instance);
                result.Instance = null;
                result.VrmInstance = null;
                result.ErrorCode = "invalid_vrm";
                result.Error = ex.Message;
                Debug.LogWarning("[NeonCompanion] Built-in VRM load failed: " + ex);
            }
            return result;
        }

        public static async Task<Vrm10AnimationInstance> LoadBuiltInVrmAnimationAsync(
            string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName))
                return null;

            string resourcePath =
                BuiltInVrmAnimationPrefix + clipName.Trim().ToLowerInvariant() + ".vrma";
            TextAsset source = Resources.Load<TextAsset>(resourcePath);
            if (source == null)
            {
                Debug.LogWarning(
                    "[NeonCompanion] Built-in VRM animation is missing: " + resourcePath);
                return null;
            }

            GltfData data = null;
            RuntimeGltfInstance runtimeInstance = null;
            try
            {
                data = new GlbLowLevelParser(resourcePath, source.bytes).Parse();
                var animationData = new VrmAnimationData(data);
                using (var loader = new VrmAnimationImporter(animationData))
                {
                    IAwaitCaller awaitCaller = Application.isPlaying
                        ? (IAwaitCaller)new RuntimeOnlyAwaitCaller()
                        : new ImmediateCaller();
                    runtimeInstance = await loader.LoadAsync(awaitCaller);
                    if (runtimeInstance == null)
                        return null;
                    Vrm10AnimationInstance animation =
                        runtimeInstance.GetComponentInChildren<Vrm10AnimationInstance>(true);
                    if (animation != null)
                        return animation;
                    DestroyLoadedObject(runtimeInstance.gameObject);
                    runtimeInstance = null;
                    return null;
                }
            }
            catch (Exception ex)
            {
                if (runtimeInstance != null)
                    DestroyLoadedObject(runtimeInstance.gameObject);
                Debug.LogWarning(
                    "[NeonCompanion] Built-in VRM animation load failed for '" +
                    clipName + "': " + ex);
                return null;
            }
            finally
            {
                if (data != null)
                    data.Dispose();
            }
        }

        private static void DestroyLoadedObject(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(value);
            else
                UnityEngine.Object.DestroyImmediate(value);
        }

        private static bool ValidateSceneLimits(
            GameObject root,
            Avatar3DLoadResult result,
            int animationCount)
        {
            if (result.RendererCount == 0)
            {
                result.ErrorCode = "empty_scene";
                result.Error = "Model scene contains no renderers.";
                UnityEngine.Object.Destroy(root);
                return false;
            }

            if (result.SceneNodeCount <= MaxSceneNodes &&
                result.RendererCount <= MaxRenderers &&
                result.TriangleCount <= MaxTriangles &&
                animationCount <= MaxAnimationClips)
                return true;

            result.ErrorCode = "scene_limit_exceeded";
            result.Error = "Model scene exceeds limits (512 nodes, 128 renderers, 500,000 triangles, 128 animation clips). " +
                "Detected " + result.SceneNodeCount + " nodes, " + result.RendererCount +
                " renderers, " + result.TriangleCount + " triangles, " +
                animationCount + " animation clips.";
            UnityEngine.Object.Destroy(root);
            return false;
        }

        private static void SetGenericCapabilities(Avatar3DLoadResult result)
        {
            AvatarCapabilities capabilities = result.Capabilities;
            capabilities.isVerified = true;
            capabilities.canRender = true;
            capabilities.canAnimate = result.AnimationNames.Count > 0;
            capabilities.hasStateAnimations = capabilities.canAnimate;
            capabilities.isRuntimeSupported = true;
            capabilities.animationClipCount = result.AnimationNames.Count;
            capabilities.sceneNodeCount = result.SceneNodeCount;
            capabilities.rendererCount = result.RendererCount;
            capabilities.triangleCount = result.TriangleCount;
            capabilities.evidence.Add("gltfast_scene_import");
        }

        private static void ExtractVrmCapabilities(
            Vrm10Instance vrm,
            Avatar3DLoadResult result,
            bool includeBuiltInMotionPack)
        {
            AvatarCapabilities capabilities = result.Capabilities;
            capabilities.isVerified = true;
            capabilities.canRender = true;
            capabilities.isRuntimeSupported = true;
            capabilities.sceneNodeCount = result.SceneNodeCount;
            capabilities.rendererCount = result.RendererCount;
            capabilities.triangleCount = result.TriangleCount;
            capabilities.evidence.Add("univrm_0_131_2_runtime");

            HumanBodyBones[] requiredBones =
            {
                HumanBodyBones.Hips,
                HumanBodyBones.Spine,
                HumanBodyBones.Head,
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperLeg,
                HumanBodyBones.LeftLowerLeg,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg,
                HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot
            };
            capabilities.hasHumanoid = true;
            for (int i = 0; i < requiredBones.Length; i++)
            {
                Transform bone;
                if (!vrm.TryGetBoneTransform(requiredBones[i], out bone) || bone == null)
                {
                    capabilities.hasHumanoid = false;
                    break;
                }
            }

            VRM10ObjectExpression expressions = vrm.Vrm != null ? vrm.Vrm.Expression : null;
            int customCount = expressions != null && expressions.CustomClips != null
                ? expressions.CustomClips.Count
                : 0;
            int emotionalExpressionCount = expressions == null ? 0 :
                CountPresent(
                    expressions.Happy,
                    expressions.Angry,
                    expressions.Sad,
                    expressions.Relaxed,
                    expressions.Surprised) + customCount;
            int mouthCount = expressions == null ? 0 :
                CountPresent(
                    expressions.Aa,
                    expressions.Ih,
                    expressions.Ou,
                    expressions.Ee,
                    expressions.Oh);
            int gazeCount = expressions == null ? 0 :
                CountPresent(
                    expressions.LookUp,
                    expressions.LookDown,
                    expressions.LookLeft,
                    expressions.LookRight);
            int blinkCount = expressions == null ? 0 :
                CountPresent(expressions.Blink, expressions.BlinkLeft, expressions.BlinkRight);

            capabilities.expressionCount = emotionalExpressionCount + mouthCount +
                gazeCount + blinkCount + (expressions != null && expressions.Neutral != null ? 1 : 0);
            capabilities.hasExpressions = emotionalExpressionCount > 0;
            capabilities.hasBlink = expressions != null &&
                (expressions.Blink != null ||
                 (expressions.BlinkLeft != null && expressions.BlinkRight != null));
            capabilities.hasGaze = vrm.Vrm != null && vrm.Vrm.LookAt != null &&
                (gazeCount > 0 || HasEyeBones(vrm));
            capabilities.hasLipsync = mouthCount > 0;

            string[] states = { "idle", "thinking", "talking", "listening", "smile", "confused" };
            if (includeBuiltInMotionPack && capabilities.hasHumanoid)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    if (Resources.Load<TextAsset>(
                            BuiltInVrmAnimationPrefix + states[i] + ".vrma") != null &&
                        !result.AnimationNames.Contains(states[i]))
                        result.AnimationNames.Add(states[i]);
                }
            }
            capabilities.animationClipCount = result.AnimationNames.Count;
            capabilities.hasStateAnimations = result.AnimationNames.Count > 0;
            capabilities.canAnimate = capabilities.hasStateAnimations;
            capabilities.isRestricted = !capabilities.hasBlink ||
                !capabilities.hasGaze ||
                !capabilities.hasExpressions ||
                !capabilities.hasLipsync;
        }

        private static int CountPresent(params UnityEngine.Object[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != null)
                    count++;
            }
            return count;
        }

        private static bool HasEyeBones(Vrm10Instance vrm)
        {
            Transform leftEye;
            Transform rightEye;
            return vrm.TryGetBoneTransform(HumanBodyBones.LeftEye, out leftEye) &&
                leftEye != null &&
                vrm.TryGetBoneTransform(HumanBodyBones.RightEye, out rightEye) &&
                rightEye != null;
        }

        private static async Task<ImportedGltf> TryLoadWithGltfFastAsync(string fullPath)
        {
            var importer = new GltfImport(
                deferAgent: new UninterruptedDeferAgent());
            bool loaded = await importer.LoadFile(fullPath);
            if (!loaded)
            {
                importer.Dispose();
                return null;
            }

            var root = new GameObject("Avatar3DImportedRoot");
            bool instantiated = await importer.InstantiateMainSceneAsync(root.transform);
            if (!instantiated)
            {
                DestroyLoadedObject(root);
                importer.Dispose();
                return null;
            }
            return new ImportedGltf(root, importer);
        }

        private static List<string> CollectAnimationNames(GameObject root)
        {
            var names = new List<string>();
            if (root == null)
                return names;

            var animator = root.GetComponentInChildren<Animator>(true);
            var controller = animator != null ? animator.runtimeAnimatorController : null;
            var clips = controller != null ? controller.animationClips : null;

            if (clips != null)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    var clip = clips[i];
                    if (clip == null || string.IsNullOrWhiteSpace(clip.name))
                        continue;

                    if (!names.Contains(clip.name))
                        names.Add(clip.name);
                }
            }

            var legacyAnimation = root.GetComponentInChildren<Animation>(true);
            if (legacyAnimation != null)
            {
                foreach (AnimationState state in legacyAnimation)
                {
                    if (state == null || state.clip == null || string.IsNullOrWhiteSpace(state.clip.name))
                        continue;

                    if (!names.Contains(state.clip.name))
                        names.Add(state.clip.name);
                }
            }

            return names;
        }

        private static void CollectSceneFacts(GameObject root, Avatar3DLoadResult result)
        {
            if (root == null || result == null)
                return;

            result.SceneNodeCount = root.GetComponentsInChildren<Transform>(true).Length;
            result.RendererCount = root.GetComponentsInChildren<Renderer>(true).Length;

            long triangles = 0;
            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var mesh = meshFilters[i] != null ? meshFilters[i].sharedMesh : null;
                triangles += CountMeshTriangles(mesh);
            }

            var skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                var mesh = skinnedRenderers[i] != null ? skinnedRenderers[i].sharedMesh : null;
                triangles += CountMeshTriangles(mesh);
            }

            result.TriangleCount = triangles;
        }

        private static long CountMeshTriangles(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            long triangleCount = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) == MeshTopology.Triangles)
                    triangleCount += (long)mesh.GetIndexCount(i) / 3L;
            }

            return triangleCount;
        }

        private sealed class CachedModel
        {
            public CachedModel(
                GameObject template,
                List<string> animationNames,
                IDisposable importer)
            {
                Template = template;
                AnimationNames = animationNames ?? new List<string>();
                Importer = importer;
            }

            public GameObject Template { get; }
            public List<string> AnimationNames { get; }
            public IDisposable Importer { get; }
        }

        private sealed class ImportedGltf
        {
            public ImportedGltf(GameObject root, IDisposable importer)
            {
                Root = root;
                Importer = importer;
            }

            public GameObject Root { get; }
            public IDisposable Importer { get; }
        }
    }
}
