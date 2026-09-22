# Mini ERP — Database Schema Specification

Hệ thống quản lý dữ liệu sử dụng cơ sở dữ liệu **Oracle Database** với mô hình quan hệ chuẩn hóa (3NF) tối ưu cho hoạt động giao dịch nghiệp vụ (OLTP) sản xuất và kho bãi.

---

## 1. SƠ ĐỒ THỰC THỂ QUAN HỆ (ERD)

```text
+-------------------+            +---------------------+            +--------------------+
|     WAREHOUSE     |            |        STOCK        |            |        ITEM        |
+-------------------+            +---------------------+            +--------------------+
| ID (PK)           |<-----+     | WAREHOUSE_ID (PK/FK)|     +----->| ID (PK)            |
| CODE (UQ)         |      +-----| ITEM_ID (PK/FK)     |-----+      | CODE (UQ)          |
| NAME              |            | QTY                 |            | NAME               |
| LOCATION          |            | UPDATED_AT          |            | ITEM_TYPE (RAW/FG) |
| IS_ACTIVE         |            +---------------------+            | UOM                |
+-------------------+                                               | MIN_STOCK          |
         ^                                                          +--------------------+
         |                                                                    ^
         |  +---------------------------+                                     |
         |  |   INVENTORY_TRANSACTION   |                                     |
         +--| WAREHOUSE_ID (FK)         |                                     |
            | ITEM_ID (FK)              |-------------------------------------+
            | TXN_TYPE                  |                                     |
            | QTY                       |                                     |
            | BALANCE_AFTER             |                                     |
            | REF_NO                    |                                     |
            +---------------------------+                                     |
                                                                              |
         +------------------------------+                                     |
         |       PRODUCTION_ORDER       |                                     |
         +------------------------------+                                     |
         | ID (PK)                      |                                     |
         | PO_NO (UQ)                   |                                     |
         | FG_ITEM_ID (FK)              |-------------------------------------+
         | QTY_PLANNED                  |                                     |
         | QTY_DONE                     |                                     |
         | STATUS                       |                                     |
         | WAREHOUSE_ID (FK)            |                                     |
         +------------------------------+                                     |
                                                                              |
         +------------------------------+                                     |
         |             BOM              |                                     |
         +------------------------------+                                     |
         | ID (PK)                      |                                     |
         | FG_ITEM_ID (FK)              |-------------------------------------+
         | VERSION                      |                                     |
         | STATUS (ACTIVE/INACTIVE)     |                                     |
         +------------------------------+                                     |
                        ^                                                     |
                        |                                                     |
         +------------------------------+                                     |
         |          BOM_DETAIL          |                                     |
         +------------------------------+                                     |
         | BOM_ID (PK/FK)               |                                     |
         | MAT_ITEM_ID (PK/FK)          |-------------------------------------+
         | QTY_REQUIRED                 |
         +------------------------------+
```

---

## 2. BẢNG TỪ ĐIỂN DỮ LIỆU (DATA DICTIONARY)

### A. Bảng WAREHOUSE
| Cột | Kiểu dữ liệu | Ràng buộc | Mô tả |
|---|---|---|---|
| `ID` | NUMBER | GENERATED IDENTITY, PK | Khóa chính tăng tự động |
| `CODE` | VARCHAR2(20) | NOT NULL, UNIQUE | Mã kho (`WH_RAW`, `WH_FG`, `WH_WIP`) |
| `NAME` | VARCHAR2(100) | NOT NULL | Tên kho đầy đủ |
| `LOCATION` | VARCHAR2(200) | NULL | Địa chỉ vị trí kho trong nhà máy |
| `IS_ACTIVE` | NUMBER(1) | DEFAULT 1, CHECK (0, 1) | Trạng thái hoạt động |

