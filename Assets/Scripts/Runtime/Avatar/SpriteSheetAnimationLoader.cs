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
                return cached;

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
                return cached;

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

            var result = sprites.ToArray();
            SpriteCache[cacheKey] = result;
            return result;
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
