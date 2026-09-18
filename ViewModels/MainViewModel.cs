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

public partial class MainViewModel : INotifyPropertyChanged
{
    private readonly TodoStore _store = new();
    private readonly SettingsStore _settingsStore;
    private readonly TrayIconService _tray = new();
    private readonly GoogleDriveService _googleDrive = new();
    private readonly SyncCoordinator _sync;
    private readonly Settings _settings;
    // Never reassigned after construction (see LoadFile) - AllTasks below is a passthrough to
    // _state.Tasks, and FilteredTasksView wraps that same collection instance once in the
    // constructor, so both only keep working if _state.Tasks's own identity never changes.
    private readonly AppState _state = new();
    private string _currentFilePath = null!;

    private readonly SidebarFilterItem _todayItem = new(SidebarFilterKind.Today, "Today");
    private readonly SidebarFilterItem _tomorrowItem = new(SidebarFilterKind.Tomorrow, "Tomorrow");
    private readonly SidebarFilterItem _allItem = new(SidebarFilterKind.All, "All Tasks");
    private readonly SidebarFilterItem _somedayItem = new(SidebarFilterKind.Someday, "Someday");
    private readonly SidebarFilterItem _doneItem = new(SidebarFilterKind.Done, "Completed");
    private readonly SidebarFilterItem _trashItem = new(SidebarFilterKind.Trash, "Trash");
    private readonly SidebarFilterItem _recurringItem = new(SidebarFilterKind.Recurring, "Recurring");

    private readonly DispatcherTimer _saveDebounceTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly ReminderScheduler _reminders;
    private readonly LinkedList<(string Description, Action Undo)> _undoStack = new();
    private const int MaxUndoDepth = 25;

    private SidebarFilterItem _selectedSidebarItem;
    private string _searchText = string.Empty;
    // Tracks whether the current SearchText is one a View selection wrote in (see
    // SelectedSidebarItem's setter) versus something the user actually typed, so navigating away
    // from that View can clear it again - see the setter's own comment.
    private string? _searchTextSetByViewQuery;
    private TaskItem? _selectedTask;
    private TaskDetailViewModel? _selectedTaskDetail;
    private bool _isDarkTheme;
    private bool _isFocusMode;
    private bool _isSidebarCollapsed;
    private bool _hasSeenWelcomeTour;
    private SortOption _currentSort = SortOption.ModifiedNewest;
    // Traditional multi-select filtering (AND-combined) rather than the old single mutually-
    // exclusive QuickFilter - ordered fixed list (not the HashSet's own enumeration order) so
    // chips in the UI stay in a stable, predictable position as filters are toggled on/off rather
    // than jumping around based on click order.
    private static readonly QuickFilter[] AllQuickFilters =
    {
        QuickFilter.Overdue, QuickFilter.DueToday, QuickFilter.NoDueDate,
        QuickFilter.Recurring, QuickFilter.HasLink, QuickFilter.HasAttachment, QuickFilter.HighPriority
    };
    private readonly HashSet<QuickFilter> _activeQuickFilters = new();
    private ViewMode _viewMode = ViewMode.List;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _isFilterPopupOpen;
    private string _saveStatusText = string.Empty;
    private bool _isSyncing;
    private int _syncProgressPercent;
    private Task _pendingSaveTask = Task.CompletedTask;
    private int _saveGeneration;
    private bool _isRestoringBackup;
    // Bumped whenever the open file changes (LoadFile, Save As). A Drive sync captures it when it
    // starts and abandons itself if it no longer matches - see PerformGoogleDriveSyncAsync.
    private int _fileSessionId;
    // How many PerformGoogleDriveSyncAsync calls are currently awaiting - see IsSyncing's use there.
    private int _syncCallsInFlight;
    private readonly DispatcherTimer _saveRetryTimer;
    private bool _isExecutingUndo;
    private readonly DispatcherTimer _autoSyncTimer;
    private readonly DispatcherTimer _idleSyncTimer;

    // A passthrough, not an independent collection - _state.Tasks is now the single source of
    // truth for both "what's saved" and "what's shown", instead of the two being manually kept in
    // sync at every add/remove call site (the previous shape of this: a separately-maintained
    // AllTasks alongside AppState.Tasks, with no guarantee a future call site wouldn't forget one).
    public ObservableCollection<TaskItem> AllTasks => _state.Tasks;
    public ObservableCollection<SidebarFilterItem> SidebarItems { get; } = new();
    public ObservableCollection<SidebarFilterItem> TagItems { get; } = new();
    public ObservableCollection<SidebarFilterItem> ViewItems { get; } = new();
    public ListCollectionView FilteredTasksView { get; }
    public List<TaskItem> SelectedTasks { get; private set; } = new();
    public TrayIconService Tray => _tray;
    internal string CurrentFilePath => _currentFilePath;

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

            // A View has no scope of its own in FilterTask - selecting one just loads its saved
            // query into the search box, so the existing "not closed, not done" default scope +
            // live-search-AND does the actual filtering with no new predicate branch needed. Set
            // the backing field directly (bypassing the #63 search debounce) since this is a
            // discrete click, not rapid typing - it should refresh immediately below, same as
            // every other sidebar selection.
            if (_selectedSidebarItem.Kind == SidebarFilterKind.View)
            {
                var view = _state.SavedViews.FirstOrDefault(v => v.Id == _selectedSidebarItem.ViewId);
                if (view is not null)
                {
                    _searchText = view.Query;
                    _searchTextSetByViewQuery = view.Query;
                    OnPropertyChanged(nameof(SearchText));
                }
            }
            // Navigating to anything other than a View (Today, All Tasks, a Tag, ...) should leave
            // the search box the way it'd look if you'd never opened a View - but only when the box
            // still holds exactly what that View put there. If the user edited it first, that edit
            // is theirs and switching sections shouldn't erase it (same as switching sections never
            // erases text you typed directly). Without this, the query a View writes into the
            // search box just sits there forever once you click elsewhere: the sidebar highlight
            // moves on, but the task list stays filtered by that View's query underneath it, with
            // no visible reason why - "All Tasks" (or a Tag) LOOKS selected but isn't really showing
            // all tasks, and clearing it manually in the search box was the only way out.
            else if (_searchTextSetByViewQuery is not null && _searchText == _searchTextSetByViewQuery)
            {
                _searchText = string.Empty;
                _searchTextSetByViewQuery = null;
                OnPropertyChanged(nameof(SearchText));
            }

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
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    // Ground truth for every filter checkbox's IsChecked (via QuickFilterActiveConverter) and for
    // FilterTask's AND-combination below. Exposed read-only - ToggleQuickFilterCommand/
    // RemoveQuickFilterCommand/ClearQuickFiltersCommand are the only ways to mutate it, so every
    // caller stays in sync (view refresh, chip list, empty-state message) instead of some path
    // rebuilding the set without triggering the others.
    public IReadOnlyCollection<QuickFilter> ActiveQuickFilters => _activeQuickFilters;

