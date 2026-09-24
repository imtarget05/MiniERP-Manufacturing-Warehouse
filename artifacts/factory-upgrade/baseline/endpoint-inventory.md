# Phase 0 — endpoint inventory (route list from src/Program.cs)

| # | Method | Route | Tag |
|---|--------|-------|-----|
| 1 | GET | /api/health | Support |
| 2 | GET | /api/warehouse | Warehouse |
| 3 | GET | /api/stock/{warehouseCode} | Warehouse |
| 4 | POST | /api/stock/in | Warehouse |
| 5 | POST | /api/stock/out | Warehouse |
| 6 | POST | /api/manufacturing/bom/line | Manufacturing |
| 7 | POST | /api/manufacturing/production-order | Manufacturing |
| 8 | GET | /api/manufacturing/production-order/{poNo} | Manufacturing |
| 9 | POST | /api/manufacturing/production-order/{poNo}/complete | Manufacturing |
| 10 | POST | /api/manufacturing/production-order/{poNo}/cancel | Manufacturing |
| 11 | POST | /api/procurement/purchase-order | Procurement |
| 12 | POST | /api/procurement/purchase-order/{poNo}/receive | Procurement |
| 13 | GET | /api/support/errors | ERP Support |
| 14 | POST | /api/support/change-requests | ERP Support |
| 15 | GET | /api/support/change-requests/{crNo} | ERP Support |
| 16 | POST | /api/automation/production-order/{poNo}/material-check | Automation |
| 17 | POST | /api/automation/production-order/{poNo}/reserve | Automation |
| 18 | POST | /api/automation/production-order/{poNo}/release | Automation |
| 19 | POST | /api/automation/production-order/{poNo}/complete | Automation |
| 20 | POST | /api/automation/replenishment/evaluate | Automation |
| 21 | POST | /api/automation/replenishment/sweep | Automation |
| 22 | GET | /api/automation/replenishment/alerts | Automation |
| 23 | POST | /api/automation/stale-orders/detect | Automation |
| 24 | GET | /api/automation/stale-orders | Automation |
| 25 | POST | /api/automation/reports/{reportType} | Automation |
| 26 | GET | /api/automation/reports | Automation |
| 27 | GET | /api/automation/runs | Automation |
| 28 | POST | /api/automation/incidents/collect | Automation |
| 29 | GET | /api/automation/incidents | Automation |
| 30 | POST | /api/automation/approvals | Automation |
| 31 | POST | /api/automation/approvals/{approvalNo}/decision | Automation |
| 32 | GET | /api/automation/approvals | Automation |
| 33 | POST | /api/automation/stock/adjust | Automation |

Total: 33 mapped routes (README says 31 — actual count is 33 including health).
New traceability routes (Phase 2+) are ADDITIVE and must not rename/remove the above.
