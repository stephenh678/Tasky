using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoApp.Models;

public enum NoteBlockType
{
    Text,
    Photo,
    Link,
    File,
    Checklist
}

public class NoteBlock : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private string _url = string.Empty;
    private string _linkLabel = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public NoteBlockType Type { get; set; }

    // Text blocks: Text is the plain-text mirror (used for search/word count),
    // Rtf holds the formatted content shown in the editor.
    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public string Rtf { get; set; } = string.Empty;

    // Photo and File blocks: absolute path to the copy in the app's attachments folder.
    public string PhotoPath { get; set; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(PhotoPath);

    // Link blocks
    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public string LinkLabel
    {
        get => _linkLabel;
        set => SetField(ref _linkLabel, value);
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
