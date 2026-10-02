using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Directory;

/// <summary>F07-C: countries, states and provinces, and the state of a saved address. Seeds a curated list of countries and no states.</summary>
[Migration(202610120001)]
public sealed class DirectoryMigration : Migration
{
    // code, alpha-3, name, display order, postal pattern (null when the format is not seeded)
    private static readonly (string Code, string Alpha3, string Name, int Order, string? Pattern)[] Countries =
    [
        ("VN", "VNM", "Vietnam", 1, null),
        ("US", "USA", "United States", 2, @"\d{5}(-\d{4})?"),
        ("GB", "GBR", "United Kingdom", 3, @"[A-Za-z]{1,2}\d[A-Za-z\d]? ?\d[A-Za-z]{2}"),
        ("CA", "CAN", "Canada", 4, @"[A-Za-z]\d[A-Za-z] ?\d[A-Za-z]\d"),
        ("AU", "AUS", "Australia", 5, @"\d{4}"),
        ("NZ", "NZL", "New Zealand", 6, null),
        ("SG", "SGP", "Singapore", 7, @"\d{6}"),
        ("JP", "JPN", "Japan", 8, @"\d{3}-?\d{4}"),
        ("KR", "KOR", "South Korea", 9, @"\d{5}"),
        ("CN", "CHN", "China", 10, @"\d{6}"),
        ("HK", "HKG", "Hong Kong", 11, null),
        ("TW", "TWN", "Taiwan", 12, null),
        ("TH", "THA", "Thailand", 13, null),
        ("MY", "MYS", "Malaysia", 14, null),
        ("ID", "IDN", "Indonesia", 15, null),
        ("PH", "PHL", "Philippines", 16, null),
        ("KH", "KHM", "Cambodia", 17, null),
        ("LA", "LAO", "Laos", 18, null),
        ("MM", "MMR", "Myanmar", 19, null),
        ("IN", "IND", "India", 20, @"\d{6}"),
        ("DE", "DEU", "Germany", 21, @"\d{5}"),
        ("FR", "FRA", "France", 22, @"\d{5}"),
        ("IT", "ITA", "Italy", 23, @"\d{5}"),
        ("ES", "ESP", "Spain", 24, @"\d{5}"),
        ("NL", "NLD", "Netherlands", 25, @"\d{4} ?[A-Za-z]{2}"),
        ("BE", "BEL", "Belgium", 26, null),
        ("CH", "CHE", "Switzerland", 27, null),
        ("AT", "AUT", "Austria", 28, null),
        ("SE", "SWE", "Sweden", 29, null),
        ("NO", "NOR", "Norway", 30, null),
        ("DK", "DNK", "Denmark", 31, null),
        ("FI", "FIN", "Finland", 32, null),
        ("IE", "IRL", "Ireland", 33, null),
        ("PT", "PRT", "Portugal", 34, null),
        ("PL", "POL", "Poland", 35, null),
        ("AE", "ARE", "United Arab Emirates", 36, null),
        ("SA", "SAU", "Saudi Arabia", 37, null),
        ("BR", "BRA", "Brazil", 38, null),
        ("MX", "MEX", "Mexico", 39, null),
        ("ZA", "ZAF", "South Africa", 40, null)
    ];

    public override void Up()
    {
        Create.Table("Country")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Code").AsFixedLengthAnsiString(2).NotNullable()
            .WithColumn("Alpha3").AsFixedLengthAnsiString(3).Nullable()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("AllowsBilling").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("AllowsShipping").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("PostalCodeRequired").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("PostalCodePattern").AsString(200).Nullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("UX_Country_Code").OnTable("Country").OnColumn("Code").Ascending().WithOptions().Unique();

        Create.Table("StateProvince")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CountryId").AsInt32().NotNullable()
            .WithColumn("Code").AsString(20).NotNullable()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_StateProvince_Country")
            .FromTable("StateProvince").ForeignColumn("CountryId")
            .ToTable("Country").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("UX_StateProvince_Country_Code")
            .OnTable("StateProvince")
            .OnColumn("CountryId").Ascending()
            .OnColumn("Code").Ascending()
            .WithOptions().Unique();

        Alter.Table("CustomerAddress").AddColumn("StateProvinceId").AsInt32().Nullable();

        // No cascade and no set-null: a state that an address uses cannot be removed by accident.
        Create.ForeignKey("FK_CustomerAddress_StateProvince")
            .FromTable("CustomerAddress").ForeignColumn("StateProvinceId")
            .ToTable("StateProvince").PrimaryColumn("Id")
            .OnDelete(Rule.None);

        foreach (var (code, alpha3, name, order, pattern) in Countries)
        {
            // Values are constants from this file; the pattern is escaped for a SQL literal.
            var patternSql = pattern is null ? "NULL" : "N'" + pattern.Replace("'", "''", StringComparison.Ordinal) + "'";
            Execute.Sql($"""
                INSERT INTO Country (Code, Alpha3, Name, Published, AllowsBilling, AllowsShipping, PostalCodeRequired, PostalCodePattern, DisplayOrder, CreatedOnUtc, UpdatedOnUtc)
                VALUES ('{code}', '{alpha3}', N'{name}', 1, 1, 1, {(pattern is null ? 0 : 1)}, {patternSql}, {order}, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
        }
    }

    public override void Down()
    {
        Delete.ForeignKey("FK_CustomerAddress_StateProvince").OnTable("CustomerAddress");
        Delete.Column("StateProvinceId").FromTable("CustomerAddress");
        Delete.Table("StateProvince");
        Delete.Table("Country");
    }
}
