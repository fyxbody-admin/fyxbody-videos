using System.Security.Cryptography;
using System.Text;
namespace FyxBody.Videos.Api;

public static class CredentialSecrets
{
    public static (string KeyId, string Secret, string Hash) Generate()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return ($"fv_{Guid.NewGuid():N}", secret, Hash(secret));
    }

    // SHA-256 is appropriate for random 256-bit API secrets, never human passwords.
    public static string Hash(string secret) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static bool Verify(string secret, string hash)
    {
        if (hash.Length != 71 || !hash.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(hash));
    }

    public static bool VerifyAdministrator(string provided, string configuredHash)
    {
        if (configuredHash.Length != 64 || provided.Length < 32 || provided.Length > 512) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), Convert.FromHexString(configuredHash));
        }
        catch (FormatException) { return false; }
    }
}
