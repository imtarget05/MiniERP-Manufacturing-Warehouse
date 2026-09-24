# Project Scope Management Plan & WBS
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** PM-SCP-002  

---

## 1. Scope Baseline & Work Breakdown Structure (WBS)

```mermaid
flowchart TD
    WBS["1.0 MiniERP Implementation"]
    W1["1.1 Architecture & Database"]
    W2["1.2 Backend API & Core ERP"]
    W3["1.3 Traceability & FEFO"]
    W4["1.4 Enterprise Approvals & RBAC"]
    W5["1.5 Testing & Disaster Recovery"]
    W6["1.6 Go-Live & Support Runbooks"]

    WBS --> W1
    WBS --> W2
    WBS --> W3
    WBS --> W4
    WBS --> W5
    WBS --> W6

    W1 --> W11["Oracle Schema (31 Tables)"]
    W1 --> W12["PL/SQL Packages (ERP_OPERATIONS, etc.)"]
    W2 --> W21["ASP.NET Core 8 Web API"]
    W2 --> W22["BOM Atomic Consumption"]
    W3 --> W31["FEFO Allocation Algorithm"]
    W3 --> W32["Genealogy Traversal Engine"]
    W4 --> W41["PBKDF2 & JWT Security"]
    W4 --> W42["Two-Person Approval Gate"]
    W5 --> W51["Automated Test Suites (164+ Tests)"]
    W5 --> W52["expdp/impdp Backup Verification"]
    W6 --> W61["Legacy Data Migration Scripts"]
    W6 --> W62["Incident Support Runbook (INC-001 - INC-005)"]
```

---

## 2. In-Scope Deliverables

1. **Database Tier:**
   - 31 normalized Oracle tables across core ERP, business automation, lot traceability, and helpdesk outbox.
   - 3 production-grade PL/SQL packages (`ERP_OPERATIONS`, `ERP_AUTOMATION`, `ERP_TRACEABILITY`).
2. **Application Tier:**
   - ASP.NET Core 8 Web API supporting 57 route registrations.
   - Comprehensive error translation layer mapping Oracle `ORA-` errors into standardized problem details.
   - Idempotency middleware preventing duplicate scanner inputs.
3. **Frontend Tier:**
   - Single-page responsive web dashboard for warehouse and shop floor execution.
   - Visual KPI monitoring, lot movement tools, ZPL/HTML label generation, and genealogy visualizer.
4. **DevOps & DR Tier:**
   - Docker containerization for Oracle Database Free and application services.
   - Multi-stage GitHub Actions CI/CD pipeline.
   - Validated backup and disaster recovery runbooks with automated verification.

---

## 3. Scope Exclusions (Out of Scope)

- Advanced multi-plant inter-company transfer billing and multi-currency foreign exchange revaluations.
- Automated payroll, direct labor time-clock punch systems, and employee benefits administration.
- Custom IoT direct PLC hardware drivers (replaced by standardized REST API endpoints callable from floor barcode scanners and HMIs).
