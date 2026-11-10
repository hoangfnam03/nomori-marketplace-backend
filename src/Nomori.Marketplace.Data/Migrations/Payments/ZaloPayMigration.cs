using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Payments;

/// <summary>
/// ZaloPay as a payment method (on, offered only where the server has its app id and keys), and what a gateway gives about a payment: the
/// payment page it made, and its id of the money movement that a refund names.
/// </summary>
[Migration(202611210004)]
public sealed class ZaloPayMigration : Migration
{
    public override void Up()
    {
        Alter.Table("PaymentTransaction")
            .AddColumn("GatewayRedirectUrl").AsString(2000).Nullable()
            .AddColumn("GatewayTransactionId").AsString(100).Nullable();
        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PaymentMethod WHERE SystemName = 'zalopay')
                INSERT INTO PaymentMethod (SystemName, Enabled, DisplayOrder, UpdatedOnUtc) VALUES ('zalopay', 1, 20, SYSUTCDATETIME());
            """);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM PaymentMethod WHERE SystemName = 'zalopay';");
        Delete.Column("GatewayTransactionId").Column("GatewayRedirectUrl").FromTable("PaymentTransaction");
    }
}
