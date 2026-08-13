using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoApp.Models;

public class TaskItem : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isDone;
    private bool _isClosed;
    private bool _isPinned;
    private DateTime? _dueDate;
    private string _notes = string.Empty;
    private DateTime _modifiedAt = DateTime.Now;
    private RecurrenceRule _recurrence = RecurrenceRule.None;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime ModifiedAt
    {
        get => _modifiedAt;
        set => SetField(ref _modifiedAt, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetField(ref _isPinned, value);
    }

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => SetField(ref _isDone, value);
    }

    public bool IsClosed
    {
        get => _isClosed;
        set => SetField(ref _isClosed, value);
    }

    public DateTime? DueDate
    {
        get => _dueDate;
        set => SetField(ref _dueDate, value);
    }

    public RecurrenceRule Recurrence
    {
        get => _recurrence;
        set => SetField(ref _recurrence, value);
    }

    // Legacy fields, kept only so old saved data can be migrated into Body once on load.
    public string Notes
    {
        get => _notes;
        set => SetField(ref _notes, value);
    }

    public ObservableCollection<TaskLink> Links { get; set; } = new();
    public ObservableCollection<string> Photos { get; set; } = new();

    // The unified note body: an ordered stream of text/photo/link blocks.
    public ObservableCollection<NoteBlock> Body { get; set; } = new();

    public ObservableCollection<string> Tags { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
