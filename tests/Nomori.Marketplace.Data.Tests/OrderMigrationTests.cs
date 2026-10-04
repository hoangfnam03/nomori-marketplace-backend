using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Cart;
using Nomori.Marketplace.Data.Migrations.Catalog;
using Nomori.Marketplace.Data.Migrations.Orders;

namespace Nomori.Marketplace.Data.Tests;

public sealed class OrderMigrationTests
{
    private static long Version(Type migration) => migration
        .GetCustomAttributes(typeof(MigrationAttribute), inherit: false).Cast<MigrationAttribute>().Single().Version;

    [Fact]
    public void OrderMigrationRunsAfterTheTablesItReferences()
    {
        Assert.Equal(202610160001, Version(typeof(OrderMigration)));
        Assert.True(Version(typeof(OrderMigration)) > Version(typeof(CartMigration)));
        Assert.True(Version(typeof(OrderMigration)) > Version(typeof(InventoryMigration)));
    }
}
