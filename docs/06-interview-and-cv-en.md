# Mini ERP — English CV Bullets & Interview Preparation (TKG Taekwang)

Tài liệu chuẩn bị hồ sơ ứng tuyển bằng tiếng Anh và bộ câu hỏi phỏng vấn kỹ thuật trực tiếp cho vị trí **IT ERP (C# + Oracle + PL/SQL)** tại TKG Taekwang.

---

## 1. PHẦN MÔ TẢ KINH NGHIỆM TRÊN CV TIẾNG ANH (RESUME SNIPPET)

```markdown
### Mini ERP System — Manufacturing & Warehouse Execution
**Role:** ERP Developer & Support Specialist | **Tech Stack:** C#, ASP.NET Core, Oracle Database 19c/21c, PL/SQL, Dapper, Docker

- Designed and engineered a core ERP system tailored for manufacturing plants, focusing on **Warehouse Management (WMS)** and **Manufacturing Execution (MES)** modules.
- Modeled 11 normalized relational Oracle tables (`STOCK`, `BOM`, `BOM_DETAIL`, `PRODUCTION_ORDER`, `INVENTORY_TRANSACTION`, `ERROR_LOG`, `CHANGE_REQUEST`) supporting multi-warehouse inventory tracking and full audit trails.
- Authored the comprehensive Oracle PL/SQL package `ERP_OPERATIONS`, implementing atomic BOM explosion, automated raw material reservation, and multi-table reconciliation within strict ACID transactions.
- Implemented an autonomous error-logging framework using `PRAGMA AUTONOMOUS_TRANSACTION` to ensure fault diagnostic records persist even during transaction rollbacks.
- Built high-performance RESTful APIs using ASP.NET Core (.NET 8) and Dapper, executing database stored procedures directly with parameterized queries to eliminate SQL injection and maximize throughput.
- Established an ERP Support Runbook and Incident Resolution workflow; simulated production incidents (e.g., material shortage blocking production orders), performing root-cause analysis (RCA), data fixing, and Change Request (CR) documentation.
```

---

## 2. BỘ CÂU HỎI & TRẢ LỜI PHỎNG VẤN KỸ THUẬT (ORACLE / ERP / C#)

### Question 1: How do you prevent overselling or race conditions when multiple production orders request raw materials simultaneously in Oracle?
- **Answer:** *"In the PL/SQL package, I implement pessimistic locking using `SELECT ... FOR UPDATE` on both the `PRODUCTION_ORDER` and `STOCK` records. This ensures that when an operator initiates the `complete_production_order` procedure, concurrent transactions trying to access the same material inventory must wait until the lock is released. Additionally, the inventory check, material deduction, finished goods stock-in, and status update are bound inside a single transaction with explicit commit/rollback controls."*

### Question 2: Why did you use `PRAGMA AUTONOMOUS_TRANSACTION` for error logging?
- **Answer:** *"In Oracle, when a business validation fails (such as insufficient raw materials), the main transaction executes a `ROLLBACK` to prevent partial data writes. If our error-logging logic were part of the main transaction, the error record itself would also be rolled back, leaving IT support blind to what happened. By declaring `PRAGMA AUTONOMOUS_TRANSACTION` inside the `log_error` procedure, the logging operation runs in its own separate transaction context and commits independently without affecting the rollback of the business transaction."*

### Question 3: How does your system handle Bill of Materials (BOM) changes over time?
- **Answer:** *"The `BOM` table maintains a natural composite key on `(FG_ITEM_ID, VERSION)` and includes a `STATUS` column (`ACTIVE`/`INACTIVE`). When engineering updates product specifications (e.g., changing from Version 1.0 to Version 2.0), the previous version is set to `INACTIVE` while the new version is activated. When a production order is created or processed, it queries only the active BOM version for that finished good, preserving historical production fidelity for orders executed under older versions."*

### Question 4: How do you connect ASP.NET Core to Oracle and why choose Dapper over EF Core?
- **Answer:** *"I use `Oracle.ManagedDataAccess.Core` combined with Dapper. In enterprise ERP manufacturing environments, critical business rules and complex batch calculations reside in database Stored Procedures and Packages to maximize data proximity. Dapper provides ultra-lightweight object mapping with zero abstraction overhead, allowing direct execution of `CommandType.StoredProcedure` with strongly typed parameters, while EF Core adds unnecessary overhead when working with legacy or procedure-heavy Oracle schemas."*

### Question 5: When a user reports that a production order cannot complete, what are your exact diagnostic steps?
- **Answer:** *"First, I check the `ERROR_LOG` table filtering by the order's reference number (`REF_NO`) to identify the exact error code, error message, and timestamp. Second, if it indicates a material deficit, I run an inventory vs. BOM reconciliation query to pinpoint which specific component is below the required threshold. Third, I verify with warehouse and procurement to see if pending purchase shipments exist. Fourth, I log a formal `CHANGE_REQUEST` ticket to document the root cause and planned resolution. Once the inventory is replenished via standard receiving procedures, the order is safely re-executed and the ticket is closed."*
