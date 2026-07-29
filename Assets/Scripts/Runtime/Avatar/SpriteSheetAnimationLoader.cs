using System;
using System.Collections.Generic;
using System.IO;
using NeonCompanion.Runtime.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Avatar
{
    public static class SpriteSheetAnimationLoader
    {
        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite[]> SpriteCache = new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

        // Built-in packs use "res://<ResourcesKey>" so they load from Resources (works
        // inside the APK on Android, unlike StreamingAssets+File).
        private const string ResourcesScheme = "res://";

        private static Texture2D LoadTextureFromResources(string path)
        {
            if (TextureCache.TryGetValue(path, out var cached))
            {
                if (cached != null)
                    return cached;
                TextureCache.Remove(path);
            }

            string key = path.Substring(ResourcesScheme.Length);
            Texture2D importedTexture = Resources.Load<Texture2D>(key);
            if (importedTexture != null)
            {
                TextureCache[path] = importedTexture;
                return importedTexture;
            }

            // Legacy fallback for packs that still store encoded image bytes as TextAsset.
            var asset = Resources.Load<TextAsset>(key);
            if (asset == null)
                return null;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            bool ok = texture.LoadImage(asset.bytes);
            Resources.UnloadAsset(asset);
            if (!ok)
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            TextureCache[path] = texture;
            return texture;
        }

        private static Texture2D LoadTextureResolved(string resolvedPath)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return null;

            if (TextureCache.TryGetValue(resolvedPath, out var cached))
            {
                // Guard against stale entries left over after an Editor Play-Mode reset
                if (cached != null)
                    return cached;
                TextureCache.Remove(resolvedPath);
            }

            if (!File.Exists(resolvedPath))
                return null;

            var bytes = File.ReadAllBytes(resolvedPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            TextureCache[resolvedPath] = texture;
            return texture;
        }

        public static Sprite[] LoadFrames(string path, int columns, int rows, int frameCount = 0)
        {
            if (string.IsNullOrWhiteSpace(path) || columns <= 0 || rows <= 0)
                return Array.Empty<Sprite>();

            bool isResources = path.StartsWith(ResourcesScheme, StringComparison.Ordinal);
            string resolvedPath = isResources ? path : ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return Array.Empty<Sprite>();

            string cacheKey = BuildSpriteCacheKey(resolvedPath, columns, rows, frameCount);
            if (SpriteCache.TryGetValue(cacheKey, out var cached))
            {
                // Guard against stale sprite entries after an Editor Play-Mode reset
                if (cached != null && cached.Length > 0 && cached[0] != null)
                    return cached;
                SpriteCache.Remove(cacheKey);
            }

            var texture = isResources ? LoadTextureFromResources(resolvedPath) : LoadTextureResolved(resolvedPath);
            if (texture == null)
                return Array.Empty<Sprite>();

            int frameWidth = texture.width / columns;
            int frameHeight = texture.height / rows;
            if (frameWidth <= 0 || frameHeight <= 0)
                return Array.Empty<Sprite>();

            int availableFrames = columns * rows;
            int framesToCreate = frameCount > 0 ? Mathf.Min(frameCount, availableFrames) : availableFrames;
            var sprites = new List<Sprite>(framesToCreate);
            for (int row = rows - 1; row >= 0 && sprites.Count < framesToCreate; row--)
            {
                for (int column = 0; column < columns && sprites.Count < framesToCreate; column++)
                {
                    var rect = new Rect(column * frameWidth, row * frameHeight, frameWidth, frameHeight);
                    var sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
                    sprites.Add(sprite);
                }
            }

            // Trim trailing blank frames caused by sprite-sheet grid padding.
            // Spritesheets often have fewer actual frames than columns×rows cells;
            // the remainder are fully transparent. Without trimming, the animator
            // plays into those empty cells and the character appears to blink/vanish.
            if (frameCount <= 0 && sprites.Count > 1)
            {
                int trimTo = sprites.Count;
                for (int i = sprites.Count - 1; i > 0; i--)
                {
                    var s = sprites[i];
                    var r = s.textureRect;
                    bool hasContent = false;
                    // Sample a 3×3 grid of interior points to detect any visible pixel.
                    for (int sy = 0; sy < 3 && !hasContent; sy++)
                    {
                        for (int sx = 0; sx < 3 && !hasContent; sx++)
                        {
                            int px = (int)(r.x + r.width  * (0.25f + sx * 0.25f));
                            int py = (int)(r.y + r.height * (0.25f + sy * 0.25f));
                            if (texture.GetPixel(px, py).a > 0.01f)
                                hasContent = true;
                        }
                    }

                    if (hasContent)
                        break;

                    trimTo = i;
                }

                if (trimTo < sprites.Count)
                    sprites.RemoveRange(trimTo, sprites.Count - trimTo);
            }

            var result = sprites.ToArray();
            SpriteCache[cacheKey] = result;
            return result;
        }

        public static void ReleaseFrames(
            string path,
            int columns,
            int rows,
            int frameCount,
            Sprite[] expectedFrames)
        {
            if (string.IsNullOrWhiteSpace(path) || columns <= 0 || rows <= 0)
                return;

            bool isResources = path.StartsWith(ResourcesScheme, StringComparison.Ordinal);
            string resolvedPath = isResources ? path : ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return;

            string cacheKey = BuildSpriteCacheKey(
                resolvedPath,
                columns,
                rows,
                frameCount);
            Sprite[] cached;
            if (!SpriteCache.TryGetValue(cacheKey, out cached) ||
                (expectedFrames != null && !ReferenceEquals(cached, expectedFrames)))
                return;

            SpriteCache.Remove(cacheKey);
            for (int i = 0; i < cached.Length; i++)
                DestroyRuntimeObject(cached[i]);

            string prefix = resolvedPath + "|";
            foreach (string key in SpriteCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            Texture2D texture;
            if (!TextureCache.TryGetValue(resolvedPath, out texture))
                return;
            TextureCache.Remove(resolvedPath);
            if (texture == null)
                return;
            if (isResources)
                Resources.UnloadAsset(texture);
            else
                DestroyRuntimeObject(texture);
        }

        private static void DestroyRuntimeObject(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(value);
            else
                UnityEngine.Object.DestroyImmediate(value);
        }

        /// <summary>
        /// Coroutine-friendly preloader. Yields after each clip so the caller
        /// can interleave boot-log UI updates. Populates TextureCache and SpriteCache.
        /// </summary>
        public static System.Collections.IEnumerator PreloadManifestCoroutine(
            string manifestPath,
            System.Action<string, int, int> onClipLoaded = null)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
                yield break;

            bool isResources = manifestPath.StartsWith(ResourcesScheme, StringComparison.Ordinal);
            string resolvedPath = isResources ? manifestPath : ResolvePath(manifestPath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                yield break;

            AvatarMotionPackManifest manifest;
            if (!TryLoadManifest(resolvedPath, isResources, out manifest))
                yield break;

            if (manifest == null || manifest.clips == null || manifest.clips.Count == 0)
                yield break;

            string baseDir;
            if (isResources)
            {
                int slash = resolvedPath.LastIndexOf('/');
                baseDir = slash > ResourcesScheme.Length
                    ? resolvedPath.Substring(0, slash)
                    : resolvedPath;
            }
            else
            {
                baseDir = System.IO.Path.GetDirectoryName(resolvedPath) ?? string.Empty;
            }
            int total = manifest.clips.Count;

            for (int i = 0; i < total; i++)
            {
                var clip = manifest.clips[i];
                if (clip == null || string.IsNullOrWhiteSpace(clip.spriteSheetPath))
                    continue;

                string spritePath = clip.spriteSheetPath;
                if (isResources)
                    spritePath = BuildResourcePath(baseDir, spritePath);
                else if (!System.IO.Path.IsPathRooted(spritePath) && !string.IsNullOrEmpty(baseDir))
                    spritePath = System.IO.Path.Combine(baseDir, spritePath);

                yield return PreloadFramesCoroutine(spritePath, clip.columns, clip.rows, clip.frameCount);

                if (onClipLoaded != null)
                    onClipLoaded(clip.action, i + 1, total);

                yield return null;
            }

            // Preload lipsync clip if present
            if (manifest.lipsyncClip != null && !string.IsNullOrWhiteSpace(manifest.lipsyncClip.spriteSheetPath))
            {
                string lipsyncPath = manifest.lipsyncClip.spriteSheetPath;
                if (isResources)
                    lipsyncPath = BuildResourcePath(baseDir, lipsyncPath);
                else if (!System.IO.Path.IsPathRooted(lipsyncPath) && !string.IsNullOrEmpty(baseDir))
                    lipsyncPath = System.IO.Path.Combine(baseDir, lipsyncPath);

                yield return PreloadFramesCoroutine(
                    lipsyncPath,
                    manifest.lipsyncClip.columns,
                    manifest.lipsyncClip.rows,
                    manifest.lipsyncClip.frameCount);
            }
        }

        public static System.Collections.IEnumerator PreloadFramesCoroutine(
            string path,
            int columns,
            int rows,
            int frameCount = 0,
            int spritesPerFrame = 8)
        {
            if (string.IsNullOrWhiteSpace(path) || columns <= 0 || rows <= 0)
                yield break;

            bool isResources = path.StartsWith(ResourcesScheme, StringComparison.Ordinal);
            string resolvedPath = isResources ? path : ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                yield break;

            string cacheKey = BuildSpriteCacheKey(resolvedPath, columns, rows, frameCount);
            if (HasValidSpriteCache(cacheKey))
                yield break;

            Texture2D texture = null;
            if (TextureCache.TryGetValue(resolvedPath, out texture))
            {
                if (texture == null)
                    TextureCache.Remove(resolvedPath);
            }

            if (texture == null)
            {
                if (isResources)
                    texture = LoadTextureFromResources(resolvedPath);
                else
                    yield return LoadTextureCoroutine(resolvedPath, loaded => texture = loaded);
            }

            if (texture == null)
                yield break;

            yield return BuildFrameSpritesCoroutine(
                texture,
                cacheKey,
                columns,
                rows,
                frameCount,
                Mathf.Max(1, spritesPerFrame));
        }

        private static System.Collections.IEnumerator LoadTextureCoroutine(
            string resolvedPath,
            System.Action<Texture2D> onLoaded)
        {
            if (onLoaded == null)
                yield break;

            onLoaded(null);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                yield break;

            string uri = new Uri(resolvedPath).AbsoluteUri;
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(uri, false))
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    yield return null;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    NeonLogger.LogWarning("[SpriteSheetLoader] Async texture load failed: " + request.error);
                    yield break;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null)
                    yield break;

                TextureCache[resolvedPath] = texture;
                onLoaded(texture);
            }
        }

        private static System.Collections.IEnumerator BuildFrameSpritesCoroutine(
            Texture2D texture,
            string cacheKey,
            int columns,
            int rows,
            int frameCount,
            int spritesPerFrame)
        {
            if (texture == null || string.IsNullOrEmpty(cacheKey) || columns <= 0 || rows <= 0)
                yield break;

            int frameWidth = texture.width / columns;
            int frameHeight = texture.height / rows;
            if (frameWidth <= 0 || frameHeight <= 0)
                yield break;

            int availableFrames = columns * rows;
            int framesToCreate = frameCount > 0 ? Mathf.Min(frameCount, availableFrames) : availableFrames;
            var sprites = new List<Sprite>(framesToCreate);
            int createdThisFrame = 0;
            for (int row = rows - 1; row >= 0 && sprites.Count < framesToCreate; row--)
            {
                for (int column = 0; column < columns && sprites.Count < framesToCreate; column++)
                {
                    var rect = new Rect(column * frameWidth, row * frameHeight, frameWidth, frameHeight);
                    var sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
                    sprites.Add(sprite);

                    createdThisFrame++;
                    if (createdThisFrame >= spritesPerFrame)
                    {
                        createdThisFrame = 0;
                        yield return null;
                    }
                }
            }

            if (frameCount <= 0)
                yield return TrimTrailingBlankSpritesCoroutine(texture, sprites, 4);
            SpriteCache[cacheKey] = sprites.ToArray();
        }

        private static System.Collections.IEnumerator TrimTrailingBlankSpritesCoroutine(
            Texture2D texture,
            List<Sprite> sprites,
            int checksPerFrame)
        {
            if (texture == null || sprites == null || sprites.Count <= 1)
                yield break;

            int trimTo = sprites.Count;
            int checksThisFrame = 0;
            for (int i = sprites.Count - 1; i > 0; i--)
            {
                var s = sprites[i];
                var r = s.textureRect;
                bool hasContent = false;
                for (int sy = 0; sy < 3 && !hasContent; sy++)
                {
                    for (int sx = 0; sx < 3 && !hasContent; sx++)
                    {
                        int px = (int)(r.x + r.width * (0.25f + sx * 0.25f));
                        int py = (int)(r.y + r.height * (0.25f + sy * 0.25f));
                        if (texture.GetPixel(px, py).a > 0.01f)
                            hasContent = true;
                    }
                }

                if (hasContent)
                    break;

                trimTo = i;
                checksThisFrame++;
                if (checksThisFrame >= Mathf.Max(1, checksPerFrame))
                {
                    checksThisFrame = 0;
                    yield return null;
                }
            }

            if (trimTo < sprites.Count)
                sprites.RemoveRange(trimTo, sprites.Count - trimTo);
        }

        private static string BuildSpriteCacheKey(string resolvedPath, int columns, int rows, int frameCount)
        {
            return $"{resolvedPath}|{columns}x{rows}|{frameCount}";
        }

        private static string BuildResourcePath(string baseDir, string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return rawPath;

            string noExtension = rawPath.Trim().Replace('\\', '/');
            int dot = noExtension.LastIndexOf('.');
            if (dot > noExtension.LastIndexOf('/'))
                noExtension = noExtension.Substring(0, dot);

            return baseDir.TrimEnd('/') + "/" + noExtension.TrimStart('/');
        }

        private static bool TryLoadManifest(
            string resolvedPath,
            bool isResources,
            out AvatarMotionPackManifest manifest)
        {
            manifest = null;
            TextAsset manifestAsset = null;
            try
            {
                string json;
                if (isResources)
                {
                    string key = resolvedPath.Substring(ResourcesScheme.Length);
                    manifestAsset = Resources.Load<TextAsset>(key);
                    if (manifestAsset == null)
                        return false;
                    json = manifestAsset.text;
                }
                else
                {
                    if (!System.IO.File.Exists(resolvedPath))
                        return false;
                    json = System.IO.File.ReadAllText(resolvedPath);
                }

                manifest = JsonUtility.FromJson<AvatarMotionPackManifest>(json);
                return manifest != null;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (manifestAsset != null)
                    Resources.UnloadAsset(manifestAsset);
            }
        }

        private static bool HasValidSpriteCache(string cacheKey)
        {
            if (string.IsNullOrEmpty(cacheKey))
                return false;

            Sprite[] cached;
            if (SpriteCache.TryGetValue(cacheKey, out cached))
            {
                if (cached != null && cached.Length > 0 && cached[0] != null)
                    return true;
                SpriteCache.Remove(cacheKey);
            }

            return false;
        }

        internal static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string trimmed = path.Trim();
            if (Path.IsPathRooted(trimmed))
                return File.Exists(trimmed) ? trimmed : null;

            string normalized = trimmed.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "..")
                    return null;
            }

            var candidates = new List<string>
            {
                Path.Combine(AppPaths.RootData, normalized),
                Path.Combine(Application.streamingAssetsPath, normalized),
                Path.Combine(Application.dataPath, normalized)
            };

            try
            {
                string currentDirectory = Directory.GetCurrentDirectory();
                string currentDirectoryRoot = Path.GetFullPath(currentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string currentDirectoryCandidate = Path.GetFullPath(Path.Combine(currentDirectory, normalized));
                if (currentDirectoryCandidate.StartsWith(currentDirectoryRoot, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(currentDirectoryCandidate);
            }
            catch (Exception)
            {
                // Ignore invalid working-directory-relative paths and continue candidate probing.
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
