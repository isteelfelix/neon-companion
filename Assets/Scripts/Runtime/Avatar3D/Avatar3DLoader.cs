using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    public sealed class Avatar3DLoadResult
    {
        public bool Success;
        public string Error;
        public string SourcePath;
        public GameObject Instance;
        public readonly List<string> AnimationNames = new List<string>();
    }

    public static class Avatar3DLoader
    {
        private static readonly Dictionary<string, CachedModel> Cache = new Dictionary<string, CachedModel>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static async Task<Avatar3DLoadResult> LoadAsync(string modelPath)
        {
            var result = new Avatar3DLoadResult
            {
                SourcePath = modelPath
            };

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                result.Error = "Model path is empty.";
                return result;
            }

            string fullPath = Path.GetFullPath(modelPath);
            if (!File.Exists(fullPath))
            {
                result.Error = $"Model file not found: {fullPath}";
                return result;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (ext != ".glb" && ext != ".gltf")
            {
                result.Error = $"Unsupported model format: {ext}. Expected .glb or .gltf";
                return result;
            }

            CachedModel cached;
            lock (CacheLock)
            {
                Cache.TryGetValue(fullPath, out cached);
            }

            if (cached != null && cached.Template != null)
            {
                var cachedInstance = UnityEngine.Object.Instantiate(cached.Template);
                cachedInstance.name = Path.GetFileNameWithoutExtension(fullPath);
                cachedInstance.SetActive(true);

                result.Instance = cachedInstance;
                result.AnimationNames.AddRange(cached.AnimationNames);
                result.Success = true;
                return result;
            }

            try
            {
                var importedRoot = await TryLoadWithGltfFastAsync(fullPath);
                if (importedRoot == null)
                {
                    result.Error = "Unable to load model. glTFast package is not available or import failed.";
                    return result;
                }

                importedRoot.name = Path.GetFileNameWithoutExtension(fullPath);
                importedRoot.SetActive(false);

                var animationNames = CollectAnimationNames(importedRoot);
                var template = UnityEngine.Object.Instantiate(importedRoot);
                template.name = importedRoot.name + "_Template";
                template.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(template);

                lock (CacheLock)
                {
                    Cache[fullPath] = new CachedModel(template, animationNames);
                }

                var liveInstance = UnityEngine.Object.Instantiate(template);
                liveInstance.name = importedRoot.name;
                liveInstance.SetActive(true);

                UnityEngine.Object.Destroy(importedRoot);

                result.Instance = liveInstance;
                result.AnimationNames.AddRange(animationNames);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Debug.LogWarning($"[NeonCompanion] 3D avatar load failed: {ex}");
            }

            return result;
        }

        private static async Task<GameObject> TryLoadWithGltfFastAsync(string fullPath)
        {
            var gltfImportType = Type.GetType("GLTFast.GltfImport, glTFast");
            if (gltfImportType == null)
                return null;

            object importer = Activator.CreateInstance(gltfImportType);
            if (importer == null)
                return null;

            var loadMethod = FindMethod(gltfImportType, "Load", typeof(string));
            if (loadMethod == null)
                return null;

            bool loaded = await InvokeLoadAsync(importer, loadMethod, fullPath);
            if (!loaded)
                return null;

            var root = new GameObject("Avatar3DImportedRoot");
            var instantiateMethod = FindInstantiateMethod(gltfImportType);
            if (instantiateMethod == null)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }

            object instantiateResult = instantiateMethod.Invoke(importer, new object[] { root.transform });
            if (instantiateResult is bool ok && !ok)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }

            return root;
        }

        private static MethodInfo FindMethod(Type type, string name, Type firstArg)
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                    continue;

                if (parameters[0].ParameterType == firstArg)
                    return method;
            }

            return null;
        }

        private static MethodInfo FindInstantiateMethod(Type gltfImportType)
        {
            foreach (var method in gltfImportType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, "InstantiateMainScene", StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length > 0 && parameters[0].ParameterType == typeof(Transform))
                    return method;
            }

            return null;
        }

        private static async Task<bool> InvokeLoadAsync(object importer, MethodInfo method, string fullPath)
        {
            object returnValue = method.Invoke(importer, new object[] { fullPath });
            if (returnValue is Task<bool> taskBool)
                return await taskBool;

            if (returnValue is Task task)
            {
                await task;
                return true;
            }

            if (returnValue is bool immediate)
                return immediate;

            return false;
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

        private sealed class CachedModel
        {
            public CachedModel(GameObject template, List<string> animationNames)
            {
                Template = template;
                AnimationNames = animationNames ?? new List<string>();
            }

            public GameObject Template { get; }
            public List<string> AnimationNames { get; }
        }
    }
}
