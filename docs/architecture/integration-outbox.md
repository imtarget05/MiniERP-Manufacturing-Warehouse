# Integration outbox foundation

## Requirement

FactoryFlow needs durable, idempotent integration events without changing
inventory correctness, production completion or traceability semantics.

## Decision

`INTEGRATION_OUTBOX` is an additive Oracle table loaded after the existing
schemas. It supplies an immutable event identity, aggregate identifiers,
JSON payload, idempotency key, correlation ID, retry state, availability time,
lease field and dispatch index.

It is intentionally storage-only in G1. There is no trigger, worker or
business-procedure call in this change.

## Why no producer yet

Existing PL/SQL business procedures commit internally. Adding an API-side
insert after their call would admit a committed stock mutation with a missing
event; inserting before the call would admit an event for a rolled-back
mutation. Either violates the outbox guarantee.

G3 must first establish one caller-owned transaction boundary, then prove a
business change and its outbox row commit/roll back together in disposable
Oracle QA.

## Delivery contract for later phases

Dispatch is at-least-once. A consumer must deduplicate on a stable event or
idempotency key. A dispatcher will lease `PENDING`/retryable rows using
`AVAILABLE_AT` and `LOCKED_UNTIL`; after bounded attempts it marks
`DEAD_LETTER`. No consumer may mutate the immutable physical inventory ledger.
