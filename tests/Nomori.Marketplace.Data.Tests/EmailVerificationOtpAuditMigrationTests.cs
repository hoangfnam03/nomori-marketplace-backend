using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Authentication;

namespace Nomori.Marketplace.Data.Tests;

public sealed class EmailVerificationOtpAuditMigrationTests
{
    [Fact]
    public void SecurityIdentityMigrationHasExpectedVersion()
    {
        var migrationAttribute = typeof(EmailVerificationOtpAuditMigration)
            .GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
            .Cast<MigrationAttribute>()
            .Single();

        Assert.Equal(202609210002, migrationAttribute.Version);
    }
}
