using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

[Migration(202609250001)]
public sealed class CatalogMigration : Migration
{
    public override void Up()
    {
        Create.Table("Category")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("Description").AsString(int.MaxValue).Nullable()
            .WithColumn("ParentCategoryId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("PictureId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("ShowOnHomepage").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("IX_Category_ParentCategoryId")
            .OnTable("Category").OnColumn("ParentCategoryId").Ascending();

        Create.Table("Manufacturer")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("Description").AsString(int.MaxValue).Nullable()
            .WithColumn("PictureId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Table("Product")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("ShortDescription").AsString(int.MaxValue).Nullable()
            .WithColumn("FullDescription").AsString(int.MaxValue).Nullable()
            .WithColumn("Price").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("OldPrice").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("StockQuantity").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("VendorId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("ShowOnHomepage").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("IX_Product_Published_Deleted")
            .OnTable("Product").OnColumn("Published").Ascending().OnColumn("Deleted").Ascending();

        Create.Table("ProductCategory")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("CategoryId").AsInt32().NotNullable()
            .WithColumn("IsFeaturedProduct").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("IX_ProductCategory_ProductId")
            .OnTable("ProductCategory").OnColumn("ProductId").Ascending();
        Create.Index("IX_ProductCategory_CategoryId")
            .OnTable("ProductCategory").OnColumn("CategoryId").Ascending();

        Create.Table("ProductManufacturer")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("ManufacturerId").AsInt32().NotNullable()
            .WithColumn("IsFeaturedProduct").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("IX_ProductManufacturer_ProductId")
            .OnTable("ProductManufacturer").OnColumn("ProductId").Ascending();
        Create.Index("IX_ProductManufacturer_ManufacturerId")
            .OnTable("ProductManufacturer").OnColumn("ManufacturerId").Ascending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'catalog.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('catalog.manage', 'Manage catalog (categories, products, manufacturers)', 'Catalog');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'catalog.manage'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN PermissionRecord p ON p.Id = m.PermissionRecordId
            WHERE p.SystemName = 'catalog.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'catalog.manage';
            """);

        Delete.Table("ProductManufacturer");
        Delete.Table("ProductCategory");
        Delete.Table("Product");
        Delete.Table("Manufacturer");
        Delete.Table("Category");
    }
}
