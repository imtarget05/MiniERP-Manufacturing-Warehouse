#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/check-destructive-gate-contract.sh
# PURPOSE: (a) prove statically that EVERY script in this repo which can destroy
#            or overwrite an existing database asks scripts/qa-gate.sh for a
#            PROVEN disposable identity BEFORE its first destructive statement,
#            and that it captures that identity before scripts/lib.sh can
#            substitute the production defaults;
#            (b) prove it behaviourally, by running scripts/restore-db.sh
#            through an environment matrix with shimmed docker/sqlplus/impdp and
#            counting how many times any of them was reached.
# USAGE:   bash scripts/check-destructive-gate-contract.sh
# EXIT:    0 = every check passed, 1 = at least one failed, 2 = untrustworthy
#
# WHY (b) IS NEEDED ON TOP OF (a): "it calls the gate" is a text property. The
# incident this guards is behavioural - a script that resolves DB_CONTAINER from
# lib.sh's default reaches the production container while every declaration
# looks correct. So the matrix drives the real script and asserts 0 sqlplus and
# 0 impdp invocations on every refusal.
#
# NOTHING HERE TOUCHES A DATABASE. Every refusal must happen before the first
# byte of I/O, which is exactly what the shims prove. No restore is ever run.
# ============================================================================
set -uo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)" || exit 2

HERE="$PWD/scripts"
GATE="scripts/qa-gate.sh"
LIB="scripts/lib.sh"
RESTORE="scripts/restore-db.sh"
SELF="check-destructive-gate-contract.sh"
RUN_SQL="scripts/run-sql.sh"
LIVE_PHRASE='i-have-a-change-window'
PASS=0
FAIL=0
ok()  { printf '  ok    %s\n' "$*"; PASS=$((PASS + 1)); }
bad() { printf '  FAIL  %s\n' "$*" >&2; FAIL=$((FAIL + 1)); }
check_eq() { if [ "$2" = "$3" ]; then ok "$1 ($3)"; else bad "$1: expected '$2', got '$3'"; fi; }

for f in "$GATE" "$LIB" "$RESTORE" "$RUN_SQL"; do
  [ -f "$f" ] || { printf 'FAIL  missing %s - the contract cannot be checked\n' "$f" >&2; exit 2; }
done
command -v python3 >/dev/null 2>&1 || { printf 'FAIL  python3 is required\n' >&2; exit 2; }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/destructive-gate-contract.XXXXXX")" || exit 2
SHIM="$WORK/shim"
SHIM_LOG="$WORK/shim-invocations.log"
mkdir -p "$SHIM"
: > "$WORK/all-refusals.txt"
trap 'rm -rf "$WORK"' EXIT

# ---------------------------------------------------------------------------
# 1. STATIC: discover the destructive scripts, then require the gate from each.
#    Discovery is a scan, not a list, so a NEW destructive script is covered the
#    day it is written. An empty result is a failure: a classifier that matches
#    nothing would make the whole contract vacuous.
# ---------------------------------------------------------------------------
SCAN="$(python3 scripts/scan-gate-contract.py scripts "$SELF")" || {
  bad "the gate-contract scanner could not run"; SCAN=""; }
if [ -z "$SCAN" ]; then
  bad "the gate-contract scanner produced no output - this check cannot be trusted"
else
  printf '%s\n' "$SCAN" | grep '^WARNING' >&2
  printf '%s\n' "$SCAN" | grep '^INFO' | cut -f2 | while IFS= read -r n; do
    printf '  --    %s\n' "$n"
  done
fi
DESTRUCTIVE_COUNT="$(printf '%s\n' "$SCAN" | grep -c '^INFO' || true)"
HIT_COUNT="$(printf '%s\n' "$SCAN" | grep -c '^HIT' || true)"
if [ "$DESTRUCTIVE_COUNT" -eq 0 ]; then
  bad "no destructive script was classified - the contract would pass vacuously; update the classifier deliberately"
