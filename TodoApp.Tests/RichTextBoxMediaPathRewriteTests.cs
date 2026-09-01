using System;
using System.IO;
using TodoApp.Behaviors;
using TodoApp.Services;

namespace TodoApp.Tests;

// Reproduces the actual bug report: a photo added on one computer never showed up when the note
// was opened on another, even after Google Drive sync downloaded the real bytes - because the
// note's saved XAML bakes in whichever machine's absolute local path (username, drive layout, all
// of it) was current when the image/file was inserted there, and nothing ever rewrote it. WPF's
// BitmapImage eagerly opens that path the instant the XAML is parsed, so a path from another
// machine doesn't just fail to show one image - it throws and drops the block's entire content.
public class RichTextBoxMediaPathRewriteTests
{
    // RewriteMediaPathsForThisDevice resolves through MediaPathResolver's process-wide "currently
    // open file" state - point it at an isolated temp file per test and restore the default
    // afterward, since this suite can run against a real Tasky install with real user data.
    private static void WithTempDataFile(Action test)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tasky_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            MediaPathResolver.SetDataFilePath(Path.Combine(tempDir, "Tasky.tasky"));
            test();
        }
        finally
        {
            MediaPathResolver.SetDataFilePath(TodoStore.GetDefaultDataFilePath());
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void InlineImageFromAnotherMachine_IsRewrittenToThisDevicesFolder()
    {
        WithTempDataFile(() =>
        {
            var xaml = "<BitmapImage UriSource=\"C:\\Users\\stephart\\Documents\\Tasky\\InlineImages\\" +
                       "a3e9cf8a-f164-4ee3-bf53-3786f151e866.png\" />";

            var rewritten = RichTextBoxBehavior.RewriteMediaPathsForThisDevice(xaml);

            var expected = Path.Combine(MediaPathResolver.InlineImagesDirectory, "a3e9cf8a-f164-4ee3-bf53-3786f151e866.png");
            Assert.Equal($"<BitmapImage UriSource=\"{expected}\" />", rewritten);
        });
    }

    [Fact]
    public void FileChipTagFromAnotherMachine_IsRewrittenToThisDevicesFolder()
    {
        WithTempDataFile(() =>
        {
            var xaml = "<Border Tag=\"C:\\Users\\otheruser\\Documents\\Tasky\\Attachments\\report.xlsx\" />";

            var rewritten = RichTextBoxBehavior.RewriteMediaPathsForThisDevice(xaml);

            var expected = Path.Combine(MediaPathResolver.AttachmentsDirectory, "report.xlsx");
            Assert.Equal($"<Border Tag=\"{expected}\" />", rewritten);
        });
    }

    [Fact]
    public void NonPathTagMarkers_AreLeftAlone()
    {
        WithTempDataFile(() =>
        {
            var xaml = "<Grid Tag=\"ImageContainer\"><Border Tag=\"CardBody\" /></Grid>";

            var rewritten = RichTextBoxBehavior.RewriteMediaPathsForThisDevice(xaml);

            Assert.Equal(xaml, rewritten);
        });
    }

    [Fact]
    public void PathAlreadyOnThisDevice_IsLeftUnchanged()
    {
        WithTempDataFile(() =>
        {
            var localPath = Path.Combine(MediaPathResolver.InlineImagesDirectory, "same-machine.png");
            var xaml = $"<BitmapImage UriSource=\"{localPath}\" />";

            var rewritten = RichTextBoxBehavior.RewriteMediaPathsForThisDevice(xaml);

            Assert.Equal(xaml, rewritten);
        });
    }

    [Fact]
    public void MultipleImagesInOneDocument_AreAllRewritten()
    {
        WithTempDataFile(() =>
        {
            var xaml =
                "<FlowDocument>" +
                "<BitmapImage UriSource=\"C:\\Users\\stephart\\Documents\\Tasky\\InlineImages\\one.png\" />" +
                "<BitmapImage UriSource=\"C:\\Users\\stephart\\Documents\\Tasky\\InlineImages\\two.png\" />" +
                "</FlowDocument>";

            var rewritten = RichTextBoxBehavior.RewriteMediaPathsForThisDevice(xaml);

            var dir = MediaPathResolver.InlineImagesDirectory;
            Assert.Contains($"UriSource=\"{Path.Combine(dir, "one.png")}\"", rewritten);
            Assert.Contains($"UriSource=\"{Path.Combine(dir, "two.png")}\"", rewritten);
        });
    }
}
