#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/qa-gate.sh  (library - source it, never execute it)
# PURPOSE: authorisation + TARGET IDENTITY gate for the mutating smoke / E2E
#          scripts: test-api.sh, test-traceability.sh, test-incident.sh.
#          They move stock, create purchase and production orders and close
#          change requests, so they must be refused unless the operator has
#          named the target AND the target can be PROVEN to be a throwaway.
#
# WHY THIS FILE IS SHAPED LIKE THIS - two incidents, not one theory:
#   1. scripts/test-api.sh resolved its base URL as
#        BASE_URL="${1:-http://localhost:5000}"
#      A POSITIONAL default, so an exported BASE_URL was ignored and a caller
#      that set nothing reached whatever answered on :5000. In this workspace
#      that is the production API; a 53-check smoke run wrote 41 rows.
#   2. Removing the default and requiring MINIERP_QA_DISPOSABLE=1 fixed the
#      accident but NOT the class: a self-declared acknowledgement is not
#      evidence. Pointed at :5000 with MINIERP_QA_DISPOSABLE=1 set, the gate
#      allowed the run and 43 more production rows were written.
#      So the disposable path is no longer a declaration, it is a PROOF: the
#      target has to be distinguishable from production by its own identity.
#
# IDENTITY RULES
#   A. Known production identities are named in one place below. For the
#      database, a match is ABSOLUTE: no flag, phrase or instance id can make
#      minierp-oracle a disposable. You may not aim a test at production and
#      call it a throwaway.
#   B. lib.sh's own defaults (DB_CONTAINER=minierp-oracle, APP_USER=erp_user,
#      BASE_URL=http://localhost:5000) ARE production identities. A value
#      equal to a default is treated as production, never as disposable -
#      "nobody overrode it" is not evidence of a fresh database.
#   C. MINIERP_QA_DISPOSABLE=1 is honoured only when the target carries a
#      disposable marker: a QA instance id whose token looks like a QA id
#      (qa / test / w5a / w5ar / a run-id shape), or QA in the database
#      container / project / volume name. API port and schema user may equal
#      a production default ONLY when such a marker is present and the
#      database identity is itself QA-marked.
#   D. If the target looks like production and no disposable proof exists,
#      MINIERP_QA_DISPOSABLE=1 is refused. Only
#      MINIERP_QA_LIVE_SMOKE='i-have-a-change-window' authorises it, with a
#      banner, because the go-live runbook has to stay runnable.
#
# WHAT THIS STILL DOES NOT PROVE: that a QA-named target is genuinely
# disposable rather than a production system someone renamed. Identity is
# evidence of separation, not of intent. See docs backlog W9-B7.
#
# GATE_MODES:
#   --gate-check   run the gate, print the resolved identity, exit 0 without
#                  making any request.
#
# EXIT: 0 authorised (or --gate-check passed), 78 (EX_CONFIG) refused.
# ============================================================================

QA_GATE_LIVE_PHRASE='i-have-a-change-window'

# --- the one place production identities are declared -------------------------
QA_PROD_API_PORTS='5000 8080'                 # production API and dashboard
QA_PROD_DB_NAMES='minierp-oracle minierp-api minierp-ui it-portal'
QA_PROD_VOLUMES='04-minierp-manufacturing-warehouse_oracle_data'
QA_PROD_PROJECTS='04-minierp-manufacturing-warehouse'
QA_PROD_USERS='erp_user'                      # lib.sh's default schema
# lib.sh substitutes these when the environment says nothing, so "unset" and
# "explicitly set to the default" must be judged identically - as production.
QA_DEFAULT_API_URL='http://localhost:5000'
QA_DEFAULT_DB_CONTAINER='minierp-oracle'
QA_DEFAULT_APP_USER='erp_user'

# qa_gate_sanitize <url> -> scheme://host[:port]; userinfo, path and query removed
qa_gate_sanitize() {
  local u="${1:-}" scheme="http"
  case "$u" in
    https://*) scheme="https"; u="${u#https://}" ;;
    http://*)              u="${u#http://}" ;;
  esac
  u="${u##*@}"     # drop any user:password@ userinfo
  u="${u%%/*}"     # drop path and query
  [ -z "$u" ] && return 0
  printf '%s://%s' "$scheme" "$u"
}

