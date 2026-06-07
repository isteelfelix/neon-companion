using System;
using UnityEngine;

namespace NeonCompanion.Runtime.Terminal
{
    /// <summary>
    /// Creates the platform-appropriate <see cref="IPtySession"/>. Compile-time guards
    /// keep each platform's native backend out of the others' builds.
    ///
    /// Windows  -> ConPtySession (ConPTY, kernel32)        [implemented]
    /// macOS    -> UnixPtySession (forkpty/openpt, libc)   [Phase 1b]
    /// Linux    -> UnixPtySession (forkpty/openpt, libc)   [Phase 1b]
    /// </summary>
    public static class PtySessionFactory
    {
        /// <summary>True when a real PTY backend exists for the current build target.</summary>
        public static bool IsSupported
        {
            get
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Creates and starts a shell session sized to <paramref name="columns"/> x
        /// <paramref name="rows"/> character cells, rooted at <paramref name="workingDirectory"/>.
        /// </summary>
        public static IPtySession Create(string workingDirectory, int columns, int rows)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            ConPtySession session = new ConPtySession();
            session.Start(ResolveWindowsShell(), workingDirectory, columns, rows);
            return session;
#else
            // Phase 1b: UnixPtySession (forkpty/openpt) lands here.
            throw new PlatformNotSupportedException(
                "Real PTY backend not yet available on platform: " + Application.platform);
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static string ResolveWindowsShell()
        {
            // ConPTY/CreateProcess resolves bare names against PATH. Windows PowerShell is
            // always present; pwsh (PowerShell 7) is preferred when installed but is left
            // for a later refinement to keep startup dependency-free.
            return "powershell.exe";
        }
#endif
    }
}
