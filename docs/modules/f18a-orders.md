# F18-A Orders, shop orders and their lifecycle

| | |
|---|---|
| **Module ID** | F18-A (slice of F18 "Orders, customer order history and seller order work") |
| **Status** | Backend and Angular implemented; 532 service tests pass, the Angular build, lint and 30 unit tests pass. SQL store, migration, HTTP and screens not yet run against a real database. |
| **Branch** | `feat/orders/foundation` (backend and frontend) |
| **Depends on** | F02 (customers), F05 (shops, members), F03 (permissions, audit), F07-A (primary currency), F16-A (cart shape), F20-A (shipping option), F19-A (payment method name) |
| **Unblocks** | F17 (checkout builds the order), F21 (returns refer to order lines), F19 wiring (a payment is `order` + id), settlement (completed shop orders) |
| **Feature map** | "introduce ShopOrder or fulfilment groups if one checkout spans shops. Seller views must restrict order lines, customer address disclosure, notes, refunds, and reports to their shop." "Persist the price snapshot on order lines." |
| **Product requirements** | [vendor-orders-prd.md](vendor-orders-prd.md) (lifecycle, roles, screens) |

## 1. Purpose

Everything before this slice ends at a cart. This slice adds the **order**: what the customer bought, split into one **shop order** per shop, with a price snapshot on every line, and the lifecycle each party may drive (shop confirms and ships, customer cancels or confirms receipt, administrator intervenes). Placing an order from a cart is F17; this slice provides the service checkout will call, and every screen that reads or moves an order afterwards.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **One `Order` per checkout, one `ShopOrder` per shop** (PRD D1, feature map F18). Shops only ever see their own shop order. A shop order has its own number `<order number>-<n>`. |
| D2 | **Orders are created by the service, never by a client route.** `IOrderService.CreateAsync` takes what checkout decided (shops, lines with prices, chosen shipping, address, payment method) and **recomputes every total itself**; a total sent by a caller does not exist. A unique placement key makes the call idempotent (F17 sends its idempotency key). |
| D3 | **Snapshots, not links.** Lines keep the product name, variant label, SKU, picture, unit price and quantity at the time of purchase; shop orders keep the shop name and the shipping method name and fee; the order keeps the recipient name, phone and address. Later changes to products, shops or the address book never change an order. |
| D4 | **Money is in the primary currency of the time**; the order stores its currency code. Amounts follow the currency decimals (F07-A). |
| D5 | **The status machine is in `OrderRules`** and is applied with compare-and-set (`WHERE Status = expected`) together with its history row in one transaction, so two people acting on the same shop order cannot both win (PRD NFR-03). |
| D6 | **Lifecycle** (PRD section 5): `pending` → `confirmed` → `shipped` → `delivered` → `completed`; `cancelled` from `pending`, `confirmed`, and (administrator only) `shipped` and `delivered`. `completed` is set by the system only (no caller yet, F29). |
| D7 | **Who may do what:** shop member confirms, ships, edits tracking while shipped, marks delivered, cancels while `pending` or `confirmed`. Customer cancels while `pending` and confirms receipt while `shipped`. Administrator cancels before `completed`. Every cancel needs a reason. |
| D8 | **A shop sees the recipient's name, phone and address (it has to ship) and the customer's note, but never the customer's email**, which is not stored on the order at all. Another shop's order is `404`. |
| D9 | **The order status is derived** from its shop orders: `processing` while one is not finished, `completed` when all are completed or cancelled with at least one completed, `cancelled` when all are cancelled. It is not stored. |
| D10 | **History is the audit trail of an order.** Every change writes who (customer, shop member, administrator, system), when, from, to and the reason or tracking text. It is shown to the shop and the customer; the administrator's name is shown as "Platform". |

## 3. Actors and authorization matrix

