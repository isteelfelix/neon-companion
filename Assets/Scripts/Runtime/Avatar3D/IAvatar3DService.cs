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
        bool SetPose(string poseName);
        Transform GetRuntimeTransform();
        GameObject GetRuntimeRoot();
        void Unload();
    }
}
