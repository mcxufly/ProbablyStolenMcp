"""Run from the project root: python3 -m unittest discover -s tests -v."""

import io
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import threading
import unittest
from unittest import mock
import uuid

from bridge import server

ROOT = Path(__file__).resolve().parents[1]


def request(method, params=None, request_id=1):
    value = {"jsonrpc": "2.0", "id": request_id, "method": method}
    if params is not None:
        value["params"] = params
    return value


def initialization(version="2025-06-18"):
    return request("initialize", {"protocolVersion": version, "capabilities": {},
                                  "clientInfo": {"name": "unittest", "version": "1"}})


INITIALIZED = {"jsonrpc": "2.0", "method": "notifications/initialized"}


def call(name="game_observe", arguments=None, request_id=2):
    params = {"name": name}
    if arguments is not None:
        params["arguments"] = arguments
    return request("tools/call", params, request_id)


class FakeTcp:
    """An ephemeral loopback peer, always stopped/joined by its context manager."""
    def __init__(self, responder=None):
        self.responder = responder or (lambda connection, message: connection.sendall(
            server.encode_json({"ok": True, "result": {"snapshot": "s1", "controls": []}}) + b"\n"))
        self.requests = []
        self.errors = []
        self.stopped = threading.Event()
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen()
        self.listener.settimeout(0.05)
        self.port = self.listener.getsockname()[1]
        self.thread = threading.Thread(target=self.run, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def run(self):
        while not self.stopped.is_set():
            try:
                connection, address = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            with connection:
                connection.settimeout(1)
                try:
                    raw = bytearray()
                    while b"\n" not in raw:
                        chunk = connection.recv(65536)
                        if not chunk:
                            break
                        raw.extend(chunk)
                    message = server.decode_json(bytes(raw).rstrip(b"\n"))
                    self.requests.append(message)
                    self.responder(connection, message)
                except (BrokenPipeError, ConnectionResetError):
                    pass  # The client deliberately closes an oversized response.
                except Exception as error:
                    self.errors.append(error)

    def __exit__(self, *args):
        self.stopped.set()
        self.listener.close()
        self.thread.join(2)
        if self.thread.is_alive():
            raise AssertionError("Fake TCP server did not stop")
        if self.errors:
            raise AssertionError(self.errors)


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.token = secrets.token_urlsafe(32)

    def ready(self, bridge=None, version="2025-06-18"):
        instance = server.McpServer(bridge or server.TcpBridge(self.token))
        self.assertIn("result", instance.handle(initialization(version)))
        self.assertIsNone(instance.handle(INITIALIZED))
        return instance

    def stdio(self, messages, port=None, extra_env=None, module=True):
        env = os.environ.copy()
        env["PS_MCP_TOKEN"] = self.token
        env.pop("PS_MCP_PORT", None)
        if port is not None:
            env["PS_MCP_PORT"] = str(port)
        if extra_env:
            env.update(extra_env)
        raw = b"".join(server.encode_json(message) + b"\n" for message in messages)
        command = [sys.executable, "-m", "bridge"] if module else [sys.executable, "bridge/server.py"]
        result = subprocess.run(command, input=raw, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                cwd=ROOT, env=env, timeout=5)
        self.assertNotIn(self.token.encode(), result.stdout)
        self.assertNotIn(self.token.encode(), result.stderr)
        return result, [server.decode_json(line) for line in result.stdout.splitlines()]

    def test_subprocess_initialize_list_ping_call_end_to_end(self):
        with FakeTcp() as peer:
            process, messages = self.stdio([
                initialization(), INITIALIZED, request("ping", request_id="ping"),
                request("tools/list", request_id="list"),
                call(arguments={"offset": 0, "limit": 10}),
            ], peer.port)
        self.assertEqual(process.returncode, 0)
        self.assertEqual(process.stderr, b"")
        self.assertEqual(len(messages), 4)
        init = messages[0]["result"]
        self.assertEqual(init["serverInfo"]["version"], "0.1.1")
        self.assertEqual(set(init["capabilities"]), {"tools"})
        self.assertIn("untrusted", init["instructions"])
        self.assertIn("saved progress", init["instructions"])
        self.assertIn("retry", init["instructions"])
        self.assertEqual(messages[1], {"jsonrpc": "2.0", "id": "ping", "result": {}})
        tools = messages[2]["result"]["tools"]
        self.assertEqual([tool["name"] for tool in tools], list(server.OPERATIONS))
        self.assertTrue(all(tool["inputSchema"]["additionalProperties"] is False for tool in tools))
        result = messages[3]["result"]
        self.assertFalse(result["isError"])
        self.assertEqual(json.loads(result["content"][0]["text"]), result["structuredContent"])
        self.assertEqual(result["structuredContent"]["snapshot"], "s1")
        self.assertEqual(len(peer.requests), 1)
        self.assertEqual(peer.requests[0]["token"], self.token)
        self.assertEqual(peer.requests[0]["operation"], "observe")
        self.assertEqual(peer.requests[0]["arguments"], {"offset": 0, "limit": 10})
        self.assertEqual(set(peer.requests[0]), {"token", "operation", "arguments", "request_id"})
        self.assertEqual(str(uuid.UUID(peer.requests[0]["request_id"])), peer.requests[0]["request_id"])

    def test_ui_operations_get_new_request_ids_and_unicode(self):
        arguments = {"snapshot": "s1", "handle": "h1"}
        with FakeTcp() as peer:
            instance = self.ready(server.TcpBridge(self.token, peer.port))
            self.assertFalse(instance.handle(call("game_ui_action", arguments))["result"]["isError"])
            value = {**arguments, "value": "中文\nquoted\""}
            self.assertFalse(instance.handle(call("game_ui_input", value))["result"]["isError"])
        self.assertEqual([item["operation"] for item in peer.requests], ["ui_action", "ui_input"])
        self.assertEqual(peer.requests[1]["arguments"], value)
        self.assertNotEqual(peer.requests[0]["request_id"], peer.requests[1]["request_id"])

    def test_all_versions_and_negotiation(self):
        bridge = mock.Mock()
        bridge.call.return_value = {"value": 1}
        for version in server.VERSIONS:
            with self.subTest(version=version):
                instance = self.ready(bridge, version)
                result = instance.handle(call())["result"]
                self.assertEqual("structuredContent" in result, version == "2025-06-18")
                tools = instance.handle(request("tools/list"))["result"]["tools"]
                self.assertEqual("annotations" in tools[0], version != "2024-11-05")
        instance = server.McpServer(bridge)
        result = instance.handle(initialization("future-version"))["result"]
        self.assertEqual(result["protocolVersion"], "2025-06-18")

    def test_lifecycle_requires_initialize_then_notification(self):
        instance = server.McpServer(mock.Mock())
        self.assertEqual(instance.handle(request("ping"))["result"], {})
        self.assertEqual(instance.handle(call())["error"]["code"], -32002)
        instance.handle(initialization())
        self.assertEqual(instance.handle(call())["error"]["code"], -32002)
        instance.handle(INITIALIZED)
        self.assertIn("result", instance.handle(request("tools/list")))
        self.assertEqual(instance.handle(initialization())["error"]["code"], -32600)
        self.assertEqual(instance.handle(request("resources/list"))["error"]["code"], -32601)

    def test_initialization_validation_and_extensible_metadata(self):
        for change in ({"unknown": True}, {"capabilities": []}, {"protocolVersion": 1},
                       {"clientInfo": {"name": "x"}}, {"clientInfo": {"name": "x", "version": "1", "bad": 1}}):
            with self.subTest(change=change):
                message = initialization()
                message["params"].update(change)
                result = server.McpServer(mock.Mock()).handle(message)
                self.assertEqual(result["error"]["code"], -32602)
        message = initialization()
        message["params"]["capabilities"] = {"newCapability": {"anything": True}}
        message["params"]["_meta"] = {"custom": [1, True]}
        self.assertIn("result", server.McpServer(mock.Mock()).handle(message))

    def test_tool_schema_validation_rejects_unknown_types_bounds_and_missing(self):
        bridge = mock.Mock()
        instance = self.ready(bridge)
        invalid = [
            call("unknown"), call(arguments={"extra": 1}),
            call(arguments={"offset": True}), call(arguments={"offset": 1.0}),
            call(arguments={"offset": -1}), call(arguments={"offset": 2147483648}),
            call(arguments={"limit": False}), call(arguments={"limit": 0}),
            call(arguments={"limit": 101}), call(arguments={"limit": "2"}),
            call("game_ui_action", {}),
            call("game_ui_action", {"snapshot": "s", "handle": ""}),
            call("game_ui_action", {"snapshot": "s" * 129, "handle": "h"}),
            call("game_ui_action", {"snapshot": True, "handle": "h"}),
            call("game_ui_input", {"snapshot": "s", "handle": "h", "value": False}),
            call("game_ui_input", {"snapshot": "s", "handle": "h", "value": "x" * 2049}),
            call("game_ui_input", {"snapshot": "s", "handle": "h"}),
            request("tools/call", {"name": "game_observe", "arguments": []}),
            request("tools/call", {"name": "game_observe", "unknown": 1}),
            request("tools/list", {"cursor": "not-issued"}),
            request("tools/list", {"unknown": True}),
        ]
        for message in invalid:
            with self.subTest(message=message):
                self.assertEqual(instance.handle(message)["error"]["code"], -32602)
        bridge.call.assert_not_called()

    def test_maximum_valid_arguments_and_empty_input(self):
        bridge = mock.Mock()
        bridge.call.return_value = {}
        instance = self.ready(bridge)
        messages = [call(arguments={"offset": 2147483647, "limit": 100}),
                    call("game_ui_input", {"snapshot": "s" * 128, "handle": "h" * 128, "value": ""}),
                    call("game_ui_input", {"snapshot": "s", "handle": "h", "value": "x" * 2048})]
        for message in messages:
            self.assertFalse(instance.handle(message)["result"]["isError"])
        self.assertEqual(bridge.call.call_count, 3)

    def test_notifications_are_silent_and_never_execute_tools(self):
        bridge = mock.Mock()
        instance = self.ready(bridge)
        messages = [INITIALIZED, {"jsonrpc": "2.0", "method": "notifications/cancelled",
                                  "params": {"requestId": 1}},
                    {"jsonrpc": "2.0", "method": "new/notification"},
                    {"jsonrpc": "2.0", "method": "tools/call", "params": {"name": "game_observe"}},
                    {key: value for key, value in initialization().items() if key != "id"}]
        for message in messages:
            self.assertIsNone(instance.handle(message))
        bridge.call.assert_not_called()

    def test_invalid_envelopes_batches_and_id_types(self):
        instance = self.ready(mock.Mock())
        invalid = [None, [], [request("ping")], 1, "x", {},
                   request("ping", request_id=None), request("ping", request_id=True),
                   request("ping", request_id=1.0), request("ping", request_id=[]),
                   {"jsonrpc": "1.0", "id": 4, "method": "ping"},
                   {"jsonrpc": "2.0", "id": 4, "method": 12},
                   {"jsonrpc": "2.0", "id": 4, "method": "ping", "extra": 1},
                   request("ping", []), {"jsonrpc": "2.0", "method": 1}]
        for message in invalid:
            with self.subTest(message=message):
                result = instance.handle(message)
                self.assertEqual(result["error"]["code"], -32600)
                self.assertIn("id", result)
        for request_id in (0, -1, "", "id-1"):
            self.assertEqual(instance.handle(request("ping", request_id=request_id))["id"], request_id)

    def test_backend_error_and_secret_redaction(self):
        def respond(connection, message):
            connection.sendall(server.encode_json({"ok": False, "error": {
                "code": "failure-" + self.token, "message": "peer echoed " + self.token}}) + b"\n")
        with FakeTcp(respond) as peer:
            process, messages = self.stdio([initialization(), INITIALIZED, call()], peer.port)
        self.assertEqual(process.returncode, 0)
        result = messages[-1]["result"]
        self.assertTrue(result["isError"])
        self.assertIn("[REDACTED]", result["content"][0]["text"])
        bridge = server.TcpBridge(self.token)
        self.assertEqual(bridge.redact({self.token: [self.token]}), {"[REDACTED]": ["[REDACTED]"]})

    def test_success_result_secret_redaction(self):
        def respond(connection, message):
            connection.sendall(server.encode_json({"ok": True, "result": {
                "text": self.token, self.token: [self.token]}}) + b"\n")
        with FakeTcp(respond) as peer:
            instance = self.ready(server.TcpBridge(self.token, peer.port))
            result = instance.handle(call())["result"]
        self.assertNotIn(self.token, json.dumps(result))
        self.assertFalse(result["isError"])

    def test_disconnected_refused_and_eof_no_retry(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reserved:
            reserved.bind(("127.0.0.1", 0))
            bridge = server.TcpBridge(self.token, reserved.getsockname()[1])
            result = self.ready(bridge).handle(call())["result"]
        self.assertEqual(result["structuredContent"]["error"]["code"], "disconnected")
        with FakeTcp(lambda connection, message: None) as peer:
            instance = self.ready(server.TcpBridge(self.token, peer.port))
            result = instance.handle(call("game_ui_action", {"snapshot": "s", "handle": "h"}))["result"]
        self.assertEqual(len(peer.requests), 1)
        self.assertTrue(result["isError"])
        self.assertEqual(result["structuredContent"]["error"]["code"], "disconnected")

    def test_timeout_has_single_attempt_and_uncertain_outcome(self):
        release = threading.Event()
        def stalled(connection, message):
            release.wait(1)
        with FakeTcp(stalled) as peer:
            try:
                with mock.patch.object(server, "NETWORK_TIMEOUT", 0.03):
                    instance = self.ready(server.TcpBridge(self.token, peer.port))
                    result = instance.handle(call("game_ui_action", {"snapshot": "s", "handle": "h"}))["result"]
            finally:
                release.set()
        self.assertEqual(len(peer.requests), 1)
        error = result["structuredContent"]["error"]
        self.assertEqual(error["code"], "timeout")
        self.assertIn("may have executed", error["message"])
        self.assertIn("do not retry", error["message"])

    def test_network_destination_timeout_and_no_retry(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        connection.connect.side_effect = TimeoutError()
        with mock.patch.object(server.socket, "socket", return_value=connection) as constructor:
            with self.assertRaises(server.BridgeFailure) as caught:
                server.TcpBridge(self.token).call("observe", {})
        self.assertEqual(caught.exception.code, "timeout")
        constructor.assert_called_once_with(socket.AF_INET, socket.SOCK_STREAM)
        connection.connect.assert_called_once_with(("127.0.0.1", 17653))
        self.assertGreater(connection.settimeout.call_args.args[0], 9)
        self.assertLessEqual(connection.settimeout.call_args.args[0], 10)
        connection.sendall.assert_not_called()

    def test_total_deadline_does_not_reset_after_partial_response(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        connection.recv.return_value = b'{"ok":'
        # deadline=10; connect at 0; send at 4; first recv at 8; next recv at 11.
        with mock.patch.object(server.socket, "socket", return_value=connection):
            with mock.patch.object(server.time, "monotonic", side_effect=[0, 0, 4, 8, 11]):
                with self.assertRaises(server.BridgeFailure) as caught:
                    server.TcpBridge(self.token).call("observe", {})
        self.assertEqual(caught.exception.code, "timeout")
        connection.connect.assert_called_once()
        connection.sendall.assert_called_once()
        connection.recv.assert_called_once()
        self.assertEqual(connection.settimeout.call_args_list, [mock.call(10), mock.call(6), mock.call(2)])

    def test_fragmented_utf8_response_and_unterminated_response(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        raw = server.encode_json({"ok": True, "result": {"text": "\U0001f680"}}, ascii_only=False) + b"\n"
        split = raw.index(b"\xf0") + 1
        connection.recv.side_effect = [raw[:split], raw[split:split + 1], raw[split + 1:]]
        with mock.patch.object(server.socket, "socket", return_value=connection):
            result = server.TcpBridge(self.token).call("observe", {})
        self.assertEqual(result, {"text": "\U0001f680"})
        with FakeTcp(lambda connection, message: connection.sendall(b'{"ok":true,"result":{}}')) as peer:
            with self.assertRaises(server.BridgeFailure) as caught:
                server.TcpBridge(self.token, peer.port).call("observe", {})
        self.assertEqual(caught.exception.code, "disconnected")

    def test_invalid_backend_response_shapes_and_json(self):
        responses = [b"not-json\n", b"\xff\n", b"[]\n", b'{"ok":1,"result":{}}\n',
                     b'{"ok":true,"result":[]}\n', b'{"ok":false,"error":"x"}\n',
                     b'{"ok":false,"error":{"code":1,"message":"x"}}\n',
                     b'{"ok":true,"result":{},"extra":1}\n',
                     b'{"ok":true,"ok":true,"result":{}}\n',
                     b'{"ok":true,"result":{"x":NaN}}\n',
                     b'{"ok":true,"result":{"x":1e9999}}\n',
                     b'{"ok":true,"result":{}}\n{}\n']
        for raw in responses:
            with self.subTest(raw=raw):
                with FakeTcp(lambda connection, message: connection.sendall(raw)) as peer:
                    with self.assertRaises(server.BridgeFailure) as caught:
                        server.TcpBridge(self.token, peer.port).call("observe", {})
                self.assertEqual(caught.exception.code, "invalid_response")
                self.assertEqual(len(peer.requests), 1)

    def test_inventory_item_and_move_operations_end_to_end(self):
        moves = {"snapshot": "s", "handle": "item", "target": "inventory", "x": 12.5, "y": 30, "amount": 2}
        arguments = [
            ("game_inventory", {"snapshot": "s", "offset": 1, "limit": 100}),
            ("game_item_action", {"snapshot": "s", "handle": "item", "action": "examine"}),
            ("game_move_item", moves),
            ("game_ui_action", {"snapshot": "s", "handle": "scroll", "action": "scroll_down"}),
        ]
        with FakeTcp() as peer:
            process, messages = self.stdio([initialization(), INITIALIZED] + [
                call(name, value, request_id=index + 2) for index, (name, value) in enumerate(arguments)
            ], peer.port)
        self.assertEqual(process.returncode, 0)
        self.assertTrue(all(not item["result"]["isError"] for item in messages[1:]))
        self.assertEqual([item["operation"] for item in peer.requests],
                         ["inventory", "item_action", "move_item", "ui_action"])
        self.assertEqual([item["arguments"] for item in peer.requests], [value for name, value in arguments])

    def test_extended_tool_schemas_and_finite_numbers(self):
        bridge = mock.Mock()
        bridge.call.return_value = {}
        instance = self.ready(bridge)
        move = {"snapshot": "s", "handle": "h", "target": "t", "x": 1, "y": 2}
        invalid = [call("game_inventory", {}), call("game_inventory", {"snapshot": "s", "offset": True}),
                   call("game_item_action", {"snapshot": "s", "handle": "h"}),
                   call("game_item_action", {"snapshot": "s", "handle": "h", "action": "hold"}),
                   call("game_item_action", {"snapshot": "s", "handle": "h", "action": "use"}),
                   call("game_ui_action", {"snapshot": "s", "handle": "h", "action": "hold"}),
                   call("game_move_item", {key: value for key, value in move.items() if key != "target"})]
        invalid.extend(call("game_move_item", {**move, key: value}) for key, value in (
            ("x", True), ("y", "2"), ("x", float("nan")), ("x", float("inf")),
            ("y", -float("inf")), ("x", 10 ** 1000), ("amount", True), ("amount", 0),
            ("amount", 2147483648), ("target", ""), ("extra", "x")))
        for message in invalid:
            with self.subTest(message=message):
                self.assertEqual(instance.handle(message)["error"]["code"], -32602)
        bridge.call.assert_not_called()
        valid = [call("game_inventory", {"snapshot": "s"}), call("game_move_item", move),
                 call("game_move_item", {**move, "x": -1.25, "amount": 2147483647})]
        valid.extend(call("game_item_action", {"snapshot": "s", "handle": "h", "action": action})
                     for action in ("open", "examine", "activate", "toggle"))
        valid.extend(call("game_ui_action", {"snapshot": "s", "handle": "h", "action": action})
                     for action in ("click", "double_click", "scroll_up", "scroll_down"))
        for message in valid:
            self.assertFalse(instance.handle(message)["result"]["isError"])

    def test_long_unicode_input_fits_tcp_request_limit(self):
        arguments = {"snapshot": "s", "handle": "h", "value": "\U0001f680" * 2048}
        with FakeTcp() as peer:
            instance = self.ready(server.TcpBridge(self.token, peer.port))
            self.assertFalse(instance.handle(call("game_ui_input", arguments))["result"]["isError"])
        self.assertEqual(peer.requests[0]["arguments"], arguments)
        self.assertLess(len(server.encode_json(peer.requests[0], ascii_only=False)), server.MAX_REQUEST_BYTES)

    def test_invalid_unicode_is_not_forwarded(self):
        with mock.patch.object(server.socket, "socket") as constructor:
            instance = self.ready(server.TcpBridge(self.token))
            result = instance.handle(call("game_ui_input", {
                "snapshot": "s", "handle": "h", "value": "\ud800"}))["result"]
        self.assertTrue(result["isError"])
        self.assertEqual(result["structuredContent"]["error"]["code"], "invalid_arguments")
        constructor.assert_not_called()

    def test_tcp_response_size_boundary_and_oversize(self):
        prefix = b'{"ok":true,"result":{"text":"'
        suffix = b'"}}'
        exact = prefix + b"a" * (server.MAX_LINE_BYTES - len(prefix) - len(suffix)) + suffix
        with FakeTcp(lambda connection, message: connection.sendall(exact + b"\n")) as peer:
            result = server.TcpBridge(self.token, peer.port).call("observe", {})
        self.assertEqual(len(result["text"]), len(exact) - len(prefix) - len(suffix))
        with FakeTcp(lambda connection, message: connection.sendall(b"x" * (server.MAX_LINE_BYTES + 1) + b"\n")) as peer:
            with self.assertRaises(server.BridgeFailure) as caught:
                server.TcpBridge(self.token, peer.port).call("observe", {})
        self.assertEqual(caught.exception.code, "response_too_large")

    def test_tcp_request_size_preflight(self):
        bridge = server.TcpBridge("\x01" * 4096)
        with mock.patch.object(server.socket, "socket") as constructor:
            with self.assertRaises(server.BridgeFailure) as caught:
                bridge.call("observe", {})
        self.assertEqual(caught.exception.code, "request_too_large")
        constructor.assert_not_called()

    def test_stdio_line_size_boundary_recovery_and_parse_errors(self):
        ping = server.encode_json(request("ping"))
        exact = ping + b" " * (server.MAX_LINE_BYTES - len(ping)) + b"\n"
        oversized = b"x" * (server.MAX_LINE_BYTES + 5) + b"\n"
        invalid = [b"\xff\n", b"{bad}\n", b'{"x":NaN}\n',
                   b'{"jsonrpc":"2.0","jsonrpc":"2.0","method":"ping"}\n']
        source = io.BytesIO(exact + oversized + b"".join(invalid) + ping + b"\n" + ping)
        output = io.BytesIO()
        server.serve(server.McpServer(mock.Mock()), source, output)
        messages = [server.decode_json(line) for line in output.getvalue().splitlines()]
        self.assertEqual(messages[0]["result"], {})
        self.assertEqual([item["error"]["code"] for item in messages[1:6]], [-32700] * 5)
        self.assertEqual(messages[6]["result"], {})
        self.assertEqual(messages[7]["error"]["code"], -32700)  # Unterminated final line.

    def test_missing_token_and_bad_port_fail_without_stdout_or_value_leak(self):
        process, messages = self.stdio([], extra_env={"PS_MCP_TOKEN": ""})
        self.assertEqual(process.returncode, 2)
        self.assertEqual(messages, [])
        self.assertIn(b"PS_MCP_TOKEN", process.stderr)
        for port in ("0", "65536", "-1", "abc", "127.0.0.2:1234", " 1234", "１２３"):
            with self.subTest(port=port):
                process, messages = self.stdio([], extra_env={"PS_MCP_PORT": port})
                self.assertEqual(process.returncode, 2)
                self.assertEqual(messages, [])
                self.assertNotIn(port.encode(), process.stderr)

    def test_environment_token_constraints_match_mod(self):
        invalid_tokens = ["", "x" * 31, "x" * 129, "x" * 31 + "!", "中" * 32,
                          "x" * 31 + " ", "x" * 31 + "\n"]
        for token in invalid_tokens:
            with self.subTest(length=len(token)):
                process, messages = self.stdio([], extra_env={"PS_MCP_TOKEN": token})
                self.assertEqual(process.returncode, 2)
                self.assertEqual(messages, [])
                self.assertIn(b"PS_MCP_TOKEN", process.stderr)
                if token:
                    self.assertNotIn(token.encode(), process.stderr)
        for size in (32, 128):
            # Generated per test, not a bundled/default credential.
            token = secrets.token_urlsafe(96)[:size]
            process, messages = self.stdio([], extra_env={"PS_MCP_TOKEN": token})
            self.assertEqual(process.returncode, 0)
            self.assertEqual(process.stderr, b"")
            self.assertEqual(messages, [])
        process, messages = self.stdio([], extra_env={"PS_MCP_PORT": "9" * 5000})
        self.assertEqual(process.returncode, 2)
        self.assertEqual(messages, [])
        self.assertNotIn(b"Traceback", process.stderr)

    def test_script_entrypoint_and_clean_eof(self):
        process, messages = self.stdio([initialization(), INITIALIZED, request("ping")], module=False)
        self.assertEqual(process.returncode, 0)
        self.assertEqual(process.stderr, b"")
        self.assertEqual(len(messages), 2)
        process, messages = self.stdio([])
        self.assertEqual(process.returncode, 0)
        self.assertEqual(messages, [])
        self.assertEqual(process.stderr, b"")


if __name__ == "__main__":
    unittest.main()
