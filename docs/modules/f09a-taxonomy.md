# F09-A Global taxonomy: categories and manufacturers

| | |
|---|---|
| **Module ID** | F09-A (slice of F09 "Global catalog taxonomy and manufacturers") |
| **Status** | Backend and Angular implemented. SQL stores and migration not yet run against a real database. |
| **Branch** | `feat/taxonomy/foundation` (backend and frontend) |
| **Depends on** | F03, F05 (done), F08-A (images, done) |
| **Unblocks** | F10-A (product ownership: sellers pick categories through the rules defined here), F13 (browsing) |

## 1. Purpose

Category and manufacturer CRUD already existed from the M05 foundation slice. This slice turns it into a **platform-owned taxonomy with enforced rules**, so sellers can later attach products to it safely. The feature map (F09, mandatory decision in section 2.2) says: platform admins create the taxonomy; sellers only select permitted categories.

Audit of the M05 code found these gaps, all fixed here:

| Gap found | Effect before |
|---|---|
| Parent category was never validated | A category could point at a missing category, itself, or create a loop, which breaks the tree |
| Deleting a category or manufacturer ignored children and products | Orphaned children, products pointing at deleted taxonomy |
| Product create/update stored any category or manufacturer id | Mappings to missing, deleted or unpublished records; duplicate rows |
| `ProductCategory` and `ProductManufacturer` had no foreign keys or unique keys | Orphan and duplicate rows possible |
| A published child of an unpublished parent was listed publicly | Public list and tree disagreed |
| No way to say "sellers may not use this category" | Sellers could not be restricted |
| List endpoints accepted any `pageSize` | Unbounded queries |
| Taxonomy changes were not audited | No trace of who changed the global taxonomy |
| Sibling names could be duplicated | Confusing duplicate categories |

## 2. Actors and authorization matrix

| Action | Platform admin (`catalog.manage`) | Shop member (`vendor.portal`) | Customer / anonymous |
|---|---|---|---|
| Create, update, delete category or manufacturer | Yes | **No** (no `catalog.manage`) | No |
| Read full tree including unpublished | Yes (`/admin/catalog/categories/tree`) | No | No |
| Read public categories and manufacturers | Yes | Yes | Yes |
| List categories a seller may attach products to | | Yes (`/catalog/categories/selectable`) | No |
| Attach a product to categories and manufacturers | Any existing, non-deleted record | Only those passing the seller rules in section 3 (used by F10-A) | No |

The `Vendors` role has no `catalog.manage`, so seller accounts cannot reach the admin taxonomy endpoints. Seller endpoints get their own ownership checks in F10-A.

## 3. Included behavior

**Tree integrity** (category create and update)
- `ParentCategoryId` is `0` (root) or an existing, non-deleted category.
- A category cannot be its own parent or move under one of its own descendants.
- A name is unique among non-deleted siblings (same parent), case-insensitive. Applies to create, rename and move.

**Delete rules**
- A category with non-deleted children returns `409 category.has_children`.
- A category or manufacturer still mapped to a non-deleted product returns `409 category.in_use` or `409 manufacturer.in_use`.
- Delete remains a soft delete.

**Seller restriction** (`Category.RestrictFromVendors`, from nopCommerce `Category.RestrictFromVendors`)
- When `true`, sellers cannot attach products to that category. Admins still can.
- Sellers may only use a category that is published, not deleted and has **every ancestor published**.
- Manufacturers are not restricted per seller in this slice (any published manufacturer).

**Selection validation** (`ITaxonomyService.ValidateSelectionAsync`, used by product create/update now and by F10-A for sellers)
- Duplicates are removed; at most 10 categories and 10 manufacturers per product.
- Every id must exist and not be deleted. Sellers additionally must satisfy the rules above and use published manufacturers.
- Errors are reported per field: `categoryIds`, `manufacturerIds`.

**Public visibility**
- Public category list, detail and tree only show a category when it and all its ancestors are published.
- `pageSize` is clamped to 1 to 100 on every list endpoint.

**Mapping integrity**
- Foreign keys from `ProductCategory` and `ProductManufacturer` to `Product`, `Category`, `Manufacturer`, and unique `(ProductId, CategoryId)` / `(ProductId, ManufacturerId)`.
- Replacing a product's mappings runs in one transaction.

**Audit**: `catalog.category_created|updated|deleted` and `catalog.manufacturer_created|updated|deleted` with entity id and changed field names (no free text).

