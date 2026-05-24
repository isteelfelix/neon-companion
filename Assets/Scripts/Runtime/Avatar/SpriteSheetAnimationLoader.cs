using System;
using System.Collections.Generic;
using System.IO;
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

            if (TextureCache.TryGetValue(path, out var cached))
                return cached;

            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            TextureCache[path] = texture;
            return texture;
        }

        public static Sprite[] LoadFrames(string path, int columns, int rows)
        {
            if (string.IsNullOrWhiteSpace(path) || columns <= 0 || rows <= 0)
                return Array.Empty<Sprite>();

            string cacheKey = $"{path}|{columns}x{rows}";
            if (SpriteCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var texture = LoadTexture(path);
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
    }
}
