using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TodoApp.Services;

/// <summary>A release found on GitHub, or a previously-staged one being resumed (see
/// <see cref="UpdateService.GetPendingStagedUpdate"/>) - <see cref="ReleaseUrl"/> and
/// <see cref="ReleaseNotes"/> are empty in the resumed case since nothing persists them
/// across a restart, and the caller already showed them once in the session that staged it.</summary>
/// <see cref="Sha256"/> is the installer's expected SHA-256 (lowercase hex) as published by GitHub
/// for the release asset - see <see cref="UpdateService.StageUpdateAsync"/>.
public sealed record UpdateInfo(Version Version, string ReleaseUrl, string ReleaseNotes, string DownloadUrl, string AssetName, string? Sha256 = null);

/// <summary>
/// Checks GitHub Releases for a newer Tasky build and, if the user opts in, downloads and runs
/// that release's installer.
///
/// Updating IS the installer, run silently - Tasky-Setup-x.y.z.exe with /SILENT. That is what
/// makes an update indistinguishable from a fresh install: the same Inno Setup package replaces
/// the files, refreshes the shortcuts and updates the version shown in Apps &amp; Features. The
/// previous approach downloaded the raw zip and hand-rolled the file swap in a throwaway
/// PowerShell script generated at runtime, which did none of that - an updated install kept
/// advertising the old version to Windows forever.
///
/// Two things keep this from tripping SmartScreen on every update: the installer is fetched with
/// HttpClient rather than a browser, and it is launched by this already-running, already-trusted
/// process. Neither path applies Mark-of-the-Web, which is what SmartScreen's Attachment Execution
/// Service check keys off.
///
/// A running Tasky.exe can't be overwritten, so the exchange is: this process starts the installer
/// and exits; Inno closes/replaces/relaunches from there (see /LAUNCHAFTER in
/// <see cref="ApplyUpdateAndRestart"/> and installer/Tasky.iss).
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/stephenh678/Tasky/releases/latest";
    private const string UserAgent = "Tasky-Desktop-Updater";

    private static readonly string StagingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasky", "update-staging");
    private static readonly string StagedInstallerFile = Path.Combine(StagingRoot, "staged-installer.txt");
    private static readonly string StagedVersionFile = Path.Combine(StagingRoot, "staged-version.txt");
    private static readonly string StagedHashFile = Path.Combine(StagingRoot, "staged-sha256.txt");

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Null means "no newer release" (or the API response didn't look like a real
    /// release) - callers don't need to separately re-check the version themselves.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        using var response = await http.GetAsync(LatestReleaseApiUrl, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
        if (!Version.TryParse(tagName.TrimStart('v', 'V'), out var latestVersion)) return null;
        if (latestVersion <= CurrentVersion) return null;

        // The installer asset specifically. A release also carries a "*-win-x64.zip" for copies of
        // Tasky predating the installer (they look for that name and nothing else), but this build
        // must never pick it up: the zip has no way to update the Apps & Features registration.
        // No installer in the release means no update, which is the safe answer rather than
        // falling back to something that would half-apply.
        string? downloadUrl = null;
        string? assetName = null;
        string? sha256 = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                if (!name.StartsWith("Tasky-Setup-", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                assetName = name;
                sha256 = ParseSha256Digest(asset.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() : null);
                break;
            }
        }
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        var htmlUrl = root.TryGetProperty("html_url", out var urlProp2) ? urlProp2.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

        return new UpdateInfo(latestVersion, htmlUrl, body, downloadUrl, assetName!, sha256);
    }

    // GitHub reports each release asset's digest as "sha256:<hex>". Anything else (absent, another
    // algorithm, not 64 hex chars) is treated as "no digest" rather than trusted.
    internal static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    internal static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// True when this copy of Tasky is running from somewhere the installer didn't put it - no
    /// Apps &amp; Features registration, or one pointing at a different folder.
    ///
    /// The case that matters is the migration: a copy predating the installer updates itself from
    /// the legacy zip, which drops the new binary over the old folder (Desktop, Downloads,
    /// wherever it was unpacked) and cannot register anything. That copy works, but Windows knows
    /// nothing about it - no Start Menu entry, nothing in Settings &gt; Apps - and its next update
    /// would install a SECOND copy under %LOCALAPPDATA%\Programs, orphaning this one. Telling the
    /// user once is what turns a stranded install into a managed one.
    /// </summary>
    public static bool IsRunningUnmanagedInstall()
    {
        try
        {
            var current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            // Inno registers a per-user install under HKCU, keyed on the AppId in
            // installer/Tasky.iss with Inno's own "_is1" suffix.
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F1C5A2E-4B3D-4C7A-9E6F-2A8D0B4E7C15}_is1");
            var registered = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(registered)) return true;
            return !string.Equals(
                registered.TrimEnd(Path.DirectorySeparatorChar), current, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Can't tell - say nothing rather than nag on a guess.
            return false;
        }
    }

    /// <summary>
    /// Deletes the staging folder when what's in it is no longer newer than what's running - i.e.
    /// after an update was applied, or after this version was installed some other way. The
    /// staged installer is ~50MB, and nothing else ever removes it: ApplyUpdateAndRestart can't
    /// (the installer it just launched is about to close this process, and the file is in use),
    /// and StageUpdateAsync only clears the folder when a NEXT update is downloaded. Without this
    /// a user who updates once and never again keeps that 50MB forever.
    /// Safe to call at startup on a background thread; failures are deliberately ignored.
    /// </summary>
    public static void CleanUpStaleStaging()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            // Still-pending staged update: leave it, the user may click Restart to apply it.
            if (GetPendingStagedUpdate() is not null) return;
            Directory.Delete(StagingRoot, recursive: true);
        }
        catch (Exception)
        {
            // A locked file (the installer may still be finishing) or a permissions problem -
            // there'll be another startup.
        }
    }

    /// <summary>A staged download left behind by a previous session's "Later" click - lets the
    /// caller offer to finish installing without hitting the network or re-downloading ~75MB.
    /// Returns null if nothing valid is staged (including a stale stage for a version that's no
    /// longer newer than what's actually running, e.g. if this same version got installed some
    /// other way in the meantime).</summary>
    public static UpdateInfo? GetPendingStagedUpdate()
    {
        try
        {
            // StagedHashFile too: a stage left by a build that predates checksum verification has
            // none, and ApplyUpdateAndRestart would refuse it - better to offer a fresh download.
            if (!File.Exists(StagedVersionFile) || !File.Exists(StagedInstallerFile) || !File.Exists(StagedHashFile)) return null;
            var installerPath = File.ReadAllText(StagedInstallerFile).Trim();
            if (!File.Exists(installerPath)) return null;
            if (!Version.TryParse(File.ReadAllText(StagedVersionFile).Trim(), out var version)) return null;
            if (version <= CurrentVersion) return null;
            return new UpdateInfo(version, ReleaseUrl: "", ReleaseNotes: "", DownloadUrl: "", AssetName: "");
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Downloads and extracts the release zip into a staging folder alongside the app's
    /// own data (not the install folder itself - nothing here touches the running app's files
    /// until <see cref="ApplyUpdateAndRestart"/> runs after Tasky has actually exited).</summary>
    public static async Task StageUpdateAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, recursive: true);
        Directory.CreateDirectory(StagingRoot);

        var installerPath = Path.Combine(StagingRoot, string.IsNullOrEmpty(info.AssetName) ? "Tasky-Setup.exe" : info.AssetName);

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }

        // Nothing to unpack - the installer IS the payload. Sanity-check it looks like a real PE
        // rather than an HTML error page GitHub served with a 200, which would otherwise only show
        // up as a baffling failure when the user clicks Restart.
        if (new FileInfo(installerPath).Length < 1_000_000)
        {
            Directory.Delete(StagingRoot, recursive: true);
            throw new InvalidOperationException("The downloaded file doesn't look like a valid Tasky installer.");
        }

        // This file is about to be EXECUTED, silently, with the user's full rights - "it came
        // over HTTPS and it's bigger than a megabyte" was the entire check. Compare it against the
        // SHA-256 GitHub publishes for the asset, which catches a truncated or corrupted download
        // and anything swapped in between GitHub's API and the download CDN. Fails closed: a
        // release with no digest doesn't install itself - the release page is still one click away.
        if (string.IsNullOrEmpty(info.Sha256))
        {
            Directory.Delete(StagingRoot, recursive: true);
            throw new InvalidOperationException(
                "This release doesn't publish a checksum, so the download couldn't be verified. Download the installer from the release page instead.");
        }
        var actualSha256 = await Task.Run(() => ComputeSha256(installerPath), ct);
        if (!string.Equals(actualSha256, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(StagingRoot, recursive: true);
            AppLogger.Error("UpdateService", $"Installer checksum mismatch: expected {info.Sha256}, got {actualSha256}");
            throw new InvalidOperationException("The downloaded installer didn't match its published checksum and was discarded. Please try again.");
        }

        File.WriteAllText(StagedHashFile, actualSha256);
        File.WriteAllText(StagedInstallerFile, installerPath);
        File.WriteAllText(StagedVersionFile, info.Version.ToString());
    }

    /// <summary>Runs the staged installer silently and returns. Inno Setup closes this process,
    /// replaces the files, refreshes the shortcuts and the Apps &amp; Features entry, then relaunches
    /// Tasky (see /LAUNCHAFTER in installer/Tasky.iss). The caller should exit right afterwards via
    /// the normal MainWindow.Close() path so the existing autosave and Drive-sync-on-close work
    /// still runs first - this deliberately doesn't call Shutdown() itself.</summary>
    public static void ApplyUpdateAndRestart()
    {
        if (!File.Exists(StagedInstallerFile))
            throw new InvalidOperationException("No staged update to apply.");

        var installerPath = File.ReadAllText(StagedInstallerFile).Trim();
        if (!File.Exists(installerPath))
            throw new InvalidOperationException("The staged installer is missing - download the update again.");

        // A staged installer can sit in %LocalAppData% for days after a "Later" click. Re-check it
        // against the hash recorded when it was verified, and make sure the path read back from
        // the pointer file is still inside the staging folder, before running anything.
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var stagingPrefix = Path.GetFullPath(StagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullInstallerPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(StagedHashFile)
            || !string.Equals(File.ReadAllText(StagedHashFile).Trim(), ComputeSha256(fullInstallerPath), StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Delete(StagingRoot, recursive: true); } catch (IOException) { }
            throw new InvalidOperationException("The staged installer changed since it was downloaded and was discarded - download the update again.");
        }
        installerPath = fullInstallerPath;

        // /SILENT shows only a progress bar (no wizard) - the user already agreed in Tasky's own
        // dialog, so re-asking would be redundant, but /VERYSILENT would leave a multi-second
        // update looking like nothing happened.
        // /CLOSEAPPLICATIONS lets Restart Manager close this instance rather than failing on a
        // locked Tasky.exe, and /LAUNCHAFTER=1 is Tasky.iss's own flag telling it to start Tasky
        // again once done (the normal post-install launch entry is skipped in silent mode).
        // The staging folder is deliberately NOT cleaned up here: this process is about to be
        // closed by the installer, so anything queued after this line may never run.
        // GetPendingStagedUpdate's version check retires it instead, and the next StageUpdateAsync
        // clears the folder outright.
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            // No /RESTARTAPPLICATIONS: that asks Restart Manager to restart what it closed, which
            // together with /LAUNCHAFTER=1 is two independent relaunch paths for one update.
            // SingleInstanceGuard would now stop the second copy, but one relaunch path is still
            // the right number. Tasky.iss sets RestartApplications=no for the same reason.
            Arguments = "/SILENT /CLOSEAPPLICATIONS /LAUNCHAFTER=1",
            UseShellExecute = true,
        });
    }
}
