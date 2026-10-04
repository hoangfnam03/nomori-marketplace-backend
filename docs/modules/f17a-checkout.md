# F17-A Checkout and order placement

| | |
|---|---|
| **Module ID** | F17-A (slice of F17 "Checkout orchestration and order placement") |
| **Status** | Backend and Angular implemented; 584 service tests pass, the Angular build, lint and 30 unit tests pass. The SQL stores and the whole flow have not been run against a real database. |
| **Branch** | `feat/checkout/foundation` (backend and frontend) |
| **Depends on** | F16-A (cart), F20-A (shipping options), F19-A (payments), F18-A (orders), F12-A (stock), F14-A (prices), F04 (saved addresses), F07-C (countries) |
| **Unblocks** | Real sales. F19-B (gateway redirects), F15 (discounts), F07-E (tax), F22 (emails) plug into this flow |
| **Feature map** | "address selection, shipping option selection, payment selection, terms/consents, order totals, order placement validation, idempotency, fraud/risk integration hook, checkout reset, confirmation." "use an idempotency key for place-order; recalculate all server-side totals; persist immutable input and price snapshots; never trust cart totals sent by the client." |

## 1. Purpose

Every piece of a sale exists on its own: a priced cart, shipping options per shop, payment methods and payments, orders split by shop, and stock that can be taken. This slice is the one place that puts them together: the customer picks a saved address, one shipping option per shop and a payment method; the server checks everything again, takes the stock, creates the order, creates the payment and empties the cart. It also closes the debts of the previous slices: stock is now taken when an order is created and **given back when a shop order is cancelled before it ships**.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **Checkout keeps no state on the server.** The choices travel in each request (`addressId`, one `rateId` per shop, `paymentMethod`). A preview and a placement run the same checks, so what the customer saw is what is checked again. There is no "checkout session" to expire or to tamper with. |
| D2 | **The server decides every number.** Prices come from the price service, shipping fees from the shop's rates for the chosen address, totals from the order service. Nothing the browser adds up is read. |
| D3 | **Every shop needs an explicit shipping choice** among the options valid for the address. The preview never picks for the customer; the screen pre-selects the cheapest one and sends it. |
| D4 | **Price changes must be accepted first.** If any line's price differs from the one the customer saw (cart `price_changed`), placing is refused (`409 checkout.prices_changed`) until the customer accepts the new prices in the cart (F16-A D7). Other blocking cart issues (`unavailable`, `variant_unavailable`, `out_of_stock`, `insufficient_stock`) refuse with `409 checkout.cart_not_ready`. |
| D5 | **Order of operations: take stock, create the order, create the payment, empty the cart.** Stock is the scarce thing, so it goes first (reserve every line, then commit all in one transaction). If a later step fails, stock is given back. See section 4. |
| D6 | **Idempotency.** The request carries a key (8 to 64 characters, letters, digits, `-` and `_`), made by the browser once per visit of the checkout page. The order's placement key is `<customer id>:<key>`, so keys of different customers never meet. The same key again returns the order it made (`replayed: true`) and takes nothing twice; the same key with another payment method is a conflict (`order.placement_conflict`). |
| D7 | **Terms must be accepted** (`acceptedTerms`). The acceptance is audited with the placement; a consent history is F04 (GDPR). |
| D8 | **A failed payment cancels the order.** If the payment provider refuses, every shop order is cancelled by the system with the reason "Payment failed", stock goes back, the cart stays and the customer is told (`409 checkout.payment_failed`). |
| D9 | **Cancelling gives stock back, once, only before shipping.** When a shop order goes from `pending` or `confirmed` to `cancelled` (by the shop, the customer, the administrator or the system) its lines go back to stock in the ledger (reason `return`, reference the shop order number). A cancel from `shipped` or `delivered` (administrator only) does not: the goods are out, and taking them back is F21. The compare-and-set of the status makes "once" true. |
| D10 | **Tax (F07-E) is part of the totals.** The order carries the tax of every line, shop order and the order. Discount codes (F15-A) are optional in the same request: `couponCode` in the preview and the placement, totals are items minus the discount plus shipping. |
| D11 | **Only saved addresses can be used.** The recipient of the order is a copy of the saved address at that moment. A new address is added in the account first (F04). |

## 3. Actors and authorization matrix

| Action | Signed-in customer | Guest |
|---|---|---|
| Preview and place an order from their own cart | Yes | `401` |

The customer id always comes from the session. The address must be one of the customer's own (another's is "not found").

## 4. Included behavior