    public bool HasActiveQuickFilters => _activeQuickFilters.Count > 0;

    public int ActiveQuickFilterCount => _activeQuickFilters.Count;

    public IEnumerable<QuickFilter> ActiveQuickFilterChips => AllQuickFilters.Where(_activeQuickFilters.Contains);

    private void ToggleQuickFilter(QuickFilter filter)
    {
        if (!_activeQuickFilters.Remove(filter)) _activeQuickFilters.Add(filter);
        RaiseQuickFiltersChanged();
    }

    private void RaiseQuickFiltersChanged()
    {
        OnPropertyChanged(nameof(ActiveQuickFilters));
        OnPropertyChanged(nameof(HasActiveQuickFilters));
        OnPropertyChanged(nameof(ActiveQuickFilterCount));
        OnPropertyChanged(nameof(ActiveQuickFilterChips));
        FilteredTasksView.Refresh();
        OnPropertyChanged(nameof(EmptyStateMessage));
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

    // For the View layer to surface a one-off notice in the status area (SaveStatusText's setter
    // stays private so only this class decides what save/sync states look like).
    public void ShowStatusMessage(string message) => SaveStatusText = message;

    // ROADMAP.md #57: replaces the plain status text with a real progress bar while a Google
    // Drive sync is running. IsSyncing gates the bar's visibility (rather than inferring "syncing"
    // from SaveStatusText's wording, which is fragile against future copy changes);
    // SyncProgressPercent is coarse/stage-based - see SyncCoordinator.PerformSyncAsync's Progress().
    public bool IsSyncing
    {
        get => _isSyncing;
        private set => SetField(ref _isSyncing, value);
    }

    public int SyncProgressPercent
    {
        get => _syncProgressPercent;
        private set => SetField(ref _syncProgressPercent, value);
    }

    // A plain computed string rather than a converter, since the message needs to distinguish
    // "nothing here" from "nothing matches your search/filter" from "everything with this tag is
    // in Trash" - situations that all boil down to an empty FilteredTasksView but need different
    // explanations, since a Tag view (unlike every other filter) never shows trashed tasks.
    public string EmptyStateMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText) || HasActiveQuickFilters)
                return "No tasks match your search or filter.";
            if (SelectedSidebarItem.Kind == SidebarFilterKind.Tomorrow)
                return "No tasks scheduled for tomorrow.";
            if (SelectedSidebarItem.Kind == SidebarFilterKind.Someday)
                return "No unscheduled tasks.";
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
                : new TaskDetailViewModel(value, OnTaskChanged, GetAllTagNames, RequestDebouncedSave, PushUndo,
                    () => _settings.AlwaysShowSubtasks, () => FocusSubtaskRequested?.Invoke());
        }
    }

    public TaskDetailViewModel? SelectedTaskDetail
    {
        get => _selectedTaskDetail;
        private set => SetField(ref _selectedTaskDetail, value);
    }

    public bool IsFocusMode
    {
        get => _isFocusMode;
        set
        {
            if (SetField(ref _isFocusMode, value))
            {
                OnPropertyChanged(nameof(SidebarWidth));
                OnPropertyChanged(nameof(CollapseListColumnForFocusMode));
                OnPropertyChanged(nameof(IsSidebarShowingIconsOnly));
            }
        }
    }

    // Focus Mode collapses the task-list column to widen the editor - meaningless once Calendar
    // view has already taken over that same column for its own grid, and collapsing it out from
    // under the calendar would just shrink it by the 340+5px Focus Mode normally reclaims. The two
    // ColumnDefinitions that used to bind to IsFocusMode directly bind to this instead, so turning
    // Focus Mode on while in Calendar (or switching to Calendar while it's already on) never
    // affects the calendar's width - ToggleFocusModeCommand's CanExecute also keeps the toolbar
    // button/F11 from turning it on mid-Calendar-view at all, but this covers the case where it
    // was already on in List view before switching.
    public bool CollapseListColumnForFocusMode => IsFocusMode && ViewMode == ViewMode.List;

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (!SetField(ref _isSidebarCollapsed, value)) return;
            OnPropertyChanged(nameof(SidebarWidth));
            OnPropertyChanged(nameof(IsSidebarShowingIconsOnly));
            _settings.SidebarCollapsed = value;
            _settingsStore.Save(_settings);
        }
    }

    // When collapsed (or in Focus Mode), the sidebar displays a clean 56px icon rail
    // with centered icons and tooltips, keeping quick navigation accessible.
    public GridLength SidebarWidth => (IsFocusMode || IsSidebarCollapsed)
        ? new GridLength(56)
        : new GridLength(220);

    public bool IsSidebarShowingIconsOnly => IsFocusMode || IsSidebarCollapsed;

    public SortOption CurrentSort
    {
        get => _currentSort;
        set
        {
            if (!SetField(ref _currentSort, value)) return;
            FilteredTasksView.CustomSort = new TaskComparer(value);
        }
    }

    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetField(ref _viewMode, value)) return;
            OnPropertyChanged(nameof(IsCalendarView));
            OnPropertyChanged(nameof(CollapseListColumnForFocusMode));
            if (value == ViewMode.Calendar) RefreshCalendarDays();
        }
    }

    // Bool mirror of ViewMode for the "Calendar View" checkable menu item. Settable (not just a
    // read-only mirror) so a two-way IsChecked binding can drive it directly - a checkable
    // MenuItem paired with a Command+fixed CommandParameter fires that same fixed parameter on
    // every click, checking OR unchecking, so unchecking it would have kept re-selecting Calendar
    // instead of switching back to List.
    public bool IsCalendarView
    {
        get => _viewMode == ViewMode.Calendar;
        set => ViewMode = value ? ViewMode.Calendar : ViewMode.List;
    }

    public DateTime CalendarMonth
    {
        get => _calendarMonth;
        private set
        {
            if (!SetField(ref _calendarMonth, value)) return;
            OnPropertyChanged(nameof(CalendarMonthLabel));
            RefreshCalendarDays();
        }
    }

    public string CalendarMonthLabel => _calendarMonth.ToString("MMMM yyyy");

    public ObservableCollection<CalendarDay> CalendarDays { get; } = new();

    // private set (not the plain get-only these were before) so the constructor can delegate
    // assignment to the InitializeXCommands() groupings below instead of one 290-line body -
    // a get-only auto-property's backing field can only be assigned directly in the constructor
    // itself, not from a method the constructor calls. Still fully read-only from outside the class.
    public RelayCommand AddTaskCommand { get; private set; } = null!;
    public RelayCommand ToggleCloseSelectedCommand { get; private set; } = null!;
    public RelayCommand DeleteSelectedCommand { get; private set; } = null!;
    public RelayCommand ShowAllCommand { get; private set; } = null!;
    public RelayCommand SelectSidebarItemCommand { get; private set; } = null!;
    public RelayCommand ShowClosedCommand { get; private set; } = null!;
    public RelayCommand ShowTrashCommand { get; private set; } = null!;
    public RelayCommand TrashAllClosedCommand { get; private set; } = null!;
    public RelayCommand TogglePinCommand { get; private set; } = null!;
    public RelayCommand ToggleFocusModeCommand { get; private set; } = null!;
    public RelayCommand ToggleSidebarCommand { get; private set; } = null!;
    public RelayCommand SetSortCommand { get; private set; } = null!;
    public RelayCommand EmptyTrashCommand { get; private set; } = null!;
    public RelayCommand ToggleQuickFilterCommand { get; private set; } = null!;
    public RelayCommand RemoveQuickFilterCommand { get; private set; } = null!;
    public RelayCommand ClearQuickFiltersCommand { get; private set; } = null!;
    public RelayCommand ToggleFilterPopupCommand { get; private set; } = null!;
    public RelayCommand SaveViewCommand { get; private set; } = null!;
    public RelayCommand DeleteViewCommand { get; private set; } = null!;
    public RelayCommand DeleteTagCommand { get; private set; } = null!;
    public RelayCommand SetViewModeCommand { get; private set; } = null!;
    public RelayCommand ToggleCalendarViewCommand { get; private set; } = null!;
    public RelayCommand PreviousMonthCommand { get; private set; } = null!;
    public RelayCommand NextMonthCommand { get; private set; } = null!;
    public RelayCommand TodayCommand { get; private set; } = null!;
    public RelayCommand SelectCalendarTaskCommand { get; private set; } = null!;
    public AsyncRelayCommand NewFileCommand { get; private set; } = null!;
    public RelayCommand OpenFileCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveFileAsCommand { get; private set; } = null!;
    public RelayCommand UndoCommand { get; private set; } = null!;
    public RelayCommand BulkMarkDoneCommand { get; private set; } = null!;
    public RelayCommand BulkTrashCommand { get; private set; } = null!;
    public RelayCommand BulkRestoreCommand { get; private set; } = null!;
    public RelayCommand BulkDeleteCommand { get; private set; } = null!;
    public RelayCommand BulkTogglePinCommand { get; private set; } = null!;
    public RelayCommand BulkSetDueDateCommand { get; private set; } = null!;
    public RelayCommand BulkAddTagCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreBackupCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportBackupCommand { get; private set; } = null!;
    public RelayCommand ExportCalendarCommand { get; private set; } = null!;
    public RelayCommand ExportAllTasksCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportBackupCommand { get; private set; } = null!;
    public RelayCommand ClearDebugLogCommand { get; private set; } = null!;
    public RelayCommand OpenDebugLogCommand { get; private set; } = null!;
    public RelayCommand GoogleDriveCommand { get; private set; } = null!;
    public RelayCommand SettingsCommand { get; private set; } = null!;
    public AsyncRelayCommand SyncGoogleDriveNowCommand { get; private set; } = null!;

    public bool IsGoogleDriveConnected => _googleDrive.IsAuthenticated;

    public string GoogleDriveStatusTooltip => _googleDrive.IsAuthenticated
        ? $"Google Drive: Connected ({_settings.GoogleDriveAccountEmail ?? "Authorized"})\nLast synced: {(_settings.LastGoogleDriveSyncTime.HasValue ? _settings.LastGoogleDriveSyncTime.Value.ToString("g") : "Never")}"
        : "Google Drive: Disconnected (Click to configure)";

    public event Action? FocusTitleRequested;

    // Same ViewModel-signals/code-behind-focuses split as FocusTitleRequested: clicking "Add
    // subtasks" reveals the section, and the caret should land in its input without a second click.
    public event Action? FocusSubtaskRequested;

    // MainWindow.xaml.cs owns showing SaveViewPromptWindow (dialogs are a View concern, same as
    // LinkPromptWindow/TablePromptWindow are only ever constructed from code-behind) - this just
    // signals "the user asked to save the current search," mirroring FocusTitleRequested above.
    public event Action? SaveViewRequested;

    // Same "ViewModel signals, code-behind shows the dialog" split as SaveViewRequested above - a
    // multi-selection has no single date/tag to bind a picker to inline (unlike TaskDetailViewModel's
    // own due-date/tag controls), so bulk-editing either one needs its own small prompt window.
    public event Action? BulkSetDueDateRequested;
    public event Action? BulkAddTagRequested;

    public MainViewModel() : this(new SettingsStore())
    {
    }

    // Tests pass their own SettingsStore (a temp settings.json) and data file, so each one gets a
    // ViewModel that shares nothing on disk with any other test running in parallel - even with
    // TaskyPaths redirected, the default constructor's settings.json and default data file are
    // still one shared pair, and LastFilePath written by one test became the file another opened.
    internal MainViewModel(SettingsStore settingsStore, string? initialFilePath = null)
    {
        _settingsStore = settingsStore;
        _settings = _settingsStore.Load();
        if (_settingsStore.LastLoadWarning is { } loadWarning)
        {
            ThemedMessageBox.Show(loadWarning, "Settings Reset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ApplyBackupSettingsToStore();
        _sync = new SyncCoordinator(_googleDrive, _store, _settings, _settingsStore);
        _isDarkTheme = _settings.Theme == "Dark";
        _isSidebarCollapsed = _settings.SidebarCollapsed;
        _hasSeenWelcomeTour = _settings.HasSeenWelcomeTour;
        ThemeService.Apply(_settings.Theme);
        AppLogger.IsVerbose = _settings.IsVerboseLogging;

        SidebarItems.Add(_todayItem);
        SidebarItems.Add(_tomorrowItem);
        SidebarItems.Add(_allItem);
        SidebarItems.Add(_somedayItem);
        SidebarItems.Add(_recurringItem);
        SidebarItems.Add(_doneItem);
        SidebarItems.Add(_trashItem);
        _selectedSidebarItem = _allItem;

        FilteredTasksView = new ListCollectionView(AllTasks) { Filter = FilterTask, CustomSort = new TaskComparer(_currentSort) };

        // Covers every way the task list itself changes (add/delete/trash/restore/undo/Drive
        // merge/LoadFile) in one place, rather than adding a RefreshCalendarDays() call to each
        // of those individually. A no-op while list view is active - see RefreshCalendarDays.
        AllTasks.CollectionChanged += (_, _) =>
        {
            if (ViewMode == ViewMode.Calendar) RefreshCalendarDays();
            UpdateTrayStatus();
        };

        var initialPath = initialFilePath ?? ResolveInitialFilePath();
        MediaPathResolver.SetDataFilePath(initialPath);

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounceTimer.Tick += (_, _) => CommitSave();

        // A failed save used to just say "will retry on next edit" - and if there was no next
        // edit, it never did: the change lived only in memory, and closing the app awaited a save
        // task that had already swallowed its failure and exited "cleanly". The usual cause
        // (OneDrive/antivirus holding the file) clears within seconds, so keep trying on a timer
        // until a save lands. See SaveAndReportAsync and LastSaveFailed.
        _saveRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _saveRetryTimer.Tick += (_, _) =>
        {
            _saveRetryTimer.Stop();
            Save();
        };

        // ROADMAP #63: typing in the search box used to call FilteredTasksView.Refresh() (an
        // O(all tasks) predicate re-scan) on every keystroke, which stutters with a large list.
        // Same debounce shape as _saveDebounceTimer above, just a much shorter interval since this
        // is UI responsiveness, not a data-safety window.
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            FilteredTasksView.Refresh();
            OnPropertyChanged(nameof(EmptyStateMessage));
        };

        var initialNotified = _settings.NotifiedTaskIds
            .Select(NotifiedReminder.Parse)
            .Where(entry => entry.HasValue)
            .Select(entry => entry!.Value);
        _reminders = new ReminderScheduler(() => AllTasks, () => RemindersEnabled, _tray,
            initialNotified, PersistNotifiedTaskIds);
        _reminders.Start();

        _tray.QuickAddHotkeyText = QuickAddHotkey;
        _tray.MenuInfoProvider = () =>
        {
            var open = AllTasks.Count(t => !t.IsDone && !t.IsClosed);
            var today = AllTasks.Count(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today);
            var overdue = AllTasks.Count(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && t.DueDate.Value.Date < DateTime.Today);
            return new TrayMenuInfo(open, today, overdue, IsGoogleDriveConnected, GoogleDriveStatusTooltip);
        };
        _tray.ShowTodayRequested += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                SelectedSidebarItem = _todayItem;
                _tray.RaiseShowRequested();
            });
        };
        _tray.SyncGoogleDriveRequested += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (SyncGoogleDriveNowCommand.CanExecute(null))
                    SyncGoogleDriveNowCommand.Execute(null);
            });
        };
        _tray.SettingsRequested += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (SettingsCommand.CanExecute(null))
                    SettingsCommand.Execute(null);
            });
        };

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
        _reminders.CheckReminders();
        UpdateTrayStatus();

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
            if (SelectedSidebarItem?.Kind == SidebarFilterKind.Done || SelectedSidebarItem?.Kind == SidebarFilterKind.Trash)
            {
                SelectedSidebarItem = _allItem;
            }

            var task = new TaskItem { Text = "New Task" };
            if (SelectedSidebarItem?.Kind == SidebarFilterKind.Today)
            {
                task.DueDate = DateTime.Today;
            }
            else if (SelectedSidebarItem?.Kind == SidebarFilterKind.Tomorrow)
            {
                task.DueDate = DateTime.Today.AddDays(1);
            }
            else if (SelectedSidebarItem?.Kind == SidebarFilterKind.Tag && SelectedSidebarItem.TagName is { } tagName)
            {
                task.Tags.Add(tagName);
            }

            task.SortOrder = AllTasks.Count > 0 ? AllTasks.Max(t => t.SortOrder) + 1 : 0;
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

            PermanentlyDelete(targets);
            SelectedTask = null;
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

            PermanentlyDelete(trashed);
        });
    }

    // The one way a task leaves for good - Delete, Bulk Delete, Empty Trash and the auto-empty
    // sweep all used to carry their own copy of these steps, and every one of them has to get all
    // of it right: a missed tombstone lets the next Drive merge bring the task straight back, a
    // missed Detach leaks the handler, a missed cleanup orphans its attachments on disk.
    // Attachments are cleaned up once for the whole batch (after every task is out of AllTasks)
    // rather than once per task - same result, without rescanning every remaining task's RTF for
    // each deleted one.
    private void PermanentlyDelete(IReadOnlyCollection<TaskItem> tasks)
    {
        if (tasks.Count == 0) return;

        foreach (var task in tasks)
        {
            DetachTask(task);
            AllTasks.Remove(task);
            RecordTaskDeletionTombstone(task);
            if (SelectedTask == task) SelectedTask = null;
        }
        CleanupTaskAttachments(tasks);
        OnTaskChanged();
    }

    public void ReorderTask(TaskItem sourceTask, TaskItem targetTask, bool insertAfter)
    {
        if (sourceTask is null || targetTask is null || ReferenceEquals(sourceTask, targetTask)) return;

        // Everything below works in AllTasks index order and then renumbers SortOrder from it, but
        // that order can drift from the Manual order actually on screen: MergeTaskOrder only
        // rewrites SortOrder (it never moves items), and pinned tasks always sort first. Line the
        // collection up with the displayed order first, or one drag snaps every other task back to
        // raw collection order.
        var manualComparer = new TaskComparer(SortOption.Manual);
        var displayed = AllTasks.OrderBy(t => t, Comparer<TaskItem>.Create((a, b) => manualComparer.Compare(a, b))).ToList();
        for (var i = 0; i < displayed.Count; i++)
        {
            var current = AllTasks.IndexOf(displayed[i]);
            if (current != i) AllTasks.Move(current, i);
        }

        int sourceIndex = AllTasks.IndexOf(sourceTask);
        int targetIndex = AllTasks.IndexOf(targetTask);
        if (sourceIndex < 0 || targetIndex < 0) return;

        bool wasPinned = sourceTask.IsPinned;
        if (targetTask.IsPinned && !sourceTask.IsPinned && !insertAfter)
        {
            sourceTask.IsPinned = true;
        }
        else if (!targetTask.IsPinned && sourceTask.IsPinned && insertAfter)
        {
            sourceTask.IsPinned = false;
        }

        if (CurrentSort != SortOption.Manual)
        {
            CurrentSort = SortOption.Manual;
        }

        int newIndex = targetIndex;
        if (insertAfter)
        {
            if (sourceIndex > targetIndex)
                newIndex = targetIndex + 1;
        }
        else
        {
            if (sourceIndex < targetIndex)
                newIndex = targetIndex - 1;
        }

        if (newIndex < 0) newIndex = 0;
        if (newIndex >= AllTasks.Count) newIndex = AllTasks.Count - 1;
        if (newIndex == sourceIndex && wasPinned == sourceTask.IsPinned) return;

        var oldSnapshot = AllTasks.Select((t, i) => (Task: t, Index: i, Pinned: t.IsPinned)).ToList();

        AllTasks.Move(sourceIndex, newIndex);

        for (int i = 0; i < AllTasks.Count; i++)
        {
            AllTasks[i].SortOrder = i;
        }

        sourceTask.ModifiedAt = DateTime.UtcNow;
        // Ordering is list-level state with its own timestamp - without this a reorder makes no
        // task "newer", so every device that merged silently threw the new arrangement away. See
        // AppState.TasksOrderModifiedAt and TaskSyncMerge.MergeTaskOrder.
        _state.TasksOrderModifiedAt = DateTime.UtcNow;
        FilteredTasksView.Refresh();
        OnTaskChanged();

        PushUndo($"Reorder \"{sourceTask.Text}\"", () =>
        {
            sourceTask.IsPinned = wasPinned;
            var sorted = AllTasks.OrderBy(t => oldSnapshot.FirstOrDefault(x => x.Task == t).Index).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var cur = AllTasks.IndexOf(sorted[i]);
                if (cur != i) AllTasks.Move(cur, i);
                AllTasks[i].SortOrder = i;
            }
            _state.TasksOrderModifiedAt = DateTime.UtcNow;
            FilteredTasksView.Refresh();
            OnTaskChanged();
        });
    }

    // ROADMAP.md #135: opt-in automatic counterpart to EmptyTrashCommand above - same
    // detach/remove/tombstone/cleanup steps, minus the confirmation dialog (there's no one to
    // confirm with on a background sweep) and the "empty an active selection" nuance (nothing here
    // was ever selected by this sweep). Called once after a file loads and once after every
    // successful Drive sync/merge - see LoadFile and PerformGoogleDriveSyncAsync - so a task
    // trashed on another device still ages out here even if this device never trashes anything
    // itself. ModifiedAt is used as a "trashed at" proxy: IsClosed makes a task read-only (see
    // TaskDetailViewModel.IsEditable), so nothing else bumps ModifiedAt after it lands in Trash -
    // Tasky Web's autoEmptyTrashIfNeeded (app.js) makes the same assumption, and both must agree.
    private void AutoEmptyTrashIfNeeded()
    {
        if (!_settings.AutoEmptyTrashEnabled) return;
        var cutoff = DateTime.UtcNow.AddDays(-_settings.AutoEmptyTrashDays);
        var expired = AllTasks.Where(t => t.IsClosed && t.ModifiedAt < cutoff).ToList();
        PermanentlyDelete(expired);
    }

    // Sidebar scope switching, sort, quick filter, and layout toggles - commands that change
    // what's visible or how it's arranged, rather than mutating any task.
    private void InitializeViewCommands()
    {
        ShowAllCommand = new RelayCommand(_ => SelectedSidebarItem = _allItem);
        ShowClosedCommand = new RelayCommand(_ => SelectedSidebarItem = _doneItem);
        ShowTrashCommand = new RelayCommand(_ => SelectedSidebarItem = _trashItem);

        // Sidebar/Tags/Views are three separate ListBoxes all bound to this one SelectedSidebarItem
        // property, and relying on that shared SelectedItem binding as the ONLY way a click reaches
        // the VM turned out to be unreliable in both directions: source-to-target (VM -> the OTHER
        // two lists) didn't always clear their stale highlight (fixed by SidebarListBoxItem's
        // ObjectsEqual-based trigger, which stopped rendering the highlight from Selector.IsSelected
        // at all), and target-to-source (a list's own click -> VM) turned out to have the mirror
        // problem: if a list's internal SelectedItem was never told about a selection made in a
        // DIFFERENT list (exactly what happens once nothing pushes into it - which briefly included
        // this list itself in an earlier fix attempt, and evidently isn't fully solved by ordinary
        // TwoWay either), then clicking an item that list already believes is selected is, to WPF,
        // not a change - no SelectionChanged, nothing pushed to the binding's source, no visible
        // effect. Reported live: after selecting a Tag, "All Tasks" stopped responding to clicks
        // entirely - the exact symptom of a Selector deciding nothing changed. The SidebarItemTemplate's
        // MouseBinding now calls this directly instead: a real mouse click always fires it, regardless
        // of whatever Selector.SelectedItem privately still thinks. SelectedItem stays bound too
        // (removing it would silently break arrow-key navigation, which has no other path to update
        // this property), so this is a second, load-bearing path for the same result, not a
        // replacement.
        SelectSidebarItemCommand = new RelayCommand(p =>
        {
            if (p is SidebarFilterItem item) SelectedSidebarItem = item;
        });

        // Disabled while Calendar view is active - see CollapseListColumnForFocusMode. WPF's
        // CommandManager.RequerySuggested (RelayCommand.CanExecuteChanged) re-evaluates this on
        // the same UI input events (button clicks, key presses) that change ViewMode in the first
        // place, so the toolbar button/F11 binding disables itself without any manual invalidation.
        ToggleFocusModeCommand = new RelayCommand(_ => IsFocusMode = !IsFocusMode, _ => ViewMode == ViewMode.List);
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);

        SetSortCommand = new RelayCommand(p =>
        {
            if (p is SortOption option) CurrentSort = option;
        });

        // Unlike the old single-select SetQuickFilterCommand, this doesn't close the popup -
        // traditional filter panels let you check several boxes in one pass rather than
        // reopening the menu after every click (see the filtering-UX research this was built
        // from: applied filters should be combinable, and each toggle shouldn't cost a re-open).
        ToggleQuickFilterCommand = new RelayCommand(p =>
        {
            if (p is QuickFilter filter) ToggleQuickFilter(filter);
        });

        RemoveQuickFilterCommand = new RelayCommand(p =>
        {
            if (p is QuickFilter filter && _activeQuickFilters.Remove(filter)) RaiseQuickFiltersChanged();
        });

        ClearQuickFiltersCommand = new RelayCommand(_ =>
        {
            _activeQuickFilters.Clear();
            RaiseQuickFiltersChanged();
        }, _ => HasActiveQuickFilters);

        ToggleFilterPopupCommand = new RelayCommand(_ => IsFilterPopupOpen = !IsFilterPopupOpen);

        // A saved view is just a query string (SavedView.cs), but "query" isn't limited to what's
        // literally typed in the search box - quick filters and a selected tag both translate to
        // equivalent operator syntax (see BuildEffectiveSearchQuery), so any of the three on their
        // own is enough to save. Gating on SearchText alone meant a tag-only or quick-filter-only
        // scope, with nothing typed, could never be saved as a view at all.
        SaveViewCommand = new RelayCommand(_ => SaveViewRequested?.Invoke(),
            _ => !string.IsNullOrWhiteSpace(SearchText) || HasActiveQuickFilters
                 || SelectedSidebarItem.Kind == SidebarFilterKind.Tag);

        DeleteViewCommand = new RelayCommand(p =>
        {
            if (p is not SidebarFilterItem { Kind: SidebarFilterKind.View } item || item.ViewId is not { } viewId) return;

            _state.SavedViews.RemoveAll(v => v.Id == viewId);
            _state.DeletedSavedViewIds.Add(viewId);
            if (SelectedSidebarItem.ViewId == viewId) SelectedSidebarItem = _allItem;
            OnTaskChanged();
        });

        // Right-click "Delete Tag" on a sidebar tag - strips it off every task that carries it
        // (not just the ones currently in view), with a confirmation since it's a bulk, cross-task
        // edit rather than the single-task RemoveTagCommand in TaskDetailViewModel.
        DeleteTagCommand = new RelayCommand(p =>
        {
            if (p is not SidebarFilterItem { Kind: SidebarFilterKind.Tag } item || item.TagName is not { } tagName) return;

            var affected = AllTasks.Where(t => t.Tags.Any(tag => tag.Equals(tagName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (affected.Count == 0) return;

            var result = ThemedMessageBox.Show(
                $"Remove the \"{tagName}\" tag from {affected.Count} task(s)? This removes it everywhere, not just from the current view.",
                "Delete Tag", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            if (SelectedSidebarItem.Kind == SidebarFilterKind.Tag
                && string.Equals(SelectedSidebarItem.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                SelectedSidebarItem = _allItem;

            foreach (var task in affected)
            {
                for (var i = task.Tags.Count - 1; i >= 0; i--)
                    if (task.Tags[i].Equals(tagName, StringComparison.OrdinalIgnoreCase))
                        task.Tags.RemoveAt(i);
                // Tags is a plain ObservableCollection with no SetField wrapper (see
                // TaskDetailViewModel.AddTagCommand's comment), so ModifiedAt needs a manual bump
                // here too or the sync merge won't see this as an edit worth keeping.
                task.ModifiedAt = DateTime.UtcNow;
            }
            OnTaskChanged();

            PushUndo($"Delete tag \"{tagName}\"", () =>
            {
                foreach (var task in affected)
                {
                    if (!task.Tags.Any(t => t.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
                        task.Tags.Add(tagName);
                    task.ModifiedAt = DateTime.UtcNow;
                }
                OnTaskChanged();
            });
        });

        SetViewModeCommand = new RelayCommand(p =>
        {
            if (p is ViewMode mode) ViewMode = mode;
        });

        ToggleCalendarViewCommand = new RelayCommand(_ =>
            ViewMode = ViewMode == ViewMode.Calendar ? ViewMode.List : ViewMode.Calendar);

        PreviousMonthCommand = new RelayCommand(_ => CalendarMonth = _calendarMonth.AddMonths(-1));
        NextMonthCommand = new RelayCommand(_ => CalendarMonth = _calendarMonth.AddMonths(1));
        TodayCommand = new RelayCommand(_ => CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));

        SelectCalendarTaskCommand = new RelayCommand(p =>
        {
            if (p is not TaskItem task) return;
            SelectedTask = task;
            ViewMode = ViewMode.List;
        });
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
            var attachmentsDir = MediaPathResolver.AttachmentsDirectory;
            var inlineImagesDir = MediaPathResolver.InlineImagesDirectory;

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
            TaskMediaHelper.CollectReferencedFileNames(block.PhotoPath, block.Rtf, set);
        }
    }

    // Only the QuickAddWindow path (global hotkey / tray "New Task") parses these tokens - it has
    // a clean one-shot "commit" moment (Enter). The inline title TextBox on an existing task is
    // two-way bound and saves on every keystroke, so there's no equivalent safe commit point:
    // stripping a "#tag" out from under the user while they're still mid-word typing it would be
    // actively wrong, not just unnecessary.
    public TaskItem AddQuickTask(string title)
    {
        var parsed = QuickEntryParser.Parse(title);
        var text = string.IsNullOrWhiteSpace(parsed.Text) ? title : parsed.Text;

        var task = new TaskItem
        {
            Text = text,
            DueDate = parsed.DueDate,
            Recurrence = parsed.Recurrence,
            RecurrenceInterval = parsed.RecurrenceInterval,
        };
        foreach (var tag in parsed.Tags) task.Tags.Add(tag.ToLowerInvariant());

        if (SelectedSidebarItem?.Kind == SidebarFilterKind.Done || SelectedSidebarItem?.Kind == SidebarFilterKind.Trash)
        {
            SelectedSidebarItem = _allItem;
        }

        task.SortOrder = AllTasks.Count > 0 ? AllTasks.Max(t => t.SortOrder) + 1 : 0;
        AllTasks.Add(task);
        AttachTask(task);
        OnTaskChanged();
        SelectedTask = task;
        return task;
    }

    // Welcome tour's sample tasks (WelcomeWindow). Unlike AddQuickTask, the title is kept verbatim
    // and quickAddTokens (if given) is parsed separately just for DueDate/Tags - a sample task
    // meant to demonstrate "#tag !due:day @time" syntax needs to keep showing that literal syntax
    // in its title, not have AddQuickTask strip it out as if it had really been typed into the
    // quick-add box (Tasky Web hit this exact bug with its own onboarding sample tasks - see
    // createDemoTask in docs/js/app.js).
    public void AddDemoTask(string title, string? quickAddTokens = null)
    {
        var task = new TaskItem { Text = title };
        if (quickAddTokens is not null)
        {
            var parsed = QuickEntryParser.Parse(quickAddTokens);
            task.DueDate = parsed.DueDate;
            task.Recurrence = parsed.Recurrence;
            task.RecurrenceInterval = parsed.RecurrenceInterval;
            foreach (var tag in parsed.Tags) task.Tags.Add(tag.ToLowerInvariant());
        }
        task.SortOrder = AllTasks.Count > 0 ? AllTasks.Max(t => t.SortOrder) + 1 : 0;
        AllTasks.Add(task);
        AttachTask(task);
        OnTaskChanged();
    }

    // Entry point for the reminder toast's "Mark Complete" button (see TrayIconService). The task
    // stays attached the whole time (see AttachTask/DetachTask), so just setting IsDone routes
    // through the normal Task_PropertyChanged pipeline - same undo entry and recurrence-spawn
    // behavior as checking it off in the list.
    public void CompleteTaskById(Guid taskId)
    {
        var task = AllTasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null || task.IsDone) return;
        task.IsDone = true;
    }

    // Entry point for the reminder toast's "Snooze 15m"/"Snooze 1 Hour" buttons. Rather than
    // changing the task's DueDate (which would be a real, saved edit the user didn't ask for),
    // this just clears the "already notified" flag so CheckReminders will pick the task back up,
    // and arms a one-shot timer so that happens close to the requested delay rather than waiting
    // for the next 15-minute polling tick.
    public void SnoozeTaskById(Guid taskId, TimeSpan duration) => _reminders.SnoozeTaskById(taskId, duration);

    public void Shutdown() => _tray.Dispose();

    public void UpdateSelectedTasks(IEnumerable<TaskItem> tasks)
    {
        SelectedTasks = tasks.ToList();
        OnPropertyChanged(nameof(SelectedTasks));
    }

    private bool FilterTask(object o)
    {
        var t = (TaskItem)o;
        var scope = _selectedSidebarItem ?? _allItem;

        var matchesScope = scope.Kind switch
        {
            SidebarFilterKind.Today => !t.IsClosed && !t.IsDone
                && (t.IsPinned || (t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Today)),
            SidebarFilterKind.Tomorrow => !t.IsClosed && !t.IsDone
                && t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today.AddDays(1),
            SidebarFilterKind.Someday => !t.IsClosed && !t.IsDone
                && !t.DueDate.HasValue,
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

        return TaskSearchMatcher.Matches(t, _searchText);
    }

    // AND-combined across every active filter (e.g. Overdue + High Priority narrows to tasks
    // matching both) - the traditional-filter-panel behavior this replaced the old single-select
    // QuickFilter with. Combining two filters that can never both be true (e.g. Due Today + No
    // Due Date) legitimately yields zero results rather than being special-cased - that's the
    // same AND semantics a user would expect from checking both boxes anywhere else.
    private bool MatchesQuickFilter(TaskItem t)
    {
        foreach (var filter in _activeQuickFilters)
        {
            if (!MatchesSingleQuickFilter(t, filter)) return false;
        }
        return true;
    }

    private static bool MatchesSingleQuickFilter(TaskItem t, QuickFilter filter) => filter switch
    {
        QuickFilter.Overdue => t.DueDate.HasValue && !t.IsDone && t.DueDate.Value.Date < DateTime.Today,
        QuickFilter.DueToday => t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today,
        QuickFilter.NoDueDate => !t.DueDate.HasValue,
        QuickFilter.Recurring => t.Recurrence != RecurrenceRule.None,
        QuickFilter.HasLink => TaskMediaHelper.HasLink(t),
        QuickFilter.HasAttachment => TaskMediaHelper.HasAttachment(t) || TaskMediaHelper.HasPhoto(t),
        QuickFilter.HighPriority => t.Priority == TaskPriority.High,
        _ => true
    };

    // Rebuilds the full 6-week (42-day) grid around CalendarMonth from scratch. Called whenever
    // the visible month changes, whenever ViewMode switches to Calendar, and (see the
    // AllTasks.CollectionChanged and Task_PropertyChanged hooks) whenever something that could
    // change a day's task membership happens while already in Calendar view. The actual grid math
    // lives in CalendarGridBuilder (pure, unit-tested); this just replaces CalendarDays wholesale
    // with its result rather than patching incrementally - simple and correct, and 42 cells is
    // cheap regardless.
    private void RefreshCalendarDays()
    {
        CalendarDays.Clear();
        foreach (var day in CalendarGridBuilder.BuildMonthGrid(_calendarMonth, AllTasks, DateTime.Now))
            CalendarDays.Add(day);
    }

    private void AttachTask(TaskItem task) => task.PropertyChanged += Task_PropertyChanged;

    private void DetachTask(TaskItem task) => task.PropertyChanged -= Task_PropertyChanged;

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskItem.ModifiedAt) || e.PropertyName == nameof(TaskItem.SortOrder)) return;
        if (sender is not TaskItem task) return;
        task.ModifiedAt = DateTime.UtcNow;

        // Only these two actually change which day (or whether at all) a task shows up on the
        // calendar grid - anything else (Text, Tags, ...) is already live via the pill's own
        // direct binding to this same TaskItem instance, so no rebuild is needed for those.
        if (ViewMode == ViewMode.Calendar && e.PropertyName is nameof(TaskItem.DueDate) or nameof(TaskItem.IsClosed))
            RefreshCalendarDays();

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
                        // The spawned occurrence may already have synced out - without a tombstone
                        // the next Drive merge sees it as a task this device just hasn't pulled
                        // yet and adds it straight back.
                        RecordTaskDeletionTombstone(spawned);
                        OnTaskChanged();
                    }
                }
                finally
                {
                    _isExecutingUndo = false;
                }
            });
        }

        // Every edit type routes through the same debounce as title typing - a checkbox toggle,
        // tag change, or block edit used to call OnTaskChanged() immediately, which serializes
        // the whole file (every task's full RTF blob) and re-runs RefreshTags()/
        // FilteredTasksView.Refresh() (O(all tasks) each) synchronously right then. With a large
        // list, and especially several quick edits in a row (bulk actions, rapid checking-off),
        // that stutters. The checkbox/tag/etc. itself still updates instantly either way - it's
        // bound directly to this same TaskItem instance - only the save-to-disk and list
        // re-sort/re-filter lag by up to the debounce interval now.
        RequestDebouncedSave();
    }

    public void UpdateTrayStatus()
    {
        var open = AllTasks.Count(t => !t.IsDone && !t.IsClosed);
        var today = AllTasks.Count(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today);
        var overdue = AllTasks.Count(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && t.DueDate.Value.Date < DateTime.Today);
        _tray.UpdateTrayTooltip(open, today, overdue);
    }

    public void OnTaskChanged()
    {
        Save();
        // RefreshTags/RefreshViews mutate TagItems/ViewItems in place (Move/Add/Remove - see
        // RefreshTags' own comment on why it diffs rather than Clear()s). OnTaskChanged can be
        // reached NESTED inside another control's own event handling - selecting a sidebar Tag or
        // View sets SelectedTask = null, whose setter flushes any pending debounced save
        // (FlushPendingSave -> CommitSave -> here), all while that very click is still being
        // processed by the ListBox whose ItemsSource this would mutate. Mutating a Selector's
        // ItemsSource mid-selection-change like that confused WPF badly enough that the click
        // sometimes silently failed to register (reported live: "at times I cannot even select the
        // tags or views" - reproduced specifically when a save happened to be pending, i.e. "at
        // times", not always). Posting to the dispatcher queue instead of calling these inline lets
        // the click's own selection-change finish first; this still runs on the very next UI tick,
        // so nothing here goes visibly stale.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshTags();
            RefreshViews();
        }));
        FilteredTasksView.Refresh();
        UpdateTrayStatus();
        _reminders.Reschedule();
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

    // Same in-place-diff approach as RefreshTags above, and for the same reason - a Clear() would
    // drop the ListBox's current selection even when the selected view is still present, just as a
    // new SidebarFilterItem instance.
    private void RefreshViews()
    {
        var desiredIds = new HashSet<string>(_state.SavedViews.Select(v => v.Id), StringComparer.Ordinal);

        for (var i = ViewItems.Count - 1; i >= 0; i--)
            if (!desiredIds.Contains(ViewItems[i].ViewId ?? ""))
                ViewItems.RemoveAt(i);

        var existingIds = new HashSet<string>(
            ViewItems.Select(v => v.ViewId ?? "").Where(id => id != ""),
            StringComparer.Ordinal);
        foreach (var view in _state.SavedViews)
            if (!existingIds.Contains(view.Id))
                ViewItems.Add(SidebarFilterItem.ForView(view));

        var ordered = _state.SavedViews
            .OrderBy(v => v.Label, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Id)
            .ToList();
        var currentPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ViewItems.Count; i++)
            currentPositions[ViewItems[i].ViewId ?? ""] = i;

        for (var i = 0; i < ordered.Count; i++)
        {
            if (currentPositions.TryGetValue(ordered[i], out var currentIndex) && currentIndex != i)
            {
                ViewItems.Move(currentIndex, i);
                currentPositions[ViewItems[i].ViewId ?? ""] = i;
            }
        }
    }

    // Called from MainWindow.xaml.cs after SaveViewPromptWindow returns a name (SaveViewRequested
    // triggers showing that dialog - see this class's own comment on that event).
    public void SaveCurrentSearchAsView(string label)
    {
        var query = BuildEffectiveSearchQuery();
        if (string.IsNullOrWhiteSpace(query)) return;

        _state.SavedViews.Add(new SavedView { Label = label.Trim(), Query = query });
        IsFilterPopupOpen = false;
        OnTaskChanged();
    }

    // Folds whatever isn't already text in the search box - active quick filters, a tag selected
    // in the sidebar - into the same tag:/is:/has:/due: operator syntax TaskSearchMatcher (and
    // Tasky Web's applySearch, which a synced view can just as easily be opened from) already
    // parse out of typed search text. Without this, saving a view while filtering by tag or by a
    // quick filter alone would either be blocked (see SaveViewCommand) or, worse, silently save an
    // empty/incomplete query that stops matching what was actually on screen.
    private string BuildEffectiveSearchQuery()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(SearchText)) parts.Add(SearchText.Trim());

        foreach (var filter in _activeQuickFilters)
        {
            if (QuickFilterToOperator(filter) is { } token && !HasToken(parts, token))
                parts.Add(token);
        }

        if (SelectedSidebarItem.Kind == SidebarFilterKind.Tag && SelectedSidebarItem.TagName is { } tagName)
        {
            var token = $"tag:{tagName}";
            if (!HasToken(parts, token)) parts.Add(token);
        }

        return string.Join(" ", parts);

        static bool HasToken(List<string> parts, string token)
            => parts.Any(p => p.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string? QuickFilterToOperator(QuickFilter filter) => filter switch
    {
        QuickFilter.Overdue => "is:overdue",
        QuickFilter.DueToday => "due:today",
        QuickFilter.NoDueDate => "due:none",
        QuickFilter.Recurring => "is:recurring",
        QuickFilter.HasLink => "has:link",
        QuickFilter.HasAttachment => "has:attachment",
        QuickFilter.HighPriority => "is:highpriority",
        _ => null
    };

    public IEnumerable<string> GetAllTagNames()
        => AllTasks.SelectMany(t => t.Tags).Distinct(StringComparer.OrdinalIgnoreCase);

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
