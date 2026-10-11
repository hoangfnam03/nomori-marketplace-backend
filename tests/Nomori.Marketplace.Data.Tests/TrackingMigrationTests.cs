using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Orders;
using Nomori.Marketplace.Data.Migrations.Shipping;

namespace Nomori.Marketplace.Data.Tests;

public sealed class TrackingMigrationTests
{
    [Fact]
    public void TrackingMigrationRunsAfterTheOrderMigrationItReferences()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202611050001, Version(typeof(TrackingMigration)));
        Assert.True(Version(typeof(TrackingMigration)) > Version(typeof(OrderMigration)));
    }
}
