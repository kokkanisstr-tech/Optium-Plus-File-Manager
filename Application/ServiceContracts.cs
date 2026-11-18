using OptiumPlusFileManager.Domain;

namespace OptiumPlusFileManager.Application;

public interface IFileRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task InsertAsync(FileRecord file, string details, CancellationToken cancellationToken = default);
    Task UpdateAsync(FileRecord file, string eventDetails, CancellationToken cancellationToken = default);
    Task AddAnalysisAsync(FileAnalysisRecord analysis, CancellationToken cancellationToken = default);
    Task<FileRecord?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<FileRecord?> GetByOriginalPathAsync(string path, CancellationToken cancellationToken = default);
    Task<FileRecord?> FindDuplicateAsync(string sha256, string exceptId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileRecord>> ListAsync(string? status, int take, CancellationToken cancellationToken = default,
        int skip = 0, string? categoryId = null, string? sourceType = null, DateTime? createdAfterUtc = null);
    Task<IReadOnlyList<FileEvent>> GetEventsAsync(string fileId, CancellationToken cancellationToken = default);
    Task<SortingRule?> FindFileNameRuleAsync(string fileName, CancellationToken cancellationToken = default);
    Task SaveFileNameRuleAsync(string fileName, string categoryId, string subcategoryId, CancellationToken cancellationToken = default);
    Task<DashboardStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileRecord>> GetPendingAsync(CancellationToken cancellationToken = default);
    Task ReplaceWatchFoldersAsync(IEnumerable<string> folders, bool recursive, CancellationToken cancellationToken = default);
    Task ReplaceProviderSettingsAsync(IEnumerable<(string ProviderId, string Model, bool Enabled)> providers,
        CancellationToken cancellationToken = default);
    Task<bool> TryReserveAiRequestsAsync(int requestCount, int monthlyLimit, CancellationToken cancellationToken = default);
    Task<int> GetAiRequestsUsedThisMonthAsync(CancellationToken cancellationToken = default);
}

public interface IFileContentExtractor
{
    Task<ExtractedContent> ExtractAsync(string filePath, string fileType, CancellationToken cancellationToken = default);
}

public interface IFileClassifier
{
    ClassificationResult Classify(FileFormatInfo format, string fileName, string extension, string extractedText);
}

public interface IAiFileAnalyzer
{
    string ProviderId { get; }
    bool IsConfigured { get; }
    string Model { get; }
    Task<AiClassification> AnalyzeAsync(FileAnalysisInput input, CancellationToken cancellationToken = default);
}

public interface IAiFileSearchAnalyzer
{
    string ProviderId { get; }
    bool IsConfigured { get; }
    Task<IReadOnlyList<AiFileSearchMatch>> SearchAsync(AiFileSearchInput input, CancellationToken cancellationToken = default);
}

public sealed record AiFileSearchCandidate(string Id, string Name, string CategoryPath, string FileType, string Content,
    string? ImageMimeType = null, byte[]? ImageData = null);

public sealed record AiFileSearchInput(string Query, IReadOnlyList<AiFileSearchCandidate> Candidates);

public sealed record AiFileSearchMatch(string CandidateId, double Score, string Reason);

public sealed record FileSearchHit(string FilePath, string FileName, string FileType, string CategoryPath,
    double Score, string Reason, bool MatchedByAi);

public sealed record FileSearchResult(IReadOnlyList<FileSearchHit> Matches, int ScannedFileCount, bool UsedAi,
    string? AiProviderId, string? ScopeName, bool FellBackToAllFolders, string Message);

public sealed record ExtractedContent(string Text, string? Warning = null);

public sealed record FileFormatInfo(
    string FileType,
    string MimeType,
    bool IsText,
    bool ExtensionConflict,
    bool SuspiciousArchive,
    string? Warning = null);

public sealed record ProcessingOutcome(FileRecord File, bool WasMoved, string Message);
