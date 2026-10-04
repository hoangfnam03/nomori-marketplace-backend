# F20-A Shipping rates per shop

| | |
|---|---|
| **Module ID** | F20-A (slice of F20 "Shipping, pickup, shipment, warehouse and fulfilment") |
| **Status** | Backend and Angular implemented; 416 service tests pass and the Angular build and lint pass. SQL store, migration, HTTP and screens not yet run against a real database. |
| **Branch** | `feat/shipping/foundation` (backend and frontend) |
| **Depends on** | F05 (shops, members), F07-C (countries, states, addresses), F16-A (cart grouped by shop), F07-A (primary currency) |
| **Unblocks** | F17 (checkout picks one option per shop), F18 (order shipping snapshot), F20-B (shipments, tracking) |
| **Feature map** | "Shipment records need ShopId/fulfilment-group ownership." "Shipping methods/rates, shipping plugin providers, pickup points, warehouses, shipments and shipment items, tracking numbers, shipping status, packaging/weight calculations." |

## 1. Purpose

A cart can hold lines from several shops (F16-A D1) and each shop ships its own parcels. This slice lets every shop publish **where it ships and for how much**, and lets a customer see, for a destination, what shipping costs for each shop in the cart. Nothing is charged or stored on an order yet: checkout (F17) picks one option per shop.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Shipping is defined by the shop**, not by the platform. A rate belongs to one shop; only members of that shop manage it. Platform-wide carriers and plugin providers are F28/F20-C. |
| D2 | **A rate is a flat fee per shop per parcel.** One cart group (all lines of one shop) is one parcel. No weight, size or per-item pricing yet (needs F07-B units and F20-C). |
| D3 | **A rate covers one country, and optionally one state.** With no state it covers the whole country. A customer destination matches a rate when the country is the same and the rate has no state or the same state. |
| D4 | **A shop can offer several methods to the same place** (for example "Standard" and "Express"): each matching rate is one option. |
| D5 | **Free shipping over an amount**: when the shop's subtotal in the cart reaches `FreeOverSubtotal`, the fee is 0. The subtotal is the shop group's subtotal from the cart (F14-A prices, before any discount, which are F15). |
| D6 | **Fees are in the primary currency** and follow its decimal places, like every other stored amount (F07-A). |
| D7 | **A shop group with no matching rate cannot be shipped to that destination.** The quote says so per shop and `canShipAll` is false; checkout will block on it. |
| D8 | **Only countries that allow shipping** in the directory (F07-C) can have a rate, and a state must belong to its country. A country that is later closed for shipping hides its rates from quotes; they are not deleted. |
| D9 | Destination comes from the customer's own saved address or from a country (and state) typed for an estimate. The customer id always comes from the session. |

## 3. Actors and authorization matrix

| Action | Member of the shop | Other signed-in customer | Guest |
|---|---|---|---|
| List, create, change, delete rates of a shop | Yes, own shop only | `404` | `401` |
| Quote shipping for own cart | Yes | Yes | `401` |

The shop id is part of the route and is checked against the caller's membership on every call; a rate id of another shop is "not found". Platform administrators do not edit shop rates (non-goal).

## 4. Included behavior

- **Rate fields:** `name` (1 to 100 characters), `countryCode`, `stateProvinceId` (optional), `fee` (0 or more), `freeOverSubtotal` (optional, above 0), `minDays` and `maxDays` (optional estimate in days, 0 to 365, min not above max), `published`, `displayOrder`.
- **Limits:** at most **50 rates** per shop (`409 shipping.rate_limit`). Names may repeat (the same method for several countries).
- **Seller screens:** list of rates grouped by country, create, edit, delete, publish switch.
- **Quote** `POST /api/v1/shipping/quote` with `{ addressId }` or `{ countryCode, stateProvinceId }`:
  - the destination country must be published and open for shipping, and a country with states needs one of them (the postal code is not needed for an estimate); a saved address that fails this is refused with `addressId`;
  - for every shop group of the cart: subtotal, options (rate id, name, fee after the free-over rule, whether it is free, estimated days) sorted by fee, then name; `canShip` false when there are none;
  - `shippingTotal` is the sum of the **cheapest** option of every shop (a preview; the customer chooses at checkout); null when some shop cannot ship;
  - lines that cannot be bought (unavailable) are not counted, because the cart does not count them in its subtotal either;
  - an empty cart has no shops: `canShipAll` is false and `shippingTotal` is null.
- **Cart page (Angular):** "Estimate shipping" with the customer's saved shipping addresses (or a country when there is none); shows options per shop and the preview total.
- **Audit:** `shipping.rate_created`, `shipping.rate_updated`, `shipping.rate_deleted` (shop id, rate id, country).

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Weight, size, per-item and per-product rates; units | F07-B, F20-C |
| Carrier plugins and live rates | F20-C, F28 |
| Pickup points, delivery dates, availability ranges per product | F20-B |
| Per-product "free shipping" or "shipping not required" (digital goods) | F20-B, F08-C |
| Platform default or marketplace-funded shipping, minimum fee | F20-C, F15 |
| Choosing an option, shipping in the order total, shipment records and tracking | F17, F18, F20-B |
| Shipping tax | F07-E |
| Rates by postal-code range | F20-C |
| Admin editing a shop's rates | F20-C |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Shipping/ShippingMethod`, `ShippingMethodCountryMapping` | A named method restricted to countries | A rate is a method plus its country and fee in one row, owned by a shop |
| Plugin `Shipping.FixedByWeightByTotal` (`ShippingByWeightByTotalRecord`: country, state, order total range, fee) | Fixed fee by destination | Flat fee, no weight or total ranges; free over an amount |
| `Nop.Services/Shipping/ShippingService.GetShippingOptionsAsync` | Collect options for a package | One parcel per shop, options from the shop's own rates |
| `ShippingSettings.FreeShippingOverXEnabled`, `FreeShippingOverXValue` | Free shipping over a total | Per rate, in the shop's subtotal |
| `Domain/Shipping/Warehouse`, `Shipment`, `ShipmentItem` | Fulfilment records | Deferred (F20-B) |
| Multi-shop packages | Not in nopCommerce (one vendor split is only informational) | A parcel per shop group |

