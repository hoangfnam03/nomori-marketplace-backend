using FluentMigrator.Runner;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nomori.Marketplace.Core.Configuration;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
	Args = args,
	ContentRootPath = AppContext.BaseDirectory
});
var databaseOptions = builder.Configuration
	.GetSection(DatabaseOptions.SectionName)
	.Get<DatabaseOptions>() ?? new DatabaseOptions();

if (!string.Equals(databaseOptions.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
	throw new InvalidOperationException($"Unsupported database provider '{databaseOptions.Provider}'.");

if (string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
	throw new InvalidOperationException("Database connection string is required. Set Database__ConnectionString or appsettings.json.");

await EnsureDatabaseExistsAsync(databaseOptions.ConnectionString);

builder.Services
	.AddFluentMigratorCore()
	.ConfigureRunner(runner => runner
		.AddSqlServer()
		.WithGlobalConnectionString(databaseOptions.ConnectionString)
		.ScanIn(typeof(Nomori.Marketplace.Data.Migrations.Foundation.FoundationMigration).Assembly)
		.For.Migrations())
	.AddLogging(logging => logging.AddFluentMigratorConsole());

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var migrationRunner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
migrationRunner.MigrateUp();

Console.WriteLine("Database migrations completed successfully.");

static async Task EnsureDatabaseExistsAsync(string connectionString)
{
	var connectionStringBuilder = new SqlConnectionStringBuilder(connectionString);
	var databaseName = connectionStringBuilder.InitialCatalog;
	if (string.IsNullOrWhiteSpace(databaseName))
		throw new InvalidOperationException("The database connection string must specify an Initial Catalog or Database.");

	connectionStringBuilder.InitialCatalog = "master";
	await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
	await connection.OpenAsync();

	var escapedDatabaseName = databaseName.Replace("]", "]]", StringComparison.Ordinal);
	await using var command = connection.CreateCommand();
	command.CommandText = $"IF DB_ID(@databaseName) IS NULL CREATE DATABASE [{escapedDatabaseName}];";
	command.Parameters.AddWithValue("@databaseName", databaseName);
	await command.ExecuteNonQueryAsync();
}
