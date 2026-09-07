---
name: Nomori backend conventions
description: Apply Nomori .NET 10, ASP.NET Core API, SQL Server, service, migration, security and testing conventions to backend files.
applyTo: "**/*.cs,**/*.csproj,**/*.json,**/*.sql"
---

- Target `net10.0`; do not change framework version without an ADR.
- Controllers are transport adapters; do not put business rules or repository access in them.
- Use dedicated `Request` and `Response` DTOs and document public endpoints in OpenAPI.
- Version API routes as `/api/v1/...` and return `ProblemDetails` for HTTP errors.
- Keep UTC in storage and business logic; avoid `DateTime.Now`.
- Add backend authorization and tests for business rules, status/error contracts and permission boundaries.
- Never edit an applied migration or log secrets, cookies, tokens or payment data.
