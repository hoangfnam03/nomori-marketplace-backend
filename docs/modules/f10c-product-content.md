# F10-C Product content, identifiers, schedule, related products and copy

| | |
|---|---|
| **Module ID** | F10-C (slice of F10 "Product core and ownership"; closes F10) |
| **Status** | Backend and Angular implemented. SQL store and migration not yet run against a real database. |
| **Branch** | `feat/product-content/foundation` (backend and frontend) |
| **Depends on** | F10-A (ownership), F10-B (lifecycle), F11-A (pictures) |
| **Unblocks** | F11-B (variants use the SKU), F12 (stock keyed by SKU), F13 (search by SKU, related products on cards) |
| **Source PRD** | [vendor-products-prd.md](vendor-products-prd.md), US-A1, US-C1 (search by SKU), NFR-03 |

## 1. Purpose

F10-A to F11-A made a product ownable, publishable and illustrated. Four gaps remain from the feature map (F10) and the PRD: the detailed description is plain text although the PRD wants basic formatting that is safe (NFR-03); a product has no SKU, GTIN or manufacturer part number; a seller cannot schedule when a product goes on sale; and a seller cannot copy a product or link related products.

## 2. Actors and authorization matrix

| Action | Member of the product's shop | Platform admin | Other shop / customer |
|---|---|---|---|
| Edit description, identifiers, schedule (seller save) | Yes | Description only, through the existing admin form | `404` |
| Set related products | Yes (own shop's products only) | No (not in this slice) | `404` |
| Copy a product | Yes | No | `404` |
| Read related products | Yes (own) | Admin product read is unchanged | Public: only live, in-window products of active shops |

## 3. Included behavior

- **Safe HTML description.** `FullDescription` is sanitized on every save (seller and admin) with an allow list: `p br strong b em i u s ul ol li h2 h3 h4 blockquote a`. Links keep `http`, `https` and `mailto` only and get `rel="noopener nofollow"`. Everything else (scripts, styles, event handlers, images, iframes, `javascript:` links) is removed. Maximum 20,000 characters of input (`400 errors.fullDescription`). The stored value is the sanitized one; the short description stays plain text.
- **Identifiers.** `Sku` (max 100), `Gtin` (max 14, digits only, length 8, 12, 13 or 14) and `ManufacturerPartNumber` (max 100), all optional. **A SKU is unique inside one shop** among products that are not deleted (case-insensitive); a second product with the same SKU gets `400 errors.sku`. Different shops may reuse a SKU. The seller list search also matches the SKU (PRD US-C1).
- **Publication window.** Optional `availableStartUtc` and `availableEndUtc`. The end must be after the start (`400 errors.availableEndUtc`). A product is shown on the storefront only when it is `live`, its shop is active and the current time is inside the window (a missing bound is open). Publishing and the window are independent: a live product outside its window is simply not shown, and the seller list says so.
- **Related products.** A product has an ordered list of at most 12 related products. A seller may only relate products **of the same shop**, never the product to itself, no duplicates, none deleted (`400 errors.relatedProductIds`). The relation is one-way (A relates to B does not make B relate to A), as in nopCommerce's `RelatedProduct`. The storefront detail returns the related products that are currently visible, in order. Deleting a product removes the relations that point to it from the public result (the rows stay but are filtered; they are released the next time the seller saves the list).
- **Copy.** `POST .../copy` creates a new **draft** in the same shop named `Copy of <name>` (cut to 400 characters). It copies descriptions, price, old price, stock, categories, manufacturers, GTIN and manufacturer part number. It does **not** copy the SKU (it would break uniqueness), pictures (a picture belongs to one product, F11-A), related products, schedule or status. The seller adds new pictures before publishing.
- **Audit:** `product.related_changed` (ids and count), `product.copied` (`sourceProductId`, new `productId`). Text fields are never written to the audit log.
- **Angular:** the seller form gets SKU, GTIN, part number, window fields, an HTML hint, a related-products picker (own shop's products, order with earlier/later) and a **Copy** button per row; the list shows the SKU and a "Scheduled" or "Expired" badge. The storefront detail renders the sanitized description as HTML and shows a "Related products" strip.

## 4. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Rich text editor (toolbar); the seller types the allowed HTML | Later UI work |
| Images inside the description | F08-B (needs controlled media in content) |
| Cross-sells, grouped products, templates, quotas | Not planned / later F10 work |
| Related products across shops | Not planned |
| Copying pictures and variants; "copy as published" | F11-B, F08-B |
| Administrator editing the new fields in the admin form | Not in this slice; the admin form keeps its fields and never blanks the new ones |
| Bulk SKU import | F27 |
| SKU rules shared across the platform | Not planned (per-shop uniqueness only) |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Product.Sku`, `ManufacturerPartNumber`, `Gtin` | Identifiers | SKU unique per shop; GTIN format checked |
| `Product.AvailableStartDateTimeUtc` / `AvailableEndDateTimeUtc` | Publication window | Same semantics; applied to every public read |
| `Domain/Catalog/RelatedProduct` | Related products | Same shop only; replace-all call |
| `Services/Catalog/CopyProductService` | Product copy | Draft, no pictures, no SKU |
| `Nop.Core/Html` helpers | HTML cleanup | Allow-list sanitizer (`HtmlSanitizer` package) |

## 6. Data model

Migration `202610060001 ProductContentMigration`:

- New nullable columns on `Product`: `Sku nvarchar(100)`, `Gtin nvarchar(14)`, `ManufacturerPartNumber nvarchar(100)`, `AvailableStartUtc datetime2`, `AvailableEndUtc datetime2`.
- Filtered unique index `UX_Product_VendorId_Sku (VendorId, Sku) WHERE Sku IS NOT NULL AND Deleted = 0`.
- New table `ProductRelation (ProductId int, RelatedProductId int, DisplayOrder int)`, primary key `(ProductId, RelatedProductId)`, foreign keys to `Product` (`ProductId` cascade; `RelatedProductId` no action, so product soft-delete is unaffected), index on `ProductId, DisplayOrder`.
- No backfill. Existing descriptions are not rewritten; they are sanitized the next time they are saved (see section 12).

`Down()` drops the table, the index and the columns.

## 7. Use cases and service contracts

```text
IProductStore (added)
  UpdateContentAsync(product)                          Sku, Gtin, ManufacturerPartNumber, window
  IsSkuTakenAsync(vendorId, sku, excludeProductId)
  GetRelatedIdsAsync(productId)                        ordered
  SetRelatedAsync(productId, relatedIds)               replace all
IProductService (added)
  SetRelatedForVendorAsync(vendorId, productId, int[] ids, actor)
  CopyForVendorAsync(vendorId, productId, actor)
  GetVisibleRelatedAsync(productId)                    public: live, in window, active shop
ProductDetail           + RelatedProductIds
Product                 + Sku, Gtin, ManufacturerPartNumber, AvailableStartUtc, AvailableEndUtc, IsAvailableAt(now)
SaveVendorProductCommand + the five fields above
```

Public queries (`OnlyActiveShops = true`) add the window condition in SQL. `HtmlContent.Sanitize` is a static helper in the services project, used by both admin and seller paths.

## 8. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}` | body gains `sku`, `gtin`, `manufacturerPartNumber`, `availableStartUtc`, `availableEndUtc` |
| 2 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}/related` body `{ "productIds": [3, 7] }` | Member |
| 3 | `POST` | `/api/v1/vendors/{vendorId}/products/{id}/copy` | Member, answers `201` with the new product |
| 4 | `GET` | `/api/v1/vendors/{vendorId}/products/{id}` | adds the new fields and `relatedProductIds` |
| 5 | `GET` | `/api/v1/vendors/{vendorId}/products?search=` | search also matches `sku` |
| 6 | `GET` | `/api/v1/catalog/products/{id}` | description is the sanitized HTML; adds `relatedProducts` (list items with `mainPictureId`) |

Call 2 answers `200 { relatedProductIds }`; a body without the list means an empty list. Errors: `400` (`errors.sku`, `errors.gtin`, `errors.availableEndUtc`, `errors.fullDescription`, `errors.relatedProductIds`), `403` inactive shop, `404` not a member or product not in the shop.

## 9. Angular

- `VendorProductApiService`: `setRelated()`, `copy()`; `VendorProduct` and `SaveVendorProductRequest` gain the new fields.
- `vendor-products.page.ts`: new form fields, related picker, Copy button, schedule badge, SKU in the list.
- `product-detail.page.ts`: description through `[innerHTML]`, related strip linking to other products.
- States: validation per field (including duplicate SKU), saving, copy in progress, empty related list.

## 10. Events, jobs, cache

None. No job flips products at the window edges; the window is evaluated on read. When F13 adds caching, entries must expire at the window edges or be keyed by time.

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| HTML | Script, event handlers, `javascript:` links, images and iframes removed; allowed tags kept; links get `rel`; over-limit refused; admin save also sanitizes |
| SKU | Unique per shop (case-insensitive); reuse allowed in another shop; own SKU on update is fine; deleted product frees it; blank becomes null |
| GTIN | Digits only and valid length; invalid refused |
| Window | End before or equal to start refused; `IsAvailableAt` for open, before, inside, after |
| Related | At most 12; self, duplicate, other shop's, deleted, unknown refused; order kept; public result hides stopped, deleted, out-of-window and inactive-shop products |
| Copy | New draft in the same shop; name prefix and cut; SKU, pictures, related and window not copied; categories and manufacturers copied; other shop gets not found; inactive shop forbidden; hidden product can be copied |
| Admin safety | Admin update never blanks the new fields |
| Audit | `product.related_changed` and `product.copied` hold ids only |
| Migration | Version ordering |

Automated: service tests with fakes. **Not automated:** SQL (unique index, window filter), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check the new columns, `UX_Product_VendorId_Sku` and `ProductRelation`.
2. Save a product with description `<p>Hi</p><script>alert(1)</script><a href="javascript:x">x</a>`: reading it back shows only `<p>Hi</p>` and a link without `href`.
3. Give two products of one shop the same SKU: `400 errors.sku`. Use the same SKU in another shop: allowed. Search the seller list by SKU.
4. Set the start in the future on a live product: it disappears from `/storefront/products` and the detail returns `404`. Set a start in the past and an end in the future: it returns. Set the end before the start: `400`.
5. Set related products; open the storefront detail: they show in order. Stop one of them: it disappears from the strip. Relate a product of another shop: `400`.
6. Copy a product: a draft `Copy of ...` appears with categories but no SKU and no pictures. Publish it without pictures: `400 errors.pictureIds`.
7. As a member of another shop call related and copy for this product: `404`.

## 12. Rollout and compatibility

Run the migrator before the API. Seller save bodies only gain optional fields. Old descriptions are not rewritten; a description saved before this slice may still contain markup that was allowed then. The storefront therefore renders it through Angular's `[innerHTML]`, which sanitizes again; the API does not re-sanitize on read.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Re-sanitize old descriptions (one-off job) | F29 |
| Rich text editor and images in descriptions | F08-B / UI |
| Variants and per-combination SKU | F11-B |
| Cache expiry at window edges | F13 |
| Admin form fields for the new data | F10 follow-up |
