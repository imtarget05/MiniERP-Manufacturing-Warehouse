#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/start-db.sh
# PURPOSE: start the Oracle Database container and wait until it is usable.
# USAGE:   bash scripts/start-db.sh
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

rule
printf '%b\n' "${C_B} Starting the Oracle Database container${C_0}"
rule

need_cmd docker
cd "$REPO_ROOT"

docker compose up -d 2>&1 | sed 's/^/       /'
wait_for_db "${DB_TIMEOUT:-600}"

printf '%b\n' "  ${C_G}Oracle is ready.${C_0} schema user: $APP_USER  service: $PDB  port: 1521"
printf '%b\n' "  Next: bash scripts/run-sql.sh"
rule
