using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Orders;
using Nomori.Marketplace.Data.Migrations.Returns;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ReturnMigrationTests
{
    [Fact]
    public void ReturnMigrationRunsAfterTheOrderMigrationItReferences()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610290001, Version(typeof(ReturnMigration)));
        Assert.True(Version(typeof(ReturnMigration)) > Version(typeof(OrderMigration)));
    }
}
