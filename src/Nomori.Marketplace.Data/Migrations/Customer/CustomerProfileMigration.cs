using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Customer;

/// <summary>
/// Adds the first Nomori customer profile slice without pulling address or checkout data into the module.
/// </summary>
[Migration(202609210003)]
public sealed class CustomerProfileMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Customer")
            .AddColumn("FirstName").AsString(100).Nullable()
            .AddColumn("LastName").AsString(100).Nullable()
            .AddColumn("Gender").AsString(20).Nullable()
            .AddColumn("DateOfBirth").AsDateTime2().Nullable()
            .AddColumn("Phone").AsString(32).Nullable();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'customer.profile.read')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('customer.profile.read', 'Read own customer profile', 'Customer');

            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'customer.profile.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('customer.profile.manage', 'Manage own customer profile', 'Customer');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName IN ('Registered', 'Administrator')
              AND p.SystemName IN ('customer.profile.read', 'customer.profile.manage')
              AND NOT EXISTS
              (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping existingMapping
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
            WHERE permission.SystemName IN ('customer.profile.read', 'customer.profile.manage');

            DELETE FROM PermissionRecord
            WHERE SystemName IN ('customer.profile.read', 'customer.profile.manage');
            """);

        Delete.Column("Phone").FromTable("Customer");
        Delete.Column("DateOfBirth").FromTable("Customer");
        Delete.Column("Gender").FromTable("Customer");
        Delete.Column("LastName").FromTable("Customer");
        Delete.Column("FirstName").FromTable("Customer");
    }
}
