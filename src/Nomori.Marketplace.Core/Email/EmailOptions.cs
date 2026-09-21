namespace Nomori.Marketplace.Core.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; init; }

    public string SmtpHost { get; init; } = string.Empty;

    public int SmtpPort { get; init; } = 587;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "Nomori Marketplace";

    public bool UseSsl { get; init; } = true;

    public bool UseStartTls { get; init; } = true;

    public string FrontendBaseUrl { get; init; } = string.Empty;

    public string PasswordRecoverySubject { get; init; } = "Reset your Nomori Marketplace password";
}
