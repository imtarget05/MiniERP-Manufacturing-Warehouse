# G1 TDD evidence — integration outbox foundation

## User journey

As an integration operator, I need a durable, idempotent event store so that a
later dispatcher can replay delivery safely without changing current ERP
workflows.

## Baseline → requirement → code → tests → evidence

| Stage | Evidence |
|---|---|
| Baseline | 224 FactoryFlow non-integration tests passed; see G0 audit. |
| Requirement | Add only a rerunnable outbox schema contract; do not wire a producer while PL/SQL owns internal commits. |
| RED | `dotnet test ... --filter 'FullyQualifiedName~IntegrationOutboxContractTests' -c Release` failed 2/2 because migration and loader entry did not exist. |
| GREEN | Same command passed 2/2 after `sql/12_integration_outbox_schema.sql` and loader update. |

| Guarantee | Test | Type | Result |
|---|---|---|---|
| The migration declares event, aggregate, payload, idempotency and bounded delivery state | `IntegrationOutboxContractTests.IntegrationOutboxMigration_IsAdditiveAndSupportsSafeReplay` | source contract | pass |
| Fresh QA schema loading includes the additive migration after current schemas | `IntegrationOutboxContractTests.SqlLoader_AppliesIntegrationOutboxAfterExistingSchemas` | source contract | pass |

## Coverage and gap

The non-integration suite was collected with `XPlat Code Coverage`: 226 tests
passed, with aggregate line coverage 14.88% and branch coverage 33.43%. This
does **not** satisfy an 80% coverage target, so G1 makes no such claim; a large
part of the API is purposely exercised only through Oracle integration tests.
The two new contract tests protect repository wiring, not Oracle execution.
Oracle integration coverage is deliberately blocked until a disposable QA
instance is available. The required next proof is an Oracle test for
JSON/check/unique constraints and a transaction rollback test after G3 creates
a caller-owned boundary.
