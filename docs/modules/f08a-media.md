# F08-A Media: image upload and delivery

| | |
|---|---|
| **Module ID** | F08-A (slice of F08 "Media, downloads, video and file safety") |
| **Status** | Backend and Angular implemented. Object storage (section 14) on `feat/media/object-storage`. |
| **Branch** | `feat/media/foundation` (backend and frontend) |
| **Depends on** | F00, F03 (done), F05 (vendor membership) |
| **Unblocks** | F09-A (category/manufacturer images), F10-A/F11 (product images), shop logo in the later `vendor-shop-settings` slice |

## 1. Purpose

Give Nomori one guarded way to upload and serve images. Before this slice, `Category.PictureId`, `Manufacturer.PictureId` and `Vendor.PictureId` were unchecked integers that pointed at nothing.

The feature map (F08) says product images, shop logos, customer uploads, invoices and digital downloads must not share an unguarded public upload endpoint. This slice therefore ties every upload to a **purpose** and checks the caller against that purpose.

## 2. Actors and authorization matrix

| Purpose | Who may upload | Owner recorded |
|---|---|---|
| `category` | Permission `catalog.manage` | uploader |
| `manufacturer` | Permission `catalog.manage` | uploader |
| `vendorLogo` | Member of a vendor (uploads for their own vendor), or administrator with `vendor.manage` (must pass `vendorId`) | uploader + vendor |
| `product` | Member of a vendor (own vendor only) | uploader + vendor |

| Action | Who |
|---|---|
| Read image bytes `GET /media/{id}` | Anyone, but only assets with visibility `public` (all assets in this slice) |
| Delete `DELETE /media/{id}` | The uploader, a member of the asset's vendor, or an administrator whose permission matches the purpose. Refused while the image is in use. |

The vendor id of a member always comes from the session, never from the request. A member of another shop gets `404` for assets of a shop they do not belong to.

## 3. Included behavior

- Multipart upload of one image, validated by **file signature (magic bytes)**, not by the client-supplied content type or file name.
- Allowed formats: JPEG, PNG, GIF, WebP. SVG is rejected on purpose (it can carry script).
- Maximum size `Media:MaxUploadBytes` (default 5 MiB).
- Bytes stored in SQL Server (`varbinary(max)`) in a separate table, behind `IMediaStore`, so a file or object store can replace it later.
- Delivery with `Content-Type` from the sniffed type, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'`, `ETag` (SHA-256), long immutable cache.
- Attachment check: category, manufacturer and vendor services accept a `pictureId` only when it is `0` (none) or an existing asset with the matching purpose (and the same vendor for `vendorLogo`).
- Delete refused with `409 media.in_use` while a category, manufacturer or vendor references the asset.
- Audit events `media.uploaded` and `media.deleted` (ids and purpose only, never content).
- Angular: `MediaApiService`, a reusable image field (upload, preview, remove) used in the admin catalog forms, and the vendor logo shown on storefront vendor pages when present.

## 4. Explicit non-goals (deferred)

| Deferred item | Reason | Goes to |
|---|---|---|
| Thumbnails, resizing, dimensions, format conversion | Needs an image-processing library decision (licence and security review). Images are served at original size. | F08-B |
| Videos, 3D objects | Not needed for marketplace MVP | F08-B / F11 |
| Private media, digital downloads, invoices | Needs authorised, non-public delivery. The `Visibility` column exists; only `public` is accepted. | F08-C |
| Malware scanning, EXIF stripping | Needs a scanner integration | F08-B |
| External object storage (Azure Blob, CDN) | S3-compatible storage (MinIO) is done, see section 14; Azure Blob and a CDN in front are later | F28 |
| Per-shop storage quota, upload rate limit | Needs product decision | F08-B |
| Orphan cleanup job (uploaded but never attached) | Needs background jobs | F29 |
| Product picture mapping (`ProductPicture`) and ordering | Belongs with product images | F11 |
| SEO file names, duplicate detection by hash | The hash is stored for later use | F08-B |

## 5. Source map from nopCommerce

