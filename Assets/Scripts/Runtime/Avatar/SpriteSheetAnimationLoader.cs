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
        // inside the APK on Android, unlike StreamingAssets+File). Sheets are stored as
        // TextAsset (.bytes) and decoded via LoadImage → readable RGBA32, which the
        // blank-frame trim (GetPixel) below needs.
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

        public static Sprite[] LoadFrames(string path, int columns, int rows)
        {
            if (string.IsNullOrWhiteSpace(path) || columns <= 0 || rows <= 0)
                return Array.Empty<Sprite>();

            bool isResources = path.StartsWith(ResourcesScheme, StringComparison.Ordinal);
            string resolvedPath = isResources ? path : ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return Array.Empty<Sprite>();

            string cacheKey = $"{resolvedPath}|{columns}x{rows}";
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

            var sprites = new List<Sprite>(columns * rows);
            for (int row = rows - 1; row >= 0; row--)
            {
                for (int column = 0; column < columns; column++)
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
            if (sprites.Count > 1)
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

            string resolvedPath = ResolvePath(manifestPath);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !System.IO.File.Exists(resolvedPath))
                yield break;

            AvatarMotionPackManifest manifest;
            try
            {
                string json = System.IO.File.ReadAllText(resolvedPath);
                manifest = JsonUtility.FromJson<AvatarMotionPackManifest>(json);
            }
            catch
            {
                yield break;
            }

            if (manifest == null || manifest.clips == null || manifest.clips.Count == 0)
                yield break;

            string baseDir = System.IO.Path.GetDirectoryName(resolvedPath) ?? string.Empty;
            int total = manifest.clips.Count;

            for (int i = 0; i < total; i++)
            {
                var clip = manifest.clips[i];
                if (clip == null || string.IsNullOrWhiteSpace(clip.spriteSheetPath))
                    continue;

                string spritePath = clip.spriteSheetPath;
                if (!System.IO.Path.IsPathRooted(spritePath) && !string.IsNullOrEmpty(baseDir))
                    spritePath = System.IO.Path.Combine(baseDir, spritePath);

                yield return PreloadFramesCoroutine(spritePath, clip.columns, clip.rows);

                if (onClipLoaded != null)
                    onClipLoaded(clip.action, i + 1, total);

                yield return null;
            }

            // Preload lipsync clip if present
            if (manifest.lipsyncClip != null && !string.IsNullOrWhiteSpace(manifest.lipsyncClip.spriteSheetPath))
            {
                string lipsyncPath = manifest.lipsyncClip.spriteSheetPath;
                if (!System.IO.Path.IsPathRooted(lipsyncPath) && !string.IsNullOrEmpty(baseDir))
                    lipsyncPath = System.IO.Path.Combine(baseDir, lipsyncPath);

                yield return PreloadFramesCoroutine(lipsyncPath, manifest.lipsyncClip.columns, manifest.lipsyncClip.rows);
            }
        }

        public static System.Collections.IEnumerator PreloadFramesCoroutine(
            string path,
            int columns,
            int rows,
            int spritesPerFrame = 8)
        {
            if (string.IsNullOrWhiteSpace(path) || columns <= 0 || rows <= 0)
                yield break;

            string resolvedPath = ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                yield break;

            string cacheKey = BuildSpriteCacheKey(resolvedPath, columns, rows);
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
                yield return LoadTextureCoroutine(resolvedPath, loaded => texture = loaded);
            }

            if (texture == null)
                yield break;

            yield return BuildFrameSpritesCoroutine(texture, cacheKey, columns, rows, Mathf.Max(1, spritesPerFrame));
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
            int spritesPerFrame)
        {
            if (texture == null || string.IsNullOrEmpty(cacheKey) || columns <= 0 || rows <= 0)
                yield break;

            int frameWidth = texture.width / columns;
            int frameHeight = texture.height / rows;
            if (frameWidth <= 0 || frameHeight <= 0)
                yield break;

            var sprites = new List<Sprite>(columns * rows);
            int createdThisFrame = 0;
            for (int row = rows - 1; row >= 0; row--)
            {
                for (int column = 0; column < columns; column++)
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

        private static string BuildSpriteCacheKey(string resolvedPath, int columns, int rows)
        {
            return $"{resolvedPath}|{columns}x{rows}";
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
