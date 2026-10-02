using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>F14-A: special price with a window, and quantity (tier) prices.</summary>
[Migration(202610100001)]
public sealed class PricingMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Product")
            .AddColumn("SpecialPrice").AsDecimal(18, 4).Nullable()
            .AddColumn("SpecialPriceStartUtc").AsDateTime2().Nullable()
            .AddColumn("SpecialPriceEndUtc").AsDateTime2().Nullable();

        Create.Table("ProductTierPrice")
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("Quantity").AsInt32().NotNullable()
            .WithColumn("Price").AsDecimal(18, 4).NotNullable();

        Create.PrimaryKey("PK_ProductTierPrice")
            .OnTable("ProductTierPrice")
            .Columns("ProductId", "Quantity");

        Create.ForeignKey("FK_ProductTierPrice_Product")
            .FromTable("ProductTierPrice").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Execute.Sql("ALTER TABLE ProductTierPrice ADD CONSTRAINT CK_ProductTierPrice_Quantity CHECK (Quantity >= 2);");
        Execute.Sql("ALTER TABLE ProductTierPrice ADD CONSTRAINT CK_ProductTierPrice_Price CHECK (Price > 0);");
    }

    public override void Down()
    {
        Delete.Table("ProductTierPrice");
        Delete.Column("SpecialPriceEndUtc").FromTable("Product");
        Delete.Column("SpecialPriceStartUtc").FromTable("Product");
        Delete.Column("SpecialPrice").FromTable("Product");
    }
}
