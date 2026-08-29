using System.Security.Cryptography;
using System.Text;

namespace TodoApp.Services;

/// <summary>
/// Encrypts small secret strings (e.g. a user-supplied Google Drive OAuth client secret) with
/// Windows DPAPI before they're written into settings.json, instead of storing them as plaintext
/// JSON. Protection is tied to the current Windows user account.
/// </summary>
public static class SecretProtector
{
    // Binds ciphertext to this specific purpose so it can't be swapped with DPAPI blobs from
    // another Tasky feature under the same Windows account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Tasky.SettingsSecret.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Either a pre-upgrade plaintext value (not yet DPAPI-protected) that got misread as
            // ciphertext, or a value protected under a different Windows account. Treat it as
            // absent rather than crashing - the user just needs to re-enter it in Settings.
            AppLogger.Warn("SecretProtector", "Could not decrypt a protected settings value - treating as absent.");
            return null;
        }
    }
}
