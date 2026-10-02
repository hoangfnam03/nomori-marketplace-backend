using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Catalog;
using Nomori.Marketplace.Data.Migrations.Media;

namespace Nomori.Marketplace.Data.Tests;

public sealed class ProductPictureMigrationTests
{
    [Fact]
    public void PictureMigrationRunsAfterLifecycleAndMedia()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610050001, Version(typeof(ProductPictureMigration)));
        Assert.True(Version(typeof(ProductPictureMigration)) > Version(typeof(ProductLifecycleMigration)));
        Assert.True(Version(typeof(ProductPictureMigration)) > Version(typeof(MediaMigration)));
    }
}