else
  ok "destructive scripts discovered by scanning: $DESTRUCTIVE_COUNT"
  while IFS="$(printf '\t')" read -r tag name dline dwhy reasons gsrc gcall lsrc icap; do
    [ "$tag" = "INFO" ] || continue
    printf '  --    %-24s first destructive statement: line %s (%s)\n' "$name" "$dline" "$dwhy"
    if [ "$gsrc" -gt 0 ]; then
      ok "$name sources qa-gate.sh (line $gsrc)"
    else
      bad "$name never sources qa-gate.sh"
    fi
    if [ "$gcall" -gt 0 ]; then
      ok "$name calls qa_gate (line $gcall)"
    else
      bad "$name never calls qa_gate"
    fi
    if [ "$gcall" -gt 0 ] && [ "$gcall" -lt "$dline" ]; then
      ok "$name calls qa_gate at line $gcall, before its first destructive statement at line $dline"
    else
      bad "$name calls qa_gate at line $gcall but its first destructive statement is at line $dline - the gate must come first"
    fi
    if [ "$icap" -gt 0 ] && [ "$lsrc" -gt 0 ] && [ "$icap" -lt "$lsrc" ]; then
      ok "$name captures the target identity at line $icap, before lib.sh's defaults at line $lsrc"
    else
      bad "$name does not capture DB_CONTAINER/APP_USER before sourcing lib.sh (identity capture line ${icap:-0}, lib.sh line $lsrc) - lib.sh would substitute minierp-oracle/erp_user and the gate could not tell"
    fi
  done <<EOF
$SCAN
EOF
  if [ "$HIT_COUNT" -eq 0 ]; then
    ok "the scanner reports no gate violation"
  else
    printf '%s\n' "$SCAN" | grep '^HIT' >&2
    bad "$HIT_COUNT gate violation(s) reported by the scanner"
  fi
fi

# The contract must be able to FAIL. A copy of the tree with the gate call
# removed from restore-db.sh must be reported as a violation; otherwise the
# check above could be reading an empty string and saying "ok".
MUT="$WORK/mutant"
mkdir -p "$MUT"
cp "$LIB" "$GATE" "$RUN_SQL" "$MUT/" 2>/dev/null
cp scripts/portable-tmp.sh "$MUT/" 2>/dev/null
sed -e '/source .*qa-gate\.sh/d' -e '/^[[:space:]]*qa_gate /d' "$RESTORE" > "$MUT/restore-db.sh"
if [ -f "$MUT/restore-db.sh" ] \
   && ! grep -qE 'source .*qa-gate\.sh|^[[:space:]]*qa_gate[[:space:]]' "$MUT/restore-db.sh"; then
  MUT_HITS="$(python3 scripts/scan-gate-contract.py "$MUT" 2>/dev/null | grep -c '^HIT' || true)"
  if [ "${MUT_HITS:-0}" -gt 0 ]; then
    ok "the contract detects a restore-db.sh with the gate removed (mutation check: $MUT_HITS violation(s))"
  else
    bad "the contract did NOT detect a restore-db.sh with the gate removed - the static check cannot fail"
  fi
else
  bad "could not build the gate-less mutant of restore-db.sh; the mutation check did not run"
fi

# The gate library must define every function it calls. A call to a function
# that does not exist does not abort a gated script - it prints
# "command not found" and substitutes an empty string, which on this very gate
# meant the authorised database was printed as a blank line. So the check is
# explicit: every qa_gate_* identifier used anywhere must also be defined.
MISSING_FNS="$(bash -c '
  source scripts/qa-gate.sh
  used=$(grep -oE "qa_gate_[a-z_]+" scripts/qa-gate.sh | sort -u)
  defined=$(declare -F | awk "{print \$3}" | sort -u)
  for f in $used; do
    printf "%s\n" "$defined" | grep -qx "$f" || printf "UNDEFINED %s\n" "$f"
  done
')"
if [ -z "$MISSING_FNS" ]; then
  ok "every qa_gate_* function the gate calls is defined (an undefined one would print an empty authorised target)"
else
  printf '%s\n' "$MISSING_FNS" >&2
  bad "the gate calls a function it does not define; the refusal/authorisation line would be silently incomplete"
fi
# And the authorised database must actually be named on that line.
GATE_LINE="$(env -i PATH="/usr/bin:/bin:/usr/sbin:/sbin" HOME="${HOME:-/tmp}" \
  DB_CONTAINER=minierp-qa-oracle APP_USER=erp_qa MINIERP_QA_INSTANCE=minierp-qa \
  MINIERP_QA_DISPOSABLE=1 \
  bash scripts/restore-db.sh --gate-check 2>&1 || true)"
if printf '%s\n' "$GATE_LINE" | grep -q 'db    -> minierp-qa-oracle/erp_qa'; then
  ok "the gate names the database it authorised (minierp-qa-oracle/erp_qa)"
else
  bad "the gate did not print the authorised database identity"
  printf '%s\n' "$GATE_LINE" | sed 's/^/        | /' | head -20 >&2
fi

