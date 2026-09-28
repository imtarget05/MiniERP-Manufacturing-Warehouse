# Requirement Traceability Matrix (RTM)
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** BA-RTM-007  
**Traceability Scope:** Business Requirements $\rightarrow$ REST API Endpoints $\rightarrow$ UI Components $\rightarrow$ Automated Tests $\rightarrow$ Implementation Status  

---

## 1. Traceability Matrix

`UI Coverage` is a per-row verdict, not a blanket claim: **Full** = every endpoint
of the row is reachable from the shipped dashboard, **Partial** = the dashboard
reaches the flow but not every endpoint or not every outcome, **Backend only** =
the dashboard never calls the endpoint. The test symbols below are the exact
`Class.Method` names enforced by `DocumentationTraceabilityTests`; a row whose
automation is scheduled but not yet executed says `Not yet verified (W5)` and is
excluded from the verified count. **As of the 2026-09-26 W5a/W8 runs no row is
pending any more** — see §2.

| Req ID | Requirement Description | API Endpoint(s) | UI Component / Tab | UI Coverage | Automated Test Class & Method | Pending Verification (W5) | Status |
|:---:|---|---|---|:---:|---|---|:---:|
| **FR-001** | Raw Material Goods Receipt with Lot & Expiry | `POST /api/warehouse/receipts/{purchaseOrderNo}/receive`, `POST /api/stock/in` | `tab-lots` (Nhận hàng & Lots), `tab-stock` (Kho & Tồn kho) | Full | `ApiIntegrationTests.StockIn_UnknownItem_Returns404`, `ApiIntegrationTests.StockIn_NonPositiveQuantity_Returns400` | — | 🟢 Implemented |
| **FR-002** | BOM Evaluation & Pre-Flight Stock Check | `POST /api/automation/production-order/{poNo}/material-check` | `tab-mfg` (Sản xuất & BOM) | Full | `AutomationIntegrationTests.MaterialCheck_EnoughStock_ReturnsReadyStatus` | — | 🟢 Implemented |
| **FR-003** | Material Soft Reservation for PO Release | `POST /api/automation/production-order/{poNo}/reserve`, `POST /api/automation/production-order/{poNo}/release` | — (no dashboard surface) | Backend only | `AutomationIntegrationTests.ReserveMaterials_SufficientStock_ReservesSuccessfully`, `AutomationIntegrationTests.ReleaseReservations_Success_ReturnsSuccess` | — | 🟢 Implemented |
| **FR-004** | Atomic Production Order Completion | `POST /api/manufacturing/production-order/{poNo}/complete` | `tab-mfg` (Sản xuất & BOM) | Full | `ApiIntegrationTests.Phase1_AcceptanceCriteria_FullWorkflow_Receipt100_Produce30_Consume30_BalanceVerified` | — | 🟢 Implemented |
| **FR-005** | Shortage Incident Detection & Auto Alert | `POST /api/manufacturing/production-order/{poNo}/complete` (ORA-20007) | `tab-incident` (Sự cố PO001, static runbook) | Partial | `ApiIntegrationTests.CompleteOrder_MaterialShortage_ReportsOra20007AndKeepsAutonomousLog` | — | 🟢 Implemented |
| **FR-006** | FEFO Material Allocation by Earliest Expiry | `GET /api/manufacturing/production-order/{poNo}/allocate-lots` | `tab-lots` (Trace genealogy card) | Full | `TraceabilityLogicTests.OrderForIssue_PrefersEarliestExpiryThenFifo`, `TraceabilityPhase3Tests.AllocationLine_ReportsShortfallWithFeFoSuggestions` | — | 🟢 Implemented |
| **FR-007** | Traceable PO Lot Issue & FG Lot Generation | `POST /api/manufacturing/production-order/{poNo}/issue-lots`, `POST /api/manufacturing/production-order/{poNo}/complete-traceable` | — (no dashboard surface) | Backend only | `TraceabilityPhase3Tests.IdempotencyHash_Operations_AreSeparated`, `TraceabilityPhase3Tests.CompleteTraceableRequest_ToleranceDefaultIsExact` | — | 🟢 Implemented |
| **FR-008** | Bidirectional Genealogy Tree Traversal | `GET /api/trace/{lotCode}?direction=backward\|forward` | `tab-lots` (Trace genealogy card) | Full | `TraceabilityPhase3Tests.TraceTree_Backward_HasExplicitLinksOnly`, `TraceabilityPhase4Tests.TraceService_UsesDedicatedForwardOutputRecord` | — | 🟢 Implemented |
| **FR-009** | Two-Person Approval for Stock Adjustments | `GET /api/automation/approvals`, `POST /api/automation/approvals`, `POST /api/automation/approvals/{approvalNo}/decision`, `POST /api/automation/stock/adjust` | `tab-incident` → `#approval-list` (danh sách duyệt `status=PENDING`, **chỉ đọc**) | Partial | `AutomationIntegrationTests.ApprovalGate_DecidedRequestIsReadable`, `SecurityContractTests.StockAdjust_NonAdminRole_Returns403` | — | 🟢 Implemented |
| **FR-010** | PBKDF2 Authentication & Token Refresh | `POST /api/auth/login`, `POST /api/auth/refresh`, `GET /api/auth/me` | Sidebar auth box (`.auth-box`) | Partial | `Phase2RbacTests.Login_ValidCredentials_ReturnsToken`, `RefreshToken_Valid_ReturnsNewToken`, `SecurityContractTests.TokenService_IssueAndValidate_RoundTripsClaims` | — | 🟢 Implemented |
| **FR-011** | Backend RBAC Mutation Policy Enforcement | Middleware: `MutationAuthorizationMiddleware` | Client HTTP Headers only | Backend only | `Phase2RbacTests.WarehouseOperator_CanMutateWarehouse_ButNotStockAdjustOrAdmin`, `SecurityContractTests.MutationPolicy_MapsProtectedOperations` | — | 🟢 Implemented |
| **FR-012** | Immutable Audit Trail Recording | `APP_AUDIT_EVENT`, Middleware: `AuditMiddleware` | Database audit table only | Backend only | `SecurityContractTests.AuditMiddleware_RecordsDeniedMutationInAppAuditEvent`, `SecurityContractTests.AuditMiddleware_RecordsAuthenticatedMutationWithItsRouteEntityKey` | — | 🟢 Implemented |
| **FR-013** | Barcode Label Generation (Zebra ZPL & HTML) | `POST /api/labels`, `GET /api/labels/{id}/render` | `tab-lots` (In nhãn lot card) | Partial | `LabelRenderServiceTests.RenderZpl_ContainsBarcodeBlock`, `LabelRenderServiceTests.RenderHtml_ContainsLotQtyAndHumanReadableCode` | — | 🟢 Implemented |
| **FR-014** | Fail-Soft External Helpdesk Dispatch | `POST /api/integration/helpdesk/incidents`, `GET /api/integration/helpdesk/deliveries/{externalRef}` | — (no dashboard surface) | Backend only | `HelpdeskIntegrationTests.SendIncident_WhenSuccess_SendsBearerAndIdempotencyHeaders`, `HelpdeskIntegrationTests.SendIncident_Replay_ReturnsSentWithoutReinvokingHttp`, `HelpdeskIntegrationTests.SendIncident_PostsToTheReceiverIncidentRoute`, `HelpdeskIntegrationTests.HelpdeskOptions_IncidentPath_DefaultsToTheReceiverRoute` | — | 🟢 Implemented |
| **FR-015** | System Health Probes & Package Verification | `GET /api/health`, `GET /api/health/ready`, `GET /api/health/details` | Sidebar API status dot (`#api-dot`, `#api-status`) | Partial | `ApiIntegrationTests.Health_ReportsDatabaseUpAndPackageValid` | — | 🟢 Implemented |

