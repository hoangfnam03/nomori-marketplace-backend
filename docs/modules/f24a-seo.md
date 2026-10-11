# F24-A SEO: readable URLs, sitemap, robots, page metadata

| | |
|---|---|
| **Module ID** | F24-A (slice of F24 "SEO and public URLs"): URLs that carry the name, `sitemap.xml`, `robots.txt`, canonical links, page titles, Open Graph and structured data for products and shops |
| **Status** | Backend and Angular implemented; 1010 service tests (42 new in this slice), 44 data tests, the Angular build, lint and 47 unit tests (10 new) pass. The API endpoints, the built Angular server (301, 404, tags in the HTML, JSON-LD, sitemap and robots through the storefront) were run against SQL Server LocalDB. Not opened in a browser. |
| **Branch** | `feat/seo/foundation` (backend and frontend) |
| **Depends on** | F10 (products), F05 (shops), the Angular SSR server |
| **Unblocks** | F24-B (category and manufacturer pages, custom slugs and meta, redirects set by hand), F23 (CMS pages in the sitemap) |

## 1. Purpose

Public pages are addressed by id (`/storefront/products/57`) and carry no title, description or sitemap. This slice makes the product and shop pages addressable by a readable URL, tells crawlers what to index, and gives every public page the metadata that search engines and social networks read.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **The URL key is `name-slug` + `-` + `id`** (`/storefront/products/white-ceramic-vase-57`). The id is the truth; the slug is decoration. No slug table, no sync job, no migration: the slug is computed from the current name, so it can never be stale, never collides with another product and needs no history. |
| D2 | **Any key that ends in the right id resolves** (`57`, `old-name-57`, `white-vase-57`). A key that is not the canonical one is answered with **301 to the canonical URL** by the Angular server, so an old link, a renamed product or a bare id keeps working and ends on one address. |
| D3 | **The canonical slug is made by one function** (`SeoRules.Slugify`) on the API; screens receive it in the `slug` field of products and shops and never compute it. Lower case, accents removed (including `đ`), letters and digits only, words joined by `-`, at most 80 characters cut at a word. A name without any letter or digit gives an empty slug and the key is just the id. |
| D4 | **Only what a customer can see is public.** The path check, the sitemap and the structured data use the same visibility rule as the catalog (live product, active shop, publication window open). Anything else is "not found" and is not in the sitemap. |
| D5 | **Metadata is generated, not stored**: title `Name · Shop · Nomori`, description from the short description (160 characters), image, canonical link. Fields an administrator or a shop edits come with F24-B. |
| D6 | **`sitemap.xml` and `robots.txt` are served by the API** from the setting `Seo:SiteBaseUrl` (the public address of the storefront) and reached through the Angular server (and the dev proxy) at the root of the site. Sitemap: the storefront pages, every visible product and every active shop, at most 50,000 URLs (the protocol limit); more than that is cut and logged. |
| D7 | **Robots**: everything may be crawled except the private areas (`/admin`, `/vendor`, `/customer`, `/auth`, the cart, checkout and payment pages); the sitemap is announced. |
| D8 | Categories and manufacturers have no page of their own yet, so they get no URL here (F24-B). Search result pages are not indexed: the canonical link of the product list is the list without filters. |

## 3. Authorization

All of it is public and read-only. The path check answers only for what is visible; it reveals nothing about hidden products.

## 4. Included behavior

