using System.Text.RegularExpressions;

namespace MonitorSync.Core;

public static class AutomaticMonitorSelection
{
    public static MonitorDescriptor? Find(string? audioMonitorName, bool isDisplayAudio,
        IEnumerable<MonitorDescriptor> monitors)
    {
        if (!isDisplayAudio || string.IsNullOrWhiteSpace(audioMonitorName)) return null;
        var name = Normalize(audioMonitorName);
        if (name.Length == 0) return null;
        // Count unreadable matches too: two identical displays must not become
        // an apparently unique match just because one has DDC/CI disabled.
        var matches = monitors.Where(m => Normalize(m.Name) == name).Take(2).ToArray();
        return matches.Length == 1 && matches[0].Volume.HasValue ? matches[0] : null;
    }

    private static string Normalize(string name)
    {
        var model = Regex.Replace(name, @"\s*\((?:HDMI|DP|DisplayPort|USB-C)\s*\d*\)\s*$", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        model = Regex.Replace(model, @"^\d+\s*-\s*", "");
        return string.Concat(model.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }
}
