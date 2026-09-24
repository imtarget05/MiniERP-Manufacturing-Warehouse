# Requirement Traceability Matrix (RTM)
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** BA-RTM-007  
**Traceability Scope:** Business Requirements $\rightarrow$ REST API Endpoints $\rightarrow$ UI Components $\rightarrow$ Automated Tests $\rightarrow$ Implementation Status  

---

## 1. Traceability Matrix

| Req ID | Requirement Description | API Endpoint(s) | UI Component / Tab | Automated Test Class & Method | Status |
|:---:|---|---|---|---|:---:|
| **FR-001** | Raw Material Goods Receipt with Lot & Expiry | `POST /api/warehouse/receipts/{poNo}/receive`, `POST /api/stock/in` | Dashboard $\rightarrow$ "Nhận hàng & Lots" (`tab-lots`) | `TraceabilityPhase2Tests.ReceiveLot_Success`, `ApiIntegrationTests.StockIn_UnknownItem_Returns404` | 🟢 Implemented |
| **FR-002** | BOM Evaluation & Pre-Flight Stock Check | `POST /api/automation/production-order/{poNo}/material-check` | Dashboard $\rightarrow$ "Sản xuất & BOM" (`tab-mfg`) | `AutomationIntegrationTests.MaterialCheck_EnoughStock_ReturnsReadyStatus` | 🟢 Implemented |
| **FR-003** | Material Soft Reservation for PO Release | `POST /api/automation/production-order/{poNo}/reserve`, `.../release` | Dashboard $\rightarrow$ "Sản xuất & BOM" | `AutomationIntegrationTests.ReserveMaterials_SufficientStock_ReservesSuccessfully` | 🟢 Implemented |
| **FR-004** | Atomic Production Order Completion | `POST /api/manufacturing/production-order/{poNo}/complete` | Dashboard $\rightarrow$ "Sản xuất & BOM", "Sự cố PO001" | `ApiIntegrationTests.Phase1_AcceptanceCriteria_FullWorkflow_Receipt100_Produce30_Consume30_BalanceVerified` | 🟢 Implemented |
| **FR-005** | Shortage Incident Detection & Auto Alert | `POST /api/manufacturing/production-order/{poNo}/complete` (ORA-20007) | Dashboard $\rightarrow$ "Sự cố PO001" (`tab-incident`) | `ApiIntegrationTests.CompleteOrder_MaterialShortage_ReportsOra20007AndKeepsAutonomousLog` | 🟢 Implemented |
| **FR-006** | FEFO Material Allocation by Earliest Expiry | `GET /api/manufacturing/production-order/{poNo}/allocate-lots` | Dashboard $\rightarrow$ "Nhận hàng & Lots" | `TraceabilityPhase3Tests.AllocateLots_SortsByEarliestExpiry` | 🟢 Implemented |
| **FR-007** | Traceable PO Lot Issue & FG Lot Generation | `POST /.../{poNo}/issue-lots`, `POST /.../{poNo}/complete-traceable` | Dashboard $\rightarrow$ "Nhận hàng & Lots" | `TraceabilityPhase3Tests.CompleteTraceable_GeneratesOutputAndConsumes` | 🟢 Implemented |
| **FR-008** | Bidirectional Genealogy Tree Traversal | `GET /api/trace/{lotCode}?direction=backward\|forward` | Dashboard $\rightarrow$ "Cây phả hệ" (`genealogy-tree`) | `TraceabilityPhase4Tests.BackwardTrace_ReturnsFullGenealogy` | 🟢 Implemented |
| **FR-009** | Two-Person Approval for Stock Adjustments | `POST /api/automation/approvals`, `POST /.../decision`, `/stock/adjust` | Dashboard $\rightarrow$ Admin Operations | `AutomationIntegrationTests.AdjustStock_RequiresApprovalGate` | 🟢 Implemented |
| **FR-010** | PBKDF2 Authentication & Token Refresh | `POST /api/auth/login`, `POST /api/auth/refresh`, `/api/auth/me` | Dashboard Sidebar $\rightarrow$ Auth Box | `Phase2RbacTests.Login_ValidCredentials_ReturnsToken`, `RefreshToken_Valid_ReturnsNewToken` | 🟢 Implemented |
| **FR-011** | Backend RBAC Mutation Policy Enforcement | Middleware: `MutationAuthorizationMiddleware` | Client HTTP Headers | `Phase2RbacTests.WarehouseOperator_CanMutateWarehouse_CannotModifyAdmin` | 🟢 Implemented |
| **FR-012** | Immutable Audit Trail Recording | `APP_AUDIT_EVENT`, Middleware: `AuditMiddleware` | Database Audit / Support Logs | `SecurityContractTests.AuditMiddleware_LogsMutations` | 🟢 Implemented |
| **FR-013** | Barcode Label Generation (Zebra ZPL & HTML)| `POST /api/labels`, `GET /api/labels/{id}/render` | Dashboard $\rightarrow$ "In nhãn Barcode" | `TraceabilityPhase2Tests.PrintLabel_GeneratesZplAndHtml` | 🟢 Implemented |
| **FR-014** | Fail-Soft External Helpdesk Dispatch | `POST /api/integration/helpdesk/incidents`, `/deliveries/{ref}` | Background Outbox (`HELPDESK_DELIVERY`) | `HelpdeskIntegrationTests.DeliverIncident_Success_MarksSent` | 🟢 Implemented |
| **FR-015** | System Health Probes & Package Verification| `GET /api/health`, `/api/health/ready`, `/api/health/details` | Dashboard Sidebar $\rightarrow$ API Status dot | `ApiIntegrationTests.Health_ReportsDatabaseUpAndPackageValid` | 🟢 Implemented |

---

## 2. Verification Sign-off Summary

- **Total Functional Requirements Tracked:** 15
- **Verified with Automated Tests:** 15 / 15 (100%)
- **Covered in REST API Endpoints:** 15 / 15 (100%)
- **Covered in Web Dashboard Interface:** 15 / 15 (100%)
