# F10-A Product ownership

| | |
|---|---|
| **Module ID** | F10-A (slice of F10 "Product core and ownership") |
| **Status** | Backend and Angular implemented. SQL stores and migration not yet run against a real database. |
| **Branch** | `feat/product-ownership/foundation` (backend and frontend) |
| **Depends on** | F05 (shops and members), F09-A (taxonomy rules, `ITaxonomyService`), F03 |
| **Unblocks** | F10-B (review and publication), F11 (product images, variants), F12, F13, F16 |
| **Source PRD** | [vendor-products-prd.md](vendor-products-prd.md). This slice implements its ownership, isolation and basic create/edit parts only (see section 4). |

## 1. Purpose

Before this slice, `Product.VendorId` was a nullable column that any administrator could set or clear, and there were no seller endpoints. The feature map (F05 and F10) requires: *every seller product has a non-null shop*, *a seller can only read and write its own products*, *ShopId is immutable unless a platform admin transfers it*, and *platform-owned products use a dedicated Nomori shop, not `0` or null*.

Audit findings fixed here:

| Finding | Effect before |
|---|---|
| `Product.VendorId` nullable, never validated (any integer accepted) | Products without an owner, or pointing at missing or deleted shops |
| Admin product update always rewrote `VendorId` from the request; the Angular form never sent it | **Saving any product in the admin UI removed its shop link** |
| No seller product endpoints; no ownership check anywhere | Nothing to protect yet, but no pattern to follow |
| Public product list ignored the shop status | Products of deactivated or deleted shops stayed visible |
| Public product responses had no shop information | Storefront could not show who sells a product |

## 2. Actors and authorization matrix

| Action | Platform admin | Member of shop `{id}` | Other shop's member | Customer / anonymous |
|---|---|---|---|---|
| Create product for shop `{id}` | Via admin API (any non-deleted shop, default platform shop) | Own shop only (shop must be active) | `404` | No |
| List, read, update, delete shop `{id}`'s products | Via admin API | Own shop only | `404` | No |
| Change a product's shop (transfer) | **Yes**, dedicated endpoint, audited | No | No | No |
| Set `showOnHomepage`, `displayOrder` | Yes | **No** (kept as is) | No | No |
| See published products of active shops | Yes | Yes | Yes | Yes |

The shop id of a member always comes from the URL and is checked against the session (`VendorCaller.IsMemberOf`), never trusted from the body. A product of another shop returns `404`, never `403`, so its existence is not revealed.

## 3. Included behavior

- **Platform shop.** One vendor row flagged `IsPlatformShop` ("Nomori Official") owns platform products. It cannot be deleted or deactivated. Existing products without a shop are assigned to it by the migration.
- **`Product.VendorId` is required** (`NOT NULL`, foreign key to `Vendor`). The code keeps the name `VendorId`; the feature map's "ShopId" means the same thing.
- **Seller endpoints** under `/api/v1/vendors/{vendorId}/products` (list, read, create, update, delete). Created products always belong to `{vendorId}`.
- **Seller rules:** name required (max 400); price greater than 0; old price `0` or greater than price; stock not negative; categories and manufacturers validated by `ITaxonomyService` with the **seller** audience (F09-A); at most one publish rule: a product can be published only if it has a price above 0 and at least one category.
- **Admin-only fields** (`showOnHomepage`, `displayOrder`) are not accepted from sellers and keep their value on update.
- **Transfer:** `POST /api/v1/admin/catalog/products/{id}/transfer` with `{ vendorId }`. The target must be a non-deleted shop. Audited with old and new shop.
- **Admin create** defaults to the platform shop when `vendorId` is omitted. **Admin update no longer changes the shop**; sending a different `vendorId` returns `400 errors.vendorId` pointing at the transfer endpoint.
- **Shop status on the storefront.** Public product list and detail only return products whose shop is active and not deleted.
- **Shop information on products.** Public and admin product responses include `vendorId` and `vendorName`; the public and admin lists accept `vendorId` (the shop page uses the public one; the admin screen has no shop filter yet).
- **Shop deletion** still soft-deletes the shop; its products disappear from the storefront through the rule above. Deleting the platform shop returns `409 vendor.platform_shop`.
- **Audit:** `product.created`, `product.updated`, `product.deleted`, `product.transferred` with product id, shop id and the acting account; no free text.
- **Angular:** vendor portal page `/vendor/products` (list with search and status filter, create and edit form, delete with confirmation); admin product form gets a shop picker on create and a transfer control on edit; storefront vendor page lists the shop's products and product cards show the shop name.

