namespace Nomori.Marketplace.Core.Security;

public sealed record CustomerSearchResult(
    int Id,
    string Email,
    string? Username,
    string? FirstName,
    string? LastName);
