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
}
