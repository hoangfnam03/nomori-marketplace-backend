using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Reports;
using Nomori.Marketplace.Data.Migrations.Settlement;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ReportsMigrationTests
{
    [Fact]
    public void ReportsMigrationRunsAfterTheSettlementMigrationItReads()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202611100001, Version(typeof(ReportsMigration)));
        Assert.True(Version(typeof(ReportsMigration)) > Version(typeof(SettlementMigration)));
    }
}
