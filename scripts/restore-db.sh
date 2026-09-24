#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/restore-db.sh
# PURPOSE: Restore a real Oracle Data Pump backup into a clean local target.
# SAFETY:  requires both ALLOW_DESTRUCTIVE_RESTORE=true and CONFIRM_RESTORE=yes
# USAGE:   ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes \
#            bash scripts/restore-db.sh <backup_dir_or_dump>
# ============================================================================
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

CONFIRM="${CONFIRM_RESTORE:-no}"
ALLOW_DESTRUCTIVE="${ALLOW_DESTRUCTIVE_RESTORE:-no}"
BACKUP_INPUT=""

for arg in "$@"; do
  case "$arg" in
    --confirm) CONFIRM="yes" ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) [ -z "$BACKUP_INPUT" ] && BACKUP_INPUT="$arg" || die "Only one backup input is allowed" ;;
  esac
done

rule
printf '%b\n' "${C_B} MiniERP Database Restore Utility${C_0}"
rule
[ "$ALLOW_DESTRUCTIVE" = "true" ] || {
  err "SAFETY LOCK: set ALLOW_DESTRUCTIVE_RESTORE=true to permit destructive restore."
  exit 1
}
[ "$CONFIRM" = "yes" ] || {
  err "SAFETY LOCK: set CONFIRM_RESTORE=yes or pass --confirm."
  exit 1
}
[ -n "$BACKUP_INPUT" ] || die "Missing backup directory or .dmp file."
[ -e "$BACKUP_INPUT" ] || die "Backup path does not exist: $BACKUP_INPUT"

if [ -d "$BACKUP_INPUT" ]; then
  DMP_FILE="$(find "$BACKUP_INPUT" -maxdepth 1 -type f -name '*.dmp' | head -1)"
  EXPECTED_LOT_ROWS="$(python3 - "$BACKUP_INPUT/metadata.json" <<'PY'
import json, sys
try:
    print(int(json.load(open(sys.argv[1]))["database"].get("lotRows", 0)))
except Exception:
    print(0)
PY
)"
else
  DMP_FILE="$BACKUP_INPUT"
  EXPECTED_LOT_ROWS="${EXPECTED_LOT_ROWS:-0}"
fi
[ -n "$DMP_FILE" ] && [ -s "$DMP_FILE" ] \
  || die "A non-empty Oracle Data Pump .dmp file is required; seed reload is not a backup restore."

need_cmd docker
docker inspect "$DB_CONTAINER" >/dev/null 2>&1 \
  || die "container '$DB_CONTAINER' is not up - run: docker compose up -d"
db_probe || die "Oracle database is not reachable at $APP_USER@$PDB"
DP_PATH="$(docker exec "$DB_CONTAINER" bash -lc \
  'printf "SELECT directory_path FROM dba_directories WHERE directory_name = '\''DATA_PUMP_DIR'\'';\nEXIT;\n" | sqlplus -S "system/${ORACLE_PASSWORD}@FREEPDB1" | grep "^/" | tail -1' | tr -d '\r' || true)"
[ -n "$DP_PATH" ] || die "Oracle DATA_PUMP_DIR could not be resolved"
docker exec -i "$DB_CONTAINER" bash -lc \
  "printf 'GRANT READ, WRITE ON DIRECTORY DATA_PUMP_DIR TO $APP_USER;\nEXIT;\n' | sqlplus -S \"system/\${ORACLE_PASSWORD}@$PDB\"" \
  >/tmp/minierp_restore_dp_grant.log 2>&1 || die "Could not grant Data Pump directory access"