| Action | Customer (owner) | Shop member | Administrator (`orders.manage`) |
|---|---|---|---|
| List and open own orders | Yes | no | Yes (all) |
| List and open the shop orders of a shop | no | Own shop only (`404` otherwise) | Yes (all) |
| Confirm, ship, edit tracking, mark delivered | no | Own shop only | no |
| Confirm receipt (`shipped` → `delivered`) | Yes | Yes (see D7) | no |
| Cancel | `pending` only | `pending`, `confirmed` | Before `completed` |

The customer id and the shop id always come from the session and the route, never from a body. A shop order of another customer is `404`.

## 4. Included behavior

- **Create (service only):** at least one shop, at most 20; each shop at least one line, at most 50 lines per shop; quantity 1 to 10,000; unit price 0 or more; shipping fee 0 or more; all amounts within the currency decimals; recipient name, phone, address line, city and a two-letter country required; a shop appears once; a placement key (1 to 100 characters) already used returns the order it created, with different content a conflict. Totals: line total = unit price × quantity, shop subtotal = sum of lines, shop total = subtotal + shipping fee, order totals = sums of shop orders. Every shop order starts `pending` with a history row.
- **Shop actions:** `confirm`; `ship` with carrier (1 to 100 characters) and tracking number (1 to 100); `tracking` change while shipped; `deliver`; `cancel` with reason.
- **Customer actions:** `cancel` with reason, `confirm-receipt`.
- **Administrator action:** `cancel` with reason.
- **Lists:**
  - customer: own orders, newest first, with their shop orders;
  - shop: shop orders of the shop, filter by status, search (shop order number, recipient name or phone), date range, counts per status for the tabs;
  - administrator: orders, filter by shop, status, date range, search by order number.
