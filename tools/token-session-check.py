r"""Live check of token login: one server process, browser login, then reads and calls.

  $env:ALTIUM_OAUTH_CLIENT_ID = "<client_id registered on the login server>"
  python tools\token-session-check.py --exe $env:TEMP\altium-vault-probe\altium-vault-mcp.exe
  python tools\token-session-check.py --exe ... --call vault_update_parameters @update-test.json

Order: vault_status without login (must show the sign-in state and "not logged in") → vault_session login (prints
the link: open it in the browser and log in with your own account; the link waits 5 minutes) → login_status until
completed → vault_session show, vault_status, vault_folders → the --call calls (tool name and
arguments: a JSON string or @file.json; for writes — test objects only).

The NTLM session is not touched: the tokens are kept in a separate state directory (--state, by default
a temporary one), the Windows login's session.bin is neither read nor written. The script does not accept a password.
Exit code 1 — the login failed or the check returned an error.
"""

import argparse
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import DEFAULT_EXE, McpClient, environment  # noqa: E402


def show(name, text, is_error, seconds, limit=1500):
    print(f"== {name}: {'ERROR' if is_error else 'ok'}, {seconds:.1f} s, {len(text)} characters")
    print(text if len(text) <= limit else text[:limit] + " …")
    return is_error


def arguments(text):
    if text.startswith("@"):
        with open(text[1:], encoding="utf-8-sig") as handle:
            return json.load(handle)
    return json.loads(text)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Live check of token login.")
    parser.add_argument("--exe", default=DEFAULT_EXE)
    parser.add_argument("--state", default=os.path.join(tempfile.gettempdir(), "altium-token-check"),
                        help="the state directory for the tokens (a temporary one by default)")
    parser.add_argument("--call", nargs=2, action="append", default=[], metavar=("TOOL", "JSON"),
                        help="call a tool after the login; can be repeated")
    options = parser.parse_args()

    if not os.environ.get("ALTIUM_OAUTH_CLIENT_ID"):
        print("Set ALTIUM_OAUTH_CLIENT_ID — the client_id registered on the login server.")
        return 2

    env = environment(["ALTIUM_AUTH=token", "ALTIUM_TOKEN_STORE=file", f"ALTIUM_STATE_DIR={options.state}"])
    client = McpClient(options.exe, env)
    failed = False

    try:
        show("vault_status (before login: the sign-in state without totals)", *client.call_tool("vault_status", {}))
        text, is_error, seconds = client.call_tool("vault_session", {"action": "login"})
        show("vault_session login", text, is_error, seconds, limit=3000)
        if is_error:
            return 1

        status = json.loads(text)
        print("\nOpen the link in the browser and log in:\n" + str(status.get("loginUrl")) + "\n", flush=True)

        while status.get("state") == "pending":
            text, is_error, seconds = client.call_tool("vault_session", {"action": "login_status", "waitSeconds": 30})
            if is_error:
                show("vault_session login_status", text, is_error, seconds)
                return 1
            status = json.loads(text)

        show("vault_session login_status", text, False, seconds)
        if status.get("state") != "completed":
            return 1

        for name, args in [("vault_session", {"action": "show"}), ("vault_status", {}), ("vault_folders", {})]:
            failed |= show(f"{name} {json.dumps(args, ensure_ascii=False)}", *client.call_tool(name, args))

        for name, args in options.call:
            failed |= show(f"{name} (--call)", *client.call_tool(name, arguments(args)), limit=4000)
    finally:
        client.close()

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
