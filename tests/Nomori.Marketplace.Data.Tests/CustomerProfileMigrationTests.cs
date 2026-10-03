using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Customer;

namespace Nomori.Marketplace.Data.Tests;

public sealed class CustomerProfileMigrationTests
{
    [Fact]
    public void CustomerProfileMigrationHasExpectedVersion()
    {
        var migrationAttribute = typeof(CustomerProfileMigration)
            .GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
            .Cast<MigrationAttribute>()
            .Single();

        Assert.Equal(202609210003, migrationAttribute.Version);
    }

    [Fact]
    public void AvatarMigrationRunsAfterTheMediaTables()
    {
        static long Version(Type migration) => migration
            .GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
            .Cast<MigrationAttribute>()
            .Single().Version;

        Assert.Equal(202610150001, Version(typeof(CustomerAvatarMigration)));
        Assert.True(Version(typeof(CustomerAvatarMigration)) > Version(typeof(Nomori.Marketplace.Data.Migrations.Media.MediaMigration)));
    }
}
