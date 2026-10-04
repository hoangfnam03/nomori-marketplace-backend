using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Orders;
using Nomori.Marketplace.Data.Migrations.Payments;

namespace Nomori.Marketplace.Data.Tests;

public sealed class OrderMigrationTests
{
    [Fact]
    public void OrderMigrationRunsAfterPayments()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610180001, Version(typeof(OrderMigration)));
        Assert.True(Version(typeof(OrderMigration)) > Version(typeof(PaymentMigration)));
    }
}