### B. Bảng ITEM
| Cột | Kiểu dữ liệu | Ràng buộc | Mô tả |
|---|---|---|---|
| `ID` | NUMBER | GENERATED IDENTITY, PK | Khóa chính tăng tự động |
| `CODE` | VARCHAR2(30) | NOT NULL, UNIQUE | Mã vật tư (`MAT_RUBBER_01`, `FG_RUNNER_PRO_42`) |
| `NAME` | VARCHAR2(150) | NOT NULL | Tên chi tiết vật tư / thành phẩm |
| `ITEM_TYPE` | VARCHAR2(10) | NOT NULL, CHECK ('RAW', 'FG') | Phân loại: Nguyên phụ liệu hoặc Thành phẩm |
| `UOM` | VARCHAR2(10) | DEFAULT 'PCS' | Đơn vị tính (METER, PAIR, KG, SPOOL, PCS) |
| `MIN_STOCK` | NUMBER | DEFAULT 0, CHECK (>= 0) | Mức tồn kho tối thiểu an toàn cảnh báo đặt hàng |

### C. Bảng STOCK
| Cột | Kiểu dữ liệu | Ràng buộc | Mô tả |
|---|---|---|---|
| `WAREHOUSE_ID` | NUMBER | PK, FK -> WAREHOUSE(ID) | Mã định danh kho |
| `ITEM_ID` | NUMBER | PK, FK -> ITEM(ID) | Mã định danh vật tư |
| `QTY` | NUMBER | DEFAULT 0, CHECK (>= 0) | Số lượng tồn kho thực tế khả dụng |
| `UPDATED_AT` | DATE | DEFAULT SYSDATE | Thời điểm cập nhật biến động gần nhất |

### D. Bảng BOM & BOM_DETAIL
- **BOM**: Quản lý phiên bản định mức (Version) của từng Thành phẩm. Khóa tự nhiên duy nhất `(FG_ITEM_ID, VERSION)`.
- **BOM_DETAIL**: Bảng chi tiết định mức tiêu hao nguyên vật liệu cấu thành 1 đơn vị thành phẩm.
  - Ví dụ: 1 Đôi giày `FG_RUNNER_PRO_42` cần 1 Đế `MAT_RUBBER_01` + 0.5m Vải `MAT_MESH_01` + 0.2kg Keo `MAT_GLUE_01`.

### E. Bảng PRODUCTION_ORDER
| Cột | Kiểu dữ liệu | Ràng buộc | Mô tả |
|---|---|---|---|
| `ID` | NUMBER | IDENTITY, PK | Khóa chính |
| `PO_NO` | VARCHAR2(30) | NOT NULL, UNIQUE | Mã lệnh sản xuất (`PO001`, `PO002`) |
| `FG_ITEM_ID` | NUMBER | FK -> ITEM(ID) | Thành phẩm cần sản xuất |
| `QTY_PLANNED` | NUMBER | CHECK (> 0) | Số lượng cần hoàn thành |
| `QTY_DONE` | NUMBER | DEFAULT 0 | Số lượng đã sản xuất thực tế |
| `STATUS` | VARCHAR2(15) | CHECK ('CREATED', 'RELEASED', 'COMPLETED', 'CANCELLED') | Vòng đời lệnh sản xuất |
| `WAREHOUSE_ID` | NUMBER | FK -> WAREHOUSE(ID) | Kho xuất nguyên vật liệu và nhập thành phẩm |

### F. Bảng INVENTORY_TRANSACTION
Bảng Audit Trail ghi nhận toàn bộ biến động xuất/nhập:
- `TXN_TYPE`: `STOCK_IN`, `STOCK_OUT`, `MFG_CONSUME`, `MFG_OUTPUT`, `ADJUSTMENT`.
- `BALANCE_AFTER`: Số dư tồn kho tức thời ngay sau khi giao dịch thực hiện xong (đảm bảo tính kiểm toán độc lập).

### G. Bảng ERROR_LOG & CHANGE_REQUEST
- **ERROR_LOG**: Được ghi nhận tự động bằng cơ chế `PRAGMA AUTONOMOUS_TRANSACTION` trong PL/SQL. Dù giao dịch sản xuất bị ROLLBACK do lỗi, bản ghi lỗi vẫn được lưu trữ vĩnh viễn phục vụ điều tra sự cố.
- **CHANGE_REQUEST**: Quy trình phê duyệt điều chỉnh dữ liệu khi có sự cố phát sinh tại xưởng (`INCIDENT`, `DATA_FIX`, `CONFIG`).
