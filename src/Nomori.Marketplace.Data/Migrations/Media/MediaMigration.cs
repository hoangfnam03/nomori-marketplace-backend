using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Media;

/// <summary>F08-A: image metadata and binary storage. Binary data lives in its own table so listing metadata never reads image bytes.</summary>
[Migration(202610010001)]
public sealed class MediaMigration : Migration
{
    public override void Up()
    {
        Create.Table("MediaAsset")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Purpose").AsInt32().NotNullable()
            .WithColumn("Visibility").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("MimeType").AsString(100).NotNullable()
            .WithColumn("SizeBytes").AsInt32().NotNullable()
            .WithColumn("Sha256").AsFixedLengthAnsiString(64).NotNullable()
            .WithColumn("UploadedByCustomerId").AsInt32().NotNullable()
            .WithColumn("VendorId").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_MediaAsset_Customer")
            .FromTable("MediaAsset").ForeignColumn("UploadedByCustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        Create.ForeignKey("FK_MediaAsset_Vendor")
            .FromTable("MediaAsset").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id")
            .OnDelete(Rule.SetNull);

        Create.Index("IX_MediaAsset_VendorId").OnTable("MediaAsset").OnColumn("VendorId").Ascending();
        Create.Index("IX_MediaAsset_UploadedByCustomerId").OnTable("MediaAsset").OnColumn("UploadedByCustomerId").Ascending();

        Create.Table("MediaAssetBinary")
            .WithColumn("MediaAssetId").AsInt32().PrimaryKey()
            .WithColumn("Data").AsBinary(int.MaxValue).NotNullable();

        Create.ForeignKey("FK_MediaAssetBinary_MediaAsset")
            .FromTable("MediaAssetBinary").ForeignColumn("MediaAssetId")
            .ToTable("MediaAsset").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        Delete.Table("MediaAssetBinary");
        Delete.Table("MediaAsset");
    }
}
