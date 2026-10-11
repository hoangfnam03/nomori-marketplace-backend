using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Reports;

/// <summary>F26-A: the permission to see the platform reports. The reports themselves read existing tables.</summary>
[Migration(202611100001)]
public sealed class ReportsMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'reports.view')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('reports.view', 'View platform reports', 'Reports');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'reports.view'
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
            WHERE p.SystemName = 'reports.view';
            DELETE FROM PermissionRecord WHERE SystemName = 'reports.view';
            """);
    }
}
