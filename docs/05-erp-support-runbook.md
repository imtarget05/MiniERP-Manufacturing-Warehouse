# Mini ERP — IT ERP Support & Operations Runbook

Tài liệu này là cẩm nang vận hành dành cho **Kỹ sư Hỗ trợ ERP (IT ERP Support)** tại các nhà máy sản xuất (áp dụng theo tiêu chuẩn quản lý ERP của TKG Taekwang).

---

## 1. QUY TRÌNH TIẾP NHẬN & PHÂN LOẠI SỰ CỐ (SOP)

```text
[Người dùng báo lỗi qua Ticket / Điện thoại]
                  │
                  ▼
          [Xác định phân hệ]
         /                  \
        ▼                    ▼
[Phân hệ Kho]         [Phân hệ Sản Xuất]
(Xuất/Nhập/Tồn)       (BOM/Lệnh SX/Thiêu vật tư)
        \                    /
         ▼                  ▼
[Bước 1: Tra cứu mã tham chiếu trong ERROR_LOG]
                  │
                  ▼
[Bước 2: Phân tích nguyên nhân gốc rễ (Root Cause Analysis)]
                  │
                  ▼
[Bước 3: Lập Change Request (CR) nếu cần chỉnh sửa dữ liệu]
                  │
                  ▼
[Bước 4: Thực thi Fix trên môi trường Staging -> Production]
                  │
                  ▼
[Bước 5: Xác nhận với người dùng cuối & Đóng Ticket]
```

---

## 2. KỊCH BẢN ĐIỀU TRA SỰ CỐ KINH ĐIỂN: LỆNH SẢN XUẤT PO001 KHÔNG THỂ COMPLETE

### Mô tả hiện tượng
- **Người báo:** Nguyễn Văn A - Tổ trưởng Chuyền may 1.
- **Hiện tượng:** Khi bấm nút "Hoàn thành Lệnh sản xuất PO001" trên hệ thống ERP, màn hình báo lỗi popup: `ORA-20007: Cannot complete PO due to material shortage`.

### Bước 1: Tra cứu Error Log
Chạy query sau trên công cụ PL/SQL Developer / Oracle SQL Developer:

```sql
SELECT ID, ERR_CODE, MESSAGE, PROC_NAME, REF_NO, CREATED_AT
FROM ERROR_LOG
WHERE REF_NO = 'PO001'
ORDER BY ID DESC;
```
*Kết quả:* Phát hiện thông điệp lỗi: `Material MAT_RUBBER_01 requires 50, available 40;`

### Bước 2: Chạy Query Đối Chiếu Nhu Cầu Định Mức & Tồn Kho Thực Tế
```sql
SELECT 
  it.CODE AS MAT_CODE,
  it.NAME AS MAT_NAME,
  bd.QTY_REQUIRED * po.QTY_PLANNED AS REQUIRED_QTY,
  NVL(st.QTY, 0) AS CURRENT_STOCK,
  CASE 
    WHEN NVL(st.QTY, 0) < (bd.QTY_REQUIRED * po.QTY_PLANNED) THEN 'DEFICIT / SHORTAGE'
    ELSE 'SUFFICIENT'
  END AS INVENTORY_STATUS
FROM PRODUCTION_ORDER po
JOIN BOM bm ON po.FG_ITEM_ID = bm.FG_ITEM_ID AND bm.STATUS = 'ACTIVE'
JOIN BOM_DETAIL bd ON bm.ID = bd.BOM_ID
JOIN ITEM it ON bd.MAT_ITEM_ID = it.ID
LEFT JOIN STOCK st ON st.WAREHOUSE_ID = po.WAREHOUSE_ID AND st.ITEM_ID = it.ID
WHERE po.PO_NO = 'PO001';
```

### Bước 3: Phân tích Nguyên nhân Gốc rễ (Root Cause)
1. Lệnh PO001 sản xuất 50 đôi giày `FG_RUNNER_PRO_42`. Theo định mức BOM V1.0, mỗi đôi cần 1 cặp đế cao su `MAT_RUBBER_01` -> Cần 50 cặp.
2. Tồn kho thực tế trong bảng `STOCK` tại kho `WH_RAW` chỉ có 40 cặp -> Thiếu 10 cặp.
3. Kiểm tra với phòng Mua hàng: Nhà cung ứng đã giao lô hàng 100 cặp đế giày từ sáng sớm (`PO_PUR_901`), nhưng thủ kho chưa nhập dữ liệu phiếu nhận hàng vào hệ thống ERP.

### Bước 4: Tạo Phiếu Yêu Cầu Thay Đổi (Change Request)
Để tuân thủ kiểm soát nội bộ SOX/ISO trong nhà máy, không được tự ý sửa database bằng lệnh `UPDATE STOCK` tùy tiện:

```sql
INSERT INTO CHANGE_REQUEST (
  CR_NO, TITLE, REQ_TYPE, REF_NO, ROOT_CAUSE, FIX_ACTION, STATUS, REQUESTER
) VALUES (
  'CR-2026-0901',
  'Xử lý lỗi PO001 thiếu đế cao su MAT_RUBBER_01',
  'INCIDENT',
  'PO001',
  'Tồn kho khả dụng 40 cặp, thiếu 10 cặp do lô hàng giao sáng nay chưa được nhập kho.',
  'Nhập kho phiếu PO_PUR_901 (100 cặp) qua procedure chuẩn của ERP, sau đó chạy lại lệnh PO001.',
  'IN_PROGRESS',
  'tan.mai'
);
COMMIT;
```

### Bước 5: Thực thi Giải Pháp & Kiểm Tra Kết Quả
1. Thực hiện nhập kho đúng quy trình:
   ```sql
   EXEC ERP_OPERATIONS.receive_purchase_order('PO_PUR_901', 'warehouse01');
   ```
2. Tổ trưởng chạy lại thao tác hoàn thành:
   ```sql
   EXEC ERP_OPERATIONS.complete_production_order('PO001', 'workshop_lead');
   ```
3. Kiểm tra trạng thái lệnh đã chuyển sang `COMPLETED`, số lượng `QTY_DONE = 50`.
4. Đóng Change Request:
   ```sql
   UPDATE CHANGE_REQUEST SET STATUS = 'RESOLVED', CLOSED_AT = SYSDATE WHERE CR_NO = 'CR-2026-0901';
   COMMIT;
   ```

---

## 3. CHECKLIST KIỂM SOÁT HÀNG NGÀY CHO IT ERP
1. **Kiểm tra bàn giao ca:** Rà soát bảng `ERROR_LOG` trong 24 giờ qua xem có xu hướng tăng đột biến ở phân hệ nào không.
2. **Cảnh báo vật tư dưới Min Stock:**
   ```sql
   SELECT w.CODE AS WH, i.CODE AS ITEM, i.NAME, s.QTY, i.MIN_STOCK
   FROM STOCK s
   JOIN ITEM i ON s.ITEM_ID = i.ID
   JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
   WHERE s.QTY < i.MIN_STOCK;
   ```
3. **Kiểm tra các lệnh sản xuất quá hạn:**
   ```sql
   SELECT PO_NO, FG_ITEM_ID, QTY_PLANNED, CREATED_AT
   FROM PRODUCTION_ORDER
   WHERE STATUS = 'RELEASED' AND CREATED_AT < SYSDATE - 3;
   ```
