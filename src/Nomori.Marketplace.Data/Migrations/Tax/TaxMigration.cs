using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Tax;

/// <summary>F07-E: tax categories and rates, the category of a product, and the tax charged on order lines, shop orders and orders.</summary>
[Migration(202610200001)]
public sealed class TaxMigration : Migration
{
    public override void Up()
    {
        Create.Table("TaxCategory")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("IsDefault").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("UX_TaxCategory_Name").OnTable("TaxCategory").OnColumn("Name").Ascending().WithOptions().Unique();
        // Exactly one default is kept by the service; at most one is kept by the database.
        Execute.Sql("CREATE UNIQUE INDEX UX_TaxCategory_Default ON TaxCategory (IsDefault) WHERE IsDefault = 1;");
        Execute.Sql("INSERT INTO TaxCategory (Name, IsDefault, DisplayOrder, CreatedOnUtc, UpdatedOnUtc) VALUES ('Standard', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());");

        Create.Table("TaxRate")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CategoryId").AsInt32().NotNullable()
            .WithColumn("CountryCode").AsString(2).NotNullable()
            .WithColumn("StateProvinceId").AsInt32().Nullable()
            .WithColumn("Percentage").AsDecimal(7, 3).NotNullable()
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_TaxRate_Category").FromTable("TaxRate").ForeignColumn("CategoryId").ToTable("TaxCategory").PrimaryColumn("Id");
        Create.ForeignKey("FK_TaxRate_StateProvince").FromTable("TaxRate").ForeignColumn("StateProvinceId").ToTable("StateProvince").PrimaryColumn("Id");
        Execute.Sql("ALTER TABLE TaxRate ADD CONSTRAINT CK_TaxRate_Percentage CHECK (Percentage >= 0 AND Percentage <= 100);");
        // One rate per category and place: the state may be null, so the two cases are two filtered indexes.
        Execute.Sql("CREATE UNIQUE INDEX UX_TaxRate_Country ON TaxRate (CategoryId, CountryCode) WHERE StateProvinceId IS NULL;");
        Execute.Sql("CREATE UNIQUE INDEX UX_TaxRate_State ON TaxRate (CategoryId, CountryCode, StateProvinceId) WHERE StateProvinceId IS NOT NULL;");
        Create.Index("IX_TaxRate_Country").OnTable("TaxRate").OnColumn("CountryCode").Ascending();

        Create.Table("ProductTaxCategory")
            .WithColumn("ProductId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("TaxCategoryId").AsInt32().NotNullable();

        Create.ForeignKey("FK_ProductTaxCategory_Product").FromTable("ProductTaxCategory").ForeignColumn("ProductId").ToTable("Product").PrimaryColumn("Id");
        Create.ForeignKey("FK_ProductTaxCategory_Category").FromTable("ProductTaxCategory").ForeignColumn("TaxCategoryId").ToTable("TaxCategory").PrimaryColumn("Id");
        Create.Index("IX_ProductTaxCategory_Category").OnTable("ProductTaxCategory").OnColumn("TaxCategoryId").Ascending();

        // The tax on orders. Existing rows get 0, so the new checks hold for them.
        Alter.Table("OrderLine")
            .AddColumn("TaxRate").AsDecimal(7, 3).NotNullable().WithDefaultValue(0)
            .AddColumn("TaxAmount").AsDecimal(18, 4).NotNullable().WithDefaultValue(0);
        Alter.Table("ShopOrder").AddColumn("TaxAmount").AsDecimal(18, 4).NotNullable().WithDefaultValue(0);
        Alter.Table("CustomerOrder").AddColumn("TaxTotal").AsDecimal(18, 4).NotNullable().WithDefaultValue(0);

        Execute.Sql("ALTER TABLE OrderLine ADD CONSTRAINT CK_OrderLine_Tax CHECK (TaxAmount >= 0 AND TaxRate >= 0 AND TaxRate <= 100);");
        Execute.Sql("ALTER TABLE CustomerOrder DROP CONSTRAINT CK_CustomerOrder_Amounts;");
        Execute.Sql("""
            ALTER TABLE CustomerOrder ADD CONSTRAINT CK_CustomerOrder_Amounts CHECK (
                Subtotal >= 0 AND ShippingTotal >= 0 AND DiscountTotal >= 0 AND DiscountTotal <= Subtotal AND TaxTotal >= 0
                AND Total = Subtotal - DiscountTotal + ShippingTotal + TaxTotal);
            """);
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Amounts;");
        Execute.Sql("""
            ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Amounts CHECK (
                Subtotal >= 0 AND ShippingFee >= 0 AND DiscountAmount >= 0 AND DiscountAmount <= Subtotal AND TaxAmount >= 0
                AND Total = Subtotal - DiscountAmount + ShippingFee + TaxAmount);
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Amounts;");
        Execute.Sql("ALTER TABLE CustomerOrder DROP CONSTRAINT CK_CustomerOrder_Amounts;");
        Execute.Sql("ALTER TABLE OrderLine DROP CONSTRAINT CK_OrderLine_Tax;");
        // The columns carry default constraints; they go with the columns.
        Delete.Column("TaxTotal").FromTable("CustomerOrder");
        Delete.Column("TaxAmount").FromTable("ShopOrder");
        Delete.Column("TaxRate").FromTable("OrderLine");
        Delete.Column("TaxAmount").FromTable("OrderLine");
        Execute.Sql("""
            ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Amounts CHECK (
                Subtotal >= 0 AND ShippingFee >= 0 AND DiscountAmount >= 0 AND DiscountAmount <= Subtotal
                AND Total = Subtotal - DiscountAmount + ShippingFee);
            """);
        Execute.Sql("""
            ALTER TABLE CustomerOrder ADD CONSTRAINT CK_CustomerOrder_Amounts CHECK (
                Subtotal >= 0 AND ShippingTotal >= 0 AND DiscountTotal >= 0 AND DiscountTotal <= Subtotal
                AND Total = Subtotal - DiscountTotal + ShippingTotal);
            """);

        Delete.Table("ProductTaxCategory");
        Delete.Table("TaxRate");
        Delete.Table("TaxCategory");
    }
}
