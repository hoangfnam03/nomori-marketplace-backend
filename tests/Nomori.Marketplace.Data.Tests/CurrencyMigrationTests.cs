using FluentMigrator;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Data.Migrations.Catalog;
using Nomori.Marketplace.Data.Migrations.Directory;

namespace Nomori.Marketplace.Data.Tests;

public sealed class CurrencyMigrationTests
{
    [Fact]
    public void CurrencyMigrationRunsAfterTheSearchIndexes()
    {
        long Version(Type type) =>
            type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Version;

        Assert.Equal(202610090001, Version(typeof(CurrencyMigration)));
        Assert.True(Version(typeof(CurrencyMigration)) > Version(typeof(SearchIndexMigration)));
    }

    [Fact]
    public void TheMigrationSeedsTheSettingsPermissionTheApiRequires()
    {
        Assert.Equal("settings.manage", PermissionCodes.SettingsManage);
    }
}
