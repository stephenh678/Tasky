using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
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
    // the tag box (NewTagPreview reads SanitizeTag on every NewTagText change) - now precompiled
    // once in TagUtils, shared with the bulk-edit tag dialog.
    private static string SanitizeTag(string raw) => TagUtils.Sanitize(raw);

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

    public static readonly string[] DueTimeOptions =
    [
        "All Day",
        "12:00 AM", "12:30 AM", "01:00 AM", "01:30 AM", "02:00 AM", "02:30 AM",
        "03:00 AM", "03:30 AM", "04:00 AM", "04:30 AM", "05:00 AM", "05:30 AM",
        "06:00 AM", "06:30 AM", "07:00 AM", "07:30 AM", "08:00 AM", "08:30 AM",
        "09:00 AM", "09:30 AM", "10:00 AM", "10:30 AM", "11:00 AM", "11:30 AM",
        "12:00 PM", "12:30 PM", "01:00 PM", "01:30 PM", "02:00 PM", "02:30 PM",
        "03:00 PM", "03:30 PM", "04:00 PM", "04:30 PM", "05:00 PM", "05:30 PM",
        "06:00 PM", "06:30 PM", "07:00 PM", "07:30 PM", "08:00 PM", "08:30 PM",
        "09:00 PM", "09:30 PM", "10:00 PM", "10:30 PM", "11:00 PM", "11:30 PM"
    ];

    public string SelectedDueTime
    {
        get
        {
            if (!Task.DueDate.HasValue) return "All Day";
            var time = Task.DueDate.Value.TimeOfDay;
            if (time == TimeSpan.Zero) return "All Day";
            return DateTime.Today.Add(time).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("All Day", StringComparison.OrdinalIgnoreCase))
            {
                if (Task.DueDate.HasValue && Task.DueDate.Value.TimeOfDay != TimeSpan.Zero)
                {
                    Task.DueDate = Task.DueDate.Value.Date;
                    OnPropertyChanged(nameof(SelectedDueTime));
                    OnPropertyChanged(nameof(HasDueTime));
                }
                return;
            }

            if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ||
                DateTime.TryParse(value, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out parsed))
            {
                var date = Task.DueDate?.Date ?? DateTime.Today;
                Task.DueDate = date.Add(parsed.TimeOfDay);
                OnPropertyChanged(nameof(SelectedDueTime));
                OnPropertyChanged(nameof(HasDueTime));
                OnPropertyChanged(nameof(HasDueDate));
            }
        }
    }

    public bool HasDueTime => Task.DueDate.HasValue && Task.DueDate.Value.TimeOfDay != TimeSpan.Zero;
    public bool HasDueDate => Task.DueDate.HasValue;
    public RelayCommand ClearDueDateCommand { get; }

    private string _newSubtaskText = string.Empty;
    public string NewSubtaskText
    {
        get => _newSubtaskText;
        set => SetField(ref _newSubtaskText, value);
    }

    public ObservableCollection<ChecklistItem> Subtasks
    {
        get
        {
            var block = Task.Body.FirstOrDefault(b => b.Type == NoteBlockType.Checklist);
            return block?.ChecklistItems ?? _emptySubtasks;
        }
    }
    private static readonly ObservableCollection<ChecklistItem> _emptySubtasks = new();

    public bool HasSubtasks => Subtasks.Count > 0;
    public int SubtasksTotal => Subtasks.Count;
    public int SubtasksCompleted => Subtasks.Count(s => s.IsChecked);
    public double SubtaskProgressPercent
    {
        get => SubtasksTotal > 0 ? (double)SubtasksCompleted / SubtasksTotal * 100.0 : 0.0;
        set { }
    }
    public string SubtaskProgressText => $"{SubtasksCompleted} of {SubtasksTotal} completed";

    private readonly Func<bool>? _alwaysShowSubtasks;
    private readonly Action? _requestFocusSubtask;
    private readonly Func<int, bool>? _confirmRemoveSubtasks;
    private bool _isSubtasksVisible;

    // Computed rather than a plain settable flag: the section shows if the user opened it for this
    // task (_isSubtasksVisible), if Settings > "Always show subtasks" is on, or if the task already
    // has subtasks - that last one keeps existing tasks' subtasks reachable no matter the setting.
    // A public setter would be a trap here (SetField on the backing field can't tell whether the
    // computed value actually changed), so the commands below flip the field directly instead.
    public bool IsSubtasksVisible =>
        _isSubtasksVisible || (_alwaysShowSubtasks?.Invoke() ?? false) || HasSubtasks;

    public bool ShowAddSubtasksButton => !IsSubtasksVisible;

    public RelayCommand AddSubtaskCommand { get; }
    public RelayCommand RemoveSubtaskCommand { get; }
    public RelayCommand ShowSubtasksCommand { get; }
    public RelayCommand DismissSubtasksCommand { get; }

    public void NotifySubtasksVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsSubtasksVisible));
        OnPropertyChanged(nameof(ShowAddSubtasksButton));
    }

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
        Func<IEnumerable<string>> getAllTags, Action onTypingChanged, Action<string, Action> pushUndo,
        Func<bool>? alwaysShowSubtasks = null, Action? requestFocusSubtask = null,
        Func<int, bool>? confirmRemoveSubtasks = null)
    {
        Task = task;
        _onChanged = onChanged;
        _getAllTags = getAllTags;
        _onTypingChanged = onTypingChanged;
        _pushUndo = pushUndo;
        _alwaysShowSubtasks = alwaysShowSubtasks;
        _requestFocusSubtask = requestFocusSubtask;
        _confirmRemoveSubtasks = confirmRemoveSubtasks;

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

        ClearDueDateCommand = new RelayCommand(_ => Task.DueDate = null, _ => Task.DueDate.HasValue);
        AddSubtaskCommand = new RelayCommand(_ => AddSubtask());
        RemoveSubtaskCommand = new RelayCommand(p => RemoveSubtask(p as ChecklistItem));
        ShowSubtasksCommand = new RelayCommand(_ =>
        {
            _isSubtasksVisible = true;
            NotifySubtasksVisibilityChanged();
            _requestFocusSubtask?.Invoke();
        });
        DismissSubtasksCommand = new RelayCommand(
            _ =>
            {
                var block = Task.Body.FirstOrDefault(b => b.Type == NoteBlockType.Checklist);
                var items = block?.ChecklistItems.Count ?? 0;

                if (items > 0)
                {
                    // Named count, not a bare "Remove subtasks from this task?": this block lives at
                    // Body index 1+ (EnsurePrimaryTextBlock keeps Body[0] Text-typed), which is the
                    // range AdditionalBlocks renders as "More content (synced from Tasky Web)" - so
                    // one Yes here can also clear a list the user last touched on another device.
                    if (!ConfirmRemoveSubtasks(items)) return;

                    var blockIndex = Task.Body.IndexOf(block!);
                    Task.Body.RemoveAt(blockIndex);
                    _isSubtasksVisible = false;
                    RefreshSubtasks();
                    _onChanged();

                    _pushUndo("Remove subtasks", () =>
                    {
                        if (!Task.Body.Contains(block!))
                            Task.Body.Insert(Math.Min(blockIndex, Task.Body.Count), block!);
                        _isSubtasksVisible = true;
                        RefreshSubtasks();
                        _onChanged();
                    });
                    return;
                }

                // Nothing to delete - this is a pure collapse. Only touch the task (and so only
                // Save()) if there was actually an empty leftover block to drop; otherwise a click
                // on a task nobody edited would rewrite the whole data file.
                if (block != null)
                {
                    Task.Body.Remove(block);
                    _onChanged();
                }
                _isSubtasksVisible = false;
                RefreshSubtasks();
            },
            // Without this the button is a dead control when "Always show subtasks" is on and the
            // task has none: the click would clear _isSubtasksVisible, IsSubtasksVisible would keep
            // returning true because the setting ORs in, and nothing on screen would move.
            _ => HasSubtasks || !(_alwaysShowSubtasks?.Invoke() ?? false));
    }

    // Split out so the destructive branch of DismissSubtasksCommand is reachable from tests, which
    // can't answer a real dialog - the same ViewModel-signals/View-prompts split MainViewModel uses
    // for BulkSetDueDateRequested and SaveViewRequested.
    private bool ConfirmRemoveSubtasks(int itemCount)
    {
        if (_confirmRemoveSubtasks is not null) return _confirmRemoveSubtasks(itemCount);

        var noun = itemCount == 1 ? "subtask" : "subtasks";
        return ThemedMessageBox.Show(
            $"Remove all {itemCount} {noun} from this task?",
            "Remove Subtasks",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void AddSubtask(string? text = null)
    {
        var input = (text ?? NewSubtaskText).Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        var block = Task.Body.FirstOrDefault(b => b.Type == NoteBlockType.Checklist);
        if (block is null)
        {
            block = new NoteBlock { Type = NoteBlockType.Checklist };
            Task.Body.Add(block);
        }

        var item = new ChecklistItem { Text = input, IsChecked = false };
        block.ChecklistItems.Add(item);
        NewSubtaskText = string.Empty;
        // Captured before the flip so undo restores the section's prior state too - undoing the
        // first subtask on a task that wasn't showing the section shouldn't leave it open and empty.
        var wasVisible = _isSubtasksVisible;
        _isSubtasksVisible = true;
        RefreshSubtasks();
        _onChanged();
        _pushUndo($"Add subtask \"{input}\"", () =>
        {
            block.ChecklistItems.Remove(item);
            _isSubtasksVisible = wasVisible;
            RefreshSubtasks();
        });
    }

    public void RemoveSubtask(ChecklistItem? item)
    {
        if (item is null) return;
        var block = Task.Body.FirstOrDefault(b => b.Type == NoteBlockType.Checklist);
        if (block is null) return;

        var idx = block.ChecklistItems.IndexOf(item);
        if (idx < 0) return;

        block.ChecklistItems.RemoveAt(idx);
        RefreshSubtasks();
        _onChanged();
        _pushUndo($"Delete subtask \"{item.Text}\"", () =>
        {
            block.ChecklistItems.Insert(idx, item);
            RefreshSubtasks();
        });
    }

    private void RefreshSubtasks()
    {
        OnPropertyChanged(nameof(Subtasks));
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(IsSubtasksVisible));
        OnPropertyChanged(nameof(ShowAddSubtasksButton));
        OnPropertyChanged(nameof(SubtasksTotal));
        OnPropertyChanged(nameof(SubtasksCompleted));
        OnPropertyChanged(nameof(SubtaskProgressPercent));
        OnPropertyChanged(nameof(SubtaskProgressText));
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
        if (e.PropertyName == nameof(TaskItem.DueDate))
        {
            OnPropertyChanged(nameof(SelectedDueTime));
            OnPropertyChanged(nameof(HasDueTime));
            OnPropertyChanged(nameof(HasDueDate));
        }
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

        RefreshSubtasks();

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
        RefreshSubtasks();
        _onChanged();
    }

    private void AttachChecklistItem(ChecklistItem item) => item.PropertyChanged += ChecklistItem_PropertyChanged;

    private void DetachChecklistItem(ChecklistItem item) => item.PropertyChanged -= ChecklistItem_PropertyChanged;

    private void ChecklistItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Task.ModifiedAt = DateTime.UtcNow;
        if (e.PropertyName == nameof(ChecklistItem.IsChecked))
        {
            RefreshSubtasks();
            _onChanged();
        }
        else
        {
            _onTypingChanged();
        }
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
