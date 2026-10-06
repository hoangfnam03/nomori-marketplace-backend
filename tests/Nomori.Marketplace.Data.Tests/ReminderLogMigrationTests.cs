using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Email;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ReminderLogMigrationTests
{
    [Fact]
    public void ReminderLogMigrationRunsAfterTheEmailQueueMigration()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610270001, Version(typeof(ReminderLogMigration)));
        Assert.True(Version(typeof(ReminderLogMigration)) > Version(typeof(EmailQueueMigration)));
    }
}
