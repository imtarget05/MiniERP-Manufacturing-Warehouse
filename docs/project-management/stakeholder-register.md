# Stakeholder Register & Communication Matrix
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** PM-STK-003  

---

## 1. Stakeholder Register

| Stakeholder ID | Name / Title | Department | Project Role | Influence | Interest | Key Expectations |
|---|---|---|---|:---:|:---:|---|
| **STK-01** | Trần Văn An (Executive VP) | Executive Leadership | Project Sponsor | High | High | Deliver on-time within budget; achieve $\ge 98\%$ inventory accuracy; ensure full export brand compliance. |
| **STK-02** | Nguyễn Thị Bình (Warehouse Manager) | Warehouse Logistics | Business Owner (`WH_RAW`/`WH_FG`) | High | High | Eliminate paper goods receipt notes; prevent duplicate scans; implement reliable bin location management. |
| **STK-03** | Lê Hoàng Minh (Production Superintendent)| Manufacturing Floor | Business Owner (Assembly Lines) | High | High | Prevent line downtime due to unexpected material shortages; fast production order completion. |
| **STK-04** | Đỗ Quang Hải (IT Director) | Information Technology | IT Governance Sponsor | High | Medium | Containerized architecture; strict secret management; robust backup/DR with RTO $< 2\text{ hours}$. |
| **STK-05** | Vũ Đình Khoa (QA/QC Compliance Lead) | Quality Assurance | Compliance Authority | Medium | High | Rapid lot recall; 100% backward & forward genealogy tracing; quarantine hold capabilities. |
| **STK-06** | Phạm Minh Tuấn (Lead Warehouseman) | Warehouse Operations | End-User Champion | Low | High | Easy barcode scanning interface; responsive UI on rugged handheld devices; clear error feedback. |
| **STK-07** | Hoàng Lan Anh (Production Planner) | Planning & Scheduling | End-User Champion | Medium | High | Instant visibility into raw material stock before issuing production orders to lines. |

---

## 2. Stakeholder Engagement & Communication Plan

| Communication Event | Objective | Frequency | Format | Participants |
|---|---|---|---|---|
| **Steering Committee Review** | Review milestone progress, budget, risk register, and change requests | Bi-weekly | Executive Deck + Live Demo | Sponsor, IT Director, PM |
| **Sprint Demo & Acceptance** | Review working code, verify acceptance criteria against test runs | Weekly | Working Software Walkthrough | Business Owners, Planners, PM |
| **Operational Readiness Review** | Review cutover checklist, user training status, and migration verification | Pre-Go-Live | Formal Checklist Meeting | All Stakeholders |
| **Post-Go-Live Incident Triage** | Review open tickets in `SUPPORT_INCIDENT` and Root Cause Analyses | Daily (First 2 weeks) | Standup Meeting | IT Support, Business Owners |
