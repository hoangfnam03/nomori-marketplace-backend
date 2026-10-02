# F07-A Currency and money rules

| | |
|---|---|
| **Module ID** | F07-A (slice of F07 "Localization, directory, currency, measures and time") |
| **Status** | Backend and Angular implemented. SQL store and migration not yet run against a real database. |
| **Branch** | `feat/localization/foundation` (backend and frontend) |
| **Depends on** | F01 (settings boundary), F03 (permissions), F10 (prices), F11-B (variant prices) |
| **Unblocks** | F14 (pricing, price lists), F15 (discounts), F16 (cart totals), F17/F18 (orders with a currency code), F19 (payment amounts) |
| **Feature map** | "Nomori rule: persist money with currency code and correct decimal scale; persist timestamps as UTC; do not rely on Angular-only conversion for accounting values." F06 (multi-store) is deferred, so there is one platform. |

## 1. Purpose

Prices are plain numbers and the Angular pages hard-code `$` in eight places. Before pricing, carts and orders, the platform needs a defined currency, a rule for how many decimals an amount may have, and one place that formats money. F07 is large (languages, countries and addresses, measures, time zones, tax), so it is cut into slices; this one covers **currencies and money** only.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | The platform has one **primary currency**. Every stored amount (product price, old price, variant adjustments and prices, later carts, orders, payments) is in the primary currency. |
| D2 | Other published currencies exist **for display only**: a customer may browse in another currency, shown as an approximation. Carts, orders and payments always use the primary currency (so no accounting value depends on browser conversion). |
| D3 | Each currency has a fixed number of **decimal places** (0 to 4). An amount with more decimals than the primary currency allows is refused, never silently rounded. Conversion for display rounds half away from zero to the target's decimals. |
| D4 | Exchange rates are entered by an administrator (units of the currency per 1 primary unit; the primary is exactly 1). No automatic rate feed (F28). |
| D5 | The primary currency cannot be switched, and its code or decimal places cannot be changed, once **any product exists**, because stored numbers would change meaning. A new platform sets it before the first product. |
| D6 | A currency code is three letters A to Z (ISO 4217 shape), unique, and never changes. |
| D7 | The first migration seeds **USD** as the primary currency (2 decimals), matching what the screens showed so far. |

## 3. Actors and authorization matrix

| Action | Platform admin (`settings.manage`) | Anyone |
|---|---|---|
| List all currencies, create, edit rate, decimals, symbol, name, order, published | Yes | No |
| Delete a currency | Yes (not the primary) | No |
| Make another currency primary | Yes, only while no product exists | No |
| Read published currencies and rates | Yes | Yes (public) |

`settings.manage` is a new permission, granted to the Administrator role by the migration.

## 4. Included behavior

- **Currency list** with code, name, symbol, decimal places, rate, primary flag, published flag, display order, rate update time.
- **Rules:** exactly one primary, always published, rate 1. Rate of others: above 0 and at most 1,000,000,000 with up to 8 decimals. Unpublishing or deleting the primary is refused. Name 1 to 100 characters; symbol 0 to 10 (the code is shown when empty).
- **Make primary** (only with no products): the chosen currency gets rate 1 and every other rate is divided by the chosen currency's old rate, so all conversions keep their meaning; the old primary becomes an ordinary currency.
- **Money rules** (`CurrencyRules`): `Round` (half away from zero), `Convert(amount, from, to)`, `HasValidScale(amount, decimals)`.
- **Price validation** against the primary currency's decimals on every price a seller or admin writes: product price and old price, variant price adjustment and override price. Error `400` with the field and the allowed decimals. Existing stored prices are not rewritten.
- **Public currencies** `GET /api/v1/currencies`: published currencies ordered for display with the primary flag, symbol, decimals and rate.
- **Audit:** `currency.created`, `currency.updated`, `currency.deleted`, `currency.primary_changed` (ids, codes and rate values, no free text).
- **Angular:**
  - One `CurrencyService` replaces every hard-coded `$`. Storefront prices follow the customer's display currency; seller and admin screens always show the primary currency (they enter and see accounting values).
  - Currency selector in the storefront header (kept in the browser). Converted prices show "≈" and the selector's tooltip says that payment is in the primary currency (the checkout note comes with F16).
  - Admin page `/admin/currencies` (list, add, edit, delete, make primary with confirmation).
  - Seller and admin price fields show the currency code and use the right step.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Languages, localized properties and resources | F07-D |
