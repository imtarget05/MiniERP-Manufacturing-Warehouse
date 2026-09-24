#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/backup-db.sh
# PURPOSE: Creates a timestamped backup of the MiniERP Oracle database schema.
#          Captures Data Pump dump (or schema DDL snapshot) + metadata.
#          Never logs or echoes database passwords.
# USAGE:   bash scripts/backup-db.sh [output_dir]
# ============================================================================
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

BACKUP_ROOT="${1:-$REPO_ROOT/backups}"
TIMESTAMP="$(date +%Y%m%d_%H%M%S)"
TARGET_DIR="$BACKUP_ROOT/backup_$TIMESTAMP"
DUMP_NAME="minierp_${TIMESTAMP}.dmp"
LOG_NAME="minierp_${TIMESTAMP}.log"
META_FILE="$TARGET_DIR/metadata.json"

rule
printf '%b\n' "${C_B} MiniERP Database Backup Utility${C_0}"
rule

mkdir -p "$TARGET_DIR" || die "Cannot create backup directory: $TARGET_DIR"
log "Target backup directory: $TARGET_DIR"

# 1. Capture Environment & Git Metadata
GIT_COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo "unknown")"
GIT_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")"

# 2. Check Oracle Connectivity & Schema State
db_probe || die "Oracle database is not reachable at $APP_USER@$PDB - start it first with: bash scripts/start-db.sh"

log "Gathering database schema inventory..."
probe_sql="$(mktemp)"
probe_log="$(mktemp)"
cat << 'EOF' > "$probe_sql"
SET PAGESIZE 0 FEEDBACK OFF LINESIZE 200
SELECT 'TBL_COUNT=' || COUNT(*) FROM user_tables;
SELECT 'PKG_COUNT=' || COUNT(*) FROM user_objects WHERE object_type = 'PACKAGE BODY' AND status = 'VALID';
SELECT 'LOT_TABLE=' || COUNT(*) FROM user_tables WHERE table_name = 'INVENTORY_LOT';
SELECT 'LOT_ROWS=' || COUNT(*) FROM inventory_lot;
SELECT 'HD_ROWS=' || COUNT(*) FROM helpdesk_delivery;
EXIT;
EOF

run_sql "$probe_sql" "$probe_log" || die "Database inventory query failed"
if sql_unexpected_error "$probe_log"; then
  err "Database inventory query returned an Oracle error"
  tail -25 "$probe_log" >&2
  exit 1
fi
TBL_COUNT="$(grep -oE 'TBL_COUNT=[0-9]+' "$probe_log" | tail -1 | cut -d= -f2 || true)"
PKG_COUNT="$(grep -oE 'PKG_COUNT=[0-9]+' "$probe_log" | tail -1 | cut -d= -f2 || true)"
LOT_COUNT="$(grep -oE 'LOT_TABLE=[0-9]+' "$probe_log" | tail -1 | cut -d= -f2 || true)"
LOT_ROWS="$(grep -oE 'LOT_ROWS=[0-9]+' "$probe_log" | tail -1 | cut -d= -f2 || true)"
HD_ROWS="$(grep -oE 'HD_ROWS=[0-9]+' "$probe_log" | tail -1 | cut -d= -f2 || true)"
[ -n "$TBL_COUNT" ] || die "Could not read table count"
[ -n "$PKG_COUNT" ] || die "Could not read valid package count"
[ -n "$LOT_COUNT" ] || die "Could not read traceability table state"
LOT_COUNT=$((LOT_COUNT > 0 ? 1 : 0))
LOT_ROWS="${LOT_ROWS:-0}"
HD_ROWS="${HD_ROWS:-0}"
rm -f "$probe_sql" "$probe_log"

log "Detected schema tables: $TBL_COUNT, valid packages: $PKG_COUNT"

# 3. Perform Data Pump export. A real .dmp is required for this image.
need_cmd docker
docker inspect "$DB_CONTAINER" >/dev/null 2>&1 \
  || die "container '$DB_CONTAINER' is not up - run: docker compose up -d"
# The container starts a randomized directory subdirectory for DATA_PUMP_DIR.
# Resolve the object path through SYSTEM instead of assuming a fixed filename.
DP_PATH="$(docker exec "$DB_CONTAINER" bash -lc \
  'printf "SELECT directory_path FROM dba_directories WHERE directory_name = '\''DATA_PUMP_DIR'\'';\nEXIT;\n" | sqlplus -S "system/${ORACLE_PASSWORD}@FREEPDB1" | grep "^/" | tail -1' | tr -d '\r' || true)"