# qa_gate_sanitize_db <db_container> <app_user> -> "container/schema", the
# database identity in the same one-line shape qa_gate_sanitize gives the API.
# It used to be called at the bottom of qa_gate() but never defined, so every
# database target printed "command not found" and then an empty target line -
# the one line an operator reads to see WHICH database was authorised. An unset
# component is spelled out rather than left blank, so the line can never be
# misread as the production default.
qa_gate_sanitize_db() {
  local db="${1:-}" usr="${2:-}"
  [ -n "$db" ] || db="<unset container>"
  [ -n "$usr" ] || usr="<unset schema>"
  printf '%s/%s' "$db" "$usr"
}

qa_gate_port() {  # qa_gate_port <url> -> the port, or "" when the URL has none
  # The scheme has to come off FIRST: stripping at the first "/" would cut at
  # the "//" and leave "http:", which is why an earlier version classified every
  # endpoint UNCLASSIFIED and the port rule never fired.
  local u="${1:-}"
  case "$u" in
    http://*)  u="${u#http://}"  ;;
    https://*) u="${u#https://}" ;;
  esac
  u="${u##*@}"     # userinfo
  u="${u%%/*}"     # path and query
  case "$u" in
    *:[0-9]*) printf '%s' "${u##*:}" ;;
    *)        printf '' ;;
  esac
}

# qa_gate_list_has <needle> <space separated haystack>
qa_gate_list_has() {
  local needle="${1:-}" item
  [ -z "$needle" ] && return 1
  for item in $2; do [ "$item" = "$needle" ] && return 0; done
  return 1
}

# qa_gate_is_qa_token <value> -> 0 when the value looks like a QA / test id
# Anything named "qa", "test", "w5a", "w5ar" or a run id with a separator is
# treated as a throwaway marker. Deliberately narrow: "prod" is not a match.
qa_gate_is_qa_token() {
  local v; v="$(printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]')"
  case "$v" in
    *qa*|*test*|*w5a*|*w5ar*|*tmp*|*scratch*|*throwaway*) return 0 ;;
  esac
  # run-id shape: something-<digits> or something_<digits>
  case "$v" in
    *-[0-9]*|*_[0-9]*) return 0 ;;
  esac
  return 1
}

