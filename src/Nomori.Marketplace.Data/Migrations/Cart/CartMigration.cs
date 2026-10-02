using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Cart;

/// <summary>F16-A: the shopping cart of a signed-in customer.</summary>
[Migration(202610110001)]
public sealed class CartMigration : Migration
{
    public override void Up()
    {
        Create.Table("CartItem")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("ValueIds").AsString(200).NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("Quantity").AsInt32().NotNullable()
            .WithColumn("AddedUnitPrice").AsDecimal(18, 4).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_CartItem_Customer")
            .FromTable("CartItem").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        // No cascade: products are soft-deleted and a cart line must not block that.
        Create.ForeignKey("FK_CartItem_Product")
            .FromTable("CartItem").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.None);

        Execute.Sql("ALTER TABLE CartItem ADD CONSTRAINT CK_CartItem_Quantity CHECK (Quantity BETWEEN 1 AND 10000);");

        // The same product and variant choice is one line per customer; adding again adds to its quantity.
        Create.Index("UX_CartItem_Customer_Product_Values")
            .OnTable("CartItem")
            .OnColumn("CustomerId").Ascending()
            .OnColumn("ProductId").Ascending()
            .OnColumn("ValueIds").Ascending()
            .WithOptions().Unique();

        Create.Index("IX_CartItem_ProductId").OnTable("CartItem").OnColumn("ProductId").Ascending();
    }

    public override void Down() => Delete.Table("CartItem");
}