- **Audit log:** `order.created`, `order.shop_order_changed` (ids, from, to). History rows carry the detail.
- **Angular:** `/customer/orders`, `/customer/orders/:id`, `/vendor/orders`, `/vendor/orders/:id`, `/admin/orders`.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Placing an order from the cart, stock reservation and commit, payment creation | F17 |
| **Giving stock back on cancel** (nothing is taken at creation yet, so nothing can be given back) | F17 |
| Refund when a paid shop order is cancelled | F17, F21 |
| Automatic cancel, deliver and complete after a time | F29 |
| Emails to shops and customers | F22 |
| Shipment records, several parcels, carrier list, tracking links | F20-B |
| Returns, refunds requested by customers | F21 |
| Invoices and PDFs, sales reports, bestsellers | F18-B, F26 |
| Settlement and commission of completed shop orders | F05 settlement PRD |
| Order editing after placement, recurring orders, order notes written by hand | F18-B |
| Failed-delivery return flow (`shipped` back to the shop) | F20-B |
| Administrator changing recipient data | F18-B |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Orders/Order` (`CustomOrderNumber`, totals, `OrderStatus`, billing and shipping address ids) | Order | Recipient address is copied (snapshot), not linked; no billing address yet; status derived |
| `Domain/Orders/OrderItem` (`ProductId`, `AttributesXml`, `UnitPriceInclTax`, `Quantity`, `PriceInclTax`) | Line | Variant label text instead of attribute XML; no tax columns until F07-E |
| `Domain/Orders/OrderNote` | History | Machine-written history with actor and reason; notes by hand are F18-B |
| `Domain/Orders/OrderStatus` (Pending, Processing, Complete, Cancelled) | Statuses | Per shop order with the PRD lifecycle |
| `Nop.Services/Orders/OrderProcessingService` (cancel, mark as shipped/delivered, `CheckOrderStatus`) | Transitions | Table of legal transitions with compare-and-set; derived order status |
| `Nop.Services/Orders/OrderService`, public `OrderController`, Admin `OrderController` | Reads | Seller and customer routes with ownership checks on every identifier |
| `Domain/Shipping/Shipment` | Tracking | Carrier and tracking number on the shop order only; shipments are F20-B |
| Vendor order filtering in Admin `OrderController` | Seller visibility | Dedicated shop routes; the customer email is never stored |

## 7. Data model

Migration `202610180001 OrderMigration`:

**`CustomerOrder`** (the table is not called `Order`, a reserved word)

| Column | Type | Notes |
|---|---|---|
| `Id` | `int` identity | |
| `Number` | `nvarchar(40)` | `NM<yyMMdd>-<id>`; unique (a throw-away value only until the id is known, inside the transaction) |
| `CustomerId` | `int` FK `Customer` | No action |
| `PlacementKey` | `nvarchar(100)` | Unique (idempotency) |
| `CurrencyCode` | `nvarchar(3)` | |
| `Subtotal`, `ShippingTotal`, `Total` | `decimal(18,4)` | Checks `>= 0` and `Total = Subtotal + ShippingTotal` |
| `PaymentMethod` | `nvarchar(50)` | System name of the method chosen |
| `CustomerNote` | `nvarchar(500)` null | |
| `RecipientName` (200), `RecipientPhone` (50), `Address1` (200), `Address2` (200, null), `City` (100), `StateProvince` (100, null), `PostalCode` (20, null), `CountryCode` (2) | | The address snapshot |
| `CreatedOnUtc` | `datetime2` | |

**`ShopOrder`**: `Id`, `OrderId` FK (cascade), `VendorId` FK `Vendor` (no action), `Number` (unique), `ShopName` snapshot, `Status int` (check 0 to 5), `Subtotal`, `ShippingFee`, `Total`, `ShippingMethodName`, `ShippingRateId` int null (no FK: a rate may be deleted), `Carrier` null, `TrackingNumber` null, `CancelReason` null, `CreatedOnUtc`, `UpdatedOnUtc`. Unique `(OrderId, VendorId)`; index `(VendorId, Status, Id)`.

**`OrderLine`**: `Id`, `ShopOrderId` FK (cascade), `ProductId` FK `Product` (no action), `CombinationId` int null, `Name`, `VariantLabel` null, `Sku` null, `PictureId` int, `Quantity` (check 1 to 10,000), `UnitPrice`, `LineTotal`.

**`ShopOrderHistory`**: `Id`, `ShopOrderId` FK (cascade), `FromStatus int` null, `ToStatus int`, `ActorType nvarchar(20)` (`customer`, `shop`, `admin`, `system`), `ActorCustomerId int` null, `Note nvarchar(500)` null, `CreatedOnUtc`. Index `(ShopOrderId, Id)`.

Permission `orders.manage` is added and given to the Administrator role. `Down()` drops the tables and the permission.

## 8. Use cases and service contracts

```text
OrderRules (pure)    Totals(...), Transition(from, action, actor) -> to or null, IsFinal, Overall(statuses), NumberFor(date, id)
IOrderStore          Insert (all rows, one transaction; returns the existing order for a used key), GetOrder, GetShopOrder,
                     list for customer / shop / admin, CountByStatus(vendor), TryTransition (compare-and-set + history),
                     UpdateTracking, GetHistory
IOrderService        CreateAsync                                           (checkout)
                     customer: GetMyOrders, GetMyOrder, CancelAsCustomer, ConfirmReceipt
                     shop:     GetShopOrders, GetShopOrder, Counts, Confirm, Ship, UpdateTracking, Deliver, CancelAsShop
                     admin:    GetOrders, GetOrder, CancelAsAdmin
