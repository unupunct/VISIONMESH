using System.Security.Cryptography;
using System.Text;

namespace VisionMesh.Core.Util;

/// <summary>
/// Encrypts camera credentials (RTSP/ONVIF passwords) at rest with AES-256-GCM.
///
/// The key lives in a single file beside the database, created with owner-only permissions.
/// This protects the database if it is copied off the machine; it deliberately does not try to
/// protect against an attacker who already has the server's own file access, which no
/// self-hosted design can do without an external KMS.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "vmenc1:";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private readonly byte[] _key;

    private SecretProtector(byte[] key) => _key = key;

    public static SecretProtector LoadOrCreate(string keyFilePath)
    {
        var dir = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        byte[] key;
        if (File.Exists(keyFilePath))
        {
            key = Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim());
            if (key.Length != 32) throw new InvalidOperationException($"Secret key file '{keyFilePath}' is corrupt (expected 32 bytes).");
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(keyFilePath, Convert.ToBase64String(key));
            RestrictToOwner(keyFilePath);
        }
        return new SecretProtector(key);
    }

    /// <summary>In-memory protector for tests. Never persists a key.</summary>
    public static SecretProtector CreateEphemeral() => new(RandomNumberGenerator.GetBytes(32));

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(_key, TagBytes)) aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceBytes);
        cipher.CopyTo(packed, NonceBytes + TagBytes);
        return Prefix + Convert.ToBase64String(packed);
    }

    /// <summary>Returns null when the value cannot be decrypted (wrong key, tampering, corruption).</summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var packed = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            if (packed.Length < NonceBytes + TagBytes) return null;
            var nonce = packed.AsSpan(0, NonceBytes);
            var tag = packed.AsSpan(NonceBytes, TagBytes);
            var cipher = packed.AsSpan(NonceBytes + TagBytes);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, TagBytes)) aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // NTFS inherits the data directory ACL, which the installer restricts.
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Non-fatal: the key still works, it is just more readable than we would like.
        }
    }
}
