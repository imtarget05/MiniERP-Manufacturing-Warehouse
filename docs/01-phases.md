# Mini ERP — Project Implementation Phases

Dự án **04-MiniERP-Manufacturing-Warehouse** được thiết kế theo chuẩn module hóa phục vụ môi trường nhà máy sản xuất (Manufacturing Execution & Warehouse Management).

---

## Lộ Trình Triển Khai Kỹ Thuật

```text
Phase 0: Database Infrastructure & Schema Setup
  ├── Oracle Database Free / 21c Container (docker-compose.yml)
  └── DDL Scripts (sql/01_schema.sql): 11 Tables, Constraints, Indexes

Phase 1: Business Logic Layer (Oracle PL/SQL)
  ├── Package Specification & Body (sql/02_plsql.sql)
  ├── Autonomous Transaction Error Logging (ERROR_LOG)
  ├── BOM Explosion & Pre-flight Inventory Validation
  └── Material Consumption & FG Generation within single Atomic Transaction

Phase 2: Master Data & Seed Environment
  ├── Real-world Footwear & Apparel Dataset (sql/03_seed.sql)
  └── Multi-role Users (Planner, Warehouseman, ERP Support Specialist)

Phase 3: Backend API Integration (.NET 8 / C#)
  ├── ASP.NET Core Web API with Dapper & Oracle.ManagedDataAccess.Core
  ├── Repository Pattern calling Stored Procedures directly
  └── Structured Error Handling & Response DTOs

Phase 4: ERP Operations & Incident Runbook
  ├── Simulation of Material Shortage (sql/04_incident_scenarios.sql)
  ├── Root Cause Investigation Protocol (docs/05-erp-support-runbook.md)
  └── Change Request & Data Fix Workflow (CHANGE_REQUEST)

Phase 5: Interview & Portfolio Packaging
  ├── 20-section portfolio README with Mermaid diagrams (README.md)
  └── Scripted recruiter walkthrough + technical defense notes (docs/demo/recruiter-demo.md)
```

---

## Tiêu Chí Nghiệm Thu Từng Phase (DoD)

| Phase | Kết Quả Đầu Ra | Tiêu Chí Kiểm Tra (Verification) |
|---|---|---|
| **Phase 0** | Oracle Tables | Chạy `sql/01_schema.sql` không có lỗi ORA-, đủ 11 bảng và khóa ngoại. |
| **Phase 1** | PL/SQL Package | Biên dịch Package `ERP_OPERATIONS` hợp lệ (`STATUS = 'VALID'`). |
| **Phase 2** | Seed Data | Dữ liệu `WH_RAW`, `WH_FG`, vật tư và BOM của `FG_RUNNER_PRO_42` sẵn sàng. |
| **Phase 3** | C# Web API | Swagger UI hiển thị các endpoints CRUD và gọi Procedure thành công. |
| **Phase 4** | Incident Demo | Tái hiện lỗi thiếu NVL PO001 -> Log error -> Sửa dữ liệu -> Hoàn thành. |
| **Phase 5** | Phỏng vấn | Trả lời tự tin 100% câu hỏi về Oracle, PL/SQL, BOM và xử lý incident. |
