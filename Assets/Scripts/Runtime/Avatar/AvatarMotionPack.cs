using System;
using System.Collections.Generic;
using System.IO;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar
{
    [Serializable]
    public sealed class AvatarMotionClipEntry
    {
        public string action;
        public string spriteSheetPath;
        public int columns = 1;
        public int rows = 1;
        public float frameRate = 8f;
        public bool loop = true;
        public bool pingPong = false;
    }

    [Serializable]
    public sealed class AvatarMotionPackManifest
    {
        public const int ExpectedFormatVersion = 1;
        public const string ExpectedFormat = "spritesheet-pack";

        public int formatVersion = ExpectedFormatVersion;
        public string format = ExpectedFormat;
        public List<AvatarMotionClipEntry> clips = new List<AvatarMotionClipEntry>();
        public AvatarMotionClipEntry lipsyncClip;

        public static readonly string[] RequiredActions =
            { "idle", "thinking", "talking", "listening", "smile", "confused" };
    }

    public sealed class AvatarMotionPackLoadResult
    {
        public bool isValid;
        public AvatarMotionPackManifest manifest;
        public string manifestPath;
        public string error;
    }

    public sealed class AvatarProfileMotionResolution
    {
        public List<SpriteSheetAnimation> animationClips = new List<SpriteSheetAnimation>();
        public SpriteSheetAnimation lipsyncClip;
        public string sourceManifestPath;
    }

    public static class AvatarMotionPackLoader
    {
        public static AvatarMotionPackLoadResult LoadFromPath(string manifestPath)
        {
            var result = new AvatarMotionPackLoadResult
            {
                isValid = false,
                manifestPath = manifestPath
            };

            string resolvedPath = SpriteSheetAnimationLoader.ResolvePath(manifestPath);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                result.error = "manifest file not found";
                return result;
            }

            AvatarMotionPackManifest manifest;
            try
            {
                string json = File.ReadAllText(resolvedPath);
                manifest = JsonUtility.FromJson<AvatarMotionPackManifest>(json);
            }
            catch (Exception ex)
            {
                result.error = "manifest read failed: " + ex.Message;
                return result;
            }

            if (manifest == null)
            {
                result.error = "manifest is null";
                return result;
            }

            string validationError;
            if (!ValidateManifest(manifest, out validationError))
            {
                result.error = validationError;
                return result;
            }

            result.isValid = true;
            result.manifestPath = resolvedPath;
            result.manifest = manifest;
            return result;
        }

        public static bool ValidateManifest(AvatarMotionPackManifest manifest, out string error)
        {
            error = null;

            if (manifest == null)
            {
                error = "manifest is null";
                return false;
            }

            if (manifest.formatVersion != AvatarMotionPackManifest.ExpectedFormatVersion)
            {
                error = "unsupported formatVersion";
                return false;
            }

            if (!string.Equals(manifest.format, AvatarMotionPackManifest.ExpectedFormat, StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported format";
                return false;
            }

            if (manifest.clips == null || manifest.clips.Count == 0)
            {
                error = "clips are missing";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < manifest.clips.Count; i++)
            {
                var clip = manifest.clips[i];
                if (clip == null)
                {
                    error = "clip entry is null";
                    return false;
                }

                if (!ValidateClipEntry(clip, out error))
                    return false;

                if (!seen.Add(clip.action))
                {
                    error = "duplicate action: " + clip.action;
                    return false;
                }
            }

            for (int i = 0; i < AvatarMotionPackManifest.RequiredActions.Length; i++)
            {
                string action = AvatarMotionPackManifest.RequiredActions[i];
                if (!seen.Contains(action))
                {
                    error = "missing required action: " + action;
                    return false;
                }
            }

            // JsonUtility default-constructs lipsyncClip even when the field is absent in JSON.
            // Treat it as "no lipsync" when spriteSheetPath is empty.
            if (manifest.lipsyncClip != null &&
                !string.IsNullOrWhiteSpace(manifest.lipsyncClip.spriteSheetPath))
            {
                if (!ValidateLipsyncEntry(manifest.lipsyncClip, out error))
                    return false;
            }

            return true;
        }

        public static AvatarProfileMotionResolution BuildRuntimeClips(AvatarMotionPackManifest manifest, string manifestPath)
        {
            var output = new AvatarProfileMotionResolution();
            if (manifest == null || manifest.clips == null)
                return output;

            string baseDir = string.Empty;
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                // "res://Avatars/{id}/motion_pack" → base "res://Avatars/{id}" (string slice,
                // not Path.GetDirectoryName, which mangles the scheme separators).
                if (manifestPath.StartsWith("res://", StringComparison.Ordinal))
                {
                    int slash = manifestPath.LastIndexOf('/');
                    baseDir = slash > "res://".Length ? manifestPath.Substring(0, slash) : manifestPath;
                }
                else
                {
                    baseDir = Path.GetDirectoryName(manifestPath) ?? string.Empty;
                }
            }

            for (int i = 0; i < manifest.clips.Count; i++)
            {
                var clip = manifest.clips[i];
                if (clip == null)
                    continue;

                output.animationClips.Add(ConvertClip(clip, baseDir));
            }

            if (manifest.lipsyncClip != null &&
                !string.IsNullOrWhiteSpace(manifest.lipsyncClip.spriteSheetPath))
            {
                var lipsync = ConvertClip(manifest.lipsyncClip, baseDir);
                lipsync.clipName = "lipsync";
                output.lipsyncClip = lipsync;
            }

            output.sourceManifestPath = manifestPath;
            return output;
        }

        public static AvatarProfileMotionResolution ResolveProfileMotion(AvatarProfile profile)
        {
            var resolved = new AvatarProfileMotionResolution();
            if (profile == null)
                return resolved;

            if (!string.IsNullOrWhiteSpace(profile.motionPackManifestPath))
            {
                var load = LoadFromPath(profile.motionPackManifestPath);
                if (load.isValid)
                    return BuildRuntimeClips(load.manifest, load.manifestPath);

                NeonLogger.LogWarning("Motion pack manifest rejected for avatar '" + profile.id + "': " + (load.error ?? "unknown error"));
            }

            // Fallback: built-in motion pack at Resources/Avatars/{id}/motion_pack(.json).
            // Loaded from Resources (not StreamingAssets) so it works inside the APK on
            // Android — File.* can't read StreamingAssets there. The "res://" base dir tells
            // the sprite loader to fetch sheets from Resources too.
            if (!string.IsNullOrWhiteSpace(profile.id) &&
                (profile.animationClips == null || profile.animationClips.Count == 0))
            {
                var manifestAsset = Resources.Load<TextAsset>("Avatars/" + profile.id + "/motion_pack");
                if (manifestAsset != null)
                {
                    try
                    {
                        var manifest = JsonUtility.FromJson<AvatarMotionPackManifest>(manifestAsset.text);
                        string validationError = null;
                        if (manifest != null && ValidateManifest(manifest, out validationError))
                            return BuildRuntimeClips(manifest, "res://Avatars/" + profile.id + "/motion_pack");

                        NeonLogger.LogWarning("[MotionPack] Manifest validation failed for '" +
                            profile.id + "': " + (validationError ?? "manifest was null"));
                    }
                    catch (Exception ex)
                    {
                        NeonLogger.LogWarning("[MotionPack] Failed to read manifest for '" +
                            profile.id + "': " + ex.Message);
                    }
                    finally
                    {
                        Resources.UnloadAsset(manifestAsset);
                    }
                }
            }

            resolved.animationClips = profile.animationClips ?? new List<SpriteSheetAnimation>();
            resolved.lipsyncClip = profile.lipsyncClip;
            return resolved;
        }

        private static bool ValidateClipEntry(AvatarMotionClipEntry clip, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(clip.action))
            {
                error = "clip action is empty";
                return false;
            }

            return ValidateClipGeometry(clip, clip.action, out error);
        }

        private static bool ValidateLipsyncEntry(AvatarMotionClipEntry clip, out string error)
        {
            error = null;
            return ValidateClipGeometry(clip, "lipsync", out error);
        }

        private static bool ValidateClipGeometry(AvatarMotionClipEntry clip, string label, out string error)
        {
            error = null;

            if (clip == null)
            {
                error = label + " clip is null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(clip.spriteSheetPath))
            {
                error = "spriteSheetPath is empty for action: " + label;
                return false;
            }

            if (clip.columns <= 0 || clip.rows <= 0)
            {
                error = "columns/rows must be positive for action: " + label;
                return false;
            }

            if (clip.frameRate <= 0f)
            {
                error = "frameRate must be positive for action: " + label;
                return false;
            }

            return true;
        }

        private static SpriteSheetAnimation ConvertClip(AvatarMotionClipEntry clip, string baseDir)
        {
            bool oneShot = RequiresOneShot((clip.action ?? string.Empty).Trim());
            return new SpriteSheetAnimation
            {
                clipName = (clip.action ?? string.Empty).Trim().ToLowerInvariant(),
                spriteSheetPath = ResolveClipPath(clip.spriteSheetPath, baseDir),
                columns = Mathf.Max(1, clip.columns),
                rows = Mathf.Max(1, clip.rows),
                frameRate = Mathf.Max(0.01f, clip.frameRate),
                loop = oneShot ? false : clip.loop,
                pingPong = oneShot ? false : clip.pingPong,
            };
        }

        private static bool RequiresOneShot(string action)
        {
            return string.Equals(action, "smile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "confused", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveClipPath(string rawPath, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            if (Path.IsPathRooted(rawPath))
                return rawPath;

            // Resources-backed built-in pack: build a Resources key (no extension), e.g.
            // base "res://Avatars/neon" + "idle_sheet.png" → "res://Avatars/neon/idle_sheet".
            if (!string.IsNullOrWhiteSpace(baseDir) && baseDir.StartsWith("res://", StringComparison.Ordinal))
            {
                string noExt = rawPath;
                int dot = noExt.LastIndexOf('.');
                if (dot > 0)
                    noExt = noExt.Substring(0, dot);
                return baseDir + "/" + noExt;
            }

            if (!string.IsNullOrWhiteSpace(baseDir))
                return Path.Combine(baseDir, rawPath);

            return rawPath;
        }
    }
}
