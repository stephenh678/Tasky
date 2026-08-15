using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private readonly AttachmentService _attachments;
    private readonly Func<IEnumerable<string>> _getAllTags;
    private string _newTagText = string.Empty;
    private bool _showInfoPanel;
    private bool _isTagPopupOpen;
    private List<string> _availableTags = new();

    public TaskItem Task { get; }

    public NoteBlock PrimaryBlock
    {
        get
        {
            if (Task.Body.Count == 0)
            {
                var block = new NoteBlock { Type = NoteBlockType.Text };
                Task.Body.Add(block);
                AttachBlock(block);
                return block;
            }
            return Task.Body[0];
        }
    }

    public string NewTagText
    {
        get => _newTagText;
        set
        {
            if (!SetField(ref _newTagText, value)) return;
            OnPropertyChanged(nameof(FilteredAvailableTags));
            OnPropertyChanged(nameof(TagHint));
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
            var q = NewTagText.Trim().TrimStart('#').ToLowerInvariant();
            if (q.Length == 0) return "Type to search or create a tag";

            var exists = _availableTags.Any(t => t.Equals(q, StringComparison.OrdinalIgnoreCase))
                         || Task.Tags.Any(t => t.Equals(q, StringComparison.OrdinalIgnoreCase));
            return exists ? $"Press Enter to add existing tag \"{q}\"" : $"Press Enter to create new tag \"{q}\"";
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

    public RelayCommand AddTextBlockCommand { get; }
    public RelayCommand AddLinkBlockCommand { get; }
    public RelayCommand AddPhotoBlockCommand { get; }
    public RelayCommand AddFileBlockCommand { get; }
    public RelayCommand AddChecklistBlockCommand { get; }
    public RelayCommand AddChecklistItemCommand { get; }
    public RelayCommand RemoveChecklistItemCommand { get; }
    public RelayCommand RemoveBlockCommand { get; }
    public RelayCommand OpenBlockLinkCommand { get; }
    public RelayCommand OpenBlockFileCommand { get; }
    public RelayCommand AddTagCommand { get; }
    public RelayCommand RemoveTagCommand { get; }
    public RelayCommand SelectExistingTagCommand { get; }
    public RelayCommand ToggleInfoPanelCommand { get; }
    public RelayCommand ToggleTagPopupCommand { get; }

    public RecurrenceRule[] RecurrenceOptions { get; } = Enum.GetValues<RecurrenceRule>();

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
            var basis = Task.DueDate ?? DateTime.Today;
            var next = Task.Recurrence switch
            {
                RecurrenceRule.Daily => basis.AddDays(1),
                RecurrenceRule.Weekly => basis.AddDays(7),
                RecurrenceRule.Monthly => basis.AddMonths(1),
                RecurrenceRule.Yearly => basis.AddYears(1),
                _ => basis
            };
            return $"Marking this completed creates the next occurrence, due {next:MMM d, yyyy}.";
        }
    }

    public TaskDetailViewModel(TaskItem task, AttachmentService attachments, Action onChanged,
        Func<IEnumerable<string>> getAllTags, Action onTypingChanged, Action<string, Action> pushUndo)
    {
        Task = task;
        _attachments = attachments;
        _onChanged = onChanged;
        _getAllTags = getAllTags;
        _onTypingChanged = onTypingChanged;
        _pushUndo = pushUndo;

        Task.PropertyChanged += Task_PropertyChanged;
        Task.Body.CollectionChanged += Body_CollectionChanged;
        foreach (var block in Task.Body)
            AttachBlock(block);

        if (Task.Body.Count == 0)
        {
            Task.Body.Add(new NoteBlock { Type = NoteBlockType.Text });
            _onChanged();
        }

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
        });

        AddTagCommand = new RelayCommand(_ =>
        {
            var tag = NewTagText.Trim().TrimStart('#').ToLowerInvariant();
            NewTagText = string.Empty;
            IsTagPopupOpen = false;
            if (tag.Length == 0) return;
            if (Task.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
            Task.Tags.Add(tag);
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
            _onChanged();
        });

        RemoveTagCommand = new RelayCommand(p =>
        {
            if (p is not string tag) return;
            Task.Tags.Remove(tag);
            _onChanged();
            _pushUndo($"Remove tag \"{tag}\"", () =>
            {
                if (!Task.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                    Task.Tags.Add(tag);
                _onChanged();
            });
        });

        AddTextBlockCommand = new RelayCommand(_ =>
        {
            Task.Body.Add(new NoteBlock { Type = NoteBlockType.Text });
            _onChanged();
        });

        AddLinkBlockCommand = new RelayCommand(_ =>
        {
            Task.Body.Add(new NoteBlock { Type = NoteBlockType.Link, LinkLabel = "New Link", Url = "https://" });
            _onChanged();
        });

        AddPhotoBlockCommand = new RelayCommand(_ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add Photos",
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true)
                AddPhotosFromPaths(dialog.FileNames);
        });

        AddFileBlockCommand = new RelayCommand(_ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add File",
                Filter = "All files (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true)
                AddFilesFromPaths(dialog.FileNames);
        });

        AddChecklistBlockCommand = new RelayCommand(_ =>
        {
            var block = new NoteBlock { Type = NoteBlockType.Checklist };
            block.ChecklistItems.Add(new ChecklistItem());
            Task.Body.Add(block);
            _onChanged();
        });

        AddChecklistItemCommand = new RelayCommand(p =>
        {
            if (p is not NoteBlock block) return;
            block.ChecklistItems.Add(new ChecklistItem());
        });

        RemoveChecklistItemCommand = new RelayCommand(p =>
        {
            if (p is not ChecklistItem item) return;
            foreach (var block in Task.Body.Where(b => b.Type == NoteBlockType.Checklist))
                if (block.ChecklistItems.Remove(item)) return;
        });

        OpenBlockFileCommand = new RelayCommand(p =>
        {
            if (p is not NoteBlock block || !File.Exists(block.PhotoPath)) return;

            // PhotoPath is loaded straight from the .tasky JSON. Refusing anything outside this
            // file's own Attachments folder (the only place CopyFile/SaveBytes ever write to)
            // means a hand-edited or restored-from-an-untrusted-backup file can't point this at
            // an arbitrary local file and have it opened with no warning.
            if (!_attachments.IsUnderAttachmentsRoot(block.PhotoPath)) return;

            var ext = Path.GetExtension(block.PhotoPath);
            if (ExecutableExtensions.Contains(ext))
            {
                var confirm = ThemedMessageBox.Show(
                    $"\"{block.FileName}\" is an executable program or script. Opening it will execute code on your computer.\n\nDo you want to continue?",
                    "Security Warning", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (confirm != System.Windows.MessageBoxResult.Yes) return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(block.PhotoPath) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No default app registered for this file type, or it's otherwise unopenable; ignore.
            }
        });

        RemoveBlockCommand = new RelayCommand(p =>
        {
            if (p is not NoteBlock block) return;
            var index = Task.Body.IndexOf(block);
            Task.Body.Remove(block);

            // Photo/File blocks delete the underlying attachment file immediately, which would
            // make "undo" resurrect a block pointing at a file that's already gone - so only
            // text and link blocks (nothing on disk to lose) get an undo entry.
            if (block.Type is NoteBlockType.Photo or NoteBlockType.File)
            {
                _attachments.DeleteFile(block.PhotoPath);
            }
            else
            {
                var label = block.Type switch
                {
                    NoteBlockType.Link => "link",
                    NoteBlockType.Checklist => "checklist",
                    _ => "text"
                };
                _pushUndo($"Remove {label} block", () =>
                {
                    Task.Body.Insert(Math.Min(index, Task.Body.Count), block);
                    _onChanged();
                });
            }

            _onChanged();
        });

        OpenBlockLinkCommand = new RelayCommand(p =>
        {
            if (p is not NoteBlock block || string.IsNullOrWhiteSpace(block.Url)) return;
            var url = block.Url.Contains("://") ? block.Url : "https://" + block.Url;

            // Url is loaded straight from the .tasky JSON - restricting to http(s) means a link
            // block whose Url was ever hand-edited or restored from an untrusted backup (e.g. a
            // file:// URI pointing at a local executable) can't get handed to ShellExecute and
            // launched with no warning.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Invalid or unreachable URL; silently ignore rather than crash the app.
            }
        });

    }

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
    public static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".pif", ".hta"
    };

    public void AddPhotosFromPaths(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths.Where(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            // One bad file in a multi-file drop (source removed mid-drag, disk full, a name
            // collision) used to abort the loop with _onChanged() never called - losing every
            // file that had already copied successfully, not just the one that failed.
            try
            {
                var copy = _attachments.CopyFile(Task.Id, path);
                var block = new NoteBlock { Type = NoteBlockType.Photo, PhotoPath = copy, Text = string.Empty, Rtf = string.Empty };
                Task.Body.Add(block);
                added = true;
            }
            catch (IOException)
            {
            }
        }
        if (added) _onChanged();
    }

    public void AddFilesFromPaths(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths)
        {
            try
            {
                var copy = _attachments.CopyFile(Task.Id, path);
                Task.Body.Add(new NoteBlock { Type = NoteBlockType.File, PhotoPath = copy });
                added = true;
            }
            catch (IOException)
            {
            }
        }
        if (added) _onChanged();
    }

    public void AddPhotoFromClipboardImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var fileName = $"pasted_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var path = _attachments.SaveBytes(Task.Id, stream.ToArray(), fileName);
        Task.Body.Add(new NoteBlock { Type = NoteBlockType.Photo, PhotoPath = path, Text = string.Empty, Rtf = string.Empty });
        _onChanged();
    }

    public void AddLinkFromUrl(string url)
    {
        Task.Body.Add(new NoteBlock { Type = NoteBlockType.Link, LinkLabel = DeriveLinkLabel(url), Url = url });
        _onChanged();
    }

    private static string DeriveLinkLabel(string url)
    {
        var candidate = url.Contains("://") ? url : "https://" + url;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri.Host : url;
    }

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
        if (e.PropertyName is nameof(TaskItem.DueDate) or nameof(TaskItem.Recurrence))
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

        OnPropertyChanged(nameof(WordCount));
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
        _onChanged();
    }

    private void AttachChecklistItem(ChecklistItem item) => item.PropertyChanged += ChecklistItem_PropertyChanged;

    private void DetachChecklistItem(ChecklistItem item) => item.PropertyChanged -= ChecklistItem_PropertyChanged;

    private void ChecklistItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
