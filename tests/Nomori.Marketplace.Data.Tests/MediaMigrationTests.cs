using FluentMigrator;
using Nomori.Marketplace.Data.Migrations.Media;
using Nomori.Marketplace.Data.Migrations.Vendors;

namespace Nomori.Marketplace.Data.Tests;

public sealed class MediaMigrationTests
{
    [Fact]
    public void MediaMigrationRunsAfterTheVendorTables()
    {
        var media = typeof(MediaMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();
        var application = typeof(VendorApplicationMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();

        Assert.Equal(202610010001, media.Version);
        Assert.True(media.Version > application.Version);
    }

    [Fact]
    public void ObjectStorageMigrationRunsAfterTheMediaTables()
    {
        var media = typeof(MediaMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();
        var storage = typeof(MediaObjectStorageMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();

        Assert.Equal(202610130001, storage.Version);
        Assert.True(storage.Version > media.Version);
    }
}
