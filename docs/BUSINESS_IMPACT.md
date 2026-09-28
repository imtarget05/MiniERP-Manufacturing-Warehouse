# Business Impact — MiniERP (Manufacturing Execution & Warehouse)

Domain facts: Oracle PL/SQL (31 tables 3NF; packages `ERP_OPERATIONS`,
`ERP_AUTOMATION`, `ERP_TRACEABILITY`), ASP.NET Core 8 API (62 ops), FEFO +
two-way genealogy, PBKDF2/JWT/RBAC/dual-approval, 302 tests + 9-stage
acceptance. See `README.md`, `docs/`.

## Problem (cost of status quo)

Factories without ERP suffer: untraceable lots (recall/claim exposure),
inventory drift (duplicate scans, offline replays), production stops (BOM issued
without availability check), uncontrolled adjustments (no separation of duties).

## Solution (what the system does)

Atomic PL/SQL transactions for stock/BOM/production; FEFO allocation by
earliest expiry; idempotent receipts; dual-approval adjustments; backward/
forward genealogy trees + labels; Helpdesk outbox (fail-soft).

## Impact

| Metric | Before | After | How measured |
|---|---|---|---|
| Expired-stock waste (FEFO) | baseline | −18% | ESTIMATE — plan target; pending warehouse issue-log comparison. Not measured here. |
| Lot traceability query | hours/manual | <2 s | ESTIMATE — pending timed genealogy queries at scale; existing evidence is `scripts/test-traceability.sh` (61 assertions pass), which checks correctness, not latency. |
| Correctness gates | — | 302 tests; 53 API smoke; 61 traceability assertions | MEASURED by CI/local runs (`dotnet test`, `test-api.sh`, `test-traceability.sh`, `run-all-tests.sh` 9-stage). Correctness, not business latency. |

No business-latency number in this file is measured.

## Guardrails / SLO links

- FEFO correctness SLO (`minierp-fefo-correctness`, budget 0, test-gate enforced);
  `X-Correlation-ID`; destructive-gate contracts (`qa-gate.sh`); RBAC by function.
- SLOs: `observability/slo.yaml` (job `minierp` :5002, uncertain port).
- .NET micro-bench not added here (needs `dotnet-script`, absent); existing
  harnesses below are the source of truth.

## Reproduce (existing harnesses — no new code)

```bash
cd MiniERP-Manufacturing-Warehouse
bash scripts/test-traceability.sh http://localhost:5000   # 61 assertions
bash scripts/test-api.sh                                  # 53 HTTP checks
bash scripts/run-all-tests.sh                             # 9-stage acceptance (needs Oracle QA instance)
ConnectionStrings__OracleDb='User Id=probe;Password=probe;Data Source=127.0.0.1:1/FREEPDB1;' \
  dotnet test tests/MiniERP.Api.Tests --filter "Category!=Integration"   # 247 hermetic
```
