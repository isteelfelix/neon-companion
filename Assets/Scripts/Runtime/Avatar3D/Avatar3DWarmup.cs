using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Imports the gallery's VRM models ahead of time: it parks them in
    /// <see cref="Avatar3DModelCache"/> so switching avatars is a swap rather
    /// than a multi-second import, and bakes the gallery tile still for any
    /// avatar that has none yet.
    ///
    /// Warm-up always yields to the user: it waits while a foreground load is in
    /// flight, imports one model at a time, and leaves a free cache slot for the
    /// avatar that is actually mounted. On a device whose cache holds a single
    /// model it does nothing at all.
    /// </summary>
    internal static class Avatar3DWarmup
    {
        private readonly struct WarmRequest
        {
            public WarmRequest(string avatarId, string modelPath)
            {
                AvatarId = avatarId;
                ModelPath = modelPath;
            }

            public string AvatarId { get; }
            public string ModelPath { get; }
        }

        private static readonly List<WarmRequest> Pending = new List<WarmRequest>();
        private static bool _running;

        /// <summary>Raised on the main thread once a tile still has been baked.</summary>
        internal static event Action<string> ThumbnailBaked;

        internal static void Warm(
            IEnumerable<AvatarProfile> profiles,
            string activeModelPath)
        {
            if (profiles == null || !Application.isPlaying)
                return;

            string activeKey = Avatar3DModelCache.NormalizeKey(activeModelPath);
            foreach (AvatarProfile profile in profiles)
            {
                if (profile == null ||
                    profile.avatarType != AvatarProfileTypes.Vrm ||
                    string.IsNullOrWhiteSpace(profile.modelPath) ||
                    string.IsNullOrWhiteSpace(profile.id))
                    continue;

                // The mounted avatar is loaded by the gallery anyway, and it gets
                // parked on the way out, so warming it would only import it twice.
                string key = Avatar3DModelCache.NormalizeKey(profile.modelPath);
                if (string.Equals(key, activeKey, StringComparison.Ordinal))
                    continue;
                if (IsPending(profile.id))
                    continue;

                // Nothing left to do for a model that is already warm and whose
                // tile still is on disk.
                if (Avatar3DModelCache.Contains(profile.modelPath) &&
                    AvatarThumbnailBaker.Exists(profile.id, profile.modelPath))
                    continue;

                Pending.Add(new WarmRequest(profile.id, profile.modelPath));
            }

            if (Pending.Count > 0 && !_running)
                _ = RunAsync();
        }

        private static bool IsPending(string avatarId)
        {
            for (int i = 0; i < Pending.Count; i++)
            {
                if (string.Equals(Pending[i].AvatarId, avatarId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static async Task RunAsync()
        {
            _running = true;
            try
            {
                // The gallery is built before the active avatar starts loading, so
                // give that load a moment to claim the import lock first — warming
                // is never worth delaying the avatar the user is looking at.
                await Task.Delay(2000);

                while (Pending.Count > 0)
                {
                    WarmRequest request = Pending[0];
                    Pending.RemoveAt(0);

                    bool needsStill = !AvatarThumbnailBaker.Exists(
                        request.AvatarId, request.ModelPath);
                    bool alreadyWarm = Avatar3DModelCache.Contains(request.ModelPath);

                    // One cache slot is reserved for the live avatar: filling the
                    // cache completely would just evict it the moment it is parked.
                    // A missing tile still is still worth an import — it is baked
                    // and the model goes back out of the cache right after.
                    bool roomToPark =
                        Avatar3DModelCache.Count < Avatar3DModelCache.Capacity - 1;
                    if (alreadyWarm && !needsStill)
                        continue;
                    if (!needsStill && !roomToPark)
                        continue;

                    // A click that is waiting on an import always goes first.
                    while (Avatar3DModelCache.PendingForegroundLoads > 0)
                        await Task.Delay(150);

                    await ProcessAsync(request, needsStill, roomToPark);
                }
            }
            finally
            {
                _running = false;
            }
        }

        private static async Task ProcessAsync(
            WarmRequest request,
            bool needsStill,
            bool roomToPark)
        {
            // An already-parked model only has to be photographed: it is baked in
            // place, so it never leaves the cache.
            Avatar3DParkedModel warm = Avatar3DModelCache.Peek(request.ModelPath);
            if (warm != null)
            {
                if (needsStill)
                    BakeStill(request.AvatarId, warm.Instance);
                return;
            }

            await Avatar3DModelCache.ImportLock.WaitAsync();
            try
            {
                Avatar3DLoadResult result = await Avatar3DLoader.LoadAsync(request.ModelPath);
                if (result == null || !result.Success || result.Instance == null)
                    return;

                if (needsStill)
                    BakeStill(request.AvatarId, result.Instance);

                // glTF is not poolable (the loader keeps its own template cache),
                // and a model imported only for its still is released again.
                if (result.VrmInstance == null || !roomToPark)
                {
                    UnityEngine.Object.Destroy(result.Instance);
                    return;
                }

                Avatar3DModelCache.ParkLoadResult(request.ModelPath, result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[NeonCompanion] Avatar warm-up failed for '" +
                    request.ModelPath + "': " + ex.Message);
            }
            finally
            {
                Avatar3DModelCache.ImportLock.Release();
            }
        }

        private static void BakeStill(string avatarId, GameObject instance)
        {
            if (instance == null)
                return;
            if (AvatarThumbnailBaker.Bake(avatarId, instance) == null)
                return;

            Action<string> handler = ThumbnailBaked;
            if (handler != null)
                handler(avatarId);
        }
    }
}