## 7. Data model

Migration `202610160001 ShippingRateMigration`:

**`ShippingRate`**

| Column | Type | Notes |
|---|---|---|
| `Id` | `int` identity | |
| `VendorId` | `int` FK to `Vendor` | Cascade is not used: shops are soft-deleted; rows are ignored for inactive shops |
| `Name` | `nvarchar(100)` | |
| `CountryCode` | `nvarchar(2)` | Upper case ISO code, like `CustomerAddress.CountryCode` |
| `StateProvinceId` | `int` null FK to `StateProvince` | No action |
| `Fee` | `decimal(18,4)` | Check `>= 0` |
| `FreeOverSubtotal` | `decimal(18,4)` null | Check `> 0` when set |
| `MinDays`, `MaxDays` | `int` null | Check 0 to 365 and `MinDays <= MaxDays` |
| `Published` | `bit` | |
| `DisplayOrder` | `int` | |
| `CreatedOnUtc`, `UpdatedOnUtc` | `datetime2` | |

Index `IX_ShippingRate_Vendor_Country (VendorId, CountryCode)`. `Down()` drops the table.

## 8. Use cases and service contracts

```text
ShippingRules (pure)   Matches(rate, country, state), FeeFor(rate, subtotal), Options(rates, country, state, subtotal),
                       ShippingTotal(groups)
IShippingStore         GetRates(vendorId), GetRate(vendorId, id), GetPublishedRates(vendorIds, countryCode),
                       Insert, Update, Delete, CountRates(vendorId)
IShippingService       GetRatesAsync, CreateRateAsync, UpdateRateAsync, DeleteRateAsync   (all take the shop id)
                       QuoteAsync(customerId, destination)
```

Field errors (`400`): `name`, `countryCode`, `stateProvinceId`, `fee`, `freeOverSubtotal`, `minDays`, `maxDays` and, for a quote, `addressId` or the address field names of the directory. Business codes (`409`): `shipping.rate_limit`. Not found: rate not in this shop, address not of this customer.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{vendorId}/shipping-rates` | Members only; `404` for others |
| 2 | `POST` | `/api/v1/vendors/{vendorId}/shipping-rates` | Answers the rate |
| 3 | `PUT` | `/api/v1/vendors/{vendorId}/shipping-rates/{id}` | Answers the rate |
| 4 | `DELETE` | `/api/v1/vendors/{vendorId}/shipping-rates/{id}` | `204` |
| 5 | `POST` | `/api/v1/shipping/quote` | Signed-in customer; body `{ addressId }` or `{ countryCode, stateProvinceId }` |

Write routes need the CSRF token like every other write.

## 10. Angular

- `core/shipping/shipping-api.service.ts` and models.
- Seller page `/vendor/shipping` (link in the seller portal): table of rates, form with country and state from the directory, fee, free-over, days, published.
- Cart page: "Estimate shipping" block.
- States: loading, empty (no rates yet: explains that customers cannot buy from the shop outside its rates at checkout), saving, field errors, conflict (limit), network error.

## 11. Events, jobs, cache

None. Rates are small and read per request.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Country must match; state-less rate covers every state; state rate only its state; unpublished never matches; free over the threshold (equal counts); options sorted; total is the sum of the cheapest and null when a shop cannot ship |
| Rates | Validation of every field; fee decimals follow the primary currency; country must allow shipping; state must belong to the country; limit of 50; another shop's rate is not found; audit written |
| Quote | Saved address of another customer is not found; typed country checked by the directory; groups follow the cart; shop without a rate gets `canShip` false; free over the shop subtotal; closed country hides rates |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL, HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check `ShippingRate` and its checks.
2. As a shop member open `/vendor/shipping`; add "Standard", country US, fee 5, free over 50, 3 to 5 days.
3. Add "Express" for US and "Standard" for a US state only. As another shop's member `GET .../shipping-rates` of the first shop: `404`.
4. As a customer with items from two shops in the cart, estimate shipping to a US address: the shop with rates shows its options, the other one says it does not ship there; the preview total is empty.
5. Add a rate to the second shop: the preview total is the sum of the cheapest options. Raise the cart over 50 for the first shop: its fee becomes free.
6. In the admin directory close shipping for the country: the quote no longer offers those rates.

## 13. Rollout and compatibility

Run the migrator before the API. No existing screen or API changes meaning; the cart gains an estimate block and the seller portal a page.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Weight and size rates, units, carrier plugins | F07-B, F20-C, F28 |
| Pickup points, delivery dates, shipments, tracking | F20-B |
| Choosing an option at checkout, shipping on the order | F17, F18 |
| Shipping tax and discounts on shipping | F07-E, F15 |
| Admin management of shop rates | F20-C |
