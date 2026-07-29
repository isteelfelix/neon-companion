using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    public static class AvatarProfileTypes
    {
        public const string Static2D = "static-2d";
        public const string SpriteSheet = "sprite-sheet";
        public const string Generic3D = "generic-3d";
        public const string Vrm = "vrm";
    }

    public static class BuiltInAvatarProfiles
    {
        public const string NeonVrmId = "neon-vrm";
        public const string ResourceScheme = "resource://";
        public const string NeonVrmResourcePath = "Avatars/neon/Neon.vrm";

        public static AvatarProfile CreateNeonVrm()
        {
            var capabilities = new AvatarCapabilities
            {
                isRuntimeSupported = true
            };
            capabilities.evidence.Add("built_in_resource");

            return new AvatarProfile
            {
                contractVersion = AvatarProfile.CurrentContractVersion,
                id = NeonVrmId,
                name = "Neon VRM",
                avatarType = AvatarProfileTypes.Vrm,
                modelPath = ResourceScheme + NeonVrmResourcePath,
                isBuiltIn = true,
                is3D = true,
                capabilities = capabilities,
                modelAnimationClips = new List<string>
                {
                    "idle",
                    "thinking",
                    "talking",
                    "listening",
                    "smile",
                    "confused"
                },
                stateClipMapping = new Avatar3DStateClipMapping
                {
                    idle = "idle",
                    thinking = "thinking",
                    talking = "talking",
                    listening = "listening",
                    smile = "smile",
                    confused = "confused"
                }
            };
        }

        public static AvatarProfile TryCreate(string avatarId)
        {
            if (string.Equals(avatarId, NeonVrmId, StringComparison.Ordinal))
                return CreateNeonVrm();
            return null;
        }

        public static bool IsResourcePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                path.StartsWith(ResourceScheme, StringComparison.Ordinal);
        }

        public static string GetResourcePath(string path)
        {
            return IsResourcePath(path) ? path.Substring(ResourceScheme.Length) : null;
        }
    }

    [Serializable]
    public class AvatarAssetSource
    {
        public string ownership = "local-user-owned-copy";
        public string relativePath;
        public string originalFileName;
        public string extension;
        public long fileSizeBytes;
    }

    [Serializable]
    public class AvatarCapabilities
    {
        public bool isVerified;
        public bool canRender;
        public bool canAnimate;
        public bool hasStateAnimations;
        public bool hasLipsync;
        public bool hasHumanoid;
        public bool hasBlink;
        public bool hasGaze;
        public bool hasExpressions;
        public bool isRestricted;
        public bool isRuntimeSupported;
        public int animationClipCount;
        public int expressionCount;
        public int sceneNodeCount;
        public int rendererCount;
        public long triangleCount;
        public List<string> evidence = new List<string>();
    }

    [Serializable]
    public class Avatar3DStateClipMapping
    {
        public string idle;
        public string thinking;
        public string talking;
        public string listening;
        public string smile;
        public string confused;

        public string GetClip(string state)
        {
            switch (state)
            {
                case "thinking": return thinking;
                case "talking": return talking;
                case "listening": return listening;
                case "smile": return smile;
                case "confused": return confused;
                default: return idle;
            }
        }
    }

    [Serializable]
    public class SpriteSheetAnimation
    {
        public string clipName;
        public string spriteSheetPath;
        public int columns = 1;
        public int rows = 1;
        public int frameCount;
        public float frameRate = 8f;
        public bool loop = true;
        public bool pingPong = false;
    }

    [Serializable]
    public class AvatarProfile
    {
        public const int CurrentContractVersion = 1;

        // Versioned backend contract. Legacy fields below remain populated so profiles
        // written before Phase A continue to load without migration or data loss.
        public int contractVersion;
        public string avatarType;
        public AvatarAssetSource source;
        public AvatarCapabilities capabilities;
        public Avatar3DStateClipMapping stateClipMapping;
        public string diagnostic;
        public string id;
        public string name;
        public string imagePath;
        public string modelPath;
        public bool isBuiltIn;
        public bool is3D;
        public string systemPrompt;
        // Optional runtime motion-pack manifest path (spritesheet-pack v1).
        public string motionPackManifestPath;
        public List<SpriteSheetAnimation> animationClips = new List<SpriteSheetAnimation>();
        public List<string> modelAnimationClips = new List<string>();
        // Mouth sprite sheet for lipsync: frames ordered Silence(0), A(1), E(2), I(3), O(4), U(5)
        public SpriteSheetAnimation lipsyncClip;
        public AvatarCustomizationData customization;
        public float avatarScale = 1f;
        public float avatarOffsetX = 0f;
        public float avatarOffsetY = 0f;

        public void NormalizeContract()
        {
            if (contractVersion <= 0)
                contractVersion = CurrentContractVersion;

            if (string.IsNullOrWhiteSpace(avatarType))
            {
                if (is3D || !string.IsNullOrWhiteSpace(modelPath))
                    avatarType = AvatarProfileTypes.Generic3D;
                else if (!string.IsNullOrWhiteSpace(motionPackManifestPath) ||
                         (animationClips != null && animationClips.Count > 0))
                    avatarType = AvatarProfileTypes.SpriteSheet;
                else
                    avatarType = AvatarProfileTypes.Static2D;
            }

            if ((avatarType == AvatarProfileTypes.Generic3D ||
                 avatarType == AvatarProfileTypes.Vrm) &&
                !string.IsNullOrWhiteSpace(modelPath))
                is3D = true;

            if (source == null)
            {
                string legacyPath = !string.IsNullOrWhiteSpace(modelPath) ? modelPath :
                    (!string.IsNullOrWhiteSpace(motionPackManifestPath) ? motionPackManifestPath : imagePath);
                if (!string.IsNullOrWhiteSpace(legacyPath))
                {
                    source = new AvatarAssetSource
                    {
                        relativePath = legacyPath,
                        originalFileName = System.IO.Path.GetFileName(legacyPath),
                        extension = System.IO.Path.GetExtension(legacyPath)
                    };
                }
            }

            if (capabilities == null)
            {
                bool knownType = avatarType == AvatarProfileTypes.Static2D ||
                    avatarType == AvatarProfileTypes.SpriteSheet ||
                    avatarType == AvatarProfileTypes.Generic3D ||
                    avatarType == AvatarProfileTypes.Vrm;
                capabilities = new AvatarCapabilities();
                capabilities.canRender = knownType && avatarType != AvatarProfileTypes.Vrm;
                capabilities.hasStateAnimations = avatarType == AvatarProfileTypes.SpriteSheet ||
                    (modelAnimationClips != null && modelAnimationClips.Count > 0);
                capabilities.canAnimate = capabilities.hasStateAnimations;
                capabilities.hasLipsync = lipsyncClip != null;
                capabilities.isRuntimeSupported = knownType && avatarType != AvatarProfileTypes.Vrm;
                capabilities.animationClipCount = avatarType == AvatarProfileTypes.SpriteSheet
                    ? (animationClips != null ? animationClips.Count : 0)
                    : (modelAnimationClips != null ? modelAnimationClips.Count : 0);
                capabilities.evidence.Add("legacy_profile_fields");
            }

            if (diagnostic == null)
                diagnostic = string.Empty;
            if (contractVersion > CurrentContractVersion)
            {
                diagnostic = "unsupported_contract_version";
                capabilities.canRender = false;
                capabilities.canAnimate = false;
                capabilities.hasStateAnimations = false;
                capabilities.hasLipsync = false;
                capabilities.isRuntimeSupported = false;
            }
            if (!IsKnownType(avatarType) && string.IsNullOrWhiteSpace(diagnostic))
                diagnostic = "unsupported_contract_type";
        }

        public static bool IsKnownType(string type)
        {
            return type == AvatarProfileTypes.Static2D ||
                type == AvatarProfileTypes.SpriteSheet ||
                type == AvatarProfileTypes.Generic3D ||
                type == AvatarProfileTypes.Vrm;
        }
    }
}
