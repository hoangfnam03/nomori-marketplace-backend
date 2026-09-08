# Nomori Marketplace Backend

Backend foundation for Nomori Marketplace. This repository targets .NET 10 and is being rebuilt incrementally from nopCommerce conventions.

## Prerequisites

- .NET SDK `10.0.100` or a compatible feature band allowed by `global.json`
- SQL Server will be introduced in the database foundation phase
- SSMS can be used to inspect a local or remote SQL Server instance; it is a management client and does not replace the database server.

## Projects

- `src/Nomori.Marketplace.Core`: shared abstractions and domain foundation
- `src/Nomori.Marketplace.Data`: data access and migrations
- `src/Nomori.Marketplace.Services`: business and application services
- `src/Nomori.Marketplace.Web.Framework`: web concerns shared by hosts
- `src/Nomori.Marketplace.Api`: ASP.NET Core API host
- `tests/`: project-owned unit, integration and architecture tests

## Run

```powershell
dotnet restore
dotnet run --project src/Nomori.Marketplace.Api
```

The first API checkpoint is available at:

```text
GET /api/v1/health
```

Operational health endpoints are also available:

```text
GET /health/live
GET /health/ready
```

The Phase 2 database configuration is intentionally optional until the SQL Server migration phase. Set `Database__ConnectionString` through user secrets or environment variables; do not commit a real connection string.

## Database migration

Nomori uses FluentMigrator rather than EF Core Code First. Run the dedicated migrator after configuring SQL Server:

```powershell
dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate
```

See `docs/database-guidelines.md` for the local SQL Server and SSMS workflow. The API does not run schema migrations automatically at startup.

## Validate

```powershell
dotnet build Nomori.Marketplace.sln
dotnet test Nomori.Marketplace.sln
```

## Development rules

Read `docs/decisions.md` and the module conventions before adding a feature. API endpoints use `/api/v1`, dedicated DTOs and `ProblemDetails`. Business rules belong in services, not controllers.
