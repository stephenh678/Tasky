using System;
using System.IO;
using System.Text.Json;
using TodoApp;

namespace TodoApp.Services;

public class SettingsStore
{
    private readonly string _filePath;
    private int _failureCount;

    public SettingsStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tasky");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "settings.json");
        _failureCount = 0;
    }

    public Settings Load()
    {
        if (!File.Exists(_filePath))
            return new Settings();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    // Settings are convenience state (window position, theme, last file) - never worth blocking
    // or crashing over, so a failed write (locked file, permissions) is logged and swallowed
    // rather than surfaced. This matters beyond just this call site: it's what keeps the
    // MainWindow Closing handler's SaveWindowState call from being able to abort shutdown.
    /// <summary>
    /// Saves settings to disk. Returns false if save failed (locked file, permissions, etc.).
    /// Caller can check return value to notify user after repeated failures.
    /// </summary>
    public bool Save(Settings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
            _failureCount = 0;
            return true;
        }
        catch (Exception ex)
        {
            _failureCount++;
            App.LogException(ex);
            return false;
        }
    }
    
    /// <summary>
    /// Gets the number of consecutive save failures (resets to 0 on successful save).
    /// </summary>
    public int GetFailureCount() => _failureCount;
}
