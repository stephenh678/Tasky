using System;
using System.IO;
using System.Text.Json;
using TodoApp;

namespace TodoApp.Services;

public class SettingsStore
{
    private readonly string _filePath;

    public SettingsStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tasky");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "settings.json");
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
    public void Save(Settings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            App.LogException(ex);
        }
    }
}
