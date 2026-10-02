# F13-A Catalog search, filters, facets and suggestions

| | |
|---|---|
| **Module ID** | F13-A (slice of F13 "Discovery and merchandising") |
| **Status** | Backend and Angular implemented. SQL queries and migration not yet run against a real database. |
| **Branch** | `feat/search/foundation` (backend and frontend) |
| **Depends on** | F09-A (category tree), F10 (products, tags), F11-B (specifications, tags), F12-A (availability) |
| **Unblocks** | F13-B (compare, recently viewed, search analytics), F14 (price filter on real prices), F16 (listing to cart) |
| **Feature map** | F13 "Nomori slices": API list/detail first; query validation and pagination; filters; autocomplete; dedicated index/search engine later. "Search must always apply publication, shop status, visibility, ACL/store constraints and stock policy before returning results." |

## 1. Purpose

The public list could only filter by one category (without its subcategories), one manufacturer and a price range, searched with an unescaped `LIKE` on two columns, and the Angular list page did not even send the search text. This slice makes the storefront list a real catalog search: validated input, text search over the useful fields, category with descendants, availability, tag and specification filters, facet counts to build the filter panel, and suggestions while typing.

## 2. Actors

Public (anonymous) users. Nothing here is writable. The seller list keeps working and benefits from the safer, wider text search.

## 3. Included behavior

- **Always public.** Every query here applies the existing public rules in SQL: live product, active shop, sale window open, not deleted.
- **Text search** (`search`): trimmed, at most 100 characters, split on spaces into at most 5 terms. Every term must match (AND) in the product name, short description, SKU, a tag name or a manufacturer name. `%`, `_` and `[` are matched literally. Fewer than 2 characters after trimming is refused (`400 errors.search`).
- **Category with descendants** (`categoryId`): the category and all its published descendants. An unknown or unpublished category (or one under an unpublished parent) returns an empty page, not an error.
- **Manufacturers** (`manufacturerIds`, repeatable, max 20) match any of them. The old single `manufacturerId` still works.
- **Price** (`minPrice`, `maxPrice`): not negative, and the minimum may not exceed the maximum (`400`).
- **In stock** (`inStock=true`): products that do not track inventory, or whose stock minus active reservations is above zero (F12-A). For products with variants the total of all combinations is used.
- **Tags** (`tags`, repeatable, max 10): a product matching any of them.
- **Specifications** (`specOptionIds`, repeatable, max 20): only rows marked filterable. Options of the **same** specification attribute are alternatives (OR); different attributes must all match (AND).
- **Sort:** the existing `DisplayOrder`, `NameAsc`, `NameDesc`, `PriceAsc`, `PriceDesc`, `Newest`, plus `Relevance` (name starts with the first term, then name contains it, then the rest). `Relevance` is the default when `search` is present.
- **Paging:** page at least 1, page size 1 to 100 (the Angular list asks for 20).
- **Facets** (`GET /catalog/products/facets`): same filters; the answer lists the price range, manufacturers, the 20 most used tags and the filterable specification options with counts. Counts are taken from the result of search, category, price and availability filters **without** the facet selections themselves, so choosing an option does not make its siblings disappear.
- **Suggestions** (`GET /catalog/products/suggest?q=`): at least 2 characters, up to 8 products (id, name, price, main picture), names starting with the text first.
- **List item** gets `inStock` (not tracked, or on hand above zero; the exact available quantity stays on the detail page).
- **Angular:** the list page reads and writes all filters in the URL (shareable), shows a filter panel (price, in stock, manufacturers, tags, specifications), active filter chips with "Clear all", the search term, and the Relevance sort. The search box shows suggestions (arrow keys, Enter, Escape) and the list page now actually sends the search text.

