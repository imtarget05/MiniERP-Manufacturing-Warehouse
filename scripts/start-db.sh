#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/start-db.sh
# PURPOSE: start the Oracle Database container and wait until it is usable.
# USAGE:   bash scripts/start-db.sh
#          MINIERP_QA_INSTANCE=minierp-qa bash scripts/start-db.sh
#
#          When MINIERP_QA_INSTANCE is set the stack is started with the QA
#          identity profile (docker-compose.qa.yml, compose project
#          $MINIERP_QA_INSTANCE), so the database container and volume are NOT
#          called minierp-oracle / 04-minierp-manufacturing-warehouse_oracle_data.
#          scripts/qa-gate.sh treats those two names as production under any
#          flag, so a throwaway that keeps them can never pass the gate.
#          Without MINIERP_QA_INSTANCE the historical names are kept, so a plain
#          `bash scripts/start-db.sh` behaves exactly as before.
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

rule
printf '%b\n' "${C_B} Starting the Oracle Database container${C_0}"
rule

need_cmd docker
cd "$REPO_ROOT"

QA_INSTANCE="${MINIERP_QA_INSTANCE:-}"
if [ -n "$QA_INSTANCE" ]; then
  export QA_CONTAINER_NAME="${QA_CONTAINER_NAME:-minierp-qa-oracle}"
  COMPOSE=(docker compose -p "$QA_INSTANCE"
           -f docker-compose.yml -f docker-compose.qa.yml)
  printf '%b\n' "  QA identity: project=$QA_INSTANCE  oracle container=$QA_CONTAINER_NAME"
  printf '%s\n' "  (docker-compose.qa.yml: the names scripts/qa-gate.sh calls production are not used here)"
else
  COMPOSE=(docker compose)
fi

"${COMPOSE[@]}" up -d 2>&1 | sed 's/^/       /'
if [ -n "$QA_INSTANCE" ]; then
  DB_CONTAINER="$QA_CONTAINER_NAME"; export DB_CONTAINER
  QA_DB_PROJECT="$QA_INSTANCE";      export QA_DB_PROJECT
  QA_DB_VOLUME="${QA_INSTANCE}_oracle_data"; export QA_DB_VOLUME
  printf '%b\n' "  gate identity -> DB_CONTAINER=$DB_CONTAINER QA_DB_PROJECT=$QA_DB_PROJECT QA_DB_VOLUME=$QA_DB_VOLUME"
fi
wait_for_db "${DB_TIMEOUT:-600}"

printf '%b\n' "  ${C_G}Oracle is ready.${C_0} schema user: $APP_USER  service: $PDB  port: 1521"
printf '%b\n' "  Next: bash scripts/run-sql.sh"
rule
