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
Oracle execution is verified below; the required next proof is a deterministic
reservation concurrency test and a transaction rollback test after G3 creates
a caller-owned boundary.

## Disposable Oracle QA execution

The QA compose profile was also corrected: it now overrides the base external
Oracle volume with `minierp-qa_oracle_data`. The `QaComposeContractTests` RED
test failed before the override and passed after it. On that isolated volume,
the schema loader executed `12_integration_outbox_schema.sql` successfully and
the .NET 8 integration suite passed **81/81** against an explicit QA DSN.

The host machine has only .NET 10, which caused `WebApplicationFactory` JSON
serialization failures because the net8 project permits major roll-forward.
The integration evidence above was therefore run in `mcr.microsoft.com/dotnet/sdk:8.0`,
not treated as a product failure.
