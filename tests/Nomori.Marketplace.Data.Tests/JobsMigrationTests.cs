using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Jobs;
using Nomori.Marketplace.Data.Migrations.Payments;

namespace Nomori.Marketplace.Data.Tests;

public sealed class JobsMigrationTests
{
    [Fact]
    public void JobsMigrationRunsAfterThePaymentRedirectMigrationAndThePermissionTables()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610220001, Version(typeof(JobsMigration)));
        Assert.True(Version(typeof(JobsMigration)) > Version(typeof(PaymentRedirectMigration)));
        Assert.True(Version(typeof(JobsMigration)) > Version(typeof(PaymentMigration)));
    }
}