## 4. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Full-text index, stemming, typo tolerance, synonyms, a search engine | F13-C |
| Search term statistics and "popular searches" | F13-B / F26 |
| Compare products, recently viewed | F13-B |
| Filtering variants by their own attributes (color, size of combinations) | F13-B |
| Filtering by rating or shop | F25 / vendor pages already filter by `vendorId` |
| Caching of facets and suggestions | F13-C |
| Localized search, SEO-friendly filter URLs, sitemap | F07 / F24 |
| Ranking by sales or popularity | F26 |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Nop.Services/Catalog/ProductService.SearchProductsAsync`, `ProductSearchModel` | Search and filter pipeline | One SQL query with public rules built in; no plugin provider yet |
| `Domain/FilterLevels`, `Nop.Services/FilterLevels` | Filter levels | Not needed: facets come from filterable specification rows |
| `SearchTermService` | Search analytics | Deferred |
| `Nop.Web CatalogController`, `SearchCompleteController` | Browsing and autocomplete | Same behavior through `/products`, `/products/facets`, `/products/suggest` |
| `SearchPluginManager` | Replaceable search | The service sits behind `ICatalogSearchService`, so an engine can replace the store later |

## 6. Data model

Migration `202610080001 SearchIndexMigration`: indexes only, no new columns.

- `IX_ProductProductTag_TagId (ProductTagId, ProductId)` for tag lookups.
- `IX_PSA_Option_Product (SpecificationAttributeOptionId, ProductId)` for specification filters.
- Category lookups already have `IX_ProductCategory_CategoryId`, and SKU has `UX_Product_VendorId_Sku` (F10-C); nothing is added for them.

`Down()` drops the indexes it created. `LIKE '%term%'` cannot use an index; this is accepted until F13-C.

## 7. Use cases and service contracts

```text
CatalogSearchRequest   Search, CategoryId, ManufacturerIds, MinPrice, MaxPrice, InStock, Tags, SpecOptionIds, Sort, Page, PageSize, VendorId
ICatalogSearchService
  SearchAsync(request)       CatalogResult<PagedResult<Product>>
  GetFacetsAsync(request)    CatalogResult<ProductFacets>
  SuggestAsync(text)         CatalogResult<IReadOnlyList<Product>>
IProductFacetStore         GetFacetsAsync(ProductQuery)
ProductQuery               + CategoryIds, ManufacturerIds, Tags, SpecOptionGroups, InStockOnly; ProductSortOrder.Relevance
```

The service validates, expands the category through the published category tree, turns specification option ids into groups per attribute, and builds a `ProductQuery` with `OnlyActiveShops` and `Published` always set. The SQL filter is one shared builder used by the product list and the facet queries. Field errors are `400`: `errors.search`, `errors.minPrice`, `errors.maxPrice`, `errors.manufacturerIds`, `errors.tags`, `errors.specOptionIds`.

## 8. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/catalog/products` | all filters above; list items gain `inStock` |
| 2 | `GET` | `/api/v1/catalog/products/facets` | same query string (`page`, `pageSize` and `sort` ignored) |
| 3 | `GET` | `/api/v1/catalog/products/suggest?q=` | `[{ id, name, price, mainPictureId }]` |

Facets answer: `{ totalCount, minPrice, maxPrice, manufacturers: [{ id, name, count }], tags: [{ name, count }], specifications: [{ id, name, options: [{ id, name, count }] }] }`.

## 9. Angular

- `CatalogApiService`: `getProducts()` takes the new filters; `getFacets()`, `suggest()`.
- `product-list.page.ts`: filter panel, chips, URL sync, Relevance sort.
- `search-box.component.ts`: debounced suggestions with keyboard support (`aria-combobox` pattern).
- States: loading, empty (suggest clearing filters), validation errors from the API, network error.

## 10. Events, jobs, cache

None. Facets cost several queries per call; they are requested once per filter change. Cache them when F13-C arrives (a short time to live is enough because stock and the sale window change results).

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Input | Search length and terms; price order; list limits; wildcard characters; page and size clamp |
| Category | Descendants included; unpublished or unknown gives an empty page |
| Filters | Manufacturer list; tags; specification groups (OR inside, AND across); in stock; defaults to relevance with a search |
| Public rules | The built query is always public (`Published`, `OnlyActiveShops`) |
| Facets | Facet selections are removed from the base query; same public rules |
| Suggest | Minimum length, limit, relevance sort |
| Migration | Version ordering |

Automated: service tests with fakes. **Not automated:** SQL (filter builder, facet counts, stock subquery), HTTP, Angular. Manual guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; check the new indexes.
2. Search `mug`, then `mug blue` (both terms needed), then `50%` (literal). Search by an exact SKU and by a tag name.
3. Pick a parent category: products of its subcategories appear. Unpublish the parent: the list is empty.
4. Set a price range, then `minPrice` above `maxPrice`: `400`.
5. Hold the last unit of a product in a reservation (F12-A): with "In stock" it disappears.
6. Mark specification options filterable on products, then filter by two options of one attribute (OR) and one of another attribute (AND). Open `/facets` and check the counts.
7. Stop a product, set its sale window in the future, deactivate its shop: none of these appear in list, facets or suggestions.
8. In the storefront type 2 letters in the search box: suggestions appear; arrow keys and Enter open a product; Escape closes the list.

## 12. Rollout and compatibility

Run the migrator before the API (indexes only; safe to run on a live database). The existing query parameters keep their meaning except `search`, which now matches more fields, treats `%` and `_` literally and needs 2 characters. List items gain `inStock`. The default sort is unchanged unless a search text is given.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Compare products, recently viewed, search term analytics | F13-B |
| Search engine, full-text, typo tolerance, caching | F13-C |
| Variant attribute filters | F13-B |
| Accurate available quantity on list items | F13-C (with caching) |
