# F21-A Return requests and refunds

| | |
|---|---|
| **Module ID** | F21-A (slice of F21 "Returns, refunds and after-sales"): a customer asks to return delivered items, the shop decides and receives the goods, the platform sends the money back |
| **Status** | Backend and Angular implemented; 891 service tests (59 new), 41 data tests, the Angular build, lint and 30 unit tests pass. SQL store, migration and screens have not been run against a real database or a browser (see 12). |
| **Branch** | `feat/returns/foundation` (backend and frontend) |
| **Depends on** | F18-A (orders), F19-A/B (payments, refund), F12-A (stock), F22-A (email queue), F03 (permissions) |
| **Unblocks** | F21-B (exchanges, partial shipping refunds, return shipping labels), F26 (after-sales reports) |
| **Feature map** | "separate request, decision, received-item, refund and restock events. A return must know the shop/order-line it concerns." |

## 1. Purpose

Until now a delivered order could only be completed: a customer with a broken item had no way to ask for their money back inside the system, and the shop had no record of what came back. This slice adds a **return request** on one shop order, with the order lines and quantities it concerns, and a clear path: requested, approved or rejected, goods received (optionally back on sale), refunded.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **A return belongs to one shop order and names its lines.** The customer picks order lines and quantities. A line cannot be returned more often than it was bought, counting every return that is not rejected or withdrawn. The check runs in the service and again inside the write transaction (after a lock on the shop order), so two requests at once cannot both take the last unit. |
| D2 | **Eligibility:** the shop order is delivered or completed, and not longer ago than `Returns:WindowDays` (default 14) since the shop marked it delivered (read from the order history). Completing the order does not close the window. |
| D3 | **The server works out the refund** when the request is made and stores it with the lines: unit price times quantity, minus the line's share of the shop's discount (by value), plus the tax charged on those units, rounded to the currency. Shipping is never refunded. The client cannot send an amount. |
| D4 | **Who decides what.** The shop (or an administrator) approves or rejects and confirms the goods came back. A rejection needs a reason (the customer reads it). The customer can withdraw while the request waits. **Only an administrator sends the money back**: the platform collects the payment. |
| D5 | **Every step is a compare-and-set** (`UPDATE ... WHERE Status = expected`). Two people, or a double click, cannot do a step twice. A lost race is `return.invalid_transition`. |
| D6 | **Refund claims the step before moving money.** The return is moved to refunded first, then the payment is refunded (F19); if the gateway refuses, the step is given back (`received`) and the error is shown. The customer is never refunded twice. |
| D7 | **A refund that cannot go to a gateway is explicit.** With no paid payment that still has enough left, the refund is refused (`return.no_refundable_payment`) and the return stays received. An administrator who paid by hand (cash on delivery, bank transfer) ticks "refunded outside the system" (`manual`): nothing is sent to a gateway and no payment is linked. |
| D8 | **Restock is the shop's choice** at the moment it receives the goods: units go back on sale through `IInventoryService.ReturnToStockAsync` (once, because the receive step is claimed once). Goods that cannot be sold again stay out. A failed stock return leaves the receipt done and is audited (`return.restock_failed`). |
| D9 | **Isolation.** A shop sees only its own returns; a return of another shop, or of another customer, is `404`. The customer, the shop and the actor always come from the session and the route, never from the body. |
| D10 | **Emails are best effort**, through the F22-A queue: approved, rejected and refunded go to the customer (all values HTML-encoded). A failure to queue never undoes the decision. |
| D11 | **Audited:** `return.requested`, `return.status_changed`, `return.refunded`, `return.restock_failed`, plus `payment.refunded` from F19. |
| D12 | **A fix found on the way:** the customer's order page showed "Confirm receipt" for a *shipped* shop order, but the rule needs *delivered* (F18-A), so the button always answered `409`. It is now shown for delivered orders. |

## 3. Actors and authorization matrix

| Action | Customer | Shop member (own shop) | Administrator (`orders.manage`) |
|---|---|---|---|
| Ask to return items of own delivered order | Yes | | |
| See own returns | Yes | | |
| Withdraw while waiting | Yes | | |
| See returns of the shop | | Yes | all shops |
| Approve, reject, mark goods received (with or without restock) | | Yes | Yes |
| Send the refund (online or by hand) | | no | Yes |

Anyone else gets `403` (`401` when not signed in); someone else's return is `404`.

## 4. Included behavior

