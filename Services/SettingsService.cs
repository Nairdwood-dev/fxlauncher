using System.IO;
using System.Text.Json;
using Nairdwood.Launcher.Models;

namespace Nairdwood.Launcher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nairdwood Launcher");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new LauncherSettings();
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public void Reset()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }
}
