namespace Nomori.Marketplace.Core.Security;

/// <summary>
/// Defines authentication and authorization settings shared by hosts.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Authentication";

    public string CookieName { get; init; } = "Nomori.Auth";

    public string Scheme { get; init; } = "NomoriCookie";

    public string LoginPath { get; init; } = "/api/v1/auth/login";

    public string AccessDeniedPath { get; init; } = "/api/v1/auth/forbidden";
}