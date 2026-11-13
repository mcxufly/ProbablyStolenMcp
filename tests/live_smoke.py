"""Read-only live MCP smoke test. Uses PS_MCP_TOKEN/PS_MCP_PORT; never reads game files.

Run from the project root: python3 tests/live_smoke.py
Exit 0=basic checks passed, 1=failed, 2=needs human setup/restart. No mutations.
"""
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def inspect_game(tool, expected_version="0.1.3"):
    """Only two read-only tools are permitted, even if a future edit adds a call."""
    def read(name, args):
        if name not in ("game_observe", "game_inventory"):
            raise ValueError("Live smoke tests must remain read-only")
        result = tool(name, args)
        if result.get("error"):
            raise ValueError("MCP returned " + str(result["error"].get("code", "unknown_error")))
        return result

    old = read("game_observe", {"limit": 100})
    if old.get("mod_version") != expected_version:
        return 2, f"Restart with Mod {expected_version}; frontend version alone is not evidence of the loaded DLL."
    if not old.get("in_run"):
        return 2, "A human must load the test save first."
    if old.get("blocked_sensitive_screen"):
        return 2, "A human must close the sensitive settings/debug screen first."
    current = read("game_observe", {"limit": 100})
    if not old.get("snapshot") or old["snapshot"] == current.get("snapshot"):
        raise ValueError("observe did not issue a new snapshot")
    rejected = tool("game_inventory", {"snapshot": old["snapshot"], "limit": 1})
    if rejected.get("error", {}).get("code") != "unavailable":
        raise ValueError("Old snapshot was not rejected")
    inventory = read("game_inventory", {"snapshot": current["snapshot"], "limit": 100})
    if inventory.get("snapshot") != current["snapshot"]:
        raise ValueError("Inventory read unexpectedly changed snapshot")
    targets = [e for e in inventory.get("entries", []) if e.get("kind") == "inventory"]
    if not targets:
        return 1, "No visible inventory handle on the first 100 inventory entries; inspect the scene before moving items."
    if not all(e.get("empty_drop_point") for e in targets):
        raise ValueError("Inventory handle has no verified empty drop point")
    closes = sum(e.get("kind") == "window_close" for e in current.get("entries", []))
    return 0, f"PASS: loaded DLL {expected_version}, fresh/stale snapshots, inventory reads; visible targets={len(targets)}, close handles={closes}. No gameplay action executed."


def main():
    child = subprocess.Popen([sys.executable, str(ROOT / "bridge/server.py")],
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    request_id = 0
    try:
        def rpc(method, params):
            nonlocal request_id
            request_id += 1
            child.stdin.write(json.dumps({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}) + "\n")
            child.stdin.flush()
            line = child.stdout.readline()
            if not line:
                raise ValueError("MCP exited; verify private PS_MCP_TOKEN and PS_MCP_PORT in the environment.")
            reply = json.loads(line)
            if "error" in reply:
                raise ValueError("MCP protocol error")
            return reply["result"]

        rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                           "clientInfo": {"name": "readonly-live-smoke", "version": "1"}})
        child.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        child.stdin.flush()

        def tool(name, arguments):
            result = rpc("tools/call", {"name": name, "arguments": arguments})
            return result["structuredContent"]

        code, summary = inspect_game(tool, os.environ.get("PS_MCP_EXPECT_VERSION", "0.1.3"))
        print(summary)
        return code
    except (ValueError, KeyError, BrokenPipeError):
        print("FAIL: live MCP check failed; check connection and test-save state. No credentials displayed.")
        return 1
    finally:
        child.terminate()
        try:
            child.wait(timeout=2)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait()


if __name__ == "__main__":
    sys.exit(main())
