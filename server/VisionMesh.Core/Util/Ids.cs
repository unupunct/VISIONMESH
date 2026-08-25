using System.Security.Cryptography;

namespace VisionMesh.Core.Util;

/// <summary>Identifier and token generation. All randomness comes from the OS CSPRNG.</summary>
public static class Ids
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Short, URL-safe, collision-resistant id used for cameras, devices and users.</summary>
    public static string NewId(string prefix)
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return string.IsNullOrEmpty(prefix) ? new string(chars) : prefix + "_" + new string(chars);
    }

    /// <summary>A 256-bit secret rendered as URL-safe base64, used for device and session tokens.</summary>
    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// A short human-transcribable pairing code. Uses an unambiguous alphabet so a code read off
    /// a screen and typed on a phone cannot be confused (no O/0, I/1, etc).
    /// </summary>
    public static string NewPairingCode()
    {
        const string unambiguous = "ACDEFGHJKLMNPQRTUVWXY34679";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[9];
        for (int i = 0, j = 0; i < 8; i++)
        {
            if (i == 4) chars[j++] = '-';
            chars[j++] = unambiguous[bytes[i] % unambiguous.Length];
        }
        return new string(chars);
    }

    /// <summary>Replaces characters that are unsafe in a file name with '_'.</summary>
    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "unnamed" : result;
    }
}
