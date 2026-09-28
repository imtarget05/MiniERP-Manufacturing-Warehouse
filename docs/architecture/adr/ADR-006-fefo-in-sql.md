# ADR-006: FEFO Lot Issue Ordered in SQL vs Application Layer

- **Status:** Accepted
- **Date:** 2026-09-27

## Context

ADR-003 settled the ledger shape (`STOCK` + immutable `INVENTORY_TRANSACTION` +
`LOT_STOCK`) but not WHERE lot selection order is enforced. Issuing material
with multiple live lots of different expiries must deterministically consume
the earliest-expiry lot first (FEFO) — even when two warehouse workers issue
concurrently. App-layer "read lots, sort, pick first" races between the read
and the write.

## Decision

Enforce FEFO inside the issuing SQL/PL/SQL transaction: `SELECT … ORDER BY
expiry_date FOR UPDATE` (earliest-expiry first) followed by the stock decrement
and journal insert in the same transaction. The database serialises concurrent
issuers; the order is a property of the transaction, not of caller discipline.

## Consequences

- Positive: FEFO holds under concurrency by construction; the 61-assertion
  traceability script proves earliest-expiry consumption deterministically.
- Negative: more business logic lives in PL/SQL (consistent with ADR-001's
  database-centric stance, but harder to unit-test than C# — covered by Oracle
  integration tests instead).

## Alternatives

- App-layer sorting: simple to test, but TOCTOU race — two issuers read the
  same "earliest" lot and both consume it; traceability silently corrupts.
- FIFO by receipt order: easier, but wrong for perishables/chemicals where
  expiry, not arrival, determines usability; customer complaints need
  expiry-based proof.
