namespace OptiumPlusFileManager.Infrastructure;

public static class AiFilePolicy
{
    private static readonly HashSet<string> VisionImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpeg", "webp"
    };

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpeg", "gif", "webp", "bmp", "tiff", "ico", "heif", "avif"
    };

    public const int MaximumVisionImageBytes = 12 * 1024 * 1024;

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "pdf", "docx", "xlsx", "pptx", "png", "jpeg", "gif", "webp", "bmp", "tiff", "ico",
        "heif", "avif", "zip", "rar", "7z", "gzip", "tar", "mp3", "wav", "ogg", "flac", "m4a",
        "mp4", "avi", "mkv", "executable"
    };

    public static IReadOnlyCollection<string> KnownTypes => SupportedTypes;

    public static bool IsAllowed(AppSettings settings, string filePath, string fileType)
    {
        var fullPath = Path.GetFullPath(filePath);
        return settings.AiAllowedFileTypes.Contains(fileType, StringComparer.OrdinalIgnoreCase) &&
               !settings.LocalOnlyPaths.Any(privateRoot => FileSafety.IsWithin(fullPath, privateRoot));
    }

    public static bool CanSendVisionImage(AppSettings settings, string filePath, string fileType)
    {
        var fullPath = Path.GetFullPath(filePath);
        return settings.AllowAiImageAnalysis && VisionImageTypes.Contains(fileType) &&
               settings.AiAllowedFileTypes.Contains(fileType, StringComparer.OrdinalIgnoreCase) &&
               !settings.LocalOnlyPaths.Any(privateRoot => FileSafety.IsWithin(fullPath, privateRoot));
    }

    public static bool IsKnownType(string fileType) => SupportedTypes.Contains(fileType);

    public static bool IsImageType(string fileType) => ImageTypes.Contains(fileType);

    public static bool IsVisionImageType(string fileType) => VisionImageTypes.Contains(fileType);

    public static string? GetVisionMimeType(string fileType) => fileType.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        _ => null
    };
}
