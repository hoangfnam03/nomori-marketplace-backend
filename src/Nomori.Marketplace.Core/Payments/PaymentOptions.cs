namespace Nomori.Marketplace.Core.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>The test gateway. It is only registered when it has a secret, so production simply leaves it empty.</summary>
    public SandboxPaymentOptions Sandbox { get; init; } = new();
}

public sealed class SandboxPaymentOptions
{
    public const int MinSecretLength = 16;

    /// <summary>Signs the callbacks of the sandbox. Empty means the sandbox does not exist.</summary>
    public string Secret { get; init; } = string.Empty;

    public bool IsConfigured => Secret.Length >= MinSecretLength;
}
