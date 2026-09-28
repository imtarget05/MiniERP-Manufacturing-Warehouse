#!/usr/bin/env python3
"""Report every `mktemp <template>` call under a directory whose X run is NOT
the trailing part of the template.

mktemp substitutes an X run only when that run ends the template. A template
such as `/tmp/trace-recon-XXXX.sql` therefore resolves to itself on macOS/BSD:
every run collides on one fixed path and a leftover file hard-fails the next
run. `scripts/portable-tmp.sh` exists to make that impossible; this scanner is
what keeps it impossible on the day a new script reintroduces it.

Usage: check-portable-mktemp.py <dir> [<skip-basename> ...]
Prints one line per violation, then a `scanned=N found=M` summary. The summary
is mandatory: the caller treats its absence as "the check could not be
trusted" rather than as "nothing found".
"""
import os
import re
import sys

X_RUN = re.compile(r"X{2,}")
QUOTED = re.compile(r'"([^"]*)"|\'([^\']*)\'')
CALL = re.compile(r"(?<![A-Za-z0-9_])mktemp\b")
# Characters that may follow an X run and still mean "the run is trailing".
BOUNDARY = set('/"\' )')


def strip_comment(line: str) -> str:
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


def templates_in(line: str):
    for call in CALL.finditer(line):
        tail = re.split(r";|&&|\|\||\|", line[call.end():])[0]
        candidates = [a if a is not None else b for a, b in QUOTED.findall(tail)]
        if not candidates:
            candidates = [t for t in tail.split() if not t.startswith("-") and "XXXX" in t]
        if candidates:
            yield candidates[-1]


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    root_dir, skip = argv[1], set(argv[2:])
    scanned = found = 0
    for root, dirs, files in os.walk(root_dir):
        dirs[:] = sorted(d for d in dirs if d != "worktrees")
        for name in sorted(files):
            if not name.endswith(".sh") or name in skip:
                continue
            scanned += 1
            path = os.path.join(root, name)
            with open(path, encoding="utf-8", errors="replace") as handle:
                for number, raw in enumerate(handle, 1):
                    line = strip_comment(raw)
                    if "mktemp" not in line:
                        continue
                    for template in templates_in(line):
                        for match in X_RUN.finditer(template):
                            after = template[match.end():]
                            if after and after[0] not in BOUNDARY:
                                found += 1
                                print("%s:%d non-trailing X run in mktemp template %r"
                                      % (path, number, template))
    print("scanned=%d found=%d" % (scanned, found))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
