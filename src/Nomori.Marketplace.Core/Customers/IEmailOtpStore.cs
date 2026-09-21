namespace Nomori.Marketplace.Core.Customers;

public sealed record EmailOtpChallenge(
    Guid ChallengeId,
    int CustomerId,
    string Purpose,
    string CodeHash,
    DateTime CreatedOnUtc,
    DateTime ExpiresOnUtc,
    DateTime? LastSentOnUtc,
    int FailedAttempts,
    bool Used);

public sealed record EmailOtpConsumeResult(bool Succeeded, EmailOtpChallenge? Challenge, string? FailureCode);

public interface IEmailOtpStore
{
    Task<EmailOtpChallenge?> GetLatestAsync(int customerId, string purpose, CancellationToken cancellationToken);

    Task CreateAsync(EmailOtpChallenge challenge, CancellationToken cancellationToken);

    Task<EmailOtpConsumeResult> ConsumeAsync(Guid challengeId, string purpose, string codeHash, DateTime nowUtc, int maxAttempts, CancellationToken cancellationToken);

    Task SetEmailOtpEnabledAsync(int customerId, bool enabled, CancellationToken cancellationToken);
}
