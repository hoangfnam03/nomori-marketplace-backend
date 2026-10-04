using FluentMigrator.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Data.Cart;
using Nomori.Marketplace.Data.Shipping;
using Nomori.Marketplace.Data.Catalog;
using Nomori.Marketplace.Data.Directory;
using Nomori.Marketplace.Data.Customers;
using Nomori.Marketplace.Data.Media;
using Nomori.Marketplace.Data.Security;
using Nomori.Marketplace.Data.Vendors;

namespace Nomori.Marketplace.Data.Configuration;

public static class DatabaseRegistrationExtensions
{
    public static IServiceCollection AddNomoriData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddScoped<ICustomerIdentityStore, SqlCustomerIdentityStore>();
        services.AddScoped<ICustomerProfileStore, SqlCustomerProfileStore>();
        services.AddScoped<ICustomerAccountDataStore, SqlCustomerAccountDataStore>();
        services.AddScoped<IEmailVerificationStore, SqlEmailVerificationStore>();
        services.AddScoped<IEmailOtpStore, SqlEmailOtpStore>();
        services.AddScoped<IAuthorizationStore, SqlAuthorizationStore>();
        services.AddScoped<IAuditLogStore, SqlAuditLogStore>();
        services.AddScoped<ICategoryStore, SqlCategoryStore>();
        services.AddScoped<IManufacturerStore, SqlManufacturerStore>();
        services.AddScoped<IProductStore, SqlProductStore>();
        services.AddScoped<IProductAttributeStore, SqlProductAttributeStore>();
        services.AddScoped<IInventoryStore, SqlInventoryStore>();
        services.AddScoped<IProductFacetStore, SqlProductFacetStore>();
        services.AddScoped<ICurrencyStore, SqlCurrencyStore>();
        services.AddScoped<ICartStore, SqlCartStore>();
        services.AddScoped<IShippingStore, SqlShippingStore>();
        services.AddScoped<IDirectoryStore, SqlDirectoryStore>();
        services.AddScoped<ISpecificationAttributeStore, SqlSpecificationAttributeStore>();
        services.AddScoped<IVendorStore, SqlVendorStore>();
        services.AddScoped<IVendorApplicationStore, SqlVendorApplicationStore>();
        services.AddScoped<IVendorMemberStore, SqlVendorMemberStore>();
        services.AddScoped<IMediaStore, SqlMediaStore>();
        services.AddScoped<IMediaUploadStore, SqlMediaUploadStore>();

        var storageOptions = configuration.GetSection(MediaStorageOptions.SectionName).Get<MediaStorageOptions>() ?? new MediaStorageOptions();
        if (storageOptions.UsesObjectStorage)
            services.AddSingleton<IMediaObjectStorage, MinioMediaObjectStorage>();
        else
            services.AddSingleton<IMediaObjectStorage, DisabledMediaObjectStorage>();

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
