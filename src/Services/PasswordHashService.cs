using System.Security.Cryptography;

namespace MiniERP.Api.Services;

/// <summary>
/// PBKDF2 password hashing (SHA-256). Stored format:
/// "pbkdf2$iterations$saltB64$hashB64". Replaces the legacy plaintext
/// APP_USER.PASSWORD field for demo/local auth.
/// </summary>
public static class PasswordHashService
{
    public const int DefaultIterations = 210000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored) =>
        Verify(password, stored, null, null);

    public static bool Verify(string password, string? stored, string? salt, int? iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        var value = stored;
        if (!stored.StartsWith("pbkdf2$", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(salt))
        {
            value = $"pbkdf2${iterations ?? PasswordHashService.DefaultIterations}${salt}${stored}";
        }
        var parts = value.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var rounds) || rounds <= 0) return false;
        byte[] saltBytes;
        byte[] expected;
        try
        {
            saltBytes = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, rounds,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
