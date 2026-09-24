# Project Charter
**Project Name:** MiniERP Manufacturing & Warehouse Implementation  
**Project Sponsor:** Executive Vice President of Operations, Tân Vĩnh Footwear & Apparel  
**Project Manager:** ERP Project Delivery Lead  
**Document Code:** PM-CHA-001  
**Target Completion:** Q3 2026  

---

## 1. Project Purpose & Justification

Tân Vĩnh Footwear & Apparel operates contract manufacturing assembly lines supplying global sportswear brands. Current manual and spreadsheet-based operations have led to:
1. Recurring inventory discrepancy rates exceeding 18% of ledger value.
2. Unplanned factory line shutdowns due to lack of pre-flight raw material availability checks.
3. Inability to comply with brand-mandated lot genealogy audits during quality recall investigations.
4. Weak internal controls surrounding inventory adjustments.

The **MiniERP Project** is chartered to modernize plant operations by implementing an integrated Manufacturing Execution (MES) and Warehouse Management (WMS) system.

---

## 2. Project Objectives & Success Criteria

| Objective | Target Metric | Measurement Method |
|---|---|---|
| **Inventory Record Accuracy (IRA)** | Elevate ledger-to-physical inventory accuracy to $\ge 98\%$ | Bi-weekly cycle counts across `WH_RAW` & `WH_FG` |
| **Material Starvation Downtime** | Reduce unplanned line downtime due to material shortages by $\ge 80\%$ | Shop floor shift logs and `PO_STATE_HISTORY` |
| **Lot Traceability Compliance** | Complete backward genealogy audit in under 60 seconds (vs 48 hours manually) | Automated genealogy query `/api/trace/{lotCode}` |
| **Internal Control Enforcement** | 100% of inventory adjustments above threshold require dual-manager approval | Audit trail queries in `APP_AUDIT_EVENT` & `APPROVAL_REQUEST` |
| **System Uptime & Stability** | Maintain $\ge 99.5\%$ service availability during active factory shifts | `/api/health` probes and uptime monitoring |

---

## 3. High-Level Project Scope

- **In Scope:**
  - Master data catalog (Warehouses, Items, multi-level BOMs, Users, Roles).
  - Goods receipt with lot tracking, expiration dates, and bin locations.
  - Production order lifecycle management (`CREATED` $\rightarrow$ `RELEASED` $\rightarrow$ `COMPLETED`).
  - Automated BOM explosion and material consumption within atomic ACID transactions.
  - First-Expiry-First-Out (FEFO) allocation and bidirectional genealogy tracing.
  - Two-person approval gate for stock adjustments.
  - Handheld barcode scanner integration with idempotency keys.
  - Automated database backup, disaster recovery testing, and IT Helpdesk outbox synchronization.
- **Out of Scope:**
  - Complex SAP/Oracle Financials general ledger accounting and tax reporting.
  - Human resource payroll and timecard management.
  - Heavy automated logistics fleet telematics.

---

## 4. Key Stakeholders

- **Executive Sponsor:** VP of Manufacturing Operations
- **Business Owners:** Warehouse Operations Manager, Production Floor Superintendent
- **Technical Lead:** Principal ERP Systems Architect / Tech Lead
- **Quality Lead:** Plant QA/QC Compliance Director
- **End-User Champions:** Senior Warehouseman, Line Planning Specialist