# ---------------------------------------------------------------------------
# 2. BEHAVIOURAL: the environment matrix for scripts/restore-db.sh.
#    Shims stand in for docker/sqlplus/impdp and append to $SHIM_LOG, so any
#    real I/O attempt is visible and the run is made to fail loudly rather than
#    touch a container. The backup argument is a real directory holding a real
#    (already-archived) dump, so the refusals are proven against realistic input.
# ---------------------------------------------------------------------------
DUMP_DIR="${MINIERP_CONTRACT_DUMP_DIR:-}"
if [ -z "$DUMP_DIR" ] || [ ! -d "$DUMP_DIR" ]; then
  DUMP_DIR="$(find . -maxdepth 4 -type f -name '*.dmp' -print 2>/dev/null | head -1 | xargs -I{} dirname {} 2>/dev/null || true)"
fi
if [ -z "$DUMP_DIR" ] || [ ! -d "$DUMP_DIR" ]; then
  # No dump anywhere: build a stand-in so the matrix still exercises the gate,
  # and say so, because the gate runs before the dump is ever inspected.
  DUMP_DIR="$WORK/fake-backup"
  mkdir -p "$DUMP_DIR"
  printf 'not a real dump - the gate must refuse before this is ever parsed\n' > "$DUMP_DIR/fake.dmp"
  ok "no .dmp in the repo; the matrix uses a stand-in directory (the gate must refuse before the input is inspected)"
else
  ok "the matrix points at a real backup directory: $DUMP_DIR"
fi

for tool in docker sqlplus impdp expdp; do
  cat > "$SHIM/$tool" <<SHIMEOF
#!/usr/bin/env bash
printf '%s %s\n' "$tool" "\$*" >> "$SHIM_LOG"
echo "SHIM VIOLATION: $tool was reached" >&2
exit 97
SHIMEOF
  chmod +x "$SHIM/$tool"
done