- `GET /api/v1/seo/path?type=product|vendor&key=...` returns `{ found, canonicalPath, isCanonical }`.
- `GET /sitemap.xml`, `GET /robots.txt`.
- `slug` on public product and shop responses, and on search suggestions.
- Angular: routes `/storefront/products/:key` and `/storefront/vendors/:key`; links use the canonical key; the page replaces a non-canonical address by the canonical one; title, description, canonical link, Open Graph, Twitter card and JSON-LD (`Product` with offer and availability, `Organization` for a shop) on the product, shop and list pages; the Express server issues the 301 and serves the two root files.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| Category and manufacturer pages, their slugs and sitemap entries | F24-B |
| Custom slug, meta title and description edited by shops or admins | F24-B |
| Redirects entered by hand, 404 log | F24-B |
| hreflang and a URL per language | F07-D |
| Sitemap index for more than 50,000 URLs, image sitemap | F24-B |
| Pre-rendering of pages, page speed work | F30 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `UrlRecord`, `UrlRecordService` (slug table with history, one active) | Slugs and redirects | Replaced by `name-id`: no table, no history, same effect for customers and crawlers |
| `SeoHelper.GetSeName`, `SeoSettings` | Slug rules | `Slugify` with accent removal; no per-store settings |
| `SitemapGenerator`, `RobotsTxt` | `sitemap.xml`, `robots.txt` | From the API; no admin-editable disallow list |
| `MetaTitle`, `MetaDescription`, `MetaKeywords` on entities | Page metadata | Generated; keywords are not used by search engines |
| `CanonicalUrlsEnabled` | Canonical link | Always on |

## 7. Data model

None. No migration.

## 8. Contracts

```text
SeoRules       Slugify(name), KeyFor(name, id), TryParseId(key), PathFor(type, name, id), Describe(text, max)
ISeoStore      ListProductsAsync(afterId, take, nowUtc), ListShopsAsync(afterId, take)   (sitemap rows: id, name, updated)
ISeoService    ResolvePathAsync(type, key), BuildSitemapAsync(), BuildRobots()
```

## 9. API

| # | Method | Route | Notes |
|---|---|---|---|
| 1 | `GET` | `/api/v1/seo/path` | anonymous; `404` is not used: `found = false` |
| 2 | `GET` | `/sitemap.xml` | `application/xml` |
| 3 | `GET` | `/robots.txt` | `text/plain` |

## 10. Angular

Routes `/storefront/products/:key`, `/storefront/vendors/:key`. A shared `SeoService` sets the tags (and works during server rendering). States: not found for a key without a valid id or for something not public.

## 11. Events, jobs, cache

None. The sitemap is built on request (cached for 1 hour by `Cache-Control`).

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Slug | accents and `đ`, symbols, repeated separators, length cut at a word, empty names, all-digit names |
| Key | build and parse, bare id, wrong suffix, huge numbers |
| Path | canonical, old slug, bare id, hidden product, inactive shop |
| Sitemap | only visible products and active shops, escaped characters, base address, limit |
| Robots | disallow list, sitemap line |
| Angular | key parsing and links, tags set by the service |

### Manual test guide

1. Open `/storefront/products` and a product: the address is `name-slug-id`.
2. Open `/storefront/products/<id>` and `/storefront/products/anything-<id>`: the browser ends on the canonical address (301 from the server, a replaced URL in the browser).
3. View the page source of a product: title, description, canonical, `og:` tags and the JSON-LD block are in the HTML sent by the server.
4. Open `/sitemap.xml` and `/robots.txt` on the storefront address.
5. Hide a product: its address shows not found and it leaves the sitemap.

### Server rendering and the API

A crawler only sees what the server sends, so the Angular server must have the data when it renders. Its own data requests (`/api/...`, which Angular resolves against the address of the request) are passed on to the API by a read-only proxy in `server.ts`. In production the visitors' browsers must still reach `/api` through the deployment (reverse proxy or gateway); the proxy in `server.ts` only answers GET.

## 13. Rollout and compatibility

Old links (`/storefront/products/57`) keep working and redirect. Set `Seo:SiteBaseUrl` to the public storefront address in every environment (the default is `http://localhost:4200`), and set `API_ORIGIN` (default `http://localhost:5214`) for the Angular server so it can reach the API.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Categories, manufacturers, custom slugs and meta, manual redirects | F24-B |
| Sitemap index and image sitemap | F24-B |
| The title and description follow the name and short description only | F24-B |
