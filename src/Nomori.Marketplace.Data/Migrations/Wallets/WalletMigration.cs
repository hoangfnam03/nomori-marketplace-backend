using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Wallets;

/// <summary>
/// The customer's wallet: its balance (never below zero) and a ledger of every change, each posted once by its key. And the wallet as a
/// payment method, on by default.
/// </summary>
[Migration(202611210006)]
public sealed class WalletMigration : Migration
{
    public override void Up()
    {
        Create.Table("Wallet")
            .WithColumn("CustomerId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("Balance").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_Wallet_Customer").FromTable("Wallet").ForeignColumn("CustomerId").ToTable("Customer").PrimaryColumn("Id");
        Execute.Sql("ALTER TABLE Wallet ADD CONSTRAINT CK_Wallet_Balance CHECK (Balance >= 0);");

        Create.Table("WalletEntry")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("Kind").AsInt32().NotNullable()
            .WithColumn("Amount").AsDecimal(18, 4).NotNullable()
            .WithColumn("BalanceAfter").AsDecimal(18, 4).NotNullable()
            .WithColumn("ReferenceType").AsString(30).Nullable()
            .WithColumn("ReferenceId").AsInt32().Nullable()
            .WithColumn("IdempotencyKey").AsString(100).NotNullable()
            .WithColumn("Note").AsString(200).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_WalletEntry_Wallet").FromTable("WalletEntry").ForeignColumn("CustomerId").ToTable("Wallet").PrimaryColumn("CustomerId");
        Execute.Sql("""
            ALTER TABLE WalletEntry ADD CONSTRAINT CK_WalletEntry CHECK (Kind BETWEEN 0 AND 4 AND Amount <> 0 AND BalanceAfter >= 0);
            CREATE UNIQUE INDEX UX_WalletEntry_Key ON WalletEntry (IdempotencyKey);
            CREATE INDEX IX_WalletEntry_Customer ON WalletEntry (CustomerId, Id DESC);
            IF NOT EXISTS (SELECT 1 FROM PaymentMethod WHERE SystemName = 'wallet')
                INSERT INTO PaymentMethod (SystemName, Enabled, DisplayOrder, UpdatedOnUtc) VALUES ('wallet', 1, 5, SYSUTCDATETIME());
            """);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM PaymentMethod WHERE SystemName = 'wallet';");
        Delete.Table("WalletEntry");
        Delete.Table("Wallet");
    }
}