## 4. Explicit non-goals (deferred)

| Deferred item (from vendor-products-prd.md) | Goes to |
|---|---|
| Four-state lifecycle (draft, live, stopped, hidden by admin), admin hide with reason and email, review request | F10-B |
| Product images (up to 10, ordering, at least one to publish) | F11-A (uses F08-A assets with purpose `product`) |
| Variants, attribute combinations, specifications and tags per product | F11 |
| HTML-formatted descriptions and sanitizing | F10-C. Descriptions are plain text in this slice and Angular escapes them |
| Stock reservation, low-stock warning, stock ledger | F12 |
| Searching by SKU, sort by stock, full filter set | F13 |
| Product copy, templates, related and cross-sell products, per-shop quotas | F10-C |
| Seller-side "under review" publication | F10-B |
| Orders keeping product snapshots | F18 |
| Shop "paused" state in the purchase rule (FR-07) | F16/F17 (there is no cart yet) |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Product.VendorId`, `Domain/Vendors/Vendor` | Ownership column | Required, foreign key, platform shop instead of `0` |
| Admin `ProductController` vendor checks (`GetCurrentVendorAsync`, "not your product" redirects) | Seller cannot touch other vendors' products; vendor cannot set the owner | A separate seller route with `404` for foreign products; transfer is an explicit admin action |
| `ProductService.SearchProductsAsync(vendorId)` | List by shop | `ProductQuery.VendorId` |
| `CatalogSettings`/public catalog rules for active vendors | Hide products of inactive shops | Enforced in the store query |
| Admin `ProductController` (`ShowOnHomepage`, `DisplayOrder` restricted for vendors) | Admin-only fields | Same, enforced in the service |

## 6. Data model

Migration `202610030001 ProductOwnershipMigration`:

- `Vendor.IsPlatformShop bit NOT NULL DEFAULT 0`.
- Insert the platform shop if none exists: name `Nomori Official`, email `platform@nomori.local` (edit it in the admin), `Active = 1`, `IsPlatformShop = 1`.
- `UPDATE Product SET VendorId = <platform shop> WHERE VendorId IS NULL`.
- Drop `FK_Product_Vendor` (it had `ON DELETE SET NULL`), make `Product.VendorId` `NOT NULL`, recreate `FK_Product_Vendor` without `SET NULL` (shops are soft-deleted, so the action never fires; it must not null the column).
- Unique filtered index `UX_Vendor_PlatformShop` on `IsPlatformShop` where `IsPlatformShop = 1`, so at most one platform shop exists.

`Down()` restores the nullable column and the old foreign key, removes the index and column; the platform shop row is left in place. Products that were reassigned to the platform shop stay assigned.

## 7. Use cases and service contracts

```text
IProductService (added)
  GetDetailForVendorAsync(vendorId, productId)              -> ProductDetail? (null if not owned)
  GetListForVendorAsync(vendorId, ProductQuery)             -> PagedResult<Product>
  CreateForVendorAsync(vendorId, SaveVendorProductCommand, actor)   -> CatalogResult<Product>
  UpdateForVendorAsync(vendorId, productId, SaveVendorProductCommand, actor) -> CatalogResult<Product> (not_found | validation)
  DeleteForVendorAsync(vendorId, productId, actor)          -> CatalogResult<bool>
  TransferAsync(productId, newVendorId, actor)              -> CatalogResult<Product>
