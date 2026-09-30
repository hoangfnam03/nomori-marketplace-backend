using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;
using Nomori.Marketplace.Data.Migrations.Media;

namespace Nomori.Marketplace.Data.Tests;

public sealed class TaxonomyMigrationTests
{
    [Fact]
    public void TaxonomyMigrationRunsAfterCatalogAndMedia()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610020001, Version(typeof(TaxonomyMigration)));
        Assert.True(Version(typeof(TaxonomyMigration)) > Version(typeof(CatalogMigration)));
        Assert.True(Version(typeof(TaxonomyMigration)) > Version(typeof(MediaMigration)));
    }
}
