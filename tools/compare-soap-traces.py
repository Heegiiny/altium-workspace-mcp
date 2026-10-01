r"""Comparison of two recordings of SOAP messages (ALTIUM_SOAP_TRACE).

  python tools\compare-soap-traces.py <baseline> <new recording> [--md table.md] [--show-diff N]

The requests (*.req.xml) are compared — what the client sends to the server, including the revision
release scripts (*-script-ExecuteScript.req.xml). The responses are not compared: they depend on
the database state, not on the wire format of the client.

The order of calls may differ (some reads run in parallel), so the requests
are grouped by "service / operation" and compared as sets. Each request has two
levels of match:

  exact      — the XML document matched up to namespace prefixes,
                  xmlns declarations and spaces around text (the element order,
                  namespaces, xsi:nil attributes etc. are compared strictly);
  normalized — the same after replacing what naturally differs between two runs:
                  the session identifier, GUIDs, time, numbers of new HRIDs and revisions,
                  run labels (--tag).

Exit code 0 — no differences (all requests matched at least after normalization); 1 — there are some.
"""

import argparse
import collections
import difflib
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

GUID = re.compile(r"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")
TIME = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?")
# The number of a new item (CMP-000-0975), a revision (…-3) and their prefixes: they differ between two runs.
HRID = re.compile(r"\b[A-Z]{2,6}-\d{1,4}-\d{2,8}\b|\b[A-Z]{2,6}-\d{3,8}\b")
SESSION_TAGS = {"SessionID", "SessionHandle"}


def split_name(tag):
    namespace, _, local = tag[1:].partition("}") if tag.startswith("{") else ("", "", tag)
    return namespace, local


def canonical(element, normalize, tags, parent=""):
    namespace, local = split_name(element.tag)
    text = (element.text or "").strip()

    if local in SESSION_TAGS:
        text = "<SESSION>"
    elif normalize:
        text = GUID.sub("<GUID>", text)
        text = TIME.sub("<TIME>", text)
        text = HRID.sub("<HRID>", text)
        for tag in tags:
            text = text.replace(tag, "<RUN>")
        # The revision number is database state: it differs between two runs (RevisionId, RevisionIdLevels/string).
        if re.fullmatch(r"\d{1,4}", text) and (local in ("RevisionId", "ItemRevisionId") or parent == "RevisionIdLevels"):
            text = "<REV>"

    attributes = "".join(
        "[@{%s}%s=%s]" % (*split_name(name), value if not normalize else GUID.sub("<GUID>", value))
        for name, value in sorted(element.attrib.items()))
    children = "".join(canonical(child, normalize, tags, local) for child in element)
    return "{%s}%s%s=%s(%s)" % (namespace, local, attributes, text, children)


def load(directory, tags):
    """Returns {(service, operation): [(file, exact form, normalized form), ...]}."""
    groups = collections.defaultdict(list)
    for path in sorted(glob.glob(os.path.join(directory, "*.req.xml"))):
        stem = os.path.basename(path)[:-len(".req.xml")]
        _, service, operation = stem.split("-", 2)
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
        # In the release script the declaration says utf-16 although the file is text: the declaration is not needed for the comparison.
        text = re.sub(r"^\s*<\?xml[^>]*\?>", "", text)
        try:
            root = ET.fromstring(text)
        except ET.ParseError as error:
            # Unparsable text is compared as text: two different files must not "match" because of the same error.
            groups[(service, operation)].append((path, "UNPARSABLE: %s: %s" % (error, text), "UNPARSABLE: %s" % text))
            continue
        groups[(service, operation)].append(
            (path, canonical(root, False, tags), canonical(root, True, tags)))
    return groups


def pretty(text):
    return re.sub(r"(\)|=)(?=\{)", r"\1\n", re.sub(r"\)(?=\{)", ")\n", text)).replace("(", "(\n")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Comparison of SOAP recordings.")
    parser.add_argument("baseline", help="the baseline directory (the old proxies)")
    parser.add_argument("candidate", help="the new recording directory")
    parser.add_argument("--tag", action="append", default=[], metavar="TAG",
                        help="a run label in the names of test objects; replaced during normalization")
    parser.add_argument("--md", metavar="FILE", help="write the comparison table in Markdown format")
    parser.add_argument("--show-diff", type=int, default=3, metavar="N",
                        help="how many mismatched requests to show line by line (3 by default)")
    options = parser.parse_args()

    base = load(options.baseline, options.tag)
    new = load(options.candidate, options.tag)

    rows = []
    shown = 0
    total_bad = 0

    for key in sorted(set(base) | set(new)):
        left, right = base.get(key, []), new.get(key, [])
        remaining = list(right)
        exact = normalized = 0
        unmatched_left = []

        for entry in left:
            hit = next((r for r in remaining if r[1] == entry[1]), None)
            if hit is not None:
                remaining.remove(hit)
                exact += 1
            else:
                unmatched_left.append(entry)

        still = []
        for entry in unmatched_left:
            hit = next((r for r in remaining if r[2] == entry[2]), None)
            if hit is not None:
                remaining.remove(hit)
                normalized += 1
            else:
                still.append(entry)

        bad = len(still) + len(remaining)
        total_bad += bad
        rows.append((key, len(left), len(right), exact, normalized, bad))

        for entry, partner in zip(still, remaining):
            if shown < options.show_diff:
                shown += 1
                print("\n=== DIFFERENCE: %s/%s\n  baseline: %s\n  new     : %s" % (
                    key[0], key[1], os.path.basename(entry[0]), os.path.basename(partner[0])))
                for line in difflib.unified_diff(
                        pretty(entry[2]).splitlines(), pretty(partner[2]).splitlines(), lineterm="", n=2):
                    print("  " + line)

    width = max([len("%s/%s" % key) for key, *_ in rows] + [10])
    print("%-*s %7s %7s %7s %12s %8s" % (width, "service/operation", "baseline", "new", "exact", "normalized", "differences"))
    for key, left, right, exact, normalized, bad in rows:
        print("%-*s %7d %7d %7d %12d %8d" % (width, "%s/%s" % key, left, right, exact, normalized, bad))

    totals = [sum(row[i] for row in rows) for i in (1, 2, 3, 4, 5)]
    print("%-*s %7d %7d %7d %12d %8d" % (width, "TOTAL", *totals))

    if options.md:
        with open(options.md, "w", encoding="utf-8") as handle:
            handle.write("| Service / operation | baseline | new | exact | normalized | differences |\n|---|---:|---:|---:|---:|---:|\n")
            for key, left, right, exact, normalized, bad in rows:
                handle.write("| `%s/%s` | %d | %d | %d | %d | %d |\n" % (key[0], key[1], left, right, exact, normalized, bad))
            handle.write("| **total** | %d | %d | %d | %d | %d |\n" % tuple(totals))

    print("\nResult: %s" % ("no differences" if total_bad == 0 else "THERE ARE DIFFERENCES: %d" % total_bad))
    return 1 if total_bad else 0


if __name__ == "__main__":
    sys.exit(main())
