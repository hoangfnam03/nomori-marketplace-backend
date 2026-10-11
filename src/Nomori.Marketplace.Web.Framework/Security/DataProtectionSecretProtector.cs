using Microsoft.AspNetCore.DataProtection;
using Nomori.Marketplace.Core.Settlement;

namespace Nomori.Marketplace.Web.Framework.Security;

/// <summary>Encrypts small secrets (a bank account number) with ASP.NET data protection under its own purpose.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Nomori.Marketplace.Settlement.BankAccount.v1");

    public string Protect(string plain) => protector.Protect(plain);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
}
