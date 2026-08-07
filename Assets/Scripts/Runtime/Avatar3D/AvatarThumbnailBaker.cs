using System;
using System.IO;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Rendering;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Bakes one still headshot per 3D avatar and keeps it on disk, so a gallery
    /// tile is a plain image instead of a live render target.
    ///
    /// The gallery used to point every VRM tile at the column's render texture —
    /// one texture shared by all of them, which meant every tile showed whichever
    /// model happened to be mounted. A baked still is per-avatar, survives
    /// restarts, and needs no model loaded to be shown.
    ///
    /// The shot is framed at eye level (the renderer's portrait framing), which is
    /// also why it can be baked straight after an import: a head-and-shoulders
    /// crop looks the same whether or not the idle pose has been applied yet.
    /// </summary>
    internal static class AvatarThumbnailBaker
    {
        // Matches the tile's 220x164 aspect and its scale-and-crop background, at
        // twice the size so it stays sharp on a hidpi panel.
        private const int Width = 512;
        private const int Height = 384;

        // Framing, as fractions of the model's stature. Measured off the humanoid
        // rig rather than renderer bounds — see Avatar3DRenderer.SetManualFraming.
        private const float HeadBoneHeightRatio = 0.87f;
        private const float EyeAboveHeadBone = 0.05f;
        private const float FocusBelowEyes = 0.02f;
        private const float HeadshotHalfHeight = 0.12f;

        // A tile is small and a still of it has no motion to read, so it is lit a
        // little harder than the live view.
        private const float StillBrightness = 1.3f;

        // Part of the file name: a change to the framing or the light rig makes
        // every still on disk stale, and bumping this rebakes them instead of
        // leaving the user with a gallery shot by the old rules.
        private const int StillVersion = 2;

        private static Avatar3DRenderer _renderer;
        private static Transform _stage;

        public static string StillsDirectory
        {
            get { return Path.Combine(AppPaths.RootData, "AvatarThumbnails"); }
        }

        public static string PathFor(string avatarId)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                return null;
            return Path.Combine(
                StillsDirectory,
                SanitizeId(avatarId) + ".v" + StillVersion + ".png");
        }

        /// <summary>
        /// True when a usable still is already on disk. A still older than the
        /// model file it was baked from does not count — the user replaced the
        /// model under the same profile.
        /// </summary>
        public static bool Exists(string avatarId, string modelPath)
        {
            string path = PathFor(avatarId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if (string.IsNullOrWhiteSpace(modelPath) ||
                BuiltInAvatarProfiles.IsResourcePath(modelPath))
                return true;

            try
            {
                if (!File.Exists(modelPath))
                    return true;
                return File.GetLastWriteTimeUtc(modelPath) <=
                    File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Drops this avatar's stills, including ones from older versions.</summary>
        public static void Delete(string avatarId)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                return;

            try
            {
                if (!System.IO.Directory.Exists(StillsDirectory))
                    return;

                string[] stale = System.IO.Directory.GetFiles(
                    StillsDirectory, SanitizeId(avatarId) + ".v*.png");
                for (int i = 0; i < stale.Length; i++)
                    File.Delete(stale[i]);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[NeonCompanion] Avatar thumbnail delete failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Stages the instance in front of a dedicated camera, captures one frame
        /// and puts it back exactly where it was. Works on a model that is live in
        /// the scene as well as on one parked in <see cref="Avatar3DModelCache"/>:
        /// everything happens inside this call, so no frame is drawn in between.
        /// Returns the file path, or null when nothing could be baked.
        /// </summary>
        public static string Bake(string avatarId, GameObject instance)
        {
            if (string.IsNullOrWhiteSpace(avatarId) || instance == null ||
                !Application.isPlaying)
                return null;

            Transform model = instance.transform;
            Transform originalParent = model.parent;
            Vector3 localPosition = model.localPosition;
            Quaternion localRotation = model.localRotation;
            Vector3 localScale = model.localScale;
            bool wasActive = instance.activeSelf;

            Avatar3DRenderer renderer = EnsureRenderer();
            Texture2D still = null;
            try
            {
                model.SetParent(EnsureStage(), false);
                model.localPosition = Vector3.zero;
                model.localRotation = Quaternion.identity;
                if (!wasActive)
                    instance.SetActive(true);

                renderer.ApplyGraphicsOverride(BuildStillGraphics());
                renderer.SetModelRoot(model);
                FrameHeadshot(renderer, instance);
                still = renderer.CaptureStill(Width, Height);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[NeonCompanion] Avatar thumbnail bake failed for '" +
                    avatarId + "': " + ex.Message);
            }
            finally
            {
                renderer.ClearModel();
                if (!wasActive)
                    instance.SetActive(false);
                model.SetParent(originalParent, false);
                model.localPosition = localPosition;
                model.localRotation = localRotation;
                model.localScale = localScale;
            }

            if (still == null)
                return null;

            try
            {
                // Clears out stills from an older framing/light rig at the same time.
                Delete(avatarId);
                string path = PathFor(avatarId);
                System.IO.Directory.CreateDirectory(StillsDirectory);
                File.WriteAllBytes(path, still.EncodeToPNG());
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[NeonCompanion] Avatar thumbnail write failed for '" +
                    avatarId + "': " + ex.Message);
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(still);
            }
        }

        /// <summary>
        /// Points the camera at the head, with the window sized off the humanoid
        /// rig. Renderer bounds are unusable here: a model that has not been posed
        /// yet reports its bind pose, so a bounds-derived frame pulls back far
        /// enough to show the arms out in a T. Non-humanoid models keep whatever
        /// framing <c>SetModelRoot</c> derived.
        /// </summary>
        private static void FrameHeadshot(Avatar3DRenderer renderer, GameObject instance)
        {
            Animator animator = instance.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman)
                return;

            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null)
                return;

            // A VRM's armature root rests on the floor, so the head bone's height
            // above the root is a reliable measure of the model's stature.
            float stature = (head.position.y - instance.transform.position.y) /
                HeadBoneHeightRatio;
            if (stature < 0.2f)
                return;

            Transform leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            float eyeY = leftEye != null && rightEye != null
                ? (leftEye.position.y + rightEye.position.y) * 0.5f
                : head.position.y + stature * EyeAboveHeadBone;

            Vector3 focus = new Vector3(
                head.position.x,
                eyeY - stature * FocusBelowEyes,
                head.position.z);
            renderer.SetManualFraming(focus, stature * HeadshotHalfHeight);
        }

        private static AvatarGraphicsSettings BuildStillGraphics()
        {
            AvatarGraphicsSettings source = GraphicsQualityService.Current;
            AvatarGraphicsSettings still = source != null
                ? JsonUtility.FromJson<AvatarGraphicsSettings>(JsonUtility.ToJson(source))
                : new AvatarGraphicsSettings();

            still.brightness = (source != null ? source.brightness : 1f) * StillBrightness;

            // The still is baked once, so it always gets clean edges regardless of
            // what the user chose for the live view. Post-processing stays off: it
            // is what writes an opaque background into the alpha channel, and the
            // tile is meant to show the avatar over its own colour.
            still.antialiasing = GraphicsOptions.AaMsaa4;
            still.postProcessing = false;
            still.bloom = 0f;
            return still;
        }

        // Far away from the live avatar, so a model staged here is outside every
        // other camera's far clip and its own camera sees nothing else.
        private static Transform EnsureStage()
        {
            if (_stage != null)
                return _stage;

            var stageObject = new GameObject("[AvatarThumbnailStage]");
            stageObject.hideFlags = HideFlags.HideAndDontSave;
            stageObject.transform.position = new Vector3(0f, -1000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(stageObject);
            _stage = stageObject.transform;
            return _stage;
        }

        // A renderer of its own, so baking never disturbs the framing, zoom or
        // orbit of the column and preview renderers.
        private static Avatar3DRenderer EnsureRenderer()
        {
            if (_renderer != null)
                return _renderer;

            var rendererObject = new GameObject("[AvatarThumbnailRenderer]");
            rendererObject.hideFlags = HideFlags.HideAndDontSave;
            rendererObject.transform.position = new Vector3(0f, -1000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(rendererObject);
            _renderer = rendererObject.AddComponent<Avatar3DRenderer>();
            return _renderer;
        }

        private static string SanitizeId(string avatarId)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(avatarId.Length);
            for (int i = 0; i < avatarId.Length; i++)
            {
                char c = avatarId[i];
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return builder.ToString();
        }
    }
}
