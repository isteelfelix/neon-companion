using System;
using System.Collections.Generic;
using System.IO;
using NeonCompanion.Runtime.Core;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar
{
    public static class SpriteSheetAnimationLoader
    {
        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite[]> SpriteCache = new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

        public static Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string resolvedPath = ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return null;

            return LoadTextureResolved(resolvedPath);
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

            string resolvedPath = ResolvePath(path);
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

            var texture = LoadTextureResolved(resolvedPath);
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

                LoadFrames(spritePath, clip.columns, clip.rows);

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

                LoadFrames(lipsyncPath, manifest.lipsyncClip.columns, manifest.lipsyncClip.rows);
            }
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
