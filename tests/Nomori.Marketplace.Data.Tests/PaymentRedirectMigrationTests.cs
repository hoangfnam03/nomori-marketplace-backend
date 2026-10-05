using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Payments;
using Nomori.Marketplace.Data.Migrations.Tax;

namespace Nomori.Marketplace.Data.Tests;

public sealed class PaymentRedirectMigrationTests
{
    [Fact]
    public void PaymentRedirectMigrationRunsAfterTheOrderAndPaymentTables()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610210001, Version(typeof(PaymentRedirectMigration)));
        Assert.True(Version(typeof(PaymentRedirectMigration)) > Version(typeof(PaymentMigration)));
        Assert.True(Version(typeof(PaymentRedirectMigration)) > Version(typeof(TaxMigration)));
    }
}
