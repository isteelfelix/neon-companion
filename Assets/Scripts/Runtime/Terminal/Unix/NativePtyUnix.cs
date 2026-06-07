#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
using System;
using System.Runtime.InteropServices;

namespace NeonCompanion.Runtime.Terminal
{
    /// <summary>
    /// P/Invoke surface for the Unix pseudo terminal path (macOS + Linux). Uses forkpty to
    /// spawn a shell on a new pty, then raw read/write/ioctl on the master fd. forkpty lives
    /// in libutil on Linux and in libSystem (mapped from "libc") on macOS, so two entry
    /// points are declared and the right one is chosen at runtime.
    ///
    /// UNTESTED on device — see UnixPtySession remarks.
    /// </summary>
    internal static class NativePtyUnix
    {
        // ioctl(TIOCSWINSZ) request code differs by platform.
        public const ulong TIOCSWINSZ_LINUX = 0x5414;
        public const ulong TIOCSWINSZ_MAC = 0x80087467;

        public const int SIGHUP = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct winsize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        [DllImport("libutil.so.1", EntryPoint = "forkpty", SetLastError = true)]
        public static extern int forkpty_linux(out int amaster, IntPtr name, IntPtr termp, ref winsize winp);

        [DllImport("libc", EntryPoint = "forkpty", SetLastError = true)]
        public static extern int forkpty_mac(out int amaster, IntPtr name, IntPtr termp, ref winsize winp);

        [DllImport("libc", SetLastError = true)]
        public static extern int execv(IntPtr path, IntPtr argv);

        [DllImport("libc", SetLastError = true)]
        public static extern int chdir(IntPtr path);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr write(int fd, byte[] buf, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(int fd, UIntPtr request, ref winsize w);

        [DllImport("libc", SetLastError = true)]
        public static extern int waitpid(int pid, out int status, int options);

        [DllImport("libc", SetLastError = true)]
        public static extern int kill(int pid, int sig);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int close_fd(int fd);

        [DllImport("libc", EntryPoint = "_exit")]
        public static extern void exit_now(int status);
    }
}
#endif
