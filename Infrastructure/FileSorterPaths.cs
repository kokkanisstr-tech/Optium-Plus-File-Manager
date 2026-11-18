namespace OptiumPlusFileManager.Infrastructure;

public sealed class FileSorterPaths
{
    public FileSorterPaths(string? dataRoot = null, string? sortedRoot = null)
    {
        DataRoot = Path.GetFullPath(dataRoot ?? GetDefaultDataRoot());
        Inbox = Path.Combine(DataRoot, "Inbox");
        Processing = Path.Combine(DataRoot, "Processing");
        Review = Path.Combine(DataRoot, "Review");
        Quarantine = Path.Combine(DataRoot, "Quarantine");
        Errors = Path.Combine(DataRoot, "Errors");
        Sorted = Path.GetFullPath(sortedRoot ?? GetDefaultSortedRoot());
        Database = Path.Combine(DataRoot, ".data", "filesorter.db");
        Logs = Path.Combine(DataRoot, ".data", "logs");
        Temp = Path.Combine(DataRoot, ".data", "temp");
    }

    public string DataRoot { get; }
    public string Inbox { get; }
    public string Processing { get; }
    public string Sorted { get; }
    public string Review { get; }
    public string Quarantine { get; }
    public string Errors { get; }
    public string Database { get; }
    public string Logs { get; }
    public string Temp { get; }

    public static string GetDefaultDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("FILE_SORTER_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.GetTempPath();
        var current = Path.Combine(local, "OptiumPlusFileManager");
        var previous = Path.Combine(local, "Optium");
        var older = Path.Combine(local, "OY_files");
        var legacy = Path.Combine(local, "FileSorterCollege");
        if (Directory.Exists(current)) return current;
        if (Directory.Exists(previous)) return previous;
        if (Directory.Exists(older)) return older;
        if (Directory.Exists(legacy)) return legacy;
        return current;
    }

    public static string GetDefaultSortedRoot()
    {
        var configured = Environment.GetEnvironmentVariable("FILE_SORTER_SORTED_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = GetDefaultDataRoot();
        var current = Path.Combine(desktop, "OptiumPlusFileManagerData", "Sorted");
        var previous = Path.Combine(desktop, "OptiumData", "Sorted");
        var older = Path.Combine(desktop, "OY_filesData", "Sorted");
        var legacy = Path.Combine(desktop, "FileSorterData", "Sorted");
        if (Directory.Exists(current)) return current;
        if (Directory.Exists(previous)) return previous;
        if (Directory.Exists(older)) return older;
        if (Directory.Exists(legacy)) return legacy;
        return current;
    }

    public void EnsureCreated()
    {
        foreach (var path in new[] { DataRoot, Inbox, Processing, Sorted, Review, Quarantine, Errors,
                     Path.GetDirectoryName(Database)!, Logs, Temp })
            Directory.CreateDirectory(path);
    }
}