| nopCommerce | Used for | Difference in Nomori |
|---|---|---|
| `Domain/Media/Picture`, `PictureBinary` | Separate metadata and binary tables | Adds purpose, owner and vendor; no SEO filename, no alt/title attributes |
| `Services/Media/PictureService` (`InsertPictureAsync`, mime validation) | Validation and storage | Validates by file signature; rejects unknown formats instead of trusting the mime type |
| `Domain/Media/MediaSettings` | Limits and sizes | Only `MaxUploadBytes` in this slice (thumbnail sizes are deferred) |
| Admin `PictureController` (upload) | Upload endpoint | One endpoint with purpose-based authorization; nopCommerce admin upload has no per-purpose ownership |
| `IThumbService`, `DownloadService`, `VideoService` | Not used | Deferred (section 4) |

## 6. Data model

Migration `202610010001 MediaMigration`:

**`MediaAsset`**

| Column | Type | Notes |
|---|---|---|
| `Id` | `int` identity PK | |
| `Purpose` | `int` | `0` category, `1` manufacturer, `2` vendorLogo, `3` product |
| `Visibility` | `int` | `0` public. Other values reserved for F08-C |
| `MimeType` | `nvarchar(100)` | From the sniffed signature |
| `SizeBytes` | `int` | |
| `Sha256` | `char(64)` | Hex |
| `UploadedByCustomerId` | `int` FK to `Customer` | |
| `VendorId` | `int` null FK to `Vendor` (`ON DELETE SET NULL`) | Set for `vendorLogo` and `product` |
| `CreatedOnUtc` | `datetime2` | |

**`MediaAssetBinary`**: `MediaAssetId` PK and FK (`ON DELETE CASCADE`), `Data varbinary(max)`.

Indexes: `IX_MediaAsset_VendorId`, `IX_MediaAsset_UploadedByCustomerId`.

`Down()` drops both tables. Existing `PictureId = 0` values stay valid ("no picture"). Existing non-zero `PictureId` values (none are expected, because uploading did not exist) are not modified; they would simply fail validation the next time the owning record is saved.

## 7. Use cases and service contracts

```text
IMediaService
  UploadAsync(UploadMediaCommand, MediaCaller)        -> MediaResult<MediaAsset>
  GetContentAsync(id)                                 -> MediaContent? (bytes, mime, sha256)
  DeleteAsync(id, MediaCaller)                        -> MediaResult<bool>
IMediaStore
  InsertAsync(asset, bytes), GetAsync(id), GetContentAsync(id), DeleteAsync(id), IsReferencedAsync(id)
MediaAttachment.ValidateAsync(store, pictureId, purpose, vendorId, errors)   (used by Category/Manufacturer/Vendor services)
```

`MediaCaller(CustomerId, CanManageCatalog, CanManageVendors, MemberVendorId)` is built once per request from the session.

## 8. API

| # | Method | Route | Who | CSRF |
|---|---|---|---|---|
| 1 | `POST` | `/api/v1/media` (multipart: `file`, `purpose`, optional `vendorId`) | per section 2 | yes |
| 2 | `GET` | `/api/v1/media/{id}` | anyone (public assets) | |
| 3 | `DELETE` | `/api/v1/media/{id}` | per section 2 | yes |

Upload response `201`: `{ id, purpose, mimeType, sizeBytes, vendorId, createdOnUtc, url }` where `url` is `/api/v1/media/{id}`.

| Status | When |
|---|---|
| `400` (`errors.file`) | No file, empty file, larger than the limit, or not a JPEG/PNG/GIF/WebP signature |
| `400` (`errors.purpose`, `errors.vendorId`) | Unknown purpose, missing or unexpected `vendorId` |
| `401` | Not signed in |
| `403` | Signed in but not allowed to upload this purpose |
| `404` | Asset does not exist, is not public, or belongs to a shop the caller is not a member of (delete) |
| `409` `media.in_use` | Delete of an asset still referenced |
| `400` (`errors.pictureId`) | On category, manufacturer or vendor save: unknown asset, wrong purpose or wrong vendor |

## 9. Angular

- `core/media/media-api.service.ts`: `upload(file, purpose, vendorId?)`, `delete(id)`, `url(id)`.
- `shared/components/media-image-field`: file picker, preview, upload progress, remove, error text. Emits the new `pictureId`.
- Used in `admin-catalog.page.ts` (category and manufacturer forms).
- Storefront vendor list and detail show the logo when `pictureId > 0`.
- States: idle, uploading, error (size, type, forbidden, network), uploaded.

## 10. Events, jobs, cache

No domain events or jobs. Responses carry `Cache-Control: public, max-age=31536000, immutable`; this is safe because an asset id is never reused and its bytes never change. A deleted asset returns `404` once caches expire.

