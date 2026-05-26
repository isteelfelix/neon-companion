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

            if (manifest.lipsyncClip != null)
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
                baseDir = Path.GetDirectoryName(manifestPath) ?? string.Empty;

            for (int i = 0; i < manifest.clips.Count; i++)
            {
                var clip = manifest.clips[i];
                if (clip == null)
                    continue;

                output.animationClips.Add(ConvertClip(clip, baseDir));
            }

            if (manifest.lipsyncClip != null)
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
            return new SpriteSheetAnimation
            {
                clipName = (clip.action ?? string.Empty).Trim().ToLowerInvariant(),
                spriteSheetPath = ResolveClipPath(clip.spriteSheetPath, baseDir),
                columns = Mathf.Max(1, clip.columns),
                rows = Mathf.Max(1, clip.rows),
                frameRate = Mathf.Max(0.01f, clip.frameRate),
                loop = RequiresOneShot((clip.action ?? string.Empty).Trim()) ? false : clip.loop
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

            if (!string.IsNullOrWhiteSpace(baseDir))
                return Path.Combine(baseDir, rawPath);

            return rawPath;
        }
    }
}
