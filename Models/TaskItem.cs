using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TodoApp.Models;

/// <summary>
/// Represents a single task item in the application.
/// </summary>
public class TaskItem : INotifyPropertyChanged
{
    // ROADMAP.md #132: was 500 with no feedback on truncation - a title anywhere near that length
    // was rare but not impossible, and a sync merge landing an over-limit remote value could
    // silently reshape it. Raised well past any real title's length instead of adding a UI
    // validation message or a log line for a cap nothing should ever actually hit; keep in sync
    // with docs/js/model.js's MAX_TASK_TEXT (both sides must agree, or a merge could still
    // silently reshape a title clamped differently on each platform).
    private const int MaxTextLength = 2000;
    
    private string _text = string.Empty;
    private bool _isDone;
    private bool _isClosed;
    private bool _isPinned;
    private DateTime? _dueDate;
    private string _notes = string.Empty;
    private DateTime _modifiedAt = DateTime.UtcNow;
    private RecurrenceRule _recurrence = RecurrenceRule.None;
    private int _recurrenceInterval = 1;
    private TaskPriority _priority = TaskPriority.None;

    public Guid Id { get; set; } = Guid.NewGuid();

    // Sync-relevant timestamps only - see UtcDateTimeConverter. DueDate deliberately stays local.
    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonConverter(typeof(UtcDateTimeConverter))]
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

    /// <summary>
    /// The task title. Limited to 2000 characters.
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

    // ROADMAP.md #31: recurrence used to be fixed at "every 1 [day/week/month/year]" - this
    // multiplies NextDueDate's step (e.g. Weekly + 2 = every 2 weeks). Clamped to >=1 so a
    // corrupt/hand-edited value of 0 (or negative) can never produce a next occurrence due on or
    // before the one that just completed, which would effectively spawn duplicates immediately.
    public int RecurrenceInterval
    {
        get => _recurrenceInterval;
        set => SetField(ref _recurrenceInterval, value < 1 ? 1 : value);
    }

    public TaskPriority Priority
    {
        get => _priority;
        set => SetField(ref _priority, value);
    }

    // Legacy fields, kept only so old saved data can be migrated into Body once on load. Every
    // task that's been through MigrateToBody (TodoStore.cs) - which is to say every task saved by
    // this app in a long time - has these permanently empty, yet they used to serialize into every
    // single save/sync payload regardless (ROADMAP #131). [JsonIgnore] here plus the shadow
    // "ForSerialization" properties below skip them in the wire format once empty, while still
    // reading an old file's populated values in (System.Text.Json has no built-in "omit if empty
    // collection/string" condition - only WhenWritingDefault/WhenWritingNull, which compare against
    // the CLR default, i.e. null, not ""/an empty collection - hence the null-when-empty shadow
    // getter to make WhenWritingNull apply).
    [JsonIgnore]
    public string Notes
    {
        get => _notes;
        set => SetField(ref _notes, value ?? string.Empty);
    }

    [JsonPropertyName("Notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotesForSerialization
    {
        get => _notes.Length == 0 ? null : _notes;
        set => Notes = value ?? string.Empty;
    }

    [JsonIgnore]
    public ObservableCollection<TaskLink> Links { get; set; } = new();

    [JsonPropertyName("Links")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservableCollection<TaskLink>? LinksForSerialization
    {
        get => Links.Count == 0 ? null : Links;
        set => Links = value ?? new ObservableCollection<TaskLink>();
    }

    [JsonIgnore]
    public ObservableCollection<string> Photos { get; set; } = new();

    [JsonPropertyName("Photos")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservableCollection<string>? PhotosForSerialization
    {
        get => Photos.Count == 0 ? null : Photos;
        set => Photos = value ?? new ObservableCollection<string>();
    }

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

    // Write-only snapshot for background JSON serialization (see TodoStore.SaveAsync) - taken
    // synchronously on the calling (usually UI) thread before handing off to a background writer,
    // so nothing can still be mutating the live ObservableCollections a background enumerator
    // would otherwise race against. Never exposed to any bound UI, so shallow copies of
    // Links/Photos/Tags (sharing the same item instances) are enough - only Body needs a deep
    // element copy, since NoteBlock itself owns a further nested collection (ChecklistItems).
    public TaskItem Clone() => new()
    {
        Id = Id,
        CreatedAt = CreatedAt,
        ModifiedAt = ModifiedAt,
        IsPinned = IsPinned,
        Text = Text,
        IsDone = IsDone,
        IsClosed = IsClosed,
        DueDate = DueDate,
        Recurrence = Recurrence,
        RecurrenceInterval = RecurrenceInterval,
        Priority = Priority,
        Notes = Notes,
        Links = new ObservableCollection<TaskLink>(Links),
        Photos = new ObservableCollection<string>(Photos),
        Body = new ObservableCollection<NoteBlock>(Body.Select(b => b.Clone())),
        Tags = new ObservableCollection<string>(Tags),
    };
}
