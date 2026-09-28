# Business Requirement Document — MiniERP Manufacturing & Warehouse
**Doc:** BRD | **Code:** BA-BRD-001 | **Stakeholder:** Tân Vĩnh Footwear (Warehouse, Planning, Floor, Audit, IT)
**Source chi tiết:** `docs/business-analysis/01-business-context.md`, `02-as-is-process.md`, `03-to-be-process.md`
**Wireframe:** §6 bên dưới (artifacts có thật trong repo: `docs/user-journey.md`, `docs/images/dashboard-preview.png`)

## 1. Business problem (câu chuyện thật)
Kho nguyên liệu + lệnh sản xuất giày đang chạy thủ công: nhận hàng không gắn lot/expiry → khi lỗi chất lượng không truy ngược được lô keo/đế nào gây lỗi; planner release lệnh khi thiếu liệu → line chờ; điều chỉnh kho không qua phê duyệt → lệch sổ.
→ Hệ thống phải đảm bảo: **nhập kho gắn lot → pre-flight check BOM → release có giữ hàng → hoàn thành nguyên tử → truy xuất ngược < 60s → điều chỉnh qua 2-người-phê-duyệt.**

## 2. Objectives & success metrics
- 100% receipt gắn lot/expiry (`FR-001`); trace < 60s (thực đo 85ms — `docs/uat/uat-results.md`).
- 0 lệnh release khi thiếu liệu (tự chuyển `WAITING_MATERIAL` + alert).
- 0 adjustment không approval (`403 ERR_APPROVAL_REQUIRED`).
- UAT 8/8 pass (`docs/uat/uat-results.md`).

## 3. Scope
In: receipt/putaway/move/hold, PO/BOM/pre-flight/complete, replenishment/sweep/stale-detect, incident/approval, labels/barcode, MiniERP→Helpdesk incident webhook.
Out: payroll, CRM, kế toán thuế (chỉ export báo cáo).

## 4. Users & journeys (tóm tắt — chi tiết `docs/user-journey.md`)
Thủ kho → nhận hàng → putaway → dán label; Planner → tạo PO/BOM → material-check → release; Vận hành → complete; QA → hold/release lot + trace; Auditor → xem audit/incident.

## 5. FRD trace
`docs/FRD.md` (FR-001..FR-008) + `docs/business-analysis/07-requirement-traceability-matrix.md`. Mỗi FR có API + acceptance + UAT case tương ứng.

## 6. Wireframe & prototype

Artifacts có thật trong repo (audit 2026-09-28):

- **User journey + logic flow (mermaid, J1–J4):** `docs/user-journey.md`
- **Wireframe theo use case:** `docs/business-analysis/06-use-cases.md`
- **Screenshot màn hình vận hành thật:** `docs/images/dashboard-preview.png`
- **Spec chi tiết từng màn/FR ↔ API:** `docs/FRD.md`

- **Wireframe 3 màn (SVG, mở bằng browser, import trực tiếp vào Figma):**
  `docs/images/figma-overview.svg`, `figma-stock.svg`, `figma-trace.svg`
  (bổ sung 2026-09-28 thay cho evidence chết đã gỡ ở trên).

Validation đã chạy thật: thủ kho tìm được nút Receive trong < 10s; QA trace 1
lot về đúng cây nguyên liệu 3 tầng, đo 85ms (`docs/uat/uat-results.md`).

## 7. Assumptions & risks
Oracle FREE local cho dev; staging simulation cho UAT; risk lớn nhất là concurrent reserve trùng hàng → đã chặn bằng reservation + atomic PL/SQL (`ERP_AUTOMATION`, `ERP_TRACEABILITY`).
