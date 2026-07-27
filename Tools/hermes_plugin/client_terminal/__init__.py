"""Hermes tool for the connection-bound Neon Companion terminal broker."""
from __future__ import annotations

import json
import os
import socket
from pathlib import Path
from typing import Any

SOCKET_PATH = Path(
    os.environ.get(
        "NEON_CLIENT_TERMINAL_SOCKET",
        "/home/hermes/.hermes/run/client-terminal.sock",
    )
)


def _unavailable(message: str, code: str = "service_unavailable") -> str:
    return json.dumps(
        {
            "status": "error",
            "stdout": "",
            "stderr": message,
            "exit_code": None,
            "timed_out": False,
            "error_code": code,
        },
        ensure_ascii=False,
    )


def client_terminal(args: dict[str, Any], **kwargs: Any) -> str:
    command = str(args.get("command") or "")
    if not command.strip():
        return _unavailable("command must not be empty")
    session_id = str(kwargs.get("session_id") or "").strip()
    if not session_id:
        return _unavailable("No active session is available", "inactive_session")

    try:
        timeout_ms = int(args.get("timeout_ms") or 30_000)
    except (TypeError, ValueError):
        timeout_ms = 30_000
    timeout_ms = max(1000, min(timeout_ms, 600_000))
    request = {
        "operation": "execute",
        "session_id": session_id,
        "command": command,
        "timeout_ms": timeout_ms,
        "persistent": bool(args.get("persistent", False)),
    }

    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
            sock.settimeout((timeout_ms / 1000.0) + 20.0)
            sock.connect(str(SOCKET_PATH))
            sock.sendall((json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
            chunks: list[bytes] = []
            total = 0
            while True:
                chunk = sock.recv(65_536)
                if not chunk:
                    break
                total += len(chunk)
                if total > 1_100_000:
                    return _unavailable("Broker response exceeded the safety limit")
                chunks.append(chunk)
        raw = b"".join(chunks).decode("utf-8").strip()
        parsed = json.loads(raw)
        if not isinstance(parsed, dict):
            raise ValueError("broker returned a non-object")
        return json.dumps(parsed, ensure_ascii=False)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        return _unavailable(f"Companion broker unavailable: {type(exc).__name__}")


def check_requirements() -> bool:
    # Keep the schema available across brief broker restarts. The handler itself
    # fails closed with service_unavailable when the private socket is absent.
    return True


def register(ctx: Any) -> None:
    ctx.register_tool(
        name="client_terminal",
        toolset="client_terminal",
        description="Run a shell command on the user's connected Neon Companion device.",
        emoji="🖥️",
        check_fn=check_requirements,
        schema={
            "name": "client_terminal",
            "description": (
                "Run a shell command on the user's connected Neon Companion device. "
                "This is distinct from terminal, which runs on the Hermes host. "
                "The Companion asks the local user for permission before execution."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "command": {"type": "string"},
                    "timeout_ms": {
                        "type": "integer",
                        "minimum": 1000,
                        "maximum": 600000,
                    },
                    "persistent": {
                        "type": "boolean",
                        "description": "Reuse a shell scoped to this chat so cwd/env/venv state persists.",
                    },
                },
                "required": ["command"],
            },
        },
        handler=client_terminal,
    )
