using System.IO.Compression;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using OptiumPlusFileManager.Api;
using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;
using OptiumPlusFileManager.Infrastructure;
using OptiumPlusFileManager.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OptiumPlusFileManager.IntegrationTests;

public sealed class ProjectAcceptanceTests
{
    [Fact]
    public async Task Natural_language_search_finds_content_in_csharp_folder_first()
    {
        await using var app = await TestApp.CreateAsync();
        var csharpFolder = Path.Combine(app.Paths.Sorted, "Programming", "CSharp");
        Directory.CreateDirectory(csharpFolder);
        var expected = Path.Combine(csharpFolder, "MathModule.cs");
        await File.WriteAllTextAsync(expected,
            "namespace Study; public static class MathModule { public static double Mean(double a, double b) => (a + b) / 2; }");
        var elsewhere = Path.Combine(app.Paths.Sorted, "Documents", "Study");
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "math-module.txt"), "математический модуль для занятий");

        var result = await app.Runtime.SearchFilesAsync("найди математический модуль с шарп");

        Assert.NotEmpty(result.Matches);
        Assert.Equal(Path.GetFullPath(expected), result.Matches[0].FilePath);
        Assert.Equal("C#", result.ScopeName);
        Assert.False(result.FellBackToAllFolders);
    }

    [Fact]
    public async Task Startup_indexes_sorted_files_without_history_into_persistent_search_index()
    {
        string? expected = null;
        await using var app = await TestApp.CreateAsync(paths =>
        {
            var folder = Path.Combine(paths.Sorted, "Programming", "CSharp");
            Directory.CreateDirectory(folder);
            expected = Path.Combine(folder, "MathTool.cs");
            File.WriteAllText(expected, "public static class MathTool { public static int Add(int a, int b) => a + b; }");
        });

        FileSearchResult? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            result = await app.Runtime.SearchFilesAsync("math C#");
            if (result.Message.Contains("постоянному индексу", StringComparison.OrdinalIgnoreCase)) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);

        Assert.NotNull(result);
        Assert.Contains(result!.Matches, hit => hit.FilePath.Equals(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("постоянному индексу", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Processing_adds_new_file_content_to_persistent_search_index()
    {
        await using var app = await TestApp.CreateAsync();
        var file = await app.Runtime.IngestTextAsync("MathTool.cs",
            "namespace Study; public static class MathTool { public static double Mean(double a, double b) => (a + b) / 2; }",
            "manual");
        Assert.Equal(nameof(FileStatus.Completed), file.Status);

        FileSearchResult? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            result = await app.Runtime.SearchFilesAsync("mean C#");
            if (result.Message.Contains("постоянному индексу", StringComparison.OrdinalIgnoreCase)) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);

        Assert.NotNull(result);
        Assert.Contains(result!.Matches, hit => hit.FilePath.Equals(file.CurrentPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("постоянному индексу", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Natural_language_search_falls_back_to_other_sorted_folders()
    {
        await using var app = await TestApp.CreateAsync();
        var csharpFolder = Path.Combine(app.Paths.Sorted, "Programming", "CSharp");
        Directory.CreateDirectory(csharpFolder);
        await File.WriteAllTextAsync(Path.Combine(csharpFolder, "Math.cs"),
            "public class Weather { public string Forecast => \"sunny\"; }");
        await File.WriteAllTextAsync(Path.Combine(csharpFolder, "Weather.cs"),
            "public class Weather { public string Forecast => \"sunny\"; }");
        var studyFolder = Path.Combine(app.Paths.Sorted, "Documents", "Study");
        Directory.CreateDirectory(studyFolder);
        var expected = Path.Combine(studyFolder, "math-notes.txt");
        await File.WriteAllTextAsync(expected, "математический модуль: вычисления, уравнения и примеры задач");

        var result = await app.Runtime.SearchFilesAsync("математический модуль на C#");

        Assert.True(result.FellBackToAllFolders);
        Assert.Contains(result.Matches, hit => hit.FilePath.Equals(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("остальные папки", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Natural_language_search_reports_when_file_does_not_exist()
    {
        await using var app = await TestApp.CreateAsync();

        var result = await app.Runtime.SearchFilesAsync("несуществующий файл с математикой");

        Assert.Empty(result.Matches);
        Assert.Contains("Совпадений не найдено", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ai_search_does_not_send_secret_files_to_provider()
    {
        await using var app = await TestApp.CreateAsync();
        var folder = Path.Combine(app.Paths.Sorted, "Programming", "CSharp");
        Directory.CreateDirectory(folder);
        var safePath = Path.Combine(folder, "Math.cs");
        var privatePath = Path.Combine(folder, "settings.txt");
        await File.WriteAllTextAsync(safePath, "public static class MathTools { public static int Sum(int a, int b) => a + b; }");
        await File.WriteAllTextAsync(privatePath, "api_key=redacted-test-placeholder");
        var analyzer = new FakeAiSearchAnalyzer();
        var settings = new AppSettings { AiProviderMode = "OpenAI", AiAllowedFileTypes = ["text"] };
        var service = new FileSearchService(app.Paths, settings, aiAnalyzer: analyzer);

        var result = await service.SearchAsync("math C#");

        Assert.NotNull(analyzer.LastInput);
        Assert.Single(analyzer.LastInput.Candidates);
        Assert.Equal("Math.cs", analyzer.LastInput.Candidates[0].Name);
        Assert.DoesNotContain("api_key=", analyzer.LastInput.Candidates[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privatePath, analyzer.LastInput.Candidates[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.UsedAi);
    }

    [Fact]
    public async Task Ai_search_keeps_files_from_private_source_folders_local_after_sorting()
    {
        await using var app = await TestApp.CreateAsync();
        var folder = Path.Combine(app.Paths.Sorted, "Programming", "CSharp");
        Directory.CreateDirectory(folder);
        var privateCurrentPath = Path.Combine(folder, "PrivateMath.cs");
        await File.WriteAllTextAsync(privateCurrentPath, "public static class MathTools { public static int Sum(int a, int b) => a + b; }");
        var analyzer = new FakeAiSearchAnalyzer();
        var settings = new AppSettings { AiProviderMode = "OpenAI", AiAllowedFileTypes = ["text"] };
        var service = new FileSearchService(app.Paths, settings, aiAnalyzer: analyzer,
            knownLocalOnlyCurrentPaths: [privateCurrentPath]);

        var result = await service.SearchAsync("math C#");

        Assert.Null(analyzer.LastInput);
        Assert.False(result.UsedAi);
        Assert.Contains(result.Matches, hit => hit.FilePath.Equals(privateCurrentPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ai_search_sends_image_bytes_only_after_visual_consent()
    {
        await using var app = await TestApp.CreateAsync();
        var folder = Path.Combine(app.Paths.Sorted, "Images", "Photos");
        Directory.CreateDirectory(folder);
        var imagePath = Path.Combine(folder, "picture.png");
        await File.WriteAllBytesAsync(imagePath, CreateOcrImage(ImageFormat.Png));
        var settings = new AppSettings { AiProviderMode = "OpenAI", AiAllowedFileTypes = ["png"] };
        var withoutConsent = new FakeAiSearchAnalyzer();
        var firstSearch = new FileSearchService(app.Paths, settings, aiAnalyzer: withoutConsent);

        await firstSearch.SearchAsync("picture");

        Assert.NotNull(withoutConsent.LastInput);
        Assert.Null(withoutConsent.LastInput.Candidates.Single().ImageData);
        settings.AllowAiImageAnalysis = true;
        var withConsent = new FakeAiSearchAnalyzer();
        var secondSearch = new FileSearchService(app.Paths, settings, aiAnalyzer: withConsent);

        await secondSearch.SearchAsync("picture");

        Assert.NotNull(withConsent.LastInput);
        Assert.Equal("image/png", withConsent.LastInput.Candidates.Single().ImageMimeType);
        Assert.NotNull(withConsent.LastInput.Candidates.Single().ImageData);
    }

    [Fact]
    public async Task Ai_search_rejects_provider_file_ids_outside_the_candidate_list()
    {
        const string response = """{"output":[{"content":[{"type":"output_text","text":"{\"matches\":[{\"candidateId\":\"invented\",\"score\":0.99,\"reason\":\"match\"}]}"}]}]}""";
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        var analyzer = new OpenAiFileSearchAnalyzer("model", "test-key", client);
        var input = new AiFileSearchInput("math file", [new AiFileSearchCandidate("candidate-1", "Math.cs", "Programming/CSharp", "text", "class Math")]);

        await Assert.ThrowsAsync<InvalidDataException>(() => analyzer.SearchAsync(input));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("gemini")]
    [InlineData("claude")]
    public async Task Ai_search_adapters_return_validated_matches_and_attach_consented_images(string providerId)
    {
        var matchJson = JsonSerializer.Serialize(new
        {
            matches = new[] { new { candidateId = "candidate-1", score = 0.94, reason = "Подходит по запросу" } }
        });
        var response = providerId switch
        {
            "openai" => JsonSerializer.Serialize(new { output = new[] { new { content = new[] { new { type = "output_text", text = matchJson } } } } }),
            "gemini" => JsonSerializer.Serialize(new { steps = new[] { new { type = "model_output", content = new[] { new { type = "text", text = matchJson } } } } }),
            _ => JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = matchJson } } })
        };
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        IAiFileSearchAnalyzer analyzer = providerId switch
        {
            "openai" => new OpenAiFileSearchAnalyzer("test-model", "fake-api-key", client),
            "gemini" => new GeminiFileSearchAnalyzer("test-model", "fake-api-key", client),
            _ => new ClaudeFileSearchAnalyzer("test-model", "fake-api-key", client)
        };
        var input = new AiFileSearchInput("math module",
            [new AiFileSearchCandidate("candidate-1", "MathModule.png", "Images/Photos", "png", "math notes", "image/png", [137, 80, 78, 71])]);

        var matches = await analyzer.SearchAsync(input);

        Assert.Single(matches);
        Assert.Equal("candidate-1", matches[0].CandidateId);
        Assert.Equal(0.94, matches[0].Score);
        Assert.NotNull(handler.LastBody);
        Assert.Contains(providerId switch
        {
            "openai" => "input_image",
            "gemini" => "mime_type",
            _ => "media_type"
        }, handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        if (providerId == "openai") Assert.Equal("Bearer fake-api-key", handler.Authorization);
        if (providerId == "gemini")
        {
            Assert.Equal("fake-api-key", handler.GoogleApiKey);
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", handler.LastUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(handler.LastBody!);
            Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal("application/json", body.RootElement.GetProperty("response_format").GetProperty("mime_type").GetString());
            var image = body.RootElement.GetProperty("input").EnumerateArray().Single(part =>
                part.TryGetProperty("type", out var type) && type.GetString() == "image");
            Assert.Equal("image/png", image.GetProperty("mime_type").GetString());
        }
        if (providerId == "claude") Assert.Equal("fake-api-key", handler.AnthropicApiKey);
    }

    [Fact]
    public async Task Ai_search_checks_all_files_in_batches_until_a_match_or_exhaustion()
    {
        await using var app = await TestApp.CreateAsync();
        var folder = Path.Combine(app.Paths.Sorted, "Programming", "CSharp");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < 17; index++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"Neutral{index:D2}.cs"), "// neutral file without the query");
        var analyzer = new FakeAiSearchAnalyzer(returnMatches: false);
        var settings = new AppSettings { AiProviderMode = "OpenAI", AiAllowedFileTypes = ["text"] };
        var service = new FileSearchService(app.Paths, settings, aiAnalyzer: analyzer);

        var result = await service.SearchAsync("unique phrase C#");

        Assert.Empty(result.Matches);
        Assert.Equal(17, result.ScannedFileCount);
        Assert.Equal(new[] { 16, 1 }, analyzer.BatchSizes);
    }

    [Fact]
    public async Task CSharp_without_extension_is_classified_by_contents()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("Батыр", "using System;\npublic class Greeter { public static void Main() { Console.WriteLine(\"Hi\"); } }");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.Equal(nameof(FileStatus.Completed), outcome.File.Status);
        Assert.Equal("programming", outcome.File.CategoryId);
        Assert.Equal("csharp", outcome.File.SubcategoryId);
        Assert.Equal("CSharp", outcome.File.DetectedLanguage);
        Assert.True(File.Exists(outcome.File.CurrentPath));
    }

    [Fact]
    public async Task Python_in_txt_is_classified_as_python()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("Python-в-тексте.txt", "from pathlib import Path\ndef show(path):\n    print(Path(path).name)\n");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.Equal(nameof(FileStatus.Completed), outcome.File.Status);
        Assert.Equal("programming", outcome.File.CategoryId);
        Assert.Equal("python", outcome.File.SubcategoryId);
        Assert.Equal("Python", outcome.File.DetectedLanguage);
    }

    [Fact]
    public async Task Sql_code_is_sorted_into_its_own_folder_created_on_demand()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("database-notes.txt", "CREATE TABLE Students (Id INTEGER PRIMARY KEY, Name TEXT);\nINSERT INTO Students VALUES (1, 'A');\nSELECT Name FROM Students;\nUPDATE Students SET Name = 'B';");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.Equal(nameof(FileStatus.Completed), outcome.File.Status);
        Assert.Equal("programming", outcome.File.CategoryId);
        Assert.Equal("sql", outcome.File.SubcategoryId);
        Assert.Equal("SQL", outcome.File.DetectedLanguage);
        Assert.EndsWith(Path.Combine("Programming", "SQL", "database-notes.txt"), outcome.File.CurrentPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.GetDirectoryName(outcome.File.CurrentPath)));
    }

    [Fact]
    public async Task Zip_payload_with_jpg_extension_is_not_classified_as_image()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteZip("подмена.jpg", "sample.txt", "archive payload");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.Equal("zip", outcome.File.DetectedFileType);
        Assert.NotEqual("images", outcome.File.CategoryId);
        Assert.Equal(nameof(FileStatus.NeedsReview), outcome.File.Status);
    }

    [Fact]
    public async Task Large_text_file_is_processed_with_bounded_managed_allocations()
    {
        await using var app = await TestApp.CreateAsync();
        var path = Path.Combine(app.Incoming, "large.txt");
        const int fileSize = 64 * 1024 * 1024;
        var chunk = Encoding.UTF8.GetBytes(new string('a', 64 * 1024));
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            for (var written = 0; written < fileSize; written += chunk.Length)
                await output.WriteAsync(chunk);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        Assert.Equal(fileSize, outcome.File.SizeBytes);
        Assert.True(allocated < 32L * 1024 * 1024, $"Pipeline allocated {allocated:N0} bytes for a {fileSize:N0}-byte file.");
        Assert.True(File.Exists(outcome.File.CurrentPath));
    }

    [Fact]
    public async Task File_locked_for_writing_is_not_moved_before_writer_releases_it()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("download.txt", "neutral content still being written");
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var processing = app.Runtime.ProcessWatchedFileAsync(path);
        await Task.Delay(TimeSpan.FromMilliseconds(1400));

        Assert.False(processing.IsCompleted);
        Assert.True(File.Exists(path));
        await writer.DisposeAsync();

        var outcome = await processing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(nameof(FileStatus.NeedsReview), outcome.File.Status);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(outcome.File.CurrentPath));
    }

    [Fact]
    public async Task Enabled_watcher_processes_new_files_automatically()
    {
        await using var app = await TestApp.CreateAsync();
        await app.Runtime.SetAutomaticSortingAsync(true);
        var path = app.WriteIncoming("watched.txt", "neutral file received by the live watcher");

        var deadline = DateTime.UtcNow.AddSeconds(12);
        FileRecord? processed = null;
        while (DateTime.UtcNow < deadline)
        {
            var records = await app.Runtime.ListFilesAsync(take: 20);
            processed = records.FirstOrDefault(record => record.OriginalName == "watched.txt");
            if (processed is not null && processed.Status is nameof(FileStatus.NeedsReview) or nameof(FileStatus.Completed))
                break;
            await Task.Delay(100);
        }

        Assert.NotNull(processed);
        Assert.Equal(nameof(FileStatus.NeedsReview), processed.Status);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(processed.CurrentPath));
    }

    [Fact]
    public async Task Watch_exclusions_match_nested_paths_and_filename_masks()
    {
        await using var app = await TestApp.CreateAsync();
        var excludedDirectory = Path.Combine(app.Incoming, "private");
        await app.Runtime.SaveWatchExclusionsAsync([excludedDirectory], ["*.ignore", "secret-??.txt"]);

        Assert.True(app.Runtime.IsExcludedPath(Path.Combine(excludedDirectory, "notes.txt")));
        Assert.True(app.Runtime.IsExcludedPath(Path.Combine(app.Incoming, "cache.ignore")));
        Assert.True(app.Runtime.IsExcludedPath(Path.Combine(app.Incoming, "secret-ab.txt")));
        Assert.False(app.Runtime.IsExcludedPath(Path.Combine(app.Incoming, "visible.txt")));
    }

    [Fact]
    public async Task Identical_content_is_detected_by_sha256_and_second_copy_stays_in_place()
    {
        await using var app = await TestApp.CreateAsync();
        var firstPath = app.WriteIncoming("duplicate-a.txt", "same content for SHA-256 duplicate check");
        var first = await app.Runtime.ProcessWatchedFileAsync(firstPath);
        var secondPath = app.WriteIncoming("duplicate-b.txt", "same content for SHA-256 duplicate check");

        var second = await app.Runtime.ProcessWatchedFileAsync(secondPath);

        Assert.Equal(nameof(FileStatus.NeedsReview), first.File.Status);
        Assert.Equal(nameof(FileStatus.Duplicate), second.File.Status);
        Assert.Equal(secondPath, second.File.CurrentPath);
        Assert.True(File.Exists(secondPath));
        Assert.Equal(first.File.Sha256, second.File.Sha256);
    }

    [Fact]
    public async Task Different_files_with_same_name_are_both_preserved()
    {
        await using var app = await TestApp.CreateAsync();
        var firstPath = app.WriteIncoming(Path.Combine("a", "same.txt"), "neutral content one");
        var secondPath = app.WriteIncoming(Path.Combine("b", "same.txt"), "neutral content two");

        var first = await app.Runtime.ProcessWatchedFileAsync(firstPath);
        var second = await app.Runtime.ProcessWatchedFileAsync(secondPath);

        Assert.Equal(nameof(FileStatus.NeedsReview), first.File.Status);
        Assert.Equal(nameof(FileStatus.NeedsReview), second.File.Status);
        Assert.NotEqual(first.File.Sha256, second.File.Sha256);
        Assert.NotEqual(first.File.CurrentPath, second.File.CurrentPath);
        Assert.True(File.Exists(first.File.CurrentPath));
        Assert.True(File.Exists(second.File.CurrentPath));
    }

    [Fact]
    public async Task Corrupt_pdf_is_retained_in_review_or_errors()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("damaged.pdf", "%PDF-1.7\nnot a real PDF document\n%%EOF");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.True(outcome.File.Status is nameof(FileStatus.NeedsReview) or nameof(FileStatus.Failed));
        Assert.True(File.Exists(outcome.File.CurrentPath));
        Assert.True(FileSafety.IsWithin(outcome.File.CurrentPath, app.Paths.Review) ||
                    FileSafety.IsWithin(outcome.File.CurrentPath, app.Paths.Errors));
    }

    [Fact]
    public async Task Zip_traversal_is_quarantined_without_extracting_outside_root()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteZip("unsafe.zip", "../outside.txt", "must not be extracted");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.Equal(nameof(FileStatus.Quarantined), outcome.File.Status);
        Assert.True(FileSafety.IsWithin(outcome.File.CurrentPath, app.Paths.Quarantine));
        Assert.False(File.Exists(Path.Combine(app.Root, "outside.txt")));
        Assert.True(File.Exists(outcome.File.CurrentPath));
    }

    [Fact]
    public async Task Ai_off_uses_local_classifier_without_provider_keys()
    {
        await using var app = await TestApp.CreateAsync();
        app.Settings.AiProviderMode = "Off";
        var path = app.WriteIncoming("local.txt", "neutral local-only text");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.Equal("local", outcome.File.ProviderId);
        Assert.Equal(nameof(FileStatus.NeedsReview), outcome.File.Status);
    }

    [Fact]
    public async Task Ai_provider_rejects_unknown_category_and_unexpected_json_fields()
    {
        var local = new ClassificationResult("documents", "other", "text", "", "Text", "Local", 0.68,
            null, [], ["TEXT_CONTENT"], [new("documents", "other", 0.68, "TEXT_CONTENT")]);
        var input = new FileAnalysisInput("sample.txt", ".txt", "text/plain", "text", 10, "sample", false, local);
        var invalidCategory = new InvalidPayloadAnalyzer("""
            {"categoryId":"../../outside","subcategoryId":"other","detectedFileType":"text","detectedLanguage":"","topic":"","summary":"","confidence":0.9,"suggestedExtension":null,"tags":[],"reasonCodes":[]}
            """);
        var unexpectedField = new InvalidPayloadAnalyzer("""
            {"categoryId":"documents","subcategoryId":"other","detectedFileType":"text","detectedLanguage":"","topic":"","summary":"","confidence":0.9,"suggestedExtension":null,"tags":[],"reasonCodes":[],"destinationPath":"C:\\outside"}
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => invalidCategory.AnalyzeAsync(input));
        await Assert.ThrowsAsync<JsonException>(() => unexpectedField.AnalyzeAsync(input));
    }

    [Fact]
    public async Task Provider_adapters_send_schema_enforced_requests_to_current_endpoints()
    {
        const string classification = "{\"categoryId\":\"documents\",\"subcategoryId\":\"study\",\"detectedFileType\":\"text\",\"detectedLanguage\":\"\",\"topic\":\"study\",\"summary\":\"study notes\",\"confidence\":0.91,\"suggestedExtension\":null,\"tags\":[],\"reasonCodes\":[]}";
        var local = new ClassificationResult("documents", "other", "text", "", "Text", "Local", 0.68,
            null, [], ["TEXT_CONTENT"], [new("documents", "other", 0.68, "TEXT_CONTENT")]);
        var input = new FileAnalysisInput("sample.txt", ".txt", "text/plain", "text", 10, "study notes", false, local);

        var openAiHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            output = new[] { new { content = new[] { new { type = "output_text", text = classification } } } }
        }));
        using (var client = new HttpClient(openAiHandler))
        {
            var result = await new OpenAiFileAnalyzer("gpt-5.6-terra", "test-key", client).AnalyzeAsync(input);
            Assert.Equal("openai", result.ProviderId);
            Assert.Equal("https://api.openai.com/v1/responses", openAiHandler.LastUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key", openAiHandler.Authorization);
            using var body = JsonDocument.Parse(openAiHandler.LastBody!);
            Assert.Equal("gpt-5.6-terra", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("json_schema", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        }

        var geminiHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = classification } } } } }
        }));
        using (var client = new HttpClient(geminiHandler))
        {
            var result = await new GeminiFileAnalyzer("gemini-3.6-flash", "test-key", client).AnalyzeAsync(input);
            Assert.Equal("gemini", result.ProviderId);
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent", geminiHandler.LastUri!.AbsoluteUri);
            Assert.Equal("test-key", geminiHandler.GoogleApiKey);
            using var body = JsonDocument.Parse(geminiHandler.LastBody!);
            var format = body.RootElement.GetProperty("generationConfig").GetProperty("responseFormat").GetProperty("text");
            Assert.Equal("application/json", format.GetProperty("mimeType").GetString());
            Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        }

        var claudeHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = classification } }
        }));
        using (var client = new HttpClient(claudeHandler))
        {
            var result = await new ClaudeFileAnalyzer("claude-sonnet-5-5", "test-key", client).AnalyzeAsync(input);
            Assert.Equal("claude", result.ProviderId);
            Assert.Equal("https://api.anthropic.com/v1/messages", claudeHandler.LastUri!.AbsoluteUri);
            Assert.Equal("test-key", claudeHandler.AnthropicApiKey);
            using var body = JsonDocument.Parse(claudeHandler.LastBody!);
            Assert.Equal("claude-sonnet-5-5", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("json_schema", body.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Vision_image_is_attached_using_each_providers_supported_request_shape()
    {
        const string classification = "{\"categoryId\":\"images\",\"subcategoryId\":\"photo\",\"detectedFileType\":\"png\",\"detectedLanguage\":\"\",\"topic\":\"landscape\",\"summary\":\"A landscape photo\",\"confidence\":0.92,\"suggestedExtension\":null,\"tags\":[\"landscape\"],\"reasonCodes\":[\"VISUAL_CONTENT\"]}";
        var local = new ClassificationResult("images", "other", "png", "", "Image", "Local image classification", 0.96,
            null, [], ["IMAGE_MAGIC_BYTES"], [new("images", "other", 0.96, "IMAGE_MAGIC_BYTES")]);
        var input = new FileAnalysisInput("photo.png", ".png", "image/png", "png", 3, "", false, local, [1, 2, 3]);

        var openAiHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            output = new[] { new { content = new[] { new { type = "output_text", text = classification } } } }
        }));
        using (var client = new HttpClient(openAiHandler))
        {
            await new OpenAiFileAnalyzer("test-model", "test-key", client).AnalyzeAsync(input);
            using var body = JsonDocument.Parse(openAiHandler.LastBody!);
            var image = body.RootElement.GetProperty("input")[0].GetProperty("content")[1];
            Assert.Equal("input_image", image.GetProperty("type").GetString());
            Assert.StartsWith("data:image/png;base64,", image.GetProperty("image_url").GetString(), StringComparison.Ordinal);
        }

        var geminiHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = classification } } } } }
        }));
        using (var client = new HttpClient(geminiHandler))
        {
            await new GeminiFileAnalyzer("test-model", "test-key", client).AnalyzeAsync(input);
            using var body = JsonDocument.Parse(geminiHandler.LastBody!);
            var image = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData");
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            Assert.Equal("AQID", image.GetProperty("data").GetString());
        }

        var claudeHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = classification } }
        }));
        using (var client = new HttpClient(claudeHandler))
        {
            await new ClaudeFileAnalyzer("test-model", "test-key", client).AnalyzeAsync(input);
            using var body = JsonDocument.Parse(claudeHandler.LastBody!);
            var image = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
            Assert.Equal("image", image.GetProperty("type").GetString());
            Assert.Equal("image/png", image.GetProperty("source").GetProperty("media_type").GetString());
            Assert.Equal("AQID", image.GetProperty("source").GetProperty("data").GetString());
        }
    }

    [Fact]
    public async Task Monthly_ai_quota_reservations_are_atomic_and_persisted()
    {
        await using var app = await TestApp.CreateAsync();
        var repository = new SqliteFileRepository(app.Paths);
        await repository.InitializeAsync();

        Assert.True(await repository.TryReserveAiRequestsAsync(1, 2));
        Assert.True(await repository.TryReserveAiRequestsAsync(1, 2));
        Assert.False(await repository.TryReserveAiRequestsAsync(1, 2));
        Assert.Equal(2, await repository.GetAiRequestsUsedThisMonthAsync());
    }

    [Fact]
    public async Task Legacy_default_provider_models_are_migrated_on_startup()
    {
        await using var app = await TestApp.CreateAsync();
        app.Settings.OpenAiModel = "gpt-5-mini";
        app.Settings.GeminiModel = "gemini-2.5-flash";
        app.Settings.ClaudeModel = "claude-sonnet-4-6";

        await app.RestartRuntimeAsync();

        Assert.Equal("gpt-5.6-terra", app.Settings.OpenAiModel);
        Assert.Equal("gemini-3.6-flash", app.Settings.GeminiModel);
        Assert.Equal("claude-sonnet-5-5", app.Settings.ClaudeModel);
    }

    [Fact]
    public async Task Compare_mode_reserves_both_provider_calls_before_sending_either()
    {
        var local = new ClassificationResult("documents", "other", "text", "", "Text", "Local", 0.68,
            null, [], ["TEXT_CONTENT"], [new("documents", "other", 0.68, "TEXT_CONTENT")]);
        var input = new FileAnalysisInput("sample.txt", ".txt", "text/plain", "text", 10, "sample", false, local);
        var first = new FakeAiAnalyzer("openai", new("documents", "study", "text", "", "study", "first", 0.9, null, [], [], "openai"));
        var second = new FakeAiAnalyzer("gemini", new("documents", "study", "text", "", "study", "second", 0.9, null, [], [], "gemini"));
        var reservedUnits = 0;
        var composite = new CompositeAiFileAnalyzer(new AppSettings { AiProviderMode = "Compare" },
            (count, _) => { reservedUnits += count; return Task.FromResult(false); }, [first, second]);

        await Assert.ThrowsAsync<AiRequestLimitExceededException>(() => composite.AnalyzeAsync(input));

        Assert.Equal(2, reservedUnits);
        Assert.Equal(0, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task User_can_leave_review_file_unsorted_without_moving_or_losing_it()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("leave-in-review.txt", "neutral content with no category clues");
        var processed = await app.Runtime.ProcessWatchedFileAsync(path);

        var skipped = await app.Runtime.SkipAsync(processed.File.Id);

        Assert.Equal(nameof(FileStatus.Skipped), skipped.Status);
        Assert.Equal(processed.File.CurrentPath, skipped.CurrentPath);
        Assert.True(File.Exists(skipped.CurrentPath));
        Assert.DoesNotContain(await app.Runtime.ListFilesAsync(nameof(FileStatus.NeedsReview)), item => item.Id == skipped.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Runtime.SkipAsync(skipped.Id));
    }

    [Fact]
    public async Task Cloud_policy_blocks_disallowed_formats_and_private_subfolders()
    {
        await using var app = await TestApp.CreateAsync();
        var privatePath = Path.Combine(app.Incoming, "private");
        Directory.CreateDirectory(privatePath);

        await app.Runtime.SaveCloudPolicyAsync(["text", "pdf"], [privatePath]);

        Assert.True(AiFilePolicy.IsAllowed(app.Settings, Path.Combine(app.Incoming, "ordinary.txt"), "text"));
        Assert.False(AiFilePolicy.IsAllowed(app.Settings, Path.Combine(app.Incoming, "ordinary.txt"), "png"));
        Assert.False(AiFilePolicy.IsAllowed(app.Settings, Path.Combine(privatePath, "note.txt"), "text"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Runtime.SaveCloudPolicyAsync(["text"], [app.Root]));
        await Assert.ThrowsAsync<ArgumentException>(() => app.Runtime.SaveCloudPolicyAsync(["unknown"], []));
    }

    [Fact]
    public async Task Vision_analysis_requires_both_image_allowlist_and_explicit_permission()
    {
        await using var app = await TestApp.CreateAsync();
        var path = Path.Combine(app.Incoming, "photo.png");
        await app.Runtime.SaveCloudPolicyAsync(["png"], []);

        Assert.True(AiFilePolicy.IsAllowed(app.Settings, path, "png"));
        Assert.False(AiFilePolicy.CanSendVisionImage(app.Settings, path, "png"));

        await app.Runtime.SaveCloudPolicyAsync(["png"], [], allowAiImageAnalysis: true);

        Assert.True(AiFilePolicy.CanSendVisionImage(app.Settings, path, "png"));
        app.Settings.LocalOnlyPaths = [app.Incoming];
        Assert.False(AiFilePolicy.CanSendVisionImage(app.Settings, path, "png"));
    }

    [Fact]
    public void Image_metadata_reader_extracts_dimensions_without_decoding_image_content()
    {
        var path = Path.Combine(Path.GetTempPath(), "filesorter-metadata-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromHexString("89504E470D0A1A0A0000000D4948445200000001000000010806000000"));
        try
        {
            var metadata = MediaMetadataReader.Read(path, "png", CancellationToken.None);
            Assert.Contains("1 × 1 пикс.", metadata, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Built_in_ocr_reads_english_and_russian_from_an_image()
    {
        var path = Path.Combine(Path.GetTempPath(), "filesorter-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, CreateOcrImage(ImageFormat.Png));

            var extracted = await new SafeContentExtractor().ExtractAsync(path, "png");

            Assert.Contains("STUDY NOTES 123", extracted.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("УЧЕБНЫЙ КОНСПЕКТ", extracted.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Null(extracted.Warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Built_in_ocr_reads_a_scanned_pdf_page()
    {
        var path = Path.Combine(Path.GetTempPath(), "filesorter-scan-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var jpeg = CreateOcrImage(ImageFormat.Jpeg);
            await File.WriteAllBytesAsync(path, CreateScannedPdf(jpeg, 1800, 560));

            var extracted = await new SafeContentExtractor().ExtractAsync(path, "pdf");

            Assert.Contains("STUDY NOTES 123", extracted.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("УЧЕБНЫЙ КОНСПЕКТ", extracted.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Null(extracted.Warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Demo_scan_sample_is_classified_as_a_study_document()
    {
        var path = Path.Combine(FindRepositoryRoot(), "demo", "samples", "Скан-конспект.pdf");

        var extracted = await new SafeContentExtractor().ExtractAsync(path, "pdf");
        var format = new FileFormatInfo("pdf", "application/pdf", false, false, false);
        var classification = new LocalFileClassifier().Classify(format, Path.GetFileName(path), ".pdf", extracted.Text);

        Assert.Contains("LECTURE COURSEWORK", extracted.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("УЧЕБНЫЙ КОНСПЕКТ", extracted.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(extracted.Warning);
        Assert.Equal("documents", classification.CategoryId);
        Assert.Equal("study", classification.SubcategoryId);
    }

    [Fact]
    public void Local_classifier_recognizes_inflected_russian_study_keywords()
    {
        var format = new FileFormatInfo("pdf", "application/pdf", false, false, false);

        var result = new LocalFileClassifier().Classify(format, "scan.pdf", ".pdf",
            "Учебный конспект: задание на семестр.");

        Assert.Equal("documents", result.CategoryId);
        Assert.Equal("study", result.SubcategoryId);
    }

    [Fact]
    public async Task Built_in_ocr_skips_an_image_with_unsafe_dimensions_before_decoding()
    {
        var path = Path.Combine(Path.GetTempPath(), "filesorter-oversized-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var header = Convert.FromHexString("89504E470D0A1A0A0000000D49484452");
            var dimensions = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dimensions.AsSpan(0, 4), 50_000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dimensions.AsSpan(4, 4), 50_000);
            await File.WriteAllBytesAsync(path, [.. header, .. dimensions]);

            var extracted = await new SafeContentExtractor().ExtractAsync(path, "png");

            Assert.Contains("безопасный предел OCR", extracted.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Технические метаданные не удалось", extracted.Text, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Wav_metadata_reader_extracts_format_and_duration()
    {
        var path = Path.Combine(Path.GetTempPath(), "filesorter-metadata-" + Guid.NewGuid().ToString("N") + ".wav");
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(8036u);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(8000u);
            writer.Write(8000u);
            writer.Write((ushort)1);
            writer.Write((ushort)8);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(8000u);
            writer.Write(new byte[8000]);
        }
        try
        {
            var metadata = MediaMetadataReader.Read(path, "wav", CancellationToken.None);
            Assert.Contains("частота: 8000 Гц", metadata, StringComparison.Ordinal);
            Assert.Contains("длительность: 00:00:01", metadata, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Low_confidence_result_is_never_automatically_moved_to_sorted()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("uncertain.txt", "neutral content with no category clues");

        var outcome = await app.Runtime.ProcessWatchedFileAsync(path);

        Assert.True(outcome.File.Confidence < app.Settings.AutoSortConfidence);
        Assert.Equal(nameof(FileStatus.NeedsReview), outcome.File.Status);
        Assert.False(FileSafety.IsWithin(outcome.File.CurrentPath, app.Paths.Sorted));
    }

    [Fact]
    public async Task Remembered_exact_filename_rule_is_reused_for_a_later_file()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await app.Runtime.IngestTextAsync("semester-note.txt", "neutral first note", "api");
        Assert.Equal(nameof(FileStatus.NeedsReview), first.Status);
        var approved = await app.Runtime.ApproveAsync(first.Id, "documents", "study", rememberFileNameRule: true);
        var secondPath = app.WriteIncoming("semester-note.txt", "neutral different study note");

        var second = await app.Runtime.ProcessWatchedFileAsync(secondPath);

        Assert.Equal(nameof(FileStatus.Completed), approved.Status);
        Assert.Equal(nameof(FileStatus.Completed), second.File.Status);
        Assert.Equal("documents", second.File.CategoryId);
        Assert.Equal("study", second.File.SubcategoryId);
        Assert.Contains("правилу", second.File.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Moving_checkpoint_is_recovered_once_after_runtime_restart()
    {
        await using var app = await TestApp.CreateAsync();
        var originalPath = app.WriteIncoming("recover.txt", "recovery checkpoint payload");
        var destination = Path.Combine(app.Paths.Review, "Documents", "Other", "recover.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(originalPath, destination);
        var repository = new SqliteFileRepository(app.Paths);
        await repository.InitializeAsync();
        var record = new FileRecord
        {
            OriginalName = Path.GetFileName(originalPath),
            OriginalPath = originalPath,
            CurrentPath = originalPath,
            PendingDestinationPath = destination,
            SizeBytes = new FileInfo(originalPath).Length,
            Sha256 = await FileSafety.ComputeSha256Async(originalPath),
            Status = nameof(FileStatus.Moving),
            SourceType = "watch"
        };
        await repository.InsertAsync(record, "Test crash checkpoint.");
        await app.RestartRuntimeAsync();

        var recovered = await app.Runtime.GetFileAsync(record.Id);

        Assert.NotNull(recovered);
        Assert.Equal(nameof(FileStatus.NeedsReview), recovered.Status);
        Assert.Equal(destination, recovered.CurrentPath);
        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(destination));

        await app.RestartRuntimeAsync();
        var afterSecondRestart = await app.Runtime.GetFileAsync(record.Id);
        Assert.Equal(nameof(FileStatus.NeedsReview), afterSecondRestart!.Status);
        Assert.Equal(destination, afterSecondRestart.CurrentPath);
    }

    [Fact]
    public async Task Revert_restores_original_file_only_when_saved_hash_still_matches()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("revert.cs", "using System;\nclass C { static void Main() { Console.WriteLine(1); } }");
        var processed = await app.Runtime.ProcessWatchedFileAsync(path);
        var expectedHash = processed.File.Sha256;

        var reverted = await app.Runtime.RevertAsync(processed.File.Id);

        Assert.Equal(nameof(FileStatus.Reverted), reverted.Status);
        Assert.Equal(path, reverted.CurrentPath);
        Assert.True(File.Exists(path));
        Assert.Equal(expectedHash, await FileSafety.ComputeSha256Async(path));
    }

    [Fact]
    public async Task Revert_refuses_to_restore_file_after_content_was_modified()
    {
        await using var app = await TestApp.CreateAsync();
        var path = app.WriteIncoming("tamper.cs", "using System;\nclass C { static void Main() { Console.WriteLine(2); } }");
        var processed = await app.Runtime.ProcessWatchedFileAsync(path);
        await File.WriteAllTextAsync(processed.File.CurrentPath, "tampered payload");

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Runtime.RevertAsync(processed.File.Id));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(processed.File.CurrentPath));
    }

    [Fact]
    public async Task Read_only_mcp_client_can_read_but_cannot_upload_or_move()
    {
        await using var app = await TestApp.CreateAsync();
        var review = await app.Runtime.IngestTextAsync("mcp-review.txt", "neutral content requiring review", "api");
        var tokens = new ApiTokenStore();
        await using var api = new LocalApiServer(app.Runtime, tokens, dynamicPort: true);
        await api.StartAsync();
        var readOnlyHttpClient = new HttpClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(api.BaseAddress + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {tokens.ReadToken}" }
        }, readOnlyHttpClient, ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);

        var listedTools = await client.ListToolsAsync();
        Assert.Contains(listedTools, tool => tool.Name == "list_files");
        var readResult = await client.CallToolAsync("list_files", new Dictionary<string, object?>
        {
            ["status"] = "",
            ["categoryId"] = "",
            ["sourceType"] = "",
            ["take"] = 10
        });
        Assert.NotEqual(true, readResult.IsError);

        var uploadResult = await client.CallToolAsync("put_text_file", new Dictionary<string, object?>
        {
            ["fileName"] = "blocked.txt",
            ["content"] = "read-only token must not upload"
        });
        var moveResult = await client.CallToolAsync("move_file", new Dictionary<string, object?>
        {
            ["fileId"] = review.Id,
            ["categoryId"] = "documents",
            ["subcategoryId"] = "study",
            ["rememberFileNameRule"] = false
        });

        Assert.True(uploadResult.IsError);
        Assert.True(moveResult.IsError);
        Assert.Equal(nameof(FileStatus.NeedsReview), (await app.Runtime.GetFileAsync(review.Id))!.Status);
    }

    [Fact]
    public async Task Rest_read_token_cannot_write_and_write_token_can_approve()
    {
        await using var app = await TestApp.CreateAsync();
        var tokens = new ApiTokenStore();
        await using var api = new LocalApiServer(app.Runtime, tokens, dynamicPort: true);
        await api.StartAsync();
        using var client = new HttpClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(api.BaseAddress + "/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(api.BaseAddress + "/api/files")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.ReadToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(api.BaseAddress + "/api/files")).StatusCode);
        var readOnlyUpload = await client.PostAsync(api.BaseAddress + "/api/files/text",
            JsonBody(new PutTextFileRequest("rest-review.txt", "neutral content that requires review")));
        Assert.Equal(HttpStatusCode.Unauthorized, readOnlyUpload.StatusCode);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.WriteToken);
        var upload = await client.PostAsync(api.BaseAddress + "/api/files/text",
            JsonBody(new PutTextFileRequest("rest-review.txt", "neutral content that requires review")));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(nameof(FileStatus.NeedsReview), body.RootElement.GetProperty("status").GetString());

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.ReadToken);
        var deniedApproval = await client.PostAsync(api.BaseAddress + $"/api/files/{id}/approve",
            JsonBody(new ApproveFileRequest("documents", "study", false)));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedApproval.StatusCode);
        Assert.Equal(nameof(FileStatus.NeedsReview), (await app.Runtime.GetFileAsync(id))!.Status);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.WriteToken);
        var approval = await client.PostAsync(api.BaseAddress + $"/api/files/{id}/approve",
            JsonBody(new ApproveFileRequest("documents", "study", false)));
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
    }

    [Fact]
    public async Task Packaged_executable_starts_and_serves_mcp_stdio()
    {
        var root = Path.Combine(Path.GetTempPath(), "OptiumPlusFileManager.StdioTests", Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(root, "incoming");
        var data = Path.Combine(root, "data");
        var sorted = Path.Combine(root, "sorted");
        Directory.CreateDirectory(incoming);
        var settings = new AppSettings
        {
            WatchFolders = [incoming],
            SourceFolder = incoming,
            DestinationFolder = sorted,
            AutomaticSorting = false,
            AiProviderMode = "Off"
        };
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(settings));

        try
        {
            var repoRoot = FindRepositoryRoot();
            var packagedExe = Path.Combine(repoRoot, "publish", "win-x64", "OptiumPlusFileManager.exe");
            if (!File.Exists(packagedExe))
                packagedExe = Path.Combine(AppContext.BaseDirectory, "OptiumPlusFileManager.exe");
            Assert.True(File.Exists(packagedExe), $"Published or built application executable not found at {packagedExe}.");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = packagedExe,
                Arguments = ["--mcp-stdio"],
                WorkingDirectory = Path.GetDirectoryName(packagedExe),
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["FILE_SORTER_DATA_ROOT"] = data,
                    ["FILE_SORTER_SORTED_ROOT"] = sorted
                }
            });
            await using var client = await McpClient.CreateAsync(transport).WaitAsync(TimeSpan.FromSeconds(30));
            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "put_text_file");
            var result = await client.CallToolAsync("put_text_file", new Dictionary<string, object?>
            {
                ["fileName"] = "stdio.txt",
                ["content"] = "stdio integration test"
            });
            Assert.NotEqual(true, result.IsError);

            var scannedPdfPath = Path.Combine(incoming, "скан-учебный.pdf");
            await File.WriteAllBytesAsync(scannedPdfPath,
                CreateScannedPdf(CreateOcrImage(ImageFormat.Jpeg), 1800, 560));
            var scannedPdfResult = await client.CallToolAsync("submit_local_file", new Dictionary<string, object?>
            {
                ["path"] = scannedPdfPath
            });
            Assert.NotEqual(true, scannedPdfResult.IsError);
            var scannedPdfJson = scannedPdfResult.Content.OfType<TextContentBlock>().Single().Text;
            var scannedFile = JsonSerializer.Deserialize<FileDto>(scannedPdfJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(scannedFile);
            Assert.Equal("pdf", scannedFile.DetectedFileType);
            Assert.Equal("documents", scannedFile.CategoryId);
            Assert.Equal("study", scannedFile.SubcategoryId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static HttpContent JsonBody<T>(T value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static byte[] CreateOcrImage(ImageFormat format)
    {
        using var bitmap = new Bitmap(1800, 560);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Arial", 82, FontStyle.Bold, GraphicsUnit.Pixel);
        graphics.DrawString("STUDY NOTES 123", font, Brushes.Black, new PointF(40, 45));
        graphics.DrawString("Учебный конспект", font, Brushes.Black, new PointF(40, 300));
        using var stream = new MemoryStream();
        bitmap.Save(stream, format);
        return stream.ToArray();
    }

    private static byte[] CreateScannedPdf(byte[] jpeg, int imageWidth, int imageHeight)
    {
        var pageWidth = imageWidth / 2;
        var pageHeight = imageHeight / 2;
        var pageContent = Encoding.ASCII.GetBytes($"q {pageWidth} 0 0 {pageHeight} 0 0 cm /Im0 Do Q");
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"),
            BuildPdfStream($"<< /Type /XObject /Subtype /Image /Width {imageWidth} /Height {imageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg),
            BuildPdfStream($"<< /Length {pageContent.Length} >>", pageContent)
        };

        using var pdf = new MemoryStream();
        WritePdfAscii(pdf, "%PDF-1.4\n");
        var offsets = new long[objects.Count + 1];
        for (var index = 0; index < objects.Count; index++)
        {
            offsets[index + 1] = pdf.Position;
            WritePdfAscii(pdf, $"{index + 1} 0 obj\n");
            pdf.Write(objects[index]);
            WritePdfAscii(pdf, "\nendobj\n");
        }

        var xrefOffset = pdf.Position;
        WritePdfAscii(pdf, $"xref\n0 {offsets.Length}\n0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
            WritePdfAscii(pdf, $"{offsets[index]:D10} 00000 n \n");
        WritePdfAscii(pdf, $"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] BuildPdfStream(string dictionary, byte[] contents)
    {
        using var stream = new MemoryStream();
        WritePdfAscii(stream, dictionary + "\nstream\n");
        stream.Write(contents);
        WritePdfAscii(stream, "\nendstream");
        return stream.ToArray();
    }

    private static void WritePdfAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "OptiumPlusFileManager.csproj")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the OptiumPlusFileManager source root.");
    }

    private sealed class InvalidPayloadAnalyzer(string payload) : HttpAiFileAnalyzer("test", "model", "test-key")
    {
        protected override Task<string> SendAsync(string prompt, FileAnalysisInput input, CancellationToken cancellationToken) => Task.FromResult(payload);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? Authorization { get; private set; }
        public string? GoogleApiKey { get; private set; }
        public string? AnthropicApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Headers.TryGetValues("x-goog-api-key", out var googleValues)) GoogleApiKey = googleValues.FirstOrDefault();
            if (request.Headers.TryGetValues("x-api-key", out var anthropicValues)) AnthropicApiKey = anthropicValues.FirstOrDefault();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeAiAnalyzer(string providerId, AiClassification classification) : IAiFileAnalyzer
    {
        public string ProviderId { get; } = providerId;
        public bool IsConfigured => true;
        public string Model => "test-model";
        public int CallCount { get; private set; }

        public Task<AiClassification> AnalyzeAsync(FileAnalysisInput input, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(classification);
        }
    }

    private sealed class FakeAiSearchAnalyzer(bool returnMatches = true) : IAiFileSearchAnalyzer
    {
        public string ProviderId => "fake";
        public bool IsConfigured => true;
        public AiFileSearchInput? LastInput { get; private set; }
        public int CallCount { get; private set; }
        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyList<AiFileSearchMatch>> SearchAsync(AiFileSearchInput input, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastInput = input;
            BatchSizes.Add(input.Candidates.Count);
            if (!returnMatches) return Task.FromResult<IReadOnlyList<AiFileSearchMatch>>([]);
            return Task.FromResult<IReadOnlyList<AiFileSearchMatch>>(
                input.Candidates.Take(1).Select(candidate => new AiFileSearchMatch(candidate.Id, 0.98, "Подходит по содержимому")).ToArray());
        }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private TestApp(string root, FileSorterPaths paths, AppSettings settings, FileSorterRuntime runtime)
        {
            Root = root;
            Paths = paths;
            Settings = settings;
            Runtime = runtime;
        }

        public string Root { get; }
        public string Incoming => Settings.WatchFolders[0];
        public FileSorterPaths Paths { get; }
        public AppSettings Settings { get; }
        public FileSorterRuntime Runtime { get; private set; }

        public static async Task<TestApp> CreateAsync(Action<FileSorterPaths>? prepareSortedFiles = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "OptiumPlusFileManager.IntegrationTests", Guid.NewGuid().ToString("N"));
            var incoming = Path.Combine(root, "incoming");
            Directory.CreateDirectory(incoming);
            var paths = new FileSorterPaths(Path.Combine(root, "local"), Path.Combine(root, "sorted"));
            var settings = new AppSettings
            {
                WatchFolders = [incoming],
                SourceFolder = incoming,
                DestinationFolder = paths.Sorted,
                AutomaticSorting = false,
                AiProviderMode = "Off",
                MaxFileSizeMb = 512,
                AutoSortConfidence = 0.85,
                ReviewConfidence = 0.60
            };
            Directory.CreateDirectory(paths.Sorted);
            prepareSortedFiles?.Invoke(paths);
            var runtime = new FileSorterRuntime(settings, paths);
            await runtime.InitializeAsync();
            return new TestApp(root, paths, settings, runtime);
        }

        public string WriteIncoming(string name, string content)
        {
            var path = Path.Combine(Incoming, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public string WriteZip(string name, string entryName, string content)
        {
            var path = Path.Combine(Incoming, name);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), Encoding.UTF8);
            writer.Write(content);
            return path;
        }

        public async Task RestartRuntimeAsync()
        {
            await Runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Runtime = new FileSorterRuntime(Settings, Paths);
            await Runtime.InitializeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
