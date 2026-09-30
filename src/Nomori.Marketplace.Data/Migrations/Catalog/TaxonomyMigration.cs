using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>
/// F09-A: seller restriction flag on categories and integrity for product mappings.
/// Orphan and duplicate mapping rows are deleted first, because the constraints could not be created over them.
/// </summary>
[Migration(202610020001)]
public sealed class TaxonomyMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Category").AddColumn("RestrictFromVendors").AsBoolean().NotNullable().WithDefaultValue(false);

        Execute.Sql("""
            DELETE pc FROM ProductCategory pc
            WHERE NOT EXISTS (SELECT 1 FROM Product p WHERE p.Id = pc.ProductId)
               OR NOT EXISTS (SELECT 1 FROM Category c WHERE c.Id = pc.CategoryId);

            DELETE pm FROM ProductManufacturer pm
            WHERE NOT EXISTS (SELECT 1 FROM Product p WHERE p.Id = pm.ProductId)
               OR NOT EXISTS (SELECT 1 FROM Manufacturer m WHERE m.Id = pm.ManufacturerId);

            WITH ranked AS (
                SELECT ROW_NUMBER() OVER (PARTITION BY ProductId, CategoryId ORDER BY Id) AS rn FROM ProductCategory
            )
            DELETE FROM ranked WHERE rn > 1;

            WITH ranked AS (
                SELECT ROW_NUMBER() OVER (PARTITION BY ProductId, ManufacturerId ORDER BY Id) AS rn FROM ProductManufacturer
            )
            DELETE FROM ranked WHERE rn > 1;
            """);

        Create.ForeignKey("FK_ProductCategory_Product")
            .FromTable("ProductCategory").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.ForeignKey("FK_ProductCategory_Category")
            .FromTable("ProductCategory").ForeignColumn("CategoryId")
            .ToTable("Category").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.ForeignKey("FK_ProductManufacturer_Product")
            .FromTable("ProductManufacturer").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.ForeignKey("FK_ProductManufacturer_Manufacturer")
            .FromTable("ProductManufacturer").ForeignColumn("ManufacturerId")
            .ToTable("Manufacturer").PrimaryColumn("Id").OnDelete(Rule.Cascade);

        Create.Index("UX_ProductCategory_Product_Category")
            .OnTable("ProductCategory").OnColumn("ProductId").Ascending().OnColumn("CategoryId").Ascending()
            .WithOptions().Unique();
        Create.Index("UX_ProductManufacturer_Product_Manufacturer")
            .OnTable("ProductManufacturer").OnColumn("ProductId").Ascending().OnColumn("ManufacturerId").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Index("UX_ProductManufacturer_Product_Manufacturer").OnTable("ProductManufacturer");
        Delete.Index("UX_ProductCategory_Product_Category").OnTable("ProductCategory");
        Delete.ForeignKey("FK_ProductManufacturer_Manufacturer").OnTable("ProductManufacturer");
        Delete.ForeignKey("FK_ProductManufacturer_Product").OnTable("ProductManufacturer");
        Delete.ForeignKey("FK_ProductCategory_Category").OnTable("ProductCategory");
        Delete.ForeignKey("FK_ProductCategory_Product").OnTable("ProductCategory");
        Delete.Column("RestrictFromVendors").FromTable("Category");
    }
}