# qa_gate_identity <base_url> <db_container> <db_volume> <db_project> <app_user> [needs_api 1]
# needs_api 0 for a database-only script: there is no endpoint to classify, and an
# UNCLASSIFIED api must not drag the overall verdict down to UNPROVEN.
# Prints one VERDICT line per dimension plus an overall one, e.g.
#   VERDICT api        = PRODUCTION (port 5000 is the production API port)
#   VERDICT db_name    = QA (container 'minierp-qa-oracle')
#   VERDICT user       = QA (schema 'ERPQA2026')
#   VERDICT overall    = DISPOSABLE
qa_gate_identity() {
  local url="$1" db="$2" vol="$3" proj="$4" usr="$5" needs_api="${6:-1}"
  local port qa_marker="" prod_reasons="" qa_reasons="" overall="DISPOSABLE"

  QA_ID_PORT=""; QA_ID_API="UNCLASSIFIED"; QA_ID_DB="UNCLASSIFIED"
  QA_ID_USER="UNCLASSIFIED"; QA_ID_OVERALL="DISPOSABLE"; QA_ID_MARKER="${MINIERP_QA_INSTANCE:-}"

  port=""
  [ "$needs_api" = "1" ] && port="$(qa_gate_port "$url")"
  QA_ID_PORT="${port:-<none>}"

  # ---- API endpoint ------------------------------------------------------
  if [ "$needs_api" = "0" ]; then
    QA_ID_API="N/A"
  elif [ -z "$port" ]; then
    QA_ID_API="UNCLASSIFIED"; prod_reasons="$prod_reasons
    api: no port in the URL, so it cannot be shown to be a throwaway"
    overall="UNPROVEN"
  elif qa_gate_list_has "$port" "$QA_PROD_API_PORTS"; then
    QA_ID_API="PRODUCTION"; prod_reasons="$prod_reasons
    api: port $port is a production port ($QA_PROD_API_PORTS)"
  else
    QA_ID_API="QA"; qa_reasons="$qa_reasons
    api: port $port is not a production port"
  fi
  if [ "$url" = "$QA_DEFAULT_API_URL" ]; then
    QA_ID_API="PRODUCTION"; overall="UNPROVEN"
    prod_reasons="$prod_reasons
    api: the URL is lib.sh's own default ($QA_DEFAULT_API_URL), i.e. nothing was overridden"
  fi

  # ---- database identity: ABSOLUTE on a known production name ------------
  if [ -z "$db" ]; then
    QA_ID_DB="UNCLASSIFIED"; overall="UNPROVEN"
    prod_reasons="$prod_reasons
    db: DB_CONTAINER is not set, so lib.sh would use its default ($QA_DEFAULT_DB_CONTAINER)"
  elif [ "$db" = "$QA_DEFAULT_DB_CONTAINER" ]; then
    QA_ID_DB="PRODUCTION"; overall="PRODUCTION"
    prod_reasons="$prod_reasons
    db: container '$db' IS lib.sh's default and a known production container"
  elif qa_gate_list_has "$db" "$QA_PROD_DB_NAMES"; then
    QA_ID_DB="PRODUCTION"; overall="PRODUCTION"
    prod_reasons="$prod_reasons
    db: container '$db' is a known production container"
  elif qa_gate_is_qa_token "$db"; then
    QA_ID_DB="QA"; qa_reasons="$qa_reasons
    db: container '$db' carries a QA/test marker"
  else
    QA_ID_DB="UNPROVEN"; overall="UNPROVEN"
    prod_reasons="$prod_reasons
    db: container '$db' is neither a known production name nor QA-marked"
  fi
  for v in $vol; do
    if [ -n "$v" ] && qa_gate_list_has "$v" "$QA_PROD_VOLUMES"; then
      QA_ID_DB="PRODUCTION"; overall="PRODUCTION"
      prod_reasons="$prod_reasons
    volume: '$v' is a known production volume"
    fi
  done
  if [ -n "$proj" ] && qa_gate_list_has "$proj" "$QA_PROD_PROJECTS"; then
    QA_ID_DB="PRODUCTION"; overall="PRODUCTION"
    prod_reasons="$prod_reasons
    compose project '$proj' is a known production project"
  fi

  # ---- schema user -------------------------------------------------------
  if [ -z "$usr" ]; then
    QA_ID_USER="UNCLASSIFIED"; overall="UNPROVEN"
    prod_reasons="$prod_reasons
    user: APP_USER is not set, so lib.sh would use its default ($QA_DEFAULT_APP_USER)"
  elif [ "$usr" = "$QA_DEFAULT_APP_USER" ] || qa_gate_list_has "$usr" "$QA_PROD_USERS"; then
    QA_ID_USER="PRODUCTION"; overall="UNPROVEN"
    prod_reasons="$prod_reasons
    user: schema '$usr' is the production default schema"
  elif qa_gate_is_qa_token "$usr"; then
    QA_ID_USER="QA"; qa_reasons="$qa_reasons
    user: schema '$usr' carries a QA/test marker"
  else
    QA_ID_USER="UNPROVEN"; overall="UNPROVEN"
    prod_reasons="$prod_reasons
    user: schema '$usr' is neither the production default nor QA-marked"
  fi

  # ---- the marker that can excuse a production PORT or USER --------------
  if [ -n "$QA_ID_MARKER" ] && qa_gate_is_qa_token "$QA_ID_MARKER"; then
    qa_marker="yes"
    qa_reasons="$qa_reasons
    instance: MINIERP_QA_INSTANCE='$QA_ID_MARKER' is QA-marked"
  fi

  if [ "$QA_ID_DB" = "PRODUCTION" ]; then
    overall="PRODUCTION"
  elif [ "$QA_ID_API" = "PRODUCTION" ] || [ "$QA_ID_USER" = "PRODUCTION" ]; then
    if [ "$qa_marker" = "yes" ] && [ "$QA_ID_DB" = "QA" ]; then
      overall="DISPOSABLE"
      qa_reasons="$qa_reasons
    the QA instance + a QA database identity are why the production-shaped port/user are accepted"
    else
      overall="UNPROVEN"
    fi
  fi

  QA_ID_OVERALL="$overall"
  QA_ID_PROD_REASONS="$prod_reasons"
  QA_ID_QA_REASONS="$qa_reasons"
}

