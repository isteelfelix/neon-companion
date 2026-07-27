# 23 — Companion Client Terminal Protocol

This is the backend contract required by `neon-companion` protocol v2. It lets a
Hermes agent execute a command on the user's Companion device without confusing
that execution with Hermes' own server-side `terminal` tool.

## Agent tool

Expose a separate tool named `client_terminal`:

```json
{
  "name": "client_terminal",
  "description": "Run a shell command on the user's connected Neon Companion device.",
  "parameters": {
    "type": "object",
    "properties": {
      "command": { "type": "string" },
      "timeout_ms": { "type": "integer", "minimum": 1000, "maximum": 600000 },
      "persistent": {
        "type": "boolean",
        "description": "Reuse a shell scoped to this chat so cwd/env/venv state persists."
      }
    },
    "required": ["command"]
  }
}
```

Do not alias or replace the existing Hermes `terminal` tool:

- `terminal` executes on the Hermes host.
- `client_terminal` executes on the connected Companion host.
- `read_terminal` reads the user's visible Companion PTY.

The backend must not add a second per-command approval prompt. Companion performs
the authoritative local approval before execution.

## 1. Client registration

Implement the JSON-RPC method `client.register`. Companion calls it after
`gateway.ready`:

```json
{
  "jsonrpc": "2.0",
  "id": "rpc-id",
  "method": "client.register",
  "params": {
    "client_id": "stable-installation-uuid",
    "instance_id": "per-process-uuid",
    "name": "neon-companion",
    "protocol_version": 1,
    "platform": {
      "os": "windows",
      "shell": "powershell"
    },
    "capabilities": {
      "terminal": {
        "version": 2,
        "streaming": false,
        "cancel": false,
        "persistent": true,
        "session_grants": true,
        "timeout_ms_max": 600000,
        "output_chars_max": 524288
      }
    }
  }
}
```

Reply:

```json
{
  "jsonrpc": "2.0",
  "id": "rpc-id",
  "result": {
    "accepted": true,
    "protocol_version": 1
  }
}
```

Store the registration against the authenticated WebSocket connection. Remove it
immediately when that socket closes. The model must never select an arbitrary
`client_id`; `client_terminal` always targets the client bound to the session's
current connection.

## 2. Execute request

When `client_terminal` runs, create a cryptographically random `request_id`, put a
pending future in a server-side map, and push this gateway event on the same
WebSocket:

```json
{
  "jsonrpc": "2.0",
  "method": "event",
  "params": {
    "type": "terminal.execute",
    "session_id": "runtime-session-id",
    "payload": {
      "request_id": "random-request-id",
      "command": "Get-ChildItem",
      "timeout_ms": 30000,
      "persistent": false
    }
  }
}
```

Requirements:

- `session_id` is mandatory and must belong to that connection.
- Reject an empty command before sending the event.
- Clamp timeout to the advertised client maximum.
- Only one completion is accepted for each `request_id`.
- On disconnect, fail every pending request for that client.
- Backend wait timeout should be `command timeout + 15 seconds` for local approval
  and transport overhead.

## 3. Result RPC

Implement `terminal.respond`. Companion sends it as a normal JSON-RPC request:

```json
{
  "jsonrpc": "2.0",
  "id": "rpc-id",
  "method": "terminal.respond",
  "params": {
    "request_id": "random-request-id",
    "status": "completed",
    "stdout": "...",
    "stderr": "",
    "exit_code": 0,
    "timed_out": false,
    "duration_ms": 183,
    "error_code": null
  }
}
```

Allowed statuses:

| Status | Meaning |
|---|---|
| `completed` | Process finished; `exit_code` may still be non-zero |
| `timed_out` | Companion killed/reset the command after its timeout |
| `denied` | Local user or local session policy rejected execution |
| `error` | Companion could not start or service the command |

Known `error_code` values: `user_denied`, `inactive_session`, `timeout`,
`service_unavailable`.

Validate that `request_id` belongs to the same registered socket and session,
resolve its pending future, delete it from the map, then acknowledge:

```json
{
  "jsonrpc": "2.0",
  "id": "rpc-id",
  "result": { "accepted": true }
}
```

The tool result returned to the agent should preserve `status`, `stdout`,
`stderr`, `exit_code`, and `timed_out`. A denial is a normal tool result, not a
gateway exception.

## Local permission behavior

Companion owns the permission state:

- `Run once` authorizes one request.
- `Allow for session` authorizes later requests for that chat without more
  prompts.
- `Reject` returns `status=denied`.
- Grants are memory-only and are cleared on disconnect, backend switch, or app
  restart.
- Persistent shells are separate per chat session.

The backend must not persist or infer a local grant.
