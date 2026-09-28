# Test Cases — MiniERP Manufacturing Warehouse

**Plan:** [`TEST_PLAN.md`](TEST_PLAN.md) (+ [`test-strategy.md`](test-strategy.md)).
**Contract:** `docs/qa/QA_ACCEPTANCE.md`. On disk: 207 `[Fact]`+`[Theory]`.
✅ = ran green 2026-09-27.

| ID | file::case (on disk) | Invariant asserted | Runnable offline? |
|---|---|---|---|
| ERP-001 | `TraceabilityPropertyTests.cs` (3) ✅ FEFO path | earliest-expiry picked | ✅ 3/3 |
| ERP-002 | `TraceabilityPropertyTests.cs` ✅ fall-through path | next lot picked | ✅ |
| ERP-003 | `TraceabilityPropertyTests.cs` ✅ + `ErpContractTests.cs` (9+3) | expired never allocated | ✅ property slice |
| ERP-004 | `TraceabilityContractTests.cs` (7+4) + NEG-003 `ORA-20007` | controlled reject + rollback | Oracle-free slice ✅; live Oracle = CI |
| ERP-005 | `ApiSurfaceContractTests.cs` (6) ✅ zero-qty path | qty 0 rejected | ✅ (224-pass run) |
| ERP-006 | `ApiSurfaceContractTests.cs` ✅ negative path | never negative stock | ✅ |
| ERP-007 | `TraceabilityPhase2Tests.cs` (5+1) tie-break path | deterministic tie-break | Oracle-free slice ✅ |
| ERP-008 | PL/SQL `FOR UPDATE` + `ApiIntegrationTests.cs` (11) | no double-allocate | Oracle-free slice ✅ |
| ERP-009 | `TraceabilityContractTests.cs` rollback path | full rollback halfway-fail | Oracle-free slice ✅ |
| ERP-010 | `TestOracleDsnGuardTests.cs` (9) | no half-written state | ✅ (guard itself; live-loss = CI) |
| ERP-011 | `TraceabilityPhase3Tests.cs` (5+2) + golden-path backward trace | FG → source lots | Oracle-free slice ✅ |
| ERP-012 | `TraceabilityPhase4Tests.cs` (7+3) forward trace | lot → products | Oracle-free slice ✅ |
| ERP-013 | `TraceabilityContractTests.cs` unknown-lot path | no fake genealogy | Oracle-free slice ✅ |
| ERP-014 | `TraceabilityPhase4Tests.cs` cycle path (candidate) | cycle detected | UNVERIFIED exact node |
| ERP-015 | `ApiSurfaceContractTests.cs` ✅ | `400`, never `500` | ✅ |
| ERP-016 | `SecurityContractTests.cs` (11+4) ✅ | parameterized binding | ✅ |
| ERP-017 | `TestOracleDsnGuardTests.cs` ✅ (guard passes; 26 guarded integration tests fail-closed as designed) | bad DSN detected | ✅ |
| ERP-018 | `Makefile` demo (manual) | fresh-machine boot | UNVERIFIED here |
| ERP-019 | `Makefile` + `qa-gate.sh` (manual) | second run non-destructive | UNVERIFIED here |
| ERP-020 | `verify-backup.sh` Data Pump drill (manual) | counts/checksum match | UNVERIFIED here |
| ERP-021 | corrupt-backup path (manual) | clean failure, DB untouched | UNVERIFIED here |
| ERP-022 | `docs/ERD.md` vs `sql/02_schema.sql` (doc parity) | ERD == DDL | UNVERIFIED here |

## Per-file inventory (`tests/MiniERP.Api.Tests`)

| File | Fact/Theory | Notes |
|---|---|---|
| `TraceabilityPropertyTests` 3+0 | 3 | FEFO+genealogy properties ✅ |
| `TraceabilityContractTests` 7+4, `TraceabilityPhase2Tests` 5+1, `Phase3` 5+2, `Phase4` 7+3 | 34 | traceability phases |
| `ApiSurfaceContractTests` 6, `ApiIntegrationTests` 11, `AutomationIntegrationTests` 30+1, `ErpContractTests` 9+3 | 60 | API/automation/contracts |
| `DashboardAssetContractTests` 40+4, `DocumentationTraceabilityTests` 15+2, `Phase2RbacTests` 12, `SecurityContractTests` 11+4, `HelpdeskIntegrationTests` 12+1 | 101 | assets/docs/RBAC/security |
| `TestOracleDsnGuardTests` 9 | 9 | DSN guard ✅ (26 guarded tests fail-closed offline by design) |

**QA essence:** FEFO correct + no expired issue + no negative stock + atomic transaction + unbroken traceability.

## Full-run verdicts 2026-09-27 (SDK 8.0.425; repo targets net8.0)

- Hermetic unit/contract slice (`Category!=Integration` + guard DSN
  `...Data Source=127.0.0.1:1/FREEPDB1`): first 249/250, then **250/250** after a
  one-line test-scope fix (see DEF-ERP-003). Logs:
  `evidence/2026-09-27-unit-contract.log` (25 guard-refusals without DSN),
  `evidence/2026-09-27-unit-contract-guarded.log` (249/250),
  `evidence/2026-09-27-unit-contract-green.log` (250/250).
  Without the guard DSN the 25 RBAC/Swagger tests fail-closed inside
  `TestOracleDsn.Ensure` (by design — never touches prod Oracle).
- "Oracle-free slice ✅" rows above are now backed by the 250-green guarded run.
- Live-Oracle integration (`Category=Integration`, ERP-001/002/004/008/009/011/012
  live legs + automation suite): **BLOCKED** — fresh QA oracle
  (`minierp-qa-oracle`, `-p minierp-qa`, host port shifted 1521→1522 via local-only
  `/tmp/minierp-qa-ports.yml` with `!reset` because prod holds 1521) is not
  progressing: volume still 4.0K after 20+ min, repeating ORA-00205, listener
  reports no services (DEF-ERP-004). Prod stack untouched and healthy.
- ERP-014/018/019/020/021/022: still UNVERIFIED (manual/CI procedures).

**Gate verdict: NOT QA READY** — contract slice fully green incl. RBAC/security;
live integration BLOCKED on QA-oracle bootstrap (DEF-ERP-004); DR/manual rows open.
