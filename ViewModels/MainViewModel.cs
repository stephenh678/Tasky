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
    private readonly GoogleDriveService _googleDrive = new();
    private readonly SyncCoordinator _sync;
    private readonly Settings _settings;
    // Never reassigned after construction (see LoadFile) - AllTasks below is a passthrough to
    // _state.Tasks, and FilteredTasksView wraps that same collection instance once in the
    // constructor, so both only keep working if _state.Tasks's own identity never changes.
    private readonly AppState _state = new();
    private string _currentFilePath = null!;

    private readonly SidebarFilterItem _todayItem = new(SidebarFilterKind.Today, "Today");
    private readonly SidebarFilterItem _allItem = new(SidebarFilterKind.All, "All Tasks");
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
                : new TaskDetailViewModel(value, OnTaskChanged, GetAllTagNames, RequestDebouncedSave, PushUndo);
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

    public bool ShowDoneCheckbox
    {
        get => _settings.ShowDoneCheckbox;
        set
        {
            if (_settings.ShowDoneCheckbox == value) return;
            _settings.ShowDoneCheckbox = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    // Only gates the once-a-day silent background check MainWindow runs after Loaded - Help >
    // Check for Updates always works regardless of this setting, same relationship
    // AutoBackupEnabled has to the manual Export/Import commands.
    public bool AutoCheckForUpdates
    {
        get => _settings.AutoCheckForUpdates;
        set
        {
            if (_settings.AutoCheckForUpdates == value) return;
            _settings.AutoCheckForUpdates = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    // Not bound in any XAML - just gives MainWindow's post-Loaded background check somewhere to
    // read/persist "did we already check today" without reaching into _settings directly.
    public DateTime? LastUpdateCheckUtc
    {
        get => _settings.LastUpdateCheckUtc;
        set
        {
            _settings.LastUpdateCheckUtc = value;
            _settingsStore.Save(_settings);
        }
    }

    // ROADMAP.md #135. AutoEmptyTrashIfNeeded() runs whenever this flips on (same as toggling the
    // day count) so turning it on doesn't wait for the next launch/sync to actually prune anything.
    public bool AutoEmptyTrashEnabled
    {
        get => _settings.AutoEmptyTrashEnabled;
        set
        {
            if (_settings.AutoEmptyTrashEnabled == value) return;
            _settings.AutoEmptyTrashEnabled = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
            if (value) AutoEmptyTrashIfNeeded();
        }
    }

    // Mirrors Tasky Web's setting-auto-empty-trash-days <select> options exactly.
    public int[] AutoEmptyTrashDayOptions { get; } = { 7, 14, 30, 60, 90 };

    public int AutoEmptyTrashDays
    {
        get => _settings.AutoEmptyTrashDays;
        set
        {
            var clamped = value < 1 ? 1 : value;
            if (_settings.AutoEmptyTrashDays == clamped) return;
            _settings.AutoEmptyTrashDays = clamped;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
            AutoEmptyTrashIfNeeded();
        }
    }

    // ROADMAP.md #135. No _settings-backed field or SetField/OnPropertyChanged guard against
    // redundant sets, unlike every other Settings-window toggle here - StartupService.IsEnabled
    // reads the registry Run key itself as the only source of truth (see its own doc comment), so
    // there's no cached local value to compare against or keep in sync.
    public bool StartWithWindowsEnabled
    {
        get => StartupService.IsEnabled;
        set => StartupService.SetEnabled(value);
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

    // Mirrors TodoStore's own AutoBackup* properties - kept in sync on every set (not just once at
    // startup) so a change made in the Settings window while the app is running takes effect on
    // the very next save, not just after a restart. All three setters push through the same
    // ApplyBackupSettingsToStore() the startup path already uses, rather than each one duplicating
    // its own single-field copy to _store - one shared place for "how Settings reaches TodoStore".
    public bool AutoBackupEnabled
    {
        get => _settings.AutoBackupEnabled;
        set
        {
            if (_settings.AutoBackupEnabled == value) return;
            _settings.AutoBackupEnabled = value;
            ApplyBackupSettingsToStore();
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public int AutoBackupIntervalMinutes
    {
        get => _settings.AutoBackupIntervalMinutes;
        set
        {
            if (_settings.AutoBackupIntervalMinutes == value) return;
            _settings.AutoBackupIntervalMinutes = value;
            ApplyBackupSettingsToStore();
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public int AutoBackupRetentionDays
    {
        get => _settings.AutoBackupRetentionDays;
        set
        {
            // A zero/negative value would mean "retain nothing" - every backup just made would
            // immediately qualify for pruning on the very next save, which isn't a meaningful
            // setting anyone would actually want, so floor it rather than accept it as entered.
            var requested = value;
            if (value < 1) value = 1;
            if (_settings.AutoBackupRetentionDays == value)
            {
                // The clamp changed what was typed (e.g. "0" -> 1) even though the stored setting
                // itself didn't move - still notify, or the TextBox keeps showing the un-clamped
                // text the user typed instead of the value that's actually in effect.
                if (requested != value) OnPropertyChanged();
                return;
            }
            _settings.AutoBackupRetentionDays = value;
            ApplyBackupSettingsToStore();
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

    // Focus Mode used to hide the sidebar entirely (width 0) - now it shows the same compact
    // icon-only rail the manual collapse toggle produces instead, so All Tasks/Tags/etc. stay one
    // click away without dragging the full 220px sidebar back in. The manual toggle (see
    // IsSidebarCollapsed) still tracks its own persisted state underneath; Focus Mode only forces
    // the icon-rail width while it's active and reverts to whatever that state was once it ends.
    public GridLength SidebarWidth => (IsFocusMode || IsSidebarCollapsed)
        ? new GridLength(46)
        : new GridLength(220);

    // Drives every "hide the label, icon only" binding in the sidebar (see SidebarItemTemplate,
    // and the TASKY/TAGS/VIEWS section headers in MainWindow.xaml) - true whenever the sidebar is
    // rendered at the 46px icon-rail width, whether that's from the user's own collapse toggle or
    // from Focus Mode forcing it.
    public bool IsSidebarShowingIconsOnly => IsFocusMode || IsSidebarCollapsed;

    public bool HasSeenWelcomeTour
    {
        get => _hasSeenWelcomeTour;
        set
        {
            if (!SetField(ref _hasSeenWelcomeTour, value)) return;
            _settings.HasSeenWelcomeTour = value;
            _settingsStore.Save(_settings);
        }
    }

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
    public RelayCommand NewFileCommand { get; private set; } = null!;
    public RelayCommand OpenFileCommand { get; private set; } = null!;
    public RelayCommand SaveFileAsCommand { get; private set; } = null!;
    public RelayCommand UndoCommand { get; private set; } = null!;
    public RelayCommand BulkMarkDoneCommand { get; private set; } = null!;
    public RelayCommand BulkTrashCommand { get; private set; } = null!;
    public RelayCommand BulkRestoreCommand { get; private set; } = null!;
    public RelayCommand BulkDeleteCommand { get; private set; } = null!;
    public RelayCommand BulkTogglePinCommand { get; private set; } = null!;
    public RelayCommand BulkSetDueDateCommand { get; private set; } = null!;
    public RelayCommand BulkAddTagCommand { get; private set; } = null!;
    public RelayCommand RestoreBackupCommand { get; private set; } = null!;
    public RelayCommand ExportBackupCommand { get; private set; } = null!;
    public RelayCommand ExportCalendarCommand { get; private set; } = null!;
    public RelayCommand ExportAllTasksCommand { get; private set; } = null!;
    public RelayCommand ImportBackupCommand { get; private set; } = null!;
    public RelayCommand ClearDebugLogCommand { get; private set; } = null!;
    public RelayCommand OpenDebugLogCommand { get; private set; } = null!;
    public RelayCommand GoogleDriveCommand { get; private set; } = null!;
    public RelayCommand SettingsCommand { get; private set; } = null!;
    public RelayCommand SyncGoogleDriveNowCommand { get; private set; } = null!;

    public bool IsGoogleDriveConnected => _googleDrive.IsAuthenticated;

    public string GoogleDriveStatusTooltip => _googleDrive.IsAuthenticated
        ? $"Google Drive: Connected ({_settings.GoogleDriveAccountEmail ?? "Authorized"})\nLast synced: {(_settings.LastGoogleDriveSyncTime.HasValue ? _settings.LastGoogleDriveSyncTime.Value.ToString("g") : "Never")}"
        : "Google Drive: Disconnected (Click to configure)";

    public event Action? FocusTitleRequested;

    // MainWindow.xaml.cs owns showing SaveViewPromptWindow (dialogs are a View concern, same as
    // LinkPromptWindow/TablePromptWindow are only ever constructed from code-behind) - this just
    // signals "the user asked to save the current search," mirroring FocusTitleRequested above.
    public event Action? SaveViewRequested;

    // Same "ViewModel signals, code-behind shows the dialog" split as SaveViewRequested above - a
    // multi-selection has no single date/tag to bind a picker to inline (unlike TaskDetailViewModel's
    // own due-date/tag controls), so bulk-editing either one needs its own small prompt window.
    public event Action? BulkSetDueDateRequested;
    public event Action? BulkAddTagRequested;

    public MainViewModel()
    {
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
        SidebarItems.Add(_allItem);
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
        };

        var initialPath = ResolveInitialFilePath();
        MediaPathResolver.SetDataFilePath(initialPath);

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounceTimer.Tick += (_, _) => CommitSave();

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

        var initialNotifiedIds = _settings.NotifiedTaskIds
            .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value);
        _reminders = new ReminderScheduler(() => AllTasks, () => RemindersEnabled, _tray,
            initialNotifiedIds, PersistNotifiedTaskIds);
        _reminders.Start();

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
        if (expired.Count == 0) return;

        foreach (var task in expired)
        {
            DetachTask(task);
            AllTasks.Remove(task);
            RecordTaskDeletionTombstone(task);
            CleanupTaskAttachments(task);
            if (SelectedTask == task) SelectedTask = null;
        }
        OnTaskChanged();
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

    // New/Open/Save As/Restore Backup - commands that swap out which .tasky file is open or
    // touch the file on disk directly, rather than mutating in-memory task state.
    private void InitializeFileCommands()
    {
        NewFileCommand = new RelayCommand(async _ => await CreateNewLocalFileForSyncAsync());

        OpenFileCommand = new RelayCommand(_ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open Tasky File",
                Filter = "Tasky files (*.tasky)|*.tasky|JSON files (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            LoadFile(dialog.FileName);

            // Renaming a synced .tasky file outside the app (Explorer) then reopening it here
            // looks, from the app's perspective, identical to opening a brand-new file - there's
            // no reliable way to tell "this is file X under a new name" from "this really is a
            // new file" by name alone. Rather than silently create a duplicate remote file on the
            // next sync, nudge toward the explicit fix (Choose File) - but only once this device
            // has actual sync history to plausibly be renaming *from*, so a first-time Drive user
            // opening an old file doesn't get an unexplained warning.
            if (_settings.IsGoogleDriveEnabled && _googleDrive.IsAuthenticated
                && _settings.GoogleDriveFileIdsByFile.Count > 0
                && !_settings.GoogleDriveFileIdsByFile.ContainsKey(Path.GetFileName(dialog.FileName).ToLowerInvariant()))
            {
                SaveStatusText = "This file isn't linked to Google Drive yet - syncing will create a new remote copy. If it's a renamed version of a file you already sync, use Google Drive → Choose File to link it instead.";
            }
        });

        SaveFileAsCommand = new RelayCommand(async _ =>
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
            MediaPathResolver.SetDataFilePath(_currentFilePath);
            // ROADMAP.md #124: SaveAsync awaited directly (this handler is already off the sync
            // call stack once ShowDialog returns) instead of the blocking Save()/GetResult() bridge.
            await _store.SaveAsync(_state, _currentFilePath);
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
                MarkAllTasksRestoredAndSave();
            }
            finally
            {
                _isRestoringBackup = false;
            }
        }, _ => !_isRestoringBackup);

        // Export/Import Full Backup - a portable .zip of the data file plus every attachment it
        // references, for moving everything to a new machine or just keeping an offline copy.
        // Distinct from Save As (data only, no attachments) and Restore from Backup (data only,
        // and only ever from this same machine's own Backups\ history).
        ExportBackupCommand = new RelayCommand(async _ =>
        {
            await FlushPendingSaveAsync();
            var dialog = new SaveFileDialog
            {
                Title = "Export Full Backup",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = $"Tasky Backup {DateTime.Now:yyyy-MM-dd}.zip"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var (included, missing) = BackupService.Export(_currentFilePath, AllTasks, dialog.FileName);
                var message = $"Exported {AllTasks.Count} task(s) and {included} attachment(s) to:\n{dialog.FileName}";
                if (missing > 0)
                    message += $"\n\n{missing} attachment(s) referenced by your tasks couldn't be found locally and were skipped.";
                ThemedMessageBox.Show(message, "Export Full Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't export: {ex.Message}", "Export Full Backup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        ExportCalendarCommand = new RelayCommand(_ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export Due Dates to Calendar",
                Filter = "iCalendar file (*.ics)|*.ics",
                FileName = $"Tasky Due Dates {DateTime.Now:yyyy-MM-dd}.ics"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var count = ExportService.ExportToICalendar(AllTasks, dialog.FileName);
                var message = count == 0
                    ? "No open tasks have a due date set, so nothing was exported."
                    : $"Exported {count} due date(s) to:\n{dialog.FileName}\n\nImport this file into Google Calendar, Outlook, or Apple Calendar.";
                ThemedMessageBox.Show(message, "Export Due Dates to Calendar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't export: {ex.Message}", "Export Due Dates to Calendar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        // ROADMAP.md #135: whole-list export, alongside the existing per-note "Export Selected
        // Note..." (ExportNote_Click in MainWindow.xaml.cs). Doesn't need the live FlowDocument
        // that per-note export reads from, so - unlike that one - this can be a plain command
        // here instead of MainWindow.xaml.cs code-behind.
        ExportAllTasksCommand = new RelayCommand(_ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export All Tasks",
                Filter = "Markdown Document (*.md)|*.md|HTML Document (*.html)|*.html",
                FileName = $"Tasky Export {DateTime.Now:yyyy-MM-dd}.md"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var ext = Path.GetExtension(dialog.FileName)?.ToLowerInvariant() ?? string.Empty;
                if (ext == ".html" || ext == ".htm")
                    ExportService.ExportAllToHtml(AllTasks, dialog.FileName);
                else
                    ExportService.ExportAllToMarkdown(AllTasks, dialog.FileName);
                ThemedMessageBox.Show($"Exported all tasks to:\n{dialog.FileName}", "Export All Tasks", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't export: {ex.Message}", "Export All Tasks", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        ImportBackupCommand = new RelayCommand(async _ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import Full Backup",
                Filter = "Zip archive (*.zip)|*.zip"
            };
            if (dialog.ShowDialog() != true) return;

            string extractedDataFile;
            IReadOnlyList<string> attachmentFiles;
            int backupTaskCount;
            try
            {
                (extractedDataFile, attachmentFiles) = BackupService.ExtractToTemp(dialog.FileName);
                // AllTasks is the CURRENTLY open file's tasks (the ones about to be replaced), not
                // the backup's - reading the extracted backup itself is the only way to show its
                // real count here, same as how the Drive sync merge peeks at a downloaded remote
                // file. Kept in this same try/catch since a corrupt backup can fail either step.
                // ROADMAP.md #124: awaited directly instead of the blocking Load()/GetResult() bridge - safe here since ImportBackupCommand's handler is already async.
                backupTaskCount = (await _store.LoadAsync(extractedDataFile)).Tasks.Count;
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"Couldn't read this backup:\n{ex.Message}", "Import Full Backup",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var confirm = ThemedMessageBox.Show(
                $"This will replace your currently open task list with the backup's {backupTaskCount} " +
                $"task(s) and restore its {attachmentFiles.Count} attachment(s).\n\n" +
                "Your current file will be backed up first, so this can be undone by restoring it from Restore from Backup.",
                "Import Full Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            _isRestoringBackup = true;
            try
            {
                await FlushPendingSaveAsync();
                BackupService.RestoreAttachments(attachmentFiles);
                _store.RestoreBackup(extractedDataFile, _currentFilePath);
                LoadFile(_currentFilePath);
                MarkAllTasksRestoredAndSave();

                ThemedMessageBox.Show($"Imported {attachmentFiles.Count} attachment(s) and restored your tasks.",
                    "Import Full Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't import: {ex.Message}", "Import Full Backup", MessageBoxButton.OK, MessageBoxImage.Error);
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

        OpenDebugLogCommand = new RelayCommand(_ =>
        {
            AppLogger.Info("MainViewModel", "User requested to open debug log file");
            var result = AppLogger.OpenLogFile(out var error);
            switch (result)
            {
                case AppLogger.OpenLogFileResult.NotCreatedYet:
                    ThemedMessageBox.Show($"Log file not created yet:\n{AppLogger.LogFilePath}", "Debug Log",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case AppLogger.OpenLogFileResult.Failed:
                    ThemedMessageBox.Show($"Unable to open log file:\n{error}", "Debug Log",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        });

        GoogleDriveCommand = new RelayCommand(_ => OpenSettingsWindow(SettingsSection.GoogleDrive));

        SyncGoogleDriveNowCommand = new RelayCommand(async _ => await PerformGoogleDriveSyncAsync());

        SettingsCommand = new RelayCommand(_ => OpenSettingsWindow(SettingsSection.General));
    }

    // Applies loaded Settings to TodoStore once at startup - AutoBackupEnabled/IntervalMinutes/
    // RetentionDays above keep them in sync on every subsequent change, but the initial load
    // doesn't go through those property setters (nothing "changed" yet), so this covers that.
    private void ApplyBackupSettingsToStore()
    {
        _store.AutoBackupEnabled = _settings.AutoBackupEnabled;
        _store.AutoBackupIntervalMinutes = _settings.AutoBackupIntervalMinutes;
        _store.AutoBackupRetentionDays = _settings.AutoBackupRetentionDays;
    }

    private void OpenSettingsWindow(SettingsSection initialSection)
    {
        var driveControl = new GoogleDriveSettingsControl(
            _googleDrive, _settings, _settingsStore, () => PerformGoogleDriveSyncAsync(),
            AttachExistingGoogleDriveFileAsync, CreateNewLocalFileForSyncAsync);

        var window = new SettingsWindow(this, driveControl, initialSection)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        OnPropertyChanged(nameof(IsGoogleDriveConnected));
        OnPropertyChanged(nameof(GoogleDriveStatusTooltip));
    }

    // Attaches to a file the user picked from their existing Google Drive files. Returns false
    // only when the user explicitly backed out of picking a destination for a genuinely separate
    // file - the caller uses that to know a sync shouldn't run right afterward.
    private async Task<bool> AttachExistingGoogleDriveFileAsync(string remoteFileId, string remoteFileName)
    {
        await FlushPendingSaveAsync();

        var currentFileName = Path.GetFileName(_currentFilePath);
        if (string.Equals(currentFileName, remoteFileName, StringComparison.OrdinalIgnoreCase))
        {
            // The remote file you picked shares this device's current local filename - Tasky.tasky
            // is every install's default, so this is the common case (attaching a second device to
            // an existing synced file), not a rare collision. Treating it as "download a separate
            // copy, ask where to put it" would either overwrite whatever's already open here or -
            // if you picked a different destination - silently abandon it, since the app would
            // switch to the new file and never look at the old one again. Just linking the ID here
            // and letting the caller's normal sync pass run right after (as it always does) merges
            // the remote content into what's already open instead, the same way any other sync
            // would - no separate download-and-switch step needed since it's already the open file.
            var fileKey = currentFileName.ToLowerInvariant();
            _sync.MarkLegacyAttachmentsOwnerIfUnset(fileKey);
            _settings.GoogleDriveFileIdsByFile[fileKey] = remoteFileId;
            _settingsStore.Save(_settings);
            return true;
        }

        // Different filename - this really is a separate file, so download it to its own local
        // path and make it the active file.
        var defaultDir = Path.GetDirectoryName(TodoStore.GetDefaultDataFilePath())
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");
        Directory.CreateDirectory(defaultDir);
        var targetPath = Path.Combine(defaultDir, remoteFileName);

        // Don't silently overwrite an unrelated local file that happens to share this name -
        // let the user pick a different destination instead. A directory of the same name is
        // just as much a collision as a file (Directory.CreateDirectory further up won't create
        // "Documents\Tasky\Tasky.tasky" as a folder itself, but nothing rules out one already
        // existing there from outside the app).
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            // The Save As dialog that follows is easy to misread as part of a normal download -
            // explain up front why it's asking, since this only happens when the file being
            // attached has nothing to do with whatever already has this name locally.
            ThemedMessageBox.Show(
                $"A local file named \"{remoteFileName}\" already exists that isn't related to the " +
                "file you just selected. Choose a different name or location to save the downloaded " +
                "copy so it doesn't overwrite that file.",
                "Naming Conflict", MessageBoxButton.OK, MessageBoxImage.Information);

            var dialog = new SaveFileDialog
            {
                Title = "Save Downloaded Tasky File As",
                Filter = "Tasky files (*.tasky)|*.tasky",
                FileName = remoteFileName,
                InitialDirectory = defaultDir
            };
            if (dialog.ShowDialog() != true) return false;
            targetPath = dialog.FileName;
        }

        var targetFileKey = Path.GetFileName(targetPath).ToLowerInvariant();
        // This device might be attaching to the one file that already has real attachments
        // sitting in the shared flat Drive layout (e.g. a fresh install picking up a
        // long-established file) - if it has no legacy owner of its own yet, assume this could
        // be it, so the download below actually finds them instead of coming up empty.
        _sync.MarkLegacyAttachmentsOwnerIfUnset(targetFileKey);

        await _googleDrive.DownloadFileAsync(remoteFileId, targetPath, downloadAttachments: true, _settings, _settingsStore);
        LoadFile(targetPath);

        _settings.GoogleDriveFileIdsByFile[targetFileKey] = remoteFileId;
        _settingsStore.Save(_settings);
        return true;
    }

    // Same New File flow as NewFileCommand, exposed for the Google Drive "Choose File" picker so
    // choosing "Create New" there doesn't just silently reuse whatever file already happens to be
    // open - it's an explicit choice, same as picking an existing remote file is.
    private async Task<bool> CreateNewLocalFileForSyncAsync()
    {
        FlushPendingSave();
        var dialog = new SaveFileDialog
        {
            Title = "New Tasky File",
            Filter = "Tasky files (*.tasky)|*.tasky",
            FileName = "Tasky.tasky"
        };
        if (dialog.ShowDialog() != true) return false;

        // ROADMAP.md #124: awaited directly instead of the blocking Save()/GetResult() bridge.
        await _store.SaveAsync(new AppState(), dialog.FileName);
        LoadFile(dialog.FileName);
        return true;
    }

    public async Task PerformGoogleDriveSyncAsync(bool isSilentOnExit = false)
    {
        IsSyncing = true;
        SyncProgressPercent = 0;
        try
        {
            await _sync.PerformSyncAsync(
                _state,
                _currentFilePath,
                FlushPendingSaveAsync,
                remoteState =>
                {
                    var result = MergeRemoteState(remoteState);
                    RefreshTags();
                    RefreshViews();
                    FilteredTasksView.Refresh();
                    return result;
                },
                status => SaveStatusText = status,
                () => OpenSettingsWindow(SettingsSection.GoogleDrive),
                isSilentOnExit,
                percent => SyncProgressPercent = percent);

            // Deliberately after PerformSyncAsync fully returns, not inside its merge callback
            // above - AutoEmptyTrashIfNeeded's OnTaskChanged() calls Save(), which writes the same
            // local file SyncCoordinator is still mid-writing/uploading at that point (and would
            // clobber SaveStatusText's "Syncing..." with "Saving..." while that's still visible).
            // Any pruning found here rides along on the next sync instead, same as any other edit.
            AutoEmptyTrashIfNeeded();
        }
        finally
        {
            // Left visible at whatever percent it reached (100 on success) for a beat rather than
            // snapped back to 0 - IsSyncing=false hides the bar entirely via its Visibility binding,
            // so the exact leftover percent doesn't matter once that happens.
            IsSyncing = false;
        }

        OnPropertyChanged(nameof(GoogleDriveStatusTooltip));
        OnPropertyChanged(nameof(IsGoogleDriveConnected));
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
            var targets = SelectedTasks.Where(t => !t.IsDone).ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Mark {targets.Count} task(s) complete?",
                "Mark Complete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var t in targets) t.IsDone = true;
        }, _ => SelectedTasks.Count > 0);

        BulkTrashCommand = new RelayCommand(_ =>
        {
            var targets = SelectedTasks.Where(t => !t.IsClosed).ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Move {targets.Count} task(s) to Trash?",
                "Move to Trash", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

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

            var result = ThemedMessageBox.Show($"Restore {targets.Count} task(s) from Trash?",
                "Restore from Trash", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

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
            var targets = SelectedTasks.ToList();
            if (targets.Count == 0) return;

            var result = ThemedMessageBox.Show($"Toggle pin on {targets.Count} task(s)?",
                "Toggle Pin", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var t in targets) t.IsPinned = !t.IsPinned;
            PushUndo($"Toggle pin on {targets.Count} task(s)", () =>
            {
                foreach (var t in targets) t.IsPinned = !t.IsPinned;
            });
        }, _ => SelectedTasks.Count > 0);

        BulkSetDueDateCommand = new RelayCommand(_ => BulkSetDueDateRequested?.Invoke(), _ => SelectedTasks.Count > 0);
        BulkAddTagCommand = new RelayCommand(_ => BulkAddTagRequested?.Invoke(), _ => SelectedTasks.Count > 0);
    }

    // Called from MainWindow.xaml.cs after BulkDueDatePromptWindow returns (BulkSetDueDateRequested
    // triggers showing that dialog). date is null for "Clear Due Date," not "user cancelled" -
    // cancelling never calls this at all. DueDate is a plain SetField-backed property (unlike
    // Tags/Body below), so Task_PropertyChanged picks up the change and bumps ModifiedAt on its own -
    // no manual touch needed, same as every other single-task due-date edit.
    public void ApplyBulkDueDate(DateTime? date)
    {
        var targets = SelectedTasks.ToList();
        if (targets.Count == 0) return;

        var message = date is null
            ? $"Clear the due date on {targets.Count} task(s)?"
            : $"Set the due date to {date:M/d/yyyy} on {targets.Count} task(s)?";
        var result = ThemedMessageBox.Show(message, "Set Due Date", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // Snapshot each task's own prior due date (not just "clear back to null") since they didn't
        // necessarily share one before the bulk edit - Task_PropertyChanged doesn't special-case
        // DueDate the way it does IsDone, so this needs its own explicit PushUndo.
        var previous = targets.Select(t => (Task: t, DueDate: t.DueDate)).ToList();
        foreach (var t in targets) t.DueDate = date;

        PushUndo($"Set due date on {targets.Count} task(s)", () =>
        {
            foreach (var (task, due) in previous) task.DueDate = due;
        });
    }

    // Called from MainWindow.xaml.cs after BulkAddTagPromptWindow returns a tag (BulkAddTagRequested
    // triggers showing that dialog). Mirrors TaskDetailViewModel.AddTagCommand exactly, including its
    // own manual ModifiedAt bump - Tags is a plain ObservableCollection<string> with no SetField
    // wrapper, so Add() never raises TaskItem.PropertyChanged and the sync merge would otherwise never
    // see the new tag as an edit worth keeping.
    public void ApplyBulkTag(string rawTag)
    {
        var tag = TagUtils.Sanitize(rawTag);
        if (tag.Length == 0) return;
        var targets = SelectedTasks.ToList();
        if (targets.Count == 0) return;

        var result = ThemedMessageBox.Show($"Add the \"{tag}\" tag to {targets.Count} task(s)?",
            "Add Tag", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // Only the tasks that didn't already carry this tag actually change - undo must revert
        // exactly that subset, not every selected task, or it would strip a tag a task already had
        // on its own before this bulk edit ever ran.
        var added = new List<TaskItem>();
        foreach (var t in targets)
        {
            if (t.Tags.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
            t.Tags.Add(tag);
            t.ModifiedAt = DateTime.UtcNow;
            added.Add(t);
        }
        OnTaskChanged();

        if (added.Count == 0) return;
        PushUndo($"Add tag \"{tag}\" to {added.Count} task(s)", () =>
        {
            foreach (var t in added)
            {
                for (var i = t.Tags.Count - 1; i >= 0; i--)
                    if (t.Tags[i].Equals(tag, StringComparison.OrdinalIgnoreCase))
                        t.Tags.RemoveAt(i);
                t.ModifiedAt = DateTime.UtcNow;
            }
            OnTaskChanged();
        });
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
            existing.Timestamp = DateTime.UtcNow;
        else
            _state.DeletedTasks.Add(new TaskSyncRecord { TaskId = task.Id, Timestamp = DateTime.UtcNow });
    }

    // Belt-and-suspenders for data written before RecordTaskDeletionTombstone deduplicated on
    // write (or any other source of a malformed file, e.g. hand-edited) - MergeRemoteState builds
    // a Dictionary keyed by TaskId from this list, which throws on a duplicate key, so a file
    // that already has one has to be cleaned up before it ever reaches that point. Keeps the
    // latest timestamp per TaskId, applied to both local (on load) and remote (right after
    // download) so neither side can be the one that crashes the merge.
    //
    // The decision logic itself (which tasks to add/update/remove, tombstone union) lives in
    // TaskSyncMerge.ComputeMergePlan - a pure function with no dependency on AllTasks or WPF
    // binding, so it's unit-testable without constructing a MainViewModel. This method is just
    // the thin, UI-bound half: apply that plan to AllTasks/AttachTask/DetachTask/SelectedTask.
    private (int Added, int Updated, int Removed, int Conflicted) MergeRemoteState(AppState remoteState)
    {
        var lastSyncTimeUtc = _settings.LastGoogleDriveSyncTime?.ToUniversalTime();
        var plan = TaskSyncMerge.ComputeMergePlan(_state.Tasks, remoteState.Tasks, _state.DeletedTasks, remoteState.DeletedTasks, lastSyncTimeUtc);

        foreach (var remoteTask in plan.TasksToAdd)
        {
            AllTasks.Add(remoteTask);
            AttachTask(remoteTask);
        }

        foreach (var localTask in plan.TasksToRemove)
        {
            DetachTask(localTask);
            AllTasks.Remove(localTask);
            if (SelectedTask == localTask) SelectedTask = null;
        }

        // Detached first, since TaskItem's property setters trigger Task_PropertyChanged while
        // attached, which would stamp ModifiedAt to "now" (clobbering the timestamp being restored
        // here) and can spawn a recurring-task occurrence or push an undo entry - none of which
        // belong in a sync merge.
        foreach (var (localTask, remoteTask) in plan.TasksToUpdate)
        {
            DetachTask(localTask);
            TaskSyncMerge.ApplyTaskFields(localTask, remoteTask);
            AttachTask(localTask);
        }

        // ROADMAP.md #119: surfaced instead of the losing edit just disappearing - see
        // TaskSyncMerge.CreateConflictedCopy. Added like any other new task (undo doesn't apply to
        // a sync merge, same as TasksToAdd above).
        foreach (var conflictedCopy in plan.ConflictedCopiesToAdd)
        {
            AllTasks.Add(conflictedCopy);
            AttachTask(conflictedCopy);
        }

        _state.DeletedTasks.AddRange(plan.TombstonesToAdd);

        var (mergedViews, mergedDeletedViewIds) = SavedViewSyncMerge.Merge(
            _state.SavedViews, remoteState.SavedViews, _state.DeletedSavedViewIds, remoteState.DeletedSavedViewIds);
        _state.SavedViews = mergedViews;
        _state.DeletedSavedViewIds = mergedDeletedViewIds;

        return (plan.TasksToAdd.Count, plan.TasksToUpdate.Count, plan.TasksToRemove.Count, plan.ConflictedCopiesToAdd.Count);
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
    public void AddQuickTask(string title)
    {
        var parsed = QuickEntryParser.Parse(title);
        var text = string.IsNullOrWhiteSpace(parsed.Text) ? title : parsed.Text;

        var task = new TaskItem { Text = text, DueDate = parsed.DueDate };
        foreach (var tag in parsed.Tags) task.Tags.Add(tag.ToLowerInvariant());

        AllTasks.Add(task);
        AttachTask(task);
        OnTaskChanged();
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
            foreach (var tag in parsed.Tags) task.Tags.Add(tag.ToLowerInvariant());
        }
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

    private void PersistNotifiedTaskIds(IEnumerable<Guid> ids)
    {
        _settings.NotifiedTaskIds = ids.Select(id => id.ToString()).ToList();
        _settingsStore.Save(_settings);
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

    // ROADMAP.md #31: interval multiplies the step (Weekly + interval 2 = every 2 weeks) instead of
    // recurrence being fixed at "every 1". Mirrors docs/js/model.js's nextDueDate exactly.
    internal static DateTime NextDueDate(DateTime from, RecurrenceRule rule, int interval) => rule switch
    {
        RecurrenceRule.Daily => from.AddDays(interval),
        RecurrenceRule.Weekly => from.AddDays(7 * interval),
        RecurrenceRule.Monthly => from.AddMonths(interval),
        RecurrenceRule.Yearly => from.AddYears(interval),
        _ => from
    };

    // Completing a recurring task doesn't just close it out - it spawns the next occurrence
    // (title, due date advanced by the rule/interval, tags) so the series continues. The completed
    // instance still moves into Closed as normal.
    private TaskItem SpawnNextOccurrence(TaskItem completed)
    {
        var next = new TaskItem
        {
            Text = completed.Text,
            DueDate = NextDueDate(RecurrenceAnchor(completed.DueDate), completed.Recurrence, completed.RecurrenceInterval),
            Recurrence = completed.Recurrence,
            RecurrenceInterval = completed.RecurrenceInterval,
            Tags = new ObservableCollection<string>(completed.Tags)
        };
        AllTasks.Add(next);
        AttachTask(next);
        return next;
    }

    // ROADMAP.md #31: advancing straight from a stale DueDate meant completing a long-overdue
    // recurring task (e.g. a daily task overdue by 2 weeks) spawned a next occurrence that was
    // still overdue, rather than one due tomorrow. Clamp the anchor date to today when the task
    // was already overdue, but keep its time-of-day (e.g. a "@5pm" reminder stays at 5pm) - only
    // the date component was stale, not the time. Mirrors docs/js/model.js's recurrenceAnchor
    // exactly.
    internal static DateTime RecurrenceAnchor(DateTime? dueDate)
    {
        var anchor = dueDate ?? DateTime.Today;
        return anchor.Date < DateTime.Today ? DateTime.Today.Add(anchor.TimeOfDay) : anchor;
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
        _reminders.ClearNotified();
        OnPropertyChanged(nameof(UndoMenuLabel));

        _currentFilePath = path;
        MediaPathResolver.SetDataFilePath(path);

        // _state is never reassigned (see its declaration) - AllTasks and FilteredTasksView both
        // wrap _state.Tasks by reference, so opening a different file means repopulating that
        // same collection in place from a freshly-loaded AppState, not swapping _state itself out
        // for a new one (which would leave FilteredTasksView pointed at the old, now-orphaned
        // collection).
        //
        // Deliberately still the blocking Load(), not LoadAsync (ROADMAP.md #124's other call
        // sites - SaveFileAsCommand, CreateNewLocalFileForSync, ImportBackupCommand - now await the
        // async path). LoadFile itself is called from six places including the constructor's
        // synchronous startup path (line ~502), which can't await without either going fully
        // fire-and-forget there (a visible empty-window flash on launch) or a larger restructure -
        // same "high-blast-radius, left for a dedicated pass" call the #15 FileSessionManager
        // extraction made about this exact method.
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
        _state.DeletedTasks = TaskSyncMerge.DeduplicateTombstones(loaded.DeletedTasks);

        AppLogger.Info("MainViewModel", $"LoadFile: Loaded {loaded.Tasks.Count} tasks into AllTasks");

        SelectedTask = null;
        SelectedSidebarItem = _allItem;
        AutoEmptyTrashIfNeeded();
        RefreshTags();
        RefreshViews();
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

    // Shared by RestoreBackupCommand and ImportBackupCommand, called right after LoadFile reloads
    // a backup's tasks - they carry whatever ModifiedAt they had at backup time, almost always
    // older than what's since accumulated on remote. Left alone, the very next Drive sync's
    // last-write-wins merge (MergeRemoteState) would treat the restored copy as the stale side
    // and silently overwrite it right back with the pre-restore remote state, defeating the
    // restore the user just confirmed. Each task gets a distinct tick offset off the same restore
    // moment rather than one identical DateTime.Now for all of them, so "sort by Modified" doesn't
    // collapse into an arbitrary tie for every task until each is edited again.
    //
    // Known, deliberate tradeoff: this also makes a restored task win against a remote TOMBSTONE,
    // not just a remote edit - MergeRemoteState's local-only-task removal only fires when
    // localTask.ModifiedAt <= the tombstone's deletedAt, which can never be true once ModifiedAt
    // is bumped to "now". So if a task was deleted on another device sometime after this backup's
    // snapshot was taken but before this restore, restoring will resurrect it on the next sync.
    // Fixing that properly means teaching MergeRemoteState to tell "beat a stale edit" apart from
    // "beat a newer deletion" for a restored task - real surgery on the shared merge algorithm for
    // a narrow edge case (needs both an old backup restore AND a genuine cross-device delete of
    // that exact task in the gap between snapshot and restore). Left as-is on purpose rather than
    // risking that code for this. Don't "fix" this reactively without re-reading this comment.
    private void MarkAllTasksRestoredAndSave()
    {
        var restoredAt = DateTime.UtcNow;
        var offset = 0;
        foreach (var task in AllTasks)
            task.ModifiedAt = restoredAt.AddTicks(offset++);
        RequestDebouncedSave();
    }

    private bool FilterTask(object o)
    {
        var t = (TaskItem)o;
        var scope = _selectedSidebarItem ?? _allItem;

        var matchesScope = scope.Kind switch
        {
            SidebarFilterKind.Today => !t.IsClosed && !t.IsDone
                && (t.IsPinned || (t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Today)),
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
        if (e.PropertyName == nameof(TaskItem.ModifiedAt)) return;
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
