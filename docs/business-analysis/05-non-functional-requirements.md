# Non-Functional Requirements Specification (NFRS)
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** BA-NFR-005  
**Target Enterprise:** Tân Vĩnh Footwear & Apparel Manufacturing Co., Ltd.  

---

## 1. Security (NFR-SEC)
- **NFR-SEC-01 (Authentication Standards):** All user passwords must be hashed using RFC 2898 PBKDF2 with HMAC-SHA256, 16-byte cryptographically random salt, and a minimum of 210,000 iterations. Plaintext passwords must never be stored, logged, or serialized.
- **NFR-SEC-02 (Session & Token Expiration):** Bearer tokens must expire within 60 minutes. Stateful refresh tokens must be invalidated upon explicit logout or security revocation.
- **NFR-SEC-03 (Role-Based Authorization):** Every mutating API endpoint (`POST`, `PUT`, `DELETE`) must enforce strict RBAC at the server level via ASP.NET Core authorization policies (`AuthPolicies`). Unauthenticated requests must return `401 Unauthorized`; unauthorized roles must return `403 Forbidden`.
- **NFR-SEC-04 (Secret Isolation):** Secrets (database passwords, JWT secret keys, third-party tokens) must be injected via OS environment variables. Zero credentials may be hardcoded or checked into Git.

---

## 2. Availability & Reliability (NFR-AVL)
- **NFR-AVL-01 (Uptime Target):** The core Web API and Oracle Database service must target 99.5% operational availability during factory operating shifts (06:00 to 22:00, Monday through Saturday).
- **NFR-AVL-02 (Fail-Soft External Integration):** External integrations (such as the IT Helpdesk incident dispatch) must operate asynchronously via an outbox pattern (`HELPDESK_DELIVERY`). External downtime or HTTP timeouts must never block or roll back core ERP warehouse/manufacturing transactions.
- **NFR-AVL-03 (Idempotent Scan Operations):** Scanner and barcode integration endpoints must support idempotency keys to tolerate intermittent Wi-Fi disconnection on rugged warehouse handheld terminals without duplicate stock deductions.

---

## 3. Performance & Throughput (NFR-PERF)
- **NFR-PERF-01 (API Response Times):**
  - Read-only queries (Stock on hand, PO status, location lookup): 95th percentile latency $\le 200\text{ ms}$.
  - State-changing mutations (Stock-in, stock-out, lot receiving): 95th percentile latency $\le 500\text{ ms}$.
  - Complex batch operations (Full order completion with 10-line BOM explosion): 95th percentile latency $\le 1,200\text{ ms}$.
- **NFR-PERF-02 (Database Concurrency):** Inventory row updates must employ pessimistic locking (`SELECT ... FOR UPDATE`) within PL/SQL stored procedures to guarantee consistency during concurrent assembly line issues.

---

## 4. Maintainability & Code Quality (NFR-MAINT)
- **NFR-MAINT-01 (Modular Architecture):** Minimal API endpoints must delegate business logic to clean service abstractions (`ErpDbService`, `TokenService`, `LabelRenderService`) and compiled database packages.
- **NFR-MAINT-02 (Database Versioning):** All schema alterations must be scripted in numbered, rerunnable SQL migration files (`sql/01_schema.sql` through `sql/11_rbac_seed.sql`).
- **NFR-MAINT-03 (Automated Test Coverage):** Core business logic, BOM calculations, and negative scenarios must maintain $\ge 80\%$ automated test coverage across xUnit suites.

---

## 5. Backup & Disaster Recovery (NFR-BCDR)
- **NFR-BCDR-01 (Recovery Point Objective - RPO):** Target RPO $\le 24\text{ hours}$ (simulated production baseline $\le 15\text{ minutes}$ with archive redo logging).
- **NFR-BCDR-02 (Recovery Time Objective - RTO):** Target RTO $\le 2\text{ hours}$ from disaster declaration to full application health verification (`/api/health`).
- **NFR-BCDR-03 (Automated Tooling):** Backups must be executable on demand via `scripts/backup-db.sh` using Oracle Data Pump (`expdp`). Restoration must be verifiable using `scripts/verify-backup.sh`.

---

## 6. Logging & Observability (NFR-LOG)
- **NFR-LOG-01 (Correlation & Tracing):** Every inbound HTTP request must be assigned an `X-Correlation-Id` passed through all application layers and persisted in database logs.
- **NFR-LOG-02 (Autonomous Failure Logging):** Business exceptions (`ORA-20001` through `ORA-20025`) must be recorded in `ERROR_LOG` via `PRAGMA AUTONOMOUS_TRANSACTION`, ensuring error records survive outer transaction rollbacks.
- **NFR-LOG-03 (Health Probes):** The system must expose `/api/health`, `/api/health/ready`, and `/api/health/details` reporting Oracle connectivity, table counts, and package compilation status without leaking credentials.

---

## 7. Auditability & Compliance (NFR-AUD)
- **NFR-AUD-01 (Immutable Audit Events):** All security and inventory state mutations must generate records in `APP_AUDIT_EVENT` containing actor identity, event type, target entity key, old/new payload JSON, and timestamp.
- **NFR-AUD-02 (Non-Repudiation):** Audit tables must be append-only. No application role (including warehouse supervisor) may update or delete audit event rows.
