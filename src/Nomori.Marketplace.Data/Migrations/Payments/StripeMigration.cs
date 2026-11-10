using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Payments;

/// <summary>Stripe Checkout as a payment method, on by default. It is offered only where the server has its keys.</summary>
[Migration(202611210005)]
public sealed class StripeMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PaymentMethod WHERE SystemName = 'stripe')
                INSERT INTO PaymentMethod (SystemName, Enabled, DisplayOrder, UpdatedOnUtc) VALUES ('stripe', 1, 30, SYSUTCDATETIME());
            """);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM PaymentMethod WHERE SystemName = 'stripe';");
    }
}
