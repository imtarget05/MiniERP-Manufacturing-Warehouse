# MiniERP Dashboard (static)

Dashboard vận hành nhà máy TKG — mở mà không cần Oracle/DB.

## Chạy

```bash
cd dashboard
python3 -m http.server 8080
# mở http://localhost:8080
```

Mặc định dùng dữ liệu mock từ seed (PO001, BOM FG_RUNNER_PRO_42).
Để nối API thật: chạy `bash scripts/start-api.sh` (port 5000),
nhập API base `http://localhost:5000` ở sidebar → bấm **Kiểm tra**.

## Tabs

- Tổng quan: KPI kho, cảnh báo dưới min-stock, BOM
- Kho & Tồn: `GET /api/stock/{wh}` + form `POST /api/stock/in|out`
- Sản xuất & BOM: pre-flight check + complete PO atomic
- Sự cố PO001: runbook ORA-20007 → fix → nghiệm thu
