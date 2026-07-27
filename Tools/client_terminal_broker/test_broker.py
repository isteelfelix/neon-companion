from __future__ import annotations

import asyncio
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path

from aiohttp import ClientSession, WSMsgType, web

from broker import BrokerState, ClientConnection, ClientTerminalBroker


class FakeWebSocket:
    def __init__(self) -> None:
        self.frames: asyncio.Queue[str] = asyncio.Queue()

    async def send_str(self, value: str) -> None:
        await self.frames.put(value)


REGISTER = {
    "jsonrpc": "2.0",
    "id": "register-1",
    "method": "client.register",
    "params": {
        "client_id": "client-stable",
        "instance_id": "instance-current",
        "name": "neon-companion",
        "protocol_version": 1,
        "platform": {"os": "windows", "shell": "powershell"},
        "capabilities": {
            "terminal": {
                "version": 2,
                "persistent": True,
                "session_grants": True,
                "timeout_ms_max": 600000,
                "output_chars_max": 524288,
            }
        },
    },
}


class BrokerStateTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.state = BrokerState()
        self.ws = FakeWebSocket()
        self.connection = ClientConnection(self.ws)  # type: ignore[arg-type]
        await self.state.add_connection(self.connection)
        self.assertTrue(await self.state.handle_client_rpc(self.connection, REGISTER))
        registration_reply = json.loads(await self.ws.frames.get())
        self.assertTrue(registration_reply["result"]["accepted"])

        self.state.observe_client_request(
            self.connection,
            {"jsonrpc": "2.0", "id": "create-1", "method": "session.create", "params": {}},
        )
        self.state.observe_upstream_frame(
            self.connection,
            {
                "jsonrpc": "2.0",
                "id": "create-1",
                "result": {"session_id": "runtime-123", "stored_session_id": "stored-456"},
            },
        )

    async def asyncTearDown(self) -> None:
        await self.state.remove_connection(self.connection)

    async def test_execute_uses_runtime_id_and_preserves_result(self) -> None:
        task = asyncio.create_task(
            self.state.execute(
                {
                    "session_id": "stored-456",
                    "command": "Get-Location",
                    "timeout_ms": 30_000,
                    "persistent": True,
                }
            )
        )
        event = json.loads(await asyncio.wait_for(self.ws.frames.get(), 1))
        self.assertEqual(event["params"]["type"], "terminal.execute")
        self.assertEqual(event["params"]["session_id"], "runtime-123")
        request_id = event["params"]["payload"]["request_id"]

        accepted = await self.state.handle_client_rpc(
            self.connection,
            {
                "jsonrpc": "2.0",
                "id": "respond-1",
                "method": "terminal.respond",
                "params": {
                    "request_id": request_id,
                    "status": "completed",
                    "stdout": "C:\\Work\n",
                    "stderr": "",
                    "exit_code": 0,
                    "timed_out": False,
                    "duration_ms": 12,
                    "error_code": None,
                },
            },
        )
        self.assertTrue(accepted)
        ack = json.loads(await self.ws.frames.get())
        self.assertTrue(ack["result"]["accepted"])
        result = await asyncio.wait_for(task, 1)
        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["stdout"], "C:\\Work\n")
        self.assertEqual(result["exit_code"], 0)

        await self.state.handle_client_rpc(
            self.connection,
            {
                "jsonrpc": "2.0",
                "id": "respond-duplicate",
                "method": "terminal.respond",
                "params": {"request_id": request_id, "status": "completed"},
            },
        )
        duplicate = json.loads(await self.ws.frames.get())
        self.assertEqual(duplicate["error"]["code"], -32004)

    async def test_rejects_empty_command_and_unknown_session(self) -> None:
        empty = await self.state.execute({"session_id": "stored-456", "command": "  "})
        self.assertEqual(empty["status"], "error")
        unknown = await self.state.execute({"session_id": "other", "command": "pwd"})
        self.assertEqual(unknown["error_code"], "inactive_session")

    async def test_disconnect_releases_pending_request(self) -> None:
        task = asyncio.create_task(
            self.state.execute({"session_id": "stored-456", "command": "sleep", "timeout_ms": 1000})
        )
        await asyncio.wait_for(self.ws.frames.get(), 1)
        await self.state.remove_connection(self.connection)
        result = await asyncio.wait_for(task, 1)
        self.assertEqual(result["error_code"], "service_unavailable")


class UnixPluginIntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_plugin_round_trip_over_private_unix_socket(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            socket_path = Path(tmp) / "client-terminal.sock"
            state = BrokerState()
            broker = ClientTerminalBroker("ws://127.0.0.1:9/api/ws", socket_path, state)
            await broker.start("127.0.0.1", 0)
            try:
                ws = FakeWebSocket()
                connection = ClientConnection(ws)  # type: ignore[arg-type]
                await state.add_connection(connection)
                await state.handle_client_rpc(connection, REGISTER)
                await ws.frames.get()
                connection.session_ids.update({"stored", "runtime"})
                connection.session_aliases.update({"stored": "runtime", "runtime": "runtime"})

                plugin_path = Path(__file__).parents[1] / "hermes_plugin" / "client_terminal" / "__init__.py"
                old_socket = os.environ.get("NEON_CLIENT_TERMINAL_SOCKET")
                os.environ["NEON_CLIENT_TERMINAL_SOCKET"] = str(socket_path)
                try:
                    spec = importlib.util.spec_from_file_location("client_terminal_test_plugin", plugin_path)
                    assert spec is not None and spec.loader is not None
                    module = importlib.util.module_from_spec(spec)
                    spec.loader.exec_module(module)
                    plugin_task = asyncio.create_task(
                        asyncio.to_thread(
                            module.client_terminal,
                            {"command": "Write-Output ok", "timeout_ms": 5000, "persistent": False},
                            session_id="stored",
                        )
                    )
                    event = json.loads(await asyncio.wait_for(ws.frames.get(), 2))
                    request_id = event["params"]["payload"]["request_id"]
                    await state.handle_client_rpc(
                        connection,
                        {
                            "jsonrpc": "2.0",
                            "id": "response",
                            "method": "terminal.respond",
                            "params": {
                                "request_id": request_id,
                                "status": "completed",
                                "stdout": "ok\n",
                                "stderr": "",
                                "exit_code": 0,
                                "timed_out": False,
                            },
                        },
                    )
                    await ws.frames.get()
                    plugin_result = json.loads(await asyncio.wait_for(plugin_task, 2))
                    self.assertEqual(plugin_result["stdout"], "ok\n")
                    self.assertEqual(plugin_result["status"], "completed")
                finally:
                    if old_socket is None:
                        os.environ.pop("NEON_CLIENT_TERMINAL_SOCKET", None)
                    else:
                        os.environ["NEON_CLIENT_TERMINAL_SOCKET"] = old_socket
            finally:
                await broker.close()


class WebSocketProxyIntegrationTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def _free_port() -> int:
        import socket

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.bind(("127.0.0.1", 0))
            return int(sock.getsockname()[1])

    async def test_proxy_registration_session_binding_and_terminal_round_trip(self) -> None:
        upstream_port = self._free_port()
        broker_port = self._free_port()
        upstream_seen: list[dict] = []

        async def upstream_handler(request: web.Request) -> web.WebSocketResponse:
            self.assertEqual(request.query.get("ticket"), "single-use-ticket")
            ws = web.WebSocketResponse()
            await ws.prepare(request)
            await ws.send_json(
                {"jsonrpc": "2.0", "method": "event", "params": {"type": "gateway.ready", "payload": {}}}
            )
            async for message in ws:
                if message.type != WSMsgType.TEXT:
                    continue
                payload = json.loads(message.data)
                upstream_seen.append(payload)
                if payload.get("method") == "session.create":
                    await ws.send_json(
                        {
                            "jsonrpc": "2.0",
                            "id": payload["id"],
                            "result": {"session_id": "runtime-live", "stored_session_id": "stored-live"},
                        }
                    )
            return ws

        upstream_app = web.Application()
        upstream_app.router.add_get("/api/ws", upstream_handler)
        upstream_runner = web.AppRunner(upstream_app)
        await upstream_runner.setup()
        await web.TCPSite(upstream_runner, "127.0.0.1", upstream_port).start()

        with tempfile.TemporaryDirectory() as tmp:
            broker = ClientTerminalBroker(
                f"ws://127.0.0.1:{upstream_port}/api/ws",
                Path(tmp) / "broker.sock",
            )
            await broker.start("127.0.0.1", broker_port)
            try:
                async with ClientSession() as client:
                    async with client.ws_connect(
                        f"http://127.0.0.1:{broker_port}/api/ws?ticket=single-use-ticket"
                    ) as ws:
                        ready = await ws.receive_json()
                        self.assertEqual(ready["params"]["type"], "gateway.ready")

                        await ws.send_json(REGISTER)
                        register_reply = await ws.receive_json()
                        self.assertTrue(register_reply["result"]["accepted"])
                        self.assertFalse(any(item.get("method") == "client.register" for item in upstream_seen))

                        await ws.send_json(
                            {"jsonrpc": "2.0", "id": "create", "method": "session.create", "params": {}}
                        )
                        create_reply = await ws.receive_json()
                        self.assertEqual(create_reply["result"]["stored_session_id"], "stored-live")

                        execute_task = asyncio.create_task(
                            broker.state.execute(
                                {"session_id": "stored-live", "command": "Write-Output safe", "timeout_ms": 5000}
                            )
                        )
                        execute_event = await ws.receive_json()
                        self.assertEqual(execute_event["params"]["session_id"], "runtime-live")
                        request_id = execute_event["params"]["payload"]["request_id"]
                        await ws.send_json(
                            {
                                "jsonrpc": "2.0",
                                "id": "terminal-result",
                                "method": "terminal.respond",
                                "params": {
                                    "request_id": request_id,
                                    "status": "completed",
                                    "stdout": "safe\n",
                                    "stderr": "",
                                    "exit_code": 0,
                                    "timed_out": False,
                                },
                            }
                        )
                        terminal_ack = await ws.receive_json()
                        self.assertTrue(terminal_ack["result"]["accepted"])
                        execute_result = await asyncio.wait_for(execute_task, 2)
                        self.assertEqual(execute_result["stdout"], "safe\n")
                        self.assertFalse(any(item.get("method") == "terminal.respond" for item in upstream_seen))
            finally:
                await broker.close()
                await upstream_runner.cleanup()


if __name__ == "__main__":
    unittest.main()
