# Incident Runbook: INC-005 — Database Unavailable
**Project:** MiniERP Manufacturing & Warehouse  
**Incident Code:** INC-005  
**Severity:** P0 (Disaster / Plant-Wide Outage)  
**Target Component:** Oracle Database Container / `FREEPDB1` Listener  

---

## 1. Symptoms & Incident Report
- **Reported By:** Automated monitoring alerts & floor operators.
- **Symptom:** API calls return `503 Service Unavailable`. Health probe `/api/health` reports:
  `{"status":"DOWN","error":"Oracle Database connection timeout / unreachable"}`.
- **Impact:** Entire ERP system unable to process warehouse receipts, order dispatches, or scans.

---

## 2. Investigation Protocol & Diagnostic Steps

1. **Check Container Status via Docker:**
   ```bash
   docker ps -a --filter "name=minierp-oracle"
   # Observe container state: Exited (137) (Out-of-memory killed) or restarting
   ```
2. **Inspect Docker Container Logs:**
   ```bash
   docker logs minierp-oracle --tail 100
   ```
3. **Verify Host Memory & Disk Space:**
   ```bash
   df -h /opt/oracle/oradata
   vm_stat  # or free -m
   ```

---

## 3. Root Cause Analysis (RCA)
1. **Scenario A (OOM Kill):** Host machine ran out of physical memory; Linux OOM killer terminated Oracle background processes.
2. **Scenario B (Storage Full):** Archived redo logs or Data Pump dump files filled the mount volume `/opt/oracle/oradata`, preventing Oracle from checkpointing.

---

## 4. Resolution Procedure

### Path A: Clean Restart (If container crashed without data loss)
```bash
# 1. Restart database container:
docker compose up -d oracle-db

# 2. Wait for healthy status:
bash scripts/start-db.sh

# 3. Verify health probe:
curl http://localhost:5000/api/health
```

### Path B: Disaster Recovery Restoration (If database files are corrupt)
If Oracle logs indicate unrecoverable corruption:
```bash
# 1. Execute proven restore procedure from last valid backup:
ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes \
  bash scripts/restore-db.sh backups/backup_latest

# 2. Run verification script:
bash scripts/verify-backup.sh
# Expected output: 31 tables exist, 3 packages VALID.

# 3. Restart API service:
docker compose restart api
```

---

## 5. Prevention & System Hardening
- Allocate minimum 4 GB dedicated RAM limit to the Oracle container in `docker-compose.yml`.
- Configure automated purge cron for old Data Pump dump files and archive redo logs (`ARTIFACT_KEEP=20`).
- Deploy external uptime ping monitoring on `/api/health` with automated PagerDuty/SMS alerting.
