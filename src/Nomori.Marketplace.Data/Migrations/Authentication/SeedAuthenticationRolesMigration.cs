using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Authentication;

[Migration(202609080003)]
public sealed class SeedAuthenticationRolesMigration : Migration
{
    public override void Up()
    {
        Insert.IntoTable("CustomerRole").Row(new
        {
            Name = "Registered",
            SystemName = "Registered",
            Active = true,
            IsSystemRole = true
        });
    }

    public override void Down()
    {
        Delete.FromTable("CustomerRole").Row(new { SystemName = "Registered" });
    }
}