- **Statuses:** `requested` → `approved` or `rejected`; `approved` → `received`; `received` → `refunded`; `requested` → `withdrawn` (customer). Reasons: damaged, wrong item, not as described, changed my mind, other.
- **Number:** `RT<yyMMdd>-<id>`.
- **Screens:** customer `/customer/returns` (list, withdraw) and `/customer/returns/new/:orderId/:shopOrderId` (choose quantities, reason, note), with a "Return items" button on a delivered or completed shop order; shop `/vendor/returns`; administrator `/admin/returns`. Shop and administrator share one page.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Exchanges, store credit, partial refunds that differ from the computed amount | F21-B |
| Refund of shipping, return shipping labels, who pays for the return shipment | F21-B |
| Photos and a conversation between customer and shop; disputes escalated to the platform | F21-B |
| Automatic refund when the goods are received; settlement adjustment of the shop's payout (F05 reads completed orders and does not yet know about returns) | F21-B, F05 |
| Return of a whole order in one click; return reasons managed by an administrator | F21-B |
| Reports on returns | F26 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `ReturnRequest` (`Quantity`, `ReasonForReturn`, `RequestedAction`, `ReturnRequestStatus`), `ReturnRequestService` | The request and its statuses | Request per shop order with its own lines; the amount is computed; one compare-and-set per step |
| `ReturnRequestController` (public) | Customer request page | Order lines chosen with quantities; window from the delivery date |
| Admin `ReturnRequestController` | Decision and refund | Split between shop (decide, receive) and platform (refund) |
| `ReturnRequestReason`, `ReturnRequestAction` tables | Configurable reasons and actions | Fixed list of reasons; the only action is "refund" (exchanges in F21-B) |

## 7. Data model

Migration `202610290001 ReturnMigration`:

`ReturnRequest`
- `Id` int identity, `Number` nvarchar(40) unique (a throw-away value until the id is known, as for orders), `ShopOrderId` (foreign key), `OrderId`, `ShopOrderNumber`, `VendorId`, `ShopName`, `CustomerId`.
- `Status` int check 0..5, `Reason` nvarchar(30), `CustomerNote`, `ResolutionNote` nvarchar(500) null, `CurrencyCode`, `RefundAmount` decimal(18,4) check >= 0, `Restocked` bit, `PaymentId` int null, `CreatedOnUtc`, `UpdatedOnUtc`.
- Indexes `(CustomerId, Id DESC)`, `(VendorId, Status, Id DESC)`, `(ShopOrderId)`.

`ReturnRequestLine`: `Id`, `ReturnRequestId` (foreign key, cascade), `OrderLineId`, copies of `Name`, `VariantLabel`, `ProductId`, `CombinationId`, `Quantity` check >= 1, `Amount`. Indexes on the request and on the order line.

The order tables are not changed. The statuses of other modules are read as numbers (payment 2 paid and 3 partly refunded) in `SqlReturnStore`.

## 8. Use cases and service contracts

```text
ReturnRules     pure: Transition(from, action, actor), HoldsQuantity, IsEligible(status, deliveredOn, now, window), RefundFor(line, qty, shopOrder, places), NumberFor
IReturnStore    InsertAsync (checks quantities inside the transaction); HeldQuantitiesAsync; GetAsync (with lines); GetPagedAsync
                TryTransitionAsync (compare-and-set); PaymentIdsForOrderAsync
IReturnService  customer: RequestAsync, GetMineAsync (list, one), WithdrawAsync
                shop and administrator (ReturnCaller): GetAsync (list, one), ApproveAsync, RejectAsync, ReceiveAsync(restock)
                administrator: RefundAsync(manual)
```

