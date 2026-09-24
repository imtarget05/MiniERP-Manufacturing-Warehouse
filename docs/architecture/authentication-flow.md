# Architecture: Authentication & Token Lifecycle Flow
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** ARC-AUT-005  

---

## 1. Authentication Lifecycle Sequence

```mermaid
sequenceDiagram
    autonumber
    actor Client as Floor Scanner / Web Client
    participant Auth as POST /api/auth/login
    participant Service as TokenService & PasswordHashService
    participant Store as RefreshTokenStore
    participant DB as Oracle Database (APP_USER)

    Client->>Auth: Submit { username, password }
    Auth->>DB: Query user salt, hash, iterations, active status
    DB-->>Auth: User record
    Auth->>Service: PBKDF2 verification (210,000 rounds)
    alt Valid Credentials
        Service-->>Auth: Hash Verified OK
        Auth->>Service: Generate Bearer Access Token (Expiry: 60m)
        Auth->>Store: Store Refresh Token mapping
        Auth->>DB: Record LAST_LOGIN_AT & APP_AUDIT_EVENT
        Auth-->>Client: 200 OK { accessToken, refreshToken, userDto }
    else Invalid Credentials
        Auth->>DB: Record AUTH_LOGIN_FAILED audit event
        Auth-->>Client: 401 Unauthorized
    end

    opt Token Refresh (When Access Token is near expiry)
        Client->>Auth: POST /api/auth/refresh { refreshToken }
        Auth->>Store: Validate refresh token validity
        Store-->>Auth: Token valid, user identified
        Auth->>Service: Generate fresh Access Token
        Auth-->>Client: 200 OK { accessToken, refreshToken }
    end
```

---

## 2. Token Security Characteristics

- **Signature Algorithm:** HMAC-SHA256 with 256-bit symmetric key.
- **Claims Expose:** `sub` (User ID), `unique_name` (Username), `department`, `role` (Array of role names).
- **Transport Security:** Must be transmitted via `Authorization: Bearer <token>` over TLS/HTTPS.
