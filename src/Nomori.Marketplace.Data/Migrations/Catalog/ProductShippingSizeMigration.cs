using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>A product's packed weight (grams) and size (centimetres): GHN charges by the heavier of the weight and the size.</summary>
[Migration(202611200003)]
public sealed class ProductShippingSizeMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Product")
            .AddColumn("WeightGrams").AsInt32().Nullable()
            .AddColumn("LengthCm").AsInt32().Nullable()
            .AddColumn("WidthCm").AsInt32().Nullable()
            .AddColumn("HeightCm").AsInt32().Nullable();
        Execute.Sql("""
            ALTER TABLE Product ADD CONSTRAINT CK_Product_Shipping CHECK (
                (WeightGrams IS NULL OR WeightGrams BETWEEN 1 AND 50000)
                AND ((LengthCm IS NULL AND WidthCm IS NULL AND HeightCm IS NULL)
                     OR (LengthCm BETWEEN 1 AND 200 AND WidthCm BETWEEN 1 AND 200 AND HeightCm BETWEEN 1 AND 200)));
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE Product DROP CONSTRAINT CK_Product_Shipping;");
        Delete.Column("HeightCm").Column("WidthCm").Column("LengthCm").Column("WeightGrams").FromTable("Product");
    }
}
