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
        public bool isBuiltIn;
        public string systemPrompt;
        public List<SpriteSheetAnimation> animationClips = new List<SpriteSheetAnimation>();
    }
}
