using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Discounts;
using Nomori.Marketplace.Data.Migrations.Orders;

namespace Nomori.Marketplace.Data.Tests;

public sealed class DiscountMigrationTests
{
    [Fact]
    public void DiscountMigrationRunsAfterOrdersBecauseItChangesTheirChecks()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610190001, Version(typeof(DiscountMigration)));
        Assert.True(Version(typeof(DiscountMigration)) > Version(typeof(OrderMigration)));
    }
}