| Countries, states, address forms | F07-C |
| Dimensions and weights (measure units) | F07-B |
| Time zones (platform and per customer) and date formatting settings | F07-B |
| Tax categories, tax rates, VAT validation | F07-E |
| Automatic exchange-rate update | F28 |
| Per-currency rounding rules (cash rounding, 0.05) and price formatting patterns per locale | F14 |
| Per-customer saved currency (the choice lives in the browser) | F04 follow-up |
| Charging in a non-primary currency | F19 |
| Server-side formatted money for emails and documents | F22 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Directory/Currency` (`CurrencyCode`, `Rate`, `DisplayLocale`, `CustomFormatting`, `Published`, `DisplayOrder`, `RoundingTypeId`) | Currency | Fixed decimal places instead of rounding types; no custom formatting yet |
| `CurrencySettings.PrimaryStoreCurrencyId`, `PrimaryExchangeRateCurrencyId` | Primary currencies | One primary (store and exchange-rate currency are the same) |
| `Nop.Services/Directory/CurrencyService.ConvertCurrency` | Conversion | Same maths, explicit rounding |
| `Nop.Services/Directory/ExchangeRateProvider` | Rate feeds | Manual rates only |
| `Nop.Services/Catalog/PriceFormatter` | Display | Browser `Intl.NumberFormat` for display; server formatting deferred |

## 7. Data model

Migration `202610090001 CurrencyMigration`:

- **`Currency`**: `Id` identity, `Code char(3)` (unique), `Name nvarchar(100)`, `Symbol nvarchar(10) NULL`, `DecimalPlaces int` (check 0 to 4), `RateToPrimary decimal(18,8)` (check above 0), `IsPrimary bit`, `Published bit`, `DisplayOrder int`, `RateUpdatedOnUtc`, `CreatedOnUtc`, `UpdatedOnUtc`.
- Filtered unique index `UX_Currency_Primary ON Currency (IsPrimary) WHERE IsPrimary = 1`: at most one primary.
- Seed: `USD`, `US Dollar`, `$`, 2 decimals, rate 1, primary, published.
- Permission `settings.manage` (category Settings) added to `PermissionRecord` and mapped to the Administrator role.

`Down()` removes the mapping and permission, then the table.

## 8. Use cases and service contracts

```text
CurrencyRules (pure)     Round, Convert, HasValidScale, DecimalPlacesOf
ICurrencyStore           GetAll, Get, GetByCode, GetPrimary, Insert, Update, Delete, MakePrimary(rebase), ProductExists
ICurrencyService         GetPublished, GetAll (admin), Create, Update, Delete, MakePrimary
IPrimaryCurrencyProvider GetPrimaryAsync()    used by the product and variant services to validate scale
```

Business codes (`409`): `currency.primary_locked` (primary change or decimals/code change while products exist), `currency.primary_required` (unpublish or delete the primary), `currency.code_exists`. Field errors (`400`): `errors.code`, `errors.name`, `errors.symbol`, `errors.decimalPlaces`, `errors.rate`. Not found `404`.

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/currencies` | Public, published only |
| 2 | `GET` | `/api/v1/admin/currencies` | `settings.manage`, all |
| 3 | `POST` | `/api/v1/admin/currencies` body `{ code, name, symbol, decimalPlaces, rate, published, displayOrder }` | `settings.manage` |
| 4 | `PUT` | `/api/v1/admin/currencies/{id}` body without `code` | `settings.manage` |
| 5 | `DELETE` | `/api/v1/admin/currencies/{id}` | `settings.manage` |
| 6 | `POST` | `/api/v1/admin/currencies/{id}/make-primary` | `settings.manage` |

A currency response: `{ id, code, name, symbol, decimalPlaces, rate, isPrimary, published, displayOrder, rateUpdatedOnUtc }`.

## 10. Angular

- `core/money/currency.service.ts` (loads once in the browser, display choice in `localStorage` guarded for SSR, `format()` for customers and `formatPrimary()` for sellers and admins), `currency-admin-api.service.ts`, currency selector in the shell header.
- `admin/pages/admin-currencies.page.ts`, route `/admin/currencies` guarded by `settings.manage`.
- Product cards are derived from the loaded items, so changing the display currency updates prices at once. Replaces the eight local `formatPrice` helpers; price inputs in the seller and admin forms show the code.
- States: loading, empty, validation per field, conflict messages, network error.

## 11. Events, jobs, cache

None. The public list is small and changes rarely; the browser keeps it for the session.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Rounding half away from zero for 0, 2, 3 decimals; conversion in both directions and between two non-primary currencies; scale check; decimal places of a number |
| Create | Code shape and uniqueness (case-insensitive), name, symbol, decimals, rate limits; primary cannot be created; new currency is not primary |
| Update | Primary keeps rate 1 and stays published; non-primary rate and flags change; code cannot change; decimals of the primary locked once products exist; audit holds codes and numbers only |
| Delete | Primary refused; others deleted |
| Make primary | Refused when products exist; rebases the rates; old primary becomes ordinary; already primary is a no-op |
| Public | Only published currencies, in display order |
| Prices | Product price, old price, variant adjustment and override refused when they have too many decimals; allowed with the primary's decimals; zero-decimal primary refuses cents |
| Migration | Version ordering, permission constant |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (primary index, rebase transaction, product exists), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; `Currency` has one row `USD` and the Administrator role has `settings.manage`.
2. As admin open `/admin/currencies`; add `EUR` (rate 0.92, 2 decimals) and `VND` (rate 25000, 0 decimals).
3. In the storefront header choose EUR: prices show "≈" and are converted; choose VND: no cents. Reload: the choice is kept. Seller and admin pages still show USD.
4. Save a seller product with price `10.999`: refused (2 decimals allowed).
5. Try to delete or unpublish USD, and to change its decimals while a product exists: refused. Try `make-primary` on EUR: refused (products exist).
6. On a fresh database with no products: make EUR primary; rates are rebased (USD becomes about 1.0870) and prices are validated with EUR's decimals.
7. As a non-admin call the admin routes: `403`; call `GET /api/v1/currencies` anonymously: only published rows.

## 13. Rollout and compatibility

Run the migrator before the API. Existing prices keep their numbers and are now read as USD. Price fields with more decimals than 2 can no longer be saved. API price fields are unchanged; the Angular app stops hard-coding `$`. If a platform really wants another primary currency, change it before creating products.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Measures, time zones | F07-B |
| Countries, states, addresses | F07-C |
| Languages and localized content | F07-D |
| Tax | F07-E |
| Currency stored with every order, cart and payment | F16 to F19 |
| Rate feed provider, rounding rules, locale price patterns | F28, F14 |
