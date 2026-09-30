using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ProductLifecycleMigrationTests
{
    [Fact]
    public void LifecycleMigrationRunsAfterProductOwnership()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610040001, Version(typeof(ProductLifecycleMigration)));
        Assert.True(Version(typeof(ProductLifecycleMigration)) > Version(typeof(ProductOwnershipMigration)));
    }
}
