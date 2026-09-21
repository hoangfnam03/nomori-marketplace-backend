using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Authentication;

namespace Nomori.Marketplace.Data.Tests;

public sealed class AuthorizationPermissionMigrationTests
{
    [Fact]
    public void AuthorizationPermissionMigrationHasExpectedVersion()
    {
        var migrationAttribute = typeof(AuthorizationPermissionMigration)
            .GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
            .Cast<MigrationAttribute>()
            .Single();

        Assert.Equal(202609210001, migrationAttribute.Version);
    }
}
