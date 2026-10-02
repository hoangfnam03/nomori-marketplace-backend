# F12-A Inventory: stock ledger, adjustments and reservations

| | |
|---|---|
| **Module ID** | F12-A (slice of F12 "Inventory, warehouse and fulfilment availability") |
| **Status** | Backend and Angular implemented. SQL store and migration not yet run against a real database; the concurrency behavior is not automated. |
| **Branch** | `feat/inventory/foundation` (backend and frontend) |
| **Depends on** | F10-A to F10-C, F11-B (combination stock) |
| **Unblocks** | F16 (cart holds stock), F17 (checkout commits it), F13 (availability filter), F12-B (alerts, back in stock) |
| **Source PRD** | [vendor-products-prd.md](vendor-products-prd.md), US-B1 (out of stock), US-C1 (low stock warning), FR-07, FR-08, NFR-02 |
| **Feature map** | F12 "Nomori decision": a mutable `StockQuantity` alone is insufficient under concurrent checkout; start with a stock ledger and a reservation strategy before checkout. |

## 1. Purpose

Until now stock was a number that any save could overwrite, with no history and no protection against two customers buying the last item. This slice makes stock changes go through one audited path (a ledger), guarantees stock never goes negative, and adds a reservation mechanism that the cart and checkout (F16, F17) will use.

## 2. Model in one picture

```text
on hand    = the stored quantity (Product.StockQuantity, or the combination's StockQuantity)
reserved   = sum of ACTIVE, NOT EXPIRED reservations
available  = max(0, on hand - reserved)         what a customer can still buy
```

- **Adjust** changes *on hand* by a signed delta and writes a ledger row.
- **Reserve** holds quantity for a reference (a cart or order id) until an expiry time. It does not change *on hand*.
- **Commit** turns a reservation into a sale: *on hand* decreases, a ledger row is written, the reservation is closed.
- **Release** closes a reservation without a sale.
- Expired reservations simply stop counting; nothing needs to run for that (cleanup of old rows is F29).
- Every check and write happens inside one SQL transaction that locks the stock row (`UPDLOCK, HOLDLOCK`), so concurrent buyers cannot both take the last item.

## 3. Actors and authorization matrix

| Action | Member of the product's shop | Platform admin | Other shop / customer |
|---|---|---|---|
| Read stock levels and history of a product | Yes (own) | Existing admin product read | `404` |
| Adjust stock, change tracking and the low-stock threshold | Yes (also while hidden or draft) | Existing admin form (stock delta goes through the ledger) | `404` |
| Reserve, release, commit | Internal service only (cart and checkout, later) | | No HTTP route yet |
| See available quantity | | | Public detail and attributes: available, not on hand |

## 4. Included behavior

