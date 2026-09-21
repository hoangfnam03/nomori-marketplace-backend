# Authorization / Permissions Module

## Status

MVP implemented for backend policy enforcement and minimal admin role management. Resource-level ACL and permission caching are deferred.

## Scope

- Stable permission codes owned by the Core security boundary.
- SQL-backed role-to-permission mapping for the authenticated customer.
- Dynamic ASP.NET Core policies that enforce a permission on API endpoints.
- `401` for an unauthenticated request and `403` for an authenticated customer without the required permission.

Out of scope: admin CRUD for roles/permissions, resource/store ACL, external identity claims and frontend security decisions.

## Permission catalog

- `auth.authenticated`: marker permission for an authenticated customer.
- `auth.permissions.read`: allows reading the current customer's effective permission codes.
- `admin.access`: allows access to the administration boundary.
- `admin.roles.read`: allows reading active roles and a customer's roles.
- `admin.roles.manage`: allows replacing a customer's role assignment.
- `admin.audit.read`: allows reading recent security audit entries.

The `Registered` role receives the two customer permissions, while `Administrator` receives those plus the admin permissions, through migration `202609210001`. The migration uses stable system names and is idempotent; it does not depend on identity values.

## API boundary

- `GET /api/v1/auth/permissions`
  - Requires the `auth.permissions.read` policy.
  - Returns only effective permission codes.
  - Missing cookie: `401 Unauthorized`.
  - Authenticated customer without the permission: `403 Forbidden`.

The backend remains the authorization boundary. Angular may use the permission response to improve navigation UX but must not use it as a security control.

### Admin role management

- `GET /api/v1/admin/authorization/roles` requires `admin.roles.read`.
- `GET /api/v1/admin/authorization/customers/{customerId}/roles` requires `admin.roles.read`.
- `PUT /api/v1/admin/authorization/customers/{customerId}/roles` requires `admin.roles.manage` and CSRF protection.
- `GET /api/v1/admin/authorization/audit-logs` requires `admin.audit.read`.

An administrator cannot remove their own `Administrator` role. The API validates that the target customer exists and that every assigned role is active.

## Implementation

- `PermissionCodes` defines stable codes without HTTP dependencies.
- `HasPermissionAttribute` converts a code to a dynamic authorization policy.
- `PermissionAuthorizationPolicyProvider` creates a policy requiring authentication and the requested permission.
- `PermissionAuthorizationHandler` resolves the customer id from the authenticated cookie subject and asks `IPermissionService` for the effective permission.
- `PermissionService` delegates role mapping queries to the Data layer.
- `AuthorizationManagementService` owns role assignment rules; `SqlAuthorizationStore` owns the transaction that replaces mappings.

## Validation

```powershell
dotnet test Nomori.Marketplace.sln
dotnet run --project src/Nomori.Marketplace.DbMigrator/Nomori.Marketplace.DbMigrator.csproj
```

Manual check: login as a registered customer, call `GET /api/v1/auth/permissions`, then remove the permission mapping and verify `403` while an anonymous request remains `401`.

## Deferred work

- Permission cache with invalidation on role mapping changes.
- Resource-level and store-level authorization.
- Permission catalog registration owned by future feature modules.
