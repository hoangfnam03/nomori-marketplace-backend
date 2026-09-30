using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ProductOwnershipMigrationTests
{
    [Fact]
    public void OwnershipMigrationRunsAfterTaxonomy()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610030001, Version(typeof(ProductOwnershipMigration)));
        Assert.True(Version(typeof(ProductOwnershipMigration)) > Version(typeof(TaxonomyMigration)));
    }
}
