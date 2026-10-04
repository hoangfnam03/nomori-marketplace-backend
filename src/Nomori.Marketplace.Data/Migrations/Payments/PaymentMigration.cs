using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Payments;

/// <summary>F19-A: payment methods, payments with their lifecycle, gateway events, and the permission to manage them.</summary>
[Migration(202610170001)]
public sealed class PaymentMigration : Migration
{
    public override void Up()
    {
        Create.Table("PaymentMethod")
            .WithColumn("SystemName").AsString(50).NotNullable().PrimaryKey()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Execute.Sql("""
            INSERT INTO PaymentMethod (SystemName, Enabled, DisplayOrder, UpdatedOnUtc) VALUES
                ('cod', 1, 0, SYSUTCDATETIME()),
                ('sandbox', 0, 100, SYSUTCDATETIME());
            """);

        Create.Table("PaymentTransaction")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ReferenceType").AsString(30).NotNullable()
            .WithColumn("ReferenceId").AsInt32().NotNullable()
            .WithColumn("IdempotencyKey").AsString(100).NotNullable()
            .WithColumn("Method").AsString(50).NotNullable()
            .WithColumn("CustomerId").AsInt32().Nullable()
            .WithColumn("Amount").AsDecimal(18, 4).NotNullable()
            .WithColumn("CurrencyCode").AsString(3).NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("RefundedAmount").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("ProviderReference").AsString(200).Nullable()
            .WithColumn("FailureCode").AsString(100).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        // No cascade: a payment is a financial record and outlives the account's data.
        Create.ForeignKey("FK_PaymentTransaction_Customer")
            .FromTable("PaymentTransaction").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        Execute.Sql("ALTER TABLE PaymentTransaction ADD CONSTRAINT CK_PaymentTransaction_Amount CHECK (Amount > 0);");
        Execute.Sql("ALTER TABLE PaymentTransaction ADD CONSTRAINT CK_PaymentTransaction_Status CHECK (Status BETWEEN 0 AND 6);");
        // Refunds can never exceed what was paid, whatever the application does.
        Execute.Sql("ALTER TABLE PaymentTransaction ADD CONSTRAINT CK_PaymentTransaction_Refunded CHECK (RefundedAmount >= 0 AND RefundedAmount <= Amount);");

        Create.Index("UX_PaymentTransaction_IdempotencyKey").OnTable("PaymentTransaction").OnColumn("IdempotencyKey").Ascending().WithOptions().Unique();
        Create.Index("IX_PaymentTransaction_Reference").OnTable("PaymentTransaction")
            .OnColumn("ReferenceType").Ascending().OnColumn("ReferenceId").Ascending();
        Create.Index("IX_PaymentTransaction_Status").OnTable("PaymentTransaction").OnColumn("Status").Ascending().OnColumn("Id").Descending();
        Execute.Sql("""
            CREATE UNIQUE INDEX UX_PaymentTransaction_Provider ON PaymentTransaction (Method, ProviderReference)
            WHERE ProviderReference IS NOT NULL;
            """);

        Create.Table("PaymentEvent")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TransactionId").AsInt32().Nullable()
            .WithColumn("Provider").AsString(50).NotNullable()
            .WithColumn("ProviderEventId").AsString(200).NotNullable()
            .WithColumn("Type").AsString(50).NotNullable()
            .WithColumn("Outcome").AsString(20).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_PaymentEvent_Transaction")
            .FromTable("PaymentEvent").ForeignColumn("TransactionId")
            .ToTable("PaymentTransaction").PrimaryColumn("Id");

        // One row per gateway event id: this is what makes a replayed callback harmless.
        Create.Index("UX_PaymentEvent_Provider_Event").OnTable("PaymentEvent")
            .OnColumn("Provider").Ascending().OnColumn("ProviderEventId").Ascending().WithOptions().Unique();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'payments.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('payments.manage', 'Manage payments', 'Payments');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'payments.manage'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN PermissionRecord p ON p.Id = m.PermissionRecordId
            WHERE p.SystemName = 'payments.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'payments.manage';
            """);
        Delete.Table("PaymentEvent");
        Delete.Table("PaymentTransaction");
        Delete.Table("PaymentMethod");
    }
}