Business codes (`409`): `return.not_eligible`, `return.invalid_transition`, `return.quantity_exceeded`, `return.no_refundable_payment`; payment codes of F19 pass through (`payment.provider_failed`). Field errors (`400`): `reason`, `note`, `lines`.

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/returns/reasons` | The reason codes |
| 2 | `GET` | `/api/v1/returns` | Own returns, paged |
| 3 | `GET` | `/api/v1/returns/{id}` | Own return |
| 4 | `POST` | `/api/v1/returns` | `{ shopOrderId, reason, note, lines: [{ orderLineId, quantity }] }`, CSRF, `201` |
| 5 | `POST` | `/api/v1/returns/{id}/withdraw` | CSRF |
| 6 | `GET` | `/api/v1/vendors/{vendorId}/returns?status=` | Own shop, paged |
| 7 | `GET` | `/api/v1/vendors/{vendorId}/returns/{id}` | |
| 8 | `POST` | `/api/v1/vendors/{vendorId}/returns/{id}/approve` | `{ note }`, CSRF |
| 9 | `POST` | `/api/v1/vendors/{vendorId}/returns/{id}/reject` | `{ note }` (required), CSRF |
| 10 | `POST` | `/api/v1/vendors/{vendorId}/returns/{id}/receive` | `{ restock }`, CSRF |
| 11 | `GET` | `/api/v1/admin/returns?status=`, `/{id}` | `orders.manage` |
| 12 | `POST` | `/api/v1/admin/returns/{id}/approve`, `/reject`, `/receive` | Same bodies |
| 13 | `POST` | `/api/v1/admin/returns/{id}/refund` | `{ manual }`, CSRF |

## 10. Angular

- Customer: `/customer/returns`, `/customer/returns/new/:orderId/:shopOrderId`; link "My returns" on the orders page; "Return items" on delivered and completed shop orders.
- Shop: `/vendor/returns` (menu "Returns (shop)"). Administrator: `/admin/returns` (menu "Returns", permission `orders.manage`). One shared page (`ReturnsManagePage`, route data `mode`).
- States: loading, empty, load error with retry, busy, `409` (the list is reloaded), network error. English and Vietnamese texts.

## 11. Events, jobs, cache

Emails through the F22-A queue (kinds `return.approved`, `return.rejected`, `return.refunded`). No jobs and no cache. No domain events yet (F29-B).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Allowed and refused steps per actor; window and status eligibility; refund by line (discount share, tax share, rounding, never negative); which statuses hold quantity |
| Request | Refund worked out on the server; unknown reason, missing or duplicate lines, zero quantity, long note refused; item not in the order; someone else's or unknown order is not found; not delivered is refused; completed order inside and outside the window and a longer window; quantity cannot exceed what was bought, a rejected return frees it |
| Customer | Sees only own returns; withdraws only while waiting, not twice, not someone else's |
| Shop and admin | A shop sees and acts only on its own; an administrator sees all and can decide; filter by status; approve tells the customer with the note encoded and cannot be repeated; reject needs a reason and tells the customer; receive needs an approved return; restock puts the units back once, no restock leaves stock alone, a failing stock return is audited, a second receive does nothing |
| Refund | Administrator only, received only; goes through the payment of the order and tells the customer once; two clicks refund once; no paid payment or too little left is refused and the return stays received; a refusing gateway gives the step back and a retry works; by hand moves no gateway money and links no payment |
| Migration | Version ordering |

**Not automated:** SQL (the quantity check under the lock, the compare-and-set, the payment lookup), HTTP, Angular.

### Manual test guide

1. Run the migrator, start the API and the app. Place an order, ship it and mark it delivered as the shop.
2. As the customer open the order: "Confirm receipt" and "Return items" are there. Choose 1 of 2 units of an item, a reason and a note, send. The return shows as waiting with a refund of that unit's price minus its discount share plus its tax.
3. Ask again for 2 units of the same item: `return.quantity_exceeded`. Withdraw the first request, ask again: it works.
4. As the shop open `/vendor/returns`: approve with a note. The customer gets an email and sees "Approved" with the note. Try approving again from a second tab: the list reloads, no second email.
5. Mark goods received with "put back on sale": the product's stock goes up by the returned units, once.
6. As an administrator open `/admin/returns`: "Refund". With the test gateway the payment shows the refund in `/admin/payments` and the return shows "Refunded"; the customer gets an email. Click twice quickly: one refund.
7. For an order paid on delivery (no paid payment), "Refund" answers `return.no_refundable_payment`; tick "refunded outside the system" and refund: the return is refunded and no payment changes.
8. Mark an order delivered 20 days ago (move its history date): the customer cannot ask for a return (`return.not_eligible`).
9. As another customer or another shop open the return by its id: `404`. A shop member cannot call the admin refund (`403`).

## 13. Rollout and compatibility

Run the migrator before the API. Existing orders are unchanged and can be returned from now on, inside the window counted from their delivery date. `Returns:WindowDays` (1 to 365) is validated at start. The shop's payout (F05) does not yet subtract refunded returns: until F21-B, a refunded return of an order that is already settled has to be handled by hand.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Settlement does not subtract refunded returns | F21-B, F05 |
| Exchanges, shipping refund, return labels, photos, messages between the parties | F21-B |
| The quantity check under lock, the compare-and-set and the payment lookup of `SqlReturnStore` are checked by hand only | F26 |
| Reasons are a fixed list | F21-B |
| An order that is cancelled after delivery by an administrator (F18-A) does not close its open returns | F21-B |
