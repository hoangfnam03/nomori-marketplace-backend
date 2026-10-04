# F07-E Tax by destination (categories and rates)

| | |
|---|---|
| **Module ID** | F07-E (slice of F07 "Localization, directory, currency, measures and time"; first slice of tax) |
| **Status** | Backend and Angular implemented; 698 service tests and 36 data tests pass, the Angular build, lint and 30 unit tests pass. SQL stores, the filtered unique indexes, the migration and the screens have not been run against a real database. |
| **Branch** | `feat/tax/foundation` (backend and frontend) |
| **Depends on** | F07-A (currency decimals), F07-C (countries and states), F10 (products), F17-A (checkout), F18-A (orders), F15-A (discounts) |
| **Unblocks** | F07-E-B (tax-inclusive prices, shipping tax, VAT numbers), invoices (F18-B), settlement (tax per shop order) |
| **Feature map** | "tax categories, VAT validation and tax calculation providers." "persist money with currency code and correct decimal scale." |

## 1. Purpose

Orders were placed with items plus shipping only. This slice adds **tax**: the platform keeps tax categories and, for each category, a rate per country (and optionally per state). At checkout the tax of every line is worked out from the delivery address, shown to the customer, charged, and written on the order line, so the order stays correct whatever the rates become later.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Prices are tax-exclusive.** Every price in the catalog, the cart and discounts is the price before tax; tax is added at checkout. Tax-inclusive pricing is F07-E-B. |
| D2 | **Tax follows the delivery address**, not the shop: the country and state of the chosen address decide the rate. |
| D3 | **The platform collects the tax** for every shop, and every shop order records its own tax, so settlement (F05) can pay shops correctly. |
| D4 | **A product has one tax category, assigned by an administrator.** Tax classification is a legal matter, so sellers cannot pick a reduced rate for their own products. A product with no assignment is in the **default category** (`Standard`, seeded). |
| D5 | **A rate is a percentage for a category in a country, or in one state of it.** The state rate wins over the country rate; no rate means 0 percent. Rates can be unpublished (ignored) without deleting them. |
| D6 | **The base of a line is its total after its share of the discount** (F15-A). The shop order's discount is spread over its lines in proportion to their totals (the leftover cent goes to the largest line), then the rate is applied and rounded to the currency decimals. Tax is never charged on a discount. |
| D7 | **Shipping is not taxed** in this slice (F07-E-B). |
| D8 | **The server computes the tax.** The browser sends nothing about tax. The preview shows it; the placement works it out again from fresh prices; the order service checks the numbers it is given are sane (not negative, within the currency decimals) and builds the totals. |
| D9 | **The order keeps what was charged**: each line keeps the rate and the tax amount, each shop order its tax, the order its tax total. `Total = Subtotal - Discount + Shipping + Tax`, enforced by the database for the order and each shop order. |
| D10 | **A category in use is not deleted.** Deleting needs no rates and no product assigned, and never the default category. |

## 3. Actors and authorization matrix

| Action | Customer | Shop member | Administrator (`settings.manage`) |
|---|---|---|---|
| See the tax in checkout and on their orders | Yes | no | Yes |
| See the tax of the shop's own orders | no | Own shop (`404` otherwise) | Yes |
| Manage tax categories and rates | no | no | Yes |
| Assign a tax category to a product | no | no | Yes |

## 4. Included behavior

