#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace NeonCompanion.Runtime.Terminal
{
    /// <summary>
    /// macOS/Linux pseudo terminal session via forkpty. Spawns a shell on a new pty and
    /// streams raw bytes both ways over the master fd.
    ///
    /// UNTESTED on device: this mirrors the well-worn forkpty pattern (python-pty, node-pty)
    /// but could not be exercised from the Windows dev box. The one fragile spot is the child
    /// branch after forkpty — only pre-marshalled, async-signal-safe libc calls (chdir/execv)
    /// run there. If it proves flaky in practice, the robust alternative is a posix_spawn +
    /// openpt implementation that keeps all child setup in native code.
    /// </summary>
    public sealed class UnixPtySession : IPtySession
    {
        private int _masterFd = -1;
        private int _pid = -1;
        private bool _isLinux;

        private Thread _readThread;
        private Thread _waitThread;
        private volatile bool _closed;
        private readonly object _writeLock = new object();
        private readonly object _disposeLock = new object();

        public event Action<byte[]> OutputReceived;
        public event Action<int> Exited;

        public bool IsAlive
        {
            get { return !_closed && _masterFd >= 0; }
        }

        public void Start(string shell, string workingDirectory, int columns, int rows)
        {
            _isLinux = Application.platform == RuntimePlatform.LinuxPlayer ||
                       Application.platform == RuntimePlatform.LinuxEditor;

            // Marshal everything the child needs BEFORE forking — no managed allocation may
            // happen in the child between fork and exec.
            IntPtr shellPtr = Marshal.StringToHGlobalAnsi(shell);
            IntPtr cwdPtr = string.IsNullOrEmpty(workingDirectory)
                ? IntPtr.Zero
                : Marshal.StringToHGlobalAnsi(workingDirectory);

            IntPtr[] argvManaged = new IntPtr[] { shellPtr, IntPtr.Zero };
            IntPtr argvPtr = Marshal.AllocHGlobal(IntPtr.Size * argvManaged.Length);
            Marshal.Copy(argvManaged, 0, argvPtr, argvManaged.Length);

            NativePtyUnix.winsize ws = new NativePtyUnix.winsize();
            ws.ws_row = (ushort)Math.Max(1, rows);
            ws.ws_col = (ushort)Math.Max(1, columns);
            ws.ws_xpixel = 0;
            ws.ws_ypixel = 0;

            int amaster;
            int pid = _isLinux
                ? NativePtyUnix.forkpty_linux(out amaster, IntPtr.Zero, IntPtr.Zero, ref ws)
                : NativePtyUnix.forkpty_mac(out amaster, IntPtr.Zero, IntPtr.Zero, ref ws);

            if (pid < 0)
                throw new InvalidOperationException("forkpty failed: errno " + Marshal.GetLastWin32Error());

            if (pid == 0)
            {
                // CHILD — replace the image with the shell. Keep this path allocation-free.
                if (cwdPtr != IntPtr.Zero)
                    NativePtyUnix.chdir(cwdPtr);
                NativePtyUnix.execv(shellPtr, argvPtr);
                NativePtyUnix.exit_now(127); // exec failed
                return;
            }

            // PARENT.
            _pid = pid;
            _masterFd = amaster;

            _readThread = new Thread(ReadLoop);
            _readThread.IsBackground = true;
            _readThread.Name = "UnixPty-Read";
            _readThread.Start();

            _waitThread = new Thread(WaitLoop);
            _waitThread.IsBackground = true;
            _waitThread.Name = "UnixPty-Wait";
            _waitThread.Start();
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_closed)
                {
                    long n = (long)NativePtyUnix.read(_masterFd, buffer, (IntPtr)buffer.Length);
                    if (n <= 0)
                        break;

                    byte[] chunk = new byte[n];
                    Array.Copy(buffer, chunk, (int)n);

                    Action<byte[]> handler = OutputReceived;
                    if (handler != null)
                        handler(chunk);
                }
            }
            catch (Exception)
            {
            }
        }

        private void WaitLoop()
        {
            int status;
            try
            {
                NativePtyUnix.waitpid(_pid, out status, 0);
            }
            catch (Exception)
            {
                status = 0;
            }

            if (_closed)
                return;

            int exitCode = (status >> 8) & 0xff; // WEXITSTATUS

            Action<int> handler = Exited;
            if (handler != null)
                handler(exitCode);
        }

        public void Write(byte[] data)
        {
            if (_closed || _masterFd < 0 || data == null || data.Length == 0)
                return;

            lock (_writeLock)
            {
                try
                {
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        byte[] slice;
                        if (offset == 0)
                        {
                            slice = data;
                        }
                        else
                        {
                            slice = new byte[data.Length - offset];
                            Array.Copy(data, offset, slice, 0, slice.Length);
                        }

                        long written = (long)NativePtyUnix.write(_masterFd, slice, (IntPtr)slice.Length);
                        if (written <= 0)
                            break;
                        offset += (int)written;
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            if (_closed || _masterFd < 0)
                return;

            NativePtyUnix.winsize ws = new NativePtyUnix.winsize();
            ws.ws_row = (ushort)Math.Max(1, rows);
            ws.ws_col = (ushort)Math.Max(1, columns);
            ws.ws_xpixel = 0;
            ws.ws_ypixel = 0;

            UIntPtr request = (UIntPtr)(_isLinux ? NativePtyUnix.TIOCSWINSZ_LINUX : NativePtyUnix.TIOCSWINSZ_MAC);
            NativePtyUnix.ioctl(_masterFd, request, ref ws);
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_closed)
                    return;
                _closed = true;
            }

            try
            {
                if (_pid > 0)
                    NativePtyUnix.kill(_pid, NativePtyUnix.SIGHUP);
            }
            catch (Exception)
            {
            }

            try
            {
                if (_masterFd >= 0)
                    NativePtyUnix.close_fd(_masterFd);
            }
            catch (Exception)
            {
            }
            _masterFd = -1;
        }
    }
}
#endif
