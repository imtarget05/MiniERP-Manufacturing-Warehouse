#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/portable-tmp.sh  (library - source it, never execute it)
# PURPOSE: portable scratch files for the smoke/E2E scripts.
#
# WHY: `mktemp /tmp/trace-recon-XXXX.sql` is a GNU-ism that reads as portable
# and is not. mktemp substitutes a run of X only when that run is the LAST
# component of the template. On macOS/BSD
#
#     mktemp /tmp/trace-recon-XXXX.sql   ->  /tmp/trace-recon-XXXX.sql   (literal)
#
# so every run collides on one fixed path, a stale file from an earlier run is
# reused as if it were this run's, and that leftover hard-fails the next run
# (this is what blocked the traceability acceptance run). Trailing-only
# templates such as `mktemp /tmp/trace-recon-before-XXXX` are already correct
# and are left alone.
#
# WHAT THIS DOES: one scratch DIRECTORY per run, created from a template whose
# X run IS trailing, and every named file lives inside it. The .sql suffix is
# kept (lib.sh's run_sql cares about it) without giving up portability.
# portable_tmp_sweep_legacy then removes the literal paths an older broken
# mktemp could have left behind, so a stale file can neither be picked up as a
# snapshot nor block a later run.
#
# USAGE:
#   source scripts/portable-tmp.sh
#   TMPD="$(portable_tmpdir trace-recon)" || exit 1
#   portable_tmp_sweep_legacy
#   file="$(portable_tmp_path "$TMPD" recon.sql)"
#   ... ; rm -rf "$TMPD"
# ============================================================================

# The literal paths the pre-fix templates resolve to. Kept as data so this file
# is the single place that knows the old names.
PORTABLE_TMP_LEGACY_PATHS='/tmp/trace-recon-XXXX.sql /tmp/trace-fault-XXXX.sql'

# portable_tmp_template -> the exact template portable_tmpdir hands to mktemp.
# Exposed so a contract check can assert on the template itself rather than on
# the shape of the directory it happens to produce.
portable_tmp_template() {
  printf '${TMPDIR:-/tmp}/%s.XXXXXX' "${1:-scratch}"
}

# portable_tmpdir <prefix> -> a fresh private scratch directory, or "" on failure
portable_tmpdir() {
  local prefix="${1:-scratch}" dir
  # XXXXXX is TRAILING, so BSD mktemp substitutes it; GNU mktemp accepts the
  # same template. ${TMPDIR:-/tmp} keeps the file on the filesystem it would
  # have used anyway.
  dir="$(mktemp -d "${TMPDIR:-/tmp}/${prefix}.XXXXXX" 2>/dev/null)" || dir=""
  [ -n "$dir" ] && [ -d "$dir" ] || return 1
  printf '%s' "$dir"
}

# portable_tmp_path <dir> <basename> -> a path inside <dir> (the file is not created)
portable_tmp_path() {
  printf '%s/%s' "${1:?portable_tmp_path: dir required}" "${2:?portable_tmp_path: basename required}"
}

# portable_tmp_sweep_legacy -> delete the stale literal paths left by the old
# non-trailing templates. Prints one line per file removed and never fails: a
# leftover file from another run must not abort this one.
portable_tmp_sweep_legacy() {
  local legacy
  for legacy in $PORTABLE_TMP_LEGACY_PATHS; do
    [ -n "$legacy" ] || continue
    if [ -e "$legacy" ]; then
      if rm -f "$legacy" 2>/dev/null; then
        printf 'removed stale literal temp path: %s\n' "$legacy"
      fi
    fi
  done
  return 0
}

# portable_tmp_has_non_trailing_x <template> -> 0 (true) when the template
# contains an X run that is followed by something other than the end of the
# template or a path separator, i.e. exactly the pattern mktemp will NOT
# substitute on BSD/macOS. Used by scripts/check-portable-mktemp.sh so the rule
# is enforced rather than only documented.
portable_tmp_has_non_trailing_x() {
  local rest="${1:-}"
  while :; do
    case "$rest" in
      *XXXXXX*) rest="${rest#*XXXXXX}" ;;
      *XXXX*)  rest="${rest#*XXXX}"  ;;
      *XXX*)   rest="${rest#*XXX}"   ;;
      *XX*)    rest="${rest#*XX}"    ;;
      *X*)     rest="${rest#*X}"     ;;
      *)       return 1 ;;
    esac
    case "$rest" in
      ''|/) return 1 ;;   # trailing X run: portable
      *)     return 0 ;;   # something follows the X run: not portable
    esac
  done
}
