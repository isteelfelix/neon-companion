#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NeonCompanion.Runtime.Terminal
{
    /// <summary>
    /// Windows pseudo console (ConPTY) session. Spawns a real shell attached to a
    /// pseudo console and streams raw VT bytes both ways. Requires Windows 10 1809+.
    ///
    /// Lifecycle: two input/output pipes are created, handed to CreatePseudoConsole,
    /// and the child is launched via CreateProcess with a PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
    /// attribute so it inherits the pseudo console as its console. A dedicated thread
    /// drains the output pipe; another waits for process exit.
    /// </summary>
    public sealed class ConPtySession : IPtySession
    {
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _attrList = IntPtr.Zero;
        private SafeFileHandle _inputWrite;
        private SafeFileHandle _outputRead;
        private FileStream _inputStream;
        private FileStream _outputStream;
        private NativePtyWindows.PROCESS_INFORMATION _procInfo;
        private Thread _readThread;
        private Thread _waitThread;
        private volatile bool _closed;
        private readonly object _writeLock = new object();
        private readonly object _disposeLock = new object();

        public event Action<byte[]> OutputReceived;
        public event Action<int> Exited;

        public bool IsAlive
        {
            get { return !_closed; }
        }

        /// <summary>
        /// Launches the shell. Throws <see cref="InvalidOperationException"/> with the
        /// failing Win32 call + error code on any native failure.
        /// </summary>
        public void Start(string shellCommandLine, string workingDirectory, int columns, int rows)
        {
            SafeFileHandle inputRead = null;
            SafeFileHandle outputWrite = null;

            try
            {
                if (!NativePtyWindows.CreatePipe(out inputRead, out _inputWrite, IntPtr.Zero, 0))
                    throw Fail("CreatePipe(input)");
                if (!NativePtyWindows.CreatePipe(out _outputRead, out outputWrite, IntPtr.Zero, 0))
                    throw Fail("CreatePipe(output)");

                NativePtyWindows.COORD size = new NativePtyWindows.COORD();
                size.X = (short)Math.Max(1, columns);
                size.Y = (short)Math.Max(1, rows);

                int hr = NativePtyWindows.CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);
                if (hr != 0)
                    throw new InvalidOperationException("CreatePseudoConsole failed: HRESULT 0x" + hr.ToString("X8"));

                // The pseudo console owns its ends now; we keep the opposite ends only.
                inputRead.Dispose();
                inputRead = null;
                outputWrite.Dispose();
                outputWrite = null;

                StartChildProcess(shellCommandLine, workingDirectory);

                _inputStream = new FileStream(_inputWrite, FileAccess.Write, 4096, false);
                _outputStream = new FileStream(_outputRead, FileAccess.Read, 4096, false);

                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Name = "ConPty-Read";
                _readThread.Start();

                _waitThread = new Thread(WaitLoop);
                _waitThread.IsBackground = true;
                _waitThread.Name = "ConPty-Wait";
                _waitThread.Start();
            }
            catch
            {
                if (inputRead != null) inputRead.Dispose();
                if (outputWrite != null) outputWrite.Dispose();
                Dispose();
                throw;
            }
        }

        private void StartChildProcess(string shellCommandLine, string workingDirectory)
        {
            NativePtyWindows.STARTUPINFOEX startupInfoEx = new NativePtyWindows.STARTUPINFOEX();
            startupInfoEx.StartupInfo.cb = Marshal.SizeOf(typeof(NativePtyWindows.STARTUPINFOEX));

            // First call sizes the attribute list, second initializes it.
            IntPtr attrSize = IntPtr.Zero;
            NativePtyWindows.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            _attrList = Marshal.AllocHGlobal(attrSize);
            startupInfoEx.lpAttributeList = _attrList;

            if (!NativePtyWindows.InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrSize))
                throw Fail("InitializeProcThreadAttributeList");

            if (!NativePtyWindows.UpdateProcThreadAttribute(
                    _attrList,
                    0,
                    NativePtyWindows.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw Fail("UpdateProcThreadAttribute");

            NativePtyWindows.SECURITY_ATTRIBUTES secProcess = new NativePtyWindows.SECURITY_ATTRIBUTES();
            NativePtyWindows.SECURITY_ATTRIBUTES secThread = new NativePtyWindows.SECURITY_ATTRIBUTES();
            secProcess.nLength = Marshal.SizeOf(typeof(NativePtyWindows.SECURITY_ATTRIBUTES));
            secThread.nLength = secProcess.nLength;

            string cwd = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;

            bool ok = NativePtyWindows.CreateProcess(
                null,
                shellCommandLine,
                ref secProcess,
                ref secThread,
                false, // ConPTY handles are passed via attribute, not inherited
                NativePtyWindows.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                cwd,
                ref startupInfoEx,
                out _procInfo);

            if (!ok)
                throw Fail("CreateProcess");
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_closed)
                {
                    int read = _outputStream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    byte[] chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);

                    Action<byte[]> handler = OutputReceived;
                    if (handler != null)
                        handler(chunk);
                }
            }
            catch (Exception)
            {
                // Pipe closed / process gone — normal on shutdown.
            }
        }

        private void WaitLoop()
        {
            uint waitResult = 0xFFFFFFFF;
            try
            {
                waitResult = NativePtyWindows.WaitForSingleObject(_procInfo.hProcess, NativePtyWindows.INFINITE);
            }
            catch (Exception)
            {
            }

            if (_closed)
                return;

            // WAIT_OBJECT_0 == 0. Anything else (WAIT_FAILED 0xFFFFFFFF etc.) means we did NOT
            // observe a real exit — don't kill the session on a bad wait.
            if (waitResult != 0)
            {
                UnityEngine.Debug.LogWarning("[ConPty] WaitForSingleObject returned 0x" +
                                             waitResult.ToString("X8") + " err=" + Marshal.GetLastWin32Error() +
                                             " — not reporting exit");
                return;
            }

            int exitCode = 0;
            try
            {
                uint code;
                if (NativePtyWindows.GetExitCodeProcess(_procInfo.hProcess, out code))
                    exitCode = (int)code;
            }
            catch (Exception)
            {
            }

            Action<int> handler = Exited;
            if (handler != null)
                handler(exitCode);
        }

        public void Write(byte[] data)
        {
            if (_closed || data == null || data.Length == 0)
                return;

            lock (_writeLock)
            {
                try
                {
                    if (_inputStream != null)
                    {
                        _inputStream.Write(data, 0, data.Length);
                        _inputStream.Flush();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            if (_closed || _hPC == IntPtr.Zero)
                return;

            NativePtyWindows.COORD size = new NativePtyWindows.COORD();
            size.X = (short)Math.Max(1, columns);
            size.Y = (short)Math.Max(1, rows);
            NativePtyWindows.ResizePseudoConsole(_hPC, size);
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

            // Closing the pseudo console closes the write side of the output pipe, which
            // unblocks the read loop and signals the child to exit.
            try { if (_hPC != IntPtr.Zero) NativePtyWindows.ClosePseudoConsole(_hPC); } catch (Exception) { }
            _hPC = IntPtr.Zero;

            try { if (_inputStream != null) _inputStream.Dispose(); } catch (Exception) { }
            try { if (_outputStream != null) _outputStream.Dispose(); } catch (Exception) { }

            try
            {
                if (_procInfo.hThread != IntPtr.Zero)
                    NativePtyWindows.CloseHandle(_procInfo.hThread);
                if (_procInfo.hProcess != IntPtr.Zero)
                    NativePtyWindows.CloseHandle(_procInfo.hProcess);
            }
            catch (Exception)
            {
            }

            try
            {
                if (_attrList != IntPtr.Zero)
                {
                    NativePtyWindows.DeleteProcThreadAttributeList(_attrList);
                    Marshal.FreeHGlobal(_attrList);
                }
            }
            catch (Exception)
            {
            }
            _attrList = IntPtr.Zero;
        }

        private static InvalidOperationException Fail(string call)
        {
            int err = Marshal.GetLastWin32Error();
            return new InvalidOperationException(call + " failed: Win32 error " + err);
        }
    }
}
#endif
