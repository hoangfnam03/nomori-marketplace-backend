# F10-B Product lifecycle and admin moderation

| | |
|---|---|
| **Module ID** | F10-B (slice of F10 "Product core and ownership") |
| **Status** | Backend and Angular implemented and merged (PR #12). SQL store and migration not yet run against a real database. The image rule for publishing was added by [F11-A](f11a-product-pictures.md). |
| **Branch** | `feat/product-lifecycle/foundation` (backend and frontend) |
| **Depends on** | F10-A (ownership), F05 (members and their emails), F22 is **not** required: email uses the existing sender |
| **Unblocks** | F11 (image rule for publishing), F13 (status-aware search), F16 (buyable rule) |
| **Source PRD** | [vendor-products-prd.md](vendor-products-prd.md), epics B and D, FR-04 to FR-06, FR-10, FR-11 |

## 1. Purpose

Until now a product was either published or not (`Published` boolean). The feature map (F10) says publication should support a **review status rather than only a boolean when moderation is required**, and the PRD needs a place for an administrator to hide a violating product without deleting it. This slice replaces the boolean with a four-state lifecycle and adds hide, unhide and review request.

## 2. States and transitions

| State | Meaning | Visible on storefront |
|---|---|---|
| `draft` | Created, never published | No |
| `live` | On sale | Yes (if the shop is active) |
| `stopped` | Seller stopped selling | No |
| `hiddenByAdmin` | Hidden by an administrator with a reason | No |

```text
draft   --seller publish--> live
live    --seller stop-----> stopped
stopped --seller publish--> live
any non-hidden state --admin hide (reason required)--> hiddenByAdmin
hiddenByAdmin --admin unhide--> the state it had before it was hidden
```

Rules:

- A seller **cannot** move a hidden product anywhere, and cannot publish it. Sellers can still edit its content and can **request a review**.
- Only an administrator unhides. Unhiding restores the previous state (a product that was live goes live again), so a moderation mistake costs the shop nothing.
- Seller publish needs: price above 0, at least one category, and an active shop. (The "at least one image" rule from the PRD moves to F11, when product images exist.)
- A live product cannot lose its last category through a seller edit.
- New products made by sellers are always `draft`. Administrators may create a product directly as `live` (legacy admin form behavior).

`Published` stays available everywhere as a **computed column** (`Status = live`), so every existing public query keeps working and the two can never disagree.

## 3. Actors and authorization matrix

| Action | Member of the product's shop | Platform admin (`catalog.manage`) | Other shop / customer |
|---|---|---|---|
| Publish, stop | Yes (not when hidden) | Via the admin product form (not when hidden) | No (`404`) |
| Hide with reason | No | Yes | No |
| Unhide | No | Yes | No |
| Request review of a hidden product | Yes | n/a | No (`404`) |
| See status, hidden reason, review request | Yes (own shop) | Yes | No |

## 4. Included behavior

- **Seller endpoints:** `PUT /vendors/{id}/products/{pid}/status` (`live` or `stopped`) and `POST /vendors/{id}/products/{pid}/review-request`. The seller save body no longer has `published`; creating always makes a draft.
- **Admin endpoints:** `POST /admin/catalog/products/{id}/hide` (`{ reason }`, 1 to 2000 characters), `POST /admin/catalog/products/{id}/unhide`. Admin list filters `status` and `reviewRequested`.
- **Admin product form:** the "Published" checkbox still works: checked makes a non-hidden product live, unchecked makes a live product stopped; it never changes a hidden product.
- **Email** to every member of the shop when a product is hidden (with the reason, HTML-encoded) and when it is unhidden. A failed email is logged and never undoes the action.
- **Review request:** sets `ReviewRequestedOnUtc`; it is cleared on unhide. Administrators find these products with `status=hiddenByAdmin&reviewRequested=true`.
- **Audit:** `product.published`, `product.stopped`, `product.hidden`, `product.unhidden`, `product.review_requested`. Audit details hold ids only; the reason text is **not** copied into the audit log.
- **Angular:** vendor products page shows a status badge, Publish and Stop buttons, the hidden reason and a **Request review** button; admin catalog shows the status, Hide (reason form) and Unhide, and an "Awaiting review" filter.

## 5. Explicit non-goals (deferred)

| Deferred item | Goes to |
|---|---|
| "At least one image" to publish | F11-A |
| Admin notification when a review is requested (PRD Q4 chose a list, not email) | Not planned; the filter above is the list |
| Pre-publication review (products waiting for approval before going live) | PRD Q1 chose no; the model can add a `pendingReview` state later |
| Scheduled publication windows (`AvailableStartDate`/`EndDate`) | F10-C |
| Bulk hide and unhide | F27 |
| Hide history and moderator notes beyond the latest reason | F26 |
| Seller "archive" distinct from delete | Not planned |
| Cart behavior for products that stop being sold ("no longer available" flag) | F16 |
| Localized and templated emails | F22 |

## 6. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Product.Published`, `AvailableStartDateTimeUtc`, `AvailableEndDateTimeUtc` | Publication flag and windows | Four states instead of a boolean; windows deferred |
| Admin `ProductController` (vendor cannot edit products of others; activity log "Edit product") | Moderation and logging | Explicit hide and unhide actions with a reason |
| `Domain/Logging/ActivityLog` | Audit | Existing Nomori audit log |
| `WorkflowMessageService` / message templates | Notify seller | Plain emails through the existing `IEmailSender` until F22 |

nopCommerce has no "hidden by admin" state, so this is a Nomori addition driven by the PRD.

## 7. Data model

Migration `202610040001 ProductLifecycleMigration`:

- New columns on `Product`: `Status int NOT NULL` (`0` draft, `1` live, `2` stopped, `3` hiddenByAdmin), `StatusBeforeHidden int NULL`, `HiddenReason nvarchar(2000) NULL`, `HiddenOnUtc datetime2 NULL`, `HiddenByCustomerId int NULL` (foreign key to `Customer`), `ReviewRequestedOnUtc datetime2 NULL`.
- Backfill: `Status = 1` where `Published = 1`, otherwise `0`.
- `IX_Product_Published_Deleted` is dropped, the `Published` column is dropped and recreated as a **persisted computed column** `CASE WHEN Status = 1 THEN 1 ELSE 0 END`, then the index is recreated on it. New index `IX_Product_Status_VendorId (Status, VendorId)` for seller lists and the review list.

Existing unpublished products become `draft`. They were never distinguishable from "stopped", so sellers may need to republish products they had switched off.

`Down()` restores a plain `Published` bit from `Status`, drops the new columns and indexes. Hidden products become unpublished (their hidden state is lost).

## 8. Use cases and service contracts

```text
IProductService (added)
  SetStatusForVendorAsync(vendorId, productId, ProductStatus target, actor)   target: live | stopped
  RequestReviewForVendorAsync(vendorId, productId, actor)
  HideAsync(productId, reason, actor)
  UnhideAsync(productId, actor)
IProductStore (added)  UpdateLifecycleAsync(product)    (writes Status, StatusBeforeHidden, hidden fields, ReviewRequestedOnUtc)
ProductQuery           + Status, ReviewRequested
```

Business-rule codes (`409`): `product.hidden_by_admin` (seller tried to change a hidden product's status), `product.not_hidden` (unhide or review request on a product that is not hidden), `product.already_hidden`, `product.invalid_transition`. Not found and forbidden map to `404` and `403` as in F10-A.

## 9. API

| # | Method | Route | Who |
|---|---|---|---|
| 1 | `PUT` | `/api/v1/vendors/{vendorId}/products/{id}/status` body `{ "status": "live" \| "stopped" }` | Member |
| 2 | `POST` | `/api/v1/vendors/{vendorId}/products/{id}/review-request` | Member |
| 3 | `POST` | `/api/v1/admin/catalog/products/{id}/hide` body `{ "reason": "..." }` | `catalog.manage` |
| 4 | `POST` | `/api/v1/admin/catalog/products/{id}/unhide` | `catalog.manage` |
| 5 | `GET` | `/api/v1/vendors/{vendorId}/products?status=draft\|live\|stopped\|hiddenByAdmin` | Member (replaces `published`) |
| 6 | `GET` | `/api/v1/admin/catalog/products?status=&reviewRequested=` | `catalog.manage` |

Seller product responses add `status`, `hiddenReason`, `reviewRequestedOnUtc`; the `published` boolean remains in responses (computed). Admin responses add `status`, `hiddenReason`, `hiddenOnUtc`, `reviewRequestedOnUtc`.

| Status | Cases |
|---|---|
| `400` | `errors.status` (unknown or not allowed target), `errors.reason` (missing or over 2000), `errors.price`, `errors.categoryIds` (publish needs price and a category; live products keep a category) |
| `403` | Shop is inactive |
| `404` | Not a member of `{vendorId}`, or product not in that shop |
| `409` | Codes in section 8 |

## 10. Angular

- `VendorProductApiService`: `setStatus()`, `requestReview()`; `status` replaces the published filter.
- `vendor-products.page.ts`: status badges, Publish/Stop buttons per row, hidden banner with reason and a Request review button; the form no longer has a published checkbox.
- `admin-catalog.page.ts`: status badge and moderation buttons with an inline reason form; filter "All / Awaiting review / Hidden".
- States: loading, empty, validation, forbidden, conflict, network error.

## 11. Events, jobs, cache

No background work. Email is sent inline after the database change. No caching yet; when F13 adds it, hide and unhide must invalidate it.

## 12. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Seller transitions | Draft to live, live to stopped, stopped to live; invalid targets; hidden product cannot change; publish needs price and category; inactive shop refused |
| Admin | Hide requires a reason (1 to 2000); hide stores previous state; unhide restores it; double hide and unhide of a non-hidden product refused; clears review request |
| Review request | Only when hidden; sets the time; repeated requests keep working |
| Edits | Seller can edit a hidden product; live product keeps a category; admin checkbox never changes a hidden product |
| Email and audit | Members are emailed on hide and unhide; email failure does not fail the action; audit has ids only |
| Isolation | Other shop's member gets not found for every new endpoint |
| Migration | Version ordering |

Automated: service tests with fakes. **Not automated:** SQL (computed column, index rebuild, status filters), HTTP pipeline, Angular. Manual guide below.

### Manual test guide

1. Back up, then run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`. Check that `Product.Status` exists, `Published` is a computed column, and previously published products have `Status = 1`.
2. As a shop member, create a product. It is a draft and not on `/storefront/products`. Publish it: it appears. Stop it: it disappears.
3. Publish a product without a category: `400 errors.categoryIds`.
4. As admin, hide the product with a reason. The storefront no longer shows it, every member of the shop gets an email with the reason, and the seller page shows "Hidden by admin" and the reason.
5. As the member, try to publish it: `409 product.hidden_by_admin`. Edit its text: allowed. Click Request review.
6. As admin, filter Awaiting review, then unhide: the product returns to its previous state (live), and the review request is cleared.
7. As a member of another shop, call the status, review-request and list endpoints for this product: `404`.
8. Check the audit log for `product.hidden`, `product.unhidden`, `product.published`, `product.stopped`, `product.review_requested`, and confirm the reason text is not in them.

## 13. Rollout and compatibility

- Run the migrator before the API. Products that were unpublished become `draft`.
- Client-visible changes: the seller save body loses `published` (extra fields are ignored); seller list filter `published` becomes `status`; responses gain status fields; admin `published` keeps working as described in section 4.

## 14. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Image rule for publishing | F11-A |
| Publication windows, product copy, HTML descriptions | F10-C |
| Status-aware search and cache invalidation | F13 |
| Cart handling for products no longer sold | F16 |
| Templated, localized moderation emails | F22 |
| Moderation history | F26 |
