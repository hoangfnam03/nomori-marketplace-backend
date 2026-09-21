using System.Security.Cryptography;
using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Services.Authentication;

public interface IPasswordHasher
{
    (string Hash, string Salt) HashPassword(string password);

    bool Verify(string password, CustomerPassword storedPassword);
}

public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    public (string Hash, string Salt) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool Verify(string password, CustomerPassword storedPassword)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedPassword.PasswordSalt))
            return false;

        var salt = Convert.FromBase64String(storedPassword.PasswordSalt);
        var expectedHash = Convert.FromBase64String(storedPassword.Password);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}