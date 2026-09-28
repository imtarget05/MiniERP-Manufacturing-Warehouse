#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/check-ci-dsn-contract.sh
# PURPOSE: static proof that every `dotnet test` in CI is granted an explicit
#          Oracle data source, and that none of them relies on the ambient
#          opt-in. Purely static: no network, no database, no dotnet, no docker.
# USAGE:   bash scripts/check-ci-dsn-contract.sh
# EXIT:    0 when the contract holds, 1 on the first violation, 2 when the
#          check could not be trusted (parser missing, guard renamed, ...)
#
# WHY THIS EXISTS: tests/MiniERP.Api.Tests/TestOracleDsnGuard.cs is fail-closed -
# with no ConnectionStrings__OracleDb in the environment every guarded
# WebApplicationFactory throws in its constructor, because the alternative is to
# inherit src/appsettings.json, which names a real Oracle listener, and the suite
# writes a row on every POST. That is the correct behaviour, but it means a
# `dotnet test` step added to ci.yml without a DSN is a RED build whose message
# ("test data source refused") does not obviously point at the workflow. This
# script turns that into a named, fast, local check that names the fix.
#
# It also guards its own trustworthiness, because a check that cannot fail is
# worse than no check:
#   * the DSN variable names are read back out of the C# guard, so renaming them
#     there cannot leave this script validating a variable nothing reads;
#   * the workflow is parsed with a real YAML parser (Ruby Psych), not grep, so
#     reformatting or moving a step cannot hide an unauthorised invocation;
#   * indirection is resolved by DISCOVERING which scripts run `dotnet test`
#     rather than hardcoding one, so `bash scripts/run-all-tests.sh` is covered
#     today and a new wrapper script is covered the day it is written;
#   * it fails if it found no `dotnet test` invocation at all, so deleting the
#     steps cannot make it pass vacuously;
#   * a data source committed to the workflow must be the hermetic closed-port
#     one, so this contract cannot be satisfied by pasting a real credential
#     into ci.yml.
# ============================================================================
set -uo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)" || exit 2

SELF="$(basename "${BASH_SOURCE[0]}")"
WORKFLOW=".github/workflows/ci.yml"
RUNNER="scripts/run-all-tests.sh"
RUNNER_NAME="$(basename "$RUNNER")"
GUARD="tests/MiniERP.Api.Tests/TestOracleDsnGuard.cs"

