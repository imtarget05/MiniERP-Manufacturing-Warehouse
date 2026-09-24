# Phase 2 — Receiving / put-away / scan movement

Status: PASS (Oracle/API receive, put-away, move and idempotency verified in final E2E).

## PL/SQL (`sql/08_traceability_plsql.sql`, now 573 lines, v1.1.0-phase2)
- `check_idem/mark_idem` (private) + `idem_replay` (public probe):
  same key + same hash => ORA-20014 DUPLICATE_REPLAY (safe no-op);
  same key + other hash => ORA-20013 conflict. New codes mapped in
  ErpErrorMapper (20013/20014) + ErpApiPresenter (409/200).
- `receive_lot_stock`: validates lot<->item uniqueness, PO line match,
  cumulative received <= PO qty; writes LOT_STOCK + STOCK + STOCK_IN txn +
  TRACE RECEIVE + BARCODE LOT:code in ONE tx; never flips legacy PO status.
- `putaway_lot`: same-warehouse relocate; warehouse total unchanged.
- `move_lot_stock`: cross-warehouse relocate; both aggregates updated with
  STOCK_OUT/STOCK_IN pair; grand total preserved.
- `set_lot_hold`: ACTIVE<->HOLD/BLOCKED; CONSUMED/EXPIRED immutable.
- All stock writes lock rows first (FOR UPDATE), roll back fully on failure,
  log via ERP_OPERATIONS.log_error (autonomous) for unexpected errors only.

## API (`src/Program.cs` section 6 — 11 new routes, all additive)
- GET/POST /api/warehouse/{wh}/locations | GET /api/warehouse/lots/{lot}
  GET /api/warehouse/lots/{lot}/stock | GET /api/barcodes/resolve/{code}
  POST /api/warehouse/receipts/{po}/receive (replayed flag on retry)
  POST /api/warehouse/putaway | POST /api/warehouse/move (replayed flag)
  POST /api/warehouse/lots/{lot}/hold | .../release-hold
- Service: `ErpDbService.Traceability.cs` (partial, same class) — reads +
  canonical IdempotencyHash + IsReplay + write-through methods.

## Dashboard
- New tab "Nhận hàng & Lots": receive form (PO/item/qty/lot/loc/idemkey)
  with duplicate-retry banner; Scan & Move form (lot scan + from/to + qty)
  with resolve button, Enter-to-resolve, focus return after each op.

## Seed (`sql/09_traceability_seed.sql`, in run-sql.sh load order)
- RCV-01/A-01-01/A-01-02 (WH_RAW), RCV-01/FG-01-01 (WH_FG), LINE-01 (WH_WIP);
  RAW items => TRACE_MODE=LOT; demo PO PO_PUR_LOT_01 (100 rubber soles).

## Tests
- `TraceabilityPhase2Tests.cs` (6 cases) + 2 new InlineData in contract tests.
- Final full suite: **164/164 passed**, including Oracle integration.
- Final real API E2E: **61/61 checks**, including receive replay/conflict, label reprint safety, put-away, cross-warehouse move and total-quantity preservation.
- Evidence: `artifacts/factory-upgrade/final/evidence/traceability-e2e.log` and the numbered JSON responses.
