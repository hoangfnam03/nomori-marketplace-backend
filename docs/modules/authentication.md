# Authentication Module

## Status

Backend MVP implemented. Angular integration, email delivery, MFA and external login are deferred.

## Business flow

1. Register validates email/password, hashes the password with PBKDF2 and creates the customer plus password record in one transaction.
2. The customer is assigned to the seeded `Registered` role.
3. Login validates account state and password, updates login metadata and issues an HttpOnly cookie.
4. Session reads the authenticated customer identity from the cookie claims; logout clears the cookie.
5. Password change and one-time recovery reset create new password records; failed logins use configurable lockout thresholds.

## Database

- `Customer`
- `CustomerPassword`
- `CustomerRole`
- `CustomerCustomerRoleMapping`

Migrations:

- `202609080002 AuthenticationIdentityMigration`
- `202609080003 SeedAuthenticationRolesMigration`
- `202609080004 PasswordRecoveryMigration`
- `202609080005 PermissionMigration`

Indexes and constraints include unique customer email/guid, unique role system name, password history index, customer-role composite key and foreign keys.

## API

- `POST /api/v1/auth/register`
- `POST /api/v1/auth/login`
- `POST /api/v1/auth/logout`
- `GET /api/v1/auth/session`
- `GET /api/v1/auth/csrf`
- `GET /api/v1/auth/permissions`
- `POST /api/v1/auth/password/change`
- `POST /api/v1/auth/password/forgot`
- `POST /api/v1/auth/password/reset`

Credentials are not returned or logged. Login failures use a generic `auth.invalid_credentials` error. Cookie authentication is configured in Web Framework.
State-changing endpoints require the `X-CSRF-TOKEN` header paired with the CSRF cookie. Auth endpoints use a fixed-window IP rate limit.

## Tests and validation

- Core entity tests
- Authentication service tests for hashing, email normalization and invalid password
- API controller tests
- LocalDB migration execution
- HTTP smoke test for register/login/session/logout
- HTTP smoke test for CSRF, password recovery/reset and permissions

## Deferred work

- Email delivery for recovery instructions
- Fine-grained permission attributes/policies on future module endpoints
- Email verification, MFA, OTP and external authentication
- Angular auth facade/forms/guards
