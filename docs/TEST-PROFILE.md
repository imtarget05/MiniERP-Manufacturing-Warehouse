# MiniERP test profile — classification of the 81 unrun tests

Generated from a measured `dotnet test` run at commit `4107e51`. Not a
placeholder: every count below is the actual number of failing test methods,
extracted from the runner output, not an estimate.

## The headline

```
No DSN granted:   224 passed,  81 failed   (305 total)
DSN granted to a
deliberately dead
listener:         224 passed,   0 failed   (224 total)
```

The 81 tests are **not broken and not skipped by a soft guard**. They are
blocked by a deliberate fail-closed guard that refuses to start a test host
without an explicitly authorised data source.

## Canonical report wording

> **224 PASS** (unit + logic, hermetic)
> **21/21 PASS** (DB-free logic audit, FEFO allocation)
> **81 DB-integration NOT_RUN** due to the environment authorisation guard
> — *not failures*

This is the correct characterisation. The 81 have never been *asserted false*;
they have never run.

## Why the guard refuses, and why that is correct

From `tests/MiniERP.Api.Tests/TestOracleDsnGuard.cs`:

- Every test host in the assembly goes through `TestOracleDsn`.
- Without an explicit grant, the host would fall back to
  `src/appsettings.json` → `Data Source=localhost:1521/FREEPDB1`, which is the
  developer's own Oracle listener and, in a deployed context, plausibly
  production.
- `AuditMiddleware` appends an `APP_AUDIT_EVENT` row for every
  POST/PUT/PATCH/DELETE — **including 401 and 403** — and swallows sink
  failures.
- `TestStockFixture` issues `STOCK_IN` / `STOCK_OUT`.

So a "no-DB" suite that silently inherits a DSN is a **writer**, and its
verdict looks identical whether the DSN points at production, at nothing, or at
`127.0.0.1:1`. An absent DSN must be a visible error, not a silent no-op.

This guard is a security control. It is correctly implemented and must not be
weakened to make a dashboard green.

## Classification of the 81

Every one of the 81 was confirmed to fail with the guard's own message
("Test data source refused for ..."), not with an assertion failure. Breakdown
by test class:

| Test class | Blocked | Nature |
|---|---|---|
| `AutomationIntegrationTests` | 35 | requires real Oracle — exercises approval gate, material check, stock adjust |
| `SecurityContractTests` | 15 | requires real Oracle — audit/event writes, RBAC over persisted state |
| `Phase2RbacTests` | 11 | requires real Oracle — token expiry, role transitions |
| `ApiIntegrationTests` | 11 | requires real Oracle — end-to-end HTTP + DB |
| `ApiSurfaceContractTests` | 6 | requires real Oracle — OpenAPI document served by the live pipeline |
| `DocumentationTraceabilityTests` | 2 | requires real Oracle — RTM endpoint paths validated against the served document |
| `TestOracleDsnGuardTests` | 1 | requires real Oracle — asserts a *granted* DSN actually starts and serves the real pipeline |
| **Total** | **81** | |

### Against the requested categories

| Requested category | Count | Note |
|---|---|---|
| requires real Oracle | **81** | all of them |
| requires external DSN | *(same 81)* | not a separate class — the DSN **is** the Oracle requirement |
| requires environment secret | 0 | no test failed on a missing secret |
| truly integration-only | **81** | every one writes or reads through the EF/Oracle path |

**A second finding:** only **30** test methods carry
`Trait("Category", "Integration")`, but **80** of the 81 blocked tests are
integration tests in substance. The `Category` taxonomy is therefore
**incomplete** — it under-reports integration coverage by roughly 50 tests.
This matters because `--filter 'Category!=Integration'` is the documented way to
run the hermetic subset, and it happens to work only because the guard refuses
the rest anyway, not because the taxonomy is right.

**Do not fix this by adding 50 `Trait` attributes and re-running.** The honest
correction is to record the discrepancy and re-triage deliberately.

## How to run each profile

```bash
# PROFILE 1 — UNIT / LOGIC (hermetic, no database, no network)
# This is the profile that gates a normal checkout. 224 pass.
dotnet test tests/MiniERP.Api.Tests/MiniERP.Api.Tests.csproj \
  --filter 'Category!=Integration'

# The same result via the guard's own documented escape hatch: a DSN that is
# deliberately unreachable, so the host starts but can never connect to a real
# database. 224 pass, 0 fail.
ConnectionStrings__OracleDb='User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;' \
  dotnet test tests/MiniERP.Api.Tests/MiniERP.Api.Tests.csproj

# PROFILE 2 — LOGIC AUDIT (DB-free, pure allocation rules)
dotnet test tests/MiniErp.LogicAudit/MiniErp.LogicAudit.csproj
# 21/21 pass, covers FEFO allocation with deterministic tie-breaking.

# PROFILE 3 — INTEGRATION_DB (requires a disposable Oracle)
# NOT_RUN in this environment. See "How to close this" below.
ConnectionStrings__OracleDb='User Id=...;Password=...;Data Source=<disposable-host>:1521/FREEPDB1;...' \
  dotnet test tests/MiniERP.Api.Tests/MiniERP.Api.Tests.csproj \
  --filter 'Category=Integration'

# PROFILE 4 — E2E (requires a running API + loaded schema)
# See scripts/run-all-tests.sh; needs Oracle plus a SQL load.
```

## Critical safety note

**Never point Profile 3 or 4 at a database you care about.** The suite writes
audit rows and stock movements. A disposable container only. The guard exists
precisely because this is easy to do by accident.

## How to close this gap

Profile 3 is unblocked by a **disposable Oracle** — a container on a throwaway
port, seeded from `scripts/run-sql.sh`, torn down after. No committed fixture
needed; the existing scripts already cover load and teardown. That is the
recommended next step and is cheap.

Until then the correct report line is the three-part wording above. It must not
be compressed to "305 tests" or "224/305", both of which imply the 81 are
known-good or known-bad respectively. They are **unknown**, and saying so is
the accurate claim.
