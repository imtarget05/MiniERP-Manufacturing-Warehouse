# Project Completion Plan — MiniERP Manufacturing & Warehouse

**Date:** 2026-09-24
**Status baseline:** 18/18 phases 🟢 locally, remote `main` tại `cfc8070` sạch, working tree clean.
**Mục tiêu plan này:** đưa dự án từ "xong local" sang "xong hoàn toàn": CI xanh trên remote, integration có Oracle chứng thực, release có tag, demo chạy được 5 phút.

> Phạm vi: KHÔNG mở Phase 20. Chỉ chốt những gì thuộc Phase 0–18 còn thiếu bằng chứng remote.

---

## 1. Spec — Định nghĩa "hoàn thành toàn bộ"

- [ ] Remote `origin/main` chứa toàn bộ code/docs hiện tại (đã đạt: `cfc8070` pushed, tree clean).
- [ ] CI 3 jobs xanh trên GitHub Actions sau commit `cfc8070` (sonar gate + test categorization mới).
- [ ] Full suite gồm Oracle Integration pass (19 tests cần DB), không chỉ 144 unit.
- [ ] Full-stack `docker compose up` chạy được: `oracle-db:1521 + api:5000 + ui:8080`.
- [ ] Demo 5 phút trong `docs/demo/recruiter-demo.md` chạy end-to-end không lỗi tay.
- [ ] Release `v1.1` có tag + release notes, badges README phản ánh đúng số thật.
- [ ] Không tồn tại Phase 19/20 dưới dạng code dở dang.

## 2. Architecture — Không đổi code nghiệp vụ, chỉ verify + release

```text
Local (đã xong)          Remote (cần verify)         Release (cần làm)
31 tables / 3 packages →  CI Job 1: build + unit  →  tag v1.1
62 endpoints / 15 svc   →  CI Job 2: Oracle + full →  GitHub Release
144 unit pass local     →  CI Job 3: docker build  →  demo 5 phút
docs 57 files signed    →  Sonar skip-or-run green →  closeout, STOP
```

Nguyên tắc: mọi task dưới đây là **chạy, kiểm, tag** — không thêm feature, không sửa schema, không mở Phase 20.

## 3. Tasks (bite-sized, theo thứ tự)

### Stage A — Remote CI verification (30–60 phút, chờ runner)
- [ ] A1. Mở Actions run mới nhất của `cfc8070`, xác nhận Job 1 (Build & Unit) xanh.
  - Verify: `dotnet build -p:TreatWarningsAsErrors=true` + `Category!=Integration` pass.
  - Nếu đỏ: đọc log, fix, không sửa nghiệp vụ.
- [ ] A2. Xác nhận Job 2 (Full Acceptance: `scripts/run-all-tests.sh` 9 stages) xanh.
  - Verify: Oracle container boot, `run-sql.sh` 11 files, 3 packages VALID, integration pass, artifacts upload.
  - Đây là bằng chứng duy nhất cho 19 tests Oracle-dependent vừa gắn `[Trait Category=Integration]`.
- [ ] A3. Xác nhận Job 3 (docker build api + ui) xanh + Sonar workflow skip-or-run xanh (không đỏ vì thiếu token).
  - Verify: sonar gate `configured=false` vẫn green khi không có secret.

### Stage B — Local full-stack drill (1–2 giờ, cần Docker Desktop)
- [ ] B1. `docker compose up -d oracle-db && bash scripts/start-db.sh && bash scripts/run-sql.sh` → expect `31 tables, 3 packages VALID`.
- [ ] B2. `dotnet test tests/MiniERP.Api.Tests` full (không filter) → expect `163/163` (144 unit + 19 integration).
- [ ] B3. `docker compose up -d` full 3 containers → mở `http://localhost:8080` (UI) + `http://localhost:5000/swagger` (API) đều sống.
- [ ] B4. Chạy `bash scripts/test-api.sh` + `bash scripts/test-traceability.sh` smoke pass.

### Stage C — Demo rehearsal (30 phút)
- [ ] C1. Chạy đúng script `docs/demo/recruiter-demo.md`: login `warehouse01` → receive lot → complete PO001 → trace backward. Ghi lại output curl.
- [ ] C2. Chụp lại `docs/images/dashboard-preview.png` nếu UI khác ảnh cũ (bài học từ commit `784751d/3cec819`: không dùng mockup).

### Stage D — Release & honesty polish (30 phút)
- [ ] D1. Đối chiếu số liệu README với thực tế: endpoints (62 đo được vs 57 ghi), tests (144 unit local / 163 full), tables (31). Sửa badge/section lệch, giữ nguyên disclaimer portfolio §20.
- [ ] D2. Tạo tag + release: `git tag -a v1.1 -m "Release 1.1: 18-phase ERP upgrade" && git push origin v1.1`, viết GitHub Release notes trỏ tới `FINAL-IMPLEMENTATION-REPORT.md`.
- [ ] D3. Cập nhật `docs/IMPLEMENTATION_STATUS.md` dòng cuối: ghi `cfc8070` + ngày verify CI xanh + full test count thực đo.

### Stage E — Closeout
- [ ] E1. `git status` clean, `git log --oneline -3` gồm `ca7610f + cfc8070 + tag v1.1`. Dừng. Không mở Phase 20 nếu chưa có yêu cầu mới.

## 4. DoD từng Stage (không cãi nhau)

| Stage | Pass khi |
|---|---|
| A | 3 Jobs + Sonar đều 🟢 trên cùng commit `cfc8070` |
| B | `run-sql.sh` báo VALID + full `dotnet test` 0 failed + compose 3/3 healthy |
| C | Demo script chạy 1 mạch, có log curl + ảnh dashboard thật |
| D | Tag `v1.1` tồn tại trên remote, README số liệu khớp thực đo |
| E | Tree clean, không file dở dang, tuyên bố đóng dự án |

## 5. Rủi ro đã biết (từ lịch sử repo)
- Oracle container nặng/khởi động lâu → `start-db.sh` retry + healthcheck 60 lần, kiên nhẫn.
- Sonar đỏ vì thiếu token → đã fix bằng gate step ở `cfc8070`, expect skip-green.
- Dashboard ảnh mockup → chỉ dùng screenshot thật.
- Scope creep Phase 20 → từ chối trong plan này.
