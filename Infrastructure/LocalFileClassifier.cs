using System.Text.RegularExpressions;
using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;

namespace OptiumPlusFileManager.Infrastructure;

public sealed partial class LocalFileClassifier : IFileClassifier
{
    [GeneratedRegex(@"\b(using\s+[\w.]+\s*;|namespace\s+[\w.]+|(?:public|internal|private)?\s*(?:static\s+)?class\s+\w+|Console\.(WriteLine|ReadLine)|async\s+Task)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CSharpSignals();

    [GeneratedRegex(@"(^|\n)\s*(def\s+\w+\s*\(|class\s+\w+\s*[:(]?|from\s+[\w.]+\s+import\s+|import\s+[\w.]+)|(^|\n)\s*@\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PythonSignals();

    [GeneratedRegex(@"\b(package\s+[\w.]+\s*;|import\s+[\w.]+\s*;|public\s+static\s+void\s+main\s*\(|System\.out\.print)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaSignals();

    [GeneratedRegex(@"\b(interface\s+\w+|type\s+\w+\s*=|:\s*(string|number|boolean|unknown)\b|<\w+>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeScriptSignals();

    [GeneratedRegex(@"\b(const|let|function|export|import)\b|=>|document\.querySelector", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptSignals();

    [GeneratedRegex(@"\b(create\s+table|select\s+.+\s+from|insert\s+into|update\s+\w+\s+set)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlSignals();

    [GeneratedRegex(@"#include\s*<(stdio\.h|stdlib\.h|string\.h)>|\b(printf|scanf)\s*\(|\bint\s+main\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CSignals();

    [GeneratedRegex(@"#include\s*<(iostream|vector|string|map|memory)>|\bstd::|\bcout\s*<<|\bcin\s*>>|\bclass\s+\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CppSignals();

    [GeneratedRegex(@"(^|\n)\s*(@media|@import|:root|[.#][\w-][^{]*\{[^}]*[\w-]+\s*:[^;}]+;)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssSignals();

    [GeneratedRegex(@"\b(<html|<!doctype html|<head|<body)\b|<\w+[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlSignals();

    [GeneratedRegex(@"\b(задани\w*|лабораторн\w*|практическ\w*|лекци\w*|семестр\w*|студент\w*|вариант\w*|предмет\w*|экзамен\w*|домашн\w*\s+работ\w*|учебн\w*|task|assignment|lecture|coursework|homework)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StudySignals();

    [GeneratedRegex(@"\b(отч[её]т\w*|договор\w*|проект\w*|клиент\w*|совещан\w*|срок\w*|заказчик\w*|work|meeting|invoice|project plan)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkSignals();

    [GeneratedRegex(@"\b(сч[её]т\w*|оплат\w*|налог\w*|бюджет\w*|доход\w*|расход\w*|кредит\w*|финанс\w*|invoice\s+number|bank\s+account)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FinanceSignals();

    public ClassificationResult Classify(FileFormatInfo format, string fileName, string extension, string extractedText)
    {
        if (format.ExtensionConflict || format.SuspiciousArchive)
            return Result("other", "other", format.FileType, string.Empty, "", format.Warning ?? "Сигнатура не совпадает с расширением.",
                0.20, [format.SuspiciousArchive ? "ARCHIVE_SAFETY_LIMIT" : "FORMAT_EXTENSION_MISMATCH"]);

        if (format.FileType == "executable")
            return Result("applications", "other", "executable", "", "Исполняемый файл", "Метаданные исполняемого файла; запуск запрещён.",
                0.96, ["EXECUTABLE_SIGNATURE"]);

        if (format.FileType is "png" or "jpeg" or "gif" or "webp" or "bmp" or "tiff" or "ico" or "heif" or "avif" or "svg")
            return Result("images", "other", format.FileType, "", "Изображение", "Формат изображения определён по сигнатуре содержимого.",
                0.96, ["IMAGE_MAGIC_BYTES"]);

        if (format.FileType is "mp3" or "wav" or "ogg" or "flac" or "m4a")
            return Result("audio", "other", format.FileType, "", "Аудио", "Аудиоформат определён по сигнатуре содержимого.",
                0.94, ["AUDIO_MAGIC_BYTES"]);

        if (format.FileType is "mp4" or "mkv" or "avi")
            return Result("video", "other", "video", "", "Видео", "Контейнер видео определён по сигнатуре содержимого.",
                0.93, ["VIDEO_CONTAINER_SIGNATURE"]);

        if (format.FileType is "zip" or "rar" or "7z" or "gzip" or "tar")
            return Result("archives", "other", "archive", "", "Архив", "Формат архива определён по сигнатуре содержимого.",
                0.92, ["ARCHIVE_MAGIC_BYTES"]);

        var language = DetectLanguage(extractedText, extension);
        if (language is not null)
        {
            var (subcategory, code, confidence) = language.Value;
            return Result("programming", subcategory, "source_code", code, "Исходный код", "Язык определён по синтаксическим признакам содержимого.",
                confidence, [code.ToUpperInvariant() + "_SYNTAX"]);
        }

        if (format.FileType is "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp" or "pdf" or "ole" ||
            new[] { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".odt", ".ods", ".odp" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            var (subcategory, confidence, reason) = DetectDocumentTopic(extractedText, Path.GetFileNameWithoutExtension(fileName));
            return Result("documents", subcategory, "document", "", "Документ", "Тип документа определён локальными правилами.", confidence,
                [reason]);
        }

        if (format.IsText)
        {
            var (documentCategory, confidence, reason) = DetectDocumentTopic(extractedText, Path.GetFileNameWithoutExtension(fileName));
            if (documentCategory != "other")
                return Result("documents", documentCategory, "text", "", "Текстовый документ", "Тема определена по содержимому.", confidence,
                    [reason]);

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                return Result("programming", "other-code", "structured_text", "", "Структурированные данные", "Текст содержит данные в машиночитаемом формате.",
                    0.74, ["STRUCTURED_TEXT_FORMAT"]);

            return Result("documents", "other", "text", "", "Текстовый файл", "Текст распознан, но его назначение неясно.",
                0.68, ["TEXT_CONTENT"]);
        }

        return Result("other", "other", format.FileType, "", "Неизвестный формат", "Надёжных признаков категории не найдено.",
            0.25, ["UNKNOWN_FORMAT"]);
    }

    private static (string Subcategory, string Language, double Confidence)? DetectLanguage(string text, string extension)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sample = text.Length > 150_000 ? text[..150_000] : text;
        var csharp = CSharpSignals().Matches(sample).Count;
        var python = PythonSignals().Matches(sample).Count;
        var java = JavaSignals().Matches(sample).Count;
        var ts = TypeScriptSignals().Matches(sample).Count;
        var js = JavaScriptSignals().Matches(sample).Count;
        var sql = SqlSignals().Matches(sample).Count;
        var c = CSignals().Matches(sample).Count;
        var cpp = CppSignals().Matches(sample).Count;

        var likely = new[]
        {
            (Count: csharp, Id: "csharp", Language: "CSharp", Ext: ".cs"),
            (Count: python, Id: "python", Language: "Python", Ext: ".py"),
            (Count: java, Id: "java", Language: "Java", Ext: ".java"),
            (Count: ts, Id: "javascript", Language: "TypeScript", Ext: ".ts"),
            (Count: js, Id: "javascript", Language: "JavaScript", Ext: ".js"),
            (Count: sql, Id: "sql", Language: "SQL", Ext: ".sql"),
            (Count: cpp, Id: "cpp", Language: "C++", Ext: ".cpp"),
            (Count: c, Id: "c", Language: "C", Ext: ".c")
        }.OrderByDescending(item => item.Count).ToArray();

        var best = likely[0];
        if (best.Count < 2)
        {
            var strongestOtherLanguage = likely.Where(item => item.Id != "sql").Max(item => item.Count);
            if (sql > strongestOtherLanguage) return ("sql", "SQL", sql > 1 ? 0.94 : 0.88);
            if (sql > 0 && sql == strongestOtherLanguage) return null;
            if (HtmlSignals().IsMatch(sample)) return ("html", "HTML", 0.87);
            if (CssSignals().IsMatch(sample)) return ("css", "CSS", 0.87);
            return null;
        }

        var difference = best.Count - likely[1].Count;
        if (difference == 0 && !string.Equals(extension, best.Ext, StringComparison.OrdinalIgnoreCase))
            return null;

        var confidence = best.Count >= 4 && difference >= 2 ? 0.97 : best.Count >= 3 ? 0.93 : 0.87;
        return (best.Id, best.Language, confidence);
    }

    private static (string Subcategory, double Confidence, string Reason) DetectDocumentTopic(string text, string fileName)
    {
        var sample = text + " " + fileName;
        var study = StudySignals().Matches(sample).Count;
        var work = WorkSignals().Matches(sample).Count;
        var finance = FinanceSignals().Matches(sample).Count;
        if (finance >= 2 && finance > study && finance > work) return ("finance", 0.90, "FINANCE_KEYWORDS");
        if (study >= 2 && study >= work) return ("study", 0.90, "STUDY_KEYWORDS");
        if (work >= 2 && work > study) return ("work", 0.88, "WORK_KEYWORDS");
        return ("other", 0.68, "DOCUMENT_CONTENT");
    }

    private static ClassificationResult Result(string category, string subcategory, string type, string language,
        string topic, string summary, double confidence, IReadOnlyList<string> reasons)
    {
        var candidate = new ClassificationCandidate(category, subcategory, confidence, reasons[0]);
        return new ClassificationResult(category, subcategory, type, language, topic, summary, confidence,
        language switch
        {
            "CSharp" => ".cs", "Python" => ".py", "Java" => ".java", "JavaScript" => ".js",
            "TypeScript" => ".ts", "SQL" => ".sql", "C" => ".c", "C++" => ".cpp",
            "HTML" => ".html", "CSS" => ".css", _ => null
        },
            [], reasons, [candidate]);
    }
}
