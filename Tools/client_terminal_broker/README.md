# Client Terminal Broker

A narrow WebSocket proxy that adds protocol-v2 `client.register`, `terminal.execute`, and `terminal.respond` to an unmodified Hermes gateway.

## Security boundary

- External clients still authenticate with the original Hermes cookie/ticket/token.
- A command is routed only to the registered WebSocket that owns the requested session.
- Plugin communication uses a same-UID Unix socket (`0600`).
- Commands and output are never logged.
- Companion owns local approval and memory-only session grants.
- Unknown, duplicate, disconnected, or ambiguous requests fail closed.

## Components

- `broker.py`: `/api/ws` transparent proxy plus the three intercepted RPC messages.
- `../hermes_plugin/client_terminal`: Hermes plugin exposing the separate `client_terminal` tool.
- `neon-client-terminal-broker.service`: hardened systemd unit used on neon-vps.
- `test_broker.py`: state, Unix-socket/plugin, and real WebSocket-proxy round-trip tests.

The normal Hermes `terminal` tool remains server-side and is not replaced or aliased.
