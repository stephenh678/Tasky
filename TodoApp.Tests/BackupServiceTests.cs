using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using TodoApp.Models;
using TodoApp.Services;
using Xunit;

namespace TodoApp.Tests;

public class BackupServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataFile;
    private readonly string _zipFile;

    public BackupServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "TaskyBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dataFile = Path.Combine(_testDir, "Tasky.tasky");
        _zipFile = Path.Combine(_testDir, "backup.zip");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Export_CreatesZipWithDataFileAndAttachments()
    {
        File.WriteAllText(_dataFile, "{\"Tasks\":[]}");

        var attachmentsDir = MediaPathResolver.DirectoryFor(_dataFile, "Attachments");
        Directory.CreateDirectory(attachmentsDir);
        var attachmentFile = Path.Combine(attachmentsDir, "photo1.png");
        File.WriteAllText(attachmentFile, "dummy photo content");

        var tasks = new List<TaskItem>
        {
            new()
            {
                Text = "Task with attachment",
                Body = { new NoteBlock { Type = NoteBlockType.Photo, PhotoPath = "photo1.png" } }
            }
        };

        var (included, missing) = BackupService.Export(_dataFile, tasks, _zipFile);

        Assert.Equal(1, included);
        Assert.Equal(0, missing);
        Assert.True(File.Exists(_zipFile));

        using var zip = ZipFile.OpenRead(_zipFile);
        Assert.NotNull(zip.GetEntry("Tasky.tasky"));
        Assert.NotNull(zip.GetEntry("Attachments/photo1.png"));
    }

    [Fact]
    public void ExtractToTemp_WhenDisposed_DeletesTemporaryExtractionDirectory()
    {
        File.WriteAllText(_dataFile, "{\"Tasks\":[]}");
        var (included, missing) = BackupService.Export(_dataFile, new List<TaskItem>(), _zipFile);
        Assert.True(File.Exists(_zipFile));

        string tempDirPath;
        using (var package = BackupService.ExtractToTemp(_zipFile))
        {
            tempDirPath = package.TempDirectory;
            Assert.True(Directory.Exists(tempDirPath));
            Assert.True(File.Exists(package.DataFilePath));
            Assert.Equal("Tasky.tasky", Path.GetFileName(package.DataFilePath));
        }

        // After disposing the package, temp folder must no longer exist on disk
        Assert.False(Directory.Exists(tempDirPath), "ExtractedBackupPackage temp directory was not cleaned up on Dispose.");
    }

    [Fact]
    public void ExtractToTemp_WithZipMissingTaskyFile_CleansUpTempDirAndThrows()
    {
        var invalidZip = Path.Combine(_testDir, "invalid.zip");
        using (var zip = ZipFile.Open(invalidZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("some_other_file.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello");
        }

        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            BackupService.ExtractToTemp(invalidZip);
        });
        Assert.Contains("doesn't contain a .tasky data file", ex.Message);

        // Ensure no TaskyImport_* directories remain leaked in %TEMP%
        var leakedDirs = Directory.GetDirectories(Path.GetTempPath(), "TaskyImport_*");
        var recentLeaked = leakedDirs.Where(d => Directory.GetCreationTime(d) > DateTime.Now.AddSeconds(-2)).ToList();
        Assert.Empty(recentLeaked);
    }

    [Fact]
    public void RestoreAttachments_CopiesFilesIntoMediaPathResolverAttachmentsDirectory()
    {
        var sourceDir = Path.Combine(_testDir, "source_attachments");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "test-attachment.txt");
        File.WriteAllText(sourceFile, "attachment bytes");

        BackupService.RestoreAttachments(new[] { sourceFile }, targetDataFilePath: _dataFile);

        var expectedDest = Path.Combine(MediaPathResolver.DirectoryFor(_dataFile, "Attachments"), "test-attachment.txt");
        Assert.True(File.Exists(expectedDest));
        Assert.Equal("attachment bytes", File.ReadAllText(expectedDest));
    }
}
