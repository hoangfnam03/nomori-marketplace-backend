namespace Nomori.Marketplace.Core.Configuration;

/// <summary>
/// Defines the database settings shared by data infrastructure and hosts.
/// </summary>
public sealed class DatabaseOptions : IConfig
{
    public const string SectionName = "Database";

    public string Provider { get; init; } = "SqlServer";

    public string ConnectionString { get; init; } = string.Empty;
}