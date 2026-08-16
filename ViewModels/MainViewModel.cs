using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TodoApp.Models;
using TodoApp.Services;
using TodoApp;

namespace TodoApp.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly TodoStore _store = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly TrayIconService _tray = new();
    private readonly AttachmentService _attachments;
    private readonly GoogleDriveService _googleDrive = new();
    private readonly Settings _settings;
    // Never reassigned after construction (see LoadFile) - AllTasks below is a passthrough to
    // _state.Tasks, and FilteredTasksView wraps that same collection instance once in the
    // constructor, so both only keep working if _state.Tasks's own identity never changes.
    private readonly AppState _state = new();
    private string _currentFilePath = null!;

    private readonly SidebarFilterItem _allItem = new(SidebarFilterKind.All, "All Tasks");
    private readonly SidebarFilterItem _doneItem = new(SidebarFilterKind.Done, "Completed");
    private readonly SidebarFilterItem _trashItem = new(SidebarFilterKind.Trash, "Trash");
    private readonly SidebarFilterItem _recurringItem = new(SidebarFilterKind.Recurring, "Recurring");

    private readonly DispatcherTimer _saveDebounceTimer;
    private readonly DispatcherTimer _reminderTimer;
    private readonly LinkedList<(string Description, Action Undo)> _undoStack = new();
    private readonly HashSet<Guid> _notifiedTaskIds = new();
    private const int MaxUndoDepth = 25;

    private SidebarFilterItem _selectedSidebarItem;
    private string _searchText = string.Empty;
    private TaskItem? _selectedTask;
    private TaskDetailViewModel? _selectedTaskDetail;
    private bool _isDarkTheme;
    private bool _isFocusMode;
    private bool _isSidebarCollapsed;
    private SortOption _currentSort = SortOption.ModifiedNewest;
    private QuickFilter _currentQuickFilter = QuickFilter.None;
    private bool _isFilterPopupOpen;
    private string _saveStatusText = string.Empty;
    private Task _pendingSaveTask = Task.CompletedTask;
    private int _saveGeneration;
    private bool _isRestoringBackup;
    private bool _isExecutingUndo;
    private bool _reminderCheckInProgress;
    private readonly DispatcherTimer _autoSyncTimer;
    private readonly DispatcherTimer _idleSyncTimer;
    private bool _syncInProgress;

    // A passthrough, not an independent collection - _state.Tasks is now the single source of
    // truth for both "what's saved" and "what's shown", instead of the two being manually kept in
    // sync at every add/remove call site (the previous shape of this: a separately-maintained
    // AllTasks alongside AppState.Tasks, with no guarantee a future call site wouldn't forget one).
    public ObservableCollection<TaskItem> AllTasks => _state.Tasks;
    public ObservableCollection<SidebarFilterItem> SidebarItems { get; } = new();
    public ObservableCollection<SidebarFilterItem> TagItems { get; } = new();
    public ListCollectionView FilteredTasksView { get; }
    public List<TaskItem> SelectedTasks { get; private set; } = new();
    public TrayIconService Tray => _tray;

    public double? SavedWindowLeft => _settings.WindowLeft;
    public double? SavedWindowTop => _settings.WindowTop;
    public double SavedWindowWidth => _settings.WindowWidth;
    public double SavedWindowHeight => _settings.WindowHeight;
    public bool SavedWindowMaximized => _settings.WindowMaximized;

    // The default file is literally named "Tasky", which made this read "Tasky — Tasky" - only
    // show the " — filename" suffix when the open file's name actually differs from the app name.
    public string WindowTitle
    {
        get
        {
            var fileName = Path.GetFileNameWithoutExtension(_currentFilePath);
            return fileName.Equals("Tasky", StringComparison.OrdinalIgnoreCase) ? "Tasky" : $"Tasky — {fileName}";
        }
    }

    public string UndoMenuLabel => _undoStack.Count > 0 ? $"Undo {_undoStack.Last!.Value.Description}" : "Undo";

    public SidebarFilterItem SelectedSidebarItem
    {
        get => _selectedSidebarItem;
        set
        {
            // The Tags ListBox can push null here on its own: if the currently-selected tag
            // gets removed from TagItems (e.g. RefreshTags() drops it because no task has it
            // any more), WPF resets that ListBox's SelectedItem to null and that flows straight
            // into this setter. Falling back to "All Tasks" keeps this field always non-null.
            if (!SetField(ref _selectedSidebarItem, value ?? _allItem)) return;
            SelectedTask = null;
            UpdateSelectedTasks(Enumerable.Empty<TaskItem>());
            FilteredTasksView.Refresh();
            OnPropertyChanged(nameof(EmptyStateMessage));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            FilteredTasksView.Refresh();
            OnPropertyChanged(nameof(EmptyStateMessage));
        }
    }

    public QuickFilter CurrentQuickFilter
    {
        get => _currentQuickFilter;
        set
        {
            if (!SetField(ref _currentQuickFilter, value)) return;
            FilteredTasksView.Refresh();
            OnPropertyChanged(nameof(EmptyStateMessage));
        }
    }

    public bool IsFilterPopupOpen
    {
        get => _isFilterPopupOpen;
        set => SetField(ref _isFilterPopupOpen, value);
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        private set => SetField(ref _saveStatusText, value);
    }

    // A plain computed string rather than a converter, since the message needs to distinguish
    // "nothing here" from "nothing matches your search/filter" from "everything with this tag is
    // in Trash" - situations that all boil down to an empty FilteredTasksView but need different
    // explanations, since a Tag view (unlike every other filter) never shows trashed tasks.
    public string EmptyStateMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText) || CurrentQuickFilter != QuickFilter.None)
                return "No tasks match your search or filter.";
            if (SelectedSidebarItem.Kind == SidebarFilterKind.Tag)
                return "No open or completed tasks have this tag. Check Trash?";
            return "No tasks here yet.";
        }
    }

    public TaskItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetField(ref _selectedTask, value)) return;
            AppLogger.Debug("MainViewModel", $"SelectedTask changed -> ID='{value?.Id}' Title='{value?.Text ?? "(null)"}'");
            FlushPendingSave();

            // Without this, reselecting the same task later creates ANOTHER TaskDetailViewModel
            // subscribed to the same long-lived TaskItem/NoteBlocks - Task and its blocks outlive
            // the selection (they stay in AllTasks/Task.Body regardless), so those subscriptions
            // would just keep piling up for the life of the session instead of being replaced.
            SelectedTaskDetail?.Detach();
            SelectedTaskDetail = value is null
                ? null
                : new TaskDetailViewModel(value, _attachments, OnTaskChanged, GetAllTagNames, RequestDebouncedSave, PushUndo);
        }
    }

    public TaskDetailViewModel? SelectedTaskDetail
    {
        get => _selectedTaskDetail;
        private set => SetField(ref _selectedTaskDetail, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            // Deliberately not SetField here: SetField raises PropertyChanged before this method
            // returns, and MainWindow reacts to that event by repainting the OS title bar based on
            // ThemeService.IsDark - if that event fires before ThemeService.Apply below updates
            // IsDark, the title bar reads the OLD value and ends up one step behind (dark mode
            // shows a light title bar and vice versa). ThemeService.Apply must run first.
            if (_isDarkTheme == value) return;
            _isDarkTheme = value;
            ThemeService.Apply(value ? "Dark" : "Light");
            _settings.Theme = value ? "Dark" : "Light";
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public bool RemindersEnabled
    {
        get => _settings.RemindersEnabled;
        set
        {
            if (_settings.RemindersEnabled == value) return;
            _settings.RemindersEnabled = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public bool IsVerboseLogging
    {
        get => _settings.IsVerboseLogging;
        set
        {
            if (_settings.IsVerboseLogging == value) return;
            _settings.IsVerboseLogging = value;
            AppLogger.IsVerbose = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public bool IsFocusMode
    {
        get => _isFocusMode;
        set
        {
            if (SetField(ref _isFocusMode, value))
                OnPropertyChanged(nameof(SidebarWidth));
        }
    }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (!SetField(ref _isSidebarCollapsed, value)) return;
            OnPropertyChanged(nameof(SidebarWidth));
            _settings.SidebarCollapsed = value;
            _settingsStore.Save(_settings);
        }
    }

    public GridLength SidebarWidth => IsFocusMode
        ? new GridLength(0)
        : new GridLength(IsSidebarCollapsed ? 46 : 220);

    public SortOption CurrentSort
    {
        get => _currentSort;
        set
        {
            if (!SetField(ref _currentSort, value)) return;
            FilteredTasksView.CustomSort = new TaskComparer(value);
        }
    }

    // private set (not the plain get-only these were before) so the constructor can delegate
    // assignment to the InitializeXCommands() groupings below instead of one 290-line body -
    // a get-only auto-property's backing field can only be assigned directly in the constructor
    // itself, not from a method the constructor calls. Still fully read-only from outside the class.
    public RelayCommand AddTaskCommand { get; private set; } = null!;
    public RelayCommand ToggleCloseSelectedCommand { get; private set; } = null!;
    public RelayCommand DeleteSelectedCommand { get; private set; } = null!;
    public RelayCommand ShowAllCommand { get; private set; } = null!;
    public RelayCommand ShowClosedCommand { get; private set; } = null!;
    public RelayCommand ShowTrashCommand { get; private set; } = null!;
    public RelayCommand TrashAllClosedCommand { get; private set; } = null!;
    public RelayCommand TogglePinCommand { get; private set; } = null!;
    public RelayCommand ToggleFocusModeCommand { get; private set; } = null!;
    public RelayCommand ToggleSidebarCommand { get; private set; } = null!;
    public RelayCommand SetSortCommand { get; private set; } = null!;
    public RelayCommand EmptyTrashCommand { get; private set; } = null!;
    public RelayCommand SetQuickFilterCommand { get; private set; } = null!;
    public RelayCommand ToggleFilterPopupCommand { get; private set; } = null!;
    public RelayCommand NewFileCommand { get; private set; } = null!;
    public RelayCommand OpenFileCommand { get; private set; } = null!;
    public RelayCommand SaveFileAsCommand { get; private set; } = null!;
    public RelayCommand UndoCommand { get; private set; } = null!;
    public RelayCommand BulkMarkDoneCommand { get; private set; } = null!;
    public RelayCommand BulkTrashCommand { get; private set; } = null!;
    public RelayCommand BulkRestoreCommand { get; private set; } = null!;
    public RelayCommand BulkDeleteCommand { get; private set; } = null!;
    public RelayCommand BulkTogglePinCommand { get; private set; } = null!;
    public RelayCommand RestoreBackupCommand { get; private set; } = null!;
    public RelayCommand ClearDebugLogCommand { get; private set; } = null!;
    public RelayCommand GoogleDriveCommand { get; private set; } = null!;
    public RelayCommand SyncGoogleDriveNowCommand { get; private set; } = null!;

    public bool IsGoogleDriveConnected => _googleDrive.IsAuthenticated;

    public string GoogleDriveStatusTooltip => _googleDrive.IsAuthenticated
        ? $"Google Drive: Connected ({_settings.GoogleDriveAccountEmail ?? "Authorized"})\nLast synced: {(_settings.LastGoogleDriveSyncTime.HasValue ? _settings.LastGoogleDriveSyncTime.Value.ToString("g") : "Never")}"
        : "Google Drive: Disconnected (Click to configure)";

    public event Action? FocusTitleRequested;

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        _isDarkTheme = _settings.Theme == "Dark";
        _isSidebarCollapsed = _settings.SidebarCollapsed;
        ThemeService.Apply(_settings.Theme);
        AppLogger.IsVerbose = _settings.IsVerboseLogging;

        SidebarItems.Add(_allItem);
        SidebarItems.Add(_recurringItem);
        SidebarItems.Add(_doneItem);
        SidebarItems.Add(_trashItem);
        _selectedSidebarItem = _allItem;

        FilteredTasksView = new ListCollectionView(AllTasks) { Filter = FilterTask, CustomSort = new TaskComparer(_currentSort) };

        var initialPath = ResolveInitialFilePath();
        _attachments = new AttachmentService(initialPath);

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounceTimer.Tick += (_, _) => CommitSave();

        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();

        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoSyncTimer.Tick += async (_, _) =>
        {
            _autoSyncTimer.Stop();
            if (_settings.IsGoogleDriveEnabled && _googleDrive.IsAuthenticated)
            {
                AppLogger.Info("MainViewModel", "Triggering debounced background auto-sync to Google Drive...");
                await PerformGoogleDriveSyncAsync(isSilentOnExit: true);
            }
        };

        // Everything above only pulls in another device's changes as a side effect of this
        // device also saving something - two idle devices just sitting there never notice each
        // other's edits. This timer pulls (and pushes) on its own fixed cadence regardless of
        // local activity, so a change made elsewhere shows up here without the user having to
        // touch anything first.
        _idleSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _idleSyncTimer.Tick += async (_, _) =>
        {
            if (_settings.IsGoogleDriveEnabled && _googleDrive.IsAuthenticated)
            {
                AppLogger.Info("MainViewModel", "Triggering periodic idle Google Drive sync...");
                await PerformGoogleDriveSyncAsync(isSilentOnExit: true);
            }
        };
        _idleSyncTimer.Start();

        InitializeTaskCommands();
        InitializeViewCommands();
        InitializeFileCommands();
        InitializeBulkCommands();

        LoadFile(initialPath, restoreSelection: true);
        CheckReminders();

        if (_settings.IsGoogleDriveEnabled)
        {
            Task.Run(async () =>
            {
                var authed = await _googleDrive.TrySilentAuthenticateAsync(_settings.GoogleDriveClientId, _settings.GoogleDriveClientSecret);
                if (authed)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        OnPropertyChanged(nameof(IsGoogleDriveConnected));
                        OnPropertyChanged(nameof(GoogleDriveStatusTooltip));

                        // Pull in whatever changed elsewhere since this device was last open,
                        // instead of only finding out once the user edits something here first.
                        AppLogger.Info("MainViewModel", "Syncing with Google Drive on startup...");
                        _ = PerformGoogleDriveSyncAsync(isSilentOnExit: true);
                    });
                }
            });
        }
    }

    // Everything that acts on the single SelectedTask (or the ambient single/multi TargetTasks()
    // selection), as opposed to the multi-select-only Bulk* commands in InitializeBulkCommands.
    private void InitializeTaskCommands()
    {
        AddTaskCommand = new RelayCommand(_ =>
        {
            var task = new TaskItem { Text = "New Task" };
            AllTasks.Add(task);
            AttachTask(task);
            OnTaskChanged();
            SelectedTask = task;
            FocusTitleRequested?.Invoke();
        });

        ToggleCloseSelectedCommand = new RelayCommand(_ =>
        {
            if (SelectedTask is null) return;
            var task = SelectedTask;
            var wasClosed = task.IsClosed;
            task.IsClosed = !wasClosed;
            if (!wasClosed)
                PushUndo($"Move \"{task.Text}\" to Trash", () => task.IsClosed = false);
        }, _ => SelectedTask is not null);

        DeleteSelectedCommand = new RelayCommand(_ =>
        {
            var targets = TargetTasks();
            if (targets.Count == 0) return;

            var message = targets.Count == 1
                ? $"Delete \"{targets[0].Text}\" permanently? This also removes its photos and attachments."
                : $"Delete {targets.Count} tasks permanently? This also removes their photos and attachments.";
            var result = ThemedMessageBox.Show(message, "Delete Task", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var task in targets)
            {
                DetachTask(task);
                AllTasks.Remove(task);
                RecordTaskDeletionTombstone(task);
                CleanupTaskAttachments(task);
            }
            SelectedTask = null;
            OnTaskChanged();
        }, _ => SelectedTask is not null || SelectedTasks.Count > 0);

        TrashAllClosedCommand = new RelayCommand(_ =>
        {
            var closed = AllTasks.Where(t => !t.IsClosed && t.IsDone).ToList();
            if (closed.Count == 0) return;

            var result = ThemedMessageBox.Show($"Move {closed.Count} closed task(s) to Trash?",
                "Move to Trash", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            foreach (var task in closed)
            {
                if (SelectedTask == task) SelectedTask = null;
                task.IsClosed = true;
            }

            PushUndo($"Move {closed.Count} task(s) to Trash", () =>
            {
                foreach (var task in closed) task.IsClosed = false;
            });
        });

        TogglePinCommand = new RelayCommand(p =>
        {
            if (p is TaskItem task) task.IsPinned = !task.IsPinned;
        });

        EmptyTrashCommand = new RelayCommand(_ =>
        {
            var trashed = AllTasks.Where(t => t.IsClosed).ToList();
            if (trashed.Count == 0) return;

            var result = ThemedMessageBox.Show($"Permanently delete {trashed.Count} task(s) in Trash? This also removes their photos and attachments.",
                "Empty Trash", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var task in trashed)
            {
                DetachTask(task);
                AllTasks.Remove(task);
                RecordTaskDeletionTombstone(task);
                CleanupTaskAttachments(task);
                if (SelectedTask == task) SelectedTask = null;
            }
            OnTaskChanged();
        });
    }

    // Sidebar scope switching, sort, quick filter, and layout toggles - commands that change
    // what's visible or how it's arranged, rather than mutating any task.
    private void InitializeViewCommands()
    {
        ShowAllCommand = new RelayCommand(_ => SelectedSidebarItem = _allItem);
        ShowClosedCommand = new RelayCommand(_ => SelectedSidebarItem = _doneItem);
        ShowTrashCommand = new RelayCommand(_ => SelectedSidebarItem = _trashItem);

        ToggleFocusModeCommand = new RelayCommand(_ => IsFocusMode = !IsFocusMode);
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);

        SetSortCommand = new RelayCommand(p =>
        {
            if (p is SortOption option) CurrentSort = option;
        });

        SetQuickFilterCommand = new RelayCommand(p =>
        {
            if (p is QuickFilter filter) CurrentQuickFilter = filter;
            IsFilterPopupOpen = false;
        });

        ToggleFilterPopupCommand = new RelayCommand(_ => IsFilterPopupOpen = !IsFilterPopupOpen);
    }

    // New/Open/Save As/Restore Backup - commands that swap out which .tasky file is open or
    // touch the file on disk directly, rather than mutating in-memory task state.
    private void InitializeFileCommands()
    {
        NewFileCommand = new RelayCommand(_ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "New Tasky File",
                Filter = "Tasky files (*.tasky)|*.tasky",
                FileName = "Tasky.tasky"
            };
            if (dialog.ShowDialog() != true) return;

            _store.Save(new AppState(), dialog.FileName);
            LoadFile(dialog.FileName);
        });

        OpenFileCommand = new RelayCommand(_ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open Tasky File",
                Filter = "Tasky files (*.tasky)|*.tasky|JSON files (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            LoadFile(dialog.FileName);
        });

        SaveFileAsCommand = new RelayCommand(_ =>
        {
            FlushPendingSave();
            var dialog = new SaveFileDialog
            {
                Title = "Save Tasky File As",
                Filter = "Tasky files (*.tasky)|*.tasky",
                FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + ".tasky"
            };
            if (dialog.ShowDialog() != true) return;

            _currentFilePath = dialog.FileName;
            _attachments.SetDataFilePath(_currentFilePath);
            _store.Save(_state, _currentFilePath);
            OnPropertyChanged(nameof(WindowTitle));

            _settings.LastFilePath = _currentFilePath;
            _settingsStore.Save(_settings);
        });

        RestoreBackupCommand = new RelayCommand(async _ =>
        {
            // Wrapping an async lambda in RelayCommand's Action<object?> makes this effectively
            // async void - CanExecute below is what actually stops a second invocation from
            // re-entering RestoreBackup/LoadFile while the first is still awaiting.
            _isRestoringBackup = true;
            try
            {
                // Unlike the other FlushPendingSave() call sites, this one genuinely needs the disk
                // write to have landed before RestoreBackup overwrites the file out from under it -
                // await the real completion instead of just firing it off.
                await FlushPendingSaveAsync();
                var backups = _store.ListBackups(_currentFilePath);
                if (backups.Count == 0)
                {
                    ThemedMessageBox.Show("No backups found for this file yet.", "Restore from Backup",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var picker = new RestoreBackupWindow(backups) { Owner = Application.Current.MainWindow };
                if (picker.ShowDialog() != true || picker.SelectedBackup is null) return;

                var confirm = ThemedMessageBox.Show(
                    $"Restore the backup from {picker.SelectedBackup.Timestamp:MMM d, yyyy 'at' h:mm:ss tt}?\n\n" +
                    "Your current file will be backed up first, so this can be undone by restoring again.",
                    "Restore from Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                _store.RestoreBackup(picker.SelectedBackup.FilePath, _currentFilePath);
                LoadFile(_currentFilePath);
            }
            finally
            {
                _isRestoringBackup = false;
            }
        }, _ => !_isRestoringBackup);

        ClearDebugLogCommand = new RelayCommand(_ =>
        {
            var confirm = ThemedMessageBox.Show(
                "Are you sure you want to clear the debug log file?\n\nExisting entries will be truncated and a fresh log will be started.",
                "Clear Debug Log", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            AppLogger.ClearLogFile();
            ThemedMessageBox.Show("Debug log file has been cleared.", "Debug Log", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        GoogleDriveCommand = new RelayCommand(_ => OpenGoogleDriveWindow());

        SyncGoogleDriveNowCommand = new RelayCommand(async _ => await PerformGoogleDriveSyncAsync());
    }

    private void OpenGoogleDriveWindow()
    {
        var window = new GoogleDriveWindow(_googleDrive, _settings, _settingsStore, () => PerformGoogleDriveSyncAsync())
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        OnPropertyChanged(nameof(IsGoogleDriveConnected));
        OnPropertyChanged(nameof(GoogleDriveStatusTooltip));
    }

    public async Task PerformGoogleDriveSyncAsync(bool isSilentOnExit = false)
    {
        if (!_googleDrive.IsAuthenticated)
        {
            if (!isSilentOnExit) OpenGoogleDriveWindow();
            return;
        }

        // Now that sync can be triggered by three independent, unrelated timers (edit-debounce,
        // idle, and the one-shot startup sync) plus manual "Sync Now" and exit, two of them can
        // land close enough together to overlap - e.g. the idle timer fires right as an edit's
        // debounced sync also kicks off. Overlapping runs would race on the same local file and
        // remote state, so only one is allowed to actually run at a time; the others no-op and
        // whichever trigger fires next will just pick up the same work.
        if (_syncInProgress) return;
        _syncInProgress = true;

        try
        {
            SaveStatusText = "Syncing with Google Drive...";
            await FlushPendingSaveAsync();

            var remoteId = _settings.GoogleDriveFileId;

            // This device has never linked to a remote file before (first-ever connect, or
            // reconnect after a disconnect) - resolve whether one already exists on Drive by
            // name before deciding what to do. Once a file ID is cached there's no need to touch
            // the "Tasky" folder lookup at all on this path - UploadFileAsync resolves (and
            // caches) it separately when it actually needs it.
            if (string.IsNullOrEmpty(remoteId))
            {
                var taskyFolderId = _settings.GoogleDriveFolderId;
                if (string.IsNullOrEmpty(taskyFolderId))
                {
                    taskyFolderId = await _googleDrive.GetOrCreateFolderAsync("Tasky");
                    _settings.GoogleDriveFolderId = taskyFolderId;
                }
                remoteId = await _googleDrive.FindExistingFileIdAsync(Path.GetFileName(_currentFilePath), taskyFolderId);
                if (!string.IsNullOrEmpty(remoteId))
                    _settings.GoogleDriveFileId = remoteId;
            }

            // A brand-new or just-emptied data file is never written to disk until the first
            // real edit triggers a save - FlushPendingSaveAsync only flushes an edit that's
            // already pending, so it's a no-op here and UploadFileAsync would otherwise throw
            // FileNotFoundException trying to read a file that only ever existed in memory.
            if (!File.Exists(_currentFilePath))
                await _store.SaveAsync(_state, _currentFilePath);

            if (!string.IsNullOrEmpty(remoteId))
            {
                // A remote file already exists - merge it into local rather than guessing which
                // whole file is "newer." A device that's behind just adopts what's new, a device
                // with its own new tasks keeps them, and deletions propagate via tombstones
                // instead of a device that hasn't pulled a delete yet resurrecting it. See
                // MergeRemoteState for the one case this can't fully reconcile (the same task
                // edited on two devices since they last agreed).
                //
                // A remote file that's empty or not valid JSON (an interrupted upload, or a
                // leftover from some earlier failure) must not abort the whole sync - there's
                // nothing usable to merge, but local's own state is still good, so fall through
                // and let it upload normally rather than leaving the device stuck retrying a
                // sync that can never succeed against a file it can't read.
                var tempPath = Path.Combine(Path.GetTempPath(), $"tasky_remote_{Guid.NewGuid():N}.tasky");
                try
                {
                    await _googleDrive.DownloadFileAsync(remoteId, tempPath, downloadAttachments: false);
                    var remoteState = await _store.LoadAsync(tempPath);
                    remoteState.DeletedTasks = DeduplicateTombstones(remoteState.DeletedTasks);
                    var (added, updated, removed) = MergeRemoteState(remoteState);
                    AppLogger.Info("MainViewModel", $"Google Drive merge: +{added} task(s), ~{updated} updated, -{removed} removed.");

                    RefreshTags();
                    FilteredTasksView.Refresh();
                    await _store.SaveAsync(_state, _currentFilePath);
                }
                catch (InvalidDataException ex)
                {
                    AppLogger.Warn("MainViewModel", $"Remote Google Drive file '{remoteId}' isn't readable ({ex.Message}) - skipping merge and uploading local state as-is.");
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { }
                }
            }

            // Upload the merged (or, on a first-ever sync anywhere, simply local) result.
            var newRemoteId = await _googleDrive.UploadFileAsync(_currentFilePath, remoteId, _settings, _settingsStore);
            _settings.GoogleDriveFileId = newRemoteId;
            _settings.LastGoogleDriveSyncTime = DateTime.Now;
            _settingsStore.Save(_settings);

            SaveStatusText = "Successfully synced to Google Drive.";
            OnPropertyChanged(nameof(GoogleDriveStatusTooltip));
            OnPropertyChanged(nameof(IsGoogleDriveConnected));
        }
        catch (Google.GoogleApiException gEx) when (gEx.Message.Contains("disabled") || gEx.Message.Contains("has not been used"))
        {
            SaveStatusText = "Google Drive API is disabled in your Google Cloud Console.";
            AppLogger.Error("MainViewModel", "Google Drive API disabled", gEx);
            if (!isSilentOnExit)
            {
                ThemedMessageBox.Show(
                    "Google Drive API is disabled in your Google Cloud Console project.\n\n" +
                    "Please click below to open Google Cloud Console and click 'ENABLE' on the Google Drive API page:\n" +
                    "https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=395690152006",
                    "Google Drive API Disabled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            SaveStatusText = $"Google Drive sync failed: {ex.Message}";
            AppLogger.Error("MainViewModel", "Google Drive sync error", ex);
            if (!isSilentOnExit)
            {
                ThemedMessageBox.Show($"Google Drive sync failed:\n{ex.Message}", "Google Drive Sync Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _syncInProgress = false;
        }
    }

    // Undo, and every Bulk* command driven by the task list's multi-selection (SelectedTasks)
    // rather than the single SelectedTask.
    private void InitializeBulkCommands()
    {
        UndoCommand = new RelayCommand(_ =>
        {
            if (_undoStack.Count == 0) return;
            var (_, undo) = _undoStack.Last!.Value;
            _undoStack.RemoveLast();
            OnPropertyChanged(nameof(UndoMenuLabel));
            undo();
        }, _ => _undoStack.Count > 0);

        BulkMarkDoneCommand = new RelayCommand(_ =>
        {
            foreach (var t in SelectedTasks) t.IsDone = true;
        }, _ => SelectedTasks.Count > 0);

        BulkTrashCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.Where(t => !t.IsClosed).ToList();
            if (targets.Count == 0) return;
            foreach (var t in targets) t.IsClosed = true;
            PushUndo($"Move {targets.Count} task(s) to Trash", () =>
            {
                foreach (var t in targets) t.IsClosed = false;
            });
        }, _ => SelectedTasks.Count > 0);

        BulkRestoreCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.Where(t => t.IsClosed).ToList();
            if (targets.Count == 0) return;
            foreach (var t in targets) t.IsClosed = false;
            PushUndo($"Restore {targets.Count} task(s) from Trash", () =>
            {
                foreach (var t in targets) t.IsClosed = true;
            });
        }, _ => SelectedTasks.Count > 0);

        BulkDeleteCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Delete {targets.Count} task(s) permanently? This also removes their photos and attachments.",
                "Delete Tasks", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var task in targets)
            {
                DetachTask(task);
                AllTasks.Remove(task);
                RecordTaskDeletionTombstone(task);
                CleanupTaskAttachments(task);
                if (SelectedTask == task) SelectedTask = null;
            }
            OnTaskChanged();
        }, _ => SelectedTasks.Count > 0);

        BulkTogglePinCommand = new RelayCommand(_ =>
        {
            foreach (var t in SelectedTasks) t.IsPinned = !t.IsPinned;
        }, _ => SelectedTasks.Count > 0);
    }

    // Permanent delete has no undo path (unlike Move to Trash), so a tombstone recorded here
    // never needs to be retracted. Without this, Google Drive's per-task merge would have no way
    // to tell "a device deleted this task" apart from "a device just hasn't pulled this task
    // down yet" - both look identical (missing from that device's list) without a record of which
    // task IDs were actually deleted and when.
    //
    // A task can legitimately be tombstoned more than once in its lifetime - delete, then a later
    // edit on another device revives it (an intentional part of the merge - see MergeRemoteState),
    // then it gets deleted again. Update the existing tombstone's timestamp instead of appending a
    // second one for the same TaskId: MergeRemoteState builds a Dictionary keyed by TaskId from
    // this list, which throws on a duplicate key - a second entry wouldn't just pick the "wrong"
    // timestamp, it would crash the sync outright, and keep crashing on every retry.
    private void RecordTaskDeletionTombstone(TaskItem task)
    {
        var existing = _state.DeletedTasks.FirstOrDefault(r => r.TaskId == task.Id);
        if (existing is not null)
            existing.Timestamp = DateTime.Now;
        else
            _state.DeletedTasks.Add(new TaskSyncRecord { TaskId = task.Id, Timestamp = DateTime.Now });
    }

    // Belt-and-suspenders for data written before RecordTaskDeletionTombstone deduplicated on
    // write (or any other source of a malformed file, e.g. hand-edited) - MergeRemoteState builds
    // a Dictionary keyed by TaskId from this list, which throws on a duplicate key, so a file
    // that already has one has to be cleaned up before it ever reaches that point. Keeps the
    // latest timestamp per TaskId, applied to both local (on load) and remote (right after
    // download) so neither side can be the one that crashes the merge.
    private static List<TaskSyncRecord> DeduplicateTombstones(List<TaskSyncRecord> tombstones)
    {
        return tombstones
            .GroupBy(r => r.TaskId)
            .Select(g => g.OrderByDescending(r => r.Timestamp).First())
            .ToList();
    }

    // Per-task merge for Google Drive sync, replacing a whole-file "which copy is newer" guess.
    // The whole-file version couldn't tell "clean update from another device" apart from "real
    // conflict" without misfiring (that's the story of this session's last several bug reports),
    // because a single timestamp on the whole file conflates every task's history into one
    // number. Per task, the picture is much clearer: a task missing from one side either belongs
    // there (bring it in) or was deleted (a tombstone says when, so a device that's simply behind
    // doesn't resurrect it) - and a task edited on only one side since they last agreed is always
    // safe to adopt. The one thing this still can't fully reconcile is the same task edited on
    // both sides in the same window - that's genuine field-level merging (CRDT territory), out of
    // scope here, so it falls back to keeping whichever edit has the later ModifiedAt and the
    // other edit to that specific task is lost. Narrower and rarer than losing an entire list.
    private (int Added, int Updated, int Removed) MergeRemoteState(AppState remoteState)
    {
        var localById = _state.Tasks.ToDictionary(t => t.Id);
        var remoteById = remoteState.Tasks.ToDictionary(t => t.Id);
        var localTombstones = _state.DeletedTasks.ToDictionary(r => r.TaskId, r => r.Timestamp);
        var remoteTombstones = remoteState.DeletedTasks.ToDictionary(r => r.TaskId, r => r.Timestamp);

        var added = 0;
        var updated = 0;
        var removed = 0;

        // Remote-only tasks: bring them in, unless this device already deleted the same ID and
        // remote's copy predates that deletion (i.e. remote just hasn't learned about it yet).
        foreach (var (id, remoteTask) in remoteById)
        {
            if (localById.ContainsKey(id)) continue;

            if (localTombstones.TryGetValue(id, out var deletedAt) && remoteTask.ModifiedAt <= deletedAt)
                continue;

            AllTasks.Add(remoteTask);
            AttachTask(remoteTask);
            added++;
        }

        // Local-only tasks: leave them (they'll upload as-is), unless another device already
        // deleted the same ID and this device hasn't touched it since that deletion.
        foreach (var (id, localTask) in localById)
        {
            if (remoteById.ContainsKey(id)) continue;

            if (remoteTombstones.TryGetValue(id, out var deletedAt) && localTask.ModifiedAt <= deletedAt)
            {
                DetachTask(localTask);
                AllTasks.Remove(localTask);
                if (SelectedTask == localTask) SelectedTask = null;
                removed++;
            }
        }

        // Present on both sides: the newer edit to THIS task wins - detached first, since
        // TaskItem's property setters trigger Task_PropertyChanged while attached, which would
        // stamp ModifiedAt to "now" (clobbering the timestamp being restored here) and can spawn
        // a recurring-task occurrence or push an undo entry - none of which belong in a sync merge.
        foreach (var (id, remoteTask) in remoteById)
        {
            if (!localById.TryGetValue(id, out var localTask)) continue;
            if (remoteTask.ModifiedAt <= localTask.ModifiedAt) continue;

            DetachTask(localTask);
            ApplyTaskFields(localTask, remoteTask);
            AttachTask(localTask);
            updated++;
        }

        // Tombstones union both ways, so a third device merging later learns about every
        // deletion recorded anywhere, not just the ones this device made itself.
        var mergedTombstoneIds = new HashSet<Guid>(_state.DeletedTasks.Select(r => r.TaskId));
        foreach (var remoteTombstone in remoteState.DeletedTasks)
        {
            if (mergedTombstoneIds.Add(remoteTombstone.TaskId))
                _state.DeletedTasks.Add(remoteTombstone);
        }

        return (added, updated, removed);
    }

    private static void ApplyTaskFields(TaskItem target, TaskItem source)
    {
        target.Text = source.Text;
        target.IsDone = source.IsDone;
        target.IsClosed = source.IsClosed;
        target.IsPinned = source.IsPinned;
        target.DueDate = source.DueDate;
        target.Recurrence = source.Recurrence;

        target.Tags.Clear();
        foreach (var tag in source.Tags) target.Tags.Add(tag);

        target.Body.Clear();
        foreach (var block in source.Body) target.Body.Add(block);

        target.ModifiedAt = source.ModifiedAt;
    }

    private void CleanupTaskAttachments(TaskItem deletedTask)
    {
        CleanupTaskAttachments(new[] { deletedTask });
    }

    private void CleanupTaskAttachments(IEnumerable<TaskItem> deletedTasks)
    {
        try
        {
            var deletedTaskList = deletedTasks.ToList();
            if (deletedTaskList.Count == 0) return;

            // 1. Extract referenced media filenames from deleted tasks
            var deletedMediaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in deletedTaskList)
            {
                ExtractTaskMediaFilenames(task, deletedMediaFiles);
            }

            if (deletedMediaFiles.Count == 0) return;

            // 2. Extract referenced media filenames from all remaining tasks
            var remainingTasks = AllTasks.Except(deletedTaskList);
            var remainingMediaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in remainingTasks)
            {
                ExtractTaskMediaFilenames(task, remainingMediaFiles);
            }

            // 3. Delete orphaned local attachment and inline image files
            var baseDocDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");
            var attachmentsDir = Path.Combine(baseDocDir, "Attachments");
            var inlineImagesDir = Path.Combine(baseDocDir, "InlineImages");

            foreach (var fileName in deletedMediaFiles)
            {
                if (!remainingMediaFiles.Contains(fileName))
                {
                    // Check Attachments folder
                    var attPath = Path.Combine(attachmentsDir, fileName);
                    if (File.Exists(attPath))
                    {
                        try
                        {
                            File.Delete(attPath);
                            AppLogger.Info("MainViewModel", $"Deleted orphaned local attachment file: '{attPath}'");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("MainViewModel", $"Failed to delete local attachment '{attPath}': {ex.Message}");
                        }
                    }

                    // Check InlineImages folder
                    var imgPath = Path.Combine(inlineImagesDir, fileName);
                    if (File.Exists(imgPath))
                    {
                        try
                        {
                            File.Delete(imgPath);
                            AppLogger.Info("MainViewModel", $"Deleted orphaned local inline image file: '{imgPath}'");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("MainViewModel", $"Failed to delete local inline image '{imgPath}': {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainViewModel", "Failed to cleanup task attachments", ex);
        }
    }

    private static void ExtractTaskMediaFilenames(TaskItem task, HashSet<string> set)
    {
        if (task.Body is null) return;
        foreach (var block in task.Body)
        {
            if (!string.IsNullOrEmpty(block.PhotoPath))
            {
                var pName = Path.GetFileName(block.PhotoPath);
                if (!string.IsNullOrEmpty(pName)) set.Add(pName);
            }
            if (!string.IsNullOrEmpty(block.Rtf))
            {
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(block.Rtf, @"[a-zA-Z0-9_\-]{3,}\.(png|jpg|jpeg|gif|bmp|pdf|docx|xlsx|zip|txt)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    set.Add(match.Value);
                }
            }
            if (!string.IsNullOrEmpty(block.Text))
            {
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(block.Text, @"[a-zA-Z0-9_\-]{3,}\.(png|jpg|jpeg|gif|bmp|pdf|docx|xlsx|zip|txt)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    set.Add(match.Value);
                }
            }
        }
    }

    public void AddQuickTask(string title)
    {
        var task = new TaskItem { Text = title };
        AllTasks.Add(task);
        AttachTask(task);
        OnTaskChanged();
    }

    public void SaveWindowState(double left, double top, double width, double height, bool maximized)
    {
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowMaximized = maximized;
        if (SelectedTask is { } task) _settings.LastSelectedTaskId = task.Id.ToString();
        _settingsStore.Save(_settings);
    }

    public void Shutdown() => _tray.Dispose();

    public void UpdateSelectedTasks(IEnumerable<TaskItem> tasks)
    {
        SelectedTasks = tasks.ToList();
        OnPropertyChanged(nameof(SelectedTasks));
    }

    private List<TaskItem> TargetTasks()
    {
        if (SelectedTasks.Count > 1) return SelectedTasks.ToList();
        return SelectedTask is not null ? new List<TaskItem> { SelectedTask } : new List<TaskItem>();
    }

    private void PushUndo(string description, Action undo)
    {
        _undoStack.AddLast((description, undo));
        if (_undoStack.Count > MaxUndoDepth)
            _undoStack.RemoveFirst();
        OnPropertyChanged(nameof(UndoMenuLabel));
    }

    private void CheckReminders()
    {
        // Prevent overlapping reminder checks if the previous check is still running
        if (_reminderCheckInProgress || !RemindersEnabled) return;
        
        _reminderCheckInProgress = true;
        try
        {
            var today = DateTime.Today;
        var due = AllTasks.Where(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && t.DueDate.Value.Date <= today
                                       && !_notifiedTaskIds.Contains(t.Id))
            .ToList();
        if (due.Count == 0) return;

        foreach (var t in due) _notifiedTaskIds.Add(t.Id);

            if (due.Count == 1)
                _tray.ShowBalloon("Task due", due[0].Text);
            else
                _tray.ShowBalloon("Tasks due", $"{due.Count} tasks are due or overdue.");
        }
        finally
        {
            _reminderCheckInProgress = false;
        }
    }

    private static DateTime NextDueDate(DateTime from, RecurrenceRule rule) => rule switch
    {
        RecurrenceRule.Daily => from.AddDays(1),
        RecurrenceRule.Weekly => from.AddDays(7),
        RecurrenceRule.Monthly => from.AddMonths(1),
        RecurrenceRule.Yearly => from.AddYears(1),
        _ => from
    };

    // Completing a recurring task doesn't just close it out - it spawns the next occurrence
    // (title, due date advanced by the rule, tags) so the series continues. The completed
    // instance still moves into Closed as normal.
    private TaskItem SpawnNextOccurrence(TaskItem completed)
    {
        var next = new TaskItem
        {
            Text = completed.Text,
            DueDate = NextDueDate(completed.DueDate ?? DateTime.Today, completed.Recurrence),
            Recurrence = completed.Recurrence,
            Tags = new ObservableCollection<string>(completed.Tags)
        };
        AllTasks.Add(next);
        AttachTask(next);
        return next;
    }

    // Determines which file to open on startup: the last file the user had open, otherwise the
    // default Documents location (a missing file there just means a fresh, blank AppState - see
    // TodoStore.Load). The one-time migration off the old fixed AppData location happened long
    // enough ago in this app's life that keeping it live was actively harmful: it meant a
    // deliberately-deleted default file would silently come back populated with whatever stale
    // data happened to still be sitting in that old AppData location, instead of actually
    // starting fresh.
    private string ResolveInitialFilePath()
    {
        if (_settings.LastFilePath is { } last && File.Exists(last))
            return last;

        return TodoStore.GetDefaultDataFilePath();
    }

    private void LoadFile(string path, bool restoreSelection = false)
    {
        AppLogger.Info("MainViewModel", $"LoadFile: Loading file '{path}' (restoreSelection={restoreSelection})");
        FlushPendingSave();

        foreach (var task in AllTasks)
            DetachTask(task);
        AllTasks.Clear();
        _undoStack.Clear();
        _notifiedTaskIds.Clear();
        OnPropertyChanged(nameof(UndoMenuLabel));

        _currentFilePath = path;
        _attachments.SetDataFilePath(path);

        // _state is never reassigned (see its declaration) - AllTasks and FilteredTasksView both
        // wrap _state.Tasks by reference, so opening a different file means repopulating that
        // same collection in place from a freshly-loaded AppState, not swapping _state itself out
        // for a new one (which would leave FilteredTasksView pointed at the old, now-orphaned
        // collection).
        var loaded = _store.Load(path);
        foreach (var task in loaded.Tasks)
        {
            AllTasks.Add(task);
            AttachTask(task);
        }

        // DeletedTasks isn't bound to any UI collection (unlike Tasks/AllTasks), so a plain
        // reassignment is safe here - but it still has to happen, or a tombstone written to disk
        // by a previous session stays invisible to Google Drive's merge (which only ever
        // consults the in-memory _state.DeletedTasks), letting a deleted task get silently
        // resurrected on the next sync.
        _state.DeletedTasks = DeduplicateTombstones(loaded.DeletedTasks);

        AppLogger.Info("MainViewModel", $"LoadFile: Loaded {loaded.Tasks.Count} tasks into AllTasks");

        SelectedTask = null;
        SelectedSidebarItem = _allItem;
        RefreshTags();
        FilteredTasksView.Refresh();
        OnPropertyChanged(nameof(WindowTitle));

        _settings.LastFilePath = path;
        _settingsStore.Save(_settings);

        if (restoreSelection && _settings.LastSelectedTaskId is { } lastId && Guid.TryParse(lastId, out var guid))
        {
            var match = AllTasks.FirstOrDefault(t => t.Id == guid);
            if (match is not null) SelectedTask = match;
        }
    }

    private bool FilterTask(object o)
    {
        var t = (TaskItem)o;
        var scope = _selectedSidebarItem ?? _allItem;

        var matchesScope = scope.Kind switch
        {
            SidebarFilterKind.Trash => t.IsClosed,
            SidebarFilterKind.Done => !t.IsClosed && t.IsDone,
            SidebarFilterKind.Recurring => !t.IsClosed && !t.IsDone && t.Recurrence != RecurrenceRule.None,
            // Unlike "All Tasks", a tag view includes completed tasks (just not trashed ones) -
            // otherwise a tag whose only remaining task happened to be completed looked
            // completely empty, with nothing telling you the task still exists under Completed.
            SidebarFilterKind.Tag => !t.IsClosed && t.Tags.Any(tag => tag.Equals(scope.TagName, StringComparison.OrdinalIgnoreCase)),
            _ => !t.IsClosed && !t.IsDone
        };
        if (!matchesScope) return false;
        if (!MatchesQuickFilter(t)) return false;

        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var q = _searchText.Trim();

        if (q.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tagQuery = q[4..].Trim();
            return tagQuery.Length == 0 || t.Tags.Any(tag => Contains(tag, tagQuery));
        }

        return Contains(t.Text, q)
            || t.Body.Any(b => Contains(b.Text, q))
            || t.Tags.Any(tag => Contains(tag, q));
    }

    private bool MatchesQuickFilter(TaskItem t) => _currentQuickFilter switch
    {
        QuickFilter.Overdue => t.DueDate.HasValue && !t.IsDone && t.DueDate.Value.Date < DateTime.Today,
        QuickFilter.DueToday => t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today,
        QuickFilter.NoDueDate => !t.DueDate.HasValue,
        QuickFilter.Recurring => t.Recurrence != RecurrenceRule.None,
        QuickFilter.HasLink => TaskMediaHelper.HasLink(t),
        QuickFilter.HasAttachment => TaskMediaHelper.HasAttachment(t) || TaskMediaHelper.HasPhoto(t),
        _ => true
    };

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void AttachTask(TaskItem task) => task.PropertyChanged += Task_PropertyChanged;

    private void DetachTask(TaskItem task) => task.PropertyChanged -= Task_PropertyChanged;

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskItem.ModifiedAt)) return;
        if (sender is not TaskItem task) return;
        task.ModifiedAt = DateTime.Now;

        if (e.PropertyName == nameof(TaskItem.IsDone) && !_isExecutingUndo)
        {
            var isDone = task.IsDone;
            TaskItem? spawned = null;
            if (isDone && task.Recurrence != RecurrenceRule.None)
                spawned = SpawnNextOccurrence(task);

            PushUndo(isDone ? $"Mark \"{task.Text}\" complete" : $"Mark \"{task.Text}\" incomplete", () =>
            {
                _isExecutingUndo = true;
                try
                {
                    task.IsDone = !isDone;
                    if (spawned is not null)
                    {
                        DetachTask(spawned);
                        AllTasks.Remove(spawned);
                        OnTaskChanged();
                    }
                }
                finally
                {
                    _isExecutingUndo = false;
                }
            });
        }

        // Typing the title fires on every keystroke; debounce it like body text instead of
        // writing the file (and re-sorting/re-filtering the list) on every character.
        if (e.PropertyName == nameof(TaskItem.Text))
            RequestDebouncedSave();
        else
            OnTaskChanged();
    }

    private void RequestDebouncedSave()
    {
        SaveStatusText = "Saving…";
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void CommitSave()
    {
        _saveDebounceTimer.Stop();
        OnTaskChanged();
    }

    // Call before anything that would otherwise lose the last few seconds of debounced typing:
    // switching tasks, switching files, or closing the app. This only guarantees the pending edit
    // has been HANDED OFF to a save (in-memory state is already current the instant CommitSave
    // runs) - it does not wait for that save to land on disk. That's fine for callers that only
    // care about in-memory state (e.g. switching the selected task); callers that need the disk
    // write itself to have finished (restoring a backup, closing the app) should use
    // FlushPendingSaveAsync instead.
    public void FlushPendingSave()
    {
        if (_saveDebounceTimer.IsEnabled)
            CommitSave();
    }

    public async Task FlushPendingSaveAsync()
    {
        if (_saveDebounceTimer.IsEnabled)
            CommitSave();
        await _pendingSaveTask;
    }

    public void OnTaskChanged()
    {
        Save();
        RefreshTags();
        FilteredTasksView.Refresh();
    }

    // Diffs TagItems in place instead of Clear()-then-rebuild. Clear() raises a Reset
    // notification, which the bound Tags ListBox treats as "the whole collection is gone" and
    // drops its current selection outright - even when the selected tag is still in the rebuilt
    // list, just as a new instance. Since RefreshTags runs on nearly every task edit anywhere in
    // the app (via OnTaskChanged), that silently snapped any active tag filter back to "All
    // Tasks" on almost every keystroke. Only actually-added/removed tags now touch the
    // collection, so an unrelated edit leaves the current selection's item identity untouched.
    private void RefreshTags()
    {
        // Trashed tasks don't keep a tag alive in the sidebar - otherwise trashing the last task
        // with a given tag left that tag sitting in the list pointing at a Tag view that (by
        // design, matching Trash being a separate bucket from every other filter) would always
        // show zero results, with nothing to explain why.
        var desired = AllTasks.Where(t => !t.IsClosed).SelectMany(t => t.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Remove tags no longer in use (use HashSet for O(1) lookup instead of O(n) Contains)
        var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        for (var i = TagItems.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(TagItems[i].TagName ?? ""))
                TagItems.RemoveAt(i);

        // Add new tags
        var existingSet = new HashSet<string>(
            TagItems.Select(t => t.TagName ?? "").Where(t => t != ""), 
            StringComparer.OrdinalIgnoreCase);
        foreach (var tag in desired)
            if (!existingSet.Contains(tag))
                TagItems.Add(new SidebarFilterItem(tag));

        // Reorder tags efficiently with O(n) using dictionary lookup instead of O(n²) IndexOf
        var ordered = TagItems.OrderBy(t => t.TagName, StringComparer.OrdinalIgnoreCase).ToList();
        var currentPositions = new Dictionary<SidebarFilterItem, int>(TagItems.Count);
        for (var i = 0; i < TagItems.Count; i++)
            currentPositions[TagItems[i]] = i;
        
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (currentPositions.TryGetValue(item, out var currentIndex) && currentIndex != i)
            {
                TagItems.Move(currentIndex, i);
                // Update position tracking after move
                currentPositions[TagItems[i]] = i;
            }
        }
    }

    private IEnumerable<string> GetAllTagNames()
        => AllTasks.SelectMany(t => t.Tags).Distinct(StringComparer.OrdinalIgnoreCase);

    // The hot path: nearly every task edit (typing, checking a box, trashing, tagging...) routes
    // through here via OnTaskChanged. Runs off the UI thread instead of blocking on disk IO -
    // _pendingSaveTask is tracked so FlushPendingSaveAsync (restoring a backup, closing the app)
    // can still wait for a real completion when it actually matters.
    private void Save()
    {
        SaveStatusText = "Saving…";
        var generation = ++_saveGeneration;
        _pendingSaveTask = SaveAndReportAsync(generation);
    }

    // generation guards against two problems that come from Save() firing from multiple
    // overlapping call sites (the debounce timer AND every immediate property change): an older,
    // slower save finishing after a newer one started must not stamp "Saved" over a still-pending
    // edit's "Saving…" - and a failure must actually surface instead of leaving the status stuck
    // on "Saving…" forever with the edit silently unwritten.
    private async Task SaveAndReportAsync(int generation)
    {
        try
        {
            await _store.SaveAsync(_state, _currentFilePath);
            if (generation == _saveGeneration)
                SaveStatusText = "Saved";

            ScheduleGoogleDriveAutoSync();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            if (generation == _saveGeneration)
                SaveStatusText = "Save failed - will retry on next edit";
        }
    }

    private void ScheduleGoogleDriveAutoSync()
    {
        if (_settings.IsGoogleDriveEnabled && _googleDrive.IsAuthenticated)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _autoSyncTimer.Stop();
                _autoSyncTimer.Start();
            });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName!);
        return true;
    }
}
