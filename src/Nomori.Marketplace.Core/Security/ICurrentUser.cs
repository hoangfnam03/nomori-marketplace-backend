namespace Nomori.Marketplace.Core.Security;

/// <summary>
/// Exposes the current principal without coupling Core to ASP.NET HttpContext.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    string? Subject { get; }

    string? Email { get; }
}

public interface IAuthenticationSession
{
    Task SignInAsync(Domain.Customers.Customer customer, bool rememberMe, CancellationToken cancellationToken);

    Task SignOutAsync(CancellationToken cancellationToken);
}