using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ProductContentMigrationTests
{
    [Fact]
    public void ContentMigrationRunsAfterPictures()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610060001, Version(typeof(ProductContentMigration)));
        Assert.True(Version(typeof(ProductContentMigration)) > Version(typeof(ProductPictureMigration)));
    }
}
