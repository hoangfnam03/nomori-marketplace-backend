# F11-B Seller variants, specifications and tags

| | |
|---|---|
| **Module ID** | F11-B (slice of F11 "Product variants, attributes, specifications and product media") |
| **Status** | Backend and Angular implemented. SQL stores and migration not yet run against a real database. |
| **Branch** | `feat/product-variants/foundation` (backend and frontend) |
| **Depends on** | F10-A to F10-C (ownership, lifecycle, SKU), F11-A (pictures) |
| **Unblocks** | F12 (stock per combination), F13 (filters by specification, tags), F14 (variant pricing), F16 (cart line variant) |
| **Source PRD** | [vendor-products-prd.md](vendor-products-prd.md), US-A3, US-A4, FR-03, FR-08 |

## 1. Purpose

Attributes, specification attributes and tags already exist, but only an administrator can attach them to a product, the public routes do not check that the product is visible, and a combination is a free-text JSON string nobody validates. This slice lets a shop manage the variants, specifications and tags of its own products, validates combinations, and closes the public leak.

## 2. Actors and authorization matrix

| Action | Member of the product's shop | Platform admin | Other shop / customer |
|---|---|---|---|
| Read the option catalog (attribute and specification definitions) | Yes | Existing admin routes | No |
| Set variants, specifications, tags of a product | Yes (also while hidden or draft) | Existing admin routes (unchanged) | `404` |
| Read the same three for a product | Yes (own) | Existing admin routes | `404` |
| Public reads `/products/{id}/attributes`, `/specs`, `/tags` | | | Only for products visible on the storefront; otherwise `404` |

## 3. Included behavior

- **Definitions stay platform-owned.** Sellers choose existing product attributes (for example Color, Size) and existing specification options; they type only the **values** of a variant attribute and the **tags**.
- **Variants (replace-all).** One call sets the attributes, their values and the combinations. Rules:
  - At most **3** attributes, each used once; each has **1 to 20** distinct values (name 1 to 100 characters, case-insensitive duplicates refused); optional colour `#RRGGBB`; a price adjustment per value.
  - At most **100** combinations. A combination picks exactly one value of every attribute; the same set of values cannot appear twice. Each has a stock (0 to 1,000,000), an optional SKU (max 100) and an optional override price.
  - If there are attributes there must be at least one combination; with no attributes there must be none (this clears the variants).
  - The effective price of a combination (override, or product price plus the adjustments) must be above 0.
  - A combination SKU must be unique inside the shop across product SKUs and other combinations' SKUs, and the product-level SKU check (F10-C) now also looks at combination SKUs.
  - **Product stock is the sum of the combination stocks**, written in the same transaction. While a product has variants the seller save ignores the stock field.
  - The combination key stored in `AttributesJson` is `{"<mappingId>": <valueId>}` with the ids created by the call.
- **Specifications (replace-all of option rows).** A list of specification option ids (max 30, distinct, existing). Rows are stored as type `Option`, shown on the product page and marked filterable. Rows of other types (custom text, link) created by an administrator are left alone.
- **Tags (replace-all).** Up to 20 names, trimmed, lower-cased, 1 to 100 characters, duplicates merged. Existing tags are reused by name.
- **Public safety.** The three public routes answer `404` unless the product is live, its shop is active and the sale window is open.
- **Audit:** `product.variants_changed`, `product.specs_changed`, `product.tags_changed` (ids and counts only).
- **Angular:** a new page `/vendor/products/:id/details` (link "Details" in the seller list) with three sections: Variants (attributes, values, a "Generate all combinations" button and a combinations table), Specifications, Tags. The storefront detail lets the customer pick a value per attribute and shows the matching combination's price, SKU and stock.

## 4. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Pictures per combination | F11-C |
| Attribute value cannot be removed while an open order uses it (PRD US-A3) | F16/F17 (no orders exist yet) |
| Seller-defined attributes, specification groups or custom text/link specs | Not planned (platform owned) |
| Tier prices, review types | F14 / F25 |
| Copying variants with the product copy | F11-C |
| Atomic uniqueness of combination SKUs under concurrent saves (no index) | F12 (inventory keyed by SKU) |
| Stock reservation and oversell protection | F12 |
| Specification filters and tag pages | F13 |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `ProductAttributeMapping`, `ProductAttributeValue`, `ProductAttributeCombination` | Variants | One replace-all call; combinations validated; stock summed |
| `ProductSpecificationAttribute` | Specifications | Option type only for sellers |
| `ProductTag`, `ProductProductTag` | Tags | Same, names lower-cased |
| `ProductAttributeParser` (XML attribute string) | Combination key | JSON by mapping and value id |

## 6. Data model

No schema change. Existing tables `ProductAttributeMapping`, `ProductAttributeValue`, `ProductAttributeCombination`, `ProductSpecificationAttribute`, `ProductTag`, `ProductProductTag` are used. No migration.

## 7. Use cases and service contracts

