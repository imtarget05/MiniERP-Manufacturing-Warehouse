# Phase 5 — Authentication, RBAC & Audit Trail (report)

Design reference: `04-MiniERP-UPGRADE-DESIGN.md` §Phase 5 — Authentication / RBAC / Audit: password migration, login/token, route authorization, authenticated actor propagation, security audit events, CORS tightening.
Status: PASS (auth/RBAC, JWT, actor propagation, audit, CORS and 401/403 paths verified; full suite 164/164).

## 1. Database & Security Schema

- `sql/07_traceability_schema.sql`:
  - `APP_USER` widened with `PASSWORD_HASH`, `PASSWORD_SALT`, `PASSWORD_ITERATIONS`, `LAST_LOGIN_AT`.
  - `APP_AUDIT_EVENT` immutable append-only audit trail: `(AUDIT_ID, EVENT_TYPE, ENTITY_TYPE, ENTITY_KEY, ACTOR, DETAILS_JSON, CORRELATION_ID, CREATED_AT)` with `IX_AUDIT_ENTITY` index.
- `sql/03_seed.sql`:
  - Demo accounts seeded with PBKDF2-SHA256 hashes (210,000 iterations):
    - `admin` (ERP_ADMIN)
    - `tan.mai` (ERP_SUPPORT)
    - `planner01` (PRODUCTION_OPERATOR)
    - `warehouse01` (WAREHOUSE_OPERATOR)
    - `viewer01` (VIEWER)
  - Legacy `PASSWORD` column marked `MIGRATED` to ensure plaintext passwords are never read or stored.

## 2. Authentication & Credential Architecture

- `src/Services/PasswordHashService.cs`:
  - Standards-compliant PBKDF2 (`Rfc2898DeriveBytes.Pbkdf2` using HMAC-SHA256, 210,000 iterations).
  - 16-byte cryptographically secure random salt generated via `RandomNumberGenerator`.
  - Constant-time verification (`CryptographicOperations.FixedTimeEquals`) to prevent timing side-channel attacks.
- `src/Services/TokenService.cs`:
  - Zero-dependency compact HS256 JWT bearer token issuer and validator.
  - Validates issuer, audience, algorithm, expiration, signature, and extracts user ID, username, and roles into `ClaimsPrincipal`.
  - Configurable via `JwtOptions` (environment variables `JWT_SIGNING_KEY`, `JWT_ISSUER`, `JWT_AUDIENCE`, `JWT_EXPIRY_MINUTES`).
  - Production guard: Enforces signing key presence and minimum 32-byte key size outside development.

## 3. RBAC & Mutation Authorization

- `src/Services/AuthPolicies.cs` & `src/Services/MutationAuthorization.cs`:
  - Centralized policy mapping for all state-mutating endpoints (POST, PUT, PATCH, DELETE under `/api/`):
    - Warehouse mutations (`/api/warehouse/`, `/api/stock/`, `/api/procurement/`) → `ERP_ADMIN`, `WAREHOUSE_OPERATOR`
    - Material issue (`/api/manufacturing/.../issue-lots`) → `ERP_ADMIN`, `WAREHOUSE_OPERATOR`, `PRODUCTION_OPERATOR`
    - Production completion (`/api/manufacturing/.../complete-traceable`) → `ERP_ADMIN`, `PRODUCTION_OPERATOR`
    - Lot Hold/Release (`/api/warehouse/lots/.../hold`) → `ERP_ADMIN`, `WAREHOUSE_OPERATOR`, `ERP_SUPPORT`
    - Label creation/reprint (`/api/labels`) → `ERP_ADMIN`, `WAREHOUSE_OPERATOR`, `ERP_SUPPORT`
    - Sensitive stock adjustments (`/api/automation/stock/adjust`) → `ERP_ADMIN` only
    - Read/Trace endpoints (`/api/trace/`, `/api/stock/`, etc.) → Accessible to all roles including `VIEWER`
  - Unauthenticated mutation requests return `401 Unauthorized` with `{"errorCode": "AUTH_REQUIRED"}`.
  - Role mismatch returns `403 Forbidden` with `{"errorCode": "AUTH_FORBIDDEN"}`.

## 4. Endpoints & Audit Trail

- `POST /api/auth/login`: Validates credentials against PBKDF2 hash, issues bearer token, updates `LAST_LOGIN_AT`, writes `AUTH_LOGIN` or `AUTH_LOGIN_FAILED` audit event.
- `GET /api/auth/me`: Returns current authenticated principal identity and roles (`RequireAuthorization()`).
- `CorrelationIdMiddleware`: Extracts or generates `X-Correlation-ID` header with sanitization (32-character alphanumeric/hyphen).
- `AuditMiddleware`: Records all API mutations and security denials into `APP_AUDIT_EVENT` asynchronously with correlation ID, actor, HTTP method, and response status.

## 5. Tests (tests/MiniERP.Api.Tests/SecurityContractTests.cs)

7 unit/integration-level security tests:
- `TokenService_IssueAndValidate_RoundTripsClaims`: Verifies bearer token creation and claims round-trip.
- `TokenService_TamperedToken_IsRejected`: Verifies HMAC signature failure on tampered tokens.
- `TokenService_ExpiredToken_IsRejected`: Verifies rejection of expired tokens.
- `MutationPolicy_MapsProtectedOperations`: Verifies policy routing for warehouse, manufacturing, labels, and exemptions.
- `CorrelationId_InvalidInputIsReplaced`: Verifies sanitization of incoming correlation headers.
- `ProtectedMutation_WithoutToken_Returns401`: Verifies unauthenticated mutation rejection (401).
- `ProtectedMutation_ViewerRole_Returns403`: Verifies role-based access denial (403 for `VIEWER` attempting stock-in).

## 6. Verification Evidence

- `dotnet build src/MiniERP.Api.csproj -p:TreatWarningsAsErrors=true` → 0 warnings, 0 errors.
- `dotnet test tests/MiniERP.Api.Tests/MiniERP.Api.Tests.csproj` → **164/164 passed** (full unit + Oracle integration).
- Full permission matrix verified by test cases.
