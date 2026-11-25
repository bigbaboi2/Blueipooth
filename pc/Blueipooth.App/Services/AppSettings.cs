using System.IO;
using System.Text.Json;

namespace Blueipooth.App.Services;

public enum AppThemeMode
{
    Midnight,
    Ocean,
    Light
}

public enum WindowCloseBehavior
{
    MinimizeToTray,
    ExitApplication
}

public sealed class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueipooth");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppThemeMode Theme { get; set; } = AppThemeMode.Midnight;
    public WindowCloseBehavior CloseBehavior { get; set; } = WindowCloseBehavior.MinimizeToTray;
    public bool ExpandLogsByDefault { get; set; } = false;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall back to defaults if settings file is unreadable.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore non-fatal settings persistence errors.
        }
    }
}
