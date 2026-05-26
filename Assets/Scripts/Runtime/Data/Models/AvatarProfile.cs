using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class SpriteSheetAnimation
    {
        public string clipName;
        public string spriteSheetPath;
        public int columns = 1;
        public int rows = 1;
        public float frameRate = 8f;
        public bool loop = true;
    }

    [Serializable]
    public class AvatarProfile
    {
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
    }
}
