using FluentMigrator.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Data.Customers;

namespace Nomori.Marketplace.Data.Configuration;

public static class DatabaseRegistrationExtensions
{
    public static IServiceCollection AddNomoriData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddScoped<ICustomerIdentityStore, SqlCustomerIdentityStore>();

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (!string.Equals(databaseOptions.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported database provider '{databaseOptions.Provider}'.");

        services.AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSqlServer()
                .WithGlobalConnectionString(databaseOptions.ConnectionString)
                .ScanIn(typeof(DatabaseRegistrationExtensions).Assembly).For.Migrations());

        return services;
    }
}