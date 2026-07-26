// AgentTerminalStream.cs - Live output buffers for backend background-process terminals.

using System;
using System.Collections.Generic;
using System.Text;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>
    /// Buffers <c>agent.terminal.output</c> chunks per background process, scoped to the chat that
    /// owns the process. Port of Desktop's <c>agent-terminal-stream.ts</c>, minus the xterm writer
    /// registry: Companion has no agent-terminal tabs yet, so the live path is the transport's
    /// C# event and this store is the replay/backlog half.
    ///
    /// The process id is the authoritative key. The backend routes a chunk to the session that
    /// owns the process, but emits an EMPTY session_id whenever it cannot resolve that owner —
    /// which happens for <c>terminal.close</c> after the process is pruned, and after a reconnect
    /// re-keys the gateway's session table. Binding process → session on first sight and reusing
    /// that binding for unscoped events keeps chunks in the chat they belong to instead of
    /// spilling into whichever chat happens to be focused.
    ///
    /// Not thread-safe by design: HermesGateway dispatches events on the captured Unity
    /// SynchronizationContext, so every call lands on the main thread, like the rest of the
    /// transport's per-session state.
    /// </summary>
    public sealed class AgentTerminalStream
    {
        /// <summary>Per-process backlog cap, matching Desktop MAX_BACKLOG.</summary>
        public const int MaxBacklogChars = 256000;

        // process id -> display session id that owns it.
        private readonly Dictionary<string, string> _ownerByProcess = new Dictionary<string, string>();
        // display session id -> (process id -> buffered output).
        private readonly Dictionary<string, Dictionary<string, StringBuilder>> _bufferBySession =
            new Dictionary<string, Dictionary<string, StringBuilder>>();

        /// <summary>
        /// Resolve the chat a process belongs to. <paramref name="eventSessionId"/> is the id the
        /// event carried (may be null/empty); <paramref name="fallbackSessionId"/> is used only
        /// when the process has never been seen. An explicit id always wins and re-binds, so a
        /// post-reconnect re-key follows the process instead of stranding it.
        /// </summary>
        public string ResolveOwner(string processId, string eventSessionId, string fallbackSessionId)
        {
            if (string.IsNullOrEmpty(processId))
                return string.IsNullOrEmpty(eventSessionId) ? fallbackSessionId : eventSessionId;

            if (!string.IsNullOrEmpty(eventSessionId))
            {
                Rebind(processId, eventSessionId);
                return eventSessionId;
            }

            string known;
            if (_ownerByProcess.TryGetValue(processId, out known) && !string.IsNullOrEmpty(known))
                return known;

            if (!string.IsNullOrEmpty(fallbackSessionId))
                _ownerByProcess[processId] = fallbackSessionId;
            return fallbackSessionId;
        }

        /// <summary>Append a chunk to a process's backlog, tail-trimming to the cap.</summary>
        public void Append(string sessionId, string processId, string chunk)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(processId) || string.IsNullOrEmpty(chunk))
                return;

            Dictionary<string, StringBuilder> byProcess;
            if (!_bufferBySession.TryGetValue(sessionId, out byProcess))
            {
                byProcess = new Dictionary<string, StringBuilder>();
                _bufferBySession[sessionId] = byProcess;
            }

            StringBuilder buffer;
            if (!byProcess.TryGetValue(processId, out buffer))
            {
                buffer = new StringBuilder();
                byProcess[processId] = buffer;
            }

            buffer.Append(chunk);
            if (buffer.Length > MaxBacklogChars)
                buffer.Remove(0, buffer.Length - MaxBacklogChars);
        }

        /// <summary>Buffered output for a process, or an empty string when there is none.</summary>
        public string Read(string sessionId, string processId)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(processId))
                return string.Empty;

            Dictionary<string, StringBuilder> byProcess;
            StringBuilder buffer;
            if (_bufferBySession.TryGetValue(sessionId, out byProcess) && byProcess.TryGetValue(processId, out buffer))
                return buffer.ToString();
            return string.Empty;
        }

        /// <summary>Process ids that currently hold buffered output for a chat.</summary>
        public List<string> ProcessIds(string sessionId)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(sessionId))
                return ids;

            Dictionary<string, StringBuilder> byProcess;
            if (_bufferBySession.TryGetValue(sessionId, out byProcess))
                ids.AddRange(byProcess.Keys);
            return ids;
        }

        /// <summary>
        /// Drop a process's view (the <c>close_terminal</c> tool). Returns true when something was
        /// actually buffered, so the caller can tell a real close from a stale/duplicate one.
        /// </summary>
        public bool Close(string sessionId, string processId)
        {
            if (string.IsNullOrEmpty(processId))
                return false;

            _ownerByProcess.Remove(processId);

            Dictionary<string, StringBuilder> byProcess;
            if (string.IsNullOrEmpty(sessionId) || !_bufferBySession.TryGetValue(sessionId, out byProcess))
                return false;

            bool removed = byProcess.Remove(processId);
            if (byProcess.Count == 0)
                _bufferBySession.Remove(sessionId);
            return removed;
        }

        /// <summary>Drop every buffer belonging to a chat that is being closed or unmapped.</summary>
        public void ForgetSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            _bufferBySession.Remove(sessionId);

            var orphans = new List<string>();
            foreach (var pair in _ownerByProcess)
            {
                if (pair.Value == sessionId)
                    orphans.Add(pair.Key);
            }
            for (int i = 0; i < orphans.Count; i++)
                _ownerByProcess.Remove(orphans[i]);
        }

        /// <summary>Drop everything (profile switch / transport teardown).</summary>
        public void Clear()
        {
            _ownerByProcess.Clear();
            _bufferBySession.Clear();
        }

        private void Rebind(string processId, string sessionId)
        {
            string previous;
            if (_ownerByProcess.TryGetValue(processId, out previous)
                && !string.IsNullOrEmpty(previous)
                && previous != sessionId)
            {
                // The owner moved (a reconnect re-keyed the session): carry the backlog over so the
                // tail already streamed is not lost, and leave no copy behind under the stale id.
                string carried = Read(previous, processId);
                Close(previous, processId);
                if (!string.IsNullOrEmpty(carried))
                    Append(sessionId, processId, carried);
            }

            _ownerByProcess[processId] = sessionId;
        }
    }
}
