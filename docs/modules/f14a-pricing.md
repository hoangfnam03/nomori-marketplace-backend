# F14-A Pricing: special prices, tier prices and the price calculation service

| | |
|---|---|
| **Module ID** | F14-A (slice of F14 "Pricing, price lists, tier prices and price formatting") |
| **Status** | Backend and Angular implemented. SQL stores and migration not yet run against a real database. |
| **Branch** | `feat/pricing/foundation` (backend and frontend) |
| **Depends on** | F07-A (primary currency and decimals), F10 (price, old price), F11-B (variant adjustments and prices), F13-A (price filter and sort) |
| **Unblocks** | F15 (discounts apply to a calculated price), F16 (cart uses the quote), F17/F18 (order line price snapshot) |
| **Feature map** | "Nomori rule: price calculation is a service used by cart and order placement, not logic duplicated in Angular. Persist the price snapshot on order lines." |

## 1. Purpose

A product had one base price, an "old price" for display and variant adjustments that nobody combined into a final number. This slice adds a time-boxed **special price** and **quantity (tier) prices**, and puts the whole rule in one **price calculation service** that the storefront now calls and the cart and order placement will call. The Angular app no longer works out prices itself.

## 2. Pricing pipeline

All amounts are in the primary currency (F07-A) and the result is rounded to its decimals (half away from zero).

```text
1. regular   = product price
2. special   = special price when it exists and the sale window is open       (lower than the regular price)
3. tier      = tier price of the largest quantity step reached by the order   (lower than the regular price)
4. base      = lowest of regular, special and tier
5. variant   = the combination's override price when it has one,
               otherwise base + the price adjustments of the chosen values
6. unit      = round(variant);  line total = round(unit x quantity)
```

- **Applied rule** reported with the quote: `variant_override`, `tier`, `special` or `base`.
- **Compare price** (struck through on screens): the regular price (plus adjustments) when the unit price is lower, otherwise the old price when it is higher than the unit price, otherwise none. A variant override has no compare price.
- A unit price of 0 or less is refused (`400 errors.price`); validation of the inputs normally prevents it.

## 3. Actors and authorization matrix

| Action | Member of the product's shop | Platform admin | Customer / other shop |
|---|---|---|---|
| Set special price, its window and tier prices | Yes (also hidden or draft) | Not in this slice (the admin form is unchanged and never blanks them) | `404` |
| Read them | Yes (own) | Admin product read unchanged | Public: only for visible products |
| Ask for a quote | | | Yes (public, visible products only) |

## 4. Included behavior

