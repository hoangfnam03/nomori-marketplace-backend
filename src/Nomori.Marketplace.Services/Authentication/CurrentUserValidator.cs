using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Authentication;

public sealed class CurrentUserValidator(
    ICustomerIdentityStore customerStore,
    IClock clock) : ICurrentUserValidator
{
    public async Task<bool> IsValidAsync(int customerId, CancellationToken cancellationToken)
    {
        var customer = await customerStore.FindByIdAsync(customerId, cancellationToken);
        return customer is not null
            && customer.Active
            && !customer.Deleted
            && !customer.RequireReLogin
            && customer.CannotLoginUntilDateUtc <= clock.UtcNow;
    }
}
