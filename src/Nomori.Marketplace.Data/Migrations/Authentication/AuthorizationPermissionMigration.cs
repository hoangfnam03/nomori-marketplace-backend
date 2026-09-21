using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Authentication;

/// <summary>
/// Adds the stable MVP permission catalog and maps customer-facing permissions
/// to the seeded Registered role without relying on identity values.
/// </summary>
[Migration(202609210001)]
public sealed class AuthorizationPermissionMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'auth.authenticated')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('auth.authenticated', 'Authenticated customer', 'Authentication');

            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'auth.permissions.read')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('auth.permissions.read', 'Read own permissions', 'Authorization');

            IF NOT EXISTS (SELECT 1 FROM CustomerRole WHERE SystemName = 'Administrator')
                INSERT INTO CustomerRole (Name, SystemName, Active, IsSystemRole)
                VALUES ('Administrators', 'Administrator', 1, 1);

            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'admin.access')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('admin.access', 'Access administration', 'Administration');

            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'admin.roles.read')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('admin.roles.read', 'Read customer roles', 'Administration');

            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'admin.roles.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('admin.roles.manage', 'Manage customer roles', 'Administration');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Registered'
              AND p.SystemName IN ('auth.authenticated', 'auth.permissions.read')
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM PermissionRecordCustomerRoleMapping existingMapping
                  WHERE existingMapping.PermissionRecordId = p.Id
                  AND existingMapping.CustomerRoleId = r.Id
              );

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName IN ('auth.authenticated', 'auth.permissions.read', 'admin.access', 'admin.roles.read', 'admin.roles.manage')
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM PermissionRecordCustomerRoleMapping existingMapping
                  WHERE existingMapping.PermissionRecordId = p.Id
                    AND existingMapping.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE mapping
            FROM PermissionRecordCustomerRoleMapping mapping
            INNER JOIN PermissionRecord permission ON permission.Id = mapping.PermissionRecordId
            WHERE permission.SystemName IN ('auth.authenticated', 'auth.permissions.read');

            DELETE FROM PermissionRecord
            WHERE SystemName IN ('auth.authenticated', 'auth.permissions.read', 'admin.access', 'admin.roles.read', 'admin.roles.manage');

            DELETE FROM CustomerRole
            WHERE SystemName = 'Administrator';
            """);
    }
}
