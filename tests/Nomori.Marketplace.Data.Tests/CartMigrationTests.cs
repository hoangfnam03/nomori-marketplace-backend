using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Cart;
using Nomori.Marketplace.Data.Migrations.Catalog;

namespace Nomori.Marketplace.Data.Tests;

public sealed class CartMigrationTests
{
    [Fact]
    public void CartMigrationRunsAfterPricing()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610110001, Version(typeof(CartMigration)));
        Assert.True(Version(typeof(CartMigration)) > Version(typeof(PricingMigration)));
    }
}