[ -n "$DP_PATH" ] || die "Oracle DATA_PUMP_DIR could not be resolved"
# Grant access through SYSTEM, without printing the password.
docker exec -i "$DB_CONTAINER" bash -lc \
  "printf 'GRANT READ, WRITE ON DIRECTORY DATA_PUMP_DIR TO $APP_USER;\nEXIT;\n' | sqlplus -S \"system/\${ORACLE_PASSWORD}@$PDB\"" \
  >/tmp/minierp_dp_grant.log 2>&1 || die "Could not grant Data Pump directory access"
log "Executing Oracle Data Pump Export (expdp)..."
if ! docker exec -i \
    -e "DB_USER=$APP_USER" -e "DB_PASSWORD=$APP_USER_PWD" -e "DB_TNS=$PDB" \
    "$DB_CONTAINER" bash -lc \
    'expdp "$DB_USER/$DB_PASSWORD@$DB_TNS" schemas="$DB_USER" directory=DATA_PUMP_DIR dumpfile="$1" logfile="$2" reuse_dumpfiles=YES' \
    _ "$DUMP_NAME" "$LOG_NAME" > "$TARGET_DIR/expdp_exec.log" 2>&1; then
  err "expdp failed; inspect redacted log: $TARGET_DIR/expdp_exec.log"
  exit 1
fi
docker cp "$DB_CONTAINER:$DP_PATH/$DUMP_NAME" "$TARGET_DIR/$DUMP_NAME" >/dev/null \
  || die "expdp returned success but dump file could not be copied"
docker cp "$DB_CONTAINER:$DP_PATH/$LOG_NAME" "$TARGET_DIR/$LOG_NAME" >/dev/null 2>&1 || true
[ -s "$TARGET_DIR/$DUMP_NAME" ] || die "Data Pump dump is empty"
DUMP_SUCCESS=1
ok "Data Pump dump exported: $(basename "$TARGET_DIR/$DUMP_NAME")"

# 4. Generate Snapshot SQL Archive (DDL & table counts)
snapshot_sql="$(mktemp)"
cat << 'EOF' > "$snapshot_sql"
SET PAGESIZE 5000 LINESIZE 200 FEEDBACK OFF HEADING ON
PROMPT === USER TABLES ===
SELECT table_name, num_rows, last_analyzed FROM user_tables ORDER BY table_name;
PROMPT === VALID PACKAGES ===
SELECT object_name, object_type, status FROM user_objects WHERE object_type LIKE 'PACKAGE%' ORDER BY object_name;
PROMPT === WAREHOUSE INVENTORY ===
SELECT w.code AS wh_code, i.code AS item_code, s.qty, i.safety_stock
  FROM stock s JOIN warehouse w ON s.warehouse_id = w.id
  JOIN item i ON s.item_id = i.id ORDER BY w.code, i.code;
EXIT;
EOF
run_sql "$snapshot_sql" "$TARGET_DIR/schema_snapshot.txt" \
  || die "SQL inventory snapshot failed"
if sql_unexpected_error "$TARGET_DIR/schema_snapshot.txt"; then
  err "SQL inventory snapshot returned an Oracle error"
  tail -25 "$TARGET_DIR/schema_snapshot.txt" >&2
  exit 1
fi
[ -s "$TARGET_DIR/schema_snapshot.txt" ] || die "SQL inventory snapshot is empty"
rm -f "$snapshot_sql"

# 5. Write metadata JSON
cat << EOF > "$META_FILE"
{
  "timestamp": "$TIMESTAMP",
  "gitCommit": "$GIT_COMMIT",
  "gitBranch": "$GIT_BRANCH",
  "database": {
    "pdb": "$PDB",
    "schema": "$APP_USER",
    "tableCount": $TBL_COUNT,
    "validPackages": $PKG_COUNT,
    "lotTraceabilityReady": $([ "$LOT_COUNT" -gt 0 ] && echo "true" || echo "false"),
    "lotRows": $LOT_ROWS,
    "helpdeskRows": $HD_ROWS
  },
  "artifacts": {
    "dumpFile": "$DUMP_NAME",
    "snapshot": "schema_snapshot.txt"
  },
  "status": "COMPLETED"
}
EOF

ok "Backup completed successfully at $TARGET_DIR"
ok "Metadata saved to $META_FILE"
exit 0
