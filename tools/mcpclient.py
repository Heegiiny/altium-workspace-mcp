"""A minimal MCP client over the stdio of the altium-vault-mcp process — shared code for
tools/mcp-call.py (manual calls) and tools/smoke.py (smoke check)."""

import json
import os
import subprocess
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(ROOT, "Release", "altium-vault-mcp.exe")


class McpClient:
    """A JSON-RPC client over the stdio of the server process."""

    def __init__(self, exe, env):
        self._process = subprocess.Popen(
            [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None,
            env=env, text=True, encoding="utf-8", bufsize=1)
        self._next_id = 0
        self.initialize_result = self.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "mcp-call", "version": "1"},
        })
        self.notify("notifications/initialized", {})

    def notify(self, method, params):
        self._send({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method, params):
        self._next_id += 1
        self._send({"jsonrpc": "2.0", "id": self._next_id, "method": method, "params": params})

        while True:
            line = self._process.stdout.readline()
            if not line:
                raise RuntimeError("the server exited without answering; the reason is in the output above")
            message = json.loads(line)
            if message.get("id") == self._next_id:
                if "error" in message:
                    raise RuntimeError(json.dumps(message["error"], ensure_ascii=False))
                return message["result"]

    def call_tool(self, name, arguments):
        """Calls a tool; returns (response text, error flag, seconds)."""
        started = time.time()
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        text = "".join(block.get("text", "") for block in result.get("content", []))
        return text, bool(result.get("isError")), time.time() - started

    def close(self):
        self._process.stdin.close()
        self._process.wait(timeout=60)

    def _send(self, message):
        self._process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self._process.stdin.flush()


def environment(extra=None):
    """The environment for the server: the current one plus NAME=VALUE pairs."""
    env = dict(os.environ)
    for pair in extra or []:
        name, _, value = pair.partition("=")
        env[name] = value
    return env
