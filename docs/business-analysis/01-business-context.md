# Business Context & Problem Statement
**Project:** MiniERP Manufacturing & Warehouse  
**Case Study Organization:** Tân Vĩnh Footwear & Apparel Manufacturing Co., Ltd. (Fictionalized Enterprise)  
**Location:** Bien Hoa II Industrial Zone, Dong Nai Province, Vietnam  
**Industry:** Athletic Footwear & Apparel Contract Manufacturing (OEM/ODM)  
**Document Code:** BA-CTX-001  

---

## 1. Company Background

**Tân Vĩnh Footwear & Apparel Manufacturing Co., Ltd.** operates a multi-tier production facility specializing in vulcanized running shoes and industrial technical apparel. The facility features:
- **3 Warehouses:**
  - `WH_RAW`: Raw Materials Warehouse (Rubber soles, mesh rolls, polyester thread spools, polyurethane adhesives, packaging cartons).
  - `WH_WIP`: Work-in-Progress Warehouse (Cut fabric panels, bonded uppers, semi-assembled mid-soles).
  - `WH_FG`: Finished Goods Warehouse (Export-ready boxed footwear organized for container loading).
- **Workforce:** ~1,200 floor workers, 45 warehouse operators, 18 production planners, and 6 plant quality assurance inspectors.
- **Production Output:** 8,000–12,000 pairs of shoes per day across 4 assembly lines.

---

## 2. Core Business Problem & Operational Bottlenecks

Prior to implementing MiniERP, plant operations relied on a hybrid of localized spreadsheets (Microsoft Excel), paper job tickets (Kanban cards), and disconnected legacy accounting packages. This resulted in critical operational failures:

### 2.1 Lack of Lot Traceability & Quality Recall Risk
When international brand clients (e.g. Nike, Adidas, Puma) conducted quality audits or flagged peeling outsole adhesives on delivered batches, the plant could not trace which supplier lot of polyurethane adhesive (`MAT_GLUE_01`) or rubber sole batch (`MAT_RUBBER_01`) was used. Isolating affected inventory required stopping the entire factory floor for 48 hours.

### 2.2 Inventory Discrepancies & Phantom Stock
Inventory levels recorded in spreadsheets were consistently out of sync with physical stock on the warehouse floor. Warehouse operators frequently scanned barcodes repeatedly or experienced network drops, creating duplicate stock transactions or negative balances.

### 2.3 Uncoordinated Production Releases & Line Stoppages
Production orders (`PO`) were released to the stitching and assembly lines without pre-flight validation against physical raw material stock. Lines were frequently halted midway through production due to missing eyelets, thread, or adhesives, causing costly idle labor.

### 2.4 Uncontrolled Inventory Adjustments & Lack of Auditability
Warehouse supervisors routinely made manual stock adjustments to balance ledger counts with physical shelf counts without multi-level managerial approval or immutable audit trails. This created severe internal control weaknesses and audit non-compliance.

### 2.5 Slow Incident Resolution & Disconnected IT Support
When production orders failed due to database or validation errors, shop floor operators lacked clear diagnostic error messages. IT support staff spent hours manually inspecting database tables without correlation IDs, autonomous error logs, or standardized runbooks.

---

## 3. ERP Transformation Objectives

The MiniERP implementation was commissioned to solve these challenges with the following core objectives:
1. **Single Source of Truth:** Unify raw materials, WIP, and finished goods inventory into a centralized transactional relational database (Oracle Database 23c).
2. **Guaranteed Transaction Atomicity:** Execute BOM material verification, stock consumption, and finished goods generation within atomic ACID database transactions.
3. **FEFO/FIFO Lot Traceability:** Automatically allocate materials by expiration date (FEFO) and maintain bidirectional genealogy from finished good lot down to supplier purchase orders.
4. **Enterprise Approval Gates:** Require two-person managerial approval for inventory adjustments and high-impact manual stock issues.
5. **Operational Resiliency:** Implement autonomous transaction error logging (`ERROR_LOG`), idempotent barcode processing, and standardized disaster recovery (RPO $\le 24\text{h}$, RTO $\le 2\text{h}$).