- **Ledger** `StockMovement`: append-only rows with the signed delta, the quantity after, a reason, an optional reference, note, actor and time. Reasons: `initial`, `restock`, `correction`, `damage`, `return` (seller choices), `variants_saved`, `admin_edit`, `sale`.
- **One writer.** The seller product save **no longer changes stock** (the Stock field is read-only when editing, editable only when creating, which writes an `initial` row). The admin form's stock change becomes a delta through the same atomic path (`admin_edit`); it is ignored while the product has variants.
- **Adjustments** (`POST .../stock-adjustments`): a non-zero delta (at most 1,000,000), a reason from the seller list and an optional note (max 500). A product **with variants** needs a `combinationId`; a product without variants must not send one. The result may not make *on hand* negative **or lower than the reserved quantity** (`409 inventory.insufficient_stock`). Adjusting a combination also updates the product's total in the same transaction.
- **Settings:** `TrackInventory` (default on) and `LowStockThreshold` (default 5, 0 to 100,000). A product that does not track inventory is always available and its reservations are no-ops.
- **Reservations** (internal): quantity 1 to 1,000; reference 1 to 100 characters; time to live 1 to 1,440 minutes (default 30). Reserving again with the same reference, product and combination **replaces** the quantity and expiry (idempotent). Not enough available stock gives `409 inventory.insufficient_stock`; commit of an expired or unknown reference gives `409 inventory.reservation_expired`.
- **Variants guard.** Saving variants is refused with `409 inventory.active_reservations` while the product has active reservations, because saving replaces the combinations. The save writes one `variants_saved` ledger row for the change of the product total.
- **Low stock.** Seller product responses carry `trackInventory`, `lowStockThreshold`, `isLowStock` (tracked and on hand at or below the threshold). The seller list filter `lowStock=true` returns them.
- **Public availability.** Product detail returns `availableQuantity` and `trackInventory`; the public attributes response reports each combination's **available** quantity as `stockQuantity`. Lists keep showing on hand.
- **Audit:** `product.stock_adjusted` and `product.inventory_settings_changed` (ids, delta, counts; no free text).
- **Angular:** the seller details page gets an **Inventory** section (levels with on hand, reserved and available, adjustment form, settings, last movements). The product list shows a "Low stock" badge and a filter. The storefront uses `availableQuantity` and treats untracked products as always in stock.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| HTTP routes for reserve, release, commit (the cart is the caller) | F16 / F17 |
| Cart and checkout; expiring carts; "no longer sold" handling | F16 / F17 |
| Back-in-stock subscriptions and low-stock emails | F12-B / F22 |
| Warehouses, multi-location stock, delivery dates, availability ranges | F12-C / F18 |
| Backorders and pre-orders (`AllowOutOfStockOrders` exists on combinations but is not used here) | F12-B |
| Rental and recurring products | Not planned |
| Cleanup of expired reservation rows | F29 |
| Raw admin combination endpoints (`/admin/products/{id}/attributes/combinations`) still write stock without the ledger | F12-B (to be retired) |
| Stock report and export | F26 / F27 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Product.ManageInventoryMethodId`, `LowStockActivity`, `NotifyAdminForQuantityBelow` | Track or not, threshold | Boolean tracking, threshold only (no activities yet) |
| `StockQuantityHistory` | Ledger | Always written, with reference and actor |
| `ProductService.AdjustInventoryAsync` | Decrement on order | Locked, never negative, reservation-aware |
| `ProductWarehouseInventory` | Per-warehouse stock | Deferred |
| `ProductAttributeCombination.StockQuantity` | Variant stock | Same, with the product total kept in sync |

nopCommerce has no reservation; this part is a Nomori addition driven by the feature map decision.

## 7. Data model

Migration `202610070001 InventoryMigration`:

- `Product`: `TrackInventory bit NOT NULL DEFAULT 1`, `LowStockThreshold int NOT NULL DEFAULT 5`.
- **`StockMovement`**: `Id` identity, `ProductId` (FK to `Product`, no cascade), `CombinationId int NULL` (plain id, no FK: combinations are replaced when variants are saved), `Delta int`, `QuantityAfter int`, `Reason nvarchar(50)`, `Reference nvarchar(100) NULL`, `Note nvarchar(500) NULL`, `ActorCustomerId int NULL`, `CreatedOnUtc`. Index `(ProductId, Id)`.
- **`StockReservation`**: `Id` identity, `ProductId` (FK), `CombinationId int NULL`, `Quantity int`, `Reference nvarchar(100)`, `Status int` (`0` active, `1` committed, `2` released), `ExpiresOnUtc`, `CreatedOnUtc`, `UpdatedOnUtc`. Indexes `(ProductId, Status, ExpiresOnUtc)` and a filtered unique index `UX_StockReservation_Active (Reference, ProductId, CombinationId) WHERE Status = 0`.
- No backfill: existing stock stays as it is; the ledger starts empty (history begins with the first change). Existing products track inventory with threshold 5.

`Down()` drops both tables and the two columns.

## 8. Use cases and service contracts

```text
StockRules (pure)       CanAdjust(onHand, reserved, delta), CanReserve(onHand, reservedByOthers, qty), Available(onHand, reserved)
IInventoryStore         GetLevel, GetCombinationLevels, Adjust, RecordMovement, Reserve, Release, Commit,
                        HasActiveReservations, GetMovements, SetSettings
IInventoryService       GetOverviewForVendor, AdjustForVendor, GetMovementsForVendor, SetSettingsForVendor   (seller)
                        ReserveAsync, ReleaseAsync, CommitAsync                                              (internal)
                        GetAvailabilityAsync(productId)                                                      (public)
