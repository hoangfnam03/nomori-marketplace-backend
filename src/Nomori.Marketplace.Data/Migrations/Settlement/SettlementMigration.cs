using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Settlement;

/// <summary>F05-S: commission rates, the shop ledger, bank accounts and payouts.</summary>
[Migration(202611010001)]
public sealed class SettlementMigration : Migration
{
    public override void Up()
    {
        // The rate is frozen on the shop order when it is created (the order insert fills it).
        Alter.Table("ShopOrder").AddColumn("CommissionRate").AsDecimal(5, 2).NotNullable().WithDefaultValue(0);
        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_CommissionRate CHECK (CommissionRate >= 0 AND CommissionRate <= 50);");

        Create.Table("CommissionSetting")
            .WithColumn("Id").AsInt32().PrimaryKey()
            .WithColumn("DefaultRate").AsDecimal(5, 2).NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
        Execute.Sql("ALTER TABLE CommissionSetting ADD CONSTRAINT CK_CommissionSetting_One CHECK (Id = 1);");
        Execute.Sql("ALTER TABLE CommissionSetting ADD CONSTRAINT CK_CommissionSetting_Rate CHECK (DefaultRate >= 0 AND DefaultRate <= 50);");
        Execute.Sql("INSERT INTO CommissionSetting (Id, DefaultRate, UpdatedOnUtc) VALUES (1, 0, SYSUTCDATETIME());");

        Create.Table("VendorCommission")
            .WithColumn("VendorId").AsInt32().PrimaryKey()
            .WithColumn("Rate").AsDecimal(5, 2).NotNullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_VendorCommission_Vendor").FromTable("VendorCommission").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");
        Execute.Sql("ALTER TABLE VendorCommission ADD CONSTRAINT CK_VendorCommission_Rate CHECK (Rate >= 0 AND Rate <= 50);");

        Create.Table("CommissionHistory")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("VendorId").AsInt32().Nullable()
            .WithColumn("OldRate").AsDecimal(5, 2).Nullable()
            .WithColumn("NewRate").AsDecimal(5, 2).Nullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("ChangedByCustomerId").AsInt32().NotNullable()
            .WithColumn("ChangedOnUtc").AsDateTime2().NotNullable();
        Create.Index("IX_CommissionHistory_Changed").OnTable("CommissionHistory").OnColumn("Id").Descending();

