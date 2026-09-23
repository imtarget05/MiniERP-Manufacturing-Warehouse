#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/export-swagger.sh
# PURPOSE: save artifacts/swagger.json (and a short endpoint inventory) as the
#          API acceptance evidence required by the project plan (Phase 3 DoD).
# USAGE:   bash scripts/export-swagger.sh [base_url]
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
[ $# -ge 1 ] && BASE_URL="$1"

rule
printf '%b\n' "${C_B} Exporting the OpenAPI document${C_0}"
rule
mkdir -p "$ARTIFACT_DIR"

RAW="$(mktemp)"
CODE="$(curl -s -o "$RAW" -w '%{http_code}' --max-time 30 "$BASE_URL/swagger/v1/swagger.json" 2>/dev/null || echo 000)"
if [ "$CODE" != "200" ]; then
  err "could not download swagger.json from $BASE_URL (HTTP $CODE)"
  err "start the API first: RUN_MODE=bg bash scripts/start-api.sh"
  rm -f "$RAW"; exit 1
fi

cp "$RAW" "$ARTIFACT_DIR/swagger.json"
ok "artifacts/swagger.json saved ($(wc -c < "$RAW" | tr -d ' ') bytes)"

python3 - "$RAW" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
info = doc.get("info", {})
print(f"  title   : {info.get('title')}  (v{info.get('version')})")
paths = doc.get("paths", {})
ops = [(m.upper(), p) for p, vi in paths.items() for m in vi]
print(f"  paths   : {len(paths)}    operations: {len(ops)}")
for method, path in sorted(ops, key=lambda x: (x[1], x[0])):
    print(f"    {method:<6} {path}")
PY

rm -f "$RAW"
rule
