using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;
using OptiumPlusFileManager.Infrastructure;
using System.IO.Enumeration;
using System.Text;

namespace OptiumPlusFileManager.Worker;

public sealed class FileSorterRuntime : IAsyncDisposable
{
    private readonly IFileRepository _repository;
    private readonly FileProcessingService _processor;
    private readonly FileWatchService _watchService;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly SemaphoreSlim _searchPrivacyLock = new(1, 1);
    private readonly CancellationTokenSource _searchIndexCancellation = new();
    private readonly bool _persistSettings;
    private HashSet<string>? _knownLocalOnlySearchPaths;
    private Task? _searchIndexTask;

    public FileSorterRuntime()
        : this(CreateDefaultStartup())
    {
    }

    private FileSorterRuntime((AppSettings Settings, FileSorterPaths Paths) startup)
        : this(startup.Settings, startup.Paths, persistSettings: true)
    {
    }

    public FileSorterRuntime(AppSettings settings, FileSorterPaths paths, bool persistSettings = false)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _persistSettings = persistSettings;
        _repository = new SqliteFileRepository(Paths);
        _processor = new FileProcessingService(Paths, _repository, new FileFormatDetector(), new SafeContentExtractor(),
            new LocalFileClassifier(), () => Settings);
        _watchService = new FileWatchService(this);
        _watchService.StatusChanged += message => StatusChanged?.Invoke(message);
    }

    private static (AppSettings Settings, FileSorterPaths Paths) CreateDefaultStartup()
    {
        var settings = SettingsStore.Load();
        var configuredRoot = Environment.GetEnvironmentVariable("FILE_SORTER_SORTED_ROOT");
        var sortedRoot = string.IsNullOrWhiteSpace(configuredRoot) ? settings.DestinationFolder : configuredRoot;
        return (settings, new FileSorterPaths(sortedRoot: sortedRoot));
    }

    public AppSettings Settings { get; private set; }
    public FileSorterPaths Paths { get; }
    public event Action<string>? StatusChanged;
    public event Action? FilesChanged;
    public event Action<FileRecord>? FileSorted;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Paths.EnsureCreated();
        NormalizeSettings();
        await _repository.InitializeAsync(cancellationToken);
        await _repository.ReplaceWatchFoldersAsync(Settings.WatchFolders, Settings.WatchSubdirectories, cancellationToken);
        var retry = await _processor.RecoverPendingAsync(cancellationToken);
        var allowedRoots = Settings.WatchFolders.Append(Paths.Inbox).ToArray();
        foreach (var file in retry)
        {
            if (!File.Exists(file.CurrentPath)) continue;
            try
            {
                await _processor.ProcessAsync(file.CurrentPath, file.SourceType, allowedRoots, existing: file,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }
        if (_repository is IFileSearchIndex searchIndex)
            _searchIndexTask = Task.Run(() => RebuildSearchIndexAsync(searchIndex, _searchIndexCancellation.Token),
                CancellationToken.None);
        if (Settings.AutomaticSorting)
            await _watchService.ConfigureAsync(cancellationToken);
    }

    public async Task SetWatchFoldersAsync(IEnumerable<string> folders, CancellationToken cancellationToken = default)
    {
        var normalized = folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0)
            throw new InvalidOperationException("Нужна хотя бы одна входящая папка.");
        foreach (var folder in normalized)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Папка не найдена: {folder}");
            if (FileSafety.HasReparsePoint(folder))
                throw new InvalidOperationException("Нельзя наблюдать за папкой, являющейся символической ссылкой или точкой повторной обработки.");
            if (FileSafety.IsWithin(folder, Paths.DataRoot) || FileSafety.IsWithin(folder, Paths.Sorted) ||
                FileSafety.IsWithin(Paths.Sorted, folder) && folder.Equals(Paths.Sorted, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Папка приложения или результатов сортировки не может быть входящей.");
        }

        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            Settings.WatchFolders = normalized;
            Settings.SourceFolder = normalized[0];
            SaveSettings();
            await _repository.ReplaceWatchFoldersAsync(normalized, Settings.WatchSubdirectories, cancellationToken);
            await _watchService.ConfigureAsync(cancellationToken);
        }
        finally { _settingsLock.Release(); }
    }

    public async Task SetAutomaticSortingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Settings.AutomaticSorting = enabled;
        SaveSettings();
        if (enabled)
            await _watchService.ConfigureAsync(cancellationToken);
        else
            await _watchService.StopAsync();
    }

    public async Task SetRecursiveWatchingAsync(bool recursive, CancellationToken cancellationToken = default)
    {
        Settings.WatchSubdirectories = recursive;
        SaveSettings();
        await _repository.ReplaceWatchFoldersAsync(Settings.WatchFolders, recursive, cancellationToken);
        await _watchService.ConfigureAsync(cancellationToken);
    }

    public async Task SaveWatchExclusionsAsync(IEnumerable<string> excludedPaths, IEnumerable<string> excludedPatterns,
        CancellationToken cancellationToken = default)
    {
        var paths = excludedPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Any(path => !Settings.WatchFolders.Any(root => FileSafety.IsWithin(path, root))))
            throw new InvalidOperationException("Путь исключения должен находиться внутри одной из наблюдаемых папок.");

        var patterns = excludedPatterns.Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (patterns.Any(pattern => pattern.Length > 128 || pattern.Contains('/') || pattern.Contains('\\') || pattern.Any(char.IsControl)))
            throw new ArgumentException("Маска должна содержать только имя файла или glob-шаблон длиной до 128 символов.");

        Settings.ExcludedPaths = paths;
        Settings.ExcludedPatterns = patterns;
        SaveSettings();
        await _watchService.ConfigureAsync(cancellationToken);
    }

    public Task SaveCloudPolicyAsync(IEnumerable<string> allowedFileTypes, IEnumerable<string> localOnlyPaths,
        CancellationToken cancellationToken = default) =>
        SaveCloudPolicyAsync(allowedFileTypes, localOnlyPaths, Settings.AllowAiImageAnalysis, cancellationToken);

    public Task SaveCloudPolicyAsync(IEnumerable<string> allowedFileTypes, IEnumerable<string> localOnlyPaths,
        bool allowAiImageAnalysis, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var types = allowedFileTypes.Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (types.Any(type => !AiFilePolicy.IsKnownType(type)))
            throw new ArgumentException("Список разрешённых форматов содержит неизвестный тип.");

        var paths = localOnlyPaths.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Any(path => !Settings.WatchFolders.Any(root => FileSafety.IsWithin(path, root))))
            throw new InvalidOperationException("Приватная папка должна находиться внутри одной из наблюдаемых папок.");
        if (paths.Any(path => Directory.Exists(path) && FileSafety.HasReparsePoint(path)))
            throw new InvalidOperationException("Приватная папка не может быть символической ссылкой или точкой повторной обработки.");

        Settings.AiAllowedFileTypes = types;
        Settings.AllowAiImageAnalysis = allowAiImageAnalysis;
        Settings.LocalOnlyPaths = paths;
        Interlocked.Exchange(ref _knownLocalOnlySearchPaths, null);
        SaveSettings();
        return Task.CompletedTask;
    }

    public void SetOpenDestinationFolderAfterSort(bool enabled)
    {
        Settings.OpenDestinationFolderAfterSort = enabled;
        SaveSettings();
    }

    public void SetDarkTheme(bool enabled)
    {
        Settings.DarkTheme = enabled;
        SaveSettings();
    }

    public async Task SaveAnalysisSettingsAsync(string providerMode, int maxFileSizeMb,
        CancellationToken cancellationToken = default)
    {
        var allowedModes = new[] { "Off", "Auto", "OpenAI", "Gemini", "Claude", "Compare" };
        if (!allowedModes.Contains(providerMode, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Неизвестный режим анализа.");
        Settings.AiProviderMode = allowedModes.First(mode => mode.Equals(providerMode, StringComparison.OrdinalIgnoreCase));
        Settings.MaxFileSizeMb = Math.Clamp(maxFileSizeMb, 1, 2048);
        SaveSettings();
        await _repository.ReplaceProviderSettingsAsync(new[]
        {
            ("openai", Settings.OpenAiModel, Settings.AiProviderMode is "Auto" or "Compare" or "OpenAI"),
            ("gemini", Settings.GeminiModel, Settings.AiProviderMode is "Auto" or "Compare" or "Gemini"),
            ("claude", Settings.ClaudeModel, Settings.AiProviderMode is "Auto" or "Compare" or "Claude")
        }, cancellationToken);
    }

    public async Task SaveAnalysisSettingsAsync(string providerMode, int maxFileSizeMb, int monthlyAiRequestLimit,
        CancellationToken cancellationToken = default)
    {
        Settings.MonthlyAiRequestLimit = Math.Clamp(monthlyAiRequestLimit, 1, 100_000);
        await SaveAnalysisSettingsAsync(providerMode, maxFileSizeMb, cancellationToken);
    }

    public async Task SaveAnalysisSettingsAsync(string providerMode, int maxFileSizeMb, string openAiModel,
        string geminiModel, string claudeModel, int monthlyAiRequestLimit, CancellationToken cancellationToken = default)
    {
        Settings.OpenAiModel = SafeModel(openAiModel, "gpt-5.6-terra");
        Settings.GeminiModel = SafeModel(geminiModel, "gemini-3.6-flash");
        Settings.ClaudeModel = SafeModel(claudeModel, "claude-sonnet-5-5");
        await SaveAnalysisSettingsAsync(providerMode, maxFileSizeMb, monthlyAiRequestLimit, cancellationToken);
    }

    public void SetStartWithWindows(bool enabled)
    {
        AppStartupManager.SetEnabled(enabled);
        Settings.StartWithWindows = enabled;
        SaveSettings();
    }

    public async Task<IReadOnlyList<ProcessingOutcome>> SortNowAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ProcessingOutcome>();
        var roots = Settings.WatchFolders.ToArray();
        foreach (var root in roots)
        {
            var search = Settings.WatchSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var path in Directory.EnumerateFiles(root, "*", search).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FileTypeClassifier.IsTemporary(path) || IsExcludedPath(path)) continue;
                outcomes.Add(await ProcessWatchedFileAsync(path, cancellationToken));
            }
        }
        FilesChanged?.Invoke();
        return outcomes;
    }

    public async Task<ProcessingOutcome> ProcessWatchedFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var allowed = Settings.WatchFolders.Append(Paths.Inbox).ToArray();
        var outcome = await _processor.ProcessAsync(path, "watch", allowed, cancellationToken: cancellationToken);
        NotifyFileSorted(outcome.File);
        FilesChanged?.Invoke();
        return outcome;
    }

    public async Task<FileRecord> IngestFileAsync(string sourcePath, string sourceType = "manual", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Выбранный файл не найден.", sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (FileSafety.HasReparsePoint(fullSourcePath))
            throw new UnauthorizedAccessException("Символические ссылки и точки повторной обработки нельзя добавлять в шлюз.");
        var maxBytes = Settings.MaxFileSizeMb * 1024L * 1024L;
        var info = new FileInfo(fullSourcePath);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"Размер файла превышает лимит {Settings.MaxFileSizeMb} МБ.");
        if (!await FileSafety.WaitUntilReadyAsync(fullSourcePath, cancellationToken))
            throw new IOException("Файл ещё записывается или занят другим приложением.");
        info.Refresh();
        var initialLength = info.Length;
        var initialWriteTime = info.LastWriteTimeUtc;
        Directory.CreateDirectory(Paths.Inbox);
        var destination = GetUniquePath(Paths.Inbox, FileSafety.SanitizeFileName(info.Name));
        try
        {
            await using (var input = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes)
                        throw new InvalidOperationException($"Размер файла превышает лимит {Settings.MaxFileSizeMb} МБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            info.Refresh();
            if (!File.Exists(fullSourcePath) || info.Length != initialLength || info.LastWriteTimeUtc != initialWriteTime ||
                !string.Equals(await FileSafety.ComputeSha256Async(fullSourcePath, cancellationToken),
                    await FileSafety.ComputeSha256Async(destination, cancellationToken), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исходный файл изменился во время копирования; копия не принята.");
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
        var outcome = await _processor.ProcessAsync(destination, sourceType, [Paths.Inbox], cancellationToken: cancellationToken);
        NotifyFileSorted(outcome.File);
        FilesChanged?.Invoke();
        return outcome.File;
    }

    public bool IsAllowedLocalInput(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Settings.WatchFolders.Any(root => FileSafety.IsWithin(fullPath, root)) &&
               !IsExcludedPath(fullPath) && !FileSafety.HasReparsePoint(fullPath);
    }

    public async Task<FileRecord> IngestStreamAsync(Stream input, string fileName, string sourceType,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Paths.Inbox);
        var safeName = FileSafety.SanitizeFileName(fileName);
        var destination = GetUniquePath(Paths.Inbox, safeName);
        var maxBytes = Settings.MaxFileSizeMb * 1024L * 1024L;
        try
        {
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"Загрузка превышает лимит {Settings.MaxFileSizeMb} МБ.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }

        var outcome = await _processor.ProcessAsync(destination, sourceType, [Paths.Inbox], cancellationToken: cancellationToken);
        FilesChanged?.Invoke();
        return outcome.File;
    }

    public async Task<FileRecord> IngestTextAsync(string fileName, string content, string sourceType, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(content) > 1024 * 1024)
            throw new InvalidOperationException("Текстовый файл не может превышать 1 МБ.");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await IngestStreamAsync(stream, fileName, sourceType, cancellationToken);
    }

    public Task<FileRecord?> GetFileAsync(string id, CancellationToken cancellationToken = default) => _repository.GetAsync(id, cancellationToken);
    public Task<IReadOnlyList<FileRecord>> ListFilesAsync(string? status = null, int take = 100, CancellationToken cancellationToken = default,
        int skip = 0, string? categoryId = null, string? sourceType = null, DateTime? createdAfterUtc = null) =>
        _repository.ListAsync(status, take, cancellationToken, skip, categoryId, sourceType, createdAfterUtc);
    public Task<IReadOnlyList<FileEvent>> GetEventsAsync(string id, CancellationToken cancellationToken = default) =>
        _repository.GetEventsAsync(id, cancellationToken);
    public Task<DashboardStats> GetStatsAsync(CancellationToken cancellationToken = default) => _repository.GetStatsAsync(cancellationToken);
    public Task<int> GetAiRequestsUsedThisMonthAsync(CancellationToken cancellationToken = default) =>
        _repository.GetAiRequestsUsedThisMonthAsync(cancellationToken);

    public async Task<FileSearchResult> SearchFilesAsync(string query, CancellationToken cancellationToken = default)
    {
        var localOnlyPaths = await GetKnownLocalOnlySearchPathsAsync(cancellationToken);
        var analyzer = new CompositeAiFileSearchAnalyzer(Settings, (count, token) =>
            _repository.TryReserveAiRequestsAsync(count, Settings.MonthlyAiRequestLimit, token));
        var search = new FileSearchService(Paths, Settings, aiAnalyzer: analyzer,
            knownLocalOnlyCurrentPaths: localOnlyPaths, searchIndex: _repository as IFileSearchIndex);
        return await search.SearchAsync(query, cancellationToken);
    }

    private async Task RebuildSearchIndexAsync(IFileSearchIndex searchIndex, CancellationToken cancellationToken)
    {
        try
        {
            var detector = new FileFormatDetector();
            var extractor = new SafeContentExtractor();
            using var concurrency = new SemaphoreSlim(2, 2);
            var indexedCount = 0;
            for (var skip = 0; ; skip += 1000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = await _repository.ListAsync(nameof(FileStatus.Completed), 1000, cancellationToken, skip);
                var tasks = files.Select(async file =>
                {
                    await concurrency.WaitAsync(cancellationToken);
                    try
                    {
                        await IndexExistingFileAsync(file, searchIndex, detector, extractor, cancellationToken);
                        if (Interlocked.Increment(ref indexedCount) % 100 == 0)
                            StatusChanged?.Invoke($"Подготовлен поиск по файлам: {indexedCount}.");
                    }
                    finally { concurrency.Release(); }
                });
                await Task.WhenAll(tasks);
                if (files.Count < 1000) break;
            }

            await searchIndex.SetSearchIndexReadyAsync(true, cancellationToken);
            StatusChanged?.Invoke($"Локальный поисковый индекс готов. Проверено файлов: {indexedCount}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            StatusChanged?.Invoke("Индекс поиска пока не готов; используется обычный локальный поиск.");
        }
    }

    private async Task IndexExistingFileAsync(FileRecord file, IFileSearchIndex searchIndex,
        FileFormatDetector detector, SafeContentExtractor extractor, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(file.CurrentPath);
        if (!FileSafety.IsWithin(path, Paths.Sorted) || FileSafety.HasReparsePoint(path) || !File.Exists(path))
        {
            await searchIndex.RemoveSearchDocumentAsync(file.Id, cancellationToken);
            return;
        }

        var info = new FileInfo(path);
        var current = await searchIndex.GetSearchIndexMetadataAsync(file.Id, cancellationToken);
        if (current is not null && current.SizeBytes == info.Length && current.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            return;

        try
        {
            var originalLength = info.Length;
            var originalLastWriteTicks = info.LastWriteTimeUtc.Ticks;
            var format = await detector.DetectAsync(path, cancellationToken);
            var content = await extractor.ExtractAsync(path, format.FileType, cancellationToken);
            info.Refresh();
            if (!info.Exists || info.Length != originalLength || info.LastWriteTimeUtc.Ticks != originalLastWriteTicks)
                return;

            await searchIndex.UpsertSearchDocumentAsync(new SearchIndexDocument(file.Id, path, Path.GetFileName(path),
                format.FileType, content.Text, FileSearchService.PrepareSearchTerms(Path.GetFileName(path)),
                FileSearchService.PrepareSearchTerms(content.Text), info.Length, info.LastWriteTimeUtc.Ticks), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // A single inaccessible or damaged file should not prevent indexing the rest of the library.
        }
    }

    private async Task<HashSet<string>> GetKnownLocalOnlySearchPathsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _knownLocalOnlySearchPaths) is { } cached)
            return cached;

        await _searchPrivacyLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _knownLocalOnlySearchPaths) is { } refreshed)
                return refreshed;

            var privateRoots = Settings.LocalOnlyPaths.ToArray();
            var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (privateRoots.Length > 0)
            {
                for (var skip = 0; ; skip += 1000)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var completed = await _repository.ListAsync(nameof(FileStatus.Completed), 1000, cancellationToken, skip);
                    foreach (var record in completed)
                    {
                        if (!FileSafety.IsWithin(record.CurrentPath, Paths.Sorted)) continue;
                        if (privateRoots.Any(privateRoot => FileSafety.IsWithin(record.OriginalPath, privateRoot)))
                            currentPaths.Add(Path.GetFullPath(record.CurrentPath));
                    }
                    if (completed.Count < 1000) break;
                }
            }

            Interlocked.CompareExchange(ref _knownLocalOnlySearchPaths, currentPaths, null);
            return Volatile.Read(ref _knownLocalOnlySearchPaths) ?? currentPaths;
        }
        finally { _searchPrivacyLock.Release(); }
    }

    public async Task<FileRecord> ApproveAsync(string id, string categoryId, string subcategoryId, CancellationToken cancellationToken = default)
        => await ApproveAsync(id, categoryId, subcategoryId, rememberFileNameRule: false, cancellationToken);

    public async Task<FileRecord> ApproveAsync(string id, string categoryId, string subcategoryId,
        bool rememberFileNameRule, CancellationToken cancellationToken = default)
    {
        var result = await _processor.ApproveAsync(id, categoryId, subcategoryId, rememberFileNameRule, cancellationToken);
        NotifyFileSorted(result);
        FilesChanged?.Invoke();
        return result;
    }

    public async Task<FileRecord> SkipAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await _processor.SkipAsync(id, cancellationToken);
        FilesChanged?.Invoke();
        return result;
    }

    public async Task<FileRecord> ReanalyzeAsync(string id, CancellationToken cancellationToken = default)
    {
        var roots = Settings.WatchFolders.Append(Paths.Inbox).Append(Paths.Review).Append(Paths.Sorted).Append(Paths.Errors).ToArray();
        var result = await _processor.ReanalyzeAsync(id, roots, cancellationToken);
        NotifyFileSorted(result);
        FilesChanged?.Invoke();
        return result;
    }

    public async Task<FileRecord> RevertAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await _processor.RevertAsync(id, Settings.WatchFolders.Append(Paths.Inbox).ToArray(), cancellationToken);
        FilesChanged?.Invoke();
        return result;
    }

    public IReadOnlyList<ProviderHealth> GetProviderHealth()
    {
        var providers = new IAiFileAnalyzer[]
        {
            new OpenAiFileAnalyzer(Settings.OpenAiModel,
                Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            new GeminiFileAnalyzer(Settings.GeminiModel,
                Environment.GetEnvironmentVariable("GEMINI_API_KEY")),
            new ClaudeFileAnalyzer(Settings.ClaudeModel,
                Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        };
        return providers.Select(provider => new ProviderHealth(provider.ProviderId, provider.IsConfigured,
            provider.IsConfigured ? "Ключ настроен; сеть не проверена" : "Нужен ключ и имя модели", provider.Model)).ToArray();
    }

    public async Task<Stream> OpenAllowedFileAsync(string id, CancellationToken cancellationToken = default)
    {
        var file = await _repository.GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Запись не найдена.");
        if (file.Status is not (nameof(FileStatus.Completed) or nameof(FileStatus.NeedsReview)) || !File.Exists(file.CurrentPath))
            throw new UnauthorizedAccessException("Скачать можно только готовый файл или файл очереди проверки.");
        if (!FileSafety.IsWithin(file.CurrentPath, Paths.Sorted) && !FileSafety.IsWithin(file.CurrentPath, Paths.Review))
            throw new UnauthorizedAccessException("Файл находится вне разрешённых областей загрузки.");
        if (FileSafety.HasReparsePoint(file.CurrentPath))
            throw new UnauthorizedAccessException("Файл связан с символической ссылкой.");
        if (await FileSafety.ComputeSha256Async(file.CurrentPath, cancellationToken) != file.Sha256)
            throw new InvalidOperationException("Хеш файла изменился после анализа.");
        return new FileStream(file.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public bool IsExcludedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var reserved = new[] { Paths.DataRoot, Paths.Sorted, Paths.Review, Paths.Quarantine, Paths.Errors, Paths.Processing, Paths.Inbox };
        if (reserved.Any(root => FileSafety.IsWithin(fullPath, root))) return true;
        if (Settings.ExcludedPaths.Any(root => FileSafety.IsWithin(fullPath, root))) return true;
        return Settings.WatchFolders.Any(root => FileSafety.IsWithin(fullPath, root)) &&
               Settings.ExcludedPatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(fullPath), ignoreCase: true));
    }

    private void NormalizeSettings()
    {
        Settings.ExcludedPaths ??= [];
        Settings.ExcludedPatterns ??= [];
        Settings.LocalOnlyPaths ??= [];
        Settings.AiAllowedFileTypes ??= ["text", "pdf", "docx", "xlsx", "pptx"];
        Settings.AiAllowedFileTypes = Settings.AiAllowedFileTypes
            .Where(AiFilePolicy.IsKnownType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Settings.WatchFolders.Count == 0 && !string.IsNullOrWhiteSpace(Settings.SourceFolder))
            Settings.WatchFolders = [Settings.SourceFolder];
        Settings.WatchFolders = Settings.WatchFolders.Where(Directory.Exists).Select(Path.GetFullPath)
            .Where(folder => !FileSafety.HasReparsePoint(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Settings.WatchFolders.Count == 0)
        {
            Settings.WatchFolders = [Settings.SourceFolder];
            Directory.CreateDirectory(Settings.SourceFolder);
        }
        Settings.SourceFolder = Settings.WatchFolders[0];
        Settings.DestinationFolder = Paths.Sorted;
        Settings.MonthlyAiRequestLimit = Math.Clamp(Settings.MonthlyAiRequestLimit, 1, 100_000);
        if (string.Equals(Settings.OpenAiModel, "gpt-5-mini", StringComparison.OrdinalIgnoreCase))
            Settings.OpenAiModel = "gpt-5.6-terra";
        if (string.Equals(Settings.GeminiModel, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
            Settings.GeminiModel = "gemini-3.6-flash";
        if (string.Equals(Settings.ClaudeModel, "claude-sonnet-4-6", StringComparison.OrdinalIgnoreCase))
            Settings.ClaudeModel = "claude-sonnet-5-5";
        SaveSettings();
    }

    private void NotifyFileSorted(FileRecord file)
    {
        if (file.Status != nameof(FileStatus.Completed) || file.SourceType is not ("watch" or "manual") ||
            !FileSafety.IsWithin(file.CurrentPath, Paths.Sorted))
            return;
        Interlocked.Exchange(ref _knownLocalOnlySearchPaths, null);
        FileSorted?.Invoke(file);
    }

    private void SaveSettings()
    {
        Settings.SourceFolder = Settings.WatchFolders.FirstOrDefault() ?? Settings.SourceFolder;
        Settings.DestinationFolder = Paths.Sorted;
        if (_persistSettings)
            SettingsStore.Save(Settings);
    }

    private static string GetUniquePath(string folder, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; ; index++)
        {
            var candidate = Path.Combine(folder, index == 0 ? fileName : $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SafeModel(string value, string fallback)
    {
        var clean = value.Trim();
        return clean.Length is > 0 and <= 100 && clean.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? clean : fallback;
    }

    public async ValueTask DisposeAsync()
    {
        _searchIndexCancellation.Cancel();
        if (_searchIndexTask is not null)
            await _searchIndexTask;
        await _watchService.DisposeAsync();
        _searchIndexCancellation.Dispose();
        _settingsLock.Dispose();
        _searchPrivacyLock.Dispose();
    }
}
