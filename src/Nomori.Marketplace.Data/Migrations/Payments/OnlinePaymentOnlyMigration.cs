using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Payments;

/// <summary>
/// The platform offers cash on delivery, the wallet and online payment (Stripe) only: ZaloPay and the test gateways are gone, so their method
/// rows go too. Payments made with them keep their method name as history.
/// </summary>
[Migration(202611210007)]
public sealed class OnlinePaymentOnlyMigration : Migration
{
    public override void Up() =>
        Execute.Sql("DELETE FROM PaymentMethod WHERE SystemName IN ('zalopay', 'sandbox', 'sandbox_redirect');");

    public override void Down() =>
        Execute.Sql("""
            INSERT INTO PaymentMethod (SystemName, Enabled, DisplayOrder, UpdatedOnUtc)
            SELECT v.SystemName, 0, v.DisplayOrder, SYSUTCDATETIME()
            FROM (VALUES ('sandbox', 100), ('sandbox_redirect', 110), ('zalopay', 20)) AS v (SystemName, DisplayOrder)
            WHERE NOT EXISTS (SELECT 1 FROM PaymentMethod m WHERE m.SystemName = v.SystemName);
            """);
}
