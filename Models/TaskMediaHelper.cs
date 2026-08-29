using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TodoApp.Models;

/// <summary>
/// Helper for inspecting task content and detecting embedded media (photos, attachments, links, checklists).
/// Works across both the unified inline document canvas (XAML/RTF) and legacy block formats.
/// </summary>
public static class TaskMediaHelper
{
    public static bool HasPhoto(TaskItem? task)
    {
        if (task is null) return false;
        if (task.Photos.Count > 0) return true;

        foreach (var block in task.Body)
        {
            if (block.Type == NoteBlockType.Photo || !string.IsNullOrWhiteSpace(block.PhotoPath))
                return true;

            if (!string.IsNullOrWhiteSpace(block.Rtf))
            {
                if (block.Rtf.Contains("<Image", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains("ImageContainer", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains("DeleteImageBtn", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains(".jpeg", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static bool HasAttachment(TaskItem? task)
    {
        if (task is null) return false;

        foreach (var block in task.Body)
        {
            if (block.Type == NoteBlockType.File)
                return true;

            if (!string.IsNullOrWhiteSpace(block.Rtf))
            {
                if (block.Rtf.Contains("DeleteAttachmentBtn", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains("CardBody", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains("Double-click to open", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static bool HasLink(TaskItem? task)
    {
        if (task is null) return false;
        if (task.Links.Count > 0) return true;

        foreach (var block in task.Body)
        {
            if (block.Type == NoteBlockType.Link || !string.IsNullOrWhiteSpace(block.Url))
                return true;

            if (!string.IsNullOrWhiteSpace(block.Rtf))
            {
                if (block.Rtf.Contains("<Hyperlink", StringComparison.OrdinalIgnoreCase) ||
                    block.Rtf.Contains("NavigateUri", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (!string.IsNullOrWhiteSpace(block.Text))
            {
                if (block.Text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                    block.Text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                    block.Text.Contains("www.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static bool HasChecklist(TaskItem? task)
    {
        if (task is null) return false;

        foreach (var block in task.Body)
        {
            if (block.Type == NoteBlockType.Checklist || block.ChecklistItems.Count > 0)
                return true;

            if (!string.IsNullOrWhiteSpace(block.Rtf))
            {
                if (block.Rtf.Contains("<CheckBox", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // The saved Rtf is XamlWriter output of the live FlowDocument, so an inline pasted image or
    // inserted file chip shows up as a literal UriSource="..." (BitmapImage) or Tag="..."
    // (the file-card's FrameworkElement.Tag) attribute - see RichTextBoxBehavior.CreateFileCard
    // and the various SaveBitmapToInlineAttachment call sites. A Grid's Tag is also used for a
    // handful of internal markers rather than a file path; those aren't real attachment refs.
    private static readonly Regex UriSourceRegex = new("UriSource=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex FileTagRegex = new("<Grid[^>]*\\sTag=\"([^\"]+)\"", RegexOptions.Compiled);

    private static readonly HashSet<string> NonFileTagMarkers = new(StringComparer.Ordinal)
    {
        "ImageContainer", "CardBody", "DeleteAttachmentBtn"
    };

    /// <summary>
    /// Every attachment/image filename a block actually references: its own PhotoPath (Photo/File
    /// blocks store their local path there - see NoteBlock.FileName), plus anything pasted or
    /// inserted inline into a Text block's Rtf. Extracts the full quoted UriSource/Tag attribute
    /// value rather than scanning for filename-shaped substrings, so names containing spaces or
    /// parentheses - e.g. RichTextBoxBehavior.CopyFileToAttachments' own "report (1).pdf"
    /// collision-avoidance naming - are still recognized as referenced instead of silently
    /// invisible to whatever's deciding what's safe to delete.
    /// </summary>
    public static void CollectReferencedFileNames(string? photoPath, string? rtf, HashSet<string> set)
    {
        AddFileName(set, photoPath);

        if (string.IsNullOrEmpty(rtf)) return;

        foreach (Match m in UriSourceRegex.Matches(rtf))
            AddFileName(set, m.Groups[1].Value);

        foreach (Match m in FileTagRegex.Matches(rtf))
        {
            if (NonFileTagMarkers.Contains(m.Groups[1].Value)) continue;
            AddFileName(set, m.Groups[1].Value);
        }
    }

    private static void AddFileName(HashSet<string> set, string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return;
        var name = Path.GetFileName(pathOrName);
        if (!string.IsNullOrEmpty(name)) set.Add(name);
    }
}