IProductStore           + TrackInventory, LowStockThreshold on Product; ProductQuery.LowStock
IProductAttributeStore  ReplaceVariantsAsync(productId, command, actorCustomerId)
```

The store reads the locked row, applies `StockRules` in C#, then writes, so the rules are unit-tested and the locking is in SQL. Business codes (`409`): `inventory.insufficient_stock`, `inventory.reservation_expired`, `inventory.active_reservations`. Field errors (`400`): `errors.delta`, `errors.reason`, `errors.note`, `errors.combinationId`, `errors.lowStockThreshold`. Not found and forbidden follow F10-A.

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{vendorId}/products/{id}/inventory` | Member: `{ trackInventory, lowStockThreshold, onHand, reserved, available, combinations: [{ id, sku, label?, onHand, reserved, available }] }` |
| 2 | `POST` | `/api/v1/vendors/{vendorId}/products/{id}/stock-adjustments` body `{ combinationId?, delta, reason, note? }` | Member; answers the same overview |
| 3 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}/inventory-settings` body `{ trackInventory, lowStockThreshold }` | Member; answers the overview |
| 4 | `GET` | `/api/v1/vendors/{vendorId}/products/{id}/stock-movements?page=&pageSize=` | Member, newest first |
| 5 | `GET` | `/api/v1/vendors/{vendorId}/products?lowStock=true` | Member |
| 6 | `GET` | `/api/v1/catalog/products/{id}` | adds `availableQuantity`, `trackInventory` |
| 7 | `GET` | `/api/v1/products/{id}/attributes` | combination `stockQuantity` is the available quantity |

## 10. Angular

- `VendorProductApiService`: `getInventory()`, `adjustStock()`, `setInventorySettings()`, `getStockMovements()`; `VendorProduct` gains `trackInventory`, `lowStockThreshold`, `isLowStock`; `list()` takes `lowStock`.
- `vendor-product-details.page.ts`: Inventory section. `vendor-products.page.ts`: low-stock badge, filter, read-only Stock when editing.
- `product-detail.page.ts`: `availableQuantity` and untracked products.
- States: loading, saving, validation per field, insufficient stock message, empty history.

## 11. Events, jobs, cache

No background work: reservations expire by time comparison. When F13 adds caching, availability must not be cached beyond a short time.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Adjust never below zero or below reserved; reserve needs enough available; reserved by the same reference is excluded; available is never negative |
| Adjust | Delta limits, reason whitelist, note limit; variants need a combination and plain products must not send one; foreign combination refused; audit has ids only |
| Settings | Threshold range; low stock flag; untracked products |
| Reservations | Quantity and reference limits, replace semantics, release, commit success and expired, untracked no-op |
| Create and edit | Create writes `initial`; seller save keeps stock; admin edit writes a delta; admin edit ignored with variants; insufficient admin edit refused |
| Variants | Refused while reservations are active; passes the actor |
| Isolation | Other shop gets not found; inactive shop forbidden for writes; hidden products can be adjusted |
| Public | Detail reports available quantity |
| Migration | Version ordering |

Automated: pure rules and service tests with fakes. **Not automated:** SQL locking and concurrency, HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check the two tables, the two columns and the filtered unique index.
2. Create a product with stock 5: `StockMovement` has an `initial` row. Edit the product: the Stock field is read-only.
3. In Details, Inventory: adjust +10 (`restock`), then -20: `409 inventory.insufficient_stock`. History shows the first change only.
4. Set the threshold to 20: the list shows "Low stock"; the `lowStock` filter returns it.
5. With a product with two combinations adjust each one: the product total in the list is the sum.
6. Concurrency (manual, SQL): from two sessions reserve the last unit with different references at the same time: exactly one succeeds. Reserve, wait for expiry, reserve again: succeeds.
7. Reserve then try to save variants: `409 inventory.active_reservations`. Release, save again: works.
8. Anonymous `GET /catalog/products/{id}` shows `availableQuantity` lower than on hand while a reservation is active.

## 13. Rollout and compatibility

Run the migrator before the API. The seller save body keeps `stockQuantity` but ignores it for updates (it is used for creation). The seller product response gains fields; public detail gains fields. Combination `stockQuantity` in the public attributes response changes meaning from on hand to available.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Reserve, release and commit routes with the cart; reservation cleanup | F16, F17, F29 |
| Back in stock, low-stock notification, backorders | F12-B, F22 |
| Retire raw admin combination stock endpoints | F12-B |
| Warehouses and fulfilment availability | F12-C |
