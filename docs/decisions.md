# Nomori Marketplace Decisions

The backend foundation targets .NET 10 (`net10.0`) and SQL Server. It uses `/api/v1` API versioning, `ProblemDetails` errors, OpenAPI-generated TypeScript clients and HttpOnly cookie authentication in the initial phase.

The full working decisions are maintained in the planning workspace until the repositories are split. Any change to framework version, authentication strategy, database provider or API contract requires an explicit decision update.
