# Mini ERP — REST API Specification

Hệ thống cung cấp giao diện RESTful API chuẩn xây dựng trên nền tảng **ASP.NET Core (.NET 8)** kết nối trực tiếp cơ sở dữ liệu **Oracle Database** qua Stored Procedures để đảm bảo hiệu năng và toàn vẹn dữ liệu.

---

## 1. PHÂN HỆ QUẢN LÝ KHO (WAREHOUSE & STOCK)

### GET `/api/warehouse`
- **Mô tả:** Lấy danh sách các kho đang hoạt động trong nhà máy.
- **Phản hồi mẫu:**
  ```json
  [
    {
      "id": 1,
      "code": "WH_RAW",
      "name": "Kho Nguyên Phụ Liệu - KCN Biên Hòa 2",
      "location": "Khu Công Nghiệp Biên Hòa 2, Đồng Nai",
      "isActive": true
    },
    {
      "id": 2,
      "code": "WH_FG",
      "name": "Kho Thành Phẩm Xuất Khẩu",
      "location": "Cụm Kho Logistics Long Thành, Đồng Nai",
      "isActive": true
    }
  ]
  ```

### GET `/api/stock/{warehouseCode}`
- **Mô tả:** Tra cứu danh mục tồn kho thực tế của từng vật tư tại kho cụ thể.
- **Phản hồi mẫu:**
  ```json
  [
    {
      "warehouseCode": "WH_RAW",
      "itemCode": "MAT_RUBBER_01",
      "itemName": "Đế cao su lưu hóa đúc sẵn (Size 42)",
      "itemType": "RAW",
      "uom": "PAIR",
      "quantity": 140,
      "minStock": 500,
      "isBelowMinStock": true
    }
  ]
  ```

### POST `/api/stock/in`
- **Mô tả:** Nhập kho vật tư hoặc thành phẩm. Gọi procedure `ERP_OPERATIONS.create_stock_in`.
- **Yêu cầu (Request Body):**
  ```json
  {
    "warehouseCode": "WH_RAW",
    "itemCode": "MAT_RUBBER_01",
    "quantity": 100,
    "referenceNo": "PO_PUR_901",
    "user": "warehouse01"
  }
  ```
- **Phản hồi:** `200 OK`
  ```json
  {
    "success": true,
    "message": "Stock-in completed successfully.",
    "referenceNo": "PO_PUR_901"
  }
  ```

---

## 2. PHÂN HỆ SẢN XUẤT & ĐỊNH MỨC (MANUFACTURING & BOM)

### POST `/api/manufacturing/bom/line`
- **Mô tả:** Cập nhật hoặc thêm mới định mức BOM cho sản phẩm. Gọi `ERP_OPERATIONS.save_bom_line`.
- **Yêu cầu (Request Body):**
  ```json
  {
    "finishedGoodCode": "FG_RUNNER_PRO_42",
    "version": "V1.0",
    "materialCode": "MAT_RUBBER_01",
    "quantityRequired": 1.0,
    "user": "planner01"
  }
  ```

### POST `/api/manufacturing/production-order`
- **Mô tả:** Tạo lệnh sản xuất mới tại xưởng. Gọi `ERP_OPERATIONS.create_production_order`.
- **Yêu cầu:**
  ```json
  {
    "productionOrderNo": "PO001",
    "finishedGoodCode": "FG_RUNNER_PRO_42",
    "plannedQuantity": 50,
    "warehouseCode": "WH_RAW",
    "user": "planner01"
  }
  ```

### POST `/api/manufacturing/production-order/{poNo}/complete`
- **Mô tả:** Nghiệm thu và hoàn thành lệnh sản xuất. Hệ thống thực hiện nổ BOM, kiểm tra tồn kho nguyên vật liệu, trừ kho NVL và nhập kho Thành phẩm trong một Transaction nguyên tử duy nhất.
- **Phản hồi thành công (`200 OK`):**
  ```json
  {
    "success": true,
    "poNo": "PO001",
    "status": "COMPLETED",
    "message": "Production order PO001 completed successfully. Finished goods transferred to stock."
  }
  ```
- **Phản hồi lỗi thiếu nguyên vật liệu (`400 Bad Request` - Mã ORA-20007):**
  ```json
  {
    "success": false,
    "errorCode": "ERR_MATERIAL_SHORTAGE",
    "poNo": "PO001",
    "message": "Cannot complete PO PO001 due to material shortage: Material MAT_RUBBER_01 requires 50, available 40;",
    "action": "Please consult ERP Support Runbook and submit Change Request."
  }
  ```

---

## 3. PHÂN HỆ VẬN HÀNH & HỖ TRỢ SỰ CỐ (ERP SUPPORT)

### GET `/api/support/errors?refNo=PO001`
- **Mô tả:** Truy vấn danh sách lỗi nghiệp vụ đã ghi nhận trong bảng `ERROR_LOG`.
- **Phản hồi:**
  ```json
  [
    {
      "id": 104,
      "errorCode": "ERR_MATERIAL_SHORTAGE",
      "message": "Stock-out failed. Material MAT_RUBBER_01 requires 50, available 40",
      "procedureName": "complete_production_order",
      "refNo": "PO001",
      "createdAt": "2026-09-23T08:15:30Z"
    }
  ]
  ```

### POST `/api/support/change-requests`
- **Mô tả:** Tạo phiếu yêu cầu điều chỉnh dữ liệu khi phát sinh sự cố tại xưởng.
- **Yêu cầu:**
  ```json
  {
    "changeRequestNo": "CR-2026-0901",
    "title": "Lỗi lệnh PO001 thiếu đế cao su MAT_RUBBER_01",
    "requestType": "INCIDENT",
    "refNo": "PO001",
    "rootCause": "Tồn kho thực tế còn 40 cặp, PO cần 50 cặp do lô hàng nhập chưa kịp vào sổ.",
    "fixAction": "Nhập kho bù đơn mua hàng PO_PUR_901.",
    "requester": "tan.mai"
  }
  ```
