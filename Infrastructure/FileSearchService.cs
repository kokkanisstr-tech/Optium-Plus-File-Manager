using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;

namespace OptiumPlusFileManager.Infrastructure;

public sealed class FileSearchService
{
    private const int MaximumAiCandidates = 16;
    private const int MaximumAiImages = 2;
    private const int SearchBatchSize = 16;
    private const int MaximumParallelReads = 4;
    private const int MaximumCachedDocuments = 4_096;
    private const long MaximumSearchCacheWeight = 48L * 1024 * 1024;
    private readonly FileSorterPaths _paths;
    private readonly AppSettings _settings;
    private readonly FileFormatDetector _formatDetector;
    private readonly IFileContentExtractor _extractor;
    private readonly IAiFileSearchAnalyzer? _aiAnalyzer;
    private readonly Func<int, CancellationToken, Task<bool>> _tryReserveAiRequests;
    private readonly IFileSearchIndex? _searchIndex;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<SearchCacheEntry>> DocumentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<SearchCacheEntry> DocumentCacheLru = new();
    private static long _searchCacheWeight;
    private HashSet<string> _knownLocalOnlyCurrentPaths;

    public FileSearchService(FileSorterPaths paths, AppSettings settings, FileFormatDetector? formatDetector = null,
        IFileContentExtractor? extractor = null, IAiFileSearchAnalyzer? aiAnalyzer = null,
        Func<int, CancellationToken, Task<bool>>? tryReserveAiRequests = null,
        IEnumerable<string>? knownLocalOnlyCurrentPaths = null, IFileSearchIndex? searchIndex = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _formatDetector = formatDetector ?? new FileFormatDetector();
        _extractor = extractor ?? new SafeContentExtractor();
        _aiAnalyzer = aiAnalyzer;
        _tryReserveAiRequests = tryReserveAiRequests ?? ((_, _) => Task.FromResult(true));
        _searchIndex = searchIndex;
        _knownLocalOnlyCurrentPaths = NormalizeLocalOnlyPaths(knownLocalOnlyCurrentPaths);
    }

    public void UpdateKnownLocalOnlyCurrentPaths(IEnumerable<string> paths) =>
        Volatile.Write(ref _knownLocalOnlyCurrentPaths, NormalizeLocalOnlyPaths(paths));

    public async Task<FileSearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Введите слова для поиска.", nameof(query));
        query = query.Trim();
        if (query.Length > 500)
            throw new ArgumentException("Запрос поиска не должен превышать 500 символов.", nameof(query));

        var parsedQuery = ParsedSearchQuery.Create(query);
        if (_searchIndex is not null && parsedQuery.Terms.Count > 0)
        {
            try
            {
                if (await _searchIndex.IsSearchIndexReadyAsync(cancellationToken))
                    return await SearchIndexedAsync(query, parsedQuery, cancellationToken);
            }
            catch (SqliteException)
            {
                // A damaged or unavailable search index must not make local search unusable.
            }
        }

        var targetFolder = parsedQuery.LanguageId is null ? null : GetLanguageFolder(parsedQuery.LanguageId);
        var scanned = 0;
        var usedAi = false;
        var aiProvider = (string?)null;
        var aiFailure = false;

        if (targetFolder is not null && Directory.Exists(targetFolder) && IsSafeRoot(targetFolder))
        {
            var preferredFiles = EnumerateFiles(targetFolder, targetFolder, cancellationToken);
            var preferred = await SearchDocumentsAsync(query, parsedQuery, preferredFiles, cancellationToken, () => scanned++);
            usedAi |= preferred.UsedAi;
            aiProvider ??= preferred.AiProviderId;
            aiFailure |= preferred.AiFailed;
            if (preferred.Hits.Count > 0)
            {
                return new(preferred.Hits, scanned, usedAi, aiProvider, parsedQuery.LanguageName, false,
                    BuildMessage(preferred.Hits.Count, scanned, parsedQuery.LanguageName, usedAi, aiFailure, fellBack: false));
            }

            var remainingFiles = EnumerateFiles(_paths.Sorted, _paths.Sorted, cancellationToken)
                .Where(path => !FileSafety.IsWithin(path, targetFolder));
            var remaining = await SearchDocumentsAsync(query, parsedQuery, remainingFiles, cancellationToken, () => scanned++);
            usedAi |= remaining.UsedAi;
            aiProvider ??= remaining.AiProviderId;
            aiFailure |= remaining.AiFailed;
            return new(remaining.Hits, scanned, usedAi, aiProvider, parsedQuery.LanguageName, true,
                BuildMessage(remaining.Hits.Count, scanned, parsedQuery.LanguageName, usedAi, aiFailure, fellBack: true));
        }