**Angular**
- Category form: parent chosen from a tree-based select (no more typing an id), "Restrict sellers" checkbox.
- Product form: category and manufacturer checklists replace comma-separated ids.
- Conflict and validation messages shown inline.

## 4. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Category and manufacturer **slugs, SEO fields, redirects** | F24 |
| Category/manufacturer **templates**, page-size options, price-range filters | F13 |
| Showing **descendant categories' products** when browsing a category | F13 |
| Per-vendor allow lists, restriction of individual manufacturers | Product decision, not scheduled |
| Access control list (ACL) and store mapping on categories | F03 (ACL) / F06 (deferred) |
| Localization of names and descriptions | F07 |
| Discounts attached to categories/manufacturers | F15 |
| Maximum tree depth | Not needed yet |
| Import/export of taxonomy | F27 |
| Hard delete and restore | Not planned |
| Seller "shop collections" (shop-specific navigation) | F05 follow-up (`ShopCollection`) |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Catalog/Category` (`ParentCategoryId`, `Published`, `Deleted`, `DisplayOrder`, `RestrictFromVendors`) | Entity and the seller restriction flag | Templates, meta, page-size, price range, ACL, store mapping omitted |
| `Services/Catalog/CategoryService` (hierarchy, `GetAllCategoriesAsync(showHidden)`) | Tree and visibility behavior | Ancestor-published rule enforced in SQL; cycle check added (nopCommerce leaves the admin to avoid it) |
| `Domain/Catalog/Manufacturer`, `ProductManufacturer`, `ProductCategory` | Entities and mappings | Adds foreign and unique keys |
| Admin `CategoryController`, `ManufacturerController` | Admin CRUD | REST API with conflict codes |
| `Nop.Services/Catalog/ProductService` (category mapping) | Product-category attach | Central validator shared by admin and seller paths |
| Admin `CategoryController.ImportFromXlsx` ("a vendor cannot import categories") | Principle | Generalized: sellers have no taxonomy write access at all |

## 6. Data model

Migration `202610020001 TaxonomyMigration`:

- `Category.RestrictFromVendors bit NOT NULL DEFAULT 0`.
- Cleanup before constraints: delete `ProductCategory` and `ProductManufacturer` rows whose product, category or manufacturer no longer exists, then delete duplicate `(ProductId, CategoryId)` and `(ProductId, ManufacturerId)` rows keeping the lowest `Id`. **This deletes orphan and duplicate rows**; such rows were already meaningless.
- Foreign keys (`ON DELETE CASCADE`): `FK_ProductCategory_Product`, `FK_ProductCategory_Category`, `FK_ProductManufacturer_Product`, `FK_ProductManufacturer_Manufacturer`.
- Unique indexes: `UX_ProductCategory_Product_Category`, `UX_ProductManufacturer_Product_Manufacturer`.
- `Category.ParentCategoryId` keeps `0` as the root marker (same as nopCommerce), so it has no foreign key; the service enforces it.

`Down()` drops the indexes, foreign keys and column. Cleaned rows are not restored.

## 7. Use cases and service contracts

```text
ICategoryService (changed)
  CreateAsync / UpdateAsync            + parent, cycle, sibling-name rules; RestrictFromVendors
  DeleteAsync(id)                      -> CatalogResult<bool>  (not_found | category.has_children | category.in_use)
  GetTreeAsync()                       public tree, ancestors published
  GetAdminTreeAsync()                  full tree with published/restricted flags
  GetSelectableForSellersAsync()       flat list with path, sellers only
ICategoryStore (added)
  GetAllAsync, CountChildrenAsync, CountProductsAsync, NameExistsAsync
IManufacturerService / IManufacturerStore (added)
  DeleteAsync -> CatalogResult<bool> (manufacturer.in_use); CountProductsAsync, NameExistsAsync, GetByIdsAsync
ITaxonomyService (new)
  ValidateSelectionAsync(categoryIds, manufacturerIds, TaxonomyAudience) -> CatalogResult<TaxonomySelection>
IProductStore (changed)
  SetCategoriesAsync / SetManufacturersAsync run in one transaction