# run_case <name> <expected_rc> <expect_shims 0|n> <expect_text|-> -- <env assignments...> -- <args...>
run_case() {
  local name="$1" want_rc="$2" want_shims="$3" want_text="$4"; shift 4
  [ "$1" = "--" ] && shift
  local envs=()
  while [ "$1" != "--" ] && [ $# -gt 0 ]; do envs+=("$1"); shift; done
  [ "$1" = "--" ] && shift
    : > "$SHIM_LOG"
  local out="$WORK/out.txt" rc
  env -i PATH="$SHIM:/usr/bin:/bin:/usr/sbin:/sbin" HOME="${HOME:-/tmp}" TMPDIR="${TMPDIR:-/tmp}" \
      "${envs[@]}" bash "$RESTORE" "$@" > "$out" 2>&1
  rc=$?
  local shims; shims="$(wc -l < "$SHIM_LOG" 2>/dev/null | tr -d ' ')"; [ -n "$shims" ] || shims=0
  if [ "$want_rc" = "78" ]; then cat "$out" >> "$WORK/all-refusals.txt"; fi
  if [ "$rc" = "$want_rc" ]; then ok "$name: exit $rc"
  else bad "$name: expected exit $want_rc, got $rc"; sed 's/^/        | /' "$out" | head -25 >&2; fi
  if [ "$want_shims" = "0" ] && [ "$shims" = "0" ]; then
    ok "$name: 0 sqlplus / 0 impdp / 0 docker invocations"
  elif [ "$want_shims" != "0" ]; then
    ok "$name: reached the shimmed tools ($shims invocation(s)) as expected"
  else
    bad "$name: the script reached a real I/O tool: $(tr '\n' ';' < "$SHIM_LOG")"
  fi
  if [ "$want_text" != "-" ]; then
    if grep -qE "$want_text" "$out"; then ok "$name: output matches /$want_text/"
    else bad "$name: output did not match /$want_text/"; sed 's/^/        | /' "$out" | head -25 >&2; fi
  fi
  cat "$out" >> "$WORK/all-refusals.txt"
}

QA_ID=(DB_CONTAINER=minierp-qa-oracle APP_USER=erp_qa MINIERP_QA_INSTANCE=minierp-qa
       QA_DB_VOLUME=minierp-qa_oracle_data QA_DB_PROJECT=minierp-qa
       MINIERP_QA_DISPOSABLE=1 ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes)
PROD_ID=(DB_CONTAINER=minierp-oracle APP_USER=erp_user
         QA_DB_VOLUME=04-minierp-manufacturing-warehouse_oracle_data
         QA_DB_PROJECT=04-minierp-manufacturing-warehouse
         ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes)

# C1 the incident itself: production identity + a self-declared disposable.
run_case "production identity + MINIERP_QA_DISPOSABLE=1" 78 0 'REFUSED.*restore-db\.sh' \
  -- MINIERP_QA_DISPOSABLE=1 "${PROD_ID[@]}" -- "$DUMP_DIR"
# C2 same, but the operator typed the live change-window phrase.
run_case "production identity + live change-window phrase" 0 0 'LIVE SMOKE AUTHORISED' \
  -- "MINIERP_QA_LIVE_SMOKE=$LIVE_PHRASE" "${PROD_ID[@]}" -- --gate-check "$DUMP_DIR"
# C3 a proven throwaway, asked to prove it and stop there.
run_case "QA identity + disposable + --gate-check" 0 0 'no request made' \
  -- "${QA_ID[@]}" -- --gate-check "$DUMP_DIR"
# C4 missing the destructive flag.
run_case "QA identity, ALLOW_DESTRUCTIVE_RESTORE unset" 78 0 'SAFETY LOCK.*ALLOW_DESTRUCTIVE_RESTORE' \
  -- DB_CONTAINER=minierp-qa-oracle APP_USER=erp_qa MINIERP_QA_INSTANCE=minierp-qa \
     MINIERP_QA_DISPOSABLE=1 CONFIRM_RESTORE=yes -- "$DUMP_DIR"
# C5 missing the confirmation.
run_case "QA identity, CONFIRM_RESTORE unset" 78 0 'SAFETY LOCK.*CONFIRM_RESTORE' \
  -- DB_CONTAINER=minierp-qa-oracle APP_USER=erp_qa MINIERP_QA_INSTANCE=minierp-qa \
     MINIERP_QA_DISPOSABLE=1 ALLOW_DESTRUCTIVE_RESTORE=true -- "$DUMP_DIR"
# C6 no identity at all: lib.sh would substitute minierp-oracle / erp_user.
run_case "no DB_CONTAINER / APP_USER in the environment" 78 0 'must be set in the environment' \
  -- MINIERP_QA_DISPOSABLE=1 ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes -- "$DUMP_DIR"
# C7 a QA identity with no acknowledgement at all.
run_case "QA identity, no acknowledgement" 78 0 'REFUSED' \
  -- DB_CONTAINER=minierp-qa-oracle APP_USER=erp_qa MINIERP_QA_INSTANCE=minierp-qa \
     ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes -- "$DUMP_DIR"
# C8 lib.sh's own defaults spelled out by the operator are still production.
run_case "operator names the lib.sh production defaults" 78 0 'REFUSED' \
  -- DB_CONTAINER=minierp-oracle APP_USER=erp_user MINIERP_QA_DISPOSABLE=1 \
     ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes -- "$DUMP_DIR"

# Every refusal above must have happened BEFORE the backup input was even
# looked at, so a refusal can never depend on the target's filesystem.
if grep -qE 'not a real dump' "$WORK/all-refusals.txt" 2>/dev/null; then
  bad "a refusal happened after the backup input was inspected"
else
  ok "the refused runs never parsed the backup input"
fi

# ---------------------------------------------------------------------------
# 3. The live path must be loud, and must be the ONLY way to reach a
#    production-shaped target.
# ---------------------------------------------------------------------------
run_case "the live phrase does not bypass the safety locks" 78 0 'SAFETY LOCK.*CONFIRM_RESTORE' \
  -- "MINIERP_QA_LIVE_SMOKE=$LIVE_PHRASE" "${QA_ID[@]:0:5}" ALLOW_DESTRUCTIVE_RESTORE=true \
     -- "$DUMP_DIR"
LIVE_LOG="$WORK/live.txt"
: > "$SHIM_LOG"
env -i PATH="$SHIM:/usr/bin:/bin:/usr/sbin:/sbin" HOME="${HOME:-/tmp}" \
    MINIERP_QA_LIVE_SMOKE="$LIVE_PHRASE" "${PROD_ID[@]}" \
    bash "$RESTORE" --gate-check "$DUMP_DIR" > "$LIVE_LOG" 2>&1
if grep -q 'LIVE SMOKE AUTHORISED' "$LIVE_LOG" && grep -q 'minierp-oracle' "$LIVE_LOG"; then
  ok "the live banner names the target it is about to write to (minierp-oracle)"
else
  bad "the live banner did not name the production target"
  sed 's/^/        | /' "$LIVE_LOG" | head -20 >&2
fi
check_eq "the live --gate-check run still reached no tool" "0" "$(wc -l < "$SHIM_LOG" 2>/dev/null | tr -d ' ')"

printf '%s\n' "---------------------------------------------------------------------"
if [ "$FAIL" -gt 0 ]; then
  printf 'DESTRUCTIVE GATE CONTRACT: FAILED (%d of %d checks failed)\n' "$FAIL" "$((PASS + FAIL))" >&2
  exit 1
fi
printf 'DESTRUCTIVE GATE CONTRACT: OK - %d checks, %d destructive script(s) gated before their first destructive statement\n' \
  "$PASS" "$DESTRUCTIVE_COUNT"
