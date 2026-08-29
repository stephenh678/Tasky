using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TodoApp.Services;

namespace TodoApp.Tests;

// Each test gets its own scratch directory under the OS temp folder so these never touch the
// real %AppData%\Tasky\GoogleDriveToken folder, and can run in parallel without colliding.
public class SecretProtectionTests : IDisposable
{
    private readonly string _dir;

    public SecretProtectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TaskySecretTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void SecretProtector_ProtectThenUnprotect_RoundTripsPlaintext()
    {
        var result = SecretProtector.Unprotect(SecretProtector.Protect("GOCSPX-example-secret"));

        Assert.Equal("GOCSPX-example-secret", result);
    }

    [Fact]
    public void SecretProtector_Protect_NeverEmitsPlaintextInOutput()
    {
        var protectedValue = SecretProtector.Protect("GOCSPX-example-secret");

        Assert.DoesNotContain("GOCSPX-example-secret", protectedValue);
    }

    [Fact]
    public void SecretProtector_Protect_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(SecretProtector.Protect(null));
        Assert.Null(SecretProtector.Protect(""));
    }

    [Fact]
    public void SecretProtector_Unprotect_GarbageInput_ReturnsNullInsteadOfThrowing()
    {
        var result = SecretProtector.Unprotect("not a real protected blob");

        Assert.Null(result);
    }

    [Fact]
    public async Task DpapiFileDataStore_StoreThenGet_RoundTripsValue()
    {
        var store = new DpapiFileDataStore(_dir);

        await store.StoreAsync("token-key", "refresh-token-abc123");
        var result = await store.GetAsync<string>("token-key");

        Assert.Equal("refresh-token-abc123", result);
    }

    [Fact]
    public async Task DpapiFileDataStore_Store_WritesCiphertextNotPlaintextToDisk()
    {
        var store = new DpapiFileDataStore(_dir);

        await store.StoreAsync("token-key", "refresh-token-abc123");
        var rawBytes = await File.ReadAllBytesAsync(Path.Combine(_dir, "token-key"));

        Assert.DoesNotContain("refresh-token-abc123", Encoding.UTF8.GetString(rawBytes));
    }

    [Fact]
    public async Task DpapiFileDataStore_Get_MissingKey_ReturnsDefault()
    {
        var store = new DpapiFileDataStore(_dir);

        var result = await store.GetAsync<string>("never-stored");

        Assert.Null(result);
    }

    [Fact]
    public async Task DpapiFileDataStore_Get_UnencryptedLegacyFile_ReturnsDefaultInsteadOfThrowing()
    {
        var store = new DpapiFileDataStore(_dir);
        // Simulates a pre-upgrade plaintext token file written by the old FileDataStore.
        await File.WriteAllTextAsync(Path.Combine(_dir, "legacy-key"), "{\"access_token\":\"plain\"}");

        var result = await store.GetAsync<string>("legacy-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task DpapiFileDataStore_Delete_RemovesStoredValue()
    {
        var store = new DpapiFileDataStore(_dir);
        await store.StoreAsync("token-key", "refresh-token-abc123");

        await store.DeleteAsync<string>("token-key");
        var result = await store.GetAsync<string>("token-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task DpapiFileDataStore_Clear_RemovesAllStoredValues()
    {
        var store = new DpapiFileDataStore(_dir);
        await store.StoreAsync("key-a", "value-a");
        await store.StoreAsync("key-b", "value-b");

        await store.ClearAsync();

        Assert.Empty(Directory.GetFiles(_dir));
    }
}
