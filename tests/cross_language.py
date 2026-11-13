"""Cross-language roundtrip: real C# TCP transport + Python stdio MCP.

Uses a fake gameplay callback, NOT Unity or a real save. Run after building
TransportTests.csproj. Both child processes are always stopped in finally.
"""
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
TOKEN = "test-token-" + "0" * 32


def main():
    fixture = subprocess.Popen(["dotnet", str(ROOT / "tests/bin/Release/net8.0/TransportTests.dll"), "--fixture"],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    client = None
    try:
        port = int(fixture.stdout.readline())
        env = dict(os.environ, PS_MCP_PORT=str(port), PS_MCP_TOKEN=TOKEN)
        client = subprocess.Popen([sys.executable, str(ROOT / "bridge/server.py")],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, env=env, text=True)

        def call(message):
            client.stdin.write(json.dumps(message) + "\n")
            client.stdin.flush()
            return json.loads(client.stdout.readline())

        initialized = call({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "cross-language-test", "version": "1"}}})
        assert initialized["result"]["protocolVersion"] == "2025-06-18"
        client.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        client.stdin.flush()
        tools = call({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
        assert len(tools["result"]["tools"]) == 6
        observed = call({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
            "name": "game_observe", "arguments": {"limit": 2}}})
        result = observed["result"]
        assert result["isError"] is False
        assert result["structuredContent"] == {"operation": "observe", "echo": {"limit": 2}, "fixture": True}
        assert TOKEN not in json.dumps(observed)
        moved = call({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {
            "name": "game_move_item", "arguments": {
                "snapshot": "fixture", "handle": "item", "target": "inventory", "x": 1.25, "y": 4, "amount": 2}}})
        assert moved["result"]["structuredContent"]["operation"] == "move_item"
        assert moved["result"]["structuredContent"]["echo"]["amount"] == 2
        print("PASS: Python stdio MCP → real C# loopback → fake gameplay callback → MCP result (6 tools)")
    finally:
        for child in (client, fixture):
            if child:
                child.terminate()
                try:
                    child.communicate(timeout=3)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.communicate()


if __name__ == "__main__":
    main()