FAILURES=0
fail() { printf '%s\n' "FAIL  $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { printf '%s\n' "  ok  $*"; }

for f in "$WORKFLOW" "$RUNNER" "$GUARD"; do
  [ -f "$f" ] || fail "missing $f"
done
[ "$FAILURES" -eq 0 ] || { printf '%s\n' "cannot check the contract: missing file(s)" >&2; exit 2; }

# ---------------------------------------------------------------------------
# 1. Read the variable names out of the guard, so a rename cannot silently leave
#    this script validating a variable that nothing reads.
# ---------------------------------------------------------------------------
DSN_VAR="$(sed -n 's/.*DsnVariable *= *"\([^"]*\)".*/\1/p' "$GUARD" | head -1)"
OPTIN_VAR="$(sed -n 's/.*AmbientOptInVariable *= *"\([^"]*\)".*/\1/p' "$GUARD" | head -1)"
if [ -z "$DSN_VAR" ] || [ -z "$OPTIN_VAR" ]; then
  fail "could not read DsnVariable / AmbientOptInVariable out of $GUARD - update this script when the guard is renamed"
  exit 2
fi
pass "guard variables: DSN=$DSN_VAR  ambient opt-in=$OPTIN_VAR"

command -v ruby >/dev/null 2>&1 || { fail "ruby is required to parse $WORKFLOW (no YAML parser available)"; exit 2; }

# ---------------------------------------------------------------------------
# 2. Which repo scripts run `dotnet test`? Discovered, not hardcoded - a step
#    that calls one of these inherits the problem, and a NEW script that runs the
#    tests is covered automatically. This indirection is exactly what made the
#    acceptance job invisible to a grep for "dotnet test" in ci.yml.
#    Newline-separated rather than an array so this also runs on the bash 3.2
#    that macOS still ships (no mapfile). $SELF is excluded: this file only ever
#    mentions `dotnet test` in prose.
# ---------------------------------------------------------------------------
DOTNET_TEST_SCRIPTS="$(grep -rlE 'dotnet[[:space:]]+test' scripts --include='*.sh' --exclude="$SELF" 2>/dev/null | sort)"
if [ -z "$DOTNET_TEST_SCRIPTS" ]; then
  fail "no script under scripts/ runs 'dotnet test' - the acceptance pipeline is missing?"
  exit 2
fi
pass "scripts that run 'dotnet test': $(printf '%s' "$DOTNET_TEST_SCRIPTS" | tr '\n' ' ')"

# ---------------------------------------------------------------------------
# 3. YAML-parse the workflow and emit one record per `dotnet test` invocation:
#       <job> TAB <kind: direct|script> TAB <script-or-.> TAB <effective dsn>
#    Effective env follows GitHub Actions: step env > job env > workflow env.
# ---------------------------------------------------------------------------
RECORDS="$(ruby -ryaml -e '
dsn = ARGV[0]
doc = YAML.load_file(ARGV[1])
test_scripts = ARGV[2].to_s.split("\n").map { |s| File.basename(s) }
sh_re = Regexp.new("([A-Za-z0-9_./-]+[.]sh)")
env_of = ->(h) { e = h.is_a?(Hash) ? h["env"] : nil; e.is_a?(Hash) ? e : {} }
out = []
(doc["jobs"] || {}).each do |job_name, job|
  job = {} unless job.is_a?(Hash)
  jenv = env_of.call(job)
  (job["steps"] || []).each do |step|
    next unless step.is_a?(Hash)
    run = step["run"].to_s
    called = run.scan(sh_re).flatten.map { |p| File.basename(p) }
    indirect = called.find { |b| test_scripts.include?(b) }
    if run =~ /dotnet\s+test/
      kind, target = "direct", "-"
    elsif indirect
      kind, target = "script", indirect
    else
      next
    end
    eff = {}
    eff.merge!(jenv)
    eff.merge!(env_of.call(step))
    out << [job_name, kind, target, eff[dsn].to_s].join("\t")
  end
end
puts out
' "$DSN_VAR" "$WORKFLOW" "$DOTNET_TEST_SCRIPTS")"
[ $? -eq 0 ] || { fail "ruby could not parse $WORKFLOW"; exit 2; }

INVOCATIONS="$(printf '%s\n' "$RECORDS" | grep -c . || true)"
if [ "$INVOCATIONS" -lt 2 ]; then
  fail "expected at least 2 'dotnet test' invocations in $WORKFLOW, found $INVOCATIONS - if the tests were removed or renamed, update this script deliberately"
  exit 2
fi
pass "found $INVOCATIONS 'dotnet test' invocation(s) in the workflow"

# ---------------------------------------------------------------------------
# 4. Every invocation must be granted a DSN, directly or by the runner that owns
#    the database it created.
# ---------------------------------------------------------------------------
SCRIPT_DSN_SOURCE="$(grep -n 'export ConnectionStrings__OracleDb=' "$RUNNER" | head -1 | cut -d: -f1 || true)"

while IFS="$(printf '\t')" read -r job kind target dsn; do
  [ -n "$job" ] || continue
  case "$kind" in
    direct)
      if [ -z "$dsn" ]; then
        fail "$job: 'dotnet test' has no $DSN_VAR in its effective env - the guarded test host will refuse to start"
      else
        pass "$job: $DSN_VAR granted at the step/job level"
      fi
      ;;
    script)
      if [ "$target" = "$RUNNER_NAME" ]; then
        if [ -z "$SCRIPT_DSN_SOURCE" ]; then
          fail "$job: runs '$target', which never exports $DSN_VAR - the guarded test host will refuse to start"
        else
          pass "$job: '$target' exports $DSN_VAR (line $SCRIPT_DSN_SOURCE) for the disposable it creates"
        fi
      elif [ -z "$dsn" ]; then
        fail "$job: 'dotnet test' is invoked indirectly through '$target', which this script cannot verify, and no $DSN_VAR is set - add the data source to the step env, or teach this script how '$target' grants it"
      else
        pass "$job: $DSN_VAR granted for the indirect invocation via '$target'"
      fi
      ;;
  esac
done <<EOF
$RECORDS
EOF

# ---------------------------------------------------------------------------
# 5. The ambient opt-in must never be SET in CI or in the runner. It is the
#    switch that authorises src/appsettings.json, and CI must depend on an
#    explicit grant only. This looks for an ASSIGNMENT, not for the name: the
#    workflow and the runner both name the variable in comments to explain why
#    it is not used, and flagging that documentation would be noise (and would
#    tempt someone to delete the explanation to make the check quiet).
#      - YAML: any env key of that name, at workflow, job or step level.
#      - shell: a real assignment. A comment starts with '#' and never matches.
# ---------------------------------------------------------------------------
if ruby -ryaml -e '
optin = ARGV[0]
doc = YAML.load_file(ARGV[1])
env_of = ->(h) { e = h.is_a?(Hash) ? h["env"] : nil; e.is_a?(Hash) ? e : {} }
hits = []
hits << "workflow env" if env_of.call(doc).key?(optin)
(doc["jobs"] || {}).each do |job_name, job|
  hits << "#{job_name} job env" if env_of.call(job).key?(optin)
  (job["steps"] || []).each_with_index do |step, i|
    hits << "#{job_name} step #{i + 1} env" if env_of.call(step).key?(optin)
  end
end
hits.each { |h| puts h }
exit(hits.empty? ? 0 : 1)
' "$OPTIN_VAR" "$WORKFLOW"; then
  pass "$OPTIN_VAR is not an env key anywhere in $WORKFLOW"
else
  fail "$OPTIN_VAR must not be set in $WORKFLOW - it authorises the appsettings.json target"
fi

if grep -rnE "^[[:space:]]*(export[[:space:]]+)?${OPTIN_VAR}=" scripts >/dev/null 2>&1; then
  grep -rnE "^[[:space:]]*(export[[:space:]]+)?${OPTIN_VAR}=" scripts >&2
  fail "$OPTIN_VAR must never be assigned in scripts/ - it authorises the appsettings.json target"
else
  pass "$OPTIN_VAR is never assigned in scripts/ (only 'unset' and comments mention it)"
fi

# ---------------------------------------------------------------------------
# 6. Any data source committed to the workflow must be the hermetic
#    closed-port one. This is what stops this contract from being satisfied by
#    pasting a real credential into ci.yml.
# ---------------------------------------------------------------------------
HERMETIC='User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;Pooling=true;Min Pool Size=1;'
WORKFLOW_DSNS="$(sed -n "s/^[[:space:]]*${DSN_VAR}:[[:space:]]*'\(.*\)'[[:space:]]*$/\1/p" "$WORKFLOW")"
if [ -z "$WORKFLOW_DSNS" ]; then
  fail "no $DSN_VAR found in $WORKFLOW at all - the unit job must grant one explicitly"
fi
while IFS= read -r d; do
  [ -n "$d" ] || continue
  if [ "$d" != "$HERMETIC" ]; then
    fail "$WORKFLOW contains a $DSN_VAR that is not the hermetic closed-port DSN: '$d'"
  else
    pass "workflow $DSN_VAR is the hermetic closed-port DSN (127.0.0.1:1, throwaway 'probe' credentials)"
  fi
done <<EOF
$WORKFLOW_DSNS
EOF

# ---------------------------------------------------------------------------
# 7. Job-level and step-level grants in the same job must not drift apart: a
#    step-level value silently wins in GitHub Actions, so two different values
#    mean one of them is dead configuration.
# ---------------------------------------------------------------------------
if ruby -ryaml -e '
dsn = ARGV[0]
doc = YAML.load_file(ARGV[1])
env_of = ->(h) { e = h.is_a?(Hash) ? h["env"] : nil; e.is_a?(Hash) ? e : {} }
bad = []
(doc["jobs"] || {}).each do |job_name, job|
  next unless job.is_a?(Hash)
  jenv = env_of.call(job)
  next unless jenv[dsn]
  (job["steps"] || []).each do |step|
    next unless step.is_a?(Hash)
    senv = env_of.call(step)
    next unless senv[dsn]
    bad << "#{job_name} sets #{dsn} at job level and differently at step level" if senv[dsn] != jenv[dsn]
  end
end
bad.each { |b| puts b }
exit(bad.empty? ? 0 : 1)
' "$DSN_VAR" "$WORKFLOW"; then
  pass "no job/step $DSN_VAR drift"
else
  fail "a step-level $DSN_VAR differs from its job-level value and silently overrides it"
fi

# ---------------------------------------------------------------------------
# 8. The runner must clear the opt-in as well as granting the DSN, so the grant
#    is the only thing standing between the suite and a connection, and it must
#    refuse rather than guess when it cannot resolve the disposable's port.
# ---------------------------------------------------------------------------
if grep -q "unset $OPTIN_VAR" "$RUNNER"; then
  pass "the runner clears $OPTIN_VAR before running the tests"
else
  fail "the runner must 'unset $OPTIN_VAR' - otherwise an inherited opt-in could authorise the appsettings.json target"
fi

if grep -q 'refusing to guess a data source' "$RUNNER"; then
  pass "the runner fails closed when it cannot resolve the disposable's published port"
else
  fail "the runner must refuse (not guess) a data source when the published port cannot be resolved"
fi

# ---------------------------------------------------------------------------
printf '%s\n' "---------------------------------------------------------------------"
if [ "$FAILURES" -gt 0 ]; then
  printf '%s\n' "CI DSN CONTRACT: FAILED ($FAILURES violation(s))" >&2
  exit 1
fi
printf '%s\n' "CI DSN CONTRACT: OK - every 'dotnet test' is granted an explicit, non-ambient data source"