- **Special price** (`specialPrice`, `specialPriceStartUtc`, `specialPriceEndUtc`): optional. When set it must be above 0, **lower than the regular price** and within the primary currency's decimals. The window is optional (a missing bound is open); the end must be after the start. Without a special price the window must be empty. Outside the window the special price is ignored.
- **Tier prices:** up to 20 steps `{ quantity, price }` per product. Quantity 2 to 10,000, no repeated quantity; price above 0, within the decimals and **lower than the regular price**; **strictly decreasing** as the quantity grows. Saved together with the special price in one call (replace-all).
- **Regular price changes** (seller or admin): a new regular price must stay above the special price and above every tier price (`400 errors.price`: lower or remove them first). Raising the price is always allowed.
- **Price change audit:** every change of the regular price writes `product.price_changed` with the old and new number; pricing saves write `product.pricing_changed` (counts and amounts only).
- **Quote** `GET /catalog/products/{id}/price`: quantity 1 to 10,000 and the chosen variant value ids. A product with combinations needs one value of every attribute and an existing combination; a product without combinations accepts only values of its own attributes (price adjustments). Answers the quote described above.
- **List and detail prices:** list and detail items carry `finalPrice` (the special price while its window is open, otherwise the regular price). The detail also lists the visible `tierPrices`.
- **Search consistency (F13-A):** the minimum and maximum price filters, the price sorts and the facet price range use the same "current price" (special while active), so a sale item is found at its sale price.
- **Product copy** does not copy the special price or tier prices (like the schedule, they are a deliberate choice of the original).
- **Combination price rule:** a combination's override price replaces everything (special and tier included), as it was set deliberately for that variant.
- **Angular:** seller details page gets a **Pricing** section (special price with window and a tier price table with a live check of the rules); product cards show the sale price with the regular price struck through; the product page has a quantity field, a "Buy more, pay less" table and takes the unit price and line total from the quote.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Tier prices per customer role, per shop of the buyer, or with their own date window | F14-B |
| Price lists and strategies (wholesale lists, import of lists) | F14-B / F27 |
| Customer-entered price, "call for price", price ranges ("from $5") in lists | F14-B |
| Percentage-based special prices and tier discounts | F15 |
| Coupons, discount rules, gift cards, loyalty | F15 |
| Tax-inclusive or exclusive display and tax on the quote | F07-E |
| Price snapshot table on order lines (the quote already carries every field the snapshot needs) | F18 |
| Admin form fields for special and tier prices | F10 follow-up |
| Rounding rules per currency such as 0.05 | F14-B |
| Price history report | F26 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Product.SpecialPrice`, `SpecialPriceStartDateTimeUtc`, `SpecialPriceEndDateTimeUtc` | Sale price with a window | Same, with a "lower than the price" rule |
| `Domain/Catalog/TierPrice` (`Quantity`, `Price`, customer role, store, dates) | Quantity breaks | Per product only; strictly decreasing steps |
| `Nop.Services/Catalog/PriceCalculationService.GetFinalPriceAsync` | Pipeline | Same order; the best of special and tier wins; the override of a combination wins over all |
| `ProductAttributeCombination.OverriddenPrice`, `ProductAttributeValue.PriceAdjustment` | Variant price | Same |
| `Product.OldPrice` | Struck-through price | Same |
| `Nop.Services/Catalog/PriceFormatter` | Display | Formatting stays in F07-A's currency service |

## 7. Data model

Migration `202610100001 PricingMigration`:

- `Product`: `SpecialPrice decimal(18,4) NULL`, `SpecialPriceStartUtc datetime2 NULL`, `SpecialPriceEndUtc datetime2 NULL`.
- **`ProductTierPrice`**: `ProductId int` (FK to `Product`, cascade), `Quantity int`, `Price decimal(18,4)`; primary key `(ProductId, Quantity)`; check `Quantity >= 2` and `Price > 0`.
- No backfill; existing products have no special or tier price.

`Down()` drops the table and the three columns.

## 8. Use cases and service contracts

```text
PriceRules (pure)            CurrentPrice(product, now), IsSpecialActive(product, now), TierPriceFor(tiers, quantity), Compose(...)
IPriceCalculationService     QuoteAsync(PriceRequest(productId, quantity, valueIds))
IProductPricingService       GetTierPricesAsync(productId); SetPricingForVendorAsync(vendorId, productId, SavePricingCommand, actor)
IProductStore (added)        UpdatePricingAsync(product), GetTierPricesAsync(productId), SetTierPricesAsync(productId, tiers)
Product                      + SpecialPrice, SpecialPriceStartUtc, SpecialPriceEndUtc
ProductDetail                + TierPrices
PriceQuote                   productId, combinationId, quantity, currency, regularPrice, unitPrice, comparePrice, lineTotal, appliedRule
```

Field errors (`400`): `errors.specialPrice`, `errors.specialPriceEndUtc`, `errors.tierPrices`, `errors.quantity`, `errors.valueIds`, `errors.price`. Not found and forbidden follow F10-A (`404`; `403` for an inactive shop).

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}/pricing` body `{ specialPrice, specialPriceStartUtc, specialPriceEndUtc, tierPrices: [{ quantity, price }] }` | Member; answers the saved pricing |
| 2 | `GET` | `/api/v1/vendors/{vendorId}/products/{id}` | adds `specialPrice`, window, `tierPrices` |
| 3 | `GET` | `/api/v1/catalog/products`, `/products/{id}` | items add `finalPrice`; detail adds `tierPrices` |
| 4 | `GET` | `/api/v1/catalog/products/{id}/price?quantity=&valueIds=` | Public |

## 10. Angular

- `VendorProductApiService.setPricing()`, `VendorProduct` fields; `CatalogApiService.getPriceQuote()`.
- `vendor-product-details.page.ts`: Pricing section. Product cards and the list use `finalPrice`. `product-detail.page.ts`: quantity, tier table, quote.
- States: loading, saving, validation per field, quote error, empty tier list.

## 11. Events, jobs, cache

None; the special price window is evaluated on read. When F13-C adds caching, cached prices must expire at the window edges.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Pure rules | Special active in and out of the window with open bounds; tier lookup for quantities below, at and above steps; lowest of regular, special, tier; override wins; compare price rules; rounding |
| Pricing save | Special lower than price, scale, window order; tiers: count, quantity range, duplicates, decreasing, lower than price; replace-all; clearing |
| Price change | Cannot go to or below the special price or a tier price; raising is fine; audit with numbers |
| Quote | Quantity limits; variants need a full selection and an existing combination; adjustments; override; plain product with foreign values refused; hidden or unknown product not found |
| Isolation | Other shop gets not found; inactive shop forbidden; hidden product editable |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (the price expression in filters, tier replace), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check the new columns and `ProductTierPrice`.
2. In Details, Pricing: set a special price of 8 on a product priced 10 with a window that includes now, and tiers 5 units at 7 and 10 units at 6. A tier of 11 or an increasing tier is refused.
3. Storefront list: the card shows 8 with 10 struck through. Filter price 7 to 9: the product is found; filter 9 to 12: it is not. Sort by price low to high uses 8.
4. Product page: quantity 1 gives 8; 5 gives 7 each; 10 gives 6 each with the line total; the table lists the steps. Change the window to the future: quantity 1 gives 10.
5. On a product with combinations: choose values, the unit price follows the adjustments; a combination with an override price ignores the special and tier prices.
6. Try to lower the regular price to 6.5 while tiers at 7 exist: refused. Raise it to 12: allowed, and the audit log has `product.price_changed`.
7. As another shop's member call the pricing route: `404`. Quote a stopped product anonymously: `404`.

## 13. Rollout and compatibility

Run the migrator before the API. Existing prices and responses keep their meaning; items only gain fields. The admin product form does not touch the new columns. The price filter and sort now use the current (special-aware) price.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Role and shop based tier prices, price lists | F14-B |
| Discounts and coupons on top of the quote | F15 |
| Price snapshot on order lines | F18 |
| Tax in the quote | F07-E |
| Cache expiry at the special price window edges | F13-C |