# Recreate a genuinely empty application schema. Reusing the migration schema
# leaves PL/SQL package objects behind, which makes a full schema import fail
# with ORA-31684 even though table replacement is requested.
reset_target_schema() {
  [ -n "$APP_USER" ] && [ -n "$APP_USER_PWD" ] || die "APP_USER and APP_USER_PWD are required for a clean restore."
  [[ "$APP_USER" =~ ^[A-Za-z][A-Za-z0-9_$#]*$ ]] || die "APP_USER is not a safe Oracle identifier."
  APP_USER_SQL="$(printf '%s' "$APP_USER" | tr '[:lower:]' '[:upper:]')"
  docker exec -i \
    -e "APP_USER_SQL=$APP_USER_SQL" -e "APP_PASSWORD=$APP_USER_PWD" -e "DB_TNS=$PDB" \
    "$DB_CONTAINER" bash -lc '
      set -e
      sqlplus -S "system/${ORACLE_PASSWORD}@${DB_TNS}" <<SQL
DROP USER $APP_USER_SQL CASCADE;
CREATE USER $APP_USER_SQL IDENTIFIED BY "$APP_PASSWORD";
GRANT CREATE SESSION TO $APP_USER_SQL;
ALTER USER $APP_USER_SQL QUOTA UNLIMITED ON USERS;
GRANT CREATE TABLE, CREATE SEQUENCE, CREATE TRIGGER, CREATE PROCEDURE, CREATE TYPE, CREATE VIEW, CREATE SYNONYM TO $APP_USER_SQL;
GRANT RESOURCE TO $APP_USER_SQL;
GRANT READ, WRITE ON DIRECTORY DATA_PUMP_DIR TO $APP_USER_SQL;
EXIT;
SQL
    ' >/tmp/minierp_restore_prepare.log 2>&1 \
    || { err "Could not create clean target schema."; tail -30 /tmp/minierp_restore_prepare.log >&2; exit 1; }
}
log "Creating clean target schema before Data Pump import..."
reset_target_schema
# Stream the dump into the container so the file is owned by the Oracle OS user;
# docker cp can preserve host UID/GID and make impdp fail with ORA-27041.
docker exec -u root -i "$DB_CONTAINER" bash -lc "cat > '$DP_PATH/restore_target.dmp' && chown oracle:oinstall '$DP_PATH/restore_target.dmp' && chmod 640 '$DP_PATH/restore_target.dmp'" \
  < "$DMP_FILE" || die "Could not stage backup dump with Oracle-readable permissions."
log "Importing $(basename "$DMP_FILE") into $APP_USER@$PDB..."
RESTORE_LOG="$REPO_ROOT/artifacts/restore-$(date +%Y%m%d_%H%M%S).log"
if ! docker exec -i \
    -e "DB_USER=$APP_USER" -e "DB_PASSWORD=$APP_USER_PWD" -e "DB_TNS=$PDB" \
    "$DB_CONTAINER" bash -lc \
    'impdp "$DB_USER/$DB_PASSWORD@$DB_TNS" schemas="$DB_USER" directory=DATA_PUMP_DIR dumpfile=restore_target.dmp table_exists_action=REPLACE' \
    > "$RESTORE_LOG" 2>&1; then
  err "impdp restore failed. Log: $RESTORE_LOG"
  tail -35 "$RESTORE_LOG" >&2
  exit 1
fi
[ -s "$RESTORE_LOG" ] || die "impdp log is empty; refusing unverified restore."
ok "Data Pump import completed. Log: $RESTORE_LOG"

log "Validating restored schema and traceability data..."
if [ "$EXPECTED_LOT_ROWS" -gt 0 ]; then
  lot_sql="$(mktemp)"
  lot_log="$(mktemp)"
  printf '%s\n' "SELECT COUNT(*) FROM inventory_lot;" "EXIT;" > "$lot_sql"
  run_sql "$lot_sql" "$lot_log" || { err "Could not query restored traceability lots."; exit 1; }
  actual="$(grep -Eo '^[[:space:]]*[0-9]+[[:space:]]*$' "$lot_log" | tail -1 | tr -d ' ' || true)"
  rm -f "$lot_sql" "$lot_log"
  [ -n "$actual" ] || die "Could not count restored traceability lots"
  [ "$actual" -ge 1 ] || die "Restored traceability query returned no lots (expected $EXPECTED_LOT_ROWS)."
  ok "Traceability query returned $actual lot(s); backup metadata expected $EXPECTED_LOT_ROWS."
fi
bash "$REPO_ROOT/scripts/verify-backup.sh" || die "Post-restore verification failed"
ok "Database restore completed and verified successfully."