## 11. Acceptance criteria and test matrix

| Area | Tests |
|---|---|
| Signature sniffing | Real JPEG/PNG/GIF/WebP headers accepted; SVG, HTML, text, a `.png` name on JPEG bytes (accepted as JPEG), a truncated header all handled |
| Limits | Empty and oversize files rejected |
| Authorization | Each purpose with each caller type; member cannot use another `vendorId`; admin needs `vendorId` for `vendorLogo`; `product` is members only |
| Delete | Uploader, same-shop member, admin allowed; other shop gets not found; in-use gets `media.in_use` |
| Attachment | Wrong purpose, unknown id, other shop's logo rejected; `0` accepted |
| Migration | Version ordering |

Automated: unit tests for the signature sniffer, upload, delete and attachment rules (fakes, no database). **Not automated:** the SQL store, the HTTP multipart pipeline, and the Angular component. Manual test guide below.

### Manual test guide

1. Run `dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate`; confirm `MediaAsset` and `MediaAssetBinary`.
2. As an administrator, open `/admin/catalog`, edit a category and upload a PNG. The preview appears; save.
3. Open the returned `url` in a private window: the image loads without signing in.
4. Upload a `.txt` renamed to `.png`: rejected with a `file` error. Upload an SVG: rejected. Upload a file over 5 MiB: rejected.
5. Try to delete the image while the category uses it: `409 media.in_use`. Remove it from the category, then delete: `204`.
6. As a customer without a shop, `POST /api/v1/media` with `purpose=product`: `403`.
7. As a member of shop A, upload `purpose=vendorLogo`; as a member of shop B try to delete it: `404`.
8. Check `media.uploaded` and `media.deleted` in the audit log.

## 12. Rollout and compatibility

Run the migrator before starting the API. No existing data changes. Angular admin catalog forms replace the raw "picture id" number input with the image field. Clients that still send a non-zero `pictureId` for an asset that does not exist now receive `400 errors.pictureId`.

## 13. Technical debt and follow-ups

| Item | Module ID |
|---|---|
| Thumbnails and dimensions; scanning; EXIF | F08-B |
| Private media and downloads | F08-C |
| Orphan cleanup; storage quotas | F29 / F08-B |
| Product picture mapping | F11 |
| Vendor logo editing by the shop itself | `vendor-shop-settings` (F05) |
| Copy existing database images to object storage (they keep working from SQL Server until then) | F28 |
| Background cleanup of `MediaUpload` rows that were never completed | F29 |

## 14. Object storage and direct uploads (F08-B)

Image bytes can live in S3-compatible object storage (MinIO locally). Metadata, authorization and the image id stay in SQL Server, so pages keep using `/api/v1/media/{id}`.

### Configuration

| Key | Default | Notes |
|---|---|---|
| `Media:Storage:Provider` | `Database` | `Database` keeps bytes in `MediaAssetBinary`; `S3` uses object storage |
| `Media:Storage:S3:Endpoint` | | Address the API uses, for example `http://localhost:9000` |
| `Media:Storage:S3:PublicEndpoint` | `Endpoint` | Address browsers use. Presigned posts are signed for this host |
| `Media:Storage:S3:Bucket` | `nomori-media` | |
| `Media:Storage:S3:Region` | `us-east-1` | Set so the SDK signs without asking the server |
| `Media:Storage:S3:AccessKey`, `SecretKey` | | User secrets or environment variables only |
| `Media:Storage:S3:UploadUrlLifetimeMinutes` | `10` | 1 to 60 |
| `Media:Storage:S3:RedirectCacheSeconds` | `86400` | Cache lifetime of the `302` from `/api/v1/media/{id}` |

Local setup:

```powershell
docker compose -f infra/docker-compose.yml up -d
dotnet user-secrets --project src/Nomori.Marketplace.Api set "Media:Storage:Provider" "S3"
dotnet user-secrets --project src/Nomori.Marketplace.Api set "Media:Storage:S3:Endpoint" "http://localhost:9000"
dotnet user-secrets --project src/Nomori.Marketplace.Api set "Media:Storage:S3:AccessKey" "nomori-api"
dotnet user-secrets --project src/Nomori.Marketplace.Api set "Media:Storage:S3:SecretKey" "nomori-api-dev-only"
```

`minio-init` creates the bucket, makes only `public/` anonymously readable, expires `pending/` after one day, and creates the `nomori-api` user with access to this bucket only. CORS allows `http://localhost:4200`. The compose file uses the `pgsty/minio` build because MinIO stopped publishing community images.

