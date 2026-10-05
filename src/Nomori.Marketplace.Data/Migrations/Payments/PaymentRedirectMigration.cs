using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Payments;

/// <summary>F19-B: orders that wait for a redirect payment, and the test gateway with a payment page as a payment method.</summary>
[Migration(202610210001)]
public sealed class PaymentRedirectMigration : Migration
{
    public override void Up()
    {
        // Existing orders are not waiting for anything.
        Alter.Table("CustomerOrder").AddColumn("AwaitingPayment").AsBoolean().NotNullable().WithDefaultValue(false);
        Execute.Sql("CREATE INDEX IX_CustomerOrder_AwaitingPayment ON CustomerOrder (Id) WHERE AwaitingPayment = 1;");

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PaymentMethod WHERE SystemName = 'sandbox_redirect')
                INSERT INTO PaymentMethod (SystemName, Enabled, DisplayOrder, UpdatedOnUtc) VALUES ('sandbox_redirect', 0, 110, SYSUTCDATETIME());
            """);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM PaymentMethod WHERE SystemName = 'sandbox_redirect';");
        Execute.Sql("DROP INDEX IX_CustomerOrder_AwaitingPayment ON CustomerOrder;");
        // The column carries a default constraint; it goes with the column.
        Delete.Column("AwaitingPayment").FromTable("CustomerOrder");
    }
}
