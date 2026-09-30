using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Vendors;

/// <summary>
/// Adds vendor applications and seeds the Vendors role so that an account has the role if and only if Customer.VendorId is set.
/// </summary>
[Migration(202609260001)]
public sealed class VendorApplicationMigration : Migration
{
    public override void Up()
    {
        Create.Table("VendorApplication")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("ShopName").AsString(400).NotNullable()
            .WithColumn("Email").AsString(320).NotNullable()
            .WithColumn("PhoneNumber").AsString(50).NotNullable()
            .WithColumn("Description").AsString(int.MaxValue).Nullable()
            .WithColumn("TaxCode").AsString(50).Nullable()
            .WithColumn("BusinessAddress").AsString(1000).Nullable()
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("RejectReason").AsString(2000).Nullable()
            .WithColumn("ReviewedByCustomerId").AsInt32().Nullable()
            .WithColumn("ReviewedOnUtc").AsDateTime2().Nullable()
            .WithColumn("VendorId").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_VendorApplication_Customer")
            .FromTable("VendorApplication").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        Create.ForeignKey("FK_VendorApplication_ReviewedBy")
            .FromTable("VendorApplication").ForeignColumn("ReviewedByCustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        Create.ForeignKey("FK_VendorApplication_Vendor")
            .FromTable("VendorApplication").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id")
            .OnDelete(Rule.SetNull);

        // A customer may have at most one pending application; also makes simultaneous submissions safe.
        Execute.Sql("""
            CREATE UNIQUE INDEX UX_VendorApplication_Customer_Pending
            ON VendorApplication (CustomerId) WHERE Status = 0;
            """);

        Create.Index("IX_VendorApplication_Status_CreatedOnUtc")
            .OnTable("VendorApplication")
            .OnColumn("Status").Ascending()
            .OnColumn("CreatedOnUtc").Ascending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM CustomerRole WHERE SystemName = 'Vendors')
                INSERT INTO CustomerRole (Name, SystemName, Active, IsSystemRole)
                VALUES ('Vendors', 'Vendors', 1, 1);

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Vendors'
              AND p.SystemName = 'vendor.portal'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );

            -- Core invariant backfill: customers linked to a deleted vendor lose the link ...
            UPDATE c SET VendorId = NULL
            FROM Customer c
            INNER JOIN Vendor v ON v.Id = c.VendorId
            WHERE v.Deleted = 1;

            -- ... and every customer still linked to a vendor gets the Vendors role.
            INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId)
            SELECT c.Id, r.Id
            FROM Customer c
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Vendors'
              AND c.VendorId IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM CustomerCustomerRoleMapping m
                  WHERE m.CustomerId = c.Id AND m.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM CustomerCustomerRoleMapping m
            INNER JOIN CustomerRole r ON r.Id = m.CustomerRoleId
            WHERE r.SystemName = 'Vendors';

            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN CustomerRole r ON r.Id = m.CustomerRoleId
            WHERE r.SystemName = 'Vendors';

            DELETE FROM CustomerRole WHERE SystemName = 'Vendors';
            """);

        Delete.Table("VendorApplication");
    }
}