- **Categories:** `name` (1 to 100, unique), at most 20; exactly one is the default (seeded `Standard`, 0 percent until rates are added). Rename and reorder are allowed; the default cannot be deleted.
- **Rates:** `categoryId`, `countryCode`, optional `stateProvinceId`, `percentage` (0 to 100, up to 3 decimals), `published`. One rate per category and place (`409 tax.rate_exists`); at most 500 per category. The country must be in the directory (F07-C) and a state must belong to it.
- **Product assignment:** `PUT /admin/tax/products/{productId}` with a category; `GET` returns the current category (the default when none). Removing an assignment returns the product to the default.
- **Calculation:** for the lines of a cart and a destination, each line's category is found, then the rate (state, else country, else 0), then the base after its discount share, then the tax. The result has the rate, base and tax per line, tax per shop and the total.
- **Checkout:** the preview answers `tax` (total and per shop) once there is an address, and `total = items - discount + shipping + tax`; without an address the tax is unknown and the total stays empty, like shipping. Placing recomputes the tax from fresh prices and writes it on the order.
- **Orders:** each line shows its tax rate and amount to staff and to the customer; shop orders and the order show their tax; the payment is created for the total with tax.
- **Audit:** `tax.category_created`, `tax.category_updated`, `tax.category_deleted`, `tax.rate_created`, `tax.rate_updated`, `tax.rate_deleted`, `tax.product_assigned`.
- **Angular:** page `/admin/tax` (categories, the rates of a category, assign a category to a product by id); a tax line in the checkout summary and in every order total.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Prices that include tax; showing prices with or without tax in the storefront | F07-E-B |
| Taxing shipping; a shipping tax category | F07-E-B |
| Rates by postal code or city; several taxes on one line (compound) | F07-E-B |
| VAT number validation and reverse charge for businesses | F07-E-B |
| Tax provider plugins and live rates | F28 |
| Tax-exempt customers or roles | F07-E-B |
| Tax category in the seller and admin product forms; bulk assignment and import | F10 follow-up, F27 |
| Tax on refunds and returns | F21 |
| Tax reports and invoices with tax lines | F18-B, F26 |
| Default category other than the seeded one (it cannot be changed) | F07-E-B |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Tax/TaxCategory` | Categories | Same idea; one flagged default |
| `Product.TaxCategoryId`, `IsTaxExempt` | Product category | Separate `ProductTaxCategory` table (no product table change); no per-product exemption; administrator only |
| `Plugins/Tax.FixedOrByCountryStateZip` (`TaxRate`: category, country, state, zip, percentage) | Rates | Category, country, optional state; no zip, no store |
| `Domain/Tax/TaxSettings` (`PricesIncludeTax`, `ShippingIsTaxable`, `TaxBasedOn`) | Behavior | Exclusive prices, shipping untaxed, based on the delivery address |
| `Nop.Services/Tax/TaxService.GetTaxRateAsync`, `OrderTotalCalculationService` | Calculation | Pure rules; discount share removed from the base |
| `OrderItem.PriceInclTax`, `PriceExclTax`, `Order.OrderTax` | On the order | Rate and tax per line, tax per shop order and per order |
| Tax per vendor | Not in nopCommerce | Tax per shop order, for settlement |

## 7. Data model

Migration `202610200001 TaxMigration`:

**`TaxCategory`**: `Id`, `Name` (100, unique), `IsDefault bit`, `DisplayOrder`, `CreatedOnUtc`, `UpdatedOnUtc`. Unique filtered index so at most one row has `IsDefault = 1`. Seed `Standard` (default).

**`TaxRate`**: `Id`, `CategoryId` FK (no action), `CountryCode nvarchar(2)`, `StateProvinceId` int null FK `StateProvince`, `Percentage decimal(7,3)` (check 0 to 100), `Published bit`, `CreatedOnUtc`, `UpdatedOnUtc`. Two filtered unique indexes: `(CategoryId, CountryCode)` where the state is null, and `(CategoryId, CountryCode, StateProvinceId)` where it is not.

**`ProductTaxCategory`**: `ProductId` int PK FK `Product`, `TaxCategoryId` FK. No row means the default category.

**`OrderLine`** gains `TaxRate decimal(7,3)` and `TaxAmount decimal(18,4)` (both default 0); **`ShopOrder`** gains `TaxAmount`; **`CustomerOrder`** gains `TaxTotal`. The amount checks become `Total = Subtotal - Discount + Shipping + Tax`, with `Tax >= 0`.

Existing rows get 0 tax, so the new checks hold for them. `Down()` restores the previous checks and drops the columns and tables.

## 8. Use cases and service contracts

```text
TaxRules (pure)    FindRate(rates, country, state), Calculate(lines, shopDiscounts, rateFor, decimals) -> per line, per shop, total
ITaxStore          categories, rates (by category, by country), product assignments (get for products, set, clear), usage counts
ITaxService        admin: categories and rates CRUD, assign a category to a product, get a product's category
                   checkout: CalculateAsync(country, state, lines, shopDiscounts)
