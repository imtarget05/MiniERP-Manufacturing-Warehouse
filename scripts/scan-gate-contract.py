#!/usr/bin/env python3
"""Classify the repo's shell scripts by how destructive they are, and report
which of them fail to ask scripts/qa-gate.sh for a proven disposable identity
BEFORE their first destructive statement.

Two output shapes, both tab separated:

  INFO <script> <destructive-line> <why> <reasons> <gate-source-line>
       <gate-call-line> <lib-source-line> <identity-capture-line>
  HIT  <script> <violation>

The requirements a destructive script must satisfy, and why each exists:

  * it sources scripts/qa-gate.sh, and calls qa_gate();
  * the qa_gate call comes BEFORE the first destructive statement, so a refusal
    happens before `impdp`, before `DROP USER` and before any db_probe;
  * it captures DB_CONTAINER / APP_USER into *_IN variables BEFORE sourcing
    scripts/lib.sh, because after that point lib.sh has substituted
    DB_CONTAINER=minierp-oracle and APP_USER=erp_user and "nobody overrode it"
    is indistinguishable from "the operator aimed at production";
  * it is not a library - a sourced helper is not an entry point.

An empty classification is reported as a WARNING, because a classifier that
matches nothing would let the whole contract pass vacuously.

Usage: scan-gate-contract.py <scripts-dir> [<skip-basename> ...]
"""
import os
import re
import sys

# Only executable code counts. A comment that mentions DROP USER is
# documentation, and documentation is exactly what hides a real violation.
DESTRUCTIVE = [
    (re.compile(r"\bimpdp\b"), "imports a Data Pump dump"),
    (re.compile(r"\bDROP\s+USER\b", re.I), "drops a schema"),
    (re.compile(r"\bDROP\s+TABLE\b", re.I), "drops a table"),
    (re.compile(r"\bTRUNCATE\s+TABLE\b", re.I), "truncates a table"),
    (re.compile(r"\bDROP\s+(PROFILE|LIBRARY|DATABASE\s+LINK)\b", re.I), "drops a database object"),
    (re.compile(r"00_reset_schema\.sql"), "runs the drop-everything reset"),
]
LIBRARIES = {"lib.sh", "qa-gate.sh", "portable-tmp.sh"}

RX_GATE_SOURCE = re.compile(r"source\s+\"?\$\{?[A-Za-z_]+\}?/qa-gate\.sh")
RX_GATE_CALL = re.compile(r"(?<![A-Za-z0-9_])qa_gate\s+[\"']")
RX_LIB_SOURCE = re.compile(r"source\s+.*lib\.sh")
RX_IDENT_CAPTURE = re.compile(r"^\s*(?:export\s+)?(?:DB_CONTAINER|APP_USER)_IN=")


def strip_comment(line):
    """Drop a trailing shell comment without breaking on a # inside quotes."""
    out, quote = [], None
    for ch in line:
        if quote:
            out.append(ch)
            if ch == quote:
                quote = None
            continue
        if ch in "\"'":
            quote = ch
            out.append(ch)
            continue
        if ch == "#":
            break
        out.append(ch)
    return "".join(out)


def first_line(lines, rx):
    for number, line in enumerate(lines, 1):
        if rx.search(line):
            return number
    return 0


def analyse(path, name):
    with open(path, encoding="utf-8", errors="replace") as handle:
        lines = [strip_comment(line) for line in handle]
    reasons, first = [], None
    for number, line in enumerate(lines, 1):
        for rx, why in DESTRUCTIVE:
            if rx.search(line):
                if why not in reasons:
                    reasons.append(why)
                if first is None or number < first[0]:
                    first = (number, why)
    if first is None:
        return None
    return {
        "name": name,
        "dline": first[0],
        "dwhy": first[1],
        "reasons": ";".join(reasons),
        "gsrc": first_line(lines, RX_GATE_SOURCE),
        "gcall": first_line(lines, RX_GATE_CALL),
        "lsrc": first_line(lines, RX_LIB_SOURCE),
        "icap": first_line(lines, RX_IDENT_CAPTURE),
    }


def hits_for(rec):
    out = []
    if not rec["gsrc"]:
        out.append("never sources qa-gate.sh")
    if not rec["gcall"]:
        out.append("never calls qa_gate")
    elif rec["gcall"] > rec["dline"]:
        out.append("calls qa_gate at line %d, after the first destructive statement at line %d"
                   % (rec["gcall"], rec["dline"]))
    if not rec["icap"]:
        out.append("never captures DB_CONTAINER/APP_USER into *_IN before sourcing lib.sh")
    elif rec["lsrc"] and rec["icap"] > rec["lsrc"]:
        out.append("captures the target identity at line %d, after lib.sh's defaults at line %d"
                   % (rec["icap"], rec["lsrc"]))
    return out


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    root, skip = argv[1], set(argv[2:])
    records = []
    for name in sorted(os.listdir(root)):
        if not name.endswith(".sh") or name in LIBRARIES or name in skip:
            continue
        rec = analyse(os.path.join(root, name), name)
        if rec:
            records.append(rec)
    if not records:
        print("WARNING\tno destructive script was classified - the contract would pass vacuously")
    for rec in records:
        print("INFO\t" + "\t".join(str(rec[k]) for k in
              ("name", "dline", "dwhy", "reasons", "gsrc", "gcall", "lsrc", "icap")))
        for message in hits_for(rec):
            print("HIT\t%s\t%s" % (rec["name"], message))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
