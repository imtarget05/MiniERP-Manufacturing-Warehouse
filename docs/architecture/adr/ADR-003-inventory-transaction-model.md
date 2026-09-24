# ADR-003: Immutable Double-Entry Style Inventory Transaction Model

## Status
**Accepted** (2026-09-24)

## Context
A major defect in legacy ERP implementations is allowing direct SQL `UPDATE` on current stock tables without recording the causative transaction, making historical audits impossible. We evaluated:
1. Pure Event Sourcing (No mutable `STOCK` table; balance calculated by aggregating all events on read).
2. Direct Balance Updates (Mutable `STOCK` without historical ledger).
3. Hybrid Balance + Immutable Transaction Ledger (`STOCK` snapshot + `INVENTORY_TRANSACTION` journal).

## Decision
We implemented a **Hybrid Balance Table with Mandatory Immutable Transaction Ledger (`STOCK` + `INVENTORY_TRANSACTION` + `TRACEABILITY_EVENT`)**.

## Rationale
- **Performance:** Reading stock on hand requires instant $O(1)$ index lookup on `STOCK(WAREHOUSE_ID, ITEM_ID)` rather than aggregating millions of historical event rows on every read.
- **Audit Non-Repudiation:** Every procedure that modifies `STOCK.QTY` is strictly required to insert an `INVENTORY_TRANSACTION` row within the same transaction recording `TXN_TYPE`, `QTY`, `BALANCE_AFTER`, `REF_NO`, `CREATED_BY`, and `CREATED_AT`.
- **Traceability Integration:** For lot-managed items, `LOT_STOCK` and `TRACEABILITY_EVENT` extend this pattern down to the bin and batch level.

## Consequences
- **Positive:** Blazing fast read performance paired with 100% mathematical auditability.
- **Negative:** Storage requirement grows over time; requires periodic archiving of old transaction partitions.
