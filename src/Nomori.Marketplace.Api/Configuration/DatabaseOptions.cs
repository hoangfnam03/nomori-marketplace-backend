namespace Nomori.Marketplace.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string? ConnectionString { get; init; }
}
