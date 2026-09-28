# Market & Competitor Analysis — MiniERP (Desk Research)

**Doc code:** BA-MKT-001
**Project:** MiniERP Manufacturing & Warehouse
**Ngày:** 2026-09-28
**Phương pháp:** *Desk research* (tài liệu công khai, đánh giá theo tiêu chí nghiệp vụ kho
& sản xuất của bài toán Tân Vĩnh Footwear). **Không phỏng vấn khách hàng, không survey
người dùng cuối** — phần primary research nằm ở `01-business-context.md` (case study
fictionalized) và phải ghi rõ giới hạn này khi trích dẫn.

---

## 1. Bài toán cần giải quyết (tóm tắt từ BRD)

| Pain point | Yêu cầu tối thiểu |
|---|---|
| Không truy được nguồn gốc lô (quality recall) | Lot/expiry + genealogy 2 chiều, trace < 60s |
| Tồn kho sai (duplicate scan, mất mạng) | Idempotency key + transaction nguyên tử |
| Release lệnh khi thiếu liệu | Pre-flight BOM check + reservation |
| Điều chỉnh kho không qua phê duyệt | Dual approval + audit trail |
| Sự cố kho không tới được IT | Incident webhook fail-soft (MiniERP → Helpdesk) |

## 2. Thị trường lựa chọn (build vs buy)

Đối tượng: nhà máy 1.200 công nhân, 3 kho, 4 line, phần mềm hiện Excel + sổ sách.

| Giải pháp | Phân khúc | Điểm mạnh | Điểm yếu với bài toán này | Ước chi phí |
|---|---|---|---|---|
| **Build in-house (.NET 8 + Oracle 23c)** — *lựa chọn hiện tại* | SME sản xuất | Khớp 100% quy trình FEFO/genealogy/PL-SQL; kiểm soát được RBAC & outbox | Tự bảo trì; thời gian phát triển dài | Licenses ~0 (Oracle Free, .NET free) + công sức |
| **Odoo Community/Enterprise** | SME–mid | Modules Inventory/MRP có sẵn, cộng đồng lớn | Genealogy theo lô & FEFO phải custom; dual-approval theo workflow của họ không sẵn; tích hợp Helpdesk riêng phải viết | Enterprise ~hàng trăm USD/user/năm |
| **ERPNext** | SME | Mở nguồn, có Manufacturing + Stock | Traceability 2 chiều mỏng; control trace/label ZPL phải viết thêm; triển khai ERPNext thật cần đối tác | Community free + hosting |
| **SAP Business One** | SME lớn hơn | Chuẩn mực, đối tác triển khai | Chi phí license + implement cao; overkill cho 1 nhà máy; lock-in | License/subscription theo user |
| **Marello / Infor / Epicor** | Mid–enterprise | Mạnh distribution/manufacturing | Quá nặng, chi phí & thời gian triển khai vượt phạm vi | — |

**Kết luận lựa chọn:** với 5 pain point ở §1 (đặc biệt genealogy 2 chiều + dual
approval + outbox sang Helpdesk), **build trên stack sẵn có của công ty (.NET +
Oracle)** cho độ khớp và chi phí thấp nhất; rủi ro lớn nhất là *scope creep* — được
kiểm soát bằng `docs/07-requirement-traceability-matrix.md` (mỗi FR ↔ API ↔ UAT).

## 3. So sánh hướng tự động hóa (tính năng lặp lại)

| Tiêu chí | **n8n** (đã dùng) | Power Automate | Zapier | Script nội bộ (PowerShell/CRON) |
|---|---|---|---|---|
| Self-host / data on-prem | ✅ (docker) | ❌ (cloud) | ❌ | ✅ |
| Trigger webhook nội bộ (MiniERP → Helpdesk) | ✅ `webhook` node | ⚠️ gateway/licensing | ⚠️ | ✅ |
| Kiểm thử/revision workflow | JSON export trong git (`docs/automation/ticket-triage.n8n.json`) | yếu | yếu | ✅ |
| Chi phí | 0 (self-host) | theo license M365 | theo lượt chạy | 0 |
| Phù hợp evidence portfolio | ✅ JSON version-controlled | ⚠️ | ⚠️ | ✅ nhưng khó demo |

**Kết luận:** n8n self-host là lựa chọn đúng cho workflow *ticket triage* (webhook →
HTTP request → Helpdesk `/api/integrations/minierp/incidents`), đồng thời JSON export
lưu trong repo tạo được evidence kiểm chứng được (khác với workflow chỉ tồn tại trên
cloud account cá nhân).

## 4. Benchmark competitor cho module kho (best-of-breed, nếu không build trọn vỡ)

| Nhu cầu | Tối ưu thay thế |
|---|---|
| WMS chuyên sâu | Odoo Barcode/Stock, Magaya, Fishbowl |
| Traceability chuỗi cung ứng | FoodLogiQ/TRACE (ngành thực phẩm), OpenLMIS (y tế) — tham chiếu thiết kế, không phải đối thủ trực tiếp |
| MES lightweight | Siemens Opcenter Express, Fulcrum — chỉ cần nếu mở rộng beyond 4 line |

## 5. Gap & khuyến nghị

1. **Thị trường:** chưa có primary research (khách hàng thật, survey) → nếu ứng tuyển
   vị trí Product/BA, ghi rõ "desk research + fictionalized case study", không nói
   là nghiên cứu thị trường đã chạy với khách hàng thật.
2. **Competitor pricing** trong bảng trên là định tính (order-of-magnitude), phải
   cập nhật lại từ báo giá chính thức trước khi đưa vào proposal thật.
3. **Khuyến nghị giữ nguyên** hướng build + n8n: đúng yêu cầu BRD, evidence đầy đủ
   trong repo (`BRD.md`, `FRD.md`, `uat/`, `automation/`).

## 6. Nguồn / tiêu chí tra cứu

- Tài liệu sản phẩm công khai: odoo.com, erpnext.org, sap.com/business-one,
  n8n.io, learn.microsoft.com (Power Automate), zapier.com (truy cập 2026-09).
- Tiêu chí chấm: khớp 5 pain point §1 · on-prem · chi phí · khả năng test/evidence
  trong repo · thời gian triển khai cho 1 nhà máy.
