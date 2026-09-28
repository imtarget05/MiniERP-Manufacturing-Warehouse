# Functional Requirement Document — MiniERP
**Doc:** FRD | **Code:** BA-FRD-001 | **Spec đầy đủ:** `docs/business-analysis/04-functional-requirements.md` (FR-001..), NFR `05-non-functional-requirements.md`, use-cases `06-use-cases.md`, traceability `07-requirement-traceability-matrix.md`

## FR ↔ API ↔ UAT (bản rút gọn cho recruiter search)

| FR | Tóm tắt | API (trong `src/Program.cs`) | UAT |
|---|---|---|---|
| FR-001 | Goods receipt gắn lot/expiry, idempotent | `POST /api/warehouse/receipts/{poNo}/receive` (:983, `IsReplay`) | UAT-INV-001 |
| FR-002 | Pre-flight material check + release giữ hàng | `POST .../material-check` (:617), `/reserve` (:637), `/release` (:654) | UAT-MFG-001 |
| FR-003 | Complete nguyên tử (consume + FG output) | `POST /api/manufacturing/production-order/{poNo}/complete-traceable` (:1172) | UAT-MFG-002 |
| FR-004 | Traceability xuôi/ngược | `GET /api/trace/{lotCode}` (:1215), `GET /api/barcodes/resolve/{code}` (:1293) | UAT-REP-001 (85ms) |
| FR-005 | Dual approval cho adjustment | `POST .../approvals` (:864), `POST .../stock/adjust` (:911 → 403 nếu thiếu approval) | UAT-APP-001 |
| FR-006 | Replenishment/sweep/stale-detect | `POST .../replenishment/evaluate` (:693), `/sweep` (:717), `/stale-orders/detect` (:741) | UAT-REP-001 |
| FR-007 | Incident → Helpdesk webhook (fail-soft, retry 3) | `POST .../incidents` (:802, `HelpdeskIntegrationService`), `POST /api/integration/helpdesk/incidents` (:1327) | support/INC-001..005 |
| FR-008 | Auth 10 policies + audit | `src/Services/AuthPolicies.cs`, `AuditMiddleware.cs`, `GET /api/support/errors` | UAT-SEC-001 |

Acceptance criteria chi tiết giữ nguyên trong `04-functional-requirements.md` — file này chỉ là index để recruiter/BA tìm trong 30s.
