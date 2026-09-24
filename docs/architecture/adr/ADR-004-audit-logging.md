# ADR-004: Enterprise Audit Logging Architecture

## Status
**Accepted** (2026-09-24)

## Context
Compliance audits from international sportswear brands (Nike, Adidas) require complete traceability of which user authorized or executed changes to production schedules, inventory adjustments, and security privileges.

## Decision
We implemented a two-tier audit logging architecture:
1. **Application-Level User Audit Trail (`APP_AUDIT_EVENT`):** Captured by `AuditMiddleware` and critical authentication endpoints. Records actor, event type, entity name, primary key, JSON payload diff, correlation ID, and timestamp.
2. **Autonomous Error Incident Log (`ERROR_LOG`):** Captured by database procedures using `PRAGMA AUTONOMOUS_TRANSACTION`, ensuring failure details survive transaction rollbacks.

## Rationale
- **Separation of Concerns:** Functional errors belong in `ERROR_LOG`; user-driven state changes and security decisions belong in `APP_AUDIT_EVENT`.
- **Tamper Evidence:** Audit tables have no application-level `DELETE` or `UPDATE` endpoints, guaranteeing append-only non-repudiation.

## Consequences
- **Positive:** Meets enterprise compliance standards; speeds up root cause analysis during incidents.
- **Negative:** Additional insert I/O overhead on state-changing API calls (~2-5ms).
