using System.IO;
using System.Text.Json;
using MajesticAssistant.Models;

namespace MajesticAssistant.Services;

/// <summary>Reads/writes <see cref="AppSettings"/> as a small JSON file next to the exe — same
/// on-disk convention as the kb/ and cache/ folders, no separate config format to learn.</summary>
public static class SettingsService
{
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Corrupt or hand-edited-wrong settings file — fall back to defaults rather than
            // block startup over something this low-stakes.
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
