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

// Settings-backed properties (each one persists through SettingsStore the moment it changes)
// and the Settings window itself.
public partial class MainViewModel
{
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
            if (value) _reminders.CheckReminders(); else _reminders.Reschedule();
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

    // Off by default: most tasks never need subtasks, so the editor stays uncluttered and the
    // section is one click away via "Add subtasks". TaskDetailViewModel reads this through a
    // callback rather than a snapshot, so the open task has to be told the answer changed.
    public bool AlwaysShowSubtasks
    {
        get => _settings.AlwaysShowSubtasks;
        set
        {
            if (_settings.AlwaysShowSubtasks == value) return;
            _settings.AlwaysShowSubtasks = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
            SelectedTaskDetail?.NotifySubtasksVisibilityChanged();
        }
    }

    // Only gates the once-a-day silent background check MainWindow runs after Loaded - Help >
    // Check for Updates always works regardless of this setting, same relationship
    // AutoBackupEnabled has to the manual Export/Import commands.
    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value) return;
            _settings.CloseToTray = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    // Always a valid, normalised gesture string: anything unparseable (a hand-edited
    // settings.json) reads back as the default rather than leaving quick-add with no hotkey.
    // MainWindow owns the actual RegisterHotKey call and re-registers on QuickAddHotkeyChanged.
    public string QuickAddHotkey
    {
        get => HotkeyGesture.ParseOrDefault(_settings.QuickAddHotkey).ToString();
        set
        {
            if (!HotkeyGesture.TryParse(value, out var gesture)) return;
            var normalised = gesture.ToString();
            if (_settings.QuickAddHotkey == normalised) return;
            _settings.QuickAddHotkey = normalised;
            _settingsStore.Save(_settings);
            _tray.QuickAddHotkeyText = normalised;
            OnPropertyChanged();
            QuickAddHotkeyChanged?.Invoke();
        }
    }

    public event Action? QuickAddHotkeyChanged;

    public bool HasSeenCloseToTrayNotice
    {
        get => _settings.HasSeenCloseToTrayNotice;
        set
        {
            if (_settings.HasSeenCloseToTrayNotice == value) return;
            _settings.HasSeenCloseToTrayNotice = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public bool HasSeenUnmanagedInstallNotice
    {
        get => _settings.HasSeenUnmanagedInstallNotice;
        set
        {
            if (_settings.HasSeenUnmanagedInstallNotice == value) return;
            _settings.HasSeenUnmanagedInstallNotice = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

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

    private void PersistNotifiedTaskIds(IEnumerable<NotifiedReminder> entries)
    {
        _settings.NotifiedTaskIds = entries.Select(entry => entry.ToString()).ToList();
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
}
