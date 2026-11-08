using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Orders;

/// <summary>F21: a shop order whose every item was returned and refunded ends as returned (6).</summary>
[Migration(202611210002)]
public sealed class ShopOrderReturnedMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Status;
            ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Status CHECK (Status BETWEEN 0 AND 6);
            """);
    }

    public override void Down()
    {
        // A returned shop order goes back to completed: its goods were sold and the refunds stay recorded on the returns.
        Execute.Sql("""
            UPDATE ShopOrder SET Status = 4 WHERE Status = 6;
            ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Status;
            ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Status CHECK (Status BETWEEN 0 AND 5);
            """);
    }
}