```

`CatalogResult<T>` gains an optional `ErrorCode` (`not_found` to 404, other codes to 409). Existing callers are unaffected.

## 8. API

| # | Method | Route | Who | Notes |
|---|---|---|---|---|
| 1 | `GET` | `/api/v1/catalog/categories` and `/{id}` | anyone | Ancestors must be published; `pageSize` 1 to 100 |
| 2 | `GET` | `/api/v1/catalog/categories/tree` | anyone | Same rule |
| 3 | `GET` | `/api/v1/catalog/categories/selectable` | `vendor.portal` | **New.** Flat `{ id, name, parentCategoryId, path }` |
| 4 | `GET` | `/api/v1/admin/catalog/categories/tree` | `catalog.manage` | **New.** Includes unpublished and restricted flags |
| 5 | `POST` `PUT` | `/api/v1/admin/catalog/categories` | `catalog.manage` | Adds `restrictFromVendors`; new error cases |
| 6 | `DELETE` | `/api/v1/admin/catalog/categories/{id}` | `catalog.manage` | `409 category.has_children`, `409 category.in_use` |
| 7 | `DELETE` | `/api/v1/admin/catalog/manufacturers/{id}` | `catalog.manage` | `409 manufacturer.in_use` |
| 8 | `POST` `PUT` | `/api/v1/admin/catalog/products` | `catalog.manage` | `400 errors.categoryIds / manufacturerIds` for bad selections |

| Status | Cases |
|---|---|
| `400` | `errors.parentCategoryId` (missing, self, descendant), `errors.name` (duplicate sibling), `errors.pictureId`, `errors.categoryIds`, `errors.manufacturerIds` |
| `404` | Unknown id |
| `409` | `category.has_children`, `category.in_use`, `manufacturer.in_use` (`ProblemDetails.detail`) |

Admin category and tree responses add `restrictFromVendors`. Public responses never expose it.

## 9. Angular

- `CatalogApiService`: `adminGetCategoryTree()`, `getSelectableCategories()`, `restrictFromVendors` on the category request and response.
- `admin-catalog.page.ts`: parent select built from the admin tree (indented names, the category itself and its descendants excluded), "Restrict sellers" checkbox, delete shows the API conflict message, product form uses checklists.
- States: loading, empty, validation, conflict, network error.

## 10. Events, jobs, cache

No domain events or jobs. Public tree and list are not cached in this slice. When F13 adds caching it must be invalidated by these write use cases.

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Parent rules | Missing parent, self parent, move under a descendant, valid move |
| Names | Duplicate sibling rejected on create, rename and move; same name under a different parent allowed |
| Delete | Has children, in use, free; manufacturer in use and free; unknown id |
| Selection (admin) | Missing id, deleted id, duplicates collapsed, limit of 10, unpublished allowed |
| Selection (seller) | Restricted category, unpublished category, unpublished ancestor, unpublished manufacturer rejected; allowed category accepted |
| Selectable list | Only publishable, unrestricted categories with full path |
| Audit | Events written with the actor |
| Migration | Version ordering |

Automated: unit tests with fakes for all service rules. **Not automated:** SQL stores (recursive visibility query, constraints), HTTP pipeline, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate` and check the new foreign keys and unique indexes.
2. In `/admin/catalog`, create `Fashion` and `Fashion > Women`. Try to move `Fashion` under `Women`: rejected. Create a second `Women` under `Fashion`: rejected.
3. Delete `Fashion`: `409 category.has_children`. Attach a product to `Women`, then delete `Women`: `409 category.in_use`.
4. Unpublish `Fashion`: `/api/v1/catalog/categories/tree` and `/categories/{WomenId}` no longer show `Women`.
5. Mark `Women` as restricted for sellers. As a shop member, `GET /api/v1/catalog/categories/selectable` does not list it; as a customer the same call returns `403`.
6. Create a product with `categoryIds: [999999]`: `400 errors.categoryIds`.
7. Check the audit log for `catalog.category_*` events.

## 12. Rollout and compatibility

- Run the migrator before the API. The cleanup deletes orphan and duplicate mapping rows.
- Behavior changes visible to clients: delete can now return `409`; product create and update reject unknown categories and manufacturers; the public category detail and list hide categories under an unpublished ancestor; `pageSize` above 100 is clamped.
- Existing categories keep their data and get `RestrictFromVendors = false`. Existing duplicate sibling names are left alone and only checked on the next create, rename or move.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Seller endpoints that call `ValidateSelectionAsync(..., Seller)` | F10-A |
| Descendant products when browsing a category, caching | F13 |
| Slugs and SEO | F24 |
| Localization of taxonomy names | F07 |
| Per-seller allow lists and manufacturer restrictions | Not scheduled |
| Audit only covers admin writes made through the API, not bulk import | F27 |
