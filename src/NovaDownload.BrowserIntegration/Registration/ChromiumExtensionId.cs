using System.Security.Cryptography;

namespace NovaDownload.BrowserIntegration.Registration;

/// <summary>
/// Chromium derives an unpacked extension's id from the public key in its manifest:
/// SHA-256 over the DER SubjectPublicKeyInfo, first 16 bytes, each nibble mapped to a-p.
/// Reproducing it here lets the installer write the correct native messaging allow-list
/// without ever hardcoding a browser-generated id.
/// </summary>
public static class ChromiumExtensionId
{
    private const string Alphabet = "abcdefghijklmnop";

    public static string FromPublicKey(string base64SubjectPublicKeyInfo)
    {
        if (string.IsNullOrWhiteSpace(base64SubjectPublicKeyInfo))
            throw new ArgumentException("Missing extension public key.", nameof(base64SubjectPublicKeyInfo));

        byte[] der;
        try
        {
            der = Convert.FromBase64String(base64SubjectPublicKeyInfo.Trim());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Extension public key is not valid base64.", nameof(base64SubjectPublicKeyInfo), ex);
        }

        var hash = SHA256.HashData(der);
        var chars = new char[32];
        for (var i = 0; i < 16; i++)
        {
            chars[i * 2] = Alphabet[hash[i] >> 4];
            chars[i * 2 + 1] = Alphabet[hash[i] & 0x0F];
        }
        return new string(chars);
    }

    /// <summary>True when the id has the shape Chromium accepts in <c>allowed_origins</c>.</summary>
    public static bool LooksValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length == 32 && id.All(c => c is >= 'a' and <= 'p');
}
