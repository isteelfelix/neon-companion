using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    public sealed class WindowsCompanionWindowService : ICompanionWindowService
    {
        private readonly object _writeLock = new object();
        private readonly ConcurrentQueue<CompanionWindowEvent> _events =
            new ConcurrentQueue<CompanionWindowEvent>();
        private readonly List<string> _monitorNames = new List<string>();

        private NamedPipeServerStream _pipe;
        private StreamWriter _writer;
        private CancellationTokenSource _cancellation;
        private Process _process;
        private CompanionDisplaySnapshot _snapshot;
        private CompanionWindowPreferences _preferences;
        private string _state = CompanionDisplayStates.Idle;
        private string _voiceText;
        private bool _stopping;
        private bool _pipeConnected;
        private bool _runtimeReady;
        private DateTime _lastHeartbeatUtc;
        private const int HandshakeTimeoutMilliseconds = 10000;

        public WindowsCompanionWindowService()
        {
            int count = Mathf.Max(1, Display.displays != null ? Display.displays.Length : 1);
            for (int i = 0; i < count; i++)
                _monitorNames.Add((i + 1).ToString());
        }

        public bool IsAvailable
        {
            get { return true; }
        }

        public bool IsRunning
        {
            get { return HasLiveProcess && _runtimeReady; }
        }

        private bool HasLiveProcess
        {
            get { return _process != null && !_process.HasExited; }
        }

        public IReadOnlyList<string> MonitorNames
        {
            get { return _monitorNames; }
        }

        public event Action<CompanionWindowEvent> EventReceived;

        public void Launch(CompanionDisplaySnapshot snapshot, CompanionWindowPreferences preferences)
        {
            _snapshot = snapshot;
            _preferences = preferences ?? new CompanionWindowPreferences();

            if (HasLiveProcess)
            {
                if (_runtimeReady)
                    SendProfileAndPreferences();
                return;
            }

            Stop();
            _stopping = false;
            _state = CompanionDisplayStates.Idle;
            string pipeName = "neon-companion-display-" + Guid.NewGuid().ToString("N");
            _cancellation = new CancellationTokenSource();
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            NamedPipeServerStream pipe = _pipe;
            CancellationTokenSource cancellation = _cancellation;
            Task.Run(() => AcceptClientAsync(pipeName, pipe, cancellation));

            try
            {
                string executable = Process.GetCurrentProcess().MainModule.FileName;
                string logDirectory = Path.Combine(Application.persistentDataPath, "Logs");
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(logDirectory, "companion-player.log");

                var start = new ProcessStartInfo();
                start.FileName = executable;
                start.WorkingDirectory = Path.GetDirectoryName(executable);
                start.UseShellExecute = false;
                start.CreateNoWindow = false;
                start.Arguments =
                    "--companion-player --companion-pipe " + Quote(pipeName) +
                    " --companion-parent-pid " + Process.GetCurrentProcess().Id +
                    " -popupwindow -screen-width 420 -screen-height 560 -logFile " + Quote(logPath);

                _process = new Process();
                _process.StartInfo = start;
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
                if (!_process.Start())
                    throw new InvalidOperationException("Windows did not start the Companion player process.");

                NeonLogger.Log("[CompanionWindow] Player process launched. pid=" + _process.Id);
            }
            catch (Exception ex)
            {
                QueueEvent(CompanionWindowEventKind.Failed, ex.Message);
                NeonLogger.LogError("[CompanionWindow] Launch failed: " + ex);
                Stop();
            }
        }

        public void SetProfile(CompanionDisplaySnapshot snapshot)
        {
            _snapshot = snapshot;
            Send(new CompanionProcessMessage { type = "profile", snapshot = snapshot });
        }

        public void SetState(string state)
        {
            _state = string.IsNullOrWhiteSpace(state) ? CompanionDisplayStates.Idle : state;
            Send(new CompanionProcessMessage { type = "state", text = _state });
        }

        public void StartVoicePlayback(string text)
        {
            _voiceText = text ?? string.Empty;
            Send(new CompanionProcessMessage { type = "voice_start", text = _voiceText });
        }

        public void ClearVoicePlayback()
        {
            _voiceText = null;
            Send(new CompanionProcessMessage { type = "voice_clear" });
        }

        public void UpdatePreferences(CompanionWindowPreferences preferences)
        {
            _preferences = preferences ?? new CompanionWindowPreferences();
            Send(new CompanionProcessMessage { type = "preferences", preferences = _preferences });
        }

        public void Show()
        {
            if (_preferences != null)
                _preferences.visible = true;
            Send(new CompanionProcessMessage { type = "show" });
        }

        public void Hide()
        {
            if (_preferences != null)
                _preferences.visible = false;
            Send(new CompanionProcessMessage { type = "hide" });
        }

        public void Stop()
        {
            _stopping = true;
            ClearVoicePlayback();
            Send(new CompanionProcessMessage { type = "state", text = CompanionDisplayStates.Stop });
            Send(new CompanionProcessMessage { type = "shutdown" });

            Process process = _process;
            _process = null;
            if (process != null)
            {
                process.Exited -= OnProcessExited;
                try
                {
                    if (!process.HasExited && !process.WaitForExit(1200))
                        process.Kill();
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning("[CompanionWindow] Cleanup warning: " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }

            ClosePipe();
        }

        public void Tick()
        {
            CompanionWindowEvent item;
            while (_events.TryDequeue(out item))
                EventReceived?.Invoke(item);
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task AcceptClientAsync(
            string pipeName,
            NamedPipeServerStream pipe,
            CancellationTokenSource cancellation)
        {
            CancellationToken token = cancellation.Token;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(HandshakeTimeoutMilliseconds);
                    await pipe.WaitForConnectionAsync(timeout.Token);
                }
                if (token.IsCancellationRequested)
                    return;

                var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                lock (_writeLock)
                {
                    if (!ReferenceEquals(_pipe, pipe))
                        return;
                    _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, true);
                    _writer.AutoFlush = true;
                }

                _pipeConnected = true;
                NeonLogger.Log("[CompanionWindow] IPC connected: " + pipeName);
                Task readTask = ReadClientAsync(reader, pipe, token);
                SendProfileAndPreferences();
                Task timeoutTask = Task.Delay(HandshakeTimeoutMilliseconds, token);
                Task completed = await Task.WhenAny(readTask, WaitForRuntimeReadyAsync(token), timeoutTask);
                if (completed == timeoutTask && !_runtimeReady)
                    throw new TimeoutException("IPC connected, but runtime-ready handshake timed out.");
                await readTask;
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested && !_stopping)
                    FailIpc("Pipe connection timed out.");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && !_stopping)
                    FailIpc(ex.Message);
            }
            finally
            {
                ClosePipe(pipe, cancellation);
            }
        }

        private async Task ReadClientAsync(
            StreamReader reader,
            NamedPipeServerStream pipe,
            CancellationToken token)
        {
            using (reader)
            {
                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                        break;
                    Receive(line);
                }
            }
        }

        private async Task WaitForRuntimeReadyAsync(CancellationToken token)
        {
            while (!_runtimeReady && !token.IsCancellationRequested)
                await Task.Delay(25, token);
        }

        private void FailIpc(string reason)
        {
            QueueEvent(CompanionWindowEventKind.Failed, "IPC failed: " + reason);
            NeonLogger.LogError("[CompanionWindow] IPC failed: " + reason);
        }

        private void Receive(string json)
        {
            CompanionProcessMessage message;
            try
            {
                message = JsonUtility.FromJson<CompanionProcessMessage>(json);
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("[CompanionWindow] Invalid child message: " + ex.Message);
                return;
            }

            if (message == null)
                return;

            switch (message.type)
            {
                case "runtime_ready":
                    if (!_runtimeReady)
                    {
                        _runtimeReady = true;
                        _lastHeartbeatUtc = DateTime.UtcNow;
                        QueueEvent(CompanionWindowEventKind.Started, "Companion runtime ready.");
                        NeonLogger.Log("[CompanionWindow] Runtime ready.");
                    }
                    break;
                case "heartbeat":
                    _lastHeartbeatUtc = DateTime.UtcNow;
                    break;
                case "open_avatar_settings":
                    QueueEvent(CompanionWindowEventKind.OpenAvatarSettings, null);
                    break;
                case "return_to_column":
                    QueueEvent(CompanionWindowEventKind.ReturnToColumn, null);
                    break;
                case "bounds":
                    _events.Enqueue(new CompanionWindowEvent
                    {
                        Kind = CompanionWindowEventKind.BoundsChanged,
                        X = message.x,
                        Y = message.y
                    });
                    break;
                case "click_through":
                    _events.Enqueue(new CompanionWindowEvent
                    {
                        Kind = CompanionWindowEventKind.ClickThroughChanged,
                        BoolValue = message.boolValue
                    });
                    break;
                case "visible":
                    _events.Enqueue(new CompanionWindowEvent
                    {
                        Kind = CompanionWindowEventKind.VisibilityChanged,
                        BoolValue = message.boolValue
                    });
                    break;
                case "pinned":
                    _events.Enqueue(new CompanionWindowEvent
                    {
                        Kind = CompanionWindowEventKind.PinnedChanged,
                        BoolValue = message.boolValue
                    });
                    break;
                case "diagnostic":
                    NeonLogger.Log("[CompanionWindow.Player] " + (message.text ?? string.Empty));
                    break;
            }
        }

        private void SendProfileAndPreferences()
        {
            Send(new CompanionProcessMessage { type = "profile", snapshot = _snapshot });
            Send(new CompanionProcessMessage { type = "preferences", preferences = _preferences });
            Send(new CompanionProcessMessage { type = "state", text = _state });
            if (!string.IsNullOrEmpty(_voiceText))
                Send(new CompanionProcessMessage { type = "voice_start", text = _voiceText });
        }

        private void Send(CompanionProcessMessage message)
        {
            if (message == null)
                return;

            lock (_writeLock)
            {
                if (_writer == null)
                    return;
                try
                {
                    _writer.WriteLine(JsonUtility.ToJson(message));
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                        NeonLogger.LogWarning("[CompanionWindow] IPC send failed: " + ex.Message);
                }
            }
        }

        private void OnProcessExited(object sender, EventArgs args)
        {
            if (!ReferenceEquals(sender, _process))
                return;

            int exitCode = -1;
            try
            {
                var process = sender as Process;
                if (process != null)
                    exitCode = process.ExitCode;
            }
            catch
            {
            }

            if (!_stopping)
            {
                QueueEvent(CompanionWindowEventKind.Closed, "Player exited with code " + exitCode + ".");
                NeonLogger.LogWarning("[CompanionWindow] Player exited independently. code=" + exitCode);
            }
            ClosePipe();
        }

        private void QueueEvent(CompanionWindowEventKind kind, string message)
        {
            _events.Enqueue(new CompanionWindowEvent { Kind = kind, Message = message });
        }

        private void ClosePipe(
            NamedPipeServerStream expectedPipe = null,
            CancellationTokenSource expectedCancellation = null)
        {
            if (expectedPipe != null && !ReferenceEquals(expectedPipe, _pipe))
                return;
            if (expectedCancellation != null &&
                !ReferenceEquals(expectedCancellation, _cancellation))
                return;

            CancellationTokenSource cancellation = _cancellation;
            _cancellation = null;
            try
            {
                if (cancellation != null)
                    cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            lock (_writeLock)
            {
                StreamWriter writer = _writer;
                NamedPipeServerStream pipe = _pipe;
                _writer = null;
                _pipe = null;
                try
                {
                    if (writer != null)
                        writer.Dispose();
                }
                catch (Exception)
                {
                }
                finally
                {
                    try
                    {
                        if (pipe != null)
                            pipe.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            if (cancellation != null)
                cancellation.Dispose();
            _pipeConnected = false;
            _runtimeReady = false;
            _lastHeartbeatUtc = DateTime.MinValue;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
#endif
}