IProductStore (added)   SetVendorAsync(productId, vendorId)
IVendorStore (added)    GetPlatformShopAsync()
ProductQuery            + VendorId
```

`CatalogErrors` gains `Forbidden` (`403`) for writes to an inactive shop. `IVendorService.DeleteAsync` now returns `VendorResult<bool>` so it can refuse the platform shop.

## 8. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{vendorId}/products?page&pageSize&search&published` | Member of `{vendorId}` |
| 2 | `GET` | `/api/v1/vendors/{vendorId}/products/{id}` | Member |
| 3 | `POST` | `/api/v1/vendors/{vendorId}/products` | Member; shop must be active |
| 4 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}` | Member; shop must be active |
| 5 | `DELETE` | `/api/v1/vendors/{vendorId}/products/{id}` | Member |
| 6 | `POST` | `/api/v1/admin/catalog/products/{id}/transfer` | `catalog.manage` |
| 7 | `GET` | `/api/v1/admin/catalog/products?vendorId=` | `catalog.manage`, new filter |
| 8 | `GET` | `/api/v1/catalog/products?vendorId=` | anyone, new filter |

Seller request (POST and PUT): `{ name, shortDescription, fullDescription, price, oldPrice, stockQuantity, published, categoryIds[], manufacturerIds[] }`.
Seller response: the product fields plus `vendorId`, `published`, `categoryIds`, `manufacturerIds`. Public and admin product responses add `vendorId` and `vendorName`.

| Status | Cases |
|---|---|
| `400` | `errors.name`, `price`, `oldPrice`, `stockQuantity`, `categoryIds`, `manufacturerIds`, `published` (publishing without price or category), `vendorId` (admin update or transfer) |
| `401` | Not signed in |
| `403` | Seller write to an inactive shop |
| `404` | Not a member of `{vendorId}` (including administrators), or the product is not in that shop |
| `409` | `vendor.platform_shop` when deleting the platform shop |

## 9. Angular

- `core/catalog/vendor-product-api.service.ts` for the seller routes; `CatalogApiService` gains `vendorId` on the product list, `transferProduct()` and the new response fields.
- `vendor/pages/vendor-products.page.ts` at `/vendor/products`: list, filter, create, edit, delete. Categories come from `GET /catalog/categories/selectable`; manufacturers from the public list.
- Admin catalog: shop select when creating a product; "Transfer to shop" control when editing; shop name column.
- Storefront: shop name and link on product cards and detail; product list on the shop page.
- Vendor portal page links to the products page.
- States: loading, empty, validation, forbidden, not found, network error.

## 10. Events, jobs, cache

No domain events or jobs. No caching yet (F13). When search and caching arrive they must apply the shop-visibility rule from section 3.

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Ownership | Read, update, delete a product of another shop returns not found; list only returns own products |
| Create | Shop from route; seller cannot set admin-only fields; new product gets defaults |
| Validation | Price, old price, stock, name; publish needs price and category; taxonomy uses seller rules |
| Inactive shop | Seller write refused with forbidden; reads still work |
| Admin | Update with a different `vendorId` rejected; transfer to missing or deleted shop rejected; create defaults to platform shop |
| Platform shop | Delete and deactivate refused |
| Audit | Events written with acting account and shop ids |
| Migration | Version ordering |

Automated: service tests with fakes. **Not automated:** SQL (backfill, NOT NULL change, shop-visibility filter), HTTP authorization pipeline, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`. Check `Vendor` has one row with `IsPlatformShop = 1` and no product has a null `VendorId`.
2. As admin, edit a product in `/admin/catalog` and save without touching anything: its shop stays the same (this was the bug).
3. As admin, try to delete the `Nomori Official` vendor: `409 vendor.platform_shop`.
4. As a member of shop A, open `/vendor/products`, create a product with one category and publish it. It appears on `/storefront/products` with shop A's name.
5. Sign in as a member of shop B and call `GET /api/v1/vendors/{A}/products`: `404`. Call `PUT` on A's product through B's own route: `404`.
6. As shop A's member, pick a category marked "Restrict sellers": `400 errors.categoryIds`.
7. As admin, deactivate shop A: its product disappears from the storefront; shop A's member can still list products but cannot create (`403`).
8. As admin, transfer the product to shop B (`/transfer`): it now appears in B's list; check the `product.transferred` audit entry.

## 12. Rollout and compatibility

- Run the migrator before the API. Products without a shop are assigned to the platform shop.
- Client-visible changes: admin product update ignores or rejects `vendorId`; public product lists hide products of inactive or deleted shops; product responses gain `vendorId` and `vendorName`; `DELETE /vendors/{id}` can return `409` for the platform shop.
- Angular clients must use the new fields; old fields are unchanged.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Lifecycle states, admin hide with reason, review request | F10-B |
| Product images and variants; "at least one image to publish" rule | F11 |
| HTML descriptions, product copy, related products | F10-C |
| Low-stock warning, reservation | F12 |
| SKU search, sorting, caching | F13 |
| Buying rules that depend on shop state ("paused") | F16/F17 |
| Platform shop contact email is a placeholder and must be edited by an admin | Operations note |
