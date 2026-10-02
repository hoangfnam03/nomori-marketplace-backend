using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Cart;
using Nomori.Marketplace.Data.Migrations.Customer;
using Nomori.Marketplace.Data.Migrations.Directory;

namespace Nomori.Marketplace.Data.Tests;

public sealed class DirectoryMigrationTests
{
    [Fact]
    public void DirectoryMigrationRunsAfterTheCartAndTheAddressBook()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610120001, Version(typeof(DirectoryMigration)));
        Assert.True(Version(typeof(DirectoryMigration)) > Version(typeof(CartMigration)));
        Assert.True(Version(typeof(DirectoryMigration)) > Version(typeof(CustomerAccountDataMigration)));
    }
}
