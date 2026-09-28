# Recruiter Evidence — MiniERP Manufacturing & Warehouse
Problem: kho giày thủ công — nhận hàng thiếu lot/expiry, release lệnh khi thiếu liệu, điều chỉnh kho không phê duyệt, truy xuất lỗi mất 2 ngày.
Fix: `src/Program.cs` (1363 dòng, ~45 routes) + Oracle PL/SQL `sql/01..11` (`ERP_OPERATIONS/AUTOMATION/TRACEABILITY`).
Evidence:
- `docs/BRD.md`, `docs/FRD.md`, `docs/user-journey.md` (mermaid) — BA track cho PHS JD
- `docs/uat/uat-results.md` 8/8 pass + `docs/uat/UAT-SIGNOFF.md`; trace 85ms (target 60s)
- `docs/automation/ticket-triage.n8n.json` — n8n import được: MiniERP webhook → LLM classify → Helpdesk ticket
- `src/Services/TokenService.cs` JWT HS256 + 10 policies (`AuthPolicies.cs`) + refresh rotation
- `GET /metrics`, `observability/` Prometheus/Grafana/SLO, `Makefile` demo/seed
- `tests/MiniERP.Api.Tests/` xunit + `.github/workflows/ci.yml` (build + 9-stage acceptance + docker)
Demo: `make demo` → `dashboard/` vanilla JS → trace 1 lot về đúng cây nguyên liệu 3 tầng.
