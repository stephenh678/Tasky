using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TodoApp.Services;

namespace TodoApp.Tests;

// GoogleDriveService itself talks directly to Google.Apis.Drive.v3.DriveService (a concrete SDK
// type with no fake-able seam), so these tests cover the parts of it that are already pure and
// network-free: attachment-filename extraction from task JSON and the 3-way-diff "was this file
// part of the last sync" bookkeeping. Both have caused real sync bugs before (see ROADMAP.md /
// project history - attachment sync silently not syncing anything against live data was a real
// incident), so this is genuine coverage of the bug-prone logic, not just padding.
public class GoogleDriveServiceTests : IDisposable
{
    private readonly string _dir;

    public GoogleDriveServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TaskyDriveServiceTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteDataFile(string json)
    {
        var path = Path.Combine(_dir, "Tasky.tasky");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void IsAuthenticated_FreshInstance_IsFalse()
    {
        var service = new GoogleDriveService();

        Assert.False(service.IsAuthenticated);
    }

    // ROADMAP.md #58: a filename/folder name containing a single quote or backslash (both valid
    // in a Windows filename via Save As) previously broke the Drive `q` query built in
    // GetOrCreateFolderAsync/FindExistingFileIdAsync. See https://developers.google.com/drive/api/guides/ref-search-terms
    // for the escaping rule this mirrors.
    [Theory]
    [InlineData("Steve's Tasks", "Steve\\'s Tasks")]
    [InlineData("Report (v2).tasky", "Report (v2).tasky")] // no special characters - unchanged
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("both ' and \\", "both \\' and \\\\")]
    public void EscapeDriveQueryValue_EscapesQuotesAndBackslashes(string input, string expected)
    {
        Assert.Equal(expected, GoogleDriveService.EscapeDriveQueryValue(input));
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_AppStateWrappedFormat_FindsPhotoAndFilePaths()
    {
        // Photo AND File blocks both store their local path in PhotoPath - see NoteBlock.FileName,
        // which derives from it. There is no separate "FilePath" property in the real schema.
        var path = WriteDataFile("""
            {"Tasks":[{"Id":"1","Body":[
                {"Type":"Photo","PhotoPath":"C:\\Users\\me\\Tasky\\Attachments\\photo1.png"},
                {"Type":"File","PhotoPath":"C:\\Users\\me\\Tasky\\Attachments\\report.pdf"}
            ]}]}
            """);

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.Contains("photo1.png", found);
        Assert.Contains("report.pdf", found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_RawArrayFallbackFormat_StillFindsReferences()
    {
        // Pre-AppState-wrapper format: a bare array of tasks instead of {"Tasks":[...]}.
        var path = WriteDataFile("""
            [{"Id":"1","Body":[{"Type":"Photo","PhotoPath":"photo1.png"}]}]
            """);

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.Contains("photo1.png", found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_LegacyNoteBlocksField_AlsoScanned()
    {
        var path = WriteDataFile("""
            {"Tasks":[{"Id":"1","NoteBlocks":[{"Type":"Photo","PhotoPath":"legacy.jpg"}]}]}
            """);

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.Contains("legacy.jpg", found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_RtfEmbeddedImage_FoundViaUriSource()
    {
        // The saved Rtf is XamlWriter output, so an inline pasted image shows up as a literal
        // UriSource="..." attribute - not as free-form text that merely mentions a filename.
        var path = WriteDataFile("""
            {"Tasks":[{"Id":"1","Body":[{"Type":"Text","Rtf":"<Image><BitmapImage UriSource=\"C:\\Users\\me\\Tasky\\InlineImages\\inline_photo123.png\"/></Image>"}]}]}
            """);

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.Contains("inline_photo123.png", found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_RtfMentionsFilenameAsPlainText_NotTreatedAsReference()
    {
        // A filename merely mentioned in prose isn't an actual local attachment reference - only
        // structural UriSource/Tag/PhotoPath fields are. Guards against reintroducing the old
        // blind filename-pattern regex.
        var path = WriteDataFile("""
            {"Tasks":[{"Id":"1","Body":[{"Type":"Text","Rtf":"See attached inline_photo123.png for details"}]}]}
            """);

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.DoesNotContain("inline_photo123.png", found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_FilenameWithSpacesAndParens_StillFound()
    {
        // RichTextBoxBehavior.CopyFileToAttachments generates collision-avoidance names like
        // "report (1).pdf" - the old character-class-restricted regex couldn't match these at all.
        var path = WriteDataFile("""
            {"Tasks":[{"Id":"1","Body":[{"Type":"File","PhotoPath":"C:\\Users\\me\\Tasky\\Attachments\\report (1).pdf"}]}]}
            """);

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.Contains("report (1).pdf", found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_MissingFile_ReturnsEmptySetInsteadOfThrowing()
    {
        var found = GoogleDriveService.GetReferencedAttachmentFilenames(Path.Combine(_dir, "does-not-exist.tasky"));

        Assert.Empty(found);
    }

    [Fact]
    public void GetReferencedAttachmentFilenames_MalformedJson_ReturnsEmptySetInsteadOfThrowing()
    {
        var path = WriteDataFile("{ this is not valid json ");

        var found = GoogleDriveService.GetReferencedAttachmentFilenames(path);

        Assert.Empty(found);
    }

    // ROADMAP #64: GetReferencedAttachmentFilenamesCached wraps the (expensive) static
    // GetReferencedAttachmentFilenames scan with an mtime-keyed cache, since SyncMediaDirectoryAsync
    // used to call the uncached version twice per sync and again on every later sync pass with no
    // change in between.
    [Fact]
    public void GetReferencedAttachmentFilenamesCached_UnchangedFile_ReturnsSameCachedInstance()
    {
        var path = WriteDataFile("""{"Tasks":[{"Id":"1","Body":[{"Type":"Photo","PhotoPath":"C:\\p\\a.png"}]}]}""");
        var service = new GoogleDriveService();

        var first = service.GetReferencedAttachmentFilenamesCached(path);
        var second = service.GetReferencedAttachmentFilenamesCached(path);

        Assert.Same(first, second);
        Assert.Contains("a.png", second);
    }

    [Fact]
    public void GetReferencedAttachmentFilenamesCached_FileModifiedSinceLastScan_RescansAndPicksUpChange()
    {
        var path = WriteDataFile("""{"Tasks":[{"Id":"1","Body":[{"Type":"Photo","PhotoPath":"C:\\p\\a.png"}]}]}""");
        var service = new GoogleDriveService();
        var first = service.GetReferencedAttachmentFilenamesCached(path);
        Assert.Contains("a.png", first);

        // Force a distinct, later LastWriteTimeUtc - some filesystems' write-time resolution is too
        // coarse for a same-tick rewrite to reliably bump the timestamp on its own.
        File.WriteAllText(path, """{"Tasks":[{"Id":"1","Body":[{"Type":"Photo","PhotoPath":"C:\\p\\b.png"}]}]}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var second = service.GetReferencedAttachmentFilenamesCached(path);

        Assert.DoesNotContain("a.png", second);
        Assert.Contains("b.png", second);
    }

    [Fact]
    public void ParseBlockReferences_PhotoPath_AddsFileNameOnlyNotFullPath()
    {
        var block = JsonDocument.Parse("""{"PhotoPath":"C:\\Users\\me\\Tasky\\Attachments\\photo1.png"}""").RootElement;
        var set = new HashSet<string>();

        GoogleDriveService.ParseBlockReferences(block, set);

        Assert.Equal(new[] { "photo1.png" }, set);
    }

    [Fact]
    public void ParseBlockReferences_InlineFileTag_AddsFileNameOnlyNotFullPath()
    {
        // A file-card chip pasted inline into a Text block's Rtf carries its path in the card
        // Grid's Tag attribute rather than a top-level PhotoPath field.
        var block = JsonDocument.Parse("""
            {"Rtf":"<Grid Tag=\"/home/me/Tasky/Attachments/report.pdf\">card</Grid>"}
            """).RootElement;
        var set = new HashSet<string>();

        GoogleDriveService.ParseBlockReferences(block, set);

        Assert.Equal(new[] { "report.pdf" }, set);
    }

    [Fact]
    public void ParseBlockReferences_NoRecognizedFields_LeavesSetEmpty()
    {
        var block = JsonDocument.Parse("""{"Type":"Text","Rtf":"just plain text, no attachments"}""").RootElement;
        var set = new HashSet<string>();

        GoogleDriveService.ParseBlockReferences(block, set);

        Assert.Empty(set);
    }

    [Fact]
    public void ResolveLastSyncedMediaSet_NullSettings_ReturnsEmptySet()
    {
        var result = GoogleDriveService.ResolveLastSyncedMediaSet("tasky.tasky", null);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveLastSyncedMediaSet_CachedPerFileEntry_ReturnsIt()
    {
        var settings = new Settings();
        settings.LastSyncedMediaFilesByFile["work.tasky"] = new List<string> { "photo1.png", "report.pdf" };

        var result = GoogleDriveService.ResolveLastSyncedMediaSet("work.tasky", settings);

        Assert.Equal(new HashSet<string>(new[] { "photo1.png", "report.pdf" }, StringComparer.OrdinalIgnoreCase), result);
    }

    [Fact]
    public void ResolveLastSyncedMediaSet_LegacyOwnerKey_FallsBackToSharedLegacyList()
    {
        var settings = new Settings
        {
            GoogleDriveLegacyAttachmentsFileKey = "tasky.tasky",
            LastSyncedMediaFiles = new List<string> { "old-photo.png" },
        };

        var result = GoogleDriveService.ResolveLastSyncedMediaSet("tasky.tasky", settings);

        Assert.Contains("old-photo.png", result);
    }

    [Fact]
    public void ResolveLastSyncedMediaSet_UnknownKeyAndNotLegacyOwner_ReturnsEmptySet()
    {
        var settings = new Settings
        {
            GoogleDriveLegacyAttachmentsFileKey = "other-file.tasky",
            LastSyncedMediaFiles = new List<string> { "old-photo.png" },
        };

        var result = GoogleDriveService.ResolveLastSyncedMediaSet("work.tasky", settings);

        Assert.Empty(result);
    }
}
