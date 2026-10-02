# F07-C Countries, states and address rules

| | |
|---|---|
| **Module ID** | F07-C (slice of F07 "Localization, directory, currency, measures and time") |
| **Status** | Backend and Angular implemented. SQL stores and migration not yet run against a real database. |
| **Branch** | `feat/directory/foundation` (backend and frontend) |
| **Depends on** | F03 (permissions), F04 (address book), F07-A (`settings.manage`) |
| **Unblocks** | F17 (shipping and billing address at checkout), F18 (address snapshot on orders), F20 (shipping zones by country and state), F07-E (tax by country and state), F22 (address in emails) |
| **Source PRD** | M04 customer profile: "Address book and checkout addresses" |

## 1. Purpose

The address book (F04) accepted any two letters as a country and any text as a state, with no postal-code check. Checkout, shipping and tax all need to know **which countries the platform serves, which states they have, and what a valid address looks like there**. This slice adds the country and state directory with per-country rules, and makes the address book use it.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Country code** is ISO 3166-1 alpha-2 (two letters, upper case) and never changes. An optional alpha-3 code is kept for later integrations. |
| D2 | A country is **published** (customers may pick it) and **allows billing**, **allows shipping**, or both. Unpublished or both-off countries cannot be chosen for a new or edited address; addresses already saved keep their country. |
| D3 | If a country has **published states**, an address in it must name one of them (by id). If it has none, the state is optional free text. |
| D4 | The address stores the **country code** and a **state name snapshot** (and the state id when chosen). Renaming or deleting a state later never changes the text on saved addresses or, later, on orders. |
| D5 | A country can require a **postal code**, and can have a **pattern** (regular expression). The pattern is only trusted from an administrator, is compiled when saved and runs with a short time limit, so a bad pattern cannot slow the API down. |
| D6 | The migration seeds a **curated list** of about 40 countries (with postal rules for the ones whose format is well known) and no states. Administrators add the rest; a full ISO list and state lists are an import job (F27). |
| D7 | Address validation reads the directory on every save; the country list is small, so there is no cache yet. |

## 3. Actors and authorization matrix

| Action | Customer (own addresses) | Platform admin (`settings.manage`) | Anyone |
|---|---|---|---|
| Read published countries and their states | | | Yes (public) |
| Save an address that follows the directory rules | Yes | | |
| Create, edit, delete countries and states | | Yes | No |

## 4. Included behavior

- **Country** fields: code, alpha-3, name (1 to 100), published, allows billing, allows shipping, postal code required, postal code pattern (max 200 characters), display order.
- **State or province** fields: code (1 to 20, unique inside the country, case-insensitive), name (1 to 100), published, display order.
- **Admin rules:** a country code must be two letters and unique (`409 country.code_exists`); the pattern must be a valid regular expression (`400 errors.postalCodePattern`); a pattern requires the postal code to be marked required. A country or state **used by a saved address cannot be deleted** (`409 country.in_use`, `409 state.in_use`): unpublish it instead. Deleting a country deletes its unused states. A state code is unique per country (`409 state.code_exists`).
- **Address validation** (every save of the address book):
  - the country exists, is published and allows billing or shipping (`400 errors.countryCode`);
  - the country has published states: `stateProvinceId` is required and must belong to the country, and the stored state name is the state's name; otherwise `stateProvinceId` must be empty and the free text state (max 100) is kept (`400 errors.stateProvinceId`);
  - postal code: required when the country says so, at most 20 characters, and it must match the pattern when there is one (`400 errors.zipPostalCode`);
  - the existing rules for names, address lines, city and phone stay.
  - Existing addresses are not re-checked until they are edited.
