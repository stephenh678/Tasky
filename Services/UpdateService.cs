using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
public sealed record UpdateInfo(Version Version, string ReleaseUrl, string ReleaseNotes, string DownloadUrl, string AssetName);

/// <summary>
/// Checks GitHub Releases for a newer Tasky build, and - if the user opts in - downloads and
/// applies it in place. No installer, no code-signing cert, no Velopack/Squirrel: Tasky already
/// ships as a flat, no-install folder (see Uninstall-Tasky.ps1's $KnownAppFiles), so "updating" is
/// just replacing those same files with a fresher copy from the same release zip the manual
/// download link already offers.
///
/// Two things make this avoid a repeat SmartScreen prompt after the user's initial manual install:
/// the zip is fetched with HttpClient (not a browser/Explorer download), and the extracted files
/// are written by this already-running, already-trusted process - neither path applies Windows'
/// Mark-of-the-Web, which is what SmartScreen's Attachment Execution Service check keys off.
///
/// A running Tasky.exe can't overwrite its own file (Windows keeps an executing image locked), so
/// the actual file swap happens after the app has exited, via a small PowerShell script generated
/// fresh on disk each time (see ApplyUpdateAndRestart) - the same "script deletes its own running
/// file" trick Uninstall-Tasky.ps1 already relies on, just applied to Tasky.exe instead of to the
/// script itself.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/stephenh678/Tasky/releases/latest";
    private const string UserAgent = "Tasky-Desktop-Updater";

    private static readonly string StagingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasky", "update-staging");
    private static readonly string ExtractedDir = Path.Combine(StagingRoot, "extracted");
    private static readonly string StagedVersionFile = Path.Combine(StagingRoot, "staged-version.txt");

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

        string? downloadUrl = null;
        string? assetName = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                assetName = name;
                break;
            }
        }
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        var htmlUrl = root.TryGetProperty("html_url", out var urlProp2) ? urlProp2.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

        return new UpdateInfo(latestVersion, htmlUrl, body, downloadUrl, assetName!);
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
            if (!File.Exists(StagedVersionFile)) return null;
            if (!File.Exists(Path.Combine(ExtractedDir, "Tasky.exe"))) return null;
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

        var zipPath = Path.Combine(StagingRoot, string.IsNullOrEmpty(info.AssetName) ? "update.zip" : info.AssetName);

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

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

        ZipFile.ExtractToDirectory(zipPath, ExtractedDir, overwriteFiles: true);
        File.Delete(zipPath);

        if (!File.Exists(Path.Combine(ExtractedDir, "Tasky.exe")))
        {
            Directory.Delete(StagingRoot, recursive: true);
            throw new InvalidOperationException("The downloaded file doesn't look like a valid Tasky release.");
        }

        File.WriteAllText(StagedVersionFile, info.Version.ToString());
    }

    /// <summary>Launches a detached helper script that waits for this process to exit, copies the
    /// staged files over the install folder, relaunches Tasky.exe, then deletes the staging folder
    /// and itself. Caller is responsible for actually exiting right after (normal MainWindow.Close()
    /// - this doesn't call Shutdown() itself so the existing autosave/Drive-sync-on-close path in
    /// MainWindow's Closing handler still runs first, same as any other exit).</summary>
    public static void ApplyUpdateAndRestart()
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Path.Combine(installDir, "Tasky.exe");
        var pid = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"tasky-update-{Guid.NewGuid():N}.ps1");

        // Wait-Process's own timeout throws rather than just returning, and Copy-Item can still
        // hit the file a beat after the process object reports exited (handle teardown isn't
        // instantaneous) - both are wrapped so the retry loop is what actually decides success,
        // not a single racy attempt right after Wait-Process returns.
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id {{pid}} -Timeout 30 } catch {}

$deadline = (Get-Date).AddSeconds(20)
$applied = $false
while ((Get-Date) -lt $deadline -and -not $applied) {
    try {
        # Mirror, not just overwrite: remove any file already in the install folder that isn't
        # part of this release before copying the new ones in. Otherwise a file a past release
        # shipped but this one doesn't (e.g. the loose dependency DLLs from before Tasky switched
        # to a single-file build) silently survives every update forever instead of going away
        # once the release that stops shipping it is applied.
        $newFiles = Get-ChildItem -LiteralPath '{{ExtractedDir}}' -Recurse -File |
            ForEach-Object { $_.FullName.Substring('{{ExtractedDir}}'.Length + 1) }
        Get-ChildItem -LiteralPath '{{installDir}}' -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object {
                $rel = $_.FullName.Substring('{{installDir}}'.Length + 1)
                if ($newFiles -notcontains $rel) { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
            }
        Copy-Item -Path '{{ExtractedDir}}\*' -Destination '{{installDir}}' -Recurse -Force -ErrorAction Stop
        $applied = $true
    } catch {
        Start-Sleep -Milliseconds 500
    }
}

if ($applied) {
    Remove-Item -LiteralPath '{{StagingRoot}}' -Recurse -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath '{{exePath}}'
}
# A running .ps1 can remove its own file directly - PowerShell parses the whole script into memory
# before executing it, so (like Uninstall-Tasky.ps1) it never holds this file open.
Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
""";
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
