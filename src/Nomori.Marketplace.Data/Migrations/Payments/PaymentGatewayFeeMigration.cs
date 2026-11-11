using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Payments;

/// <summary>What the gateway charged for a payment (Stripe's fee), so the platform's report can show what payments cost.</summary>
[Migration(202611210009)]
public sealed class PaymentGatewayFeeMigration : Migration
{
    public override void Up() =>
        Alter.Table("PaymentTransaction").AddColumn("GatewayFee").AsDecimal(18, 4).Nullable();

    public override void Down() =>
        Delete.Column("GatewayFee").FromTable("PaymentTransaction");
}
