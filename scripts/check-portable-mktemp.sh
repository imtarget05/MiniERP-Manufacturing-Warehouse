#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/check-portable-mktemp.sh
# PURPOSE: fail-closed proof that no script asks mktemp for a template whose X
#          run is not trailing, that the portable helper's own template really
#          substitutes on this platform, and that a stale literal file left by
#          the old broken template is detected and removed.
# USAGE:   bash scripts/check-portable-mktemp.sh
# EXIT:    0 = every check passed, 1 = at least one failed, 2 = untrustworthy
#          (library missing, so the check could not be trusted)
#
# PURELY LOCAL: no docker, no database, no network. Every temporary file this
# script creates is removed again, including the two literal /tmp paths it
# plants on purpose to prove the sweep works.
# ============================================================================
set -uo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)" || exit 2

LIB="scripts/portable-tmp.sh"
SELF="check-portable-mktemp.sh"
PASS=0
FAIL=0
ok()  { printf '  ok    %s\n' "$*"; PASS=$((PASS + 1)); }
bad() { printf '  FAIL  %s\n' "$*" >&2; FAIL=$((FAIL + 1)); }
check_eq() { # check_eq <name> <expected> <actual>
  if [ "$2" = "$3" ]; then ok "$1 ($3)"; else bad "$1: expected '$2', got '$3'"; fi
}

[ -f "$LIB" ] || { printf 'FAIL  missing %s - the portable temp contract cannot be checked\n' "$LIB" >&2; exit 2; }
# shellcheck source=scripts/portable-tmp.sh
source "$LIB"

