# Project Issue Log & Resolution Tracking
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** PM-ISS-006  

---

## 1. Active & Resolved Issue Register

| Issue ID | Date Identified | Issue Description | Severity | Impacted Area | Root Cause | Resolution Action | Resolution Date | Status |
|:---:|:---:|---|:---:|---|---|---|:---:|:---:|
| **ISS-01** | 2026-09-24 | Integration test login failure during local test runs | High | Automated Testing | Database was not re-seeded with latest PBKDF2 hash credentials from `sql/11_rbac_seed.sql`. | Re-ran `scripts/run-sql.sh` to update database users; all 164 tests passed cleanly. | 2026-09-24 | 🟢 Closed |
| **ISS-02** | 2026-09-24 | SonarCloud GitHub Actions workflow contained fallback token | Medium | Security & CI/CD | Fallback string in workflow file posed secret hygiene violation. | Removed hardcoded token fallback; gated execution on `${{ secrets.SONAR_TOKEN != '' }}`. | 2026-09-24 | 🟢 Closed |
| **ISS-03** | 2026-09-24 | DDL lock contention during concurrent test runs and schema drops | High | Environment | Orphaned test background process held open Oracle connection pool. | Terminated stale process; documented session hygiene in testing strategy. | 2026-09-24 | 🟢 Closed |
| **ISS-04** | 2026-09-24 | Floor operators requesting approval threshold enforcement | Medium | Business Controls | Inventory adjustments had no formal monetary or quantity threshold gating. | Formulated Change Request **CR-001** to enforce multi-tier approval logic. | 2026-09-24 | 🟢 Closed (Via CR-001) |
