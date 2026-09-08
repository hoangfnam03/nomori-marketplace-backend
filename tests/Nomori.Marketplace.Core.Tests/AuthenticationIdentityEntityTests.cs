using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Core.Tests;

public sealed class AuthenticationIdentityEntityTests
{
    [Fact]
    public void CustomerStartsWithNewIdentityGuidAndActiveState()
    {
        var customer = new Customer();

        Assert.NotEqual(Guid.Empty, customer.CustomerGuid);
        Assert.True(customer.Active);
        Assert.False(customer.Deleted);
    }

    [Fact]
    public void NewPasswordUsesHashedFormat()
    {
        var password = new CustomerPassword();

        Assert.Equal(PasswordFormat.Hashed, password.PasswordFormat);
    }
}