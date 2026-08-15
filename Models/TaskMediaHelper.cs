using System;
using System.Collections.Generic;
using System.Linq;

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
}
