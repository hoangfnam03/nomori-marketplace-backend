using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Email;
using Nomori.Marketplace.Data.Migrations.Jobs;

namespace Nomori.Marketplace.Data.Tests;

public sealed class EmailQueueMigrationTests
{
    [Fact]
    public void EmailQueueMigrationRunsAfterTheJobsMigrationAndThePermissionTables()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610250001, Version(typeof(EmailQueueMigration)));
        Assert.True(Version(typeof(EmailQueueMigration)) > Version(typeof(JobsMigration)));
    }
}
