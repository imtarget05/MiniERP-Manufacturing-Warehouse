# Phase 3 — Manufacturing Genealogy (report)

Design reference: `04-MiniERP-UPGRADE-DESIGN.md` §Phase 3 — FEFO/FIFO lot allocation,
material issue, traceable completion, input/output genealogy, backward/forward trace.
Gate: *end-to-end production demo returns exact genealogy* (PASS on real Oracle/API state).

## 1. PL/SQL (sql/08_traceability_plsql.sql, package ERP_TRACEABILITY v1.2.0-phase3)

Additive-only extension of the Phase 2 package (spec + body, 932 lines):

| Procedure | Contract |
|---|---|
| `allocate_lots_preview(p_po_no, p_cur OUT SYS_REFCURSOR)` | Read-only FEFO/FIFO availability per BOM material: ACTIVE, unexpired lots, `QTY_ON_HAND - QTY_RESERVED`, FEFO-then-FIFO order. `-20002` when the order does not exist. |
| `issue_lot_to_production(p_po_no, p_idem_key, p_req_hash, p_user)` | Single tx: locks the order, walks LOT-tracked BOM lines, consumes lots FEFO/FIFO exactly, mirrors `STOCK`, writes `MFG_CONSUME` txns, `ISSUE_TO_PRODUCTION` events and `PRODUCTION_LOT_CONSUMPTION` genealogy. Shortage → `-20007`; hold/frozen lot → `-20011`. Idempotent via private `check_idem`/`mark_idem` (ORA-20013 conflict / ORA-20014 replay). |
| `complete_production_traceable(p_po_no, p_fg_lot, p_qty, p_loc_code, p_tolerance, p_idem_key, p_req_hash, p_user)` | Single tx: status checks (`-20004`/`-20005`), validates every LOT line issued within tolerance (0..1, default exact), creates/validates the FG lot (reuse when exists, `-20001` on mismatch), writes FG `LOT_STOCK` + `MFG_OUTPUT` txn + `PRODUCTION_OUTPUT` event + `PRODUCTION_LOT_OUTPUT` link + barcode, consumes **non-lot** BOM lines from `STOCK`, flips the order `COMPLETED`, records idempotency (`fgLot`+`qty` body). |

**Key design decision — no delegation to legacy completion.** The first draft called
`ERP_OPERATIONS.complete_production_order` to close the order. Review caught a
double-consumption bug: the legacy procedure pre-checks and consumes **all** BOM lines
from aggregate `STOCK`, but `issue_lot_to_production` has already decremented `STOCK`
for LOT-tracked lines — the legacy pre-flight would raise a false `-20007` or
double-issue. The traceable procedure now performs the close itself (non-lot lines
only + status flip) in the same transaction.

All event types used are inside the `CK_TEV_TYPE` CHECK constraint of
`TRACEABILITY_EVENT`. Every column referenced (`INVENTORY_LOT.CREATED_AT`,
`STOCK.UPDATED_AT`, `LOT_STOCK.*`, `PRODUCTION_LOT_*`, `BOM/BOM_DETAIL.*`,
`PRODUCTION_ORDER.*`, `WAREHOUSE_LOCATION.*`, `ITEM.TRACE_MODE`) was verified
against `sql/01_schema.sql` + `sql/07_traceability_schema.sql`.

## 2. API (src/Program.cs section 7, src/Services/ErpDbService.Traceability.cs)

| Route | Behavior |
|---|---|
| `GET /api/manufacturing/production-order/{poNo}/allocate-lots` | Runs `allocate_lots_preview` via raw `OracleCommand` + `OracleDbType.RefCursor`, merges BOM need (`QTY_REQUIRED × QTY_PLANNED`), aggregate `STOCK` for non-lot lines, FEFO lot suggestions (`TraceabilityLogic.OrderForIssue`). Oracle errors mapped to `ErpBusinessException`. |
| `POST /api/manufacturing/production-order/{poNo}/issue-lots` | Body `{ idempotencyKey }`; 200 `{success, replayed:false}`; replay sentinel → 200 `replayed:true`; conflict → 409; shortage/hold → presenter contract. |
| `POST /api/manufacturing/production-order/{poNo}/complete-traceable` | Body `{ fgLot?, qty?, locationCode?, tolerance?, idempotencyKey }`; defaults auto `FG-{po}-001`, tolerance 0. Same replay/409 contract; returns `fgLot`. |
| `GET /api/trace/{lotCode}?direction=backward\|forward\|both` | Genealogy tree from explicit links only (`TraceNodeDto`: LOT → PRODUCTION_ORDER → LOT …, terminal `SOURCE` leaf = receipt reference). `TraceabilityLogic.NormalizeTraceDirection` (default BOTH, invalid → 400); unknown lot → 404. |

Idempotency hashes are separated per operation (`ISSUE_LOTS`, `COMPLETE_TRACEABLE`
includes po/fgLot/qty/loc/tol) so one key can never collide across endpoints.
Trace traversal: depth cap 4, visited set on `LOT:`/`PO:` keys (dedupes diamonds),
forward keyed `POF:` so BOTH can walk both directions.

## 3. Dashboard

- **Sản xuất & BOM tab**: "Phân bổ FEFO" card → `GET …/allocate-lots`, per-line
  need/available/FEFO lot list with THIẾU (short) highlight.
- **Nhận hàng & Lots tab**: "Trace genealogy" card — direction select
  (cả hai / lùi / xuôi) → `GET /api/trace/{lot}` recursive tree render.

## 4. Tests (TraceabilityPhase3Tests.cs)

12 new DB-free cases: direction normalization (default/case/trim/invalid→throw),
per-operation hash separation, allocation DTO shortfall + FEFO suggestions,
explicit-link genealogy tree (LOT→PO→LOT→SOURCE), tolerance default = exact, and
presenter/mapper contract for `ERR_MATERIAL_SHORTAGE`/`ERR_PO_ALREADY_COMPLETED`/
`ERR_PO_CANCELLED` (all 409).

## 5. Final Verification (real Oracle/API)

- `dotnet build src/MiniERP.Api.csproj -p:TreatWarningsAsErrors=true` → 0 warnings, 0 errors.
- Full xUnit suite: **164/164 passed**, including Oracle integration tests.
- Traceability E2E: **61/61 checks passed**. It proves FEFO selection, held/expired exclusion, exact lot issue, FG output, backward/forward genealogy and `STOCK`/`LOT_STOCK` reconciliation.
- Evidence: `artifacts/factory-upgrade/final/evidence/traceability-e2e.log`, `10-fefo-allocation.json`, `11-issue-lots.json`, `12-complete-traceable.json`, `13-backward-trace.json`, `14-forward-trace.json`.

