# ADR-002: Authentication & Credential Storage Strategy

## Status
**Accepted** (2026-09-24)

## Context
Factory floor systems require secure, tamper-evident credentials while accommodating rugged handheld barcode scanners with intermittent wireless connectivity. We evaluated:
1. Basic HTTP Authentication.
2. External Identity Provider (Keycloak / Auth0 / Okta).
3. Native ASP.NET Core PBKDF2 Password Hashing + JWT Bearer Tokens with Refresh Rotation.

## Decision
We adopted **RFC 2898 PBKDF2 Password Hashing (210,000 iterations, HMAC-SHA256, 16-byte random salt) combined with Bearer JWT tokens and in-memory refresh rotation**.

## Rationale
- **Zero External Identity Dependency:** Plant operations must not halt if an external SaaS identity provider (Auth0/Okta) experiences Internet outages.
- **Enterprise Cryptography:** 210,000 PBKDF2 iterations exceed OWASP standards and resist GPU dictionary attacks.
- **Stateless Verification:** Handheld scanners authenticate once and attach Bearer tokens to subsequent barcode scan requests without repeated database credential lookup overhead.

## Consequences
- **Positive:** High resilience, zero third-party license costs, instant local validation.
- **Negative:** Refresh token store in single-instance memory; multi-node scaling would require shared Redis cache.