- **Preview** `POST /checkout/preview` with `{ addressId?, shippingChoices?, paymentMethod? }` answers:
  - the cart as priced now (shops, lines, issues);
  - the enabled payment methods;
  - for the address: per shop the shipping options, and the chosen option (or none);
  - `subtotal`, `shippingTotal` and `total` (null while a shipping choice is missing), and `problems` and `canPlace`.
  Problems: `cart_empty`, `cart_issues`, `prices_changed`, `address_required`, `address_invalid`, `shipping_unavailable` (a shop has no option), `shipping_not_chosen`, `shipping_invalid` (a choice that is not an option or not a shop of the cart), `payment_required`, `payment_invalid`.
- **Place** `POST /checkout/place` with the same choices plus `idempotencyKey`, `acceptedTerms`, optional `note` (500 characters):
  1. check the key and the terms; if the key already made an order, return it (replay);
  2. run the preview checks; cart problems are `409`, choice problems are `400` with field errors (`addressId`, `shippingChoices`, `paymentMethod`, `acceptedTerms`, `idempotencyKey`);
  3. reserve stock for every line under the reference `checkout:<placement key>` (15 minutes), then commit all reservations in one transaction; not enough stock is `409 inventory.insufficient_stock` and nothing stays taken;
  4. create the order with the lines priced again, the fees of the chosen options, the address copy and the payment method name;
  5. create the payment (reference `order`, the order id, key `order-<id>`, the order total);
  6. empty the cart.
  If step 4 fails (or throws) the committed stock is returned. If step 5 fails the order is cancelled by the system and stock is returned. Step 6 is last, so a failure leaves the cart as it was.
- **Limits:** at most 1,000 units of a line at checkout (the stock reservation limit); the cart still allows up to 10,000, so a bigger line is refused with a field error telling the customer to lower it.
- **Stock back on cancel** (D9) in `OrderService`, via `IInventoryService.ReturnToStockAsync`, which skips products that do not track inventory and writes one ledger row per line.
- **Audit:** `checkout.placed` (order id, number, total, payment method, terms accepted), plus the existing `order.created`, `payment.created`, `order.shop_order_changed`. A stock return that fails is audited as `order.restock_failed` (the cancel itself stays done).
- **Angular:** `/storefront/checkout` with address, one shipping option per shop, payment method, note, terms and a summary that reads the preview; the cart's "Checkout" button now works; after placing, the customer lands on `/customer/orders/<id>` with a confirmation banner.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Tax-inclusive prices and their display; tax on shipping (F07-E-B; plain tax by destination is F07-E, done) | F07-E-B |
| Gift cards, reward points, free-shipping promotions, automatic discounts (coupon codes are F15-A, done) | F15-B, F15-C |
| Gateway redirects, hosted payment pages, 3-D Secure, "pay again" | F19-B |
| Guest checkout, checkout attributes and gift options | F16-B, F16-C |
| A new address inside checkout; separate billing address | F04 follow-up |
| Pickup points, delivery dates | F20-B |
| Minimum order amount; fraud and risk hook | F17-B |
| Emails (order received, new order for the shop) | F22 |
| Reconciling stock taken by a crashed request, cleaning old reservations | F29 |
| Refunding a paid order on cancel; goods returned after shipping | F21 |
| Terms version and consent log | F04 (GDPR) |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| Public `CheckoutController` (billing, shipping address, shipping method, payment method, confirm) | The steps | One request carries every choice; no per-step session state |
| `Nop.Services/Orders/OrderProcessingService.PlaceOrderAsync` | Validation, totals, order creation | Split in `CheckoutService` and `OrderService`; stock is taken first and compensated |
| `OrderTotalCalculationService` | Totals | Order service computes them from line prices and the chosen fees |
| `ShoppingCartService.GetShoppingCartWarningsAsync` | Cart checks at checkout | Cart issues and the price-change acceptance rule |
| `ProcessPaymentRequest.OrderGuid` | Idempotency of payment | Placement key and payment key `order-<id>` |
| `OrderProcessingService.CancelOrderAsync` / `ReturnBackInStockAsync`-style adjust | Stock back on cancel | Only before shipping, via the ledger |
| `CheckoutSettings` (terms of service, minimum order) | Consent | Terms required; minimum order deferred |

## 7. Data model

No new table and no migration. Changes to what exists:

- `StockMovement` gains rows with reason `return` and the shop order number as reference (the reason already exists).
- `CustomerOrder.PlacementKey` now holds `<customer id>:<key>` (it was free text and unused before this slice).

## 8. Use cases and service contracts