        Create.Table("SettlementPayout")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Number").AsString(40).NotNullable()
            .WithColumn("VendorId").AsInt32().NotNullable()
            // 0 pending, 1 paid, 2 failed
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("Amount").AsDecimal(18, 4).NotNullable()
            .WithColumn("CurrencyCode").AsString(3).NotNullable()
            .WithColumn("EntryCount").AsInt32().NotNullable()
            .WithColumn("CutoffUtc").AsDateTime2().NotNullable()
            .WithColumn("BankName").AsString(100).NotNullable()
            .WithColumn("AccountNumberProtected").AsString(1000).NotNullable()
            .WithColumn("AccountLast4").AsString(4).NotNullable()
            .WithColumn("HolderName").AsString(200).NotNullable()
            .WithColumn("Branch").AsString(200).Nullable()
            .WithColumn("Reference").AsString(100).Nullable()
            .WithColumn("FailureReason").AsString(500).Nullable()
            .WithColumn("CreatedByCustomerId").AsInt32().NotNullable()
            .WithColumn("ResolvedByCustomerId").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_SettlementPayout_Vendor").FromTable("SettlementPayout").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");
        Execute.Sql("ALTER TABLE SettlementPayout ADD CONSTRAINT CK_SettlementPayout_Status CHECK (Status BETWEEN 0 AND 2);");
        Execute.Sql("ALTER TABLE SettlementPayout ADD CONSTRAINT CK_SettlementPayout_Amount CHECK (Amount > 0);");
        Create.Index("UX_SettlementPayout_Number").OnTable("SettlementPayout").OnColumn("Number").Ascending().WithOptions().Unique();
        Create.Index("IX_SettlementPayout_Vendor").OnTable("SettlementPayout").OnColumn("VendorId").Ascending().OnColumn("Id").Descending();
        Create.Index("IX_SettlementPayout_Status").OnTable("SettlementPayout").OnColumn("Status").Ascending().OnColumn("Id").Descending();

        Create.Table("SettlementEntry")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("VendorId").AsInt32().NotNullable()
            // 0 sale, 1 refund, 2 adjustment
            .WithColumn("Kind").AsInt32().NotNullable()
            .WithColumn("ShopOrderId").AsInt32().Nullable()
            .WithColumn("ReturnRequestId").AsInt32().Nullable()
            .WithColumn("ShopOrderNumber").AsString(50).Nullable()
            .WithColumn("Gross").AsDecimal(18, 4).NotNullable()
            .WithColumn("Commission").AsDecimal(18, 4).NotNullable()
            .WithColumn("Amount").AsDecimal(18, 4).NotNullable()
            .WithColumn("CurrencyCode").AsString(3).NotNullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("PayoutId").AsInt32().Nullable()
            .WithColumn("CreatedByCustomerId").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_SettlementEntry_Vendor").FromTable("SettlementEntry").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");
        Create.ForeignKey("FK_SettlementEntry_ShopOrder").FromTable("SettlementEntry").ForeignColumn("ShopOrderId").ToTable("ShopOrder").PrimaryColumn("Id");
        Create.ForeignKey("FK_SettlementEntry_ReturnRequest").FromTable("SettlementEntry").ForeignColumn("ReturnRequestId").ToTable("ReturnRequest").PrimaryColumn("Id");
        Create.ForeignKey("FK_SettlementEntry_Payout").FromTable("SettlementEntry").ForeignColumn("PayoutId").ToTable("SettlementPayout").PrimaryColumn("Id");
        Execute.Sql("ALTER TABLE SettlementEntry ADD CONSTRAINT CK_SettlementEntry_Kind CHECK (Kind BETWEEN 0 AND 2);");
        // A sale and a refund say which order or return they come from; an adjustment says neither.
        Execute.Sql("""
            ALTER TABLE SettlementEntry ADD CONSTRAINT CK_SettlementEntry_Source CHECK (
                (Kind = 0 AND ShopOrderId IS NOT NULL AND ReturnRequestId IS NULL)
                OR (Kind = 1 AND ShopOrderId IS NOT NULL AND ReturnRequestId IS NOT NULL)
                OR (Kind = 2 AND ShopOrderId IS NULL AND ReturnRequestId IS NULL));
            """);

        // One sale per shop order and one refund per return: the recording job can run twice, or on two nodes, without a duplicate.
        Execute.Sql("CREATE UNIQUE INDEX UX_SettlementEntry_Sale ON SettlementEntry (ShopOrderId) WHERE Kind = 0;");
        Execute.Sql("CREATE UNIQUE INDEX UX_SettlementEntry_Refund ON SettlementEntry (ReturnRequestId) WHERE Kind = 1;");
        Create.Index("IX_SettlementEntry_Vendor_Payout").OnTable("SettlementEntry").OnColumn("VendorId").Ascending().OnColumn("PayoutId").Ascending();
        Create.Index("IX_SettlementEntry_Vendor_Created").OnTable("SettlementEntry").OnColumn("VendorId").Ascending().OnColumn("CreatedOnUtc").Descending();
        Create.Index("IX_SettlementEntry_Payout").OnTable("SettlementEntry").OnColumn("PayoutId").Ascending();

        Create.Table("VendorBankAccount")
            .WithColumn("VendorId").AsInt32().PrimaryKey()
            .WithColumn("BankName").AsString(100).NotNullable()
            .WithColumn("AccountNumberProtected").AsString(1000).NotNullable()
            .WithColumn("AccountLast4").AsString(4).NotNullable()
            .WithColumn("HolderName").AsString(200).NotNullable()
            .WithColumn("Branch").AsString(200).Nullable()
            .WithColumn("UpdatedByCustomerId").AsInt32().Nullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UsableFromUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_VendorBankAccount_Vendor").FromTable("VendorBankAccount").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'finance.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('finance.manage', 'Manage commission and payouts', 'Finance');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'finance.manage'
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
            WHERE p.SystemName = 'finance.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'finance.manage';
            """);
        Delete.Table("VendorBankAccount");
        Delete.Table("SettlementEntry");
        Delete.Table("SettlementPayout");
        Delete.Table("CommissionHistory");
        Delete.Table("VendorCommission");
        Delete.Table("CommissionSetting");
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_CommissionRate;");
        Delete.Column("CommissionRate").FromTable("ShopOrder");
    }
}
