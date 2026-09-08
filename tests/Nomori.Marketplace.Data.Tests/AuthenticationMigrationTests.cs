using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Authentication;

namespace Nomori.Marketplace.Data.Tests;

public sealed class AuthenticationMigrationTests
{
    [Fact]
    public void AuthenticationMigrationHasExpectedVersion()
    {
        var migrationAttribute = typeof(AuthenticationIdentityMigration)
            .GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
            .Cast<MigrationAttribute>()
            .Single();

        Assert.Equal(202609080002, migrationAttribute.Version);
    }
}