using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Vendors;

namespace Nomori.Marketplace.Data.Tests;

public sealed class VendorApplicationMigrationTests
{
    [Fact]
    public void MigrationsRunInOrderAfterTheVendorTable()
    {
        var vendor = typeof(VendorMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();
        var application = typeof(VendorApplicationMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();

        Assert.Equal(202609260001, application.Version);
        Assert.True(application.Version > vendor.Version);
    }
}