# qa_gate_print_identity <script> <base_url> <needs_db> <needs_api>
qa_gate_print_identity() {
  local script="$1" url="$2" needs_db="$3" needs_api="$4"
  local needs_db_target="$needs_db"
  [ "$needs_db" = "0" ] && needs_db_target=1
  qa_gate_identity "$url" "${DB_CONTAINER_IN:-}" "${QA_DB_VOLUME_IN:-}" "${QA_DB_PROJECT_IN:-}" "${APP_USER_IN:-}" "$needs_api"
  printf '%b\n' "  identity  api=$QA_ID_API:$QA_ID_PORT db=$QA_ID_DB user=$QA_ID_USER  => ${QA_ID_OVERALL}"
  if [ -n "$QA_ID_QA_REASONS" ]; then
    printf '%s\n' "  disposable evidence:$QA_ID_QA_REASONS" | sed 's/^/       /'
  fi
  if [ -n "$QA_ID_PROD_REASONS" ]; then
    printf '%s\n' "  not-proven evidence:$QA_ID_PROD_REASONS" | sed 's/^/       /'
  fi
}

qa_gate_refuse() {
  local reason="$1" script="$2"
  printf '%b\n' "${C_R}REFUSED${C_0} $script: $reason" >&2
  printf '%b\n' "  This script writes to the database it is pointed at (stock," >&2
  printf '%b\n' "  purchase/production orders, change requests). It will not" >&2
  printf '%b\n' "  guess a target, and it will not take your word for one." >&2
  printf '%b\n' >&2
  printf '%s\n'    "    # a real throwaway: the identity has to prove it" >&2
  printf '%s\n'    "    MINIERP_QA_DISPOSABLE=1 MINIERP_QA_INSTANCE=<qa-run-id> \\" >&2
  printf '%s\n'    "    DB_CONTAINER=<qa-container> APP_USER=<qa-schema> \\" >&2
  if [ "$3" = "1" ]; then
    printf '%s\n'  "    bash scripts/$script <disposable_base_url>" >&2
  else
    printf '%s\n'  "    bash scripts/$script" >&2
  fi
  printf '%b\n' >&2
  printf '%s\n' "    # deliberately against a real system (go-live smoke only)" >&2
  printf '%s\n'    "    MINIERP_QA_LIVE_SMOKE='$QA_GATE_LIVE_PHRASE' \\" >&2
  if [ "$3" = "1" ]; then
    printf '%s\n'  "    bash scripts/$script <real_base_url>" >&2
  else
    printf '%s\n'  "    bash scripts/$script" >&2
  fi
  printf '%b\n' >&2
  printf '%s\n' "  Add --gate-check to print the resolved identity and exit" >&2
  printf '%s\n' "  without contacting anything." >&2
  exit 78
}

# qa_gate_parse_args <script> "$@" ...  (order independent)
#   <url>          the base URL (not required for a database-only script)
#   --gate-check   authorise, print the identity, contact nothing
#   -h/--help      print the script header and exit 0
qa_gate_parse_args() {
  local script="$1"; shift
  QA_BASE_URL=""; QA_GATE_MODE="run"
  local a
  for a in "$@"; do
    case "$a" in
      -h|--help)    sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//; s/^#$//' >&2; exit 0 ;;
      --gate-check) QA_GATE_MODE="gate-check" ;;
      -*)           : ;;
      *)            [ -z "$QA_BASE_URL" ] && QA_BASE_URL="$a" ;;
    esac
  done
  if [ -z "$QA_BASE_URL" ]; then QA_BASE_URL="${BASE_URL_IN:-}"; fi
}