IOrderService      CreateAsync accepts TaxRate and TaxAmount on each line, checks them and builds the totals
ICheckoutService   preview answers tax; placing works it out from fresh prices
```

Field errors (`400`): `name`, `countryCode`, `stateProvinceId`, `percentage`, `taxCategoryId`, `tax`. Business codes (`409`): `tax.category_exists`, `tax.category_limit`, `tax.category_in_use`, `tax.default_category`, `tax.rate_exists`, `tax.rate_limit`. Not found: category, rate, product.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/admin/tax/categories` | `settings.manage` |
| 2 | `POST` | `/api/v1/admin/tax/categories` | |
| 3 | `PUT` | `/api/v1/admin/tax/categories/{id}` | |
| 4 | `DELETE` | `/api/v1/admin/tax/categories/{id}` | `409` when in use or default |
| 5 | `GET` | `/api/v1/admin/tax/categories/{id}/rates` | |
| 6 | `POST` | `/api/v1/admin/tax/categories/{id}/rates` | |
| 7 | `PUT` | `/api/v1/admin/tax/rates/{id}` | |
| 8 | `DELETE` | `/api/v1/admin/tax/rates/{id}` | |
| 9 | `GET` | `/api/v1/admin/tax/products/{productId}` | The category and the product's name |
| 10 | `PUT` | `/api/v1/admin/tax/products/{productId}` | `{ taxCategoryId }`; `null` returns to the default |
| 11 | `POST` | `/api/v1/checkout/preview`, `/place` | Answer and write the tax |

Write routes need the CSRF token like every other write.

## 10. Angular

- `core/tax/tax-api.service.ts` and models; page `/admin/tax`; menu link.
- Checkout summary and every order total show a tax line; order lines show the rate.
- States: loading, empty, saving, field errors, conflicts (name, rate, in use), network error.

## 11. Events, jobs, cache

None. Rates are read per checkout.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | State rate beats country rate; unpublished ignored; no rate is 0; rounding; the discount share leaves the base and adds up; two categories in one shop; tax per shop |
| Admin | Validation of every field; names unique; limits; default cannot be deleted; in-use refused; rate duplicates per place; state must belong to the country; assign and clear; audit |
| Checkout | Preview with tax and total; no address means no tax and no total; the same cart in two countries; placing writes line rates, shop and order tax; payment for the total with tax; discount lowers the tax base |
| Orders | Totals with tax; bad tax values refused; legacy orders with no tax |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (filtered unique indexes, checks), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run the migrator; check the new tables, the seeded `Standard` category, the new order columns and the new checks.
2. As an administrator open `/admin/tax`: add a rate of 10 percent for `Standard` in the country of a test address; add a second category `Reduced` with 5 percent for the same country and assign it to one product by id.
3. As a customer with that address, cart one `Standard` and one `Reduced` product and open checkout: the summary shows the tax of both and the total includes it.
4. Use a discount code: the tax drops, because the base after the discount is smaller.
5. Place the order: the order page shows the tax per line and in the totals; the payment amount equals the total with tax.
6. Change the rate afterwards: the existing order does not change.
7. Try to delete `Standard` (refused) and a category with rates (refused).

## 13. Rollout and compatibility

Run the migrator before the API. With no rates, tax is 0 and orders behave as before. Existing orders show 0 tax.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Tax-inclusive prices and storefront display; shipping tax; postal-code rates | F07-E-B |
| VAT numbers and reverse charge; exempt customers | F07-E-B |
| Category in product forms; bulk assignment | F10, F27 |
| Tax on refunds and returns; invoices and reports | F21, F18-B, F26 |
| Tax providers | F28 |
