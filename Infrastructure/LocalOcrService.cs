using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using OptiumPlusFileManager.Application;
using PDFiumCore;
using TesseractOCR;
using TesseractOCR.Enums;
using PixImage = TesseractOCR.Pix.Image;

namespace OptiumPlusFileManager.Infrastructure;

internal static class LocalOcrService
{
    private const int MaxOcrCharacters = 250_000;
    private const long MaxImageBytes = 32L * 1024 * 1024;
    private const long MaxImagePixels = 12_000_000;
    private const long MaxPdfBytes = 100L * 1024 * 1024;
    private const int MaxPdfPagePixels = 4_000_000;
    private const int MaxPdfPageDimension = 2_400;
    private static readonly SemaphoreSlim OcrLock = new(1, 1);
    private static readonly Lazy<Engine> Engine = new(CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
    private static bool _pdfiumInitialized;

    static LocalOcrService() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeEngine();

    public static async Task<ExtractedContent> ReadImageAsync(string path, string fileType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var dimensions = MediaMetadataReader.ReadImageDimensions(path, fileType);
            if (dimensions.Width == 0 || dimensions.Height == 0)
                return new(string.Empty, "Размеры изображения не удалось проверить по его заголовку; OCR пропущен.");
            var exceedsPixelLimit = dimensions.Width > MaxImagePixels || dimensions.Height > MaxImagePixels
                || dimensions.Width > MaxImagePixels / (long)dimensions.Height;
            if (new FileInfo(path).Length > MaxImageBytes || exceedsPixelLimit)
                return new(string.Empty, "Изображение превышает безопасный предел OCR (32 МБ или 12 мегапикселей); файл оставлен без распознавания текста.");

            var externalTesseract = Environment.GetEnvironmentVariable("FILE_SORTER_TESSERACT");
            if (!string.IsNullOrWhiteSpace(externalTesseract))
                return await ReadImageWithExternalTesseractAsync(path, externalTesseract, cancellationToken);

            await OcrLock.WaitAsync(cancellationToken);
            try
            {
                var result = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var image = PixImage.LoadFromFile(path);
                    using var page = Engine.Value.Process(image);
                    return LimitText(page.Text ?? string.Empty, "Результат OCR ограничен по длине.");
                });
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally { OcrLock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return new(string.Empty, "Локальный OCR изображения не сработал; файл сохранён для обычной проверки.");
        }
    }

