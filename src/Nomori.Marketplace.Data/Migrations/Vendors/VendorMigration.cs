using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Vendors;

[Migration(202609250002)]
public sealed class VendorMigration : Migration
{
    public override void Up()
    {
        Create.Table("Vendor")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("Email").AsString(320).NotNullable()
            .WithColumn("Description").AsString(int.MaxValue).Nullable()
            .WithColumn("PictureId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("AddressId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("AdminComment").AsString(int.MaxValue).Nullable()
            .WithColumn("Active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("IX_Vendor_Active_Deleted")
            .OnTable("Vendor").OnColumn("Active").Ascending().OnColumn("Deleted").Ascending();

        Create.Table("VendorNote")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("VendorId").AsInt32().NotNullable()
            .WithColumn("Note").AsString(int.MaxValue).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_VendorNote_Vendor")
            .FromTable("VendorNote").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("IX_VendorNote_VendorId")
            .OnTable("VendorNote").OnColumn("VendorId").Ascending();

        // Add VendorId to Customer (nullable, 1-to-1 link)
        Alter.Table("Customer").AddColumn("VendorId").AsInt32().Nullable();

        Create.ForeignKey("FK_Customer_Vendor")
            .FromTable("Customer").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id")
            .OnDelete(Rule.SetNull);

        Create.Index("IX_Customer_VendorId")
            .OnTable("Customer").OnColumn("VendorId").Ascending();

        // Migrate Product.VendorId: set existing placeholder (0) to NULL, then add FK
        Execute.Sql("UPDATE Product SET VendorId = NULL WHERE VendorId = 0");
        Alter.Table("Product").AlterColumn("VendorId").AsInt32().Nullable();

        Create.ForeignKey("FK_Product_Vendor")
            .FromTable("Product").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id")
            .OnDelete(Rule.SetNull);

        Create.Index("IX_Product_VendorId")
            .OnTable("Product").OnColumn("VendorId").Ascending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'vendor.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('vendor.manage', 'Manage vendors', 'Vendors');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'vendor.manage'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );

            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'vendor.portal')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('vendor.portal', 'Access vendor portal', 'Vendors');
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN PermissionRecord p ON p.Id = m.PermissionRecordId
            WHERE p.SystemName IN ('vendor.manage', 'vendor.portal');
            DELETE FROM PermissionRecord WHERE SystemName IN ('vendor.manage', 'vendor.portal');
            """);

        Delete.ForeignKey("FK_Product_Vendor").OnTable("Product");
        Delete.Index("IX_Product_VendorId").OnTable("Product");
        Execute.Sql("UPDATE Product SET VendorId = 0 WHERE VendorId IS NULL");
        Alter.Table("Product").AlterColumn("VendorId").AsInt32().NotNullable().WithDefaultValue(0);

        Delete.ForeignKey("FK_Customer_Vendor").OnTable("Customer");
        Delete.Index("IX_Customer_VendorId").OnTable("Customer");
        Delete.Column("VendorId").FromTable("Customer");

        Delete.Table("VendorNote");
        Delete.Table("Vendor");
    }
}
