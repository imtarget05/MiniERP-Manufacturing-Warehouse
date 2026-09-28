# Test Plan — MiniERP Manufacturing Warehouse

**Contract:** [`docs/qa/QA_ACCEPTANCE.md`](../../docs/qa/QA_ACCEPTANCE.md).
**Detailed strategy:** [`test-strategy.md`](test-strategy.md) (pyramid, NEG matrix,
golden path, CI gates) — this plan adds the acceptance layer on top of it.
**Case IDs:** `ERP-001` → `ERP-022` in `TEST_CASES.md`.
**Runner:** `dotnet test tests/MiniERP.Api.Tests` + `scripts/run-all-tests.sh`
(Oracle via `MINIERP_QA_INSTANCE=minierp-qa` throwaway — see `test-strategy.md` §5).
On disk: **207 `[Fact]`+`[Theory]` methods** (enumerated 2026-09-27).

## Scope

Oracle PL/SQL FEFO allocation, expiry rules, lot genealogy (forward/backward),
concurrent allocation, transaction atomicity, connection-loss safety, API contracts
(400/401/403/404/409), `make demo` idempotency, backup→restore DR drill, ERD parity.

## Levels

| Level | What | Where |
|---|---|---|
| Unit | FEFO sort, tie-break determinism, input validation (xUnit, no DB) | `TraceabilityPropertyTests` (3), `ApiSurfaceContractTests` (6) |
| Contract | API surface, dashboard assets, docs traceability, RBAC, security | `ApiSurfaceContractTests`, `DashboardAssetContractTests` (40+4), `Phase2RbacTests` (12), `SecurityContractTests` (11+4) |
| Property | FEFO + genealogy over seeded lots | `TraceabilityPropertyTests` (3) |
| Integration | allocate/issue/complete + rollback on halfway failure (Oracle throwaway) | `TraceabilityPhase2/3/4Tests`, `AutomationIntegrationTests` (30+1) |
| Concurrency | concurrent FEFO on same stock → no double-allocate (ERP-008) | PL/SQL `FOR UPDATE` + `ApiIntegrationTests` (11) |
| Adversarial | expired lot, negative/zero qty, malformed API payloads, invalid PL/SQL input binding | `ErpContractTests` (9+3), `ApiSurfaceContractTests` |
| DR | Data Pump backup → drop → restore → counts/checksum match; corrupt backup fails clean | `verify-backup.sh` drill (manual here) |
| E2E | `make demo` on fresh machine; golden path Receive→Allocate→Issue→Complete→Trace | manual here |

## Environments

| Env | Command | Scope |
|---|---|---|
| Offline | `dotnet test --filter "FullyQualifiedName~TraceabilityProperty"` | **3/3 passed** 2026-09-27 |
| Offline (Oracle-free) | `dotnet test --filter "Category!=Integration"` (DSN→closed port) | **224 pass / 26 fail** 2026-09-27 — all 26 pre-existing Oracle-guard failures (DEF-ERP-002) |
| CI (Docker) | Oracle 23c Free throwaway → full `dotnet test` | last-known 302/302 green (UNVERIFIED here); 55 Integration need live Oracle |

## Entry / exit criteria

- Entry: throwaway Oracle seeded via gated `run-sql.sh` (the QA gate refuses
  production names — exit 78).
- Exit: P0 100% PASS; FEFO + no-expired + no-negative + atomic + genealogy proven
  at unit + integration.

## Invariants under test

```text
FEFO correct + no expired issue + no negative stock + atomic transaction + unbroken genealogy
```
