using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>
/// F10-A: every product has an owning shop. Adds the platform shop, assigns ownerless products to it,
/// and makes <c>Product.VendorId</c> required.
/// </summary>
[Migration(202610030001)]
public sealed class ProductOwnershipMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Vendor").AddColumn("IsPlatformShop").AsBoolean().NotNullable().WithDefaultValue(false);

        // At most one platform shop.
        Execute.Sql("CREATE UNIQUE INDEX UX_Vendor_PlatformShop ON Vendor (IsPlatformShop) WHERE IsPlatformShop = 1;");

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM Vendor WHERE IsPlatformShop = 1)
                INSERT INTO Vendor (Name, Email, Description, PictureId, AddressId, AdminComment, Active, Deleted, IsPlatformShop, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
                VALUES ('Nomori Official', 'platform@nomori.local', 'Products sold by Nomori itself.',
                        0, 0, 'Platform shop. Edit the contact email.', 1, 0, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());

            UPDATE Product
            SET VendorId = (SELECT TOP (1) Id FROM Vendor WHERE IsPlatformShop = 1)
            WHERE VendorId IS NULL;
            """);

        // The old key was ON DELETE SET NULL, which would null a now-required column. Shops are soft-deleted, so no action is needed.
        Delete.ForeignKey("FK_Product_Vendor").OnTable("Product");
        // SQL Server refuses to alter a column that an index uses, so the index from VendorMigration is rebuilt around the change.
        Delete.Index("IX_Product_VendorId").OnTable("Product");
        Alter.Table("Product").AlterColumn("VendorId").AsInt32().NotNullable();
        Create.Index("IX_Product_VendorId").OnTable("Product").OnColumn("VendorId").Ascending();
        Create.ForeignKey("FK_Product_Vendor")
            .FromTable("Product").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id");
    }

    public override void Down()
    {
        Delete.ForeignKey("FK_Product_Vendor").OnTable("Product");
        Delete.Index("IX_Product_VendorId").OnTable("Product");
        Alter.Table("Product").AlterColumn("VendorId").AsInt32().Nullable();
        Create.Index("IX_Product_VendorId").OnTable("Product").OnColumn("VendorId").Ascending();
        Create.ForeignKey("FK_Product_Vendor")
            .FromTable("Product").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.SetNull);

        Execute.Sql("DROP INDEX UX_Vendor_PlatformShop ON Vendor;");
        Delete.Column("IsPlatformShop").FromTable("Vendor");
    }
}
