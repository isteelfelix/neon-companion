#!/usr/bin/env python3
"""Connection-bound Neon Companion terminal bridge for an unmodified Hermes gateway."""
from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import secrets
import signal
import socket
import stat
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit, urlunsplit

from aiohttp import ClientSession, ClientTimeout, WSMsgType, web

LOG = logging.getLogger("client-terminal-broker")
ALLOWED_STATUSES = {"completed", "timed_out", "denied", "error"}
DEFAULT_TIMEOUT_MS = 30_000
GLOBAL_TIMEOUT_MAX_MS = 600_000
GLOBAL_OUTPUT_MAX = 524_288


@dataclass(eq=False)
class ClientConnection:
    websocket: web.WebSocketResponse
    registration: dict[str, Any] | None = None
    session_ids: set[str] = field(default_factory=set)
    session_aliases: dict[str, str] = field(default_factory=dict)
    rpc_methods: dict[str, str] = field(default_factory=dict)
    pending_ids: set[str] = field(default_factory=set)
    send_lock: asyncio.Lock = field(default_factory=asyncio.Lock)

    async def send_json(self, payload: dict[str, Any]) -> None:
        async with self.send_lock:
            await self.websocket.send_str(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))


@dataclass
class PendingExecution:
    connection: ClientConnection
    session_id: str
    future: asyncio.Future[dict[str, Any]]


