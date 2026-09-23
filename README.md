# MiniERP — Hệ thống Kho & Sản xuất cho Nhà máy

> **Dự án portfolio** mô phỏng nghiệp vụ ERP lõi tại nhà máy sản xuất giày da/may mặc (KCN),  
> xây dựng bằng **C# / ASP.NET Core** + **Oracle Database** + **PL/SQL**.

<p align="center">
  <a href="https://github.com/imtarget05/MiniERP-Manufacturing-Warehouse/actions/workflows/ci.yml">
    <img src="https://github.com/imtarget05/MiniERP-Manufacturing-Warehouse/actions/workflows/ci.yml/badge.svg" alt="CI/CD Pipeline"/>
  </a>
  <img src="https://img.shields.io/badge/Tests-56%2F56%20Passing-brightgreen?logo=checkmarx&logoColor=white" alt="Tests"/>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/Database-Oracle%2019c%2F21c-F80000?logo=oracle&logoColor=white" alt="Oracle"/>
  <img src="https://img.shields.io/badge/Language-C%23%20%7C%20PL%2FSQL-239120?logo=csharp&logoColor=white" alt="C# PL/SQL"/>
  <img src="https://img.shields.io/badge/Container-Docker-2496ED?logo=docker&logoColor=white" alt="Docker"/>
  <img src="https://img.shields.io/badge/API-REST%20%2F%20Swagger-85EA2D?logo=swagger&logoColor=black" alt="Swagger"/>
  <img src="https://img.shields.io/badge/Target-ERP%20Manufacturing-orange?logo=target&logoColor=white" alt="Target"/>
</p>

---

## Dự án này làm được gì?

Nhà máy sản xuất thường gặp 3 vấn đề lặp đi lặp lại:

1. **Lệnh sản xuất thất bại giữa chừng** vì thiếu nguyên vật liệu — phát hiện quá muộn.
2. **Hai bộ phận cùng lấy một lô hàng** trong kho → số liệu âm, tồn kho sai.
3. **Sự cố xảy ra nhưng không có log** để truy vết nguyên nhân.

Dự án này xây dựng một hệ thống mini ERP giải quyết đúng 3 vấn đề đó:

- ✅ **Kiểm tra nguyên vật liệu trước khi bắt đầu sản xuất** — nếu thiếu thì cảnh báo ngay, không để dây chuyền chạy rồi mới lỗi.
- ✅ **Đặt giữ (soft reservation) nguyên vật liệu** cho từng lệnh sản xuất, tránh hai lệnh cùng dùng chung một kho.
- ✅ **Ghi log sự cố tự động** — kể cả khi giao dịch bị hủy, bản ghi lỗi vẫn được lưu lại để IT Support tra cứu.

---

## Tôi đã xây dựng những gì?

| Phần | Công nghệ | Mô tả |
|---|---|---|
| 🗄️ **Cơ sở dữ liệu Oracle** | Oracle 19c/21c | 20 bảng: kho hàng, nguyên vật liệu, BOM định mức, lệnh sản xuất, lịch sử giao dịch |
| ⚙️ **PL/SQL Packages** | PL/SQL | Logic nghiệp vụ trong Oracle: tính BOM, kiểm kho, xuất/nhập kho theo lệnh, ghi log sự cố |
| 🌐 **REST API** | C# / ASP.NET Core 8 | 31 endpoint để quản lý lệnh sản xuất, kho, lệnh mua hàng |
| 📊 **Dashboard web** | HTML / JS | Màn hình tổng quan kho — tồn kho, cảnh báo thiếu hàng, trạng thái lệnh |
| 🧪 **Test tự động** | xUnit | 56 test cases, chạy tự động qua GitHub Actions mỗi khi push code |

---

## Luồng nghiệp vụ chính (ví dụ thực tế)

> **Kịch bản:** Lệnh sản xuất `PO001` — làm 50 đôi giày `FG_RUNNER_PRO_42`

```
1. Kế hoạch viên tạo lệnh PO001 → trạng thái: RELEASED

2. Hệ thống tự kiểm tra kho nguyên liệu theo BOM:
   - Đế cao su:  cần 50, tồn kho 40 → THIẾU 10 đơn vị ❌

3. Cảnh báo tự động:
   - Lệnh PO001 chuyển sang "WAITING_MATERIAL"
   - Hệ thống tạo yêu cầu mua hàng bổ sung
   - Ghi nhận sự cố vào ERROR_LOG (kể cả khi giao dịch thất bại)

4. Mua hàng nhập thêm 100 đế cao su → kho cập nhật tự động, cảnh báo tự đóng

5. Sản xuất hoàn thành:
   - Hệ thống trừ nguyên liệu, nhập thành phẩm vào kho WH_FG
   - Cập nhật trạng thái PO001: COMPLETED ✅
   - Số liệu kho được kiểm tra cân bằng tự động
```