```text
CheckoutRules (pure)   ValidateKey(key), Problems(...) from the inputs, MapToFailure(problems)
ICheckoutService       PreviewAsync(customerId, request), PlaceAsync(customerId, request)
IInventoryService      + ReturnToStockAsync(reference, lines)                  (new)
IOrderStore            + GetByPlacementKeyAsync(key)                           (new)
IOrderService          + FindByPlacementKeyAsync(key), CancelAsSystemAsync(orderId, reason)   (new)
                       cancel actions now return stock (D9)
```

Field errors (`400`): `addressId`, `shippingChoices`, `paymentMethod`, `acceptedTerms`, `idempotencyKey`, `note`, `cart`. Business codes (`409`): `checkout.cart_not_ready`, `checkout.prices_changed`, `checkout.payment_failed`, `inventory.insufficient_stock`, `order.placement_conflict`.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `POST` | `/api/v1/checkout/preview` | Signed-in customer; answers the preview |
| 2 | `POST` | `/api/v1/checkout/place` | `201` with the order and payment; `200` with `replayed: true` for a repeated key |

Both need the CSRF token like every other write. The preview is a `POST` because the choices are a body.

## 10. Angular

- `core/checkout/checkout-api.service.ts` and models; page `storefront/pages/checkout.page.ts`, route `/storefront/checkout`; the cart's button; confirmation banner on the order page.
- States: loading, empty cart (back to the cart), guest (sign in), no address (link to add one), shop that cannot ship to the address, price change (back to the cart), placing, conflict or insufficient stock (back to the cart), payment failed, network error.

## 11. Events, jobs, cache

None. The reservation lasts 15 minutes and is committed in the same request. Emails and reconciliation hang on the audit and the order later (F22, F29).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Key | Valid and invalid keys; the placement key is per customer |
| Preview | Problems for each case; the chosen option is checked against the address options; totals; payment method check; an empty cart |
| Place, happy path | Order made with fresh prices and the chosen fees; stock committed; payment created for the order total; cart emptied; audit |
| Validation | Missing terms; address of another customer; shipping choice missing, invalid, unknown shop; payment missing or disabled; price change not accepted; blocking issue |
| Stock | Not enough stock leaves nothing taken; untracked products take nothing; a line above 1,000 units is refused |
| Idempotency | Same key returns the same order and takes nothing twice; same key with another payment method conflicts; a lost race returns the winner's order |
| Failure | Order failure returns the stock; payment failure cancels the order, returns the stock and keeps the cart |
| Stock back on cancel | Pending and confirmed cancels return stock once; shipped and delivered admin cancels do not; untracked products skipped; a failing return is audited, the cancel stays |
| Migration | None (no schema change) |

Automated: pure rules and service tests with fakes. **Not automated:** SQL transactions, HTTP, Angular, the whole flow. Manual guide below.

### Manual test guide

1. As a customer with a saved shipping address, put products of two shops in the cart. Both shops must have a shipping rate for the address country (F20-A) and the `cod` method must be on.
2. Open `/storefront/cart`, click "Checkout". The shipping options of each shop show; pick one each (the cheapest is pre-selected), pick "Pay on delivery", accept the terms, place the order.
3. You land on the order page with the banner: one order, two shop orders, `pending`. The cart is empty. In the seller portal the stock of the products dropped (stock ledger: `sale`).
4. As the customer cancel one shop order: its stock comes back (ledger `return`). Place again, cancel the other as the shop: same.
5. Confirm and ship a shop order as the shop, then cancel it as an administrator: stock does not come back.
6. Change a price in the seller portal after adding to the cart: "Checkout" shows the price notice and refuses until prices are accepted in the cart.
7. Send the same request twice (replay the call with the same key, for example with the browser tools): the second answer has `replayed: true` and stock dropped once.
8. Turn the `cod` method off in `/admin/payments` and place: refused with a payment method error.

## 13. Rollout and compatibility

No migration. The cart's disabled "Checkout" button becomes active. Orders and payments created from now on are linked by the reference `order` + id. Orders created by hand before this slice (test data) have placement keys without the customer prefix; they are never matched by a new request.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Tax, discounts, gift cards | F07-E, F15 |
| Gateway payments with redirects and callbacks into orders | F19-B |
| Stock taken by a request that crashed between steps; old reservations | F29 |
| Emails, automatic cancel of unconfirmed orders | F22, F29 |
| Refund on cancel, returns | F21 |
| New address in checkout, billing address, guest checkout | F04, F16-B |
| Minimum order, fraud hook, terms version | F17-B |
