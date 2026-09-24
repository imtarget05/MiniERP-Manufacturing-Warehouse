# Phase 4 — Label Workflow & Printing (report)

Status: PASS (real Oracle/API label create, HTML/ZPL render and reprint audit verified).

## 1. PL/SQL (sql/08_traceability_plsql.sql, ERP_TRACEABILITY.create_label_job)

Additive procedure in `ERP_TRACEABILITY` package:

| Procedure | Contract |
|---|---|
| `create_label_job(p_label_type, p_entity_type, p_entity_key, p_copies, p_format, p_user, p_job_id OUT)` | Validates entity type (`LOT`, `ITEM`, `LOCATION`, `PRODUCTION_ORDER`), verifies target entity exists (`-20002` if not found), inserts immutable `LABEL_PRINT_JOB` audit row (`STATUS='PRINTED'`, `PRINTED_AT=SYSDATE`). |

**Reprint Safety Invariant:**
Calling `create_label_job` records an auditable print job event in `LABEL_PRINT_JOB`. It never modifies `INVENTORY_LOT`, never mutates `LOT_STOCK` or `STOCK`, and generates zero inventory transactions. Multiple reprints of the same entity key append new job audit entries without any stock side effects.

## 2. Service Layer (src/Services/LabelRenderService.cs)

Deterministic label rendering engine producing both HTML and ZPL (Zebra Programming Language):

- `RenderHtml(...)`: Structured card with company header, item code/name, lot code, quantity + UOM, location, timestamps, barcode representation, and human-readable code. Output HTML is byte-deterministic for identical inputs.
- `RenderZpl(...)`: Standard ZPL II output (`^XA ... ^XZ`) containing barcode blocks (`^BC`), item codes, lot identifiers, quantities, and human-readable text. Deterministic and ready for thermal barcode printers.

## 3. API Endpoints (src/Program.cs section 8)

| Route | Method | Description |
|---|---|---|
| `/api/labels` | POST | Creates a new label print/reprint job and returns rendered payload (`HTML` or `ZPL`). |
| `/api/labels` | GET | Lists print jobs filtered optionally by `entityType` and `entityKey`. |
| `/api/labels/{id:long}` | GET | Retrieves print job metadata by ID. |
| `/api/labels/{id:long}/render` | GET | Re-renders existing print job output deterministically. |

## 4. Dashboard (dashboard/index.html & dashboard/app.js)

- **Nhận hàng & Lots tab**: "In nhãn lot (label workflow)" card.
- Supports selecting entity key, label type (`RAW_MATERIAL`, `FINISHED_GOOD`, `LOCATION`), copies, format (`HTML` or `ZPL`).
- Generates live visual label preview in HTML or raw ZPL code block for label printer dispatch.
- Reprints are visibly audited with print job ID.

## 5. Tests (tests/MiniERP.Api.Tests/TraceabilityPhase4Tests.cs)

7 unit tests verifying:
- Format normalization (default `HTML`, case/whitespace insensitive, unknown throws `ArgumentException`).
- Entity type validation (`LOT`, `ITEM`, `LOCATION`, `PRODUCTION_ORDER` valid; unknown rejected).
- Label type validation (`RAW_MATERIAL`, `FINISHED_GOOD`, `LOCATION` valid; invalid rejected).
- `LabelJobDto` contract shape and serialization.
- `RenderHtml` deterministic output test (byte-identical reprints).
- `RenderZpl` deterministic output and barcode block structure verification.
- `LabelPrintResponse` payload integrity.

## 6. Final Verification (real Oracle/API)

- `dotnet build src/MiniERP.Api.csproj -p:TreatWarningsAsErrors=true` → 0 warnings, 0 errors.
- Full xUnit suite: **164/164 passed**, including Oracle integration tests.
- Traceability E2E: **61/61 checks passed**; three label/reprint jobs were audited and aggregate stock remained unchanged.
- Direct API probes confirmed label history and deterministic HTML/ZPL rendering.
- Evidence: `artifacts/factory-upgrade/final/evidence/traceability-e2e.log` and the label JSON responses.
