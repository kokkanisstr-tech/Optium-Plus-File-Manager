using System.Text.Json;
using OptiumPlusFileManager.Application;

namespace OptiumPlusFileManager.Infrastructure;

public static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(FileSorterPaths.GetDefaultDataRoot(), "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is null)
                    return CreateDefaults();

                settings.WatchFolders ??= [];
                settings.ExcludedPaths ??= [];
                settings.ExcludedPatterns ??= [];
                settings.LocalOnlyPaths ??= [];
                settings.AiAllowedFileTypes ??= ["text", "pdf", "docx", "xlsx", "pptx"];

                var defaults = CreateDefaults();
                if (string.IsNullOrWhiteSpace(settings.SourceFolder))
                    settings.SourceFolder = defaults.SourceFolder;
                if (string.IsNullOrWhiteSpace(settings.DestinationFolder))
                    settings.DestinationFolder = defaults.DestinationFolder;
                if (settings.WatchFolders.Count == 0 && !string.IsNullOrWhiteSpace(settings.SourceFolder))
                    settings.WatchFolders.Add(settings.SourceFolder);
                settings.MaxFileSizeMb = Math.Clamp(settings.MaxFileSizeMb, 1, 2048);
                settings.MonthlyAiRequestLimit = Math.Clamp(settings.MonthlyAiRequestLimit, 1, 100_000);
                settings.AutoSortConfidence = Math.Clamp(settings.AutoSortConfidence, 0.50, 1.0);
                settings.ReviewConfidence = Math.Clamp(settings.ReviewConfidence, 0, settings.AutoSortConfidence);
                return settings;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // При повреждённом файле настроек программа запускается с безопасными значениями по умолчанию.
        }

        return CreateDefaults();
    }

    public static void Save(AppSettings settings)
    {
        var folder = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    private static AppSettings CreateDefaults()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return new AppSettings
        {
            SourceFolder = Path.Combine(desktop, "Файлы для сортировки"),
            DestinationFolder = FileSorterPaths.GetDefaultSortedRoot(),
            WatchFolders = [Path.Combine(desktop, "Файлы для сортировки")],
            AutomaticSorting = false
        };
    }
}
