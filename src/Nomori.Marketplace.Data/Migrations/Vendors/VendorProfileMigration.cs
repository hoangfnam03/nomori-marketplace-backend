using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Vendors;

/// <summary>
/// Vendor shop settings: the shop keeps the phone number, tax code and business address it applied with,
/// so its members can edit them. Existing shops are filled from their approved application.
/// </summary>
[Migration(202610140001)]
public sealed class VendorProfileMigration : Migration
{
    public override void Up()
    {
        // Same lengths as VendorApplication, so values copy across unchanged.
        Alter.Table("Vendor")
            .AddColumn("PhoneNumber").AsString(50).Nullable()
            .AddColumn("TaxCode").AsString(50).Nullable()
            .AddColumn("BusinessAddress").AsString(1000).Nullable();

        // Status 1 is Approved. A vendor has at most one approved application.
        Execute.Sql("""
            UPDATE v SET PhoneNumber = a.PhoneNumber, TaxCode = a.TaxCode, BusinessAddress = a.BusinessAddress
            FROM Vendor v
            INNER JOIN VendorApplication a ON a.VendorId = v.Id AND a.Status = 1;
            """);
    }

    public override void Down()
    {
        Delete.Column("BusinessAddress").FromTable("Vendor");
        Delete.Column("TaxCode").FromTable("Vendor");
        Delete.Column("PhoneNumber").FromTable("Vendor");
    }
}
