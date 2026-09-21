# Customer / Profile Module

## Status

**M04 is partially complete.** Profile, Address Book, customer preferences and verified email change are implemented for Backend and Angular. GDPR consent, export and deletion remain deferred.

The implemented profile slice is ready for manual testing and is a valid dependency for later Catalog work. Do not mark the whole M04 module as complete until its deferred Address, Attributes and GDPR slices are implemented.

## Audit summary

Completed in the Profile first slice:

- Own-profile read and update; the API never accepts a customer ID from the client.
- Customer fields: first name, last name, gender, date of birth and phone.
- Read-only identity information: email, username and email-verification state.
- Migration `202609210003`, permissions, backend validation, CSRF protection and a privacy-safe audit event.
- Angular route guard, loading/error/save states, reusable Nomori account/auth styling and header navigation.
- Unit tests for profile validation/audit behavior and migration version.

Still required for full M04:

- GDPR consent, export and deletion workflows.
- Dedicated API integration tests and browser E2E tests for this endpoint/page.

## Scope

Included:

- Read the authenticated customer's own profile.
- Update first name, last name, gender, date of birth and phone.
- Show email, username and email verification status as read-only identity data.
- Backend validation, CSRF protection, permission policies and audit logging.
- Angular lazy route `/customer/profile` using the existing Nomori auth/account visual language.

Deferred:

- Email change and re-verification flow.
- Address book and checkout addresses.
- Customer attributes/custom fields.
- GDPR consent/export/delete.
- Newsletter, reward points and order history.
- Admin customer CRUD.

## Source map from nopCommerce

The implementation was adapted from:

- `Nop.Web.Models.Customer.CustomerInfoModel` for profile field semantics.
- `CustomerController.Info` for the update boundary and field behavior.
- `CustomerModelFactory.PrepareCustomerInfoModelAsync` for read-model separation.
- `Nop.Core.Domain.Customers.Customer` for customer-owned identity/profile fields.

Nomori does not copy MVC views or nopCommerce's settings/newsletter/address orchestration into the API module.

## Data model

Migration `202609210003 CustomerProfileMigration` adds these nullable columns to `Customer`:

- `FirstName` (`nvarchar(100)`)
- `LastName` (`nvarchar(100)`)
- `Gender` (`nvarchar(20)`)
- `DateOfBirth` (`datetime2`)
- `Phone` (`nvarchar(32)`)

The first slice keeps these fields on `Customer` because they are one-to-one identity/profile data. A separate profile table can be introduced later if the profile grows substantially.

## Business rules

- A customer can read or update only their own profile.
- Email, username and email verification state are read-only in this slice.
- First and last name are optional and limited to 100 characters.
- Gender accepts `male`, `female`, `other` or `unspecified`.
- Date of birth must be between `1900-01-01` and the current UTC date.
- Phone accepts digits, spaces, `+`, `-`, parentheses and is limited to 32 characters.
- Successful updates record `customer.profile_updated` with changed field names only; personal values are not copied into audit metadata.

## API

```text
GET /api/v1/customer/profile
PUT /api/v1/customer/profile
```

`GET` requires `customer.profile.read`. `PUT` requires `customer.profile.manage`, authentication and CSRF protection.

```json
{
  "firstName": "Mai",
  "lastName": "Nomori",
  "gender": "unspecified",
  "dateOfBirth": "1995-04-12",
  "phone": "+84 901 234 567"
}
```

Failure responses use `ProblemDetails`/`ValidationProblemDetails`: anonymous `401`, insufficient permission `403`, invalid field `400`, and missing customer `404`.

## Angular

```text
src/app/customer/customer.routes.ts
src/app/customer/pages/profile.page.ts
src/app/core/customer/customer-profile-api.service.ts
src/app/core/customer/customer-profile.models.ts
```

The route is protected by `authGuard`. The page reuses `.account-page`, `.auth-panel`, `.field`, `.submit`, `.form-error`, `.form-success` and the existing typography/color variables used by the Account and Auth pages. It has loading, save, validation, success and error states.

## Validation

```powershell
dotnet run --project src/Nomori.Marketplace.DbMigrator/Nomori.Marketplace.DbMigrator.csproj
dotnet test Nomori.Marketplace.sln

cd ..\nomori-marketplace-frontend
npm run typecheck
npm run build
```

Manual smoke test: verify and login, open `/customer/profile`, load the profile, save valid data, try invalid date/gender/phone values, test anonymous `401`, then inspect `customer.profile_updated` in the admin audit endpoint.

## Manual test guide

### Preconditions

1. Run migration `202609210003` and restart the API.
2. Register, verify the email and log in as a `Registered` customer.
3. Confirm `GET /api/v1/auth/session` returns `isAuthenticated: true`.

### UI test

1. From the signed-in header, select **Profile** (or open `/customer/profile`).
2. Confirm email, verification and username are shown but cannot be edited.
3. Enter a first name, last name, gender, date of birth and phone; select **Save profile**.
4. Refresh the page. The values must remain.
5. Enter a phone containing letters, or use a future date through an API client. The page must show the backend field error and must not report success.
6. Select **Customer settings**. Add an address, mark it default, edit it and delete it. Only the signed-in customer's addresses are returned.
7. Save the supplied preferences. The initial definitions are `preferred_language` and `marketing_opt_in`; future definitions can be added in the database without changing the API contract.
8. Request an email change with a new email and the current password. Open the link sent to the new email. The customer must sign in again using the new email; the old email must no longer work.

### API and authorization test

```http
GET /api/v1/customer/profile
PUT /api/v1/customer/profile
X-CSRF-TOKEN: <token>
```

`GET` and `PUT` return `200` for the current customer with the required permission. Anonymous access returns `401`; a signed-in account without the permission receives `403`; invalid input receives `400 ValidationProblemDetails`. The migration assigns both permissions to `Registered` and `Administrator` by default.

### How it works

**Backend:** the authentication cookie establishes the current customer. The permission policy protects the controller; the controller sends a customer-owned command to `CustomerProfileService`; the service normalizes and validates values, persists only profile columns and writes `customer.profile_updated` without storing personal values in audit metadata.

**Frontend:** `authGuard` prevents anonymous navigation. `ProfilePage` loads data through `CustomerProfileApiService`, patches the reactive form, and sends the update through the existing credential/CSRF interceptor. It renders API validation errors beside the affected input and displays a success state only after the API returns `200`.

### Audit-log check

As an administrator with `admin.audit.read`, call:

```http
GET /api/v1/admin/authorization/audit-logs?take=100
```

After a successful profile update, verify an event named `customer.profile_updated`. Its details should contain changed field names only, not the entered values.

## Rollout

Run the migrator before starting the API. Existing customers receive null profile fields and remain compatible with the auth schema. The `Registered` and `Administrator` roles receive the two profile permissions through the same migration.
