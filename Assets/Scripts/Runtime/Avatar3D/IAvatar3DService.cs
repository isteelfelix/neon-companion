using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    public interface IAvatar3DService
    {
        bool IsLoaded { get; }
        IReadOnlyList<string> AvailableAnimations { get; }
        AvatarCapabilities Capabilities { get; }
        Task<bool> LoadAvatar(string modelPath);
        bool SetAnimation(string clipName);
        bool SetMouthShape(string shape);
        void ClearMouth();
        bool SetExpression(string expressionName, float weight);

        /// <summary>
        /// Blends the face into a named emotional state, which then fades back to
        /// neutral on its own. Unlike <see cref="SetExpression"/> this is a whole
        /// composed face rather than one blendshape, and it needs no reset call.
        /// </summary>
        bool SetEmotion(string emotionName);
        bool SetPose(string poseName);
        void SetGazeNormalized(float horizontal, float vertical);
        Transform GetRuntimeTransform();
        GameObject GetRuntimeRoot();
        void Unload();
    }
}
