# Incident Runbook: INC-002 — Warehouse User Receives 403 Forbidden
**Project:** MiniERP Manufacturing & Warehouse  
**Incident Code:** INC-002  
**Severity:** P2 (High)  
**Target Component:** RBAC Authorization / `MutationAuthorizationMiddleware`  

---

## 1. Symptoms & Incident Report
- **Reported By:** Warehouse clerk attempting to execute a cycle-count stock adjustment.
- **Symptom:** Submitting `/api/automation/stock/adjust` returns `403 Forbidden`:
  `{"success":false,"errorCode":"AUTH_FORBIDDEN","message":"User lacks required role."}`.
- **Impact:** Stock adjustment cannot be written directly to `STOCK` ledger.

---

## 2. Investigation Protocol & Diagnostic Steps

1. **Verify Current User Token Claims:**
   ```bash
   # Inspect claims of user token:
   curl -H "Authorization: Bearer $USER_TOKEN" http://localhost:5000/api/auth/me
   # Returns: {"id":3,"username":"warehouse01","roles":["WAREHOUSE"]}
   ```
2. **Review Endpoint Security Policy (`src/Program.cs`):**
   - The `/api/automation/stock/adjust` endpoint and `adjust_stock_with_approval` procedure enforce the **Two-Person Rule** (implemented under **CR-001**).
   - Only users with role `ADMIN` or `MANAGER` may execute direct stock adjustments. Regular `WAREHOUSE` staff are only permitted to submit *proposals* via `/api/automation/approvals`.
3. **Inspect Active Role Mappings (`USER_ROLE`):**
   ```sql
   SELECT u.USERNAME, r.NAME AS ROLE_NAME
     FROM APP_USER u
     JOIN USER_ROLE ur ON u.ID = ur.USER_ID
     JOIN ERP_ROLE r ON ur.ROLE_ID = r.ID
    WHERE u.USERNAME = 'warehouse01';
   ```

---

## 3. Root Cause Analysis (RCA)
This is an intended enterprise security control functioning as designed, rather than an application bug. Standard warehouse operators are restricted from direct ledger write-offs to prevent unauthorized inventory shrinkage. The user followed the wrong operational procedure by attempting a direct adjustment rather than an approval request.

---

## 4. Resolution Procedure

1. **Instruct User on Correct Workflow:**
   - Direct the operator to submit an adjustment proposal:
     ```bash
     curl -X POST http://localhost:5000/api/automation/approvals \
       -H "Authorization: Bearer $USER_TOKEN" \
       -H "Content-Type: application/json" \
       -d '{"approvalType":"STOCK_ADJUST","referenceNo":"CYCLE_COUNT_01","targetTable":"STOCK","notes":"Discrepancy in bin 01"}'
     ```
2. **Manager Approval:**
   - The Warehouse Manager (`admin` or `manager01`) reviews and approves the request:
     ```bash
     curl -X POST http://localhost:5000/api/automation/approvals/APP-001/decision \
       -H "Authorization: Bearer $MANAGER_TOKEN" \
       -H "Content-Type: application/json" \
       -d '{"decision":"APPROVED","comment":"Physical count verified."}'
     ```

---

## 5. Prevention & System Hardening
- Update UI to clearly separate "Đề xuất kiểm kê" (Warehouse role) from "Phê duyệt điều chỉnh" (Manager role).
- Display descriptive UI tooltips explaining why direct adjustment buttons are disabled for standard warehouse roles.
