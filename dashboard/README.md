# MiniERP Dashboard (static)

Dashboard vận hành nhà máy TKG. Đây là **client thuần của API**: mọi con số và
mọi danh sách trên màn hình đến từ response body, hoặc panel hiển thị trạng
thái `N/A` / empty state có kiểm soát khi API không có contract để đọc.

## Yêu cầu

* Cần API MiniERP chạy được (không cần Oracle trực tiếp từ trình duyệt).
* Cần đăng nhập: dashboard **không** có dữ liệu nào trước khi có phiên hợp lệ.

## API base đến từ đâu

Không có ô nhập API base trên UI, và không có hằng số origin trong `app.js`.
Origin do **`runtime-config.js`** quyết định, và file này được nạp **trước**
`app.js`.

`runtime-config.js` hỗ trợ **một hook được hỗ trợ chính thức**: nếu
`window.__MINI_ERP_RUNTIME_CONFIG__` đã được đặt trước khi file nạp (ví dụ
bằng một script riêng đứng trước nó trong `index.html`, hoặc bằng cách mount
một bản `runtime-config.js` riêng), thì `apiBaseUrl` lấy từ đó:

```html
<script>window.__MINI_ERP_RUNTIME_CONFIG__ = { apiBaseUrl: 'https://erp.example.com' };</script>
<script src="runtime-config.js"></script>
```

Hook này được hỗ trợ có chủ đích (xem mục *Nguồn của origin* bên dưới về rủi ro).

| `apiBaseUrl` | Khi nào dùng |
|---|---|
| `''` (rỗng) | Bản container/Compose: Nginx cùng origin reverse-proxy `/api/` → `api:5000`. Đây là cấu hình mặc định trong image. |
| origin tuyệt đối | Chỉ khi phục vụ file tĩnh từ cổng khác cổng API (ví dụ `python3 -m http.server` khi API ở 5000). API phải cho phép origin này qua CORS. |

## Chạy

### Cách khuyến nghị — container (same-origin, không cần CORS)

```bash
docker build -t local/minierp-ui:dev -f dashboard/Dockerfile dashboard
docker compose up -d api ui      # ui đọc nginx.conf + runtime-config.js trong image
# mở http://localhost:8080
```

Container chạy bằng user không phải root (`nginx`, uid 101); mọi đường dẫn ghi
của Nginx nằm dưới `/tmp` vì filesystem của image không ghi được cho user thường.

### Chạy tĩnh trên host

```bash
bash scripts/start-api.sh &                       # API ở port 5000
python3 -m http.server 8080 --directory dashboard # static ở port 8080
```

**Cảnh báo:** ở cách này `runtime-config.js` trong repo có `apiBaseUrl: ''`, nên
mọi lệnh gọi sẽ thành `/api/...` trên chính port 8080 — tức là **file server
tĩnh**, và sẽ không có gì để trả lời. Bắt buộc phải sửa `runtime-config.js`
trước khi mở trình duyệt:

```js
window.__MINI_ERP_CONFIG__ = { apiBaseUrl: 'http://localhost:5000' };
```

Về CORS: `CORS_ALLOWED_ORIGINS` là **cộng thêm** vào danh sách
`Cors:AllowedOrigins` trong `src/appsettings.json`, không thay thế nó. Danh sách
mặc định đã có sẵn `http://localhost:8080`, nên **cổng 8080 mặc định không cần
đổi gì**. Nếu bạn đổi sang cổng khác (8081…) thì phải thêm origin đó qua
`CORS_ALLOWED_ORIGINS`, vì hai cổng khác nhau là hai origin khác nhau.
Nếu không muốn làm việc này thì dùng container.

### Đăng nhập

`#` tài khoản, mật khẩu → `POST /api/auth/login`. Access token + refresh token
lưu **chỉ trong `sessionStorage` (2 khoá)** và bị xoá khi đăng xuất; không có
token trong `localStorage`, cookie hay URL. Access token hết hạn → tự refresh
một lần rồi thử lại; 401/403 không hồi phục được → quay vại màn hình đăng nhập
và xoá sạch panel.

## Tính mới của dữ liệu trên màn hình

Bảng tồn kho và KPI bên cạnh là **cùng một ảnh chụp** của lần `loadOverview` gần
nhất. Đổi kho chỉ đọc lại ảnh đó, không gọi API thêm; một lượt nhập/xuất từ
người khác, hoặc ghi thẳng qua API, sẽ không hiện cho tới khi bấm **Tải lại**
(hoặc đăng nhập lại / sau một thao tác làm mới). Cách này đổi lại một request mỗi
lần đổi kho và giữ cho bảng với KPI luôn khớp nhau.

Mọi đường đọc đều qua một chốt chặn epoch: nếu phiên đăng nhập kết thúc (401/403
hoặc đăng xuất) trong lúc một request còn đang bay, kết quả của request đó bị
bỏ và **không** ghi đè lên màn hình.

## Tabs và endpoint thật

| Tab | Nguồn dữ liệu |
|---|---|
| Tổng quan | `GET /api/health`, `GET /api/warehouse`, `GET /api/stock/{wh}` (mọi kho), `GET /api/automation/stale-orders?days=7`, `GET /api/support/errors?take=20`, `GET /api/automation/replenishment/alerts?status=OPEN` |
| Kho & Tồn kho | `GET /api/stock/{wh}` · `POST /api/stock/in|out` |
| Sản xuất | `GET /api/manufacturing/production-order/{poNo}` · `POST …/material-check` · `…/complete` · `…/allocate-lots` |
| Nhận hàng & Lots | `POST /api/warehouse/receipts/{po}/receive` · `POST /api/warehouse/putaway|move` · `GET /api/barcodes/resolve/{code}` · `POST /api/labels` · `GET /api/trace/{lotCode}` |
| Giám sát & Sự cố | `GET /api/automation/incidents?take=20` · `GET /api/automation/approvals?status=PENDING` |

**Định mức BOM: `N/A`.** Host chỉ có `POST /api/manufacturing/bom/line` (ghi),
không có endpoint đọc BOM, nên panel nói rõ điều đó thay vì dựng dòng giả.

**Danh sách lệnh sản xuất: không có endpoint.** Host chỉ tra được một lệnh theo
mã, nên người dùng tự nhập mã lệnh; không có mã nào được điền sẵn.

## Nguồn của origin

`runtime-config.js` là handle của operator trên API origin: ai ghi được file
này thì chỉ định được nơi bearer được gửi đi. Ràng buộc: file được phục vụ
same-origin với `no-store`; kẻ tấn công ghi được file này thì vốn đã ghi được
`index.html`/`app.js`; và nếu bạn đặt origin tuyệt đối ở đây thì API phải cho
phép origin đó qua CORS — tức là một hành động cố ý thứ hai. Không nên template
origin từ nguồn không tin cậy vào file này.