class BrokerState:
    def __init__(self) -> None:
        self.connections: set[ClientConnection] = set()
        self.pending: dict[str, PendingExecution] = {}
        self._lock = asyncio.Lock()

    async def add_connection(self, connection: ClientConnection) -> None:
        async with self._lock:
            self.connections.add(connection)

    async def remove_connection(self, connection: ClientConnection) -> None:
        async with self._lock:
            self.connections.discard(connection)
            doomed = [request_id for request_id, item in self.pending.items() if item.connection is connection]
            for request_id in doomed:
                item = self.pending.pop(request_id)
                connection.pending_ids.discard(request_id)
                if not item.future.done():
                    item.future.set_result(self._error_result("service_unavailable", "Companion disconnected"))
        connection.registration = None
        connection.session_ids.clear()
        connection.session_aliases.clear()
        connection.rpc_methods.clear()

    @staticmethod
    def _rpc_ok(rid: Any, result: dict[str, Any]) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": rid, "result": result}

    @staticmethod
    def _rpc_error(rid: Any, code: int, message: str) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": rid, "error": {"code": code, "message": message}}

    @staticmethod
    def _error_result(code: str, message: str) -> dict[str, Any]:
        return {
            "status": "error",
            "stdout": "",
            "stderr": message,
            "exit_code": None,
            "timed_out": False,
            "error_code": code,
        }

    async def handle_client_rpc(self, connection: ClientConnection, payload: dict[str, Any]) -> bool:
        method = payload.get("method")
        if method == "client.register":
            await connection.send_json(self._register(connection, payload))
            return True
        if method == "terminal.respond":
            await connection.send_json(await self._terminal_respond(connection, payload))
            return True
        return False

    def _register(self, connection: ClientConnection, payload: dict[str, Any]) -> dict[str, Any]:
        rid = payload.get("id")
        params = payload.get("params")
        if not isinstance(params, dict):
            return self._rpc_error(rid, -32602, "invalid client.register params")
        if params.get("protocol_version") != 1:
            return self._rpc_error(rid, -32602, "unsupported client protocol_version")
        terminal = ((params.get("capabilities") or {}).get("terminal") or {})
        if not isinstance(terminal, dict) or int(terminal.get("version") or 0) < 2:
            return self._rpc_error(rid, -32602, "terminal capability version 2 is required")
        if not str(params.get("client_id") or "").strip() or not str(params.get("instance_id") or "").strip():
            return self._rpc_error(rid, -32602, "client_id and instance_id are required")
        connection.registration = params
        return self._rpc_ok(rid, {"accepted": True, "protocol_version": 1})

    async def _terminal_respond(self, connection: ClientConnection, payload: dict[str, Any]) -> dict[str, Any]:
        rid = payload.get("id")
        params = payload.get("params")
        if not isinstance(params, dict):
            return self._rpc_error(rid, -32602, "invalid terminal.respond params")
        request_id = str(params.get("request_id") or "")
        if not request_id:
            return self._rpc_error(rid, -32602, "request_id is required")

        async with self._lock:
            pending = self.pending.get(request_id)
            if pending is None:
                return self._rpc_error(rid, -32004, "unknown or already completed request_id")
            if pending.connection is not connection or connection.registration is None:
                return self._rpc_error(rid, -32003, "request_id does not belong to this connection")
            status = str(params.get("status") or "")
            if status not in ALLOWED_STATUSES:
                return self._rpc_error(rid, -32602, "invalid terminal status")
            self.pending.pop(request_id, None)
            connection.pending_ids.discard(request_id)

        result = self._normalize_result(connection, params)
        if not pending.future.done():
            pending.future.set_result(result)
        return self._rpc_ok(rid, {"accepted": True})

    def _normalize_result(self, connection: ClientConnection, params: dict[str, Any]) -> dict[str, Any]:
        registration = connection.registration or {}
        terminal = ((registration.get("capabilities") or {}).get("terminal") or {})
        advertised = int(terminal.get("output_chars_max") or GLOBAL_OUTPUT_MAX)
        output_max = max(1, min(advertised, GLOBAL_OUTPUT_MAX))
        stdout = str(params.get("stdout") or "")
        stderr = str(params.get("stderr") or "")
        if len(stdout) + len(stderr) > output_max:
            stdout = stdout[:output_max]
            remaining = max(0, output_max - len(stdout))
            suffix = "\n[output truncated by broker]"
            stderr = stderr[: max(0, remaining - len(suffix))]
            if remaining >= len(suffix):
                stderr += suffix
        exit_code = params.get("exit_code")
        if exit_code is not None and not isinstance(exit_code, int):
            exit_code = None
        duration_ms = params.get("duration_ms")
        if duration_ms is not None and not isinstance(duration_ms, int):
            duration_ms = None
        return {
            "status": str(params.get("status")),
            "stdout": stdout,
            "stderr": stderr,
            "exit_code": exit_code,
            "timed_out": bool(params.get("timed_out", False)),
            "duration_ms": duration_ms,
            "error_code": params.get("error_code"),
        }

    def observe_client_request(self, connection: ClientConnection, payload: dict[str, Any]) -> None:
        rid = payload.get("id")
        method = payload.get("method")
        if rid is not None and isinstance(method, str):
            connection.rpc_methods[str(rid)] = method

    def observe_upstream_frame(self, connection: ClientConnection, payload: dict[str, Any]) -> None:
        rid = payload.get("id")
        if rid is not None:
            method = connection.rpc_methods.pop(str(rid), None)
            if method in {"session.create", "session.resume", "session.activate"}:
                self._remember_session_payload(connection, payload.get("result"))

        if payload.get("method") == "event":
            params = payload.get("params")
            if isinstance(params, dict):
                runtime_id = self._clean_session_id(params.get("session_id"))
                self._remember_session_id(connection, runtime_id)
                if params.get("type") == "session.info":
                    self._remember_session_payload(connection, params.get("payload"), runtime_id)

    def _remember_session_payload(
        self,
        connection: ClientConnection,
        payload: Any,
        event_runtime_id: str | None = None,
    ) -> None:
        if not isinstance(payload, dict):
            return
        runtime_id = self._clean_session_id(payload.get("session_id")) or event_runtime_id
        aliases: list[str] = []
        for key in ("session_id", "stored_session_id", "session_key", "resumed"):
            alias = self._clean_session_id(payload.get(key))
            if alias:
                aliases.append(alias)
                self._remember_session_id(connection, alias)
        info = payload.get("info")
        if isinstance(info, dict):
            alias = self._clean_session_id(info.get("stored_session_id"))
            if alias:
                aliases.append(alias)
                self._remember_session_id(connection, alias)
        if runtime_id:
            connection.session_aliases[runtime_id] = runtime_id
            for alias in aliases:
                connection.session_aliases[alias] = runtime_id

    @staticmethod
    def _clean_session_id(value: Any) -> str | None:
        if isinstance(value, str) and value.strip():
            return value.strip()
        return None

    @staticmethod
    def _remember_session_id(connection: ClientConnection, value: Any) -> None:
        if isinstance(value, str) and value.strip():
            connection.session_ids.add(value.strip())

    async def execute(self, request: dict[str, Any]) -> dict[str, Any]:
        session_id = str(request.get("session_id") or "").strip()
        command = str(request.get("command") or "")
        if not session_id:
            return self._error_result("inactive_session", "session_id is required")
        if not command.strip():
            return self._error_result("service_unavailable", "command must not be empty")

        try:
            requested_timeout = int(request.get("timeout_ms") or DEFAULT_TIMEOUT_MS)
        except (TypeError, ValueError):
            requested_timeout = DEFAULT_TIMEOUT_MS
        requested_timeout = max(1000, min(requested_timeout, GLOBAL_TIMEOUT_MAX_MS))

        async with self._lock:
            matches = [
                connection
                for connection in self.connections
                if connection.registration is not None and session_id in connection.session_ids
            ]
            if len(matches) != 1:
                detail = "No registered Companion owns this session" if not matches else "Session is bound to multiple Companions"
                return self._error_result("inactive_session", detail)
            connection = matches[0]
            runtime_session_id = connection.session_aliases.get(session_id, session_id)
            terminal = (((connection.registration or {}).get("capabilities") or {}).get("terminal") or {})
            advertised_timeout = int(terminal.get("timeout_ms_max") or GLOBAL_TIMEOUT_MAX_MS)
            timeout_ms = min(requested_timeout, max(1000, min(advertised_timeout, GLOBAL_TIMEOUT_MAX_MS)))
            request_id = secrets.token_urlsafe(24)
            future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
            self.pending[request_id] = PendingExecution(connection, runtime_session_id, future)
            connection.pending_ids.add(request_id)

        event = {
            "jsonrpc": "2.0",
            "method": "event",
            "params": {
                "type": "terminal.execute",
                "session_id": runtime_session_id,
                "payload": {
                    "request_id": request_id,
                    "command": command,
                    "timeout_ms": timeout_ms,
                    "persistent": bool(request.get("persistent", False)),
                },
            },
        }
        try:
            await connection.send_json(event)
            return await asyncio.wait_for(future, timeout=(timeout_ms / 1000.0) + 15.0)
        except asyncio.TimeoutError:
            return self._error_result("timeout", "Companion did not respond before the backend deadline")
        except Exception:
            LOG.exception("terminal.execute delivery failed session=%s", session_id)
            return self._error_result("service_unavailable", "Could not deliver command to Companion")
        finally:
            async with self._lock:
                self.pending.pop(request_id, None)
                connection.pending_ids.discard(request_id)


