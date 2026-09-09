using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MonitorSync.App;

public sealed record UserSettings(string? MonitorId = null, string? EndpointId = null, bool SyncEnabled = false);

public static class SettingsStore
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorSync");
    private static readonly object LogGate = new();
    private static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public static UserSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { Log("Settings could not be read: " + e.Message); return new(); }
    }

    public static void Save(UserSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("MonitorSync") is string value && !string.IsNullOrWhiteSpace(value);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value)
            {
                var executable = Environment.ProcessPath ?? throw new IOException("Cannot determine the application path.");
                key.SetValue("MonitorSync", $"\"{executable}\" --background");
            }
            // Keep the empty MSI-owned value so upgrades preserve the preference.
            else key.SetValue("MonitorSync", "");
        }
    }

    public static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
