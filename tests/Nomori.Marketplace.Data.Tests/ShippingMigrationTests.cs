using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Cart;
using Nomori.Marketplace.Data.Migrations.Directory;
using Nomori.Marketplace.Data.Migrations.Shipping;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ShippingMigrationTests
{
    [Fact]
    public void ShippingMigrationRunsAfterTheTablesItReferences()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610160001, Version(typeof(ShippingRateMigration)));
        Assert.True(Version(typeof(ShippingRateMigration)) > Version(typeof(DirectoryMigration)));
        Assert.True(Version(typeof(ShippingRateMigration)) > Version(typeof(CartMigration)));
    }
}
