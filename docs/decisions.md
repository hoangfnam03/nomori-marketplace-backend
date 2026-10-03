# Nomori Marketplace Decisions

The backend foundation targets .NET 10 (`net10.0`) and SQL Server. It uses `/api/v1` API versioning, `ProblemDetails` errors, OpenAPI-generated TypeScript clients and HttpOnly cookie authentication in the initial phase.

The full working decisions are maintained in the planning workspace until the repositories are split. Any change to framework version, authentication strategy, database provider or API contract requires an explicit decision update.

## Media storage (F08-B)

- Image bytes go to S3-compatible object storage when `Media:Storage:Provider` is `S3`. `Database` stays the default, so a fresh checkout runs without Docker.
- Browsers upload with a presigned POST, not a presigned PUT, because a POST policy can cap the size (`content-length-range`). The API validates the bytes in a separate complete step before an image exists.
- Public images are served from fixed object URLs, and `/api/v1/media/{id}` stays the stable address that redirects there. Presigned GET URLs are kept for private files in later modules (F08-C).

