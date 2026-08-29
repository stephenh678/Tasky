using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Apis.Util.Store;

namespace TodoApp.Services;

/// <summary>
/// Drop-in replacement for Google.Apis.Util.Store.FileDataStore that encrypts each stored value
/// at rest with Windows DPAPI (tied to the current Windows user account), instead of writing the
/// OAuth token JSON to disk in plaintext. A value written under one Windows account can't be read
/// back under another, and a file copied off the machine is unreadable without wrapping the
/// original account's DPAPI master key - roughly the same protection Windows gives Wi-Fi
/// passwords and Credential Manager entries.
/// </summary>
public class DpapiFileDataStore : IDataStore
{
    // Binds the encrypted blob to this specific store's purpose so it can't be swapped with
    // ciphertext DPAPI-protected by some other Tasky feature under the same Windows account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Tasky.GoogleDriveToken.v1");

    private readonly string _folder;

    public DpapiFileDataStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetFilePath(key), protectedBytes);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = GetFilePath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = GetFilePath(key);
        if (!File.Exists(path)) return Task.FromResult(default(T)!);

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return Task.FromResult(JsonSerializer.Deserialize<T>(json)!);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Either a pre-upgrade plaintext token file (not DPAPI-protected) or a token written
            // under a different Windows account/profile. Treat it as absent so the caller falls
            // back to an interactive sign-in instead of crashing - a one-time re-login, not data
            // loss, since Drive re-auth doesn't touch any task data.
            AppLogger.Warn("DpapiFileDataStore", $"Could not decrypt stored value for key '{key}' - treating as absent.");
            return Task.FromResult(default(T)!);
        }
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.GetFiles(_folder))
                File.Delete(file);
        }
        return Task.CompletedTask;
    }

    private string GetFilePath(string key) => Path.Combine(_folder, key);
}
