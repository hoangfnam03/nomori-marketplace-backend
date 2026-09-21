using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Authentication;

[Migration(202609080005)]
public sealed class PermissionMigration : Migration
{
    public override void Up()
    {
        Create.Table("PermissionRecord")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("SystemName").AsString(255).NotNullable()
            .WithColumn("Name").AsString(255).NotNullable()
            .WithColumn("Category").AsString(100).NotNullable();
        Create.Index("IX_PermissionRecord_SystemName").OnTable("PermissionRecord").OnColumn("SystemName").Ascending().WithOptions().Unique();

        Create.Table("PermissionRecordCustomerRoleMapping")
            .WithColumn("PermissionRecordId").AsInt32().NotNullable()
            .WithColumn("CustomerRoleId").AsInt32().NotNullable();
        Create.PrimaryKey("PK_PermissionRecordCustomerRoleMapping").OnTable("PermissionRecordCustomerRoleMapping").Columns("PermissionRecordId", "CustomerRoleId");
        Create.ForeignKey("FK_PermissionRecordCustomerRoleMapping_Permission").FromTable("PermissionRecordCustomerRoleMapping").ForeignColumn("PermissionRecordId").ToTable("PermissionRecord").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.ForeignKey("FK_PermissionRecordCustomerRoleMapping_Role").FromTable("PermissionRecordCustomerRoleMapping").ForeignColumn("CustomerRoleId").ToTable("CustomerRole").PrimaryColumn("Id").OnDelete(Rule.Cascade);

        Insert.IntoTable("PermissionRecord").Row(new { SystemName = "auth.session.read", Name = "Read authentication session", Category = "Authentication" });
        Insert.IntoTable("PermissionRecordCustomerRoleMapping").Row(new
        {
            PermissionRecordId = 1,
            CustomerRoleId = 1
        });
    }

    public override void Down()
    {
        Delete.Table("PermissionRecordCustomerRoleMapping");
        Delete.Table("PermissionRecord");
    }
}