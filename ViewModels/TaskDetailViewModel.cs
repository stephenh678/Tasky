using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.ViewModels;

public class TaskDetailViewModel : INotifyPropertyChanged
{
    private readonly Action _onChanged;
    private readonly Action _onTypingChanged;
    private readonly Action<string, Action> _pushUndo;
    private readonly Func<IEnumerable<string>> _getAllTags;
    private string _newTagText = string.Empty;
    private bool _showInfoPanel;
    private bool _isTagPopupOpen;
    private List<string> _availableTags = new();
    private bool _suppressModifiedBump;

    public TaskItem Task { get; }

    public NoteBlock PrimaryBlock
    {
        get
        {
            EnsurePrimaryTextBlock();
            return Task.Body[0];
        }
    }

    // Body[0] is the only block NoteEditor renders as the main text area - guarantee it exists and
    // is Text-typed. Called once at construction (a brand-new task, or one whose first content came
    // from a non-desktop client - e.g. Tasky Web's "+ Photo" on an otherwise-empty task) and
    // defensively from the PrimaryBlock getter too, since TaskSyncMerge.ApplyTaskFields replaces
    // this same live Body collection wholesale on a remote merge and can leave a non-Text block at
    // index 0 while this task is already open here. Wrapped in _suppressModifiedBump so
    // Body_CollectionChanged doesn't treat this normalization as a real edit - merely
    // selecting/viewing a task must never bump ModifiedAt and make it silently win a future sync
    // merge over an actual remote edit (see the code review that caught this).
    private void EnsurePrimaryTextBlock()
    {
        if (Task.Body.Count > 0 && Task.Body[0].Type == NoteBlockType.Text) return;

        _suppressModifiedBump = true;
        try
        {
            var block = new NoteBlock { Type = NoteBlockType.Text };
            if (Task.Body.Count == 0)
                Task.Body.Add(block);
            else
                Task.Body.Insert(0, block);
        }
        finally
        {
            _suppressModifiedBump = false;
        }
    }

    // Body[0] (PrimaryBlock) is the only content NoteEditor renders - anything else in Body only
    // gets there via a client that isn't this desktop UI (Tasky Web's per-type "+" buttons, or a
    // second device's own PrimaryBlock once merged in here as index 1+). Surfaced read-mostly
    // below the main editor so that content isn't silently invisible on desktop - see
    // Body_CollectionChanged for how this stays live across edits and remote sync merges.
    public IReadOnlyList<NoteBlock> AdditionalBlocks => Task.Body.Skip(1).ToList();

    public RelayCommand RemoveAdditionalBlockCommand { get; }

    public string NewTagText
    {
        get => _newTagText;
        set
        {
            if (!SetField(ref _newTagText, value)) return;
            OnPropertyChanged(nameof(FilteredAvailableTags));
            OnPropertyChanged(nameof(TagHint));
            OnPropertyChanged(nameof(NewTagPreview));
            OnPropertyChanged(nameof(CanCreateNewTag));
        }
    }

    // Existing tags (from every task) that aren't already on this one, narrowed by whatever's typed.
    public IEnumerable<string> FilteredAvailableTags
    {
        get
        {
            var q = NewTagText.Trim().TrimStart('#');
            return q.Length == 0
                ? _availableTags
                : _availableTags.Where(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
    }

    // Makes it unambiguous whether Enter will attach an existing tag or create a brand new one.
    public string TagHint
    {
        get
        {
            var q = NewTagPreview;
            if (q.Length == 0) return "Type to search or create a tag";

            return CanCreateNewTag ? $"Press Enter to create new tag \"{q}\"" : $"Press Enter to add existing tag \"{q}\"";
        }
    }

    // Cleaned/lowercased form of whatever's typed - what AddTagCommand will actually store, so the
    // "+ Create" row below shows the exact tag that pressing Enter would create instead of the raw
    // (possibly '#'-prefixed, mixed-case, punctuation-containing) text still in the box.
    public string NewTagPreview => SanitizeTag(NewTagText);

    // ROADMAP #62: was an inline Regex.Replace call, recompiling this pattern on every keystroke in
    // the tag box (NewTagPreview reads SanitizeTag on every NewTagText change).
    private static readonly Regex InvalidTagCharsPattern = new("[^\\w-]", RegexOptions.Compiled);

    // Strips everything but word characters and hyphens - matches Tasky Web's addTag() (docs/js/app.js)
    // so a tag typed here and one typed there normalize to the same string instead of a stray space or
    // comma silently breaking "#tag" quick-add parsing or the "tag:name" search operator on either
    // platform.
    private static string SanitizeTag(string raw)
        => InvalidTagCharsPattern.Replace(raw.Trim().TrimStart('#'), "").ToLowerInvariant();

    // Existing tags only ever appear as clickable rows in FilteredAvailableTags - a name nobody's
    // used yet had no click target at all, just the hint text below the box, so creating a tag by
    // mouse alone looked broken (Enter still worked, but nothing indicated it). This drives a
    // "+ Create <name>" row so a not-yet-existing tag is just as clickable as an existing one.
    public bool CanCreateNewTag
    {
        get
        {
            var q = NewTagPreview;
            if (q.Length == 0) return false;
            return !_availableTags.Any(t => t.Equals(q, StringComparison.OrdinalIgnoreCase))
                   && !Task.Tags.Any(t => t.Equals(q, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ShowInfoPanel
    {
        get => _showInfoPanel;
        set => SetField(ref _showInfoPanel, value);
    }

    public bool IsTagPopupOpen
    {
        get => _isTagPopupOpen;
        set => SetField(ref _isTagPopupOpen, value);
    }

    public int WordCount => Task.Body
        .Where(b => b.Type == NoteBlockType.Text)
        .Sum(b => b.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    public string CreatedDisplay => Task.CreatedAt.ToString("MMM d, yyyy 'at' h:mm tt");

    public string ModifiedDisplay => Task.ModifiedAt.ToString("MMM d, yyyy 'at' h:mm tt");

    public RelayCommand AddTagCommand { get; }
    public RelayCommand RemoveTagCommand { get; }
    public RelayCommand SelectExistingTagCommand { get; }
    public RelayCommand ToggleInfoPanelCommand { get; }
    public RelayCommand ToggleTagPopupCommand { get; }

    public RecurrenceRule[] RecurrenceOptions { get; } = Enum.GetValues<RecurrenceRule>();
    public TaskPriority[] PriorityOptions { get; } = Enum.GetValues<TaskPriority>();

    // ROADMAP.md #31: lets "Repeat" go beyond the fixed "every 1 [unit]" - a plain 1-30 range
    // (an editable numeric box would need its own PreviewTextInput/paste validation, same as
    // SettingsWindow's RetentionDaysTextBox; a bounded ComboBox sidesteps that for a value that
    // never needs to go higher than "every 30 days/weeks/months/years" anyway).
    public int[] RecurrenceIntervalOptions { get; } = Enumerable.Range(1, 30).ToArray();

    // Completed and trashed tasks are meant to be reviewed, restored, or reopened - not edited in
    // place. "Open" (neither) is the only status where content should actually be changeable.
    public bool IsEditable => !Task.IsDone && !Task.IsClosed;
    public bool IsReadOnly => !IsEditable;

    // Unlike the rest of the editor, the complete toggle itself stays live for a merely-Completed
    // task (unchecking it is exactly how you reopen it) - it only locks once the task is in Trash,
    // where toggling IsDone in place would go nowhere and just be confusing.
    public bool CanToggleComplete => !Task.IsClosed;

    // Spells out what "Repeat" actually does, since a bare dropdown gives no sense of when the
    // next occurrence lands or that completing this task is what creates it.
    public string RecurrenceSummary
    {
        get
        {
            if (Task.Recurrence == RecurrenceRule.None) return string.Empty;
            var interval = Task.RecurrenceInterval;
            var basis = Task.DueDate ?? DateTime.Today;
            var next = Task.Recurrence switch
            {
                RecurrenceRule.Daily => basis.AddDays(interval),
                RecurrenceRule.Weekly => basis.AddDays(7 * interval),
                RecurrenceRule.Monthly => basis.AddMonths(interval),
                RecurrenceRule.Yearly => basis.AddYears(interval),
                _ => basis
            };
            // "every 1 week" reads oddly - drop the count entirely for the common single-interval
            // case rather than saying "every 1 week"/"every week", which is a very literal
            // description nobody actually says out loud.
            var unit = Task.Recurrence switch
            {
                RecurrenceRule.Daily => "day",
                RecurrenceRule.Weekly => "week",
                RecurrenceRule.Monthly => "month",
                RecurrenceRule.Yearly => "year",
                _ => ""
            };
            var cadence = interval == 1 ? $"every {unit}" : $"every {interval} {unit}s";
            return $"Repeats {cadence}. Marking this completed creates the next occurrence, due {next:MMM d, yyyy}.";
        }
    }

    public TaskDetailViewModel(TaskItem task, Action onChanged,
        Func<IEnumerable<string>> getAllTags, Action onTypingChanged, Action<string, Action> pushUndo)
    {
        Task = task;
        _onChanged = onChanged;
        _getAllTags = getAllTags;
        _onTypingChanged = onTypingChanged;
        _pushUndo = pushUndo;

        Task.PropertyChanged += Task_PropertyChanged;
        Task.Body.CollectionChanged += Body_CollectionChanged;
        foreach (var block in Task.Body)
            AttachBlock(block);

        EnsurePrimaryTextBlock();

        ToggleInfoPanelCommand = new RelayCommand(_ => ShowInfoPanel = !ShowInfoPanel);
        ToggleTagPopupCommand = new RelayCommand(_ =>
        {
            IsTagPopupOpen = !IsTagPopupOpen;
            if (!IsTagPopupOpen) return;

            _availableTags = _getAllTags()
                .Where(t => !Task.Tags.Any(existing => existing.Equals(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            NewTagText = string.Empty;
            OnPropertyChanged(nameof(FilteredAvailableTags));
            OnPropertyChanged(nameof(TagHint));
            OnPropertyChanged(nameof(CanCreateNewTag));
        });

        AddTagCommand = new RelayCommand(_ =>
        {
            var tag = SanitizeTag(NewTagText);
            NewTagText = string.Empty;
            IsTagPopupOpen = false;
            if (tag.Length == 0) return;
            if (Task.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
            Task.Tags.Add(tag);
            // Same gap as Body/NoteBlock changes (see Body_CollectionChanged): Tags is a plain
            // ObservableCollection property with no SetField wrapper, so adding/removing an entry
            // never raises TaskItem.PropertyChanged and MainViewModel's sync merge never sees it
            // as an edit worth keeping.
            Task.ModifiedAt = DateTime.UtcNow;
            _onChanged();
        });

        SelectExistingTagCommand = new RelayCommand(p =>
        {
            IsTagPopupOpen = false;
            if (p is not string tag) return;
            tag = tag.Trim().ToLowerInvariant();
            if (tag.Length == 0) return;
            if (Task.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
            Task.Tags.Add(tag);
            Task.ModifiedAt = DateTime.UtcNow;
            _onChanged();
        });

        RemoveTagCommand = new RelayCommand(p =>
        {
            if (p is not string tag) return;
            Task.Tags.Remove(tag);
            Task.ModifiedAt = DateTime.UtcNow;
            _onChanged();
            _pushUndo($"Remove tag \"{tag}\"", () =>
            {
                if (!Task.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                    Task.Tags.Add(tag);
                Task.ModifiedAt = DateTime.UtcNow;
                _onChanged();
            });
        });

        RemoveAdditionalBlockCommand = new RelayCommand(p =>
        {
            if (p is not NoteBlock block || block == PrimaryBlock) return;
            var index = Task.Body.IndexOf(block);
            if (index < 0) return;
            Task.Body.RemoveAt(index);
            Task.ModifiedAt = DateTime.UtcNow;
            _onChanged();
            _pushUndo("Remove block", () =>
            {
                if (!Task.Body.Contains(block))
                    Task.Body.Insert(Math.Min(index, Task.Body.Count), block);
                Task.ModifiedAt = DateTime.UtcNow;
                _onChanged();
            });
        });
    }

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    // Called when the user selects a different task (or none) - see MainViewModel.SelectedTask.
    // Without this, Task/its blocks (which stay in AllTasks/Task.Body regardless of selection)
    // would accumulate one more set of live subscriptions from a fresh TaskDetailViewModel every
    // time the same task gets reselected over a session.
    public void Detach()
    {
        Task.PropertyChanged -= Task_PropertyChanged;
        Task.Body.CollectionChanged -= Body_CollectionChanged;
        foreach (var block in Task.Body)
            DetachBlock(block);
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskItem.ModifiedAt))
            OnPropertyChanged(nameof(ModifiedDisplay));
        if (e.PropertyName is nameof(TaskItem.DueDate) or nameof(TaskItem.Recurrence) or nameof(TaskItem.RecurrenceInterval))
            OnPropertyChanged(nameof(RecurrenceSummary));
        if (e.PropertyName is nameof(TaskItem.IsDone) or nameof(TaskItem.IsClosed))
        {
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(IsReadOnly));
        }
        if (e.PropertyName == nameof(TaskItem.IsClosed))
            OnPropertyChanged(nameof(CanToggleComplete));
    }

    private void Body_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (NoteBlock block in e.NewItems)
                AttachBlock(block);

        // A block removed via RemoveBlockCommand can come back through its own undo entry
        // (Task.Body.Insert) - detaching here, not just in Detach(), stops that round-trip from
        // double-subscribing the same block.
        if (e.OldItems is not null)
            foreach (NoteBlock block in e.OldItems)
                DetachBlock(block);

        // Task.PropertyChanged (which MainViewModel listens to for ModifiedAt) only fires for
        // TaskItem's own direct properties - it never sees changes nested inside Body, since
        // Body itself is the same ObservableCollection reference before and after a block is
        // added or removed. Without this, a device that only edits a task's note content (no
        // title/tag/due-date change) never bumps ModifiedAt, so Google Drive sync's "newer wins"
        // merge treats the edit as if nothing happened and silently drops it in favor of an
        // untouched remote copy. See Block_PropertyChanged/ChecklistItem_PropertyChanged below
        // for the same fix applied to editing an existing block's content. Skipped for
        // EnsurePrimaryTextBlock's own inserts (_suppressModifiedBump) - that's normalization for
        // display, not a real edit, and must not make a merely-viewed task win a sync merge.
        if (!_suppressModifiedBump)
            Task.ModifiedAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(AdditionalBlocks));
    }

    private void AttachBlock(NoteBlock block)
    {
        block.PropertyChanged += Block_PropertyChanged;

        if (block.Type != NoteBlockType.Checklist) return;

        foreach (var item in block.ChecklistItems)
            AttachChecklistItem(item);

        block.ChecklistItems.CollectionChanged += ChecklistItems_CollectionChanged;
    }

    private void DetachBlock(NoteBlock block)
    {
        block.PropertyChanged -= Block_PropertyChanged;

        if (block.Type != NoteBlockType.Checklist) return;

        foreach (var item in block.ChecklistItems)
            DetachChecklistItem(item);

        block.ChecklistItems.CollectionChanged -= ChecklistItems_CollectionChanged;
    }

    private void Block_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteBlock.Text))
            OnPropertyChanged(nameof(WordCount));

        // See the comment in Body_CollectionChanged - editing an existing block's content is
        // exactly the same "invisible to sync" gap, just one level deeper (a property on a block
        // inside Body, rather than Body itself changing shape).
        Task.ModifiedAt = DateTime.UtcNow;

        if (e.PropertyName is nameof(NoteBlock.Text) or nameof(NoteBlock.Rtf) or nameof(NoteBlock.PhotoPath))
            _onTypingChanged();
    }

    private void ChecklistItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ChecklistItem item in e.NewItems)
                AttachChecklistItem(item);
        if (e.OldItems is not null)
            foreach (ChecklistItem item in e.OldItems)
                DetachChecklistItem(item);
        Task.ModifiedAt = DateTime.UtcNow;
        _onChanged();
    }

    private void AttachChecklistItem(ChecklistItem item) => item.PropertyChanged += ChecklistItem_PropertyChanged;

    private void DetachChecklistItem(ChecklistItem item) => item.PropertyChanged -= ChecklistItem_PropertyChanged;

    private void ChecklistItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Task.ModifiedAt = DateTime.UtcNow;
        if (e.PropertyName == nameof(ChecklistItem.IsChecked))
            _onChanged();
        else
            _onTypingChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
