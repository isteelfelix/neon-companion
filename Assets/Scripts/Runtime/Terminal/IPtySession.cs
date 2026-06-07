using System;

namespace NeonCompanion.Runtime.Terminal
{
    /// <summary>
    /// A live pseudo-terminal session backed by a real, long-lived shell process.
    /// Raw bytes flow in both directions; VT/ANSI interpretation is the emulator's job
    /// (see <see cref="Emulator.TerminalEmulator"/>), NOT this layer's.
    ///
    /// Implementations: <see cref="ConPtySession"/> (Windows ConPTY) and
    /// UnixPtySession (forkpty/openpt on macOS/Linux).
    /// </summary>
    public interface IPtySession : IDisposable
    {
        /// <summary>
        /// Raised when the child writes output. NOTE: fired on a background thread —
        /// callers must marshal to Unity's main thread before touching UI.
        /// </summary>
        event Action<byte[]> OutputReceived;

        /// <summary>Raised once when the child process exits, carrying its exit code.</summary>
        event Action<int> Exited;

        /// <summary>True while the shell process is running and the session is usable.</summary>
        bool IsAlive { get; }

        /// <summary>Send raw bytes (typically UTF-8 keystrokes / control sequences) to the shell.</summary>
        void Write(byte[] data);

        /// <summary>Inform the pseudo terminal of a new viewport size in character cells.</summary>
        void Resize(int columns, int rows);

        /// <summary>Terminate the shell and release all native resources.</summary>
        void Close();
    }
}