```

Field errors (`400`): `shops`, `lines`, `quantity`, `unitPrice`, `shippingFee`, `recipientName`, `recipientPhone`, `address1`, `city`, `countryCode`, `placementKey`, `carrier`, `trackingNumber`, `reason`. Business codes (`409`): `order.invalid_transition`, `order.placement_conflict`. Not found: order, shop order (of another shop or customer too).

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/orders?page=&pageSize=` | Customer: own orders |
| 2 | `GET` | `/api/v1/orders/{id}` | Customer: own order, `404` otherwise |
| 3 | `POST` | `/api/v1/orders/shop-orders/{id}/cancel` | `{ reason }` |
| 4 | `POST` | `/api/v1/orders/shop-orders/{id}/confirm-receipt` | |
| 5 | `GET` | `/api/v1/vendors/{vendorId}/orders?status=&search=&from=&to=&page=&pageSize=` | Members only |
| 6 | `GET` | `/api/v1/vendors/{vendorId}/orders/counts` | `{ status: count }` |
| 7 | `GET` | `/api/v1/vendors/{vendorId}/orders/{id}` | Shop order with lines and history |
| 8 | `POST` | `/api/v1/vendors/{vendorId}/orders/{id}/confirm` | |
| 9 | `POST` | `/api/v1/vendors/{vendorId}/orders/{id}/ship` | `{ carrier, trackingNumber }` |
| 10 | `PUT` | `/api/v1/vendors/{vendorId}/orders/{id}/tracking` | Same body, only while shipped |
| 11 | `POST` | `/api/v1/vendors/{vendorId}/orders/{id}/deliver` | |
| 12 | `POST` | `/api/v1/vendors/{vendorId}/orders/{id}/cancel` | `{ reason }` |
| 13 | `GET` | `/api/v1/admin/orders?vendorId=&status=&search=&from=&to=&page=&pageSize=` | `orders.manage` |
| 14 | `GET` | `/api/v1/admin/orders/{id}` | The order with every shop order |
| 15 | `POST` | `/api/v1/admin/orders/shop-orders/{id}/cancel` | `{ reason }` |

Write routes need the CSRF token like every other write. A bad transition answers `409 order.invalid_transition`; the screen reloads the order.

## 10. Angular

- `core/orders/order-api.service.ts` and models; pages `customer/pages/orders.page.ts`, `order-detail.page.ts`, `vendor/pages/vendor-orders.page.ts`, `vendor-order-detail.page.ts`, `admin/pages/admin-orders.page.ts`; links in the account area, seller portal and admin menu.
- States: loading, empty, filters, saving, reason required, transition conflict (reload), forbidden and not found, network error.

## 11. Events, jobs, cache

None. Emails (F22) and the automatic transitions (F29) will hang on the history rows and the status.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Every legal and illegal (status, action, actor); completed only by the system; derived order status |
| Create | Totals recomputed; snapshots kept; validation of every field and limit; decimals; one shop once; same key returns the same order; same key with other content conflicts; numbers; history row |
| Shop actions | Confirm, ship needs carrier and tracking, tracking only when shipped, deliver, cancel needs a reason; other shop's order not found |
| Customer actions | Own order only; cancel only while pending; receipt only while shipped |
| Admin | Cancels shipped and delivered, not completed or cancelled |
| Races | A lost compare-and-set is `order.invalid_transition` and writes no history |
| Views | Shop view has no customer email and no other shop's data; counts per status; filters |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL (transaction, unique keys), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run the migrator; check the four tables, the checks and `orders.manage` on the Administrator role.
2. There is no checkout yet: insert one order with two shop orders by script (the fixture in `OrderTests` shows the shape), one per shop of two test shops, owned by a test customer.
3. As that customer open `/customer/orders`: one order with two shop orders. Cancel one while pending, with a reason.
4. As a member of the other shop open `/vendor/orders`: only its shop order, with the recipient phone and address and no email. Confirm, then ship with carrier and tracking; edit the tracking; as the customer confirm receipt.
5. Open the first shop's order id under the second shop's route: `404`. As the second shop try to cancel a shipped order: refused.
6. As an administrator open `/admin/orders`, filter by shop and status, cancel a delivered shop order with a reason.
7. In two browsers confirm the same shop order together: one succeeds, the other is told to reload.

## 13. Rollout and compatibility

Run the migrator before the API. No existing screen or API changes meaning; the account area, seller portal and admin menu gain links.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Checkout creates the order, takes and gives back stock, links the payment | F17 |
| Refund on cancel; return requests | F19, F21 |
| Emails; automatic transitions | F22, F29 |
| Shipments and several parcels | F20-B |
| Invoices, reports, order notes by hand | F18-B, F26 |
| Settlement of completed shop orders | F05 settlement PRD |
