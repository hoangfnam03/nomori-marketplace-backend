using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Directory;
using Nomori.Marketplace.Data.Migrations.Payments;
using Nomori.Marketplace.Data.Migrations.Shipping;

namespace Nomori.Marketplace.Data.Tests;

public sealed class PaymentMigrationTests
{
    [Fact]
    public void PaymentMigrationRunsAfterTheTablesAndPermissionsItNeeds()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610170001, Version(typeof(PaymentMigration)));
        Assert.True(Version(typeof(PaymentMigration)) > Version(typeof(ShippingRateMigration)));
        Assert.True(Version(typeof(PaymentMigration)) > Version(typeof(CurrencyMigration)));
    }
}
