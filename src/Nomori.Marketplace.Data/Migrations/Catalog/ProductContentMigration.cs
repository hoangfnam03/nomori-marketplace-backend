using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>F10-C: identifiers, publication window and related products. The SKU is unique inside one shop among products that are not deleted.</summary>
[Migration(202610060001)]
public sealed class ProductContentMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Product")
            .AddColumn("Sku").AsString(100).Nullable()
            .AddColumn("Gtin").AsString(14).Nullable()
            .AddColumn("ManufacturerPartNumber").AsString(100).Nullable()
            .AddColumn("AvailableStartUtc").AsDateTime2().Nullable()
            .AddColumn("AvailableEndUtc").AsDateTime2().Nullable();

        // Filtered, so products without a SKU and deleted products never collide. The default collation is case-insensitive.
        Execute.Sql("CREATE UNIQUE INDEX UX_Product_VendorId_Sku ON Product (VendorId, Sku) WHERE Sku IS NOT NULL AND Deleted = 0;");

        Create.Table("ProductRelation")
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("RelatedProductId").AsInt32().NotNullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.PrimaryKey("PK_ProductRelation")
            .OnTable("ProductRelation")
            .Columns("ProductId", "RelatedProductId");

        Create.ForeignKey("FK_ProductRelation_Product")
            .FromTable("ProductRelation").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        // No action: SQL Server forbids a second cascade path, and products are soft-deleted anyway.
        Create.ForeignKey("FK_ProductRelation_RelatedProduct")
            .FromTable("ProductRelation").ForeignColumn("RelatedProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.None);

        Create.Index("IX_ProductRelation_ProductId")
            .OnTable("ProductRelation")
            .OnColumn("ProductId").Ascending()
            .OnColumn("DisplayOrder").Ascending();
    }

    public override void Down()
    {
        Delete.Table("ProductRelation");
        Execute.Sql("DROP INDEX UX_Product_VendorId_Sku ON Product;");
        Delete.Column("AvailableEndUtc").FromTable("Product");
        Delete.Column("AvailableStartUtc").FromTable("Product");
        Delete.Column("ManufacturerPartNumber").FromTable("Product");
        Delete.Column("Gtin").FromTable("Product");
        Delete.Column("Sku").FromTable("Product");
    }
}
