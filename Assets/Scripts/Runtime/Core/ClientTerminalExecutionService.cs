using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NeonCompanion.Runtime.Core
{
    /// <summary>
    /// Executes commands requested by a remote Hermes agent on this Companion host.
    /// Permission grants and persistent shells are scoped to one chat session and live only
    /// for the current app/backend connection.
    /// </summary>
    public sealed class ClientTerminalExecutionService : IDisposable
    {
        private const int DefaultTimeoutMs = 30000;
        private const int MinTimeoutMs = 1000;
        private const int MaxTimeoutMs = 600000;
        private const int MaxOutputChars = 524288;

        private readonly ProcessExecutionService _processExecution;
        private readonly object _sync = new object();
        private readonly HashSet<string> _approvedSessions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, PersistentShellService> _persistentShells =
            new Dictionary<string, PersistentShellService>(StringComparer.Ordinal);
        private bool _disposed;

        public ClientTerminalExecutionService(ProcessExecutionService processExecution)
        {
            _processExecution = processExecution ?? throw new ArgumentNullException(nameof(processExecution));
        }

        public bool HasSessionGrant(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;

            lock (_sync)
            {
                return !_disposed && _approvedSessions.Contains(sessionId);
            }
        }

        public void GrantSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            lock (_sync)
            {
                if (!_disposed)
                    _approvedSessions.Add(sessionId);
            }
        }

        public void RevokeSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            PersistentShellService shell = null;
            lock (_sync)
            {
                _approvedSessions.Remove(sessionId);
                if (_persistentShells.TryGetValue(sessionId, out shell))
                    _persistentShells.Remove(sessionId);
            }

            if (shell != null)
                shell.Dispose();
        }

        /// <summary>
        /// Drops all ephemeral grants and agent shells. Call this when the Hermes socket changes:
        /// a replacement backend must never inherit authority granted to the previous connection.
        /// </summary>
        public void ResetConnection()
        {
            List<PersistentShellService> shells;
            lock (_sync)
            {
                _approvedSessions.Clear();
                shells = new List<PersistentShellService>(_persistentShells.Values);
                _persistentShells.Clear();
            }

            for (int i = 0; i < shells.Count; i++)
                shells[i].Dispose();
        }

        public async Task<ProcessResult> ExecuteAsync(
            string sessionId,
            string command,
            int timeoutMs,
            bool persistent)
        {
            if (_disposed)
                return Error("Client terminal service is disposed.");
            if (string.IsNullOrEmpty(sessionId))
                return Error("Missing client session id.");
            if (string.IsNullOrWhiteSpace(command))
                return Error("Command is empty.");

            int boundedTimeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            boundedTimeout = Math.Max(MinTimeoutMs, Math.Min(MaxTimeoutMs, boundedTimeout));

            ProcessResult result;
            if (persistent)
            {
                PersistentShellService shell = GetOrCreateShell(sessionId);
                if (shell == null)
                    return Error("Persistent client shell is unavailable.");
                result = await shell.ExecuteAsync(command, boundedTimeout);
            }
            else
            {
                result = await _processExecution.ExecuteAsync(command, boundedTimeout);
            }

            TruncateOutput(result);
            return result;
        }

        private PersistentShellService GetOrCreateShell(string sessionId)
        {
            lock (_sync)
            {
                if (_disposed)
                    return null;

                PersistentShellService shell;
                if (!_persistentShells.TryGetValue(sessionId, out shell))
                {
                    shell = new PersistentShellService();
                    _persistentShells[sessionId] = shell;
                }
                return shell;
            }
        }

        private static void TruncateOutput(ProcessResult result)
        {
            if (result == null)
                return;

            result.stdout = Truncate(result.stdout);
            result.stderr = Truncate(result.stderr);
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxOutputChars)
                return value ?? string.Empty;
            return value.Substring(0, MaxOutputChars) + "\n[output truncated by Companion]";
        }

        private static ProcessResult Error(string message)
        {
            return new ProcessResult
            {
                exitCode = -1,
                stdout = string.Empty,
                stderr = message ?? string.Empty
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ResetConnection();
        }
    }
}
