namespace OptiumPlusFileManager.Application;

public static class FileTypeClassifier
{
    private static readonly HashSet<string> TemporaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".part", ".crdownload", ".download"
    };

    public static bool IsTemporary(string filePath) => TemporaryExtensions.Contains(Path.GetExtension(filePath));
}
