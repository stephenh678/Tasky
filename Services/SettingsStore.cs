using System;
using System.IO;
using System.Text.Json;

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
        catch (JsonException)
        {
            return new Settings();
        }
    }

    public void Save(Settings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
