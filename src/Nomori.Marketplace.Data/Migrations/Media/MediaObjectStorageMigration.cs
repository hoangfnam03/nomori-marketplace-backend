using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Media;

/// <summary>
/// F08-B: image bytes may live in S3-compatible object storage. Existing assets stay in MediaAssetBinary (provider 0).
/// MediaUpload tracks presigned direct uploads until the API has validated the file.
/// </summary>
[Migration(202610130001)]
public sealed class MediaObjectStorageMigration : Migration
{
    public override void Up()
    {
        Alter.Table("MediaAsset")
            .AddColumn("StorageProvider").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("StorageKey").AsString(400).Nullable();

        Create.Table("MediaUpload")
            .WithColumn("Id").AsGuid().PrimaryKey()
            .WithColumn("Purpose").AsInt32().NotNullable()
            .WithColumn("VendorId").AsInt32().Nullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("ObjectKey").AsString(400).NotNullable()
            .WithColumn("ExpiresOnUtc").AsDateTime2().NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("MediaAssetId").AsInt32().Nullable();

        Create.ForeignKey("FK_MediaUpload_Customer")
            .FromTable("MediaUpload").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        // A completed upload whose asset is deleted keeps its row as a record; the link is cleared.
        Create.ForeignKey("FK_MediaUpload_MediaAsset")
            .FromTable("MediaUpload").ForeignColumn("MediaAssetId")
            .ToTable("MediaAsset").PrimaryColumn("Id")
            .OnDelete(Rule.SetNull);

        Create.Index("IX_MediaUpload_CustomerId").OnTable("MediaUpload").OnColumn("CustomerId").Ascending();
        Create.Index("IX_MediaUpload_MediaAssetId").OnTable("MediaUpload").OnColumn("MediaAssetId").Ascending();
    }

    public override void Down()
    {
        Delete.Table("MediaUpload");
        Delete.Column("StorageKey").FromTable("MediaAsset");
        Delete.Column("StorageProvider").FromTable("MediaAsset");
    }
}
