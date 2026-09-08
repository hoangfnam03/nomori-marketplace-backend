# Database Guidelines

## Strategy

Nomori uses SQL Server and FluentMigrator. The project is migration-first, not EF Core Code First.

- Domain entities describe business data.
- FluentMigrator classes own schema creation and upgrade history.
- The `Nomori.Marketplace.DbMigrator` console project creates the target database and runs migrations.
- The API registers data infrastructure but does not mutate schema automatically at startup.
- SSMS is a management client for inspecting the SQL Server instance, database, tables and migration results.

## Local setup

1. Install or start a SQL Server instance.
2. Open `src/Nomori.Marketplace.DbMigrator/appsettings.json` and set a local connection string, or provide `Database__ConnectionString` as an environment variable.
3. Run:

```powershell
dotnet run --project src/Nomori.Marketplace.DbMigrator -- migrate
```

The migrator creates the database named in `Initial Catalog`/`Database` when it does not exist, then runs pending migrations. It never drops a database automatically.

Example for Windows authentication with LocalDB:

```text
Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=NomoriMarketplace_Db;Integrated Security=True;TrustServerCertificate=True;
```

Do not commit production credentials or real connection strings.

## Migration rules

- Use a monotonically increasing migration id and a descriptive class name.
- Use UTC columns with names ending in `Utc` for timestamps.
- Add indexes and constraints with the migration that needs them.
- Never edit a migration that has already run in a shared environment; add a new migration.
- Keep migrations provider-specific only when SQL Server behavior requires it.
- Test clean database -> migrate and previous version -> migrate latest.
- Copy/adapt only the tables required by the current module from nopCommerce. Do not copy the full nopCommerce schema or raw upgrade SQL.
- Preserve the Nomori namespace and table naming conventions; do not keep `Nop` prefixes in new objects.

## Current migrations

- `FoundationMigration` creates `NomoriSystemMetadata`, a small system table used to verify the migration pipeline.
- `AuthenticationIdentityMigration` creates `Customer`, `CustomerPassword`, `CustomerRole` and `CustomerCustomerRoleMapping` with foreign keys, unique indexes and password history ordering.

Permission records, customer profile fields, addresses, MFA and external authentication remain separate slices and are not included in the minimal Auth identity schema.