```text
IVendorProductDetailsService (new)
  GetOptionCatalogAsync()
  GetTagsAsync / SetTagsAsync(vendorId, productId, names, actor)
  GetSpecOptionIdsAsync / SetSpecOptionsAsync(vendorId, productId, optionIds, actor)
  GetVariantsAsync / SetVariantsAsync(vendorId, productId, SaveVariantsCommand, actor)
IProductAttributeStore (added)    ReplaceVariantsAsync(productId, command), IsCombinationSkuTakenAsync(vendorId, sku, excludeProductId)
ISpecificationAttributeStore (added)  ReplaceProductOptionSpecsAsync(productId, optionIds)
IProductStore (added)             HasVariantsAsync(productId)
IProductService (added)           GetPublicProductAsync(id)    live, shop active, in window
```

Not found and forbidden follow F10-A (`404`; `403` for an inactive shop on writes). Field errors are `400` (`errors.attributes`, `errors.combinations`, `errors.optionIds`, `errors.tagNames`).

## 8. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `GET` | `/api/v1/vendors/{vendorId}/product-options` | Member: `{ attributes, specAttributes[{ ..., options }] }` |
| 2 | `GET`/`PUT` | `/api/v1/vendors/{vendorId}/products/{id}/variants` | Member; body `{ attributes: [{ productAttributeId, isRequired, values: [{ name, colorSquaresRgb, priceAdjustment }] }], combinations: [{ valueIndexes: [0, 1], sku, stockQuantity, overriddenPrice }] }` |
| 3 | `GET`/`PUT` | `/api/v1/vendors/{vendorId}/products/{id}/specs` | Member; body `{ optionIds: [4, 9] }` |
| 4 | `GET`/`PUT` | `/api/v1/vendors/{vendorId}/products/{id}/tags` | Member; body `{ tagNames: ["gift"] }` |
| 5 | `GET` | `/api/v1/products/{id}/attributes`, `/specs`, `/tags` | Public, now `404` for non-visible products |

`valueIndexes` has one index per attribute, in attribute order, into that attribute's `values`. `GET variants` answers the same shape as the public attributes response (mappings with values, combinations).

## 9. Angular

- `VendorProductApiService`: `getOptions()`, `getVariants()`, `setVariants()`, `getSpecs()`, `setSpecs()`, `getTags()`, `setTags()`.
- `vendor-product-details.page.ts` (route `/vendor/products/:id/details`), link from the list.
- `product-detail.page.ts`: value selection and matching combination.
- States: loading, saving, validation per section, not found, empty.

## 10. Events, jobs, cache

None. When F13 adds caching, tag, specification and variant changes must invalidate it.

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Variants | Valid save creates mappings, values, combinations and sums stock; limits (3 attributes, 20 values, 100 combinations); duplicate attribute, value or combination refused; wrong index count refused; unknown attribute refused; colour format; price must stay above 0; empty attributes clear the variants; attributes without combinations refused |
| SKU | Combination SKU clashes with a product SKU, another combination of the shop, or the same request; allowed in another shop; product SKU check sees combination SKUs |
| Stock | Seller save ignores stock while variants exist |
| Specs | Unknown option refused; more than 30 refused; duplicates merged; other rows untouched |
| Tags | Normalized, limit, reuse of existing tags |
| Isolation | Other shop's member gets not found; inactive shop forbidden; hidden and draft products can be edited |
| Public | Draft, stopped, hidden, out-of-window and inactive-shop products are not public |
| Audit | Counts only |

Automated: service tests with fakes. **Not automated:** SQL (replace transactions, SKU queries), HTTP, Angular. Manual guide below.

### Manual test guide

1. As a shop member open Products, then **Details** for a product.
2. Add attributes Color (Red, Blue) and Size (S, M); create combinations Red-S, Red-M, Blue-S with stocks 3, 4, 5 and a SKU each; save. The product stock in the list becomes 12 and the Stock field is read-only in the edit form.
3. Reuse a SKU of another product of the shop: error on that combination. Add the same combination twice: error.
4. Pick specification options and tags; save; reopen: they are back.
5. Publish the product; on the storefront pick Red and S: price, SKU and stock of that combination show.
6. Call `GET /api/v1/products/{id}/attributes` for a draft product anonymously: `404`.
7. As a member of another shop call the `variants`, `specs`, `tags` routes: `404`.

## 12. Rollout and compatibility

No migration. Existing admin routes are unchanged. The public attribute, specification and tag routes now return `404` for products that are not visible; the storefront only calls them after the product itself loaded. Products with combinations created through the admin routes keep working; their `AttributesJson` is not rewritten, and a seller save of variants replaces them.

The storefront detail previously read the public attributes response as if it had a nested `mapping` object, which it does not; it now uses a flat `PublicAttributeDetail` type.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Pictures per combination, copy with variants | F11-C |
| Open-order protection for attribute values | F16/F17 |
| Unique index and reservation for combination SKUs | F12 |
| Filters by specification and tag | F13 |