### Bucket layout

| Prefix | Content | Read access |
|---|---|---|
| `pending/{uploadId}` | Bytes posted by a browser, not yet validated | None (deleted after completion, expired after 1 day) |
| `public/{purpose}/{yyyy}/{MM}/{random}.{ext}` | Validated images | Anonymous, `Cache-Control: public, max-age=31536000, immutable` |

Keys are never reused, so the long cache lifetime is safe.

### Direct upload flow

```text
Browser                      API                                   Object storage
  | POST /media/uploads        |                                        |
  | {purpose, vendorId, size}  | authorize (section 2), check size      |
  |                            | MediaUpload row, presigned POST:       |
  |<-- {uploadId, url, fields} |   key = pending/{id}, 1..size bytes    |
  | POST url (fields + file) --------------------------------------------> 204
  | POST /media/uploads/{id}/complete                                   |
  |                            | owner and expiry check, re-authorize   |
  |                            | read bytes, size + signature check --->|
  |                            | put public/... with sniffed type ----->|
  |                            | delete pending/{id}, insert MediaAsset |
  |<-- 201 MediaResponse       |                                        |
```

- The policy admits one key and at most the announced size. Storage refuses other keys (`403`) and larger bodies (`400 EntityTooLarge`).
- Completion is idempotent: a retried request returns the same asset. If two requests race, the first to link the upload wins and the other removes its copy.
- Completion stays open 30 minutes after the presigned post expires; after that it returns `409 media.upload_expired`.
- Only the account that started an upload can complete it; anyone else gets `404`. Authorization is checked again, so a member who left the shop is refused.
- Bytes that fail validation are deleted from `pending/` straight away.
- `POST /api/v1/media` (multipart) still works with either provider. With `S3` it stores the validated bytes in `public/`.

### API additions

| # | Method | Route | Who | CSRF |
|---|---|---|---|---|
| 4 | `POST` | `/api/v1/media/uploads` (`{ purpose, vendorId?, sizeBytes }`) | per section 2 | yes |
| 5 | `POST` | `/api/v1/media/uploads/{id}/complete` | the account that created the upload | yes |

`GET /api/v1/media/{id}` returns `302` to the public object URL for object storage images (`Cache-Control: public, max-age=86400`), and serves database images as before.

| Status | When |
|---|---|
| `409` `media.direct_upload_unavailable` | Provider is `Database`; clients fall back to `POST /api/v1/media` |
| `409` `media.upload_expired` | Completion more than 30 minutes after the presigned post expired |
| `400` (`errors.file`) | Size out of range, nothing uploaded yet, or not a JPEG/PNG/GIF/WebP signature |

### Data model

Migration `202610130001 MediaObjectStorageMigration`:

- `MediaAsset.StorageProvider int not null default 0` (`0` database, `1` object storage) and `MediaAsset.StorageKey nvarchar(400) null`.
- `MediaUpload`: `Id uniqueidentifier` PK, `Purpose`, `VendorId`, `CustomerId` (FK `Customer`), `ObjectKey`, `ExpiresOnUtc`, `CreatedOnUtc`, `MediaAssetId` (FK `MediaAsset`, `ON DELETE SET NULL`).

Existing assets keep `StorageProvider = 0` and are still served from SQL Server, whichever provider is configured.

### Angular

`MediaApiService.upload()` keeps its signature. It creates the upload, posts the file to storage with an `HttpClient` that bypasses the interceptors (no CSRF header, no cookies to another origin), and completes it. On `409 media.direct_upload_unavailable` it falls back to the multipart upload. A storage `EntityTooLarge` is reported as status `413`.

### Tests

- Unit (`MediaServiceTests`): unavailable with the database provider; authorization and size checks on create; the policy size; completion copying to a public key with the sniffed type; idempotency; another caller; a missing file; bad bytes; expiry; lost shop membership; multipart upload and delete with object storage.
- Angular (`media-api.service.spec.ts`): direct flow, fallback, `EntityTooLarge` to `413`, API validation errors passed through.
- Manual, against MinIO: presigned POST accepted; oversize body and changed key refused; `pending/` not public; `public/` served as `image/png` with the immutable cache header; CORS preflight from `http://localhost:4200`; `302` from `/api/v1/media/{id}`; SQL stores round trip.
