using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Platform
{
    public interface IFileDropService
    {
        event Action<IReadOnlyList<string>> FilesDropped;

        bool IsAvailable { get; }

        void Start();
        void Stop();
    }
}
