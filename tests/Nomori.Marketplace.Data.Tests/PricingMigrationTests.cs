using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;
using Nomori.Marketplace.Data.Migrations.Directory;

namespace Nomori.Marketplace.Data.Tests;

public sealed class PricingMigrationTests
{
    [Fact]
    public void PricingMigrationRunsAfterTheCurrencies()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610100001, Version(typeof(PricingMigration)));
        Assert.True(Version(typeof(PricingMigration)) > Version(typeof(CurrencyMigration)));
    }
}
