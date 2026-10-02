using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Directory;

/// <summary>F07-A: currencies with exactly one primary, seeded with USD, and the permission to manage them.</summary>
[Migration(202610090001)]
public sealed class CurrencyMigration : Migration
{
    public override void Up()
    {
        Create.Table("Currency")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Code").AsFixedLengthAnsiString(3).NotNullable()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Symbol").AsString(10).Nullable()
            .WithColumn("DecimalPlaces").AsInt32().NotNullable().WithDefaultValue(2)
            .WithColumn("RateToPrimary").AsDecimal(18, 8).NotNullable().WithDefaultValue(1)
            .WithColumn("IsPrimary").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("RateUpdatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("UX_Currency_Code").OnTable("Currency").OnColumn("Code").Ascending().WithOptions().Unique();

        Execute.Sql("ALTER TABLE Currency ADD CONSTRAINT CK_Currency_DecimalPlaces CHECK (DecimalPlaces BETWEEN 0 AND 4);");
        Execute.Sql("ALTER TABLE Currency ADD CONSTRAINT CK_Currency_Rate CHECK (RateToPrimary > 0);");
        // At most one primary currency, enforced by the database and not only by the service.
        Execute.Sql("CREATE UNIQUE INDEX UX_Currency_Primary ON Currency (IsPrimary) WHERE IsPrimary = 1;");

        Execute.Sql("""
            INSERT INTO Currency (Code, Name, Symbol, DecimalPlaces, RateToPrimary, IsPrimary, Published, DisplayOrder, RateUpdatedOnUtc, CreatedOnUtc, UpdatedOnUtc)
            VALUES ('USD', 'US Dollar', '$', 2, 1, 1, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
            """);

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'settings.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('settings.manage', 'Manage platform settings', 'Settings');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'settings.manage'
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
            WHERE p.SystemName = 'settings.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'settings.manage';
            """);
        Delete.Table("Currency");
    }
}