# qa_gate <script> <base_url> <needs_db 0|1> [needs_api 1] [mode run|gate-check]
qa_gate() {
  local script="$1" base_url="${2:-}" needs_db="${3:-0}" needs_api="${4:-1}"
  # The mode is the FIFTH argument, not $4: passing the mode where needs_api
  # belongs silently disabled the API authorisation rules.
  local mode="${5:-${MINIERP_QA_GATE_MODE:-run}}"
  local live="${MINIERP_QA_LIVE_SMOKE:-}"
  local target needs_db_target

  needs_db_target="$needs_db"; [ "$needs_db" = "0" ] && needs_db_target=1

  # 1. an API target must exist and must not be a default we invented
  if [ "$needs_api" = "1" ] && [ -z "$base_url" ]; then
    qa_gate_refuse "no base URL given (there is no default; pass it as \$1 or export BASE_URL)" "$script" "$needs_db_target"
  fi

  # 2. DB scripts must not inherit the shared container/schema from lib.sh.
  #    *_IN values are captured by the caller BEFORE lib.sh is sourced: after
  #    sourcing, DB_CONTAINER is always a value, so reading it here would be
  #    vacuous.
  if [ "$needs_db" = "1" ]; then
    if [ -z "${DB_CONTAINER_IN:-}" ] || [ -z "${APP_USER_IN:-}" ]; then
      qa_gate_refuse "DB_CONTAINER and APP_USER must be set in the environment; unset means lib.sh would use the shared production defaults" "$script" "$needs_db_target"
    fi
  fi

  # 3. the target must have an identity before any acknowledgement counts
  qa_gate_identity "$base_url" "${DB_CONTAINER_IN:-}" "${QA_DB_VOLUME_IN:-}" "${QA_DB_PROJECT_IN:-}" "${APP_USER_IN:-}" "$needs_api"
  target="$(qa_gate_sanitize "$base_url")"
  local is_live=0
  [ "$live" = "$QA_GATE_LIVE_PHRASE" ] && is_live=1

  if [ "$is_live" = "1" ]; then
    # A deliberate live smoke is allowed to look exactly like production. That
    # is the whole point of the phrase, and the banner says so loudly.
    printf '%b\n' "${C_Y}################################################################${C_0}" >&2
    printf '%b\n' "${C_Y}#  LIVE SMOKE AUTHORISED - THIS RUN WILL WRITE TO A REAL SYSTEM      #${C_0}" >&2
    printf '%b\n' "${C_Y}################################################################${C_0}" >&2
  elif [ "${MINIERP_QA_DISPOSABLE:-}" = "1" ]; then
    if [ "$QA_ID_OVERALL" != "DISPOSABLE" ]; then
      qa_gate_print_identity "$script" "$base_url" "$needs_db" "$needs_api"
      qa_gate_refuse "MINIERP_QA_DISPOSABLE=1 is set, but the target's identity is $QA_ID_OVERALL - an acknowledgement is not proof. Name a real throwaway (QA-marked container, project, volume and schema, and MINIERP_QA_INSTANCE), or use MINIERP_QA_LIVE_SMOKE if this really is the live system" "$script" "$needs_db_target"
    fi
  else
    qa_gate_print_identity "$script" "$base_url" "$needs_db" "$needs_api"
    qa_gate_refuse "no authorisation: the target identity is $QA_ID_OVERALL and no acknowledgement was given" "$script" "$needs_db_target"
  fi

  if [ "$is_live" = "1" ]; then
    printf '%s\n' "  as    -> live-smoke (MINIERP_QA_LIVE_SMOKE)"
  else
    printf '%s\n' "  as    -> disposable (MINIERP_QA_DISPOSABLE=1, identity proven DISPOSABLE)"
  fi
  printf '%b\n' "${C_B}QA-GATE${C_0} $script authorised"
  [ "$needs_api" = "1" ] && printf '%s\n' "  api   -> $target"
  [ "$needs_db" = "1" ] && printf '%s\n' "  db    -> $(qa_gate_sanitize_db "${DB_CONTAINER_IN:-}" "${APP_USER_IN:-}")" \
                           "${QA_DB_VOLUME_IN:+$([ -n "$QA_DB_VOLUME_IN" ] && printf '  vol    -> %s' "$QA_DB_VOLUME_IN")}" \
                           "${QA_DB_PROJECT_IN:+$([ -n "$QA_DB_PROJECT_IN" ] && printf '  proj   -> %s' "$QA_DB_PROJECT_IN")}"
  qa_gate_print_identity "$script" "$base_url" "$needs_db" "$needs_api"

  if [ "$mode" = "gate-check" ]; then
    printf '%s\n' "  --gate-check: authorised, no request made."
    exit 0
  fi
}
