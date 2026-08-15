using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoApp.Models;

/// <summary>
/// Represents a single block within a task's body (text, photo, link, file, or checklist).
/// </summary>
public enum NoteBlockType
{
    Text,
    Photo,
    Link,
    File,
    Checklist
}

/// <summary>
/// A single content block in a task's rich-text body.
/// </summary>
public class NoteBlock : INotifyPropertyChanged
{
    private const int MaxTextLength = 10000;
    private const int MaxLinkLabelLength = 500;
    
    private string _text = string.Empty;
    private string _url = string.Empty;
    private string _linkLabel = string.Empty;
    private string _rtf = string.Empty;
    private string _photoPath = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public NoteBlockType Type { get; set; }

    // Text blocks: Text is the plain-text mirror (used for search/word count),
    // Rtf holds the formatted content shown in the editor.
    /// <summary>
    /// Plain text content of this block. Limited to 10000 characters.
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            var input = value ?? string.Empty;
            var truncated = input.Length > MaxTextLength ? input.Substring(0, MaxTextLength) : input;
            SetField(ref _text, truncated);
        }
    }

    /// <summary>
    /// Rich text format (XAML+Base64) of this block. Raises PropertyChanged to trigger saves.
    /// </summary>
    public string Rtf
    {
        get => _rtf;
        set => SetField(ref _rtf, value);
    }

    // Photo and File blocks: absolute path to the copy in the app's attachments folder.
    public string PhotoPath
    {
        get => _photoPath;
        set => SetField(ref _photoPath, value ?? string.Empty);
    }

    public string FileName => System.IO.Path.GetFileName(PhotoPath);

    // Link blocks
    /// <summary>
    /// URL of the hyperlink. Validated as a well-formed URI.
    /// </summary>
    public string Url
    {
        get => _url;
        set
        {
            // Validate URL format
            var trimmed = (value ?? string.Empty).Trim();
            var isValid = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            SetField(ref _url, isValid ? trimmed : string.Empty);
        }
    }

    /// <summary>
    /// Display label for the link. Limited to 500 characters.
    /// </summary>
    public string LinkLabel
    {
        get => _linkLabel;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            var truncated = trimmed.Length > MaxLinkLabelLength ? trimmed.Substring(0, MaxLinkLabelLength) : trimmed;
            SetField(ref _linkLabel, truncated);
        }
    }

    // Checklist blocks: an ordered list of check-off items.
    public ObservableCollection<ChecklistItem> ChecklistItems { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