- **Address use** is part of the rule set so checkout can ask for a shipping or a billing country (`CanShipTo`, `CanBillTo`); the address book accepts a country that allows either.
- **Public API** returns published countries (with `hasStates`, `postalCodeRequired`, and the postal pattern as a hint for forms) and the published states of a country. The server remains the judge; the form only helps.
- **Audit:** `country.created`, `country.updated`, `country.deleted`, `state.created`, `state.updated`, `state.deleted` (ids and codes only).
- **Angular:** the address form in Customer settings gets a country list, a state list that appears only when the country has states (otherwise a text field), address line 2, company and a postal code field that shows whether it is required. A new admin page `/admin/countries` manages countries and their states.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Full ISO country list, state lists, bulk import | F27 |
| Address formatting per country (line order, labels such as "county" or "prefecture") | F18 / F22 |
| Address autocomplete, geocoding, address verification services | F28 |
| Shipping zones and rates by country and state | F20 |
| Tax rates, VAT numbers and EU rules by country | F07-E |
| Localized country and state names | F07-D |
| Time zone and currency defaults per country | F07-B |
| Address attributes configured by an administrator, address of vendors and applications | Later |
| Dialing codes and phone number validation per country | F07-C follow-up |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Directory/Country` (`TwoLetterIsoCode`, `ThreeLetterIsoCode`, `AllowsBilling`, `AllowsShipping`, `Published`, `DisplayOrder`) | Country | Same flags; postal rules added |
| `Domain/Directory/StateProvince` (`Abbreviation`, `Published`, `DisplayOrder`) | State | Same; `Code` unique per country |
| `Nop.Services/Directory/CountryService`, `StateProvinceService` | Services | One `IDirectoryService` |
| `AddressSettings` (state required, zip required) and address validators | Rules | State required when states exist; zip required and pattern per country |
| `Domain/Common/Address` (country and state ids) | Address | Country code plus state name snapshot |

## 7. Data model

Migration `202610120001 DirectoryMigration`:

**`Country`**: `Id` identity, `Code char(2)` unique, `Alpha3 char(3) NULL`, `Name nvarchar(100)`, `Published`, `AllowsBilling`, `AllowsShipping` (default true), `PostalCodeRequired` (default false), `PostalCodePattern nvarchar(200) NULL`, `DisplayOrder`, `CreatedOnUtc`, `UpdatedOnUtc`.

**`StateProvince`**: `Id` identity, `CountryId` (FK to `Country`, cascade), `Code nvarchar(20)`, `Name nvarchar(100)`, `Published`, `DisplayOrder`; unique index `(CountryId, Code)`.

**`CustomerAddress`**: new `StateProvinceId int NULL` (FK to `StateProvince`, no action, so a used state cannot be removed by accident).

Seed: about 40 countries (Vietnam first in display order), postal rules for US, GB, CA, DE, FR, IT, ES, NL, JP, KR, CN, IN, SG, AU. No states. Existing addresses keep their country code even if it is not in the seed.

`Down()` drops the new column and both tables.

## 8. Use cases and service contracts

```text
PostalCodeRules (pure)       IsValidPattern(pattern), Matches(pattern, code)       (timeout, never throws)
IDirectoryStore              countries and states: lists, get, insert, update, delete, in-use checks
IDirectoryService            GetCountries, GetStates (public); country and state CRUD (admin);
                             ResolveAddressAsync(countryCode, stateProvinceId, stateText, postalCode, AddressUse) -> errors + normalized values
CustomerAccountDataService   SaveAddress now calls ResolveAddressAsync
CustomerAddress              + StateProvinceId
```

Business codes (`409`): `country.code_exists`, `country.in_use`, `state.code_exists`, `state.in_use`. Field errors (`400`): `errors.code`, `errors.alpha3`, `errors.name`, `errors.postalCodePattern`, `errors.countryCode`, `errors.stateProvinceId`, `errors.zipPostalCode`. Not found `404`.

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/directory/countries` | Public, published only |
| 2 | `GET` | `/api/v1/directory/countries/{code}/states` | Public, published states of a published country |
| 3 | `GET`/`POST` | `/api/v1/admin/directory/countries` | `settings.manage` |
| 4 | `PUT`/`DELETE` | `/api/v1/admin/directory/countries/{id}` | `settings.manage` |
| 5 | `GET`/`POST` | `/api/v1/admin/directory/countries/{id}/states` | `settings.manage` |
| 6 | `PUT`/`DELETE` | `/api/v1/admin/directory/states/{id}` | `settings.manage` |
| 7 | `POST`/`PUT` | `/api/v1/customer/addresses`, `/{id}` | body gains `stateProvinceId`; answers gain it too |

## 10. Angular

- `core/directory/directory-api.service.ts` (public and admin), models.
- `customer/pages/account-data.page.ts`: address form with the directory.
- `admin/pages/admin-countries.page.ts`, route `/admin/countries` guarded by `settings.manage`, link in the shell.
- States: loading, empty, validation per field, conflicts (in use, duplicate code), network error.

## 11. Events, jobs, cache

None. Directory reads are small; cache them (with invalidation on admin changes) if they show up in a profile.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Postal rules | Valid and invalid examples for several patterns; an invalid pattern is reported not thrown; a slow pattern times out safely |
| Countries | Code shape and uniqueness; alpha-3; name; pattern validity and its link to "required"; delete refused when used and allowed otherwise; public list only published |
| States | Code and name rules; unique per country; delete refused when used; public list only published states of published countries |
| Address | Unknown, unpublished and billing-and-shipping-off countries refused; state required when states exist and must belong to the country; free text kept otherwise; state name snapshot; postal required, length, pattern; billing and shipping use rules |
| Compatibility | Names, lines, city and phone rules unchanged |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL, HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check `Country` (about 40 rows) and the new `CustomerAddress.StateProvinceId`.
2. As admin open `/admin/countries`; add states `CA` and `NY` to United States.
3. In Customer settings add an address in the United States: the state list appears and is required; zip `1234` is refused, `94105` is accepted. Change to Vietnam: a state text field appears and zip is optional.
4. Try to save a country code `XX` or `ZZ` through the API: `400 errors.countryCode`.
5. In admin, unpublish Vietnam: a new address in Vietnam is refused, the saved one still shows.
6. Try to delete United States (used by the address): `409`. Try to delete the state `CA` used by the address: `409`.
7. Save a country pattern like `(a+)+$`: it is accepted but never hangs the API on a long input (the check fails safely).

## 13. Rollout and compatibility

Run the migrator before the API. Existing addresses are untouched. The address API accepts the same body as before (new optional `stateProvinceId`); clients that send a country that is not in the directory now get `400`, and a country with states needs a state.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Full country and state import | F27 |
| Address formats and snapshots on orders | F18 |
| Shipping and tax by country and state | F20, F07-E |
| Localized names | F07-D |
| Phone rules per country | F07-C follow-up |
