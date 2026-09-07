using System.ComponentModel.DataAnnotations;

namespace Nomori.Marketplace.Api.Configuration;

public sealed class ApplicationOptions
{
    public const string SectionName = "Application";

    [Required]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string Version { get; init; } = string.Empty;
}
