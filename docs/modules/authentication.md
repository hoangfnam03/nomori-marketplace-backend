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

## Manual API test scenarios

Use the HTTPS profile for local testing:

```text
Base URL: https://localhost:7014
Swagger UI: https://localhost:7014/swagger/index.html
OpenAPI JSON: https://localhost:7014/openapi/v1.json
```

The development HTTPS certificate may need to be trusted in the browser. In Swagger UI, call `GET /api/v1/auth/csrf` first. Keep both the `Nomori.Csrf` cookie and the returned `token`; every state-changing request below must send the token in the `X-CSRF-TOKEN` header. The browser should retain the authentication cookie returned by login.

### Postman Desktop setup

1. Send `GET https://localhost:7014/api/v1/auth/csrf`.
2. Open the response cookies for `localhost` and confirm that `Nomori.Csrf` exists. Keep Postman's cookie jar enabled; the token header alone is not sufficient.
3. Copy the `token` value from the JSON response body, without quotes or the `< >` placeholder characters.
4. On each state-changing request, add a header named `X-CSRF-TOKEN` whose value is the copied token. Postman must also send the `Nomori.Csrf` cookie automatically for the same `https://localhost:7014` host.
5. After login, confirm that Postman also stores and sends the `Nomori.Auth.Development` cookie.

If the CSRF request was sent to a different host, port or scheme, use the cookie manager to add/correct the cookie under `localhost` for this exact domain and port, or request a new token. Sending only `X-CSRF-TOKEN`, sending the literal text `<csrf-token>`, or losing the `Nomori.Csrf` cookie results in `400 Bad Request`.

Use these test values and do not use a real email address or real password:

```json
{
	"email": "qa.auth@example.test",
	"password": "Password!123",
	"newPassword": "Password!456"
}
```

### 1. Get CSRF token

```http
GET /api/v1/auth/csrf
```

Expected: `200 OK`, response contains a non-empty `token`, and the response sets the `Nomori.Csrf` cookie.

### 2. Register a new customer

```http
POST /api/v1/auth/register
Content-Type: application/json
X-CSRF-TOKEN: <csrf-token>

{
	"email": "qa.auth@example.test",
	"password": "Password!123"
}
```

Expected: `201 Created`, response contains a generated `customerId` and the normalized email `qa.auth@example.test`. The customer is assigned to the seeded `Registered` role.

### 3. Reject duplicate registration

Repeat the registration request with the same email.

Expected: `409 Conflict` with `ProblemDetails`; the response must not expose credentials or password data.

### 4. Login and read the session

```http
POST /api/v1/auth/login
Content-Type: application/json
X-CSRF-TOKEN: <csrf-token>

{
	"email": "QA.Auth@Example.Test",
	"password": "Password!123",
	"rememberMe": false
}
```

Expected: `204 No Content`, a `Nomori.Auth.Development` authentication cookie is returned, and email normalization still finds the registered customer.

Then call:

```http
GET /api/v1/auth/session
```

Expected while the login cookie is retained: `200 OK` with `isAuthenticated: true`, the generated `customerId` and `email: "qa.auth@example.test"`.

### 5. Read permissions as an authenticated customer

```http
GET /api/v1/auth/permissions
```

Expected: `200 OK` with a `permissions` array. Calling this endpoint without the authentication cookie must return `401 Unauthorized`.

### 6. Reject invalid credentials and verify lockout

Send the login request with `Password!wrong` five times.

Expected: each response is `401 Unauthorized` with the generic error code `auth.invalid_credentials`. On the fifth failure, the account is locked for 15 minutes. A valid password during the lockout must still return the same generic `401` response.

### 7. Change password

While authenticated, call:

```http
POST /api/v1/auth/password/change
Content-Type: application/json
X-CSRF-TOKEN: <csrf-token>

{
	"currentPassword": "Password!123",
	"newPassword": "Password!456"
}
```

Expected: `204 No Content`. The current session requires login again; login with `Password!456` should succeed after the old session is cleared or re-authenticated.

### 8. Recover and reset a password in Development

First request a recovery token:

```http
POST /api/v1/auth/password/forgot
Content-Type: application/json

{
	"email": "qa.auth@example.test"
}
```

Expected in `Development`: `200 OK` with a `token` field. Copy that token immediately; it expires after 30 minutes.

Reset the password with the token:

```http
POST /api/v1/auth/password/reset
Content-Type: application/json
X-CSRF-TOKEN: <csrf-token>

{
	"token": "<token-from-forgot-response>",
	"newPassword": "Password!789"
}
```

Expected: `204 No Content`. Reusing the same token must return `401 Unauthorized`. An unknown or expired token must also return `401 Unauthorized`.

### 9. Logout and verify session state

```http
POST /api/v1/auth/logout
X-CSRF-TOKEN: <csrf-token>
```

Expected: `204 No Content`, the authentication cookie is cleared, and a subsequent `GET /api/v1/auth/session` returns:

```json
{
	"isAuthenticated": false,
	"customerId": null,
	"email": null
}
```

### 10. CSRF and input validation checks

- Repeat register, login, logout, password change and password reset without `X-CSRF-TOKEN`: expected `400 Bad Request`.
- Register with `{ "email": "not-an-email", "password": "Password!123" }`: expected `400 Bad Request` with a validation error.
- Register or login with an empty email or password: expected `400 Bad Request` with `credentials: ["Email and password are required."]`.
- Call an auth endpoint repeatedly beyond the configured fixed window: expected `429 Too Many Requests`.

## Deferred work

- Email delivery for recovery instructions
- Fine-grained permission attributes/policies on future module endpoints
- Email verification, MFA, OTP and external authentication
- Angular auth facade/forms/guards
