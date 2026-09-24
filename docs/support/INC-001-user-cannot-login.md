# Incident Runbook: INC-001 — User Cannot Login
**Project:** MiniERP Manufacturing & Warehouse  
**Incident Code:** INC-001  
**Severity:** P2 (High)  
**Target Component:** Authentication / `TokenService` / `APP_USER`  

---

## 1. Symptoms & Incident Report
- **Reported By:** Morning shift warehouseman (`warehouse01`).
- **Symptom:** User enters credentials into the handheld scanner or Web Dashboard login box, clicks "Đăng nhập", and receives:
  `401 Unauthorized - {"success":false,"errorCode":"INVALID_CREDENTIALS"}`.
- **Impact:** Operator cannot perform goods receipts or issue material tags.

---

## 2. Investigation Protocol & Diagnostic Steps

1. **Verify API Reachability & Health:**
   ```bash
   curl -s http://localhost:5000/api/health
   # Expected: {"status":"UP","database":{"status":"HEALTHY"}}
   ```
2. **Inspect Authentication Audit Logs (`APP_AUDIT_EVENT`):**
   ```sql
   SELECT ID, EVENT_TYPE, ACTOR, DETAILS_JSON, CREATED_AT
     FROM APP_AUDIT_EVENT
    WHERE ACTOR = 'warehouse01' AND EVENT_TYPE LIKE 'AUTH_%'
    ORDER BY CREATED_AT DESC FETCH FIRST 5 ROWS ONLY;
   ```
   *Finding:* Rows report `AUTH_LOGIN_FAILED` with details `{"reason":"invalid_credentials"}`.
3. **Verify Account Status & Hash in `APP_USER`:**
   ```sql
   SELECT USERNAME, IS_ACTIVE, PASSWORD_ITERATIONS, SUBSTR(PASSWORD_HASH, 1, 30) AS HASH_PREFIX
     FROM APP_USER
    WHERE USERNAME = 'warehouse01';
   ```
   *Finding:* `IS_ACTIVE = 1`, but `PASSWORD_HASH` reflects an unmigrated legacy salt.

---

## 3. Root Cause Analysis (RCA)
The user's account password was updated during an offline password rotation, but the user attempted authentication using their previous pre-cutover password. Furthermore, caps-lock on the handheld terminal caused casing mismatch against PBKDF2 hash evaluation.

---

## 4. Resolution Procedure

1. **Reset Password with Canonical PBKDF2 Hash:**
   ```sql
   -- Reset password to standard canonical credential: Warehouse@123
   UPDATE APP_USER
      SET PASSWORD_HASH = 'pbkdf2$210000$ndgEAurF27OchO3DwWmuDQ==$z4sgk4650iW8vCF1Uap12L5ohbBytqKrxReXSzUrT6o=',
          PASSWORD_ITERATIONS = 210000,
          IS_ACTIVE = 1
    WHERE USERNAME = 'warehouse01';
   COMMIT;
   ```
2. **Verify User Login:**
   ```bash
   curl -X POST http://localhost:5000/api/auth/login \
     -H "Content-Type: application/json" \
     -d '{"username":"warehouse01","password":"Warehouse@123"}'
   # Returns 200 OK with accessToken
   ```

---

## 5. Prevention & System Hardening
- Implement self-service password reset with SMS/email OTP.
- Add visual caps-lock indicators on handheld login screens.
- Enforce periodic token refresh rotation instead of frequent full logins.
