using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Discounts;
using Nomori.Marketplace.Data.Migrations.Tax;

namespace Nomori.Marketplace.Data.Tests;

public sealed class TaxMigrationTests
{
    [Fact]
    public void TaxMigrationRunsAfterDiscountsBecauseItReplacesTheirChecks()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610200001, Version(typeof(TaxMigration)));
        Assert.True(Version(typeof(TaxMigration)) > Version(typeof(DiscountMigration)));
    }
}
