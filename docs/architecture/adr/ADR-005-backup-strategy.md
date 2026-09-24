# ADR-005: Backup & Disaster Recovery Tooling Strategy

## Status
**Accepted** (2026-09-24)

## Context
A mission-critical ERP system must survive hardware destruction, database corruption, or operator error with verifiable recovery targets (RPO $\le 24\text{ hours}$, RTO $\le 2\text{ hours}$). We evaluated:
1. Physical Oracle RMAN backups.
2. File-system volume snapshots (Docker volume `oracle_data` copy).
3. Logical Oracle Data Pump (`expdp` / `impdp`) schema-level exports with automated verification scripts.

## Decision
We adopted **Logical Oracle Data Pump (`expdp`/`impdp`) packaged in automated Bash scripts (`backup-db.sh`, `restore-db.sh`, `verify-backup.sh`) accompanied by metadata snapshot manifests**.

## Rationale
- **Portability:** Data Pump exports (`.dmp`) are completely portable across Docker containers, development laptops, staging VMs, and cloud Oracle Autonomous Database instances.
- **Referential Integrity:** Data Pump exports metadata, tables, constraints, sequences, triggers, and PL/SQL packages in consistent transactional state.
- **Automated Verification:** The accompanying `verify-backup.sh` programmatically queries table counts, package status (`VALID`), and master records, proving the backup is sound before disaster strikes.

## Consequences
- **Positive:** Simple, scriptable, human-verifiable, container-friendly backup/restore workflow.
- **Negative:** Point-in-time recovery granularity is daily without continuous Oracle Archive Redo log shipping.
