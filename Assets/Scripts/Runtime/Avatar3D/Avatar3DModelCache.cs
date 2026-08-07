using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NeonCompanion.Runtime.Data.Models;
using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// A model that was imported once and is being kept alive off-screen so the
    /// next switch back to it costs nothing. Importing a VRM is seconds of main
    /// thread work, which is why clicking through the gallery used to leave the
    /// preview blank: every tile paid the full import again.
    /// </summary>
    internal sealed class Avatar3DParkedModel
    {
        public string Key;
        public GameObject Instance;
        public Vrm10Instance VrmInstance;
        public AvatarCapabilities Capabilities = new AvatarCapabilities();
        public readonly List<string> AnimationNames = new List<string>();

        /// <summary>
        /// The instance already carries an initialized driver, so re-mounting it
        /// skips gaze/touch/rest-pose setup as well as the import.
        /// </summary>
        public bool DriverReady;
    }

    /// <summary>
    /// Keeps a small pool of already-imported VRM models parked off-screen, and
    /// serializes every import so a burst of gallery clicks cannot run several
    /// multi-second imports against each other on the main thread.
    ///
    /// Only VRM models are pooled: glTF/glb already gets a template cache inside
    /// <see cref="Avatar3DLoader"/>, and re-instantiating a template is cheap.
    /// </summary>
    internal static class Avatar3DModelCache
    {
        private static readonly List<Avatar3DParkedModel> Parked =
            new List<Avatar3DParkedModel>();
        private static readonly SemaphoreSlim ImportGate = new SemaphoreSlim(1, 1);
        private static Transform _holder;
        private static int _capacity;
        private static int _pendingForegroundLoads;
        private static bool _lowMemoryHooked;

        /// <summary>
        /// Held for the duration of an import. Every caller that imports a model
        /// takes it, so imports queue instead of overlapping.
        /// </summary>
        public static SemaphoreSlim ImportLock
        {
            get { return ImportGate; }
        }

        /// <summary>
        /// How many imports a user action is currently waiting on. Background
        /// warm-up yields while this is non-zero so it never delays a click.
        /// </summary>
        public static int PendingForegroundLoads
        {
            get { return Volatile.Read(ref _pendingForegroundLoads); }
        }

        public static void BeginForegroundLoad()
        {
            Interlocked.Increment(ref _pendingForegroundLoads);
        }

        public static void EndForegroundLoad()
        {
            Interlocked.Decrement(ref _pendingForegroundLoads);
        }

        /// <summary>
        /// How many models may stay parked on top of the live one. Sized off
        /// device memory: a parked VRM keeps its meshes and textures resident.
        /// </summary>
        public static int Capacity
        {
            get
            {
                if (_capacity > 0)
                    return _capacity;

                int memoryMb = SystemInfo.systemMemorySize;
                if (Application.isMobilePlatform)
                    _capacity = memoryMb >= 6000 ? 2 : 1;
                else
                    _capacity = memoryMb >= 12000 ? 3 : 2;
                return _capacity;
            }
        }

        public static int Count
        {
            get
            {
                PruneDestroyed();
                return Parked.Count;
            }
        }

        /// <summary>
        /// Parking only happens in play mode: outside it there is no frame loop to
        /// benefit from a warm model, and a surviving hidden GameObject would leak
        /// into the editor scene between tests.
        /// </summary>
        private static bool CanPark
        {
            get { return Application.isPlaying; }
        }

        public static string NormalizeKey(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return string.Empty;

            if (BuiltInAvatarProfiles.IsResourcePath(modelPath))
                return modelPath.Trim();

            try
            {
                return Path.GetFullPath(modelPath).ToLowerInvariant();
            }
            catch (Exception)
            {
                return modelPath.Trim().ToLowerInvariant();
            }
        }

        public static bool Contains(string modelPath)
        {
            return IndexOf(NormalizeKey(modelPath)) >= 0;
        }

        /// <summary>
        /// Reads a parked model without taking ownership of it. Used to answer
        /// "what can this avatar do" without paying for an import.
        /// </summary>
        public static Avatar3DParkedModel Peek(string modelPath)
        {
            int index = IndexOf(NormalizeKey(modelPath));
            return index >= 0 ? Parked[index] : null;
        }

        /// <summary>
        /// Hands the parked instance to the caller, who now owns it. It comes back
        /// active and unparented, ready to be mounted into the render scene.
        /// </summary>
        public static Avatar3DParkedModel Take(string modelPath)
        {
            int index = IndexOf(NormalizeKey(modelPath));
            if (index < 0)
                return null;

            Avatar3DParkedModel model = Parked[index];
            Parked.RemoveAt(index);

            if (model.Instance == null)
                return null;

            // Back to the origin in its own right: a host that does not reparent
            // the model (the pet window) would otherwise inherit wherever the
            // cache happened to keep it. Cancel first — a Park() that lost the
            // race against a deactivating ancestor (see Reparent) may still have
            // a retry queued against this same transform.
            Transform instanceTransform = model.Instance.transform;
            Avatar3DModelCachePump.CancelReparent(instanceTransform);
            Reparent(instanceTransform, null);
            instanceTransform.localPosition = Vector3.zero;
            instanceTransform.localRotation = Quaternion.identity;
            model.Instance.SetActive(true);
            return model;
        }

        public static void Park(Avatar3DParkedModel model)
        {
            if (model == null || model.Instance == null)
                return;

            // A glTF instance is not worth the memory (the loader keeps a template
            // for it), and outside play mode nothing survives anyway.
            if (!CanPark || model.VrmInstance == null || string.IsNullOrEmpty(model.Key))
            {
                Destroy(model.Instance);
                model.Instance = null;
                return;
            }

            HookLowMemory();
            PruneDestroyed();

            int existing = IndexOf(model.Key);
            if (existing >= 0)
            {
                Avatar3DParkedModel duplicate = Parked[existing];
                Parked.RemoveAt(existing);
                if (duplicate != model && duplicate.Instance != null)
                    Destroy(duplicate.Instance);
            }

            model.Instance.SetActive(false);
            Reparent(model.Instance.transform, EnsureHolder());
            Parked.Add(model);

            while (Parked.Count > Capacity)
            {
                Avatar3DParkedModel evicted = Parked[0];
                Parked.RemoveAt(0);
                if (evicted.Instance != null)
                    Destroy(evicted.Instance);
            }
        }

        public static Avatar3DParkedModel FromLoadResult(
            string modelPath,
            Avatar3DLoadResult result)
        {
            if (result == null || result.Instance == null)
                return null;

            var model = new Avatar3DParkedModel();
            model.Key = NormalizeKey(modelPath);
            model.Instance = result.Instance;
            model.VrmInstance = result.VrmInstance;
            model.Capabilities = result.Capabilities ?? new AvatarCapabilities();
            model.AnimationNames.AddRange(result.AnimationNames);
            return model;
        }

        /// <summary>
        /// Parks a freshly imported model the caller does not intend to mount —
        /// the capability probe run on selection, or a background warm-up.
        /// </summary>
        public static void ParkLoadResult(string modelPath, Avatar3DLoadResult result)
        {
            Park(FromLoadResult(modelPath, result));
        }

        public static void Clear()
        {
            for (int i = 0; i < Parked.Count; i++)
            {
                if (Parked[i].Instance != null)
                    Destroy(Parked[i].Instance);
            }
            Parked.Clear();
        }

        private static int IndexOf(string key)
        {
            if (string.IsNullOrEmpty(key))
                return -1;

            PruneDestroyed();
            for (int i = 0; i < Parked.Count; i++)
            {
                if (string.Equals(Parked[i].Key, key, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static void PruneDestroyed()
        {
            for (int i = Parked.Count - 1; i >= 0; i--)
            {
                if (Parked[i] == null || Parked[i].Instance == null)
                    Parked.RemoveAt(i);
            }
        }

        // Unity refuses to reparent a GameObject while any of its ancestors is
        // mid SetActive — e.g. parking a model from OnDisable while the whole
        // Avatars page is being torn down, which is exactly when Park() runs.
        // SetParent does not throw for this: it logs an error and silently
        // leaves the transform where it was. Left unchecked, the model would be
        // recorded as parked while it is still hanging off a hierarchy that may
        // be destroyed once the deactivation finishes — so success is verified
        // and a failed attempt is handed off to the pump instead, which is a
        // separate persistent object unaffected by the cascade that blocked it.
        private static void Reparent(Transform target, Transform parent)
        {
            target.SetParent(parent, false);
            if (target.parent != parent)
                Avatar3DModelCachePump.RetryReparent(target, parent);
        }

        // The holder itself is inactive, which is what keeps every parked model out
        // of the render cameras regardless of its own active state.
        private static Transform EnsureHolder()
        {
            if (_holder != null)
                return _holder;

            var holderObject = new GameObject("[Avatar3DModelCache]");
            holderObject.hideFlags = HideFlags.HideAndDontSave;
            holderObject.SetActive(false);
            if (Application.isPlaying)
                UnityEngine.Object.DontDestroyOnLoad(holderObject);
            _holder = holderObject.transform;
            return _holder;
        }

        private static void HookLowMemory()
        {
            if (_lowMemoryHooked)
                return;
            _lowMemoryHooked = true;
            Application.lowMemory += Clear;
        }

        private static void Destroy(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(value);
            else
                UnityEngine.Object.DestroyImmediate(value);
        }
    }

    /// <summary>
    /// Retries a reparent that Unity refused because an ancestor of the target
    /// was mid activation/deactivation. Lives on its own persistent GameObject
    /// outside every other hierarchy, so its Update keeps running through
    /// whatever cascade blocked the original SetParent call.
    /// </summary>
    internal sealed class Avatar3DModelCachePump : MonoBehaviour
    {
        private struct PendingReparent
        {
            public Transform Target;
            public Transform Parent;
        }

        private static Avatar3DModelCachePump _instance;
        private readonly List<PendingReparent> _pending = new List<PendingReparent>();

        internal static void RetryReparent(Transform target, Transform parent)
        {
            if (target == null)
                return;
            EnsureInstance()._pending.Add(
                new PendingReparent { Target = target, Parent = parent });
        }

        internal static void CancelReparent(Transform target)
        {
            if (_instance == null || target == null)
                return;
            List<PendingReparent> pending = _instance._pending;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].Target == target)
                    pending.RemoveAt(i);
            }
        }

        private static Avatar3DModelCachePump EnsureInstance()
        {
            if (_instance != null)
                return _instance;

            var pumpObject = new GameObject("[Avatar3DModelCachePump]");
            pumpObject.hideFlags = HideFlags.HideAndDontSave;
            if (Application.isPlaying)
                DontDestroyOnLoad(pumpObject);
            _instance = pumpObject.AddComponent<Avatar3DModelCachePump>();
            return _instance;
        }

        private void Update()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingReparent entry = _pending[i];
                if (entry.Target == null)
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                entry.Target.SetParent(entry.Parent, false);
                if (entry.Target.parent == entry.Parent)
                    _pending.RemoveAt(i);
            }
        }
    }
}