---

## Dashboard kho

![MiniERP Factory Operations Dashboard](docs/images/dashboard-preview.png)

---

## Tại sao làm dự án này?

Dự án được xây dựng nhắm vào vị trí **IT ERP Support tại nhà máy sản xuất** (ví dụ: TKG Taekwang, các KCN Đồng Nai/Bình Dương).

Công việc IT ERP Support hàng ngày bao gồm:
- 🔧 Hỗ trợ người dùng khi lệnh sản xuất lỗi, tồn kho sai
- 🔍 Tra cứu log, tìm nguyên nhân gốc rễ (Root Cause Analysis)
- 🗂️ Phối hợp với bộ phận kho/sản xuất để sửa dữ liệu đúng quy trình

Dự án này mô phỏng đúng các tình huống đó — tôi có thể demo và giải thích từng bước trong buổi phỏng vấn.

---

## Chạy thử trong 2 phút

**Yêu cầu:** Docker Desktop đã cài.

```bash
# 1. Khởi động Oracle Database (container)
docker compose up -d && bash scripts/start-db.sh

# 2. Tạo bảng, nạp dữ liệu mẫu
bash scripts/run-sql.sh

# 3. Chạy 56 test cases
dotnet test tests/MiniERP.Api.Tests

# 4. Khởi động API
bash scripts/start-api.sh
# → http://localhost:5000/swagger

# 5. Mở dashboard kho
cd dashboard && python3 -m http.server 8080
# → http://localhost:8080
```

Hoặc chạy tất cả 7 giai đoạn một lần:
```bash
bash scripts/run-all-tests.sh
```

---

## Demo nhanh (cho buổi phỏng vấn)

**Demo 1 — Lệnh sản xuất thiếu nguyên liệu:**
```bash
# Thử hoàn thành PO001 khi kho thiếu đế cao su
curl -X POST http://localhost:5000/api/manufacturing/production-order/PO001/complete
# → 409 Conflict: thiếu nguyên liệu, LOG được ghi tự động

# Xem log sự cố (kể cả sau khi giao dịch thất bại)
curl http://localhost:5000/api/support/errors?refNo=PO001
```

**Demo 2 — Nhập hàng, sản xuất thành công:**
```bash
# Nhập 100 đế cao su vào kho
curl -X POST http://localhost:5000/api/procurement/purchase-order/PO_PUR_901/receive

# Hoàn thành lệnh sản xuất
curl -X POST http://localhost:5000/api/manufacturing/production-order/PO001/complete
# → 200 OK: COMPLETED ✅
```

**Demo 3 — Điều chỉnh kho cần phê duyệt 2 người:**
```bash
# Không có phê duyệt → bị từ chối
curl -X POST http://localhost:5000/api/automation/stock/adjust \
  -H "Content-Type: application/json" \
  -d '{"warehouseCode":"WH_RAW","itemCode":"MAT_RUBBER_01","quantityDelta":10}'
# → 403 Forbidden: cần phê duyệt trước
```

---

## Cấu trúc project

```
04-MiniERP-Manufacturing-Warehouse/
├── 📁 sql/              ← DDL Oracle, PL/SQL packages, dữ liệu mẫu nhà máy giày
├── 📁 src/              ← ASP.NET Core 8 Web API (C#) — 31 endpoints
├── 📁 dashboard/        ← Giao diện web tổng quan kho (HTML/JS)
├── 📁 tests/            ← 56 automated test cases (xUnit)
├── 📁 scripts/          ← Script bash để setup, chạy và kiểm tra
└── 📁 docs/             ← Tài liệu nghiệp vụ, runbook xử lý sự cố ERP
```

---

## Tech stack

<p>
  <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=csharp&logoColor=white"/>
  <img src="https://img.shields.io/badge/ASP.NET_Core_8-512BD4?style=for-the-badge&logo=dotnet&logoColor=white"/>
  <img src="https://img.shields.io/badge/Oracle_19c%2F21c-F80000?style=for-the-badge&logo=oracle&logoColor=white"/>
  <img src="https://img.shields.io/badge/PL%2FSQL-F80000?style=for-the-badge&logo=oracle&logoColor=white"/>
  <img src="https://img.shields.io/badge/Docker-2496ED?style=for-the-badge&logo=docker&logoColor=white"/>
  <img src="https://img.shields.io/badge/GitHub_Actions-2088FF?style=for-the-badge&logo=githubactions&logoColor=white"/>
  <img src="https://img.shields.io/badge/xUnit-512BD4?style=for-the-badge&logo=dotnet&logoColor=white"/>
</p>

---

*Dự án portfolio của **Mai Nguyễn Bình Tân** — GitHub: [@imtarget05](https://github.com/imtarget05)*