---

## 2. Verification Sign-off Summary

- **Total Functional Requirements Tracked:** 15
- **Verified with Automated Tests:** 15 / 15 — FR-012 was the last pending row. Its two
  audit-trail tests (`SecurityContractTests.AuditMiddleware_RecordsDeniedMutationInAppAuditEvent`,
  `SecurityContractTests.AuditMiddleware_RecordsAuthenticatedMutationWithItsRouteEntityKey`)
  carry `[Trait("Category","Integration")]` and both ran **green** in the 2026-09-26 W8
  full run (`Failed: 0, Passed: 302, Skipped: 0, Total: 302`, disposable Oracle 23c).
  The `Not yet verified` marker is retained as a convention: `DocumentationTraceabilityTests`
  enforces that any row carrying it is *excluded* from the verified count and names a
  real scheduled test, so a future regression cannot silently inflate the ratio.
- **Covered in REST API Endpoints:** 15 / 15 (100%) — every row names a served path, and
  `DocumentationTraceabilityTests.RtmEndpoints_AreServedByTheOpenApiDocument` checks each
  one against the shipped OpenAPI document.
- **Covered in Web Dashboard Interface:** 5 / 15 full, 5 / 15 partial, 5 / 15 backend-only
  (10 / 15 reachable from the dashboard in some form). This is a re-count of the per-row
  `UI Coverage` column above, not an independent measurement; the earlier blanket
  "15 / 15 (100%)" line was wrong and has been removed. `DocumentationTraceabilityTests.
  RtmUiSelectors_ExistInTheShippedDashboard` and `RtmUiCoverage_OnlyUsesTheDocumentedScale`
  are the only UI-side guards, and they check the selectors and the scale — not end-to-end
  browser reachability, and not whether a verdict is *true*. A genuine browser-level UI
  traceability run is **not** part of the 2026-09-26 evidence and remains open.

  **How the census was derived, and what corrected it.** Each row's verdict was checked
  against every `/api/…` call in the shipped `dashboard/app.js` and every `id="tab-…"`
  section in `dashboard/index.html`. That audit found **FR-009 mis-described**: it was
  recorded as *"— (no dashboard surface) / Backend only"*, but the dashboard does render the
  approvals queue — `index.html:45` nav → `index.html:341` `<section id="tab-incident">` →
  `index.html:355` `<div id="approval-list">`, fed by `app.js:299`
  `GET /api/automation/approvals?status=PENDING&take=20` and rendered at `app.js:455`. The
  row is therefore **Partial**, not Backend-only: the queue and each request's status are
  visible, but the dashboard exposes no way to *create* a request or *decide* one, so the
  three `POST` endpoints in the row are unreachable from the UI. The other five
  backend-only rows were re-checked the same way and hold (`FR-003` `reserve`/`release` and
  `FR-007` `issue-lots`/`complete-traceable` are never called; `FR-011` is middleware;
  `FR-012` has no `/api/audit` call). `GET /api/automation/approvals` was added to the row's
  API column so the row and its verdict agree; the path is served at `Program.cs:823` and is
  present in the OpenAPI document, so `RtmEndpoints_AreServedByTheOpenApiDocument` still holds.
- **Test-suite context:** the symbols above are drawn from a suite of **302** tests
  (247 without a database, 55 `Category=Integration` against Oracle); see
  [`../IMPLEMENTATION_STATUS.md`](../IMPLEMENTATION_STATUS.md).
