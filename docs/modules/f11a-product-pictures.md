# F11-A Product pictures

| | |
|---|---|
| **Module ID** | F11-A (slice of F11 "Product variants, attributes, specifications and product media") |
| **Status** | Backend and Angular implemented and merged (PR #13). SQL store and migration not yet run against a real database. |
| **Branch** | `feat/product-pictures/foundation` (backend and frontend) |
| **Depends on** | F08-A (media upload, purpose `product`), F10-A (ownership), F10-B (lifecycle) |
| **Unblocks** | F13 (cards with images), F16 (cart line image) |
| **Source PRD** | [vendor-products-prd.md](vendor-products-prd.md), US-A2, US-B1, FR-05, NFR-04 (partly) |

## 1. Purpose

F08-A can already store images with purpose `product`, but nothing attaches them to a product. This slice adds the product-to-picture mapping with an order, the "at least one image" publish rule that F10-B deferred, and shows the main image on seller and storefront screens.

## 2. Actors and authorization matrix

| Action | Member of the product's shop | Platform admin | Other shop / customer |
|---|---|---|---|
| Upload an image (`POST /media`, purpose `product`) | Yes (own shop, F08-A) | No | No |
| Set the pictures of a product | Yes (also while hidden) | No (not in this slice) | `404` |
| Read picture ids of a product | Yes (own) | Yes (admin product read) | Public: only for live products |

## 3. Included behavior

- A product has an **ordered list of pictures**; the first is the **main picture**. Maximum **10**.
- The seller sets the whole list in one call (add, remove and reorder together).
- Every picture must be a media asset with purpose `product`, uploaded for the **same shop** as the product, and **not attached to another product**. Duplicates in the list are refused.
- Seller **publish** needs at least one picture (`400 errors.pictureIds`). A **live** product cannot lose its last picture.
- Removing a picture from a product leaves the asset unattached, so the existing media delete works on it.
- Deleting a product (soft delete) releases its pictures.
- Media delete returns `409 media.in_use` while a product still uses the picture (extends F08-A).
- Responses: seller and storefront product detail carry `pictureIds` (ordered). Seller and storefront lists carry `mainPictureId` (`0` when none). Image bytes still come from `GET /media/{id}`.
- Audit: `product.pictures_changed` (ids and count only).
- Angular: the seller product form has a pictures section (upload several, move earlier or later, remove, first is marked main); the product list and storefront cards and detail show the main picture.

## 4. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Thumbnails and resized variants | F08-B |
| Alt text, titles, SEO file names | F08-B / F24 |
| Videos and 3D objects | F08-B / F11 later slice |
| Pictures for variant combinations | F11-B |
| Administrators uploading or editing product pictures | Not planned (admin keeps the existing form) |
| "At least one image" for the admin Published checkbox | Not enforced; admin is a trusted legacy path |
| Orphan cleanup for uploaded but never attached images | F29 |
| Product copy that duplicates pictures | F10-C |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Catalog/ProductPicture` (`ProductId`, `PictureId`, `DisplayOrder`) | Mapping with order | Unique per picture (a picture belongs to one product); no per-row attributes |
| `ProductService.InsertProductPictureAsync` and Admin `ProductController` picture actions | Add, reorder, remove | One replace-all call with ownership checks; nopCommerce lets any picture id be mapped |
| `CatalogSettings`/`MediaSettings` picture limits | Limits | Fixed maximum of 10 (PRD) |

## 6. Data model

Migration `202610050001 ProductPictureMigration`:

**`ProductPicture`**

| Column | Type | Notes |
|---|---|---|
| `MediaAssetId` | `int` PK, FK to `MediaAsset` | One picture belongs to at most one product |
| `ProductId` | `int` FK to `Product` | |
| `DisplayOrder` | `int` | Zero-based; `0` is the main picture |

Index `IX_ProductPicture_ProductId (ProductId, DisplayOrder)`. `Down()` drops the table. No data backfill: existing products have no pictures, so **live products without pictures stay live**; the rule applies the next time a seller changes their status.

## 7. Use cases and service contracts

```text
IProductStore (added)
  GetPictureIdsAsync(productId)                       ordered
  GetMainPictureIdsAsync(productIds)                  dictionary productId -> mediaAssetId
  GetPictureOwnerAsync(mediaAssetId)                  product id or null
  SetPicturesAsync(productId, mediaAssetIds)          replace all in one transaction
IProductService (added)
  SetPicturesForVendorAsync(vendorId, productId, int[] pictureIds, actor)
  GetMainPictureIdsAsync(productIds)
ProductDetail           + PictureIds
```

`ProductService` receives `IMediaStore` to validate assets. Business errors: `400 errors.pictureIds` (more than 10, duplicate, unknown, wrong purpose, other shop, used by another product, empty list on a live product). Not found and forbidden follow F10-A (`404`, `403` for an inactive shop).

## 8. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}/pictures` body `{ "pictureIds": [12, 15] }` | Member |
| 2 | `GET` | `/api/v1/vendors/{vendorId}/products/{id}` | adds `pictureIds` |
| 3 | `GET` | `/api/v1/vendors/{vendorId}/products` | adds `mainPictureId` per item |
| 4 | `GET` | `/api/v1/storefront/products`, `/products/{id}` | adds `mainPictureId`; detail adds `pictureIds` |

Call 1 answers `200 { pictureIds }`. A body without `pictureIds` is treated as an empty list.

## 9. Angular

- `VendorProductApiService.setPictures()`; `VendorProduct.pictureIds` and `mainPictureId`.
- `vendor-products.page.ts`: pictures section in the edit form (upload with `MediaApiService`, earlier/later buttons, remove) saved with the Save button; thumbnail in the list.
- Storefront product card and detail use `mainPictureId` / `pictureIds`.
- States: uploading, error (size, type, limit reached, network), empty (no picture, publish disabled with a hint).

## 10. Events, jobs, cache

None. Image URLs are immutable and already cached by F08-A.

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Rules | Up to 10; 11 refused; duplicates refused; unknown, wrong purpose, other shop's asset, asset of another product refused |
| Order | Order is stored; first is main; reorder works |
| Publish | Publish without picture refused; with picture allowed; live product cannot clear pictures; stopped product can |
| Isolation | Other shop's member gets not found; inactive shop forbidden |
| Hidden | Seller can change pictures of a hidden product |
| Audit | `product.pictures_changed` holds ids only |
| Migration | Version ordering |

Automated: service tests with fakes. **Not automated:** SQL, HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check `ProductPicture`.
2. As a shop member, create a draft with a category and price; try to publish: `400 errors.pictureIds`.
3. Upload two images and save them; publish works; the list shows the first as the thumbnail.
4. Move the second image first and save: the thumbnail changes.
5. Try to remove all images while live: `400`. Stop the product, then remove them: allowed.
6. Try to attach a picture uploaded by another shop: `400`.
7. Delete an unattached picture through `DELETE /media/{id}`: `204`; an attached one: `409 media.in_use`.
8. Open the storefront: card and detail show the image.

## 12. Rollout and compatibility

Run the migrator before the API. Seller responses only gain fields. Live products without pictures are not changed automatically.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Thumbnails, alt text | F08-B |
| Variant pictures, specification and tags | F11-B |
| Orphan image cleanup | F29 |
| Publication windows, HTML descriptions, product copy, related products | F10-C |
