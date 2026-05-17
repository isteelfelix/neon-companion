using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NeonCompanion.Editor
{
    public static class UiTextureGenerator
    {
        private const string OutputDir = "Assets/UI/Avatars";
        private const int AvatarSize = 512;
        private const int HaloSize = 1024;
        private const int AtmoW = 1920;
        private const int AtmoH = 1080;

        private static readonly AvatarDef[] Avatars =
        {
            new AvatarDef("neon",
                new Color32(122, 100, 240, 255),
                new Color32(60, 50, 140, 255),
                new Color32(28, 20, 70, 255),
                new Color32(200, 90, 220, 130), new Vector2(0.7f, 0.7f)),
            new AvatarDef("aurora",
                new Color32(80, 210, 220, 255),
                new Color32(140, 100, 230, 255),
                new Color32(230, 120, 210, 255),
                new Color32(240, 220, 130, 110), new Vector2(0.5f, 0.95f)),
            new AvatarDef("ember",
                new Color32(240, 150, 80, 255),
                new Color32(200, 70, 40, 255),
                new Color32(70, 28, 18, 255),
                new Color32(255, 200, 100, 130), new Vector2(0.2f, 0.8f)),
            new AvatarDef("glass",
                new Color32(60, 66, 84, 255),
                new Color32(30, 33, 42, 255),
                new Color32(22, 24, 33, 255),
                new Color32(124, 122, 237, 110), new Vector2(0.5f, 0.5f)),
            new AvatarDef("flora",
                new Color32(120, 200, 130, 255),
                new Color32(50, 130, 90, 255),
                new Color32(20, 50, 40, 255),
                new Color32(230, 230, 130, 130), new Vector2(0.7f, 0.3f)),
            new AvatarDef("mono",
                new Color32(225, 228, 235, 255),
                new Color32(150, 158, 173, 255),
                new Color32(70, 78, 95, 255),
                new Color32(255, 255, 255, 160), new Vector2(0.3f, 0.3f)),
            new AvatarDef("cobalt",
                new Color32(80, 130, 240, 255),
                new Color32(40, 70, 200, 255),
                new Color32(20, 30, 90, 255),
                new Color32(140, 200, 240, 130), new Vector2(0.8f, 0.2f)),
            new AvatarDef("rose",
                new Color32(245, 150, 170, 255),
                new Color32(200, 80, 130, 255),
                new Color32(70, 30, 50, 255),
                new Color32(255, 180, 200, 130), new Vector2(0.2f, 0.7f)),
        };

        [MenuItem("Tools/Neon Companion/Generate UI Textures")]
        public static void Generate()
        {
            Directory.CreateDirectory(OutputDir);

            foreach (var a in Avatars)
            {
                var tex = BuildAvatar(a, AvatarSize);
                Save(tex, $"{OutputDir}/{a.Id}.png");
            }

            Save(BuildHalo(HaloSize), $"{OutputDir}/halo.png");
            Save(BuildVignette(AvatarSize), $"{OutputDir}/vignette.png");
            Save(BuildAtmo(AtmoW, AtmoH), $"{OutputDir}/atmo.png");
            Save(BuildBrandMark(64), $"{OutputDir}/brand-mark.png");

            AssetDatabase.Refresh();

            // Reimport with UI-friendly settings
            foreach (var a in Avatars) ApplyUiImport($"{OutputDir}/{a.Id}.png");
            ApplyUiImport($"{OutputDir}/halo.png");
            ApplyUiImport($"{OutputDir}/vignette.png");
            ApplyUiImport($"{OutputDir}/atmo.png");
            ApplyUiImport($"{OutputDir}/brand-mark.png");

            AssetDatabase.SaveAssets();
            Debug.Log($"[Neon] Generated UI textures into {OutputDir}");
        }

        // ===== Avatar: diagonal 3-stop gradient + radial highlight =====
        private static Texture2D BuildAvatar(AvatarDef a, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            var pixels = new Color32[size * size];

            // diagonal direction (top-left → bottom-right) for the base gradient
            Vector2 dir = new Vector2(1f, 1f).normalized;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 uv = new Vector2(x / (float)(size - 1), 1f - y / (float)(size - 1));

                    // base 3-stop gradient projected on dir
                    float t = Mathf.Clamp01(Vector2.Dot(uv, dir) / 1.414f);
                    Color baseCol = Sample3(a.Top, a.Mid, a.Bottom, t);

                    // highlight: soft radial around hotspot
                    float d = Vector2.Distance(uv, a.Hotspot);
                    float h = Mathf.Clamp01(1f - d / 0.55f);
                    h = h * h * (3f - 2f * h); // smoothstep

                    Color hi = (Color)a.Highlight;
                    hi.a *= h;

                    Color outCol = AlphaBlend(baseCol, hi);
                    pixels[y * size + x] = outCol;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        // ===== Halo: soft circular glow with subtle conic hue rotation =====
        private static Texture2D BuildHalo(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            var pixels = new Color32[size * size];

            Vector2 c = new Vector2(0.5f, 0.5f);
            Color accent = new Color(124 / 255f, 122 / 255f, 237 / 255f, 1f);
            Color accent2 = new Color(162 / 255f, 133 / 255f, 240 / 255f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 uv = new Vector2(x / (float)(size - 1), y / (float)(size - 1));
                    float d = Vector2.Distance(uv, c) * 2f; // 0 center → 1 edge
                    float intensity = Mathf.Clamp01(1f - d);
                    // soften falloff (blur-like)
                    intensity = Mathf.Pow(intensity, 1.6f);

                    // conic-ish hue: angle 0..1
                    float ang = (Mathf.Atan2(uv.y - 0.5f, uv.x - 0.5f) / (Mathf.PI * 2f)) + 0.5f;
                    Color hue = Color.Lerp(accent, accent2, Mathf.Abs(0.5f - ang) * 2f);

                    Color outCol = hue;
                    outCol.a = intensity * 0.85f;
                    pixels[y * size + x] = outCol;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        // ===== Vignette: black radial darkening at bottom =====
        private static Texture2D BuildVignette(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            var pixels = new Color32[size * size];

            Vector2 c = new Vector2(0.5f, 0.6f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 uv = new Vector2(x / (float)(size - 1), y / (float)(size - 1));
                    float d = Vector2.Distance(uv, c) * 2f;
                    float t = Mathf.Clamp01((d - 0.5f) / 0.5f);
                    Color outCol = new Color(0f, 0f, 0f, t * 0.4f);
                    pixels[y * size + x] = outCol;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        // ===== Atmosphere: two radial glows on dark bg (replaces app::before) =====
        private static Texture2D BuildAtmo(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            var pixels = new Color32[w * h];

            Color accent = new Color(124 / 255f, 122 / 255f, 237 / 255f, 1f);
            Color accent2 = new Color(162 / 255f, 133 / 255f, 240 / 255f, 1f);
            Vector2 hot1 = new Vector2(0.7f, 0.75f); // top-right (CSS y-flip)
            Vector2 hot2 = new Vector2(0.2f, 0.1f);  // bottom-left

            float aspect = w / (float)h;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector2 uv = new Vector2(x / (float)(w - 1), y / (float)(h - 1));

                    Vector2 d1 = uv - hot1;
                    d1.x *= aspect;
                    float i1 = Mathf.Clamp01(1f - d1.magnitude / 0.6f);
                    i1 = Mathf.Pow(i1, 1.8f);

                    Vector2 d2 = uv - hot2;
                    d2.x *= aspect;
                    float i2 = Mathf.Clamp01(1f - d2.magnitude / 0.5f);
                    i2 = Mathf.Pow(i2, 2f);

                    Color outCol = new Color(11 / 255f, 12 / 255f, 16 / 255f, 1f);
                    outCol = AlphaBlend(outCol, new Color(accent.r, accent.g, accent.b, i1 * 0.18f));
                    outCol = AlphaBlend(outCol, new Color(accent2.r, accent2.g, accent2.b, i2 * 0.12f));
                    pixels[y * w + x] = outCol;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        // ===== Brand mark: accent → accent2 diagonal gradient =====
        private static Texture2D BuildBrandMark(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            var pixels = new Color32[size * size];

            Color a = new Color(124 / 255f, 122 / 255f, 237 / 255f, 1f);
            Color b = new Color(162 / 255f, 133 / 255f, 240 / 255f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float t = (x + (size - 1 - y)) / (float)((size - 1) * 2);
                    pixels[y * size + x] = Color.Lerp(a, b, t);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        // ===== Helpers =====
        private static Color Sample3(Color32 top, Color32 mid, Color32 bot, float t)
        {
            if (t < 0.5f) return Color.Lerp(top, mid, t * 2f);
            return Color.Lerp(mid, bot, (t - 0.5f) * 2f);
        }

        private static Color AlphaBlend(Color dst, Color src)
        {
            float a = src.a + dst.a * (1f - src.a);
            if (a <= 0f) return new Color(0, 0, 0, 0);
            float r = (src.r * src.a + dst.r * dst.a * (1f - src.a)) / a;
            float g = (src.g * src.a + dst.g * dst.a * (1f - src.a)) / a;
            float b = (src.b * src.a + dst.b * dst.a * (1f - src.a)) / a;
            return new Color(r, g, b, a);
        }

        private static void Save(Texture2D tex, string path)
        {
            var png = tex.EncodeToPNG();
            File.WriteAllBytes(path, png);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void ApplyUiImport(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.sRGBTexture = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.SaveAndReimport();
        }

        private readonly struct AvatarDef
        {
            public readonly string Id;
            public readonly Color32 Top;
            public readonly Color32 Mid;
            public readonly Color32 Bottom;
            public readonly Color32 Highlight;
            public readonly Vector2 Hotspot;
            public AvatarDef(string id, Color32 top, Color32 mid, Color32 bot, Color32 hi, Vector2 hotspot)
            {
                Id = id; Top = top; Mid = mid; Bottom = bot; Highlight = hi; Hotspot = hotspot;
            }
        }
    }
}
