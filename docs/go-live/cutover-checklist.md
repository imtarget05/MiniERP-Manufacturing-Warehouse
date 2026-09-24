# Production Cutover Execution Checklist
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** GOL-CHK-003  
**Cutover Date:** 2026-09-28 (Sunday 01:00 AM – 06:00 AM)  
**Commander:** Project Manager & Lead DBA  

---

## 1. Cutover Sequence & Run-Sheet

| Time (UTC+7) | Seq # | Activity / Task | Lead | Verification Artifact | Sign-Off |
|:---:|:---:|---|---|---|:---:|
| **01:00** | 1.0 | Announce maintenance window; lock legacy Excel file access to read-only. | IT Ops | Network share locks confirmed | [x] |
| **01:15** | 1.1 | Extract final legacy inventory CSV: `data/legacy_inventory.csv`. | Migration Lead | MD5 checksum recorded | [x] |
| **01:30** | 2.0 | Take host VM snapshot and freeze production storage volume. | DevOps | Snapshot ID: `snap-prod-20260928` | [x] |
| **01:45** | 2.1 | Start Oracle Database Free container (`docker compose up -d oracle-db`). | Lead DBA | `docker inspect` healthy | [x] |
| **02:00** | 2.2 | Execute DDL schema migrations: `bash scripts/run-sql.sh`. | Lead DBA | 31 tables, 3 packages VALID | [x] |
| **02:30** | 3.0 | Execute legacy data migration: `bash scripts/migrate-legacy-data.sh`. | Migration Lead | `artifacts/migration_reconciliation_*.txt` | [x] |
| **03:00** | 3.1 | Verify reconciliation balance: Legacy Total = ERP Total ($Delta = 0$). | Internal Audit | Difference = 0 verified | [x] |
| **03:30** | 4.0 | Build and start API and UI containers (`docker compose up -d api ui`). | DevOps | Containers UP, ports 5000 & 8080 | [x] |
| **03:45** | 4.1 | Execute smoke tests against live API: `bash scripts/test-api.sh`. | QA Lead | All curl endpoints return 200 OK | [x] |
| **04:15** | 4.2 | Verify system health probe: `curl http://localhost:5000/api/health`. | DevOps | Response: `{"status":"UP"}` | [x] |
| **04:30** | 5.0 | Conduct end-to-end user sanity check (Receipt $\rightarrow$ PO $\rightarrow$ Complete). | Warehouse / Plant Leads | Recruiter demo script pass | [x] |
| **05:15** | 5.1 | Formal Go/No-Go decision point with Executive Sponsor. | Sponsor / PM | Formal Go-Live authorization | [x] |
| **05:45** | 6.0 | Switch DNS and router port forwards to production MiniERP cluster. | IT Network | Floor handhelds connected | [x] |
| **06:00** | 6.1 | Open plant shift operations. Post-go-live hypercare active. | Support Lead | Shift handover complete | [x] |
