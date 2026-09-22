# 04-MiniERP-Manufacturing-Warehouse

[![Tech](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Database](https://img.shields.io/badge/Database-Oracle%2019c%2F21c-red.svg)](https://www.oracle.com/database/)
[![Language](https://img.shields.io/badge/Language-C%23%20%7C%20PL%2FSQL-blue.svg)]()
[![Target](https://img.shields.io/badge/Target-TKG%20Taekwang%20ERP-orange.svg)]()

Hệ thống Mini ERP chuyên sâu mô phỏng 2 phân hệ cốt lõi: **Quản lý Kho (Warehouse Management)** và **Điều hành Sản xuất (Manufacturing Execution)**, được xây dựng theo chuẩn yêu cầu tuyển dụng kỹ sư **IT ERP** tại tập đoàn sản xuất TKG Taekwang.

---

## 1. KIẾN TRÚC HỆ THỐNG (SYSTEM ARCHITECTURE)

```text
               +-------------------------------------------+
               |     Client / Web UI / Mobile Barcode      |
               +-------------------------------------------+
                                     │ (HTTPS / JSON)
                                     ▼
               +-------------------------------------------+
               |     ASP.NET Core Web API (.NET 8.0)       |
               |  - Swagger / OpenAPI Documentation        |
               |  - Dapper Micro-ORM Object Mapping        |
               |  - Strict DTO Validation                  |
               +-------------------------------------------+
                                     │ (Oracle Managed Data Access)
                                     ▼
               +-------------------------------------------+
               |    Oracle Database 19c / 21c (Docker)     |
               |                                           |
               |  [Package ERP_OPERATIONS]                 |
               |  ├── create_stock_in / create_stock_out   |
               |  ├── save_bom_line (Bill of Materials)    |
               |  ├── create_production_order              |
               |  ├── complete_production_order (Atomic)   |
               |  └── log_error (Autonomous Transaction)   |
               |                                           |
               |  [11 Relational Tables]                   |
               |  WAREHOUSE, ITEM, STOCK, BOM, BOM_DETAIL, |
               |  PRODUCTION_ORDER, PURCHASE_ORDER,        |
               |  INVENTORY_TRANSACTION, APP_USER,         |
               |  ERROR_LOG, CHANGE_REQUEST                |
               +-------------------------------------------+
```

---

## 2. NGHIỆP VỤ CỐT LÕI (CORE DOMAIN WORKFLOWS)

### A. Phân hệ Quản lý Kho (Warehouse)
- **Quản lý đa kho:** Kho Nguyên phụ liệu (`WH_RAW`), Kho Thành phẩm xuất khẩu (`WH_FG`), Kho Bán thành phẩm (`WH_WIP`).
- **Xuất/Nhập/Tồn:** Thực hiện kiểm tra tồn kho bằng kỹ thuật Pessimistic Locking (`SELECT FOR UPDATE`), cập nhật số dư tức thời.
- **Audit Trail bất biến:** Mọi thao tác đều sinh bản ghi `INVENTORY_TRANSACTION` với số dư sau giao dịch (`BALANCE_AFTER`).

### B. Phân hệ Sản xuất & Định mức (Manufacturing & BOM)
- **Định mức kỹ thuật (BOM):** 1 Đôi giày thể thao `FG_RUNNER_PRO_42` tiêu hao:
  - 1 Đế cao su đúc sẵn (`MAT_RUBBER_01`)
  - 0.5m Vải lưới (`MAT_MESH_01`)
  - 0.1 Cuộn chỉ may (`MAT_THREAD_01`)
  - 0.2kg Keo dán PU (`MAT_GLUE_01`)
  - 1 Hộp carton (`MAT_BOX_01`)
- **Giao dịch Hoàn thành Sản xuất (Complete PO):**
  1. Kiểm tra tồn kho toàn bộ nguyên vật liệu trước khi trừ (Pre-flight check).
  2. Nếu thiếu dù chỉ 1 vật tư: Ghi lỗi vào `ERROR_LOG` và ném `ORA-20007` (Không để xảy ra tình trạng thiếu hàng dở dang).
  3. Nếu đủ vật tư: Tự động trừ kho NVL (`MFG_CONSUME`) và nhập kho Thành phẩm (`MFG_OUTPUT`) trong một Transaction ACID duy nhất.

---

## 3. KỊCH BẢN VẬN HÀNH ERP SUPPORT (INCIDENT RUNBOOK)

Hệ thống cung cấp sẵn kịch bản mô phỏng sự cố thực tế nhà máy tại `sql/04_incident_scenarios.sql`:

1. **Sự cố:** Lệnh sản xuất `PO001` (50 đôi giày) báo lỗi thất bại khi hoàn thành vì kho chỉ còn 40 cặp đế giày `MAT_RUBBER_01`.
2. **Điều tra:** Kỹ sư ERP Support tra cứu bảng `ERROR_LOG` độc lập (`PRAGMA AUTONOMOUS_TRANSACTION`), chạy query đối chiếu BOM vs STOCK.
3. **Quy trình:** Lập phiếu `CHANGE_REQUEST` số `CR-2026-0901` ghi nhận nguyên nhân gốc rễ (RCA) và giải pháp.
4. **Xử lý:** Nhập bổ sung 100 cặp đế giày từ đơn mua hàng `PO_PUR_901`.
5. **Nghiệm thu:** Chạy lại `complete_production_order`, lệnh hoàn thành `STATUS = COMPLETED`, tồn kho thành phẩm tăng 50.

---

## 4. HƯỚNG DẪN KHỞI CHẠY (QUICK START)

### Bước 1: Khởi chạy Oracle Database Container
```bash
docker-compose up -d
```

### Bước 2: Chạy DDL Schema, PL/SQL Package và Seed Data
```bash
# Kết nối SQL*Plus hoặc DBeaver và chạy theo thứ tự:
sql/01_schema.sql
sql/02_plsql.sql
sql/03_seed.sql
```

### Bước 3: Chạy ứng dụng Backend ASP.NET Core
```bash
cd src
dotnet run
```
Truy cập giao diện tương tác Swagger UI tại: `http://localhost:5000`

---

## 5. CẤU TRÚC THƯ MỤC DỰ ÁN

```text
04-MiniERP-Manufacturing-Warehouse/
├── README.md                              # Tài liệu tổng quan
├── docker-compose.yml                     # Cấu hình container Oracle Database Free
├── docs/
│   ├── 01-phases.md                       # Lộ trình và tiêu chí hoàn thành từng phase
│   ├── 02-database-schema.md              # Từ điển dữ liệu và mô tả 11 bảng
│   ├── 03-api-spec.md                     # Đặc tả chi tiết REST API
│   ├── 04-plsql-spec.md                   # Đặc tả kỹ thuật Oracle Package & Procedures
│   ├── 05-erp-support-runbook.md          # Sổ tay xử lý sự cố cho kỹ sư ERP
│   └── 06-interview-and-cv-en.md          # Hướng dẫn trả lời phỏng vấn & CV tiếng Anh
├── sql/
│   ├── 01_schema.sql                      # DDL tạo bảng, khóa ngoại, chỉ mục
│   ├── 02_plsql.sql                       # Package ERP_OPERATIONS xử lý kho & sản xuất
│   ├── 03_seed.sql                        # Dữ liệu mẫu xưởng may/giày da thực tế
│   └── 04_incident_scenarios.sql          # Kịch bản mô phỏng sự cố PO001 và data fix
└── src/
    ├── MiniERP.Api.csproj
    ├── appsettings.json
    ├── Program.cs
    ├── Models/ErpModels.cs
    └── Services/ErpDbService.cs
```
