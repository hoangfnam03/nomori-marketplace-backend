namespace Nomori.Marketplace.Core.Email;

public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string? TextBody = null);
