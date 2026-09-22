# Mini ERP — Oracle PL/SQL Technical Specification

Hệ thống Mini ERP sử dụng kỹ thuật lưu trữ logic nghiệp vụ trực tiếp trong cơ sở dữ liệu qua **Oracle Package** (`ERP_OPERATIONS`), tận dụng khả năng xử lý giao dịch nguyên tử (ACID), con trỏ (Cursor), câu lệnh MERGE và cơ chế `PRAGMA AUTONOMOUS_TRANSACTION`.

---

## 1. TỔNG QUAN PACKAGE `ERP_OPERATIONS`

| Thành phần | Loại | Mục đích nghiệp vụ |
|---|---|---|
| `get_current_stock` | Function | Trả về số lượng khả dụng hiện thời của 1 vật tư tại 1 kho. |
| `log_error` | Procedure | Ghi log lỗi vào bảng `ERROR_LOG` mà không bị ảnh hưởng bởi ROLLBACK. |
| `create_stock_in` | Procedure | Tăng tồn kho, tính số dư tức thời, ghi nhật ký `INVENTORY_TRANSACTION`. |
| `create_stock_out` | Procedure | Khóa dòng (`FOR UPDATE`), kiểm tra tồn kho, trừ kho và ghi audit trail. |
| `save_bom_line` | Procedure | Cập nhật định mức tiêu hao nguyên phụ liệu cho thành phẩm. |
| `create_production_order` | Procedure | Khởi tạo lệnh sản xuất trạng thái `RELEASED`. |
| `complete_production_order` | Procedure | Nổ BOM, kiểm tra toàn bộ tồn kho NVL, trừ kho NVL, nhập kho FG trong 1 transaction. |
| `cancel_production_order` | Procedure | Hủy lệnh sản xuất khi chưa thực thi. |
| `receive_purchase_order` | Procedure | Nhận hàng từ nhà cung cấp, tăng tồn kho vật tư. |

---

## 2. CƠ CHẾ GHI LOG SỰ CỐ ĐỘC LẬP (PRAGMA AUTONOMOUS_TRANSACTION)

Trong vận hành ERP, khi một thao tác (ví dụ hoàn thành lệnh sản xuất) gặp lỗi và phát sinh ngoại lệ (`EXCEPTION`), lệnh `ROLLBACK` sẽ hủy toàn bộ các thao tác `INSERT/UPDATE` trước đó.

Nếu bảng log lỗi nằm trong transaction chính, bản ghi log cũng sẽ bị ROLLBACK mất dấu. Vì vậy, procedure `log_error` bắt buộc áp dụng `PRAGMA AUTONOMOUS_TRANSACTION`:

```sql
PROCEDURE log_error (
  p_err_code IN VARCHAR2,
  p_message  IN VARCHAR2,
  p_proc     IN VARCHAR2,
  p_ref      IN VARCHAR2,
  p_user     IN VARCHAR2 DEFAULT 'system'
) IS
  PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
  INSERT INTO ERROR_LOG (ERR_CODE, MESSAGE, PROC_NAME, REF_NO, CREATED_BY, CREATED_AT)
  VALUES (p_err_code, SUBSTR(p_message, 1, 1000), p_proc, p_ref, p_user, SYSDATE);
  COMMIT;
EXCEPTION
  WHEN OTHERS THEN
    ROLLBACK;
END log_error;
```

**Lợi ích vận hành:**
- Kỹ sư hỗ trợ ERP luôn có dấu vết lịch sử lỗi ngay cả khi ứng dụng bị crash.
- Dễ dàng thống kê tần suất lỗi theo từng phân hệ (`PROC_NAME`) và mã tham chiếu (`REF_NO`).

---

## 3. THIẾT KẾ GIAO DỊCH NỔ BOM TRONG `complete_production_order`

Thao tác hoàn thành lệnh sản xuất là cốt lõi của hệ thống ERP sản xuất:

```text
[Bắt đầu Transaction]
       │
       ▼
1. Khóa bản ghi PRODUCTION_ORDER (SELECT FOR UPDATE)
       │
       ▼
2. Lấy định mức BOM ACTIVE tương ứng với FG_ITEM_ID
       │
       ▼
3. Vòng lặp duyệt Cursor c_bom_lines:
   - Tính tổng NVL cần: QTY_REQUIRED * QTY_PLANNED
   - Kiểm tra số lượng tồn kho khả dụng trong STOCK
   - Nếu bất kỳ NVL nào thiếu:
       -> Ghi log_error('ERR_MATERIAL_SHORTAGE')
       -> RAISE_APPLICATION_ERROR(-20007) [Dừng ngay lập tức]
       │
       ▼ (Nếu tất cả NVL đều đủ)
4. Trừ kho từng NVL:
   - UPDATE STOCK (QTY = QTY - NEEDED)
   - INSERT INVENTORY_TRANSACTION (MFG_CONSUME, QTY âm, BALANCE_AFTER)
       │
       ▼
5. Tăng kho Thành phẩm:
   - MERGE INTO STOCK (QTY = QTY + PLANNED)
   - INSERT INVENTORY_TRANSACTION (MFG_OUTPUT, QTY dương, BALANCE_AFTER)
       │
       ▼
6. Cập nhật PRODUCTION_ORDER (STATUS = 'COMPLETED', COMPLETED_AT = SYSDATE)
       │
       ▼
[COMMIT Transaction]
```

---

## 4. BẢNG MÃ LỖI NGHIỆP VỤ (BUSINESS EXCEPTION CODES)

| Mã lỗi | Mô tả | Xử lý đề xuất |
|---|---|---|
| `-20001` | Số lượng nhập/xuất kho phải lớn hơn 0 | Kiểm tra lại payload đầu vào từ giao diện. |
| `-20002` | Mã kho hoặc mã vật tư không tồn tại | Kiểm tra danh mục `WAREHOUSE` và `ITEM`. |
| `-20003` | Xuất kho thất bại do tồn kho không đủ | Bổ sung phiếu nhập kho hoặc kiểm kê kho. |
| `-20004` | Lệnh sản xuất đã được hoàn thành trước đó | Không cho phép chạy lại lệnh đã đóng. |
| `-20005` | Lệnh sản xuất đã bị hủy | Kiểm tra lại trạng thái lệnh sản xuất. |
| `-20006` | Không tìm thấy định mức BOM đang hoạt động | Yêu cầu bộ phận Kỹ thuật phát hành BOM ACTIVE. |
| `-20007` | Không thể hoàn thành lệnh do thiếu nguyên phụ liệu | Xem chi tiết danh sách vật tư thiếu trong `ERROR_LOG`. |
| `-20008` | Lệnh sản xuất không tồn tại hoặc sai trạng thái hủy | Kiểm tra mã `PO_NO`. |
| `-20009` | Phiếu mua hàng không ở trạng thái CREATED để nhập kho | Kiểm tra trạng thái phiếu `PURCHASE_ORDER`. |
