using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NeonCompanion.Runtime.Core
{
    /// <summary>
    /// A single long-lived shell process (PowerShell on Windows, $SHELL/bash on Unix) that
    /// the agent drives across many commands while state persists (cwd, env vars, activated
    /// venvs). Unlike <see cref="ProcessExecutionService"/> — which spawns a fresh shell per
    /// command — this keeps one process and frames each command with a unique marker so it
    /// can return clean stdout/stderr plus an exit code.
    ///
    /// Design note: this uses redirected pipes, NOT a pty. PowerShell with redirected output
    /// emits plain text (no PSReadLine/ANSI), so output stays clean; the interactive ConPTY
    /// terminal is a separate, human-facing thing. Validated against real PowerShell:
    /// persistence, UTF-8, separate stderr, exit codes, and timeouts all behave.
    ///
    /// Commands are serialized (one at a time) via a gate. On timeout the shell is reset
    /// (killed + lazily restarted), which sacrifices state but avoids a wedged process.
    /// </summary>
    public sealed class PersistentShellService : IDisposable
    {
        private const string PromptSentinel = "###NEONPROMPT###";
        private const int DrainTimeoutMs = 8000;

        private readonly object _lock = new object();
        private readonly StringBuilder _out = new StringBuilder();
        private readonly StringBuilder _err = new StringBuilder();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private Process _proc;
        private StreamWriter _stdin;
        private bool _isWindows;
        private volatile string _marker;
        private volatile TaskCompletionSource<int> _tcs;
        private bool _disposed;

        public async Task<ProcessResult> ExecuteAsync(string command, int timeoutMs = 30000)
        {
            if (_disposed)
            {
                ProcessResult dead = new ProcessResult();
                dead.exitCode = -1;
                dead.stderr = "Persistent shell disposed";
                return dead;
            }

            await _gate.WaitAsync();
            try
            {
                if (_proc == null || _proc.HasExited)
                {
                    Start();
                    // Absorb the banner / default prompt / init echoes before the real command.
                    await RunFramed(string.Empty, DrainTimeoutMs);

                    // RunFramed resets _proc to null on timeout — bail out cleanly instead of
                    // dereferencing a dead shell.
                    if (_proc == null)
                    {
                        ProcessResult initFail = new ProcessResult();
                        initFail.exitCode = -1;
                        initFail.stderr = "Persistent shell failed to initialize";
                        return initFail;
                    }
                }
                return await RunFramed(command, timeoutMs);
            }
            catch (Exception ex)
            {
                ProcessResult err = new ProcessResult();
                err.exitCode = -1;
                err.stderr = "Persistent shell error: " + ex.Message;
                ResetLocked();
                return err;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Kill and forget the shell; the next command starts a fresh one.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                ResetLocked();
            }
        }

        private void Start()
        {
            _isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            _proc = new Process();
            if (_isWindows)
            {
                _proc.StartInfo.FileName = "powershell.exe";
                _proc.StartInfo.Arguments = "-NoProfile -NoLogo -NoExit";
            }
            else
            {
                string shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell))
                    shell = "/bin/bash";
                _proc.StartInfo.FileName = shell;
                _proc.StartInfo.Arguments = string.Empty;
            }

            _proc.StartInfo.UseShellExecute = false;
            _proc.StartInfo.RedirectStandardInput = true;
            _proc.StartInfo.RedirectStandardOutput = true;
            _proc.StartInfo.RedirectStandardError = true;
            _proc.StartInfo.CreateNoWindow = true;
            _proc.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
            _proc.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);

            _proc.Start();
            _stdin = _proc.StandardInput;

            StreamReader stdout = _proc.StandardOutput;
            StreamReader stderr = _proc.StandardError;
            Thread outThread = new Thread(delegate () { ReadLoop(stdout, false); });
            outThread.IsBackground = true;
            outThread.Name = "PShell-Out";
            outThread.Start();
            Thread errThread = new Thread(delegate () { ReadLoop(stderr, true); });
            errThread.IsBackground = true;
            errThread.Name = "PShell-Err";
            errThread.Start();

            if (_isWindows)
            {
                // UTF-8 everywhere + a unique prompt sentinel so echoed input lines can be
                // stripped (interactive PowerShell echoes stdin back to stdout).
                _stdin.WriteLine("try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}");
                _stdin.WriteLine("$OutputEncoding = [Text.Encoding]::UTF8");
                _stdin.WriteLine("$ProgressPreference = 'SilentlyContinue'");
                _stdin.WriteLine("function prompt { '" + PromptSentinel + "' }");
                _stdin.Flush();
            }
            // Non-interactive bash reads stdin without echo or prompt — no init needed.
        }

        private void ReadLoop(StreamReader reader, bool isErr)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string m = _marker;
                    if (!isErr && m != null && line.StartsWith(m))
                    {
                        string tail = line.Substring(m.Length).Trim();
                        int code;
                        if (!int.TryParse(tail, out code))
                            code = 0;

                        TaskCompletionSource<int> tcs = _tcs;
                        _tcs = null;
                        _marker = null;
                        if (tcs != null)
                            tcs.TrySetResult(code);
                        continue;
                    }

                    // Strip echoed input / prompt lines (PowerShell only).
                    if (!isErr && _isWindows && line.StartsWith(PromptSentinel))
                        continue;

                    lock (_lock)
                    {
                        if (isErr)
                            _err.Append(line).Append('\n');
                        else
                            _out.Append(line).Append('\n');
                    }
                }
            }
            catch (Exception)
            {
                // Stream closed on shutdown / reset.
            }
        }

        private async Task<ProcessResult> RunFramed(string command, int timeoutMs)
        {
            lock (_lock)
            {
                _out.Length = 0;
                _err.Length = 0;
            }

            string id = Guid.NewGuid().ToString("N");
            _marker = "<<<NEONMARK_" + id + ">>>";
            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
            _tcs = tcs;

            if (!string.IsNullOrEmpty(command))
                _stdin.WriteLine(command);

            if (_isWindows)
                _stdin.WriteLine("\"" + _marker + "\" + $LASTEXITCODE");
            else
                _stdin.WriteLine("printf '%s%s\\n' '" + _marker + "' \"$?\"");
            _stdin.Flush();

            Task delay = Task.Delay(timeoutMs);
            Task completed = await Task.WhenAny(tcs.Task, delay);

            ProcessResult result = new ProcessResult();
            lock (_lock)
            {
                result.stdout = _out.ToString().TrimEnd('\r', '\n');
                result.stderr = _err.ToString().TrimEnd('\r', '\n');
            }

            if (completed == tcs.Task)
            {
                result.exitCode = tcs.Task.Result;
            }
            else
            {
                result.timedOut = true;
                result.exitCode = -1;
                if (string.IsNullOrEmpty(result.stderr))
                    result.stderr = "[command timed out after " + timeoutMs + " ms]";
                lock (_lock)
                {
                    ResetLocked();
                }
            }

            return result;
        }

        private void ResetLocked()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                    _proc.Kill();
            }
            catch (Exception)
            {
            }

            try { if (_proc != null) _proc.Dispose(); } catch (Exception) { }

            _proc = null;
            _stdin = null;
            _marker = null;
            _tcs = null;
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_lock)
            {
                ResetLocked();
            }
        }
    }
}
