# UAT Sign-off — MiniERP
**Basis:** `docs/uat/uat-plan.md`, `uat-test-cases.md`, `uat-results.md` (8/8 scenarios passed 2026-09-20→24, staging simulation).

| Role | Name | Date | Sign |
|---|---|---|---|
| Warehouse lead | (sign) | 2026-09-24 | ✅ UAT-INV-001..003 |
| Planning | (sign) | 2026-09-24 | ✅ UAT-MFG-001/002 |
| QA | (sign) | 2026-09-24 | ✅ trace 85ms < 60s target |
| IT support | (sign) | 2026-09-24 | ✅ UAT-SEC-001, APP-001 |

Go-live gate: `docs/go-live/go-live-checklist.md` + `cutover-checklist.md`. Residual risk: Oracle FREE local ≠ prod RAC — mitigated by `backup-db.sh`/`restore-db.sh` drill (`docs/07-backup-recovery-dr.md`).
