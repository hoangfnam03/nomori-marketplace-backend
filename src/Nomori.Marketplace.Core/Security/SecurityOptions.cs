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

    public int MaxFailedLoginAttempts { get; init; } = 5;

    public int LockoutMinutes { get; init; } = 15;

    public int RecoveryTokenLifetimeMinutes { get; init; } = 30;

    public int MinimumPasswordLength { get; init; } = 12;

    public int PasswordHistoryLimit { get; init; } = 5;

    public int SessionLifetimeMinutes { get; init; } = 60;

    public int RememberMeLifetimeDays { get; init; } = 30;

    public int EmailVerificationTokenLifetimeHours { get; init; } = 24;

    public int EmailOtpLifetimeMinutes { get; init; } = 10;

    public int EmailOtpMaxAttempts { get; init; } = 5;

    public int EmailOtpResendDelaySeconds { get; init; } = 60;
}
