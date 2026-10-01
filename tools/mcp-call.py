"""Calling altium-vault-mcp tools over stdio — manual checks without an MCP client.

Examples (PowerShell):
  python tools\\mcp-call.py --list
  python tools\\mcp-call.py vault_status
  python tools\\mcp-call.py vault_component_detail '{"component":"CMP-000-00021"}'
  python tools\\mcp-call.py vault_update_parameters @update.json --save result.json
  python tools\\mcp-call.py --exe $env:TEMP\\altium-vault-probe\\altium-vault-mcp.exe vault_status

Tool arguments are a JSON string or @file.json (more convenient than escaping quotes
in PowerShell). The server is started for each call and continues the saved session, so
no extra license slot is taken. ALTIUM_BASE_URL is taken from the environment.
Exit code 1 — the tool returned an error.
"""

import argparse
import json
import sys

from mcpclient import DEFAULT_EXE, McpClient, environment


def parse_arguments(text):
    if not text:
        return {}
    if text.startswith("@"):
        with open(text[1:], encoding="utf-8-sig") as handle:
            return json.load(handle)
    return json.loads(text)


def main():
    sys.stdout.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="Calling altium-vault-mcp tools over stdio.")
    parser.add_argument("--exe", default=DEFAULT_EXE, help="path to altium-vault-mcp.exe (Release by default)")
    parser.add_argument("--env", action="append", default=[], metavar="NAME=VALUE",
                        help="an extra environment variable for the server; can be repeated")
    parser.add_argument("--save", metavar="FILE", help="save the response to a JSON file")
    parser.add_argument("--list", action="store_true", help="list the tools and their parameters")
    parser.add_argument("--init", action="store_true",
                        help="print the initialize response (including the server instructions) and its length")
    parser.add_argument("tool", nargs="?", help="the tool name, for example vault_status")
    parser.add_argument("arguments", nargs="?", help="arguments: a JSON string or @file.json")
    options = parser.parse_args()

    client = McpClient(options.exe, environment(options.env))
    exit_code = 0

    try:
        if options.init:
            print(json.dumps(client.initialize_result, ensure_ascii=False, indent=1))
            instructions = client.initialize_result.get("instructions") or ""
            print("instructions length: %d characters" % len(instructions), file=sys.stderr)
            return 0

        if options.list:
            for tool in client.request("tools/list", {})["tools"]:
                properties = tool.get("inputSchema", {}).get("properties", {})
                print("%-28s %s" % (tool["name"], ", ".join(properties)))
            return 0

        if not options.tool:
            parser.error("specify a tool or --list")

        result = client.request("tools/call", {"name": options.tool, "arguments": parse_arguments(options.arguments)})
        text = "".join(block.get("text", "") for block in result.get("content", []))

        try:
            payload = json.loads(text)
            output = json.dumps(payload, ensure_ascii=False, indent=1)
        except ValueError:
            output = text

        if options.save:
            with open(options.save, "w", encoding="utf-8") as handle:
                handle.write(output)
            print("response saved to", options.save)
        else:
            print(output)

        if result.get("isError"):
            exit_code = 1
    finally:
        client.close()

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
