using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Identity;

namespace FyxBody.Videos.Api.Portal;

public sealed class PortalPasswords(IPasswordHasher<PortalUser> hasher)
{
    private readonly PortalUser dummy = new();
    private readonly string dummyHash = hasher.HashPassword(new PortalUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    public string Hash(PortalUser user, string password) => hasher.HashPassword(user, password);
    public PasswordVerificationResult Verify(PortalUser user, string password) => hasher.VerifyHashedPassword(user, user.PasswordHash, password);
    public void VerifyDummy(string password) => hasher.VerifyHashedPassword(dummy, dummyHash, password);
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    public static void Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 128)
            throw new ApiException(400, "invalid_password", "Passwords must contain between 12 and 128 characters.");
    }
    public static string Normalize(string? username) => (username ?? "").Trim().ToUpperInvariant();
    public static string Username(string? username)
    {
        var value = (username ?? "").Trim();
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9@._+-]{2,127}$", RegexOptions.CultureInvariant))
            throw new ApiException(400, "invalid_username", "Use 3 to 128 letters, digits or @ . _ + - characters for the username.");
        return value;
    }
    public static string DisplayName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length is < 1 or > 200 || value.Any(char.IsControl))
            throw new ApiException(400, "invalid_display_name", "Display name must contain 1 to 200 characters.");
        return value;
    }
}
