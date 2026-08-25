using System.Security.Cryptography;

namespace VisionMesh.Core.Util;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing with a per-hash random salt.
/// Stored form: <c>pbkdf2$sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>
/// The iteration count travels with the hash so it can be raised later without invalidating
/// existing passwords.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2$sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256") return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations < 1000) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True when a stored hash used fewer iterations than we now require and should be upgraded on next login.</summary>
    public static bool NeedsRehash(string stored, int iterations = DefaultIterations)
    {
        var parts = stored.Split('$');
        return parts.Length != 5 || !int.TryParse(parts[2], out var used) || used < iterations;
    }
}

/// <summary>
/// Hashing for bearer-style secrets (device tokens, session tokens). These are already
/// high-entropy random values, so a single SHA-256 is sufficient and keeps lookup cheap -
/// unlike passwords, they are not guessable and do not need a slow KDF.
/// </summary>
public static class TokenHasher
{
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool Verify(string token, string storedHash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(storedHash)) return false;
        var actual = System.Text.Encoding.UTF8.GetBytes(Hash(token));
        var expected = System.Text.Encoding.UTF8.GetBytes(storedHash);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
