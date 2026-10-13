using System.Security.Cryptography;
using System.Text;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>Random secrets and their hashes. Plain values are handed out once and never stored or logged.</summary>
public static class Secrets
{
    public static string NewHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Compares a presented plain value with a stored hash in constant time.</summary>
    public static bool Matches(string value, string hash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(value)), Encoding.ASCII.GetBytes(hash));
}
