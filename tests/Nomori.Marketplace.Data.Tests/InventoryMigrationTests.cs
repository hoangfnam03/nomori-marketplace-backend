using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;

namespace Nomori.Marketplace.Data.Tests;

public sealed class InventoryMigrationTests
{
    [Fact]
    public void InventoryMigrationRunsAfterContent()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610070001, Version(typeof(InventoryMigration)));
        Assert.True(Version(typeof(InventoryMigration)) > Version(typeof(ProductContentMigration)));
    }
}