class ClientTerminalBroker:
    def __init__(self, upstream_ws: str, unix_socket: Path, state: BrokerState | None = None) -> None:
        self.upstream_ws = upstream_ws
        self.unix_socket = unix_socket
        self.state = state or BrokerState()
        self.http_runner: web.AppRunner | None = None
        self.unix_server: asyncio.AbstractServer | None = None
        self.client_session: ClientSession | None = None

    def build_app(self) -> web.Application:
        app = web.Application(client_max_size=2 * 1024 * 1024)
        app.router.add_get("/health", self.health)
        app.router.add_get("/api/client-ws", self.websocket_proxy)
        app.router.add_get("/api/ws", self.websocket_proxy)
        return app

    async def start(self, host: str, port: int) -> None:
        self.client_session = ClientSession(timeout=ClientTimeout(total=None, connect=15))
        self.http_runner = web.AppRunner(self.build_app(), access_log=None)
        await self.http_runner.setup()
        await web.TCPSite(self.http_runner, host, port).start()

        self.unix_socket.parent.mkdir(parents=True, exist_ok=True)
        if self.unix_socket.exists() or self.unix_socket.is_socket():
            self.unix_socket.unlink()
        self.unix_server = await asyncio.start_unix_server(
            self.handle_unix_request, path=str(self.unix_socket), limit=1_100_000
        )
        os.chmod(self.unix_socket, stat.S_IRUSR | stat.S_IWUSR)
        LOG.info("listening host=%s port=%d socket=%s", host, port, self.unix_socket)

    async def close(self) -> None:
        if self.unix_server is not None:
            self.unix_server.close()
            await self.unix_server.wait_closed()
        if self.http_runner is not None:
            await self.http_runner.cleanup()
        if self.client_session is not None:
            await self.client_session.close()
        try:
            self.unix_socket.unlink(missing_ok=True)
        except OSError:
            pass

    async def health(self, _request: web.Request) -> web.Response:
        registered = sum(1 for connection in self.state.connections if connection.registration is not None)
        return web.json_response({"ok": True, "registered_clients": registered, "pending": len(self.state.pending)})

    def _upstream_url(self, query_string: str) -> str:
        parts = urlsplit(self.upstream_ws)
        return urlunsplit((parts.scheme, parts.netloc, parts.path, query_string or parts.query, ""))

    async def websocket_proxy(self, request: web.Request) -> web.StreamResponse:
        client_ws = web.WebSocketResponse(max_msg_size=2 * 1024 * 1024, heartbeat=30)
        await client_ws.prepare(request)
        connection = ClientConnection(client_ws)
        await self.state.add_connection(connection)

        headers: dict[str, str] = {}
        for key in ("Cookie", "Authorization", "User-Agent"):
            value = request.headers.get(key)
            if value:
                headers[key] = value

        assert self.client_session is not None
        try:
            async with self.client_session.ws_connect(
                self._upstream_url(request.query_string),
                headers=headers,
                max_msg_size=2 * 1024 * 1024,
                heartbeat=30,
            ) as upstream_ws:
                to_upstream = asyncio.create_task(self._client_to_upstream(connection, client_ws, upstream_ws))
                to_client = asyncio.create_task(self._upstream_to_client(connection, upstream_ws))
                done, pending = await asyncio.wait({to_upstream, to_client}, return_when=asyncio.FIRST_COMPLETED)
                for task in pending:
                    task.cancel()
                await asyncio.gather(*done, *pending, return_exceptions=True)
        except Exception as exc:
            # Do not log the upstream URL: it may contain a single-use auth ticket.
            LOG.error("websocket proxy failed: %s", type(exc).__name__)
            if not client_ws.closed:
                await client_ws.close(code=1011, message=b"upstream unavailable")
        finally:
            await self.state.remove_connection(connection)
        return client_ws

    async def _client_to_upstream(self, connection: ClientConnection, client_ws: web.WebSocketResponse, upstream_ws: Any) -> None:
        async for message in client_ws:
            if message.type == WSMsgType.TEXT:
                try:
                    payload = json.loads(message.data)
                except (TypeError, json.JSONDecodeError):
                    await upstream_ws.send_str(message.data)
                    continue
                if isinstance(payload, dict) and await self.state.handle_client_rpc(connection, payload):
                    continue
                if isinstance(payload, dict):
                    self.state.observe_client_request(connection, payload)
                await upstream_ws.send_str(message.data)
            elif message.type == WSMsgType.BINARY:
                await upstream_ws.send_bytes(message.data)
            elif message.type in {WSMsgType.CLOSE, WSMsgType.CLOSED, WSMsgType.ERROR}:
                return

    async def _upstream_to_client(self, connection: ClientConnection, upstream_ws: Any) -> None:
        async for message in upstream_ws:
            if message.type == WSMsgType.TEXT:
                try:
                    payload = json.loads(message.data)
                    if isinstance(payload, dict):
                        self.state.observe_upstream_frame(connection, payload)
                except (TypeError, json.JSONDecodeError):
                    pass
                async with connection.send_lock:
                    await connection.websocket.send_str(message.data)
            elif message.type == WSMsgType.BINARY:
                async with connection.send_lock:
                    await connection.websocket.send_bytes(message.data)
            elif message.type in {WSMsgType.CLOSE, WSMsgType.CLOSED, WSMsgType.ERROR}:
                return

    async def handle_unix_request(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            peer_socket = writer.get_extra_info("socket")
            if peer_socket is not None and hasattr(socket, "SO_PEERCRED"):
                _pid, uid, _gid = struct.unpack(
                    "3i", peer_socket.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
                )
                if uid != os.getuid():
                    raise PermissionError("unix peer uid mismatch")
            raw = await asyncio.wait_for(reader.readline(), timeout=5)
            if not raw or len(raw) > 1_048_576:
                raise ValueError("invalid request size")
            request = json.loads(raw)
            if not isinstance(request, dict) or request.get("operation") != "execute":
                raise ValueError("unsupported operation")
            result = await self.state.execute(request)
        except Exception as exc:
            LOG.warning("unix request rejected: %s", type(exc).__name__)
            result = BrokerState._error_result("service_unavailable", "Broker rejected the request")
        writer.write((json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        try:
            await writer.drain()
        finally:
            writer.close()
            await writer.wait_closed()


async def run(args: argparse.Namespace) -> None:
    broker = ClientTerminalBroker(args.upstream_ws, Path(args.unix_socket))
    await broker.start(args.host, args.port)
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, stop.set)
        except NotImplementedError:
            pass
    await stop.wait()
    await broker.close()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default=os.getenv("NEON_CLIENT_TERMINAL_HOST", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.getenv("NEON_CLIENT_TERMINAL_PORT", "8648")))
    parser.add_argument(
        "--upstream-ws",
        default=os.getenv("NEON_CLIENT_TERMINAL_UPSTREAM_WS", "ws://neon-vps.tail53a46e.ts.net:9119/api/ws"),
    )
    parser.add_argument(
        "--unix-socket",
        default=os.getenv("NEON_CLIENT_TERMINAL_SOCKET", "/home/hermes/.hermes/run/client-terminal.sock"),
    )
    return parser.parse_args()


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    asyncio.run(run(parse_args()))


if __name__ == "__main__":
    main()
