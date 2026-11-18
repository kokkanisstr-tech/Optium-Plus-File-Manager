using Microsoft.Win32;

namespace OptiumPlusFileManager.Infrastructure;

public static class AppStartupManager
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "Optium+ File Manager";
    private static readonly string[] LegacyValueNames = ["OptiumPlusFileManager", "Optium", "OY_files", "FileSorterCollege"];

    public static bool IsEnabled()
    {
        bool enabled;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            enabled = key?.GetValue(ValueName) is string current && !string.IsNullOrWhiteSpace(current)
                || key is not null && LegacyValueNames.Any(name =>
                    key.GetValue(name) is string legacy && !string.IsNullOrWhiteSpace(legacy));
        }

        if (enabled)
            SetEnabled(true);
        return enabled;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                      ?? throw new InvalidOperationException("Не удалось открыть параметры автозапуска Windows.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            foreach (var legacyName in LegacyValueNames)
                key.DeleteValue(legacyName, throwOnMissingValue: false);
            return;
        }

        var appPath = Path.Combine(AppContext.BaseDirectory, "OptiumPlusFileManager.exe");
        if (!File.Exists(appPath))
            appPath = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь запуска приложения.");
        foreach (var legacyName in LegacyValueNames)
            key.DeleteValue(legacyName, throwOnMissingValue: false);
        key.SetValue(ValueName, $"\"{appPath}\" --minimized", RegistryValueKind.String);
    }
}
