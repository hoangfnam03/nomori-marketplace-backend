using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Returns;
using Nomori.Marketplace.Data.Migrations.Settlement;

namespace Nomori.Marketplace.Data.Tests;

public sealed class SettlementMigrationTests
{
    [Fact]
    public void SettlementMigrationRunsAfterTheReturnMigrationItReferences()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202611010001, Version(typeof(SettlementMigration)));
        Assert.True(Version(typeof(SettlementMigration)) > Version(typeof(ReturnMigration)));
    }
}
