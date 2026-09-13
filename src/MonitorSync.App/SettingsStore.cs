using System.IO;
using Microsoft.Win32;

namespace MonitorSync.App;

public static class SettingsStore
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorSync");
    private static readonly object LogGate = new();

    public static bool BrightnessActive
    {
        get => GetActive(nameof(BrightnessActive));
        set => SetActive(nameof(BrightnessActive), value);
    }

    public static bool VolumeActive
    {
        get => GetActive(nameof(VolumeActive));
        set => SetActive(nameof(VolumeActive), value);
    }

    private static bool GetActive(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\MonitorSync");
        // Preserve the previous global opt-out until each feature has its own preference.
        return (key?.GetValue(name) ?? key?.GetValue("Active")) is not int value || value != 0;
    }

    private static void SetActive(string name, bool active)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\MonitorSync");
        key.SetValue(name, active ? 1 : 0, RegistryValueKind.DWord);
    }

    public static void InitializeStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        // Missing means first run. An empty value preserves an explicit opt-out.
        // Refresh enabled entries if the executable moved or was upgraded.
        if (key?.GetValue("MonitorSync") is not string value || !string.IsNullOrWhiteSpace(value))
            StartsWithWindows = true;
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
