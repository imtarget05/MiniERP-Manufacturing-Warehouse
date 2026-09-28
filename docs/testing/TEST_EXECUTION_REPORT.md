# Test Execution Report — MiniERP Manufacturing Warehouse

Cases: [`TEST_CASES.md`](TEST_CASES.md) (`ERP-001`→`ERP-022`).
Strategy: [`test-strategy.md`](test-strategy.md).
Evidence dir: [`evidence/`](evidence/). Session date: 2026-09-27 UTC.

| Date (UTC) | Command | Scope | Result | Verdict | Evidence |
|---|---|---|---|---|---|
| 2026-09-27 | `curl /api/health` + Prometheus `/api/v1/targets` (live stack) | Observability smoke (supports ERP monitoring) | — | PASS (after mitigation, see DEF-ERP-001) | shell transcript 2026-09-27 (contract §7) |
| 2026-09-27 | `dotnet test --filter "FullyQualifiedName~TraceabilityProperty"` (net8.0) | ERP-001–003 property slice | **3/3 passed** (534 ms) | VERIFIED ✅ | `evidence/2026-09-27-property.log` |
| 2026-09-27 | `dotnet test --filter "Category!=Integration"` (net8.0, 40 s) | Oracle-free suite (250 executed) | **224 pass / 26 fail** — every failure throws in `TestOracleDsn.Ensure` (Oracle-guard fail-closed, pre-existing, unrelated to app logic) | CLOSED (DEF-ERP-002) by guarded rows below | `evidence/2026-09-27-oracle-free.log` |
| 2026-09-27 | `ConnectionStrings__OracleDb='User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;' dotnet test --filter "Category!=Integration"` (net8.0, SDK 8.0.425) | hermetic unit/contract slice (250) | **249 pass / 1 fail** (`SwaggerOperations_MatchTheEndpointsTheHostMaps`: swagger advertises `GET /metrics`, host scan is `/api/`-scoped) | VERIFIED ✅ with 1 scopedrift (DEF-ERP-003) | `evidence/2026-09-27-unit-contract.log` (unguarded 25-refusal run) + `evidence/2026-09-27-unit-contract-guarded.log` |
| 2026-09-27 | same guarded command after one-line test-scope fix | same slice | **250 pass / 0 fail** (18 s) | VERIFIED ✅ | `evidence/2026-09-27-unit-contract-green.log` |
| — | full `dotnet test` on Oracle 23c throwaway | all 22 cases incl. 55 Integration | BLOCKED — QA oracle (`minierp-qa-oracle`, port 1522 via local-only `/tmp/minierp-qa-ports.yml`) not bootstrapping after 20+ min (DEF-ERP-004); last-known 302/302 green (HARD_TEST_REPORT.md §III) | BLOCKED | shell transcript 2026-09-27 |
| — | full `dotnet test` on Oracle 23c throwaway | all 22 cases incl. 55 Integration | UNVERIFIED — last-known 302/302 green (HARD_TEST_REPORT.md §III) | UNVERIFIED | `scripts/run-all-tests.sh` in CI |
| — | `make demo`, backup→restore drill, ERD parity | ERP-018–022 | UNVERIFIED here (need Docker/Oracle) | UNVERIFIED | runbook logs when executed |

## How to record a run

1. Unit/contract: `dotnet test tests/MiniERP.Api.Tests --filter "Category!=Integration"`.
2. Full: throwaway Oracle via `MINIERP_QA_INSTANCE=minierp-qa` (see `test-strategy.md` §5),
   then `dotnet test tests/MiniERP.Api.Tests` or `scripts/run-all-tests.sh`.
3. Save `.trx`/logs under `evidence/` with date prefix.
4. Fill one row above; update `Status` in `TEST_CASES.md`.
5. Any FAIL/FLAKY gets an entry in `DEFECT_REPORT.md` before the run counts as reviewed.