    public static async Task<ExtractedContent> ReadPdfPagesAsync(string path, IReadOnlyList<int> pageIndices,
        CancellationToken cancellationToken)
    {
        if (pageIndices.Count == 0) return new(string.Empty);
        await OcrLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => ReadPdfPages(path, pageIndices, cancellationToken));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return new(string.Empty, "Локальный OCR сканированных страниц PDF не сработал; файл сохранён для проверки.");
        }
        finally { OcrLock.Release(); }
    }

    private static ExtractedContent ReadPdfPages(string path, IReadOnlyList<int> pageIndices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > MaxPdfBytes)
            return new(string.Empty, "Размер PDF превышает безопасный предел OCR (100 МБ); сканированные страницы оставлены для проверки.");
        EnsurePdfiumInitialized();
        var document = fpdfview.FPDF_LoadDocument(path, null);
        if (document is null)
            return new(string.Empty, "PDFium не смог открыть PDF для OCR сканов; требуется ручная проверка.");

        try
        {
            var pageCount = fpdfview.FPDF_GetPageCount(document);
            var output = new StringBuilder();
            var warnings = new List<string>();
            var engine = Engine.Value;
            foreach (var pageIndex in pageIndices.Take(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageIndex < 0 || pageIndex >= pageCount) continue;

                var page = fpdfview.FPDF_LoadPage(document, pageIndex);
                if (page is null)
                {
                    warnings.Add("Одна из сканированных страниц PDF не открылась для OCR.");
                    continue;
                }

                try
                {
                    var widthPoints = fpdfview.FPDF_GetPageWidthF(page);
                    var heightPoints = fpdfview.FPDF_GetPageHeightF(page);
                    if (!float.IsFinite(widthPoints) || !float.IsFinite(heightPoints) || widthPoints <= 0 || heightPoints <= 0)
                    {
                        warnings.Add("Размер одной страницы PDF не удалось проверить; её OCR пропущен.");
                        continue;
                    }
                    if (widthPoints > 100_000 || heightPoints > 100_000)
                    {
                        warnings.Add("Страница PDF слишком велика для безопасного OCR; она оставлена для проверки.");
                        continue;
                    }

                    var scale = 120d / 72d;
                    var width = Math.Max(1, (int)Math.Ceiling(widthPoints * scale));
                    var height = Math.Max(1, (int)Math.Ceiling(heightPoints * scale));
                    var reduction = Math.Min(1d, Math.Min((double)MaxPdfPageDimension / width,
                        (double)MaxPdfPageDimension / height));
                    reduction = Math.Min(reduction, Math.Sqrt((double)MaxPdfPagePixels / ((long)width * height)));
                    width = Math.Max(1, (int)Math.Floor(width * reduction));
                    height = Math.Max(1, (int)Math.Floor(height * reduction));

                    var bitmap = fpdfview.FPDFBitmapCreate(width, height, 0);
                    if (bitmap is null)
                    {
                        warnings.Add("Для одной из страниц PDF не хватило памяти на безопасный OCR-кадр.");
                        continue;
                    }

                    try
                    {
                        fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, uint.MaxValue);
                        fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);
                        var nativeStride = fpdfview.FPDFBitmapGetStride(bitmap);
                        var nativeBuffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                        if (nativeStride < width * 4 || nativeBuffer == IntPtr.Zero)
                        {
                            warnings.Add("Не удалось прочитать растровое изображение одной страницы PDF.");
                            continue;
                        }

                        using var raster = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                        var rectangle = new Rectangle(0, 0, width, height);
                        var bitmapData = raster.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        try
                        {
                            var rowBuffer = new byte[checked(width * 4)];
                            for (var row = 0; row < height; row++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                Marshal.Copy(IntPtr.Add(nativeBuffer, checked(row * nativeStride)), rowBuffer, 0, rowBuffer.Length);
                                Marshal.Copy(rowBuffer, 0,
                                    IntPtr.Add(bitmapData.Scan0, checked(row * bitmapData.Stride)), rowBuffer.Length);
                            }
                        }
                        finally { raster.UnlockBits(bitmapData); }

                        using var png = new MemoryStream();
                        raster.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                        using var image = PixImage.LoadFromMemory(png.ToArray());
                        using var recognized = Engine.Value.Process(image);
                        var text = (recognized.Text ?? string.Empty).Trim();
                        if (text.Length > 0)
                        {
                            if (output.Length > 0) output.AppendLine();
                            output.Append("Страница ").Append(pageIndex + 1).AppendLine(":").AppendLine(text);
                            if (output.Length > MaxOcrCharacters)
                            {
                                output.Length = MaxOcrCharacters;
                                warnings.Add("Результат OCR ограничен по длине.");
                                break;
                            }
                        }
                    }
                    finally { fpdfview.FPDFBitmapDestroy(bitmap); }
                }
                finally { fpdfview.FPDF_ClosePage(page); }
            }

            if (pageIndices.Count > 10)
                warnings.Add("OCR выполнен не более чем для первых 10 сканированных страниц PDF.");
            return new(output.ToString(), warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct(StringComparer.Ordinal)));
        }
        finally { fpdfview.FPDF_CloseDocument(document); }
    }

    private static async Task<ExtractedContent> ReadImageWithExternalTesseractAsync(string path, string executable,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
            return new(string.Empty, "Путь FILE_SORTER_TESSERACT не найден; OCR изображения пропущен.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("stdout");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(GetConfiguredLanguages());

        using var process = Process.Start(start);
        if (process is null)
            return new(string.Empty, "Не удалось запустить локальный Tesseract OCR.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaxOcrCharacters, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 16_384, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            return process.ExitCode == 0
                ? new(output.Text, output.Truncated ? "Результат OCR ограничен по длине." : null)
                : new(string.Empty, "Внешний локальный Tesseract OCR не смог распознать изображение.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(string.Empty, "Локальный OCR изображения превысил ограничение по времени.");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            var remaining = maxCharacters - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return (output.ToString(), truncated);
    }

    private static Engine CreateEngine()
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        var languages = GetConfiguredLanguages();
        foreach (var language in languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (language is not ("eng" or "rus"))
                throw new InvalidOperationException("Встроенный OCR содержит только языковые модели eng и rus.");
            if (!File.Exists(Path.Combine(dataPath, language + ".traineddata")))
                throw new FileNotFoundException("Встроенная языковая модель OCR не найдена.");
        }

        var previousPrefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        Environment.SetEnvironmentVariable("TESSDATA_PREFIX", dataPath);
        try { return new Engine(dataPath, languages, EngineMode.Default); }
        finally { Environment.SetEnvironmentVariable("TESSDATA_PREFIX", previousPrefix); }
    }

    private static string GetConfiguredLanguages()
    {
        var value = Environment.GetEnvironmentVariable("FILE_SORTER_OCR_LANGUAGES")?.Trim();
        return string.IsNullOrWhiteSpace(value) ? "eng+rus" : value;
    }

    private static void EnsurePdfiumInitialized()
    {
        if (_pdfiumInitialized) return;
        fpdfview.FPDF_InitLibrary();
        _pdfiumInitialized = true;
    }

    private static ExtractedContent LimitText(string text, string warning) =>
        text.Length <= MaxOcrCharacters ? new(text) : new(text[..MaxOcrCharacters], warning);

    private static void DisposeEngine()
    {
        if (!Engine.IsValueCreated || !OcrLock.Wait(TimeSpan.FromSeconds(5))) return;
        try { Engine.Value.Dispose(); }
        finally { OcrLock.Release(); }
    }
}
