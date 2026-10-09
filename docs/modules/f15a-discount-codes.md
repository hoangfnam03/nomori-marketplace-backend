# F15-A Discount codes (platform-funded and shop-funded)

| | |
|---|---|
| **Module ID** | F15-A (slice of F15 "Promotions, discounts, gift cards and rewards") |
| **Status** | Backend and Angular implemented; 648 service tests and 35 data tests pass, the Angular build, lint and 30 unit tests pass. SQL stores, the locking redeem, the migration and the screens have not been run against a real database. |
| **Branch** | `feat/discounts/foundation` (backend and frontend) |
| **Depends on** | F03 (permissions, audit), F05 (shops, members), F07-A (primary currency), F17-A (checkout), F18-A (orders) |
| **Unblocks** | F15-B (product and category discounts, automatic discounts), F15-C (gift cards, reward points), settlement (who funded what) |
| **Feature map** | "Marketplace decision: decide whether a promotion is platform-funded, seller-funded, or co-funded. The resulting allocation must be explicit in order and settlement data." |

## 1. Purpose

A customer can enter a code at checkout and pay less. This slice decides **who pays for the discount** and records it on the order, so settlement can later give every shop the right amount. It covers coupon codes only: a percentage or a fixed amount, with dates, a minimum, and usage limits.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Two kinds of discount, by who funds it.** A **platform** discount is created by an administrator and applies to the whole cart; the platform bears its cost. A **shop** discount is created by a member of a shop and applies only to that shop's lines; the shop bears its cost. Co-funded discounts are F15-B. |
| D2 | **The funding is written on the order.** Every shop order records the amount discounted on it (`DiscountAmount`) and who funded it (`DiscountFunding`: `platform` or `shop`). A platform discount is split across the shop orders in proportion to their subtotals, so each shop order is complete on its own; settlement (F05) will make the shop whole for a platform-funded amount. |
| D3 | **Coupon code only, one per order.** A discount is applied by typing its code at checkout. Automatic discounts, stacking several codes and customer-role rules are F15-B. |
| D4 | **The server decides the amount** from the cart subtotals at the moment of placing; the browser sends only the code. The discount never exceeds what it applies to (a shop discount never exceeds that shop's subtotal, a platform discount never exceeds the cart subtotal). Shipping is never discounted. |
| D5 | **Amount rules.** Percentage: 0 to 100 (up to 2 decimals) of the eligible subtotal, rounded to the currency decimals, optionally capped by `MaxDiscountAmount`. Fixed: an amount in the primary currency, at most the eligible subtotal. |
| D6 | **Rules a code can carry:** start and end date, minimum eligible subtotal, total number of uses, number of uses per customer, enabled switch. A code is unique across the platform (case-insensitive). |
| D7 | **Redeeming is atomic.** Checking the limits and recording the use happen in one transaction that locks the discount, so two buyers cannot both take the last use. Checkout redeems right after the order is created; if the limits refuse at that moment the order is cancelled by the system (stock goes back) and the customer is told (`checkout.coupon_unavailable`). |
| D8 | **A use is given back when checkout fails after redeeming** (payment refused). A shop order cancelled later does not give the use back (F15-B decides the policy). |
| D9 | **A used discount is never deleted**, only disabled; its usage history stays. A discount that was never used can be deleted. A shop's discounts can only be edited by members of that shop; they cannot be moved between shops. |
| D10 | **Orders keep the numbers they were placed with.** `Total = Subtotal - DiscountTotal + ShippingTotal`, enforced by the database for both the order and each shop order. |
| D11 | **Codes can be picked from a list at checkout, as well as typed.** The preview lists every code that is switched on and not over, of the platform or of a shop among the lines being bought (at most 50), each with the amount it takes off now or the reason it cannot be used (`min_subtotal` with the amount still missing, `not_started` with its start, `limit_reached`, `customer_limit_reached`, `nothing_to_discount`). Usable codes come first, biggest discount first. Picking a code is the same as typing it; placing checks it again. **Every code is shown**, so a code meant to stay private (for example for one partner) has no way to be hidden yet; a "show in list" flag would be a follow-up. |

## 3. Actors and authorization matrix

| Action | Customer | Shop member | Administrator (`discounts.manage`) |
|---|---|---|---|
| Enter a code at checkout (preview and place) | Yes | Yes | Yes |
| List, create, change, enable, delete **shop** discounts | no | Own shop only (`404` otherwise) | no |
| List, create, change, enable, delete **platform** discounts | no | no | Yes |
| See usage count of a discount | no | Own shop's | Platform's |

Members of a shop cannot use their own shop's discount code on their own shop's products (F16-A D6: they cannot buy them at all). A platform discount applies to every shop that is not the buyer's own, which the cart already guarantees.

## 4. Included behavior

- **Discount fields:** `name` (1 to 100), `code` (3 to 32, upper case letters, digits, `-`, `_`), `type` (`percentage` or `fixed`), `value`, `maxDiscountAmount` (percentage only), `startsOnUtc`, `endsOnUtc`, `minSubtotal`, `maxUses`, `maxUsesPerCustomer`, `enabled`. The funding and the shop come from the route, never from the body.
- **Validation:** percentage above 0 and at most 100; fixed above 0 and within the currency decimals; `endsOnUtc` after `startsOnUtc`; limits 1 to 1,000,000; at most 200 discounts per shop and 500 for the platform.
- **Checking a code at checkout** (`not applicable` reasons, in this order): `not_found` (unknown, or a shop discount whose shop is not in the cart), `disabled`, `not_started`, `expired`, `min_subtotal`, `limit_reached` (total uses), `customer_limit_reached`. A reason is a stable code the screen translates.
- **Amount:** eligible subtotal = whole cart for a platform discount, the lines of the discount's shop for a shop discount. A platform discount is split by subtotal; rounding leftovers go to the largest shop order, so the parts add up to the total exactly.
- **Checkout:** preview and place take an optional `couponCode`. The preview answers the discount (code, name, funding, amount, split) or the reason it does not apply, and `total = subtotal - discount + shipping`. A code that does not apply blocks placing (`coupon_invalid`, field `couponCode`) so the customer never pays something different from what was shown.
- **Orders:** the order carries `DiscountCode` and `DiscountTotal`; each shop order `DiscountAmount` and `DiscountFunding`. The payment is created for the order total after the discount.
- **Admin and seller screens:** a table of discounts (code, kind, value, dates, uses, enabled), a form, enable/disable, delete when unused. The same component serves both routes.
- **Audit:** `discount.created`, `discount.updated`, `discount.deleted`, `discount.redeemed`, `discount.released`.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Discounts on products, categories, manufacturers; max discounted quantity | F15-B |
| Automatic discounts (no code), several discounts at once, cumulative rules | F15-B |
| Customer role, first-order or other requirements; requirement plugins | F15-B |
| Free-shipping and shipping discounts | F15-B |
| Co-funded discounts (a split between platform and shop) | F15-B |
| Gift cards, reward points | F15-C |
| Giving a use back when a shop order is cancelled; refund of the discount | F15-B, F21 |
| Showing codes in the cart before checkout | F15-B |
| Reports on discount usage, settlement of platform-funded amounts | F26, F05 settlement PRD |
| Tax on the discounted amount (done in F07-E: the discount share leaves the tax base) | F07-E |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Discounts/Discount` (`UsePercentage`, `DiscountPercentage`, `DiscountAmount`, `MaximumDiscountAmount`, `StartDateUtc`, `EndDateUtc`, `RequiresCouponCode`, `CouponCode`, `DiscountLimitationId`, `LimitationTimes`) | The discount | Coupon code always required; one limit pair (total, per customer); no cumulative flag |
| `Domain/Discounts/DiscountType` (`AssignedToOrderTotal`, `AssignedToSkus`, ...) | What it applies to | Only "order total" in two flavours: whole cart (platform) or one shop (shop) |
| `Domain/Discounts/DiscountUsageHistory` | Usage | `DiscountUsage` rows tied to the order, written atomically with the limit check |
| `Nop.Services/Discounts/DiscountService` (`ValidateDiscountAsync`, `GetAllDiscountsAsync`, `GetPreferredDiscount`) | Validation | Pure rules with explicit reason codes |
| `Domain/Discounts/DiscountRequirement` and plugins | Requirements | Deferred |
| `OrderTotalCalculationService.GetDiscountAmountAsync` | Amount | Percentage with cap, or fixed, never above the eligible subtotal |
| `Order.OrderDiscount`, `AppliedDiscounts` | On the order | `DiscountCode`, `DiscountTotal` and per shop funding |
| Vendor-level discounts | Not in nopCommerce | Shop-funded discounts (marketplace requirement) |

## 7. Data model

Migration `202610190001 DiscountMigration`:

**`Discount`**: `Id`, `VendorId` int null FK `Vendor` (null = platform), `Name` (100), `Code` (32, upper case), `Type` int (0 percentage, 1 fixed; check), `Value decimal(18,4)` (check above 0), `MaxDiscountAmount` decimal null, `StartsOnUtc` null, `EndsOnUtc` null, `MinSubtotal` decimal null, `MaxUses` int null, `MaxUsesPerCustomer` int null, `UsedCount int` (default 0), `Enabled bit`, `CreatedOnUtc`, `UpdatedOnUtc`. Unique `UX_Discount_Code`; index `(VendorId, Id)`; checks for the date order, limits above 0 and `UsedCount >= 0`.

**`DiscountUsage`**: `Id`, `DiscountId` FK (no action), `OrderId` int null FK `CustomerOrder` (set null), `CustomerId` FK, `Amount decimal(18,4)`, `CreatedOnUtc`. Index `(DiscountId, CustomerId)`; unique `(OrderId)` where not null (one code per order).

**`CustomerOrder`** gains `DiscountCode nvarchar(32)` null and `DiscountTotal decimal(18,4)` default 0; **`ShopOrder`** gains `DiscountAmount decimal(18,4)` default 0 and `DiscountFunding nvarchar(10)` null (`platform` or `shop`). The amount checks become `Total = Subtotal - DiscountTotal + ShippingTotal` and `Total = Subtotal - DiscountAmount + ShippingFee`, with `0 <= discount <= subtotal`.

Permission `discounts.manage` is added and given to the Administrator role. `Down()` restores the old checks and drops the columns, the tables and the permission.

## 8. Use cases and service contracts

```text
DiscountRules (pure)   NormalizeCode, Evaluate(discount, usage, nowUtc, shopSubtotals) -> reason or amount, Split(amount, shopSubtotals)
IDiscountStore         list for a scope, get, insert, update, delete, find by code, count usage (total, per customer),
                       TryRedeem (lock, check limits, insert usage, bump count), Release(orderId)
IDiscountService       admin/shop: List, Create, Update, SetEnabled, Delete        (scope = platform or a shop)
                       checkout:   EvaluateAsync(code, customerId, shopSubtotals) -> applied discount or reason
                                   RedeemAsync(discount, customerId, orderId, amount)    ReleaseAsync(orderId)
IOrderService          CreateAsync accepts the discount (code, funding, amount per shop) and keeps the totals consistent
ICheckoutService       preview and place take couponCode
```

Field errors (`400`): `name`, `code`, `type`, `value`, `maxDiscountAmount`, `startsOnUtc`, `endsOnUtc`, `minSubtotal`, `maxUses`, `maxUsesPerCustomer`, `couponCode`. Business codes (`409`): `discount.code_exists`, `discount.limit`, `discount.in_use`, `checkout.coupon_unavailable`. Not found: discount of another scope.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{vendorId}/discounts` | Members only; `404` for others |
| 2 | `POST` | `/api/v1/vendors/{vendorId}/discounts` | |
| 3 | `PUT` | `/api/v1/vendors/{vendorId}/discounts/{id}` | |
| 4 | `DELETE` | `/api/v1/vendors/{vendorId}/discounts/{id}` | `409 discount.in_use` once used |
| 5 | `GET` | `/api/v1/admin/discounts` | `discounts.manage`; platform discounts |
| 6 | `POST` | `/api/v1/admin/discounts` | |
| 7 | `PUT` | `/api/v1/admin/discounts/{id}` | |
| 8 | `DELETE` | `/api/v1/admin/discounts/{id}` | |
| 9 | `POST` | `/api/v1/checkout/preview`, `/place` | Optional `couponCode`; the preview answers `discount` |

Write routes need the CSRF token like every other write.

## 10. Angular

- `core/discounts/discount-api.service.ts` and models; a shared `DiscountManagerComponent`; pages `/vendor/discounts` and `/admin/discounts`; links in the seller portal and admin menu.
- Checkout page: a code field with "Apply", the discount line in the summary, the reason when a code does not apply; below it a "Choose a discount code (n usable)" button that opens a dialog with the codes for the lines being bought (D11), each code with a radio: ticking a usable code disables the others (one code per order; untick to choose another, which leaves room for several kinds of code later) and "Use code" applies it and closes the dialog, the others disabled with what is missing ("Buy X more", "Valid from ...").
- Order totals (customer, shop, admin) show the discount line and, for shops, who funded it.
- States: loading, empty, saving, field errors, code exists, limit, in use, network error.

## 11. Events, jobs, cache

None. Old usage rows stay (F29 may archive).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Percentage and fixed; cap; never above the eligible subtotal; rounding; split adds up exactly; every reason in order; shop discount needs its shop in the cart; dates are inclusive of the start and exclusive of the end |
| Admin and seller | Validation of every field; code normalised and unique; limits per scope; another scope's discount is not found; delete refused once used; audit |
| Checkout | Preview totals with a code; invalid code blocks placing; amount is the server's; platform split across shop orders; shop discount only on its shop; payment for the discounted total; order totals consistent |
| Redeem | Limits checked atomically (total and per customer); refused redeem cancels the order and returns the stock; a failed payment releases the use; replay does not use the code twice |
| Orders | Totals with discount; the discount never exceeds a shop subtotal; funding recorded |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (the locking redeem, constraints), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run the migrator; check `Discount`, `DiscountUsage`, the new order columns and the new checks, and `discounts.manage` on the Administrator role.
2. As an administrator create a platform discount `WELCOME10` (10 percent, minimum 20). As a member of one shop create `SHOP5` (fixed 5, 1 use per customer).
3. As a customer put products of two shops in the cart and open checkout. Type `WELCOME10`: the discount line appears and the total drops by 10 percent of the items; place the order: the order page shows the discount, and each shop order carries its share.
4. Try `SHOP5` with only the other shop's products: "does not apply". With that shop's products: 5 off that shop only. Place twice with the same customer: the second time the code is refused.
5. Disable a code in its screen: checkout refuses it. Try to delete a used code: refused.
6. Make a payment method fail (the test gateway) with a code applied: the order is cancelled, stock returns, and the code can be used again.

## 13. Rollout and compatibility

Run the migrator before the API. Existing orders get `DiscountTotal = 0`, so the new checks hold for them. Checkout without a code behaves as before.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Product, category and manufacturer discounts; automatic and stacked discounts; requirements | F15-B |
| Co-funded discounts; shipping discounts | F15-B |
| Gift cards and reward points | F15-C |
| Policy for a use when a shop order is cancelled | F15-B |
| Settlement of platform-funded amounts | F05 settlement PRD |
| Discount reports | F26 |
