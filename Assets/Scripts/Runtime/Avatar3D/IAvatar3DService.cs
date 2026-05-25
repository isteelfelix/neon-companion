using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    public interface IAvatar3DService
    {
        bool IsLoaded { get; }
        IReadOnlyList<string> AvailableAnimations { get; }
        Task<bool> LoadAvatar(string modelPath);
        bool SetAnimation(string clipName);
        bool SetPose(string poseName);
        Transform GetRuntimeTransform();
        GameObject GetRuntimeRoot();
        void Unload();
    }
}
