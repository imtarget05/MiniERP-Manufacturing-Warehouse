# Traceability Matrix — MiniERP Manufacturing Warehouse

`Requirement → Business Rule → Test Case → Automated Test → Execution Evidence`

| Business invariant | Test Case | Automated test (exact node, on disk) | Source file | Evidence |
|---|---|---|---|---|
| FEFO (earliest-expiry first; empty skipped; deterministic tie-break) | ERP-001, ERP-002, ERP-007 | `TraceabilityPropertyTests` (3) + `TraceabilityPhase2Tests` tie-break | `tests/MiniERP.Api.Tests/` | VERIFIED 3/3 2026-09-27 |
| NO_EXPIRED_ISSUE (`EXPIRY < SYSDATE` locked) | ERP-003 | property slice + `ErpContractTests` (9+3) | `tests/MiniERP.Api.Tests/` | VERIFIED slice (in 224-run) |
| NONEGATIVE_STOCK (0/negative rejected) | ERP-005, ERP-006 | `ApiSurfaceContractTests` (6) | `tests/MiniERP.Api.Tests/ApiSurfaceContractTests.cs` | VERIFIED (in 224-run) |
| CONTROLLED_SHORTAGE (`ORA-20007` + rollback) | ERP-004, ERP-009 | `TraceabilityContractTests` (7+4), NEG-003 | `tests/MiniERP.Api.Tests/` | VERIFIED Oracle-free slice; live = CI |
| UNBROKEN_GENEALOGY (FG↔lots↔PO both directions) | ERP-011–013 | `TraceabilityPhase3Tests` (5+2), `TraceabilityPhase4Tests` (7+3) | `tests/MiniERP.Api.Tests/` | VERIFIED Oracle-free slice; live = CI |
| PARAM_BINDING (no injection) | ERP-016 | `SecurityContractTests` (11+4) | `tests/MiniERP.Api.Tests/SecurityContractTests.cs` | VERIFIED (in 224-run) |
| DSN_FAIL_CLOSED (bad DSN → guard, no CI crash) | ERP-010, ERP-017 | `TestOracleDsnGuardTests` (9) | `tests/MiniERP.Api.Tests/TestOracleDsnGuardTests.cs` | VERIFIED 2026-09-27 (DEF-ERP-002 documents the 26 fail-closed) |
| IDEMPOTENT_REPLAY (same key+payload → replay w/o re-deduct) | E2E | NEG-007/NEG-008 (`409` on mismatch) | `test-strategy.md` §NEG | UNVERIFIED here (live Oracle) |
| RESTORE_REAL (counts/checksum match; corrupt fails clean) | ERP-020, ERP-021 | `verify-backup.sh` Data Pump drill | `scripts/` | UNVERIFIED here |
