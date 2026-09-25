using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

[Migration(202609250003)]
public sealed class ProductAttributeMigration : Migration
{
    public override void Up()
    {
        Create.Table("ProductAttribute")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("Description").AsString(int.MaxValue).Nullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Table("ProductAttributeMapping")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("ProductAttributeId").AsInt32().NotNullable()
            .WithColumn("TextPrompt").AsString(int.MaxValue).Nullable()
            .WithColumn("IsRequired").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("ControlType").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_PAM_Product")
            .FromTable("ProductAttributeMapping").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.ForeignKey("FK_PAM_ProductAttribute")
            .FromTable("ProductAttributeMapping").ForeignColumn("ProductAttributeId")
            .ToTable("ProductAttribute").PrimaryColumn("Id");

        Create.Index("IX_PAM_ProductId")
            .OnTable("ProductAttributeMapping").OnColumn("ProductId");

        Create.Table("ProductAttributeValue")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductAttributeMappingId").AsInt32().NotNullable()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("ColorSquaresRgb").AsString(100).Nullable()
            .WithColumn("PriceAdjustment").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("IsPreSelected").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_PAV_PAM")
            .FromTable("ProductAttributeValue").ForeignColumn("ProductAttributeMappingId")
            .ToTable("ProductAttributeMapping").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("IX_PAV_MappingId")
            .OnTable("ProductAttributeValue").OnColumn("ProductAttributeMappingId");

        Create.Table("ProductAttributeCombination")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("AttributesJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("StockQuantity").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("AllowOutOfStockOrders").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Sku").AsString(400).Nullable()
            .WithColumn("OverriddenPrice").AsDecimal(18, 4).Nullable();

        Create.ForeignKey("FK_PAC_Product")
            .FromTable("ProductAttributeCombination").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("IX_PAC_ProductId")
            .OnTable("ProductAttributeCombination").OnColumn("ProductId");
    }

    public override void Down()
    {
        Delete.Table("ProductAttributeCombination");
        Delete.Table("ProductAttributeValue");
        Delete.Table("ProductAttributeMapping");
        Delete.Table("ProductAttribute");
    }
}
