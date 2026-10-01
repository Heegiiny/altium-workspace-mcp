r"""Smoke check of the server: read-only calls and dry runs on a live server.

  python tools\smoke.py --exe $env:TEMP\altium-vault-probe\altium-vault-mcp.exe
  python tools\smoke.py --exe ... --cases tools\my-cases.json
  python tools\smoke.py --exe ... --only folders --only table

The call set is tools/smoke-cases.json if it exists, otherwise tools/smoke-cases.example.json
(read-only cases that work on any vault; copy it and add your own parts and test folders). It is a list of objects:
  name         the label in the table;
  tool         the tool name;
  arguments    the arguments (an object);
  maxChars     optional: the response length limit for this case (default 20000, the response size limit);
  expectError  optional: true — the response must be an error (checks the refusal text);
  skip         optional: true — the case is a template and is not run.

The result is a table: time, response length in characters, the error flag. Exit code 1 if a case returned
an error without expectError (or did not return one with expectError) or the response is longer than the limit.
One server session for the whole run, nothing is written: copying uses dryRun.
"""

import argparse
import json
import os
import sys

from mcpclient import DEFAULT_EXE, McpClient, environment

DEFAULT_LIMIT = 20000
_TOOLS = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CASES = os.path.join(_TOOLS, "smoke-cases.json")
if not os.path.exists(DEFAULT_CASES):
    DEFAULT_CASES = os.path.join(_TOOLS, "smoke-cases.example.json")


def run(client, cases):
    rows = []
    failed = False

    for case in cases:
        limit = case.get("maxChars", DEFAULT_LIMIT)
        expect_error = case.get("expectError", False)

        try:
            text, is_error, seconds = client.call_tool(case["tool"], case.get("arguments", {}))
        except RuntimeError as error:
            text, is_error, seconds = str(error), True, 0.0

        problems = []
        if is_error != expect_error:
            problems.append("an error was expected" if expect_error else "error: " + text[:160].replace("\n", " "))
        if len(text) > limit:
            problems.append("longer than the limit %d" % limit)

        failed |= bool(problems)
        rows.append((case["name"], case["tool"], seconds, len(text), is_error, limit, problems))

    return rows, failed


def print_table(rows):
    width = max(len(row[0]) for row in rows)
    print("%-*s  %-24s %8s %9s %8s  %s" % (width, "case", "tool", "time, s", "chars", "limit", "result"))

    for name, tool, seconds, chars, is_error, limit, problems in rows:
        status = "ok" if not problems else "; ".join(problems)
        if is_error and not problems:
            status = "ok (expected refusal)"
        print("%-*s  %-24s %8.1f %9d %8d  %s" % (width, name, tool, seconds, chars, limit, status))


def main():
    sys.stdout.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="Smoke check of altium-vault-mcp.")
    parser.add_argument("--exe", default=DEFAULT_EXE, help="path to altium-vault-mcp.exe (Release by default)")
    parser.add_argument("--env", action="append", default=[], metavar="NAME=VALUE",
                        help="an extra environment variable for the server")
    parser.add_argument("--cases", default=DEFAULT_CASES, help="the cases file (JSON)")
    parser.add_argument("--var", action="append", default=[], metavar="NAME=VALUE",
                        help="substitute {NAME} in the cases file (a run label in the names of test objects)")
    parser.add_argument("--only", action="append", default=[], metavar="SUBSTRING",
                        help="run only the cases whose label contains the substring; can be repeated")
    options = parser.parse_args()

    with open(options.cases, encoding="utf-8-sig") as handle:
        text = handle.read()

    for pair in options.var:
        name, _, value = pair.partition("=")
        text = text.replace("{" + name + "}", value)

    cases = [case for case in json.loads(text) if not case.get("skip")]

    if options.only:
        cases = [case for case in cases if any(part.lower() in case["name"].lower() for part in options.only)]

    if not cases:
        parser.error("no cases to run")

    client = McpClient(options.exe, environment(options.env))
    try:
        rows, failed = run(client, cases)
    finally:
        client.close()

    print_table(rows)
    print("\nResult: %s" % ("there are remarks" if failed else "all fine"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