        var allFiles = EnumerateFiles(_paths.Sorted, _paths.Sorted, cancellationToken);
        var allResults = await SearchDocumentsAsync(query, parsedQuery, allFiles, cancellationToken, () => scanned++);
        usedAi |= allResults.UsedAi;
        aiProvider ??= allResults.AiProviderId;
        aiFailure |= allResults.AiFailed;
        return new(allResults.Hits, scanned, usedAi, aiProvider, parsedQuery.LanguageName, false,
            BuildMessage(allResults.Hits.Count, scanned, parsedQuery.LanguageName, usedAi, aiFailure, fellBack: false));
    }

    private async Task<FileSearchResult> SearchIndexedAsync(string query, ParsedSearchQuery parsedQuery,
        CancellationToken cancellationToken)
    {
        var matchExpression = string.Join(" OR ", parsedQuery.Terms.Select(term =>
            $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
        var indexed = await _searchIndex!.SearchIndexAsync(matchExpression, parsedQuery.Terms, 1000, cancellationToken);
        var documents = new List<SearchDocument>(indexed.Count);
        foreach (var item in indexed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSafety.IsWithin(item.FilePath, _paths.Sorted) || FileSafety.HasReparsePoint(item.FilePath))
            {
                await _searchIndex.RemoveSearchDocumentAsync(item.FileId, cancellationToken);
                continue;
            }

            var info = new FileInfo(item.FilePath);
            if (!info.Exists)
            {
                await _searchIndex.RemoveSearchDocumentAsync(item.FileId, cancellationToken);
                continue;
            }

            var length = info.Length;
            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            var current = item;
            if (length != item.SizeBytes || lastWriteTicks != item.LastWriteTicks)
            {
                var refreshed = await ReadDocumentAsync(item.FilePath, cancellationToken);
                if (refreshed is null) continue;

                info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != lastWriteTicks)
                    continue;
                current = new SearchIndexDocument(item.FileId, refreshed.Path, refreshed.Name, refreshed.FileType,
                    refreshed.Content, PrepareSearchTerms(refreshed.Name), PrepareSearchTerms(refreshed.Content),
                    length, lastWriteTicks, string.Join(' ', refreshed.NameTokens),
                    string.Join(' ', refreshed.ContentTokens));
                await _searchIndex.UpsertSearchDocumentAsync(current, cancellationToken);
            }

            var path = Path.GetFullPath(current.FilePath);
            var nameTokens = current.MatchedNameTerms.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var contentTokens = current.MatchedContentTerms.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            documents.Add(new SearchDocument(path, current.FileName, current.FileType, GetCategoryPath(path), current.Content,
                nameTokens, contentTokens));
        }

        var targetFolder = parsedQuery.LanguageId is null ? null : GetLanguageFolder(parsedQuery.LanguageId);
        if (targetFolder is not null && Directory.Exists(targetFolder) && IsSafeRoot(targetFolder))
        {
            var preferred = documents.Where(document => FileSafety.IsWithin(document.Path, targetFolder)).ToArray();
            var preferredResult = await SearchIndexedDocumentsAsync(query, parsedQuery, preferred, cancellationToken);
            if (preferredResult.Hits.Count > 0)
            {
                return new(preferredResult.Hits, preferred.Length, preferredResult.UsedAi, preferredResult.AiProviderId,
                    parsedQuery.LanguageName, false,
                    BuildMessage(preferredResult.Hits.Count, preferred.Length, parsedQuery.LanguageName,
                        preferredResult.UsedAi, preferredResult.AiFailed, fellBack: false, indexed: true));
            }

            var remaining = documents.Where(document => !FileSafety.IsWithin(document.Path, targetFolder)).ToArray();
            var remainingResult = await SearchIndexedDocumentsAsync(query, parsedQuery, remaining, cancellationToken);
            var scanned = preferred.Length + remaining.Length;
            return new(remainingResult.Hits, scanned, remainingResult.UsedAi, remainingResult.AiProviderId,
                parsedQuery.LanguageName, true,
                BuildMessage(remainingResult.Hits.Count, scanned, parsedQuery.LanguageName, remainingResult.UsedAi,
                    remainingResult.AiFailed, fellBack: true, indexed: true));
        }

        var allResults = await SearchIndexedDocumentsAsync(query, parsedQuery, documents, cancellationToken);
        return new(allResults.Hits, documents.Count, allResults.UsedAi, allResults.AiProviderId, null, false,
            BuildMessage(allResults.Hits.Count, documents.Count, null, allResults.UsedAi, allResults.AiFailed,
                fellBack: false, indexed: true));
    }

    private async Task<SearchBatchResult> SearchIndexedDocumentsAsync(string query, ParsedSearchQuery parsedQuery,
        IReadOnlyList<SearchDocument> documents, CancellationToken cancellationToken)
    {
        var hits = new Dictionary<string, FileSearchHit>(StringComparer.OrdinalIgnoreCase);
        var scored = documents.Select(document => new RankedDocument(document, Score(document, parsedQuery.Terms)))
            .ToArray();
        foreach (var item in scored.Where(item => item.Score > 0))
        {
            var document = item.Document;
            AddLocalHit(hits, new FileSearchHit(document.Path, document.Name, document.FileType, document.CategoryPath,
                item.Score, document.ReasonFor(parsedQuery.Terms), false));
        }

        var analyzer = _aiAnalyzer;
        var aiActive = analyzer?.IsConfigured == true && !FileSafety.ContainsSecrets("search-query", query) && !LooksLikeAbsolutePath(query);
        if (aiActive && scored.Length > 0)
        {
            var aiCandidates = scored.OrderByDescending(item => item.Score)
                .ThenBy(item => item.Document.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumAiCandidates).ToArray();
            var ai = await AnalyzeAiBatchAsync(query, aiCandidates, hits, cancellationToken);
            return new(hits.Values.OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.FileName, StringComparer.OrdinalIgnoreCase).Take(100).ToArray(), ai.UsedAi,
                ai.AiProviderId, ai.AiFailed);
        }

        return new(hits.Values.OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.FileName, StringComparer.OrdinalIgnoreCase).Take(100).ToArray(), false, null, false);
    }

    public static string PrepareSearchTerms(string value) => string.Join(' ', ParsedSearchQuery.Tokenize(value));

    private async Task<SearchBatchResult> SearchDocumentsAsync(string query, ParsedSearchQuery parsedQuery,
        IEnumerable<string> paths, CancellationToken cancellationToken, Action scanned)
    {
        var hits = new Dictionary<string, FileSearchHit>(StringComparer.OrdinalIgnoreCase);
        var aiCandidates = new List<RankedDocument>(MaximumAiCandidates);
        var analyzer = _aiAnalyzer;
        var aiActive = analyzer?.IsConfigured == true &&
                       !FileSafety.ContainsSecrets("search-query", query) && !LooksLikeAbsolutePath(query);
        var usedAi = false;
        var aiFailed = false;
        string? aiProviderId = null;
        var pathBatch = new List<string>(SearchBatchSize);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathBatch.Add(path);
            if (pathBatch.Count < SearchBatchSize) continue;
            await ProcessPathBatchAsync(pathBatch);
            pathBatch.Clear();
        }

        if (pathBatch.Count > 0)
            await ProcessPathBatchAsync(pathBatch);

        if (aiActive && aiCandidates.Count > 0)
        {
            var batch = await AnalyzeAiBatchAsync(query, aiCandidates, hits, cancellationToken);
            usedAi |= batch.UsedAi;
            aiProviderId ??= batch.AiProviderId;
            aiFailed |= batch.AiFailed;
        }

        return new(hits.Values.OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.FileName, StringComparer.OrdinalIgnoreCase).Take(100).ToArray(), usedAi,
            usedAi ? aiProviderId : null, aiFailed);

        async Task ProcessPathBatchAsync(IReadOnlyList<string> batchPaths)
        {
            foreach (var _ in batchPaths) scanned();
            var documents = await ReadDocumentBatchAsync(batchPaths, cancellationToken);
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document is null) continue;

                var score = Score(document, parsedQuery.Terms);
                if (score > 0)
                {
                    var hit = new FileSearchHit(document.Path, document.Name, document.FileType, document.CategoryPath,
                        score, document.ReasonFor(parsedQuery.Terms), false);
                    AddLocalHit(hits, hit);
                }

                if (!aiActive || IsLocalOnly(document.Path) ||
                    !AiFilePolicy.IsAllowed(_settings, document.Path, document.FileType) ||
                    FileSafety.ContainsSecrets(document.Name, document.Content))
                    continue;

                var compact = document with { Content = Clip(document.Content, 4_000) };
                aiCandidates.Add(new(compact, score));
                if (aiCandidates.Count < MaximumAiCandidates) continue;

                var batch = await AnalyzeAiBatchAsync(query, aiCandidates, hits, cancellationToken);
                usedAi |= batch.UsedAi;
                aiProviderId ??= batch.AiProviderId;
                aiFailed |= batch.AiFailed;
                aiCandidates.Clear();
                if (batch.AiFailed || batch.FoundMatch) aiActive = false;
            }
        }
    }

    private async Task<IReadOnlyList<SearchDocument?>> ReadDocumentBatchAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(Math.Min(MaximumParallelReads, paths.Count));
        var reads = paths.Select(async path =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try { return await ReadDocumentAsync(path, cancellationToken); }
            finally { concurrency.Release(); }
        });
        return await Task.WhenAll(reads);
    }

    private async Task<SearchAiBatchResult> AnalyzeAiBatchAsync(string query, IReadOnlyList<RankedDocument> ranked,
        Dictionary<string, FileSearchHit> hits, CancellationToken cancellationToken)
    {
        var analyzer = _aiAnalyzer;
        if (analyzer is null) return new(false, null, false, false);
        var candidates = await BuildAiCandidatesAsync(ranked, cancellationToken);
        if (candidates.Count == 0) return new(false, null, false, false);
        try
        {
            if (!await _tryReserveAiRequests(1, cancellationToken))
                return new(false, null, true, false);
            var aiMatches = await analyzer.SearchAsync(new AiFileSearchInput(query,
                candidates.Select(candidate => candidate.Candidate).ToArray()), cancellationToken);
            var byId = candidates.ToDictionary(candidate => candidate.Candidate.Id, StringComparer.Ordinal);
            var foundMatch = false;
            foreach (var match in aiMatches.Where(match => match.Score >= 0.50))
            {
                if (!byId.TryGetValue(match.CandidateId, out var candidate)) continue;
                var document = candidate.Document;
                var reason = string.IsNullOrWhiteSpace(match.Reason) ? "Подходит по содержимому" : match.Reason;
                hits[document.Path] = new(document.Path, document.Name, document.FileType, document.CategoryPath,
                    Math.Max(match.Score, hits.TryGetValue(document.Path, out var previous) ? previous.Score : 0),
                    $"ИИ: {reason}", true);
                foundMatch = true;
            }
            return new(true, analyzer.ProviderId, false, foundMatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new(false, null, true, false);
        }
    }

    private async Task<IReadOnlyList<AiCandidateReference>> BuildAiCandidatesAsync(IReadOnlyList<RankedDocument> ranked,
        CancellationToken cancellationToken)
    {
        var candidates = new List<AiCandidateReference>(MaximumAiCandidates);
        var imageCount = 0;
        foreach (var item in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Count >= MaximumAiCandidates) break;
            var document = item.Document;
            if (IsLocalOnly(document.Path) ||
                !AiFilePolicy.IsAllowed(_settings, document.Path, document.FileType) ||
                FileSafety.ContainsSecrets(document.Name, document.Content))
                continue;

            byte[]? image = null;
            string? imageMime = null;
            var fileInfo = new FileInfo(document.Path);
            if (imageCount < MaximumAiImages && fileInfo.Exists && fileInfo.Length <= AiFilePolicy.MaximumVisionImageBytes &&
                AiFilePolicy.CanSendVisionImage(_settings, document.Path, document.FileType) &&
                AiFilePolicy.GetVisionMimeType(document.FileType) is { } allowedMime)
            {
                try
                {
                    image = await File.ReadAllBytesAsync(document.Path, cancellationToken);
                    if (image.Length > AiFilePolicy.MaximumVisionImageBytes)
                        image = null;
                    else
                    {
                        imageMime = allowedMime;
                        imageCount++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { image = null; }
            }

            var candidate = new AiFileSearchCandidate($"candidate-{candidates.Count + 1}", document.Name,
                document.CategoryPath, document.FileType, Clip(document.Content, 4_000), imageMime, image);
            candidates.Add(new(candidate, document));
        }
        return candidates;
    }

    private async Task<SearchDocument?> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || !FileSafety.IsWithin(path, _paths.Sorted) || FileSafety.HasReparsePoint(path))
                return null;
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;
            var length = info.Length;
            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            if (TryGetCachedDocument(fullPath, length, lastWriteTicks, out var cached))
                return cached;

            var format = await _formatDetector.DetectAsync(path, cancellationToken);
            var content = await _extractor.ExtractAsync(path, format.FileType, cancellationToken);
            info.Refresh();
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != lastWriteTicks)
                return null;

            var name = Path.GetFileName(fullPath);
            var nameTokens = ParsedSearchQuery.Tokenize(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var contentTokens = ParsedSearchQuery.Tokenize(content.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var document = new SearchDocument(fullPath, name, format.FileType, GetCategoryPath(fullPath), content.Text,
                nameTokens, contentTokens);
            StoreCachedDocument(fullPath, length, lastWriteTicks, document);
            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // A locked, damaged, or disappearing item does not stop the rest of the search.
            return null;
        }
    }

    private bool IsLocalOnly(string path) =>
        Volatile.Read(ref _knownLocalOnlyCurrentPaths).Contains(Path.GetFullPath(path)) ||
        _settings.LocalOnlyPaths.Any(privateRoot => FileSafety.IsWithin(path, privateRoot));

    private static bool TryGetCachedDocument(string path, long length, long lastWriteTicks, out SearchDocument? document)
    {
        lock (CacheLock)
        {
            if (DocumentCache.TryGetValue(path, out var node))
            {
                if (node.Value.Length == length && node.Value.LastWriteTicks == lastWriteTicks)
                {
                    DocumentCacheLru.Remove(node);
                    DocumentCacheLru.AddLast(node);
                    document = node.Value.Document;
                    return true;
                }

                RemoveCacheEntry(node);
            }
        }

        document = null;
        return false;
    }

    private static void StoreCachedDocument(string path, long length, long lastWriteTicks, SearchDocument document)
    {
        var estimatedWeight = EstimateCacheWeight(document);
        if (estimatedWeight > MaximumSearchCacheWeight) return;

        lock (CacheLock)
        {
            if (DocumentCache.TryGetValue(path, out var existing))
                RemoveCacheEntry(existing);

            var entry = new SearchCacheEntry(path, length, lastWriteTicks, document, estimatedWeight);
            var node = DocumentCacheLru.AddLast(entry);
            DocumentCache[path] = node;
            _searchCacheWeight += estimatedWeight;

            while (DocumentCache.Count > MaximumCachedDocuments || _searchCacheWeight > MaximumSearchCacheWeight)
            {
                if (DocumentCacheLru.First is not { } oldest) break;
                RemoveCacheEntry(oldest);
            }
        }
    }

    private static void RemoveCacheEntry(LinkedListNode<SearchCacheEntry> node)
    {
        DocumentCacheLru.Remove(node);
        DocumentCache.Remove(node.Value.Path);
        _searchCacheWeight -= node.Value.EstimatedWeight;
    }

    private static long EstimateCacheWeight(SearchDocument document) =>
        ((long)document.Name.Length + document.Content.Length) * sizeof(char) +
        ((long)document.NameTokens.Count + document.ContentTokens.Count) * 64;

    private static HashSet<string> NormalizeLocalOnlyPaths(IEnumerable<string>? paths) =>
        (paths ?? []).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AddLocalHit(Dictionary<string, FileSearchHit> hits, FileSearchHit hit)
    {
        if (hits.TryGetValue(hit.FilePath, out var existing))
        {
            if (hit.Score > existing.Score) hits[hit.FilePath] = hit;
            return;
        }
        if (hits.Count < 100)
        {
            hits.Add(hit.FilePath, hit);
            return;
        }

        var worst = hits.Values.Aggregate((currentWorst, candidate) =>
            candidate.Score < currentWorst.Score || candidate.Score.Equals(currentWorst.Score) &&
            string.Compare(candidate.FileName, currentWorst.FileName, StringComparison.OrdinalIgnoreCase) > 0
                ? candidate : currentWorst);
        if (hit.Score < worst.Score || hit.Score.Equals(worst.Score) &&
            string.Compare(hit.FileName, worst.FileName, StringComparison.OrdinalIgnoreCase) >= 0)
            return;
        hits.Remove(worst.FilePath);
        hits.Add(hit.FilePath, hit);
    }

    private IEnumerable<string> EnumerateFiles(string root, string boundary, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root) || !IsSafeRoot(root) || !FileSafety.IsWithin(root, boundary))
            yield break;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (!Directory.Exists(directory) || FileSafety.HasReparsePoint(directory))
                continue;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !FileSafety.IsWithin(entry, boundary)) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return Path.GetFullPath(entry);
            }
        }
    }

    private bool IsSafeRoot(string root) => Directory.Exists(root) && FileSafety.IsWithin(root, _paths.Sorted) &&
                                             !FileSafety.HasReparsePoint(root);

    private string GetCategoryPath(string filePath)
    {
        var relative = Path.GetRelativePath(_paths.Sorted, filePath);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
        {
            var category = CategoryCatalog.Categories.FirstOrDefault(item =>
                item.DirectoryName.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
            if (category is not null)
            {
                if (category.Subcategories.Count == 0)
                    return category.DirectoryName;
                if (segments.Length > 1)
                {
                    var subcategory = category.Subcategories.FirstOrDefault(item =>
                        item.DirectoryName.Equals(segments[1], StringComparison.OrdinalIgnoreCase));
                    if (subcategory is not null)
                        return $"{category.DirectoryName}/{subcategory.DirectoryName}";
                }
            }
        }
        return "Other";
    }

    private string GetLanguageFolder(string languageId)
    {
        var categoryPath = CategoryCatalog.GetPath("programming", languageId);
        return Path.Combine(_paths.Sorted, categoryPath.Category, categoryPath.Subcategory!);
    }

    private static double Score(SearchDocument document, IReadOnlyList<string> queryTerms)
    {
        if (queryTerms.Count == 0) return 0;
        var matched = 0;
        var weighted = 0d;
        foreach (var term in queryTerms)
        {
            var inName = document.NameTokens.Contains(term);
            var inContent = document.ContentTokens.Contains(term);
            if (!inName && !inContent) continue;
            matched++;
            weighted += inName ? 0.65 : 0.25;
            if (inName && inContent) weighted += 0.10;
        }
        var requiredMatches = Math.Max(1, (int)Math.Ceiling(queryTerms.Count * 0.6));
        if (matched < requiredMatches) return 0;
        var coverage = matched / (double)queryTerms.Count;
        return Math.Clamp(0.25 + coverage * 0.45 + Math.Min(0.3, weighted * 0.12), 0.25, 0.95);
    }

    private static string BuildMessage(int matches, int scanned, string? scope, bool usedAi, bool aiFailed,
        bool fellBack, bool indexed = false)
    {
        var scanDescription = indexed ? $"подходящих записей индекса — {scanned}" : $"проверено файлов — {scanned}";
        if (matches == 0)
        {
            var scopeText = fellBack ? $"Папка {scope} и остальные отсортированные папки" : "Отсортированные папки";
            var aiText = aiFailed ? " Облачный ИИ не ответил, поэтому проверка выполнена локально." : string.Empty;
            return $"Совпадений не найдено. {scopeText}: {scanDescription}.{aiText}";
        }

        var fallbackText = fellBack ? $" В папке {scope} совпадений не было, поэтому проверены остальные папки." : string.Empty;
        var aiDescription = usedAi ? " Релевантность дополнительно проверил облачный ИИ." :
            aiFailed ? " Облачный ИИ недоступен; показаны локальные совпадения." : " Выполнен локальный поиск по содержимому.";
        var indexDescription = indexed ? " Поиск выполнен по постоянному индексу." : string.Empty;
        return $"Показано совпадений: {matches} (не более 100). {scanDescription}.{fallbackText}{aiDescription}{indexDescription}";
    }

    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static bool LooksLikeAbsolutePath(string value) =>
        Regex.IsMatch(value, @"(?:\b[a-z]:[\\/]|\\\\[^\\\r\n]+\\|(?:^|\s)/(?:[^/\s]+/)+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record SearchDocument(string Path, string Name, string FileType, string CategoryPath, string Content,
        HashSet<string> NameTokens, HashSet<string> ContentTokens)
    {
        public string ReasonFor(IReadOnlyList<string> terms) => terms.Any(NameTokens.Contains)
            ? "Ключевые слова в имени файла" : "Ключевые слова в содержимом";
    }

    private sealed record SearchCacheEntry(string Path, long Length, long LastWriteTicks,
        SearchDocument Document, long EstimatedWeight);

    private sealed record RankedDocument(SearchDocument Document, double Score);

    private sealed record SearchBatchResult(IReadOnlyList<FileSearchHit> Hits, bool UsedAi, string? AiProviderId, bool AiFailed);

    private sealed record AiCandidateReference(AiFileSearchCandidate Candidate, SearchDocument Document);

    private sealed record SearchAiBatchResult(bool UsedAi, string? AiProviderId, bool AiFailed, bool FoundMatch);

    private sealed record ParsedSearchQuery(string Text, string? LanguageId, string? LanguageName, IReadOnlyList<string> Terms)
    {
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "in", "on", "for", "with", "and", "or", "to", "of", "file", "files", "find", "show",
            "дай", "найди", "найти", "покажи", "файл", "файлы", "мне", "в", "на", "с", "со", "для", "и", "или", "это", "этот", "та",
            "модуль", "модули", "модульи"
        };

        public static ParsedSearchQuery Create(string query)
        {
            var normalized = NormalizeForMatching(query);
            var (languageId, languageName) = DetectLanguage(query);
            var tokens = Tokenize(normalized)
                .Where(token => !StopWords.Contains(token) && !IsLanguageToken(token, languageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(query, languageId, languageName, tokens);
        }

        public static IReadOnlyList<string> Tokenize(string text)
        {
            var split = Regex.Replace(text, @"([\p{Ll}\d])([\p{Lu}])", "$1 $2", RegexOptions.CultureInvariant);
            split = Regex.Replace(split, @"[_./\\-]+", " ", RegexOptions.CultureInvariant);
            var terms = Regex.Matches(NormalizeForMatching(split), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
                .Select(match => match.Value).ToArray();
            return terms.Select(NormalizeToken).Where(token => token.Length > 1).ToArray();
        }

        private static (string? Id, string? Name) DetectLanguage(string query)
        {
            var value = NormalizeForMatching(query);
            if (Regex.IsMatch(value, @"(?<!\w)c\s*#|\bc\s+sharp\b|\bcsharp\b|\bс\s+шарп\w*\b|\bси\s+шарп\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("csharp", "C#");
            if (Regex.IsMatch(value, @"\bc\s*\+\s*\+|\bcpp\b|\bc\+\+\b|\bси\s+плюс\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("cpp", "C++");
            if (Regex.IsMatch(value, @"\bsql\b|\bэскуэль\b|\bскюэль\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("sql", "SQL");
            if (Regex.IsMatch(value, @"\bpython\b|\bпитон\w*\b|\bпайтан\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("python", "Python");
            if (Regex.IsMatch(value, @"\bjavascript\b|\btypescript\b|\bjs\b|\bts\b|\bяваскрипт\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("javascript", "JavaScript / TypeScript");
            if (Regex.IsMatch(value, @"\bjava\b|\bджава\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("java", "Java");
            if (Regex.IsMatch(value, @"\bhtml\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("html", "HTML");
            if (Regex.IsMatch(value, @"\bcss\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("css", "CSS");
            if (Regex.IsMatch(value, @"(?<!\w)\bc\b|\bязык\s+c\b|\bси\s+язык\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return ("c", "C");
            return (null, null);
        }

        private static bool IsLanguageToken(string token, string? languageId) => token switch
        {
            "csharp" or "sharp" or "шарп" => languageId == "csharp",
            "cpp" or "plus" => languageId == "cpp",
            "sql" or "эскуэль" or "скюэль" => languageId == "sql",
            "python" or "питон" or "пайтан" => languageId == "python",
            "javascript" or "typescript" or "js" or "ts" => languageId == "javascript",
            "java" or "джава" => languageId == "java",
            "html" => languageId == "html",
            "css" => languageId == "css",
            "c" => languageId == "c",
            _ => false
        };

        private static string NormalizeForMatching(string value)
        {
            var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"\bmathemat(?:ics|ical)?\b|\bmaths?\b|математ\w*|вычислен\w*", " math ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"модул\w*|\bmodules?\b|библиотек\w*", " module ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"(?<!\w)c\s*#(?!\w)|\bc\s+sharp\b|\bс\s+шарп\w*\b|\bси\s+шарп\w*\b", " csharp ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"(?<!\w)c\s*\+\s*\+(?!\w)", " cpp ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return normalized;
        }

        private static string NormalizeToken(string token)
        {
            if (token.StartsWith("математ", StringComparison.OrdinalIgnoreCase) || token.StartsWith("вычислен", StringComparison.OrdinalIgnoreCase)) return "math";
            if (token.StartsWith("модул", StringComparison.OrdinalIgnoreCase) || token.StartsWith("библиотек", StringComparison.OrdinalIgnoreCase)) return "module";
            if (token is "mathematics" or "mathematical" or "maths") return "math";
            if (token is "modules") return "module";
            if (token.StartsWith("шарп", StringComparison.OrdinalIgnoreCase)) return "шарп";
            if (token.Length > 5 && token.StartsWith("математ", StringComparison.OrdinalIgnoreCase)) return "math";
            foreach (var suffix in new[] { "иями", "ями", "ами", "ого", "ему", "ому", "ыми", "ими", "ый", "ая", "ое", "ые", "ий", "ой", "ую", "юю", "ам", "ям", "ах", "ях", "ов", "ев", "ом", "ем", "ы", "и", "а", "я", "е", "у", "ю" })
                if (token.Length > suffix.Length + 3 && token.EndsWith(suffix, StringComparison.Ordinal))
                    return token[..^suffix.Length];
            return token;
        }
    }
}
