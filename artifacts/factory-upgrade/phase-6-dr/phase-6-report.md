# Phase 6 — Monitoring & Disaster Recovery (report)

Design reference: `04-MiniERP-UPGRADE-DESIGN.md` §Phase 6 — Monitoring + backup/restore: readiness/details, structured correlation logs, backup/restore/verification scripts, recovery documentation.
Status: PASS (readiness, structured correlation, fresh backup, clean-target Data Pump restore and verification proven).

## 1. Operational Health & Diagnostics (src/Program.cs, src/Services/ErpDbService.cs)

| Endpoint | Method | Security Policy | Behavior |
|---|---|---|---|
| `/api/health` | GET | Anonymous | Liveness probe returning `UP` / `DOWN` with Oracle connection probe. |
| `/api/health/ready` | GET | Anonymous | Kubernetes / Docker readiness probe checking: Oracle connectivity, table count >= 31, and all 3 packages (`ERP_OPERATIONS`, `ERP_AUTOMATION`, `ERP_TRACEABILITY`) compiled `VALID`. Returns `503 Service Unavailable` with `DB_SCHEMA_NOT_READY` if incomplete. |
| `/api/health/details` | GET | `SupportDetails` (`ERP_ADMIN`, `ERP_SUPPORT`) | Deep diagnostics showing Oracle banner, table counts, individual package compile statuses, and `ERP_TRACEABILITY.package_version`. |

## 2. Structured Correlation Logs (src/Services/CorrelationIdMiddleware.cs)

- Inspects incoming `X-Correlation-ID` header; validates and normalizes (length cap 64, safe character set `[a-zA-Z0-9_-]`).
- Generates 32-character GUID `N` when missing or malformed.
- Emits `X-Correlation-ID` in HTTP response headers.
- Stores correlation ID in `HttpContext.Items[CorrelationIdMiddleware.ItemName]` for consumption by `AuditMiddleware`, logger scopes, and outgoing Helpdesk events.

## 3. Backup & Disaster Recovery Tooling (scripts/)

| Script | Purpose | Safeguards |
|---|---|---|
| `scripts/backup-db.sh` | Creates timestamped archive under `backups/backup_YYYYMMDD_HHMMSS` with Oracle Data Pump export (`expdp`), SQL schema snapshot, and JSON metadata (`metadata.json`). | Zero credentials exposed in console/process logs; captures git commit, table count, and package health. |
| `scripts/restore-db.sh` | Restores a real Oracle Data Pump dump into a clean target schema. | **Two-layer safety lock**: requires both `ALLOW_DESTRUCTIVE_RESTORE=true` and `CONFIRM_RESTORE=yes`/`--confirm`; creates a clean schema and runs post-restore verification. |
| `scripts/verify-backup.sh` | Validates the active/restored schema. | Checks 31 tables, 3 VALID package bodies, core master data, `INVENTORY_LOT` queryability and `HELPDESK_DELIVERY`. |

## 4. Disaster Recovery Runbook (docs/07-backup-recovery-dr.md)

- Defines RPO (<= 15 min production, <= 24h demo) and RTO (<= 30 min production, <= 10 min demo) baselines.
- Documented command-line instructions for manual and automated DR drills.

## 5. Verification Evidence

- `bash -n scripts/backup-db.sh` → OK (exit code 0)
- `bash -n scripts/restore-db.sh` → OK (exit code 0)
- Real DR drill: `backup-db.sh` produced a non-empty Oracle Data Pump dump; `restore-db.sh` recreated a clean schema, completed `impdp`, restored 9 lots matching metadata, and `verify-backup.sh` passed.
- Safety lock check: restore without `ALLOW_DESTRUCTIVE_RESTORE=true` was rejected with exit code 1.
- Evidence: `artifacts/factory-upgrade/final/evidence/dr/verification-summary.txt` and the archived `impdp` log.
