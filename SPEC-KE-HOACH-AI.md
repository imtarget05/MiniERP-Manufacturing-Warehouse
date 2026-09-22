# KẾ HOẠCH & PROMPT DÀNH CHO AI: HOÀN THIỆN ĐỒ ÁN MINI ERP (C# + ORACLE PL/SQL)

> **Mục đích:** File này được thiết kế để bạn copy toàn bộ hoặc gửi trực tiếp cho một AI Coding Assistant (Cline, Cursor, Roo Code, Claude, Copilot, ChatGPT...) để AI tự động thực thi, kiểm thử và hoàn thiện toàn bộ mã nguồn của dự án **04-MiniERP-Manufacturing-Warehouse** mà không cần bạn phải can thiệp thủ công.
> **Vị trí dự án trên máy:** `~/Downloads/PORTFOLIO/04-MiniERP-Manufacturing-Warehouse/`

---

## PHẦN 1: TỔNG QUAN YÊU CẦU & BỐI CẢNH DỰ ÁN

- **Mục tiêu:** Xây dựng hoàn chỉnh hệ thống ERP mini 2 phân hệ **Quản lý Kho (WMS)** và **Điều hành Sản xuất (MES)** phục vụ ứng tuyển vị trí **IT ERP (C# + Oracle + PL/SQL)** tại TKG Taekwang.
- **Kiến trúc:**
  - Cơ sở dữ liệu: Oracle Database 19c/21c (chạy qua Docker container).
  - Business Logic: Lưu trữ trực tiếp trong Package Oracle PL/SQL `ERP_OPERATIONS` (gồm Stored Procedures nổ BOM, trừ kho NVL, cộng kho thành phẩm trong 1 transaction, cơ chế ghi lỗi `PRAGMA AUTONOMOUS_TRANSACTION`).
  - Backend API: ASP.NET Core Web API (.NET 8) sử dụng Dapper kết nối Oracle.
  - Test & Demo: Kịch bản mô phỏng sự cố PO001 thiếu nguyên vật liệu và quy trình điều tra, sửa lỗi dữ liệu.

---

## PHẦN 2: CÁC FILE ĐÃ CÓ SẴN TRONG REPOSITORY
AI không cần viết lại từ đầu mà sẽ dựa trên các file chuẩn đã dựng sẵn:
- `sql/01_schema.sql`: 11 bảng quan hệ, khóa ngoại, chỉ mục.
- `sql/02_plsql.sql`: Package specification và body `ERP_OPERATIONS`.
- `sql/03_seed.sql`: Dữ liệu mẫu nhà máy may mặc/giày da.
- `sql/04_incident_scenarios.sql`: Kịch bản sự cố PO001 fail -> fix -> success.
- `docker-compose.yml`: Cấu hình container Oracle Database Free.
- `src/`: Dự án ASP.NET Core (`MiniERP.Api.csproj`, `Program.cs`, `Models/`, `Services/`).
- `docs/`: 6 file đặc tả chi tiết (schema, API spec, PL/SQL spec, runbook, CV).

---

## PHẦN 3: LỘ TRÌNH THỰC THI CHI TIẾT (DÀNH CHO AI)

```text
[BƯỚC 1: KHỞI TẠO MÔI TRƯỜNG DOCKER ORACLE]
├── Kiểm tra Docker daemon đang chạy trên máy.
├── Chạy docker compose up -d tại thư mục 04-MiniERP-Manufacturing-Warehouse.
└── Chờ Oracle container đạt trạng thái "healthy" (port 1521 sẵn sàng nhận kết nối).

[BƯỚC 2: THỰC THI SCHEMA, PL/SQL & SEED DATA]
├── Dùng công cụ sqlplus / sqlcl / dotnet migration chạy sql/01_schema.sql.
├── Biên dịch Package ERP_OPERATIONS từ sql/02_plsql.sql (đảm bảo STATUS = 'VALID').
└── Nạp dữ liệu ban đầu từ sql/03_seed.sql.

[BƯỚC 3: KIỂM THỬ KỊCH BẢN SỰ CỐ PO001]
├── Thực thi sql/04_incident_scenarios.sql.
├── Kiểm tra bắt đúng mã lỗi ORA-20007 khi thiếu đế giày MAT_RUBBER_01.
├── Kiểm tra bản ghi lỗi xuất hiện trong bảng ERROR_LOG.
├── Thực hiện nhập kho bù qua procedure receive_purchase_order.
└── Hoàn thành PO001 thành công, xuất kho thành phẩm tăng 50.

[BƯỚC 4: BUILD & VERIFY BACKEND ASP.NET CORE API]
├── Di chuyển vào thư mục src/.
├── Chạy lệnh dotnet restore và dotnet build.
├── Kiểm tra chuỗi kết nối OracleDb trong appsettings.json.
├── Chạy dotnet run và kiểm tra các endpoint qua curl hoặc test HTTP:
│   ├── GET /api/warehouse
│   ├── GET /api/stock/WH_RAW
│   ├── POST /api/stock/in
│   ├── POST /api/manufacturing/production-order
│   ├── POST /api/manufacturing/production-order/PO001/complete
│   └── GET /api/support/errors?refNo=PO001
└── Xuất file swagger.json làm bằng chứng nghiệm thu API.
```

---

## PHẦN 4: ĐOẠN PROMPT CHUẨN ĐỂ BẠN COPY-PASTE CHO AI

*(Bạn hãy copy toàn bộ đoạn trong khung dưới đây và dán vào AI Assistant của bạn)*

```text
Bạn là một chuyên gia Senior .NET & Oracle Database Developer.
Nhiệm vụ của bạn là hoàn thiện, build và kiểm thử toàn diện đồ án "04-MiniERP-Manufacturing-Warehouse" đặt tại thư mục:
/Users/mainguyenbinhtan/Downloads/PORTFOLIO/04-MiniERP-Manufacturing-Warehouse

YÊU CẦU THỰC HIỆN TỪNG BƯỚC:
1. Đọc kỹ các file tài liệu trong thư mục docs/ và các file SQL trong thư mục sql/.
2. Khởi động Docker container Oracle Database bằng docker compose up -d.
3. Chạy các file SQL theo thứ tự: sql/01_schema.sql -> sql/02_plsql.sql -> sql/03_seed.sql vào cơ sở dữ liệu.
4. Chạy script mô phỏng sự cố sql/04_incident_scenarios.sql để xác nhận:
   - Thao tác complete_production_order cho PO001 báo lỗi thiếu vật tư ORA-20007.
   - Bản ghi lỗi được ghi thành công vào bảng ERROR_LOG (nhờ PRAGMA AUTONOMOUS_TRANSACTION).
   - Sau khi nhập thêm hàng PO_PUR_901, PO001 hoàn thành thành công và trạng thái chuyển sang COMPLETED.
5. Kiểm tra mã nguồn C# trong src/, chạy dotnet restore, dotnet build và sửa mọi cảnh báo/lỗi biên dịch nếu có.
6. Viết thêm một bộ test đơn vị (Unit/Integration Tests) hoặc script bash/curl tự động gọi thử toàn bộ các endpoints trong Program.cs để chứng minh hệ thống hoạt động 100%.
7. Báo cáo kết quả kiểm thử và cung cấp lệnh chạy hoàn chỉnh cho tôi.
```
