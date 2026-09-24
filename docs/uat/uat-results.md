# User Acceptance Testing (UAT) Results Report
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** UAT-RES-003  
**Execution Period:** 2026-09-20 to 2026-09-24  
**Environment:** Staging Simulation Environment  

---

## 1. Executive Summary

User Acceptance Testing for the MiniERP Manufacturing & Warehouse implementation was conducted with representatives from Warehouse Logistics, Production Planning, Floor Operations, Internal Audit, and IT Support.

A total of **8 core UAT scenarios** (comprising 32 individual test steps) were executed. **100% of scenarios passed** without blocking defects. All critical business flows—including raw material receipt, BOM pre-flight validation, atomic production completion, dual-authorization approval, and backward quality recall—demonstrated full compliance with acceptance criteria.

---

## 2. Test Execution Summary

| Test Case ID | Scenario Title | Category | Severity | Result | Defects Logged |
|:---:|---|:---:|:---:|:---:|:---:|
| **UAT-INV-001** | Goods Receipt with Lot Number & Expiry Date | Warehouse | Critical | 🟢 PASSED | None |
| **UAT-INV-002** | Direct Goods Issue & Insufficient Quantity Check | Warehouse | High | 🟢 PASSED | None |
| **UAT-INV-003** | Bin Location Transfer (Putaway) | Warehouse | Medium | 🟢 PASSED | None |
| **UAT-MFG-001** | Production Order Creation & Shortage Detection | Manufacturing | Critical | 🟢 PASSED | None |
| **UAT-MFG-002** | Production Order Atomic Completion & FG Output | Manufacturing | Critical | 🟢 PASSED | None |
| **UAT-SEC-001** | Multi-Role Authorization & 403 Enforcement | Security | Critical | 🟢 PASSED | None |
| **UAT-APP-001** | Two-Person Approval Gate for Adjustments | Governance | High | 🟢 PASSED | None |
| **UAT-REP-001** | Operational Dashboard & Traceability Report | Reporting | High | 🟢 PASSED | None |

---

## 3. Detailed Acceptance Findings

1. **Transaction Atomicity:** During `UAT-MFG-002`, completing a production order for 30 pairs of `FG_RUNNER_PRO_42` reduced raw material stock by exactly 30 soles, 15m mesh, 3 spools thread, 6kg glue, and 30 boxes, while simultaneously producing 30 pairs of shoes. No orphaned records or partial deductions occurred.
2. **Shortage Resilience:** During `UAT-MFG-001`, when attempting to complete an order requiring 50 soles with only 40 in stock, the database raised `ORA-20007` and automatically rolled back all stock updates. The incident was recorded in `ERROR_LOG` with the exact actor and timestamp preserved.
3. **Traceability Speed:** Backward genealogy traversal on finished shoe lots rendered the full multi-tier component tree in under **85 milliseconds**, completely satisfying the compliance target of under 60 seconds.
4. **Approval Security:** Non-manager accounts attempting to bypass approval for stock adjustments were blocked with `403 Forbidden`. The system only executed adjustments upon verification of a valid `APPROVED` record.
