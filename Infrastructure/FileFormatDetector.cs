using System.IO.Compression;
using System.Text;
using OptiumPlusFileManager.Application;

namespace OptiumPlusFileManager.Infrastructure;

public sealed class FileFormatDetector
{
    private const int SampleSize = 512 * 1024;
    private const long MaximumZipInspectionBytes = 256L * 1024 * 1024;

    public async Task<FileFormatInfo> DetectAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        var sample = new byte[SampleSize];
        int count;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            count = await stream.ReadAsync(sample.AsMemory(), cancellationToken);
        }

        var prefix = sample.AsSpan(0, count);
        var detected = DetectSignature(prefix);
        var suspiciousArchive = false;
        string? warning = null;

        if (detected.FileType == "zip")
        {
            try
            {
                if (new FileInfo(path).Length > MaximumZipInspectionBytes)
                {
                    suspiciousArchive = true;
                    warning = "ZIP-контейнер больше безопасного лимита анализа 256 МБ.";
                }
                else
                {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                if (archive.Entries.Count > 10_000)
                {
                    suspiciousArchive = true;
                    warning = "В архиве больше 10 000 элементов.";
                }
                else
                {
                    long expandedLength = 0;
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (HasTraversal(entry.FullName))
                        {
                            suspiciousArchive = true;
                            warning = "В архиве обнаружен путь, выходящий за его корневую папку.";
                            break;
                        }

                        expandedLength = Math.Min(long.MaxValue - entry.Length, expandedLength) + entry.Length;
                        if (expandedLength > 2L * 1024 * 1024 * 1024 ||
                            (entry.CompressedLength == 0 ? entry.Length > 0 : entry.Length / (double)Math.Max(1, entry.CompressedLength) > 1000d))
                        {
                            suspiciousArchive = true;
                            warning = "Архив превышает безопасные ограничения распакованного размера.";
                            break;
                        }
                    }

                    if (!suspiciousArchive)
                    {
                        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (names.Contains("[Content_Types].xml"))
                        {
                            if (names.Any(name => name.StartsWith("word/", StringComparison.OrdinalIgnoreCase)))
                                detected = new("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false);
                            else if (names.Any(name => name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)))
                                detected = new("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", false);
                            else if (names.Any(name => name.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase)))
                                detected = new("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", false);
                        }

                        if (detected.FileType == "zip")
                        {
                            var mimetype = archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase));
                            if (mimetype is not null && mimetype.Length <= 128)
                            {
                                using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII);
                                var officeMime = reader.ReadToEnd();
                                detected = officeMime switch
                                {
                                    "application/vnd.oasis.opendocument.text" => new("odt", officeMime, false),
                                    "application/vnd.oasis.opendocument.spreadsheet" => new("ods", officeMime, false),
                                    "application/vnd.oasis.opendocument.presentation" => new("odp", officeMime, false),
                                    _ => detected
                                };
                            }
                        }
                    }
                }
                }
            }
            catch (InvalidDataException)
            {
                suspiciousArchive = true;
                warning = "Структура архива повреждена.";
            }
            catch (IOException)
            {
                suspiciousArchive = true;
                warning = "Не удалось прочитать структуру ZIP-контейнера.";
            }
        }

        if (detected.FileType == "unknown" && IsLikelyText(prefix))
            detected = new("text", "text/plain", true);

        var extensionConflict = IsKnownExtension(extension) && !IsCompatible(extension, detected.FileType);
        if (extensionConflict)
            warning ??= $"Расширение {extension} не соответствует сигнатуре содержимого ({detected.FileType}).";

        return new FileFormatInfo(detected.FileType, detected.MimeType, detected.IsText,
            extensionConflict, suspiciousArchive, warning);
    }

    private static (string FileType, string MimeType, bool IsText) DetectSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8)) return ("pdf", "application/pdf", false);
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("png", "image/png", false);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return ("jpeg", "image/jpeg", false);
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return ("gif", "image/gif", false);
        if (bytes.StartsWith("BM"u8)) return ("bmp", "image/bmp", false);
        if (bytes.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || bytes.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }))
            return ("tiff", "image/tiff", false);
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 })) return ("ico", "image/x-icon", false);
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ("webp", "image/webp", false);
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) &&
            (bytes[8..12].SequenceEqual("heic"u8) || bytes[8..12].SequenceEqual("heix"u8) || bytes[8..12].SequenceEqual("hevc"u8) ||
             bytes[8..12].SequenceEqual("mif1"u8) || bytes[8..12].SequenceEqual("msf1"u8)))
            return ("heif", "image/heif", false);
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && bytes[8..12].SequenceEqual("avif"u8))
            return ("avif", "image/avif", false);
        if (bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) || bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) || bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x07, 0x08 }))
            return ("zip", "application/zip", false);
        if (bytes.StartsWith(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }) ||
            bytes.StartsWith(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 })) return ("rar", "application/vnd.rar", false);
        if (bytes.StartsWith(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return ("7z", "application/x-7z-compressed", false);
        if (bytes.StartsWith(new byte[] { 0x1F, 0x8B })) return ("gzip", "application/gzip", false);
        if (bytes.StartsWith("MZ"u8)) return ("executable", "application/vnd.microsoft.portable-executable", false);
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            if (bytes[8..12].SequenceEqual("M4A "u8) || bytes[8..12].SequenceEqual("M4B "u8) || bytes[8..12].SequenceEqual("M4P "u8))
                return ("m4a", "audio/mp4", false);
            return ("mp4", "video/mp4", false);
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WAVE"u8))
            return ("wav", "audio/wav", false);
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("AVI "u8))
            return ("avi", "video/x-msvideo", false);
        if (bytes.StartsWith("ID3"u8) || bytes.Length > 1 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
            return ("mp3", "audio/mpeg", false);
        if (bytes.StartsWith("OggS"u8)) return ("ogg", "application/ogg", false);
        if (bytes.StartsWith("fLaC"u8)) return ("flac", "audio/flac", false);
        if (bytes.StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return ("mkv", "video/x-matroska", false);
        if (bytes.Length >= 262 && bytes[257..262].SequenceEqual("ustar"u8)) return ("tar", "application/x-tar", false);
        if (bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            return ("ole", "application/x-ole-storage", false);
        if (IsLikelyText(bytes))
        {
            var preview = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 4096)]).TrimStart('\uFEFF', '\u0000', ' ', '\t', '\r', '\n');
            if (preview.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                preview.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && preview.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                return ("svg", "image/svg+xml", true);
        }
        return ("unknown", "application/octet-stream", false);
    }

    private static bool IsLikelyText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return true;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return true;
        if (bytes.Contains((byte)0)) return false;
        var printable = 0;
        foreach (var value in bytes)
            if (value is 9 or 10 or 13 || value >= 32) printable++;
        return printable / (double)bytes.Length > 0.90;
    }

    private static bool HasTraversal(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        return normalized.StartsWith('/') || Path.IsPathRooted(entryName) ||
               normalized.Split('/').Any(part => part is ".." or ".") ||
               (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':');
    }

    private static bool IsKnownExtension(string extension) => extension.ToLowerInvariant() is
        ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".ico" or
        ".heic" or ".heif" or ".avif" or ".svg" or ".zip" or ".rar" or ".7z" or ".gz" or ".tar" or
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".mp4" or ".m4v" or ".mov" or ".mkv" or ".avi" or
        ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".odt" or ".ods" or ".odp" or
        ".exe" or ".dll" or ".msi" or ".cs" or ".csx" or ".py" or ".pyw" or ".java" or ".js" or ".mjs" or
        ".cjs" or ".ts" or ".tsx" or ".jsx" or ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hpp" or
        ".hh" or ".sql" or ".html" or ".htm" or ".css" or ".json" or ".xml" or ".txt" or ".csv" or ".tsv" or
        ".md" or ".rtf" or ".ps1" or ".bat" or ".cmd" or ".sh" or ".yaml" or ".yml";

    private static bool IsCompatible(string extension, string fileType) => extension.ToLowerInvariant() switch
    {
        ".pdf" => fileType == "pdf",
        ".png" => fileType == "png",
        ".jpg" or ".jpeg" => fileType == "jpeg",
        ".gif" => fileType == "gif",
        ".webp" => fileType == "webp",
        ".bmp" => fileType == "bmp",
        ".tif" or ".tiff" => fileType == "tiff",
        ".ico" => fileType == "ico",
        ".heic" or ".heif" => fileType == "heif",
        ".avif" => fileType == "avif",
        ".svg" => fileType == "svg",
        ".zip" => fileType == "zip",
        ".rar" => fileType == "rar",
        ".7z" => fileType == "7z",
        ".gz" => fileType == "gzip",
        ".mp3" => fileType == "mp3",
        ".wav" => fileType == "wav",
        ".ogg" => fileType == "ogg",
        ".flac" => fileType == "flac",
        ".m4a" => fileType == "m4a",
        ".mp4" or ".m4v" or ".mov" => fileType == "mp4",
        ".mkv" => fileType == "mkv",
        ".avi" => fileType == "avi",
        ".docx" => fileType == "docx",
        ".xlsx" => fileType == "xlsx",
        ".pptx" => fileType == "pptx",
        ".odt" => fileType == "odt",
        ".ods" => fileType == "ods",
        ".odp" => fileType == "odp",
        ".doc" or ".xls" or ".ppt" => fileType == "ole",
        ".tar" => fileType == "tar",
        ".exe" or ".dll" or ".msi" => fileType == "executable",
        ".cs" or ".csx" or ".py" or ".pyw" or ".java" or ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or
        ".jsx" or ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" or ".sql" or ".html" or
        ".htm" or ".css" or ".json" or ".xml" or ".txt" or ".csv" or ".tsv" or ".md" or ".rtf" or ".ps1" or
        ".bat" or ".cmd" or ".sh" or ".yaml" or ".yml" => fileType == "text",
        _ => true
    };
}