cleanup() {
  local p
  for p in $PORTABLE_TMP_LEGACY_PATHS; do rm -f "$p"; done
  [ -n "${TMPD:-}" ] && rm -rf "$TMPD"
  return 0
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# 1. The RULE must flag a non-trailing X run and accept a trailing one. Proving
#    the rule on every platform (not only on macOS) is what keeps this check
#    meaningful on the GNU runner too.
# ---------------------------------------------------------------------------
if portable_tmp_has_non_trailing_x '/tmp/trace-recon-XXXX.sql'; then
  ok "the rule flags '/tmp/trace-recon-XXXX.sql' (X run is followed by .sql)"
else
  bad "the rule failed to flag '/tmp/trace-recon-XXXX.sql' - it cannot detect the defect"
fi
for good in '/tmp/trace-recon-before-XXXX' '/tmp/d.XXXXXX' 'XXXXXX'; do
  if portable_tmp_has_non_trailing_x "$good"; then
    bad "the rule wrongly flags the portable template '$good'"
  else
    ok "the rule accepts the portable template '$good'"
  fi
done

# What this platform's mktemp actually does with each shape. Recorded, not
# asserted: GNU substitutes a mid-template X run, BSD does not. The code must
# not depend on either, which is why the fix is 'mktemp -d' + an inner name.
SUBST="$(mktemp /tmp/portable-check-literal-XXXX.sql 2>/dev/null || true)"
if [ -n "$SUBST" ]; then
  printf '  info  this mktemp substituted a mid-template X run (%s) - GNU-style\n' "$SUBST"
  rm -f "$SUBST"
else
  printf '  info  this mktemp refused a mid-template X run - BSD/macOS-style\n'
fi
# The helper's own template is the load-bearing assertion, checked everywhere.
if portable_tmp_has_non_trailing_x "$(portable_tmp_template trace-recon)"; then
  bad "portable_tmpdir's own template has a non-trailing X run: $(portable_tmp_template trace-recon)"
else
  ok "the helper's own template is trailing-X only ($(portable_tmp_template trace-recon))"
fi

# ---------------------------------------------------------------------------
# 2. No script under scripts/ may pass a non-trailing-X template to mktemp.
#    Discovered by scanning, not hardcoded, so a NEW script inherits the rule.
#    A parser failure must not read as "nothing found": the scan is required to
#    print a summary line, and the scan must have seen real files.
# ---------------------------------------------------------------------------
SCAN_OUT="$(python3 scripts/check-portable-mktemp.py scripts "$SELF")"
SCAN_SUMMARY="$(printf '%s\n' "$SCAN_OUT" | grep '^scanned=' || true)"
SCAN_HITS="$(printf '%s\n' "$SCAN_OUT" | grep -c 'non-trailing X run' || true)"
if [ -z "$SCAN_SUMMARY" ]; then
  bad "the template scan produced no summary - this check cannot be trusted"
else
  ok "template scan ran over ${SCAN_SUMMARY#scanned=} shell scripts"
  if [ "$SCAN_HITS" -eq 0 ]; then
    ok "no script under scripts/ passes mktemp a template with a non-trailing X run"
  else
    printf '%s\n' "$SCAN_OUT" | grep 'non-trailing X run' >&2
    bad "found $SCAN_HITS non-trailing-X mktemp template(s) - BSD/macOS resolves them to literal paths and a stale file then hard-fails the next run"
  fi
fi

# ---------------------------------------------------------------------------
# 3. The two lines the traceability script used to get wrong must now build
#    their scratch paths through the helper, and the file they write must keep
#    the .sql suffix lib.sh's run_sql needs. The two ALREADY-portable
#    trailing-X templates must be left exactly as they are.
# ---------------------------------------------------------------------------
TRACE="scripts/test-traceability.sh"
for want in 'portable-tmp.sh' 'portable_tmpdir' 'portable_tmp_path' 'portable_tmp_sweep_legacy'; do
  if grep -q "$want" "$TRACE"; then ok "$TRACE uses $want"; else bad "$TRACE never uses $want"; fi
done
if grep -qE "mktemp[^\"']*['\"][^'\"]*X+[^'\"]*\.(sql|log)" "$TRACE"; then
  bad "$TRACE still builds a suffixed mktemp template"
else
  ok "$TRACE no longer builds a suffixed mktemp template"
fi
for keep in 'mktemp /tmp/trace-recon-before-XXXX' 'mktemp /tmp/trace-recon-after-XXXX'; do
  if grep -qF "$keep" "$TRACE"; then ok "$TRACE keeps the already-portable '$keep'"; else bad "$TRACE no longer has '$keep'"; fi
done

# ---------------------------------------------------------------------------
# 4. A stale literal file from the old broken template must be DETECTED and
#    REMOVED, and must not be recreated. This is the exact failure that blocked
#    the acceptance run, so it is exercised for real rather than described.
# ---------------------------------------------------------------------------
for p in $PORTABLE_TMP_LEGACY_PATHS; do
  printf 'stale content that must never be reused\n' > "$p" 2>/dev/null || true
done
PLANTED_PRESENT=0
for p in $PORTABLE_TMP_LEGACY_PATHS; do [ -e "$p" ] && PLANTED_PRESENT=$((PLANTED_PRESENT + 1)); done
check_eq "stale literal files were planted before the sweep" "2" "$PLANTED_PRESENT"

SWEEP_OUT="$(portable_tmp_sweep_legacy)"
LEFT=0
for p in $PORTABLE_TMP_LEGACY_PATHS; do [ -e "$p" ] && LEFT=$((LEFT + 1)); done
check_eq "no stale literal file survives portable_tmp_sweep_legacy" "0" "$LEFT"
check_eq "the sweep reported both removals (a silent no-op is not 'detected')" \
         "2" "$(printf '%s\n' "$SWEEP_OUT" | grep -c 'removed stale literal temp path' || true)"
check_eq "sweeping again reports nothing" "0" "$(portable_tmp_sweep_legacy | grep -c . || true)"

# ---------------------------------------------------------------------------
# 5. The replacement path: a scratch dir per run, the .sql suffix preserved
#    inside it, and no literal X run left in the resulting path.
# ---------------------------------------------------------------------------
TMPD="$(portable_tmpdir trace-recon)" || TMPD=""
if [ -n "$TMPD" ] && [ -d "$TMPD" ]; then
  ok "portable_tmpdir created a private directory (${TMPD##*/})"
  F1="$(portable_tmp_path "$TMPD" recon.sql)"
  check_eq "the inner name is stable across calls" "$F1" "$(portable_tmp_path "$TMPD" recon.sql)"
  case "$F1" in
    */recon.sql) ok "the .sql suffix is preserved inside the scratch dir" ;;
    *) bad "the scratch path lost its .sql suffix: $F1" ;;
  esac
  case "$F1" in
    *XXXX*) bad "the scratch path still contains a literal X run: $F1" ;;
    *) ok "the scratch path contains no literal X run" ;;
  esac
  TMPD2="$(portable_tmpdir trace-recon)" || TMPD2=""
  if [ -n "$TMPD2" ] && [ "$TMPD2" != "$TMPD" ]; then
    ok "two runs never share a scratch directory"
  else
    bad "two runs collided on the same scratch directory"
  fi
  rm -rf "$TMPD2" "$TMPD"
  TMPD=""
else
  bad "portable_tmpdir did not create a directory"
fi

printf '%s\n' "---------------------------------------------------------------------"
if [ "$FAIL" -gt 0 ]; then
  printf 'PORTABLE MKTEMP CONTRACT: FAILED (%d of %d checks failed)\n' "$FAIL" "$((PASS + FAIL))" >&2
  exit 1
fi
printf 'PORTABLE MKTEMP CONTRACT: OK - %d checks, no non-trailing mktemp template, stale literals swept\n' "$PASS"
