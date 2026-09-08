# Authentication Module

## Status

Backend MVP implemented. Angular integration, password recovery, MFA and external login are deferred.

## Business flow

1. Register validates email/password, hashes the password with PBKDF2 and creates the customer plus password record in one transaction.
2. The customer is assigned to the seeded `Registered` role.
3. Login validates account state and password, updates login metadata and issues an HttpOnly cookie.
4. Session reads the authenticated customer identity from the cookie claims; logout clears the cookie.

## Database

- `Customer`
- `CustomerPassword`
- `CustomerRole`
- `CustomerCustomerRoleMapping`

Migrations:

- `202609080002 AuthenticationIdentityMigration`
- `202609080003 SeedAuthenticationRolesMigration`

Indexes and constraints include unique customer email/guid, unique role system name, password history index, customer-role composite key and foreign keys.

## API

- `POST /api/v1/auth/register`
- `POST /api/v1/auth/login`
- `POST /api/v1/auth/logout`
- `GET /api/v1/auth/session`

Credentials are not returned or logged. Login failures use a generic `auth.invalid_credentials` error. Cookie authentication is configured in Web Framework.

## Tests and validation

- Core entity tests
- Authentication service tests for hashing, email normalization and invalid password
- API controller tests
- LocalDB migration execution
- HTTP smoke test for register/login/session/logout

## Deferred work

- Password change and forgot/reset password
- CSRF strategy for cookie-based state changes
- Rate limiting and lockout threshold policy
- Permission records and role authorization policies
- Email verification, MFA, OTP and external authentication
- Angular auth facade/forms/guards
