using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OptiumPlusFileManager.Application;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig;

namespace OptiumPlusFileManager.Infrastructure;

public sealed class SafeContentExtractor : IFileContentExtractor
{
    private const int MaxTextCharacters = 250_000;
    private const long MaxOfficeXmlBytes = 2 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public async Task<ExtractedContent> ExtractAsync(string filePath, string fileType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (fileType == "text")
                return await ReadTextFileAsync(filePath, cancellationToken);
            if (fileType is "docx" or "xlsx" or "pptx")
                return await ExtractOpenXmlAsync(filePath, fileType, cancellationToken);
            if (fileType == "pdf")
                return await ExtractPdfAsync(filePath, cancellationToken);
            if (fileType == "zip")
                return ExtractArchiveListing(filePath, cancellationToken);
            if (fileType is "png" or "jpeg" or "gif" or "webp" or "bmp" or "tiff" or "ico" or "heif" or "avif")
            {
                var metadata = MediaMetadataReader.Read(filePath, fileType, cancellationToken);
                var ocr = await LocalOcrService.ReadImageAsync(filePath, fileType, cancellationToken);
                return CombineMetadata(metadata, ocr);
            }
            if (fileType is "mp3" or "wav" or "ogg" or "flac" or "m4a" or "mp4" or "avi" or "mkv")
                return new(MediaMetadataReader.Read(filePath, fileType, cancellationToken));
            if (fileType == "executable")
                return new(MediaMetadataReader.Read(filePath, fileType, cancellationToken));
            if (fileType is "odt" or "ods" or "odp")
                return new(string.Empty, "Текст OpenDocument пока не извлекается; файл можно проверить и распределить вручную.");
            if (fileType is "tar" or "rar" or "7z" or "gzip")
                return new(string.Empty, "Содержимое этого типа архива не распаковывается; исходный файл не изменён.");
            if (fileType == "ole")
                return new(string.Empty, "Старый бинарный формат Office не поддерживается для извлечения.");
            return new(string.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return new(string.Empty, "Не удалось безопасно извлечь текст; подробности файла не записаны в журнал.");
        }
    }

    private static async Task<ExtractedContent> ReadTextFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var builder = new StringBuilder(Math.Min(MaxTextCharacters, (int)Math.Min(stream.Length, 32 * 1024)));
        var buffer = new char[32 * 1024];
        while (builder.Length < MaxTextCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxTextCharacters - builder.Length)), cancellationToken);
            if (count == 0) break;
            builder.Append(buffer, 0, count);
        }

        var clipped = stream.Length > builder.Length;
        return new(builder.ToString(), clipped ? "Для анализа использована ограниченная выборка текстового файла." : null);
    }

    private static Task<ExtractedContent> ExtractOpenXmlAsync(string path, string type, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 10_000)
            throw new InvalidDataException("В документе слишком много элементов контейнера.");

        var output = new StringBuilder();
        IEnumerable<ZipArchiveEntry> entries = type switch
        {
            "docx" => archive.Entries.Where(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)),
            "xlsx" => archive.Entries.Where(entry => entry.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            _ => archive.Entries.Where(entry => Regex.IsMatch(entry.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || entry.FullName.Equals("ppt/presentation.xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > MaxOfficeXmlBytes)
                continue;
            using var entryStream = entry.Open();
            var document = XDocument.Load(entryStream, LoadOptions.None);
            foreach (var node in document.DescendantNodes().OfType<XText>())
            {
                var value = node.Value.Trim();
                if (value.Length == 0) continue;
                if (output.Length + value.Length + 1 > MaxTextCharacters) break;
                output.AppendLine(value);
            }
            if (output.Length >= MaxTextCharacters) break;
        }

        return Task.FromResult(new ExtractedContent(output.ToString(), output.Length == 0 ? "Документ не содержит извлекаемого текста." : null));
    }

    private static async Task<ExtractedContent> ExtractPdfAsync(string path, CancellationToken cancellationToken)
    {
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages <= 0)
            return new(string.Empty, "PDF не содержит страниц.");

        var output = new StringBuilder();
        var scannedPageIndices = new List<int>();
        var pageIndex = 0;
        foreach (var page in pdf.GetPages().Take(40))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length == 0)
                scannedPageIndices.Add(pageIndex);
            if (output.Length + text.Length > MaxTextCharacters)
            {
                output.Append(text.AsSpan(0, Math.Max(0, MaxTextCharacters - output.Length)));
                break;
            }
            if (text.Length > 0)
            {
                output.AppendLine(text);
            }
            pageIndex++;
        }

        var warnings = new List<string>();
        if (pdf.NumberOfPages > 40)
            warnings.Add("Для анализа извлечены первые 40 страниц PDF.");
        if (output.Length >= MaxTextCharacters)
            warnings.Add("Текст PDF ограничен по длине.");

        if (scannedPageIndices.Count > 0 && output.Length < MaxTextCharacters)
        {
            var ocr = await LocalOcrService.ReadPdfPagesAsync(path, scannedPageIndices, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ocr.Text))
            {
                if (output.Length > 0) output.AppendLine();
                var remaining = Math.Max(0, MaxTextCharacters - output.Length);
                if (remaining > 0)
                    output.Append(ocr.Text.AsSpan(0, Math.Min(ocr.Text.Length, remaining)));
            }
            if (!string.IsNullOrWhiteSpace(ocr.Warning))
                warnings.Add(ocr.Warning);
        }

        if (output.Length == 0 && warnings.Count == 0)
            warnings.Add("PDF не содержит доступного текстового слоя или распознанного текста.");

        return new(output.ToString(), warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct(StringComparer.Ordinal)));
    }

    private static ExtractedContent ExtractArchiveListing(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var lines = new List<string>(Math.Min(archive.Entries.Count, 1000));
        foreach (var entry in archive.Entries.Take(1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add($"{entry.FullName} ({entry.Length} bytes)");
        }
        return new(string.Join(Environment.NewLine, lines), archive.Entries.Count > 1000 ? "Показаны первые 1 000 элементов архива." : null);
    }

    private static ExtractedContent CombineMetadata(string metadata, ExtractedContent extracted)
    {
        var combined = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { metadata, extracted.Text }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(combined, extracted.Warning);
    }

}
