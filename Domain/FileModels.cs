namespace OptiumPlusFileManager.Domain;

public enum FileStatus
{
    Detected,
    Ready,
    Extracting,
    Classifying,
    NeedsReview,
    Moving,
    Completed,
    Failed,
    Quarantined,
    Reverted,
    Duplicate,
    Skipped
}

public sealed class FileRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string OriginalName { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
    public string? PendingDestinationPath { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string DetectedMimeType { get; set; } = "application/octet-stream";
    public string DetectedFileType { get; set; } = "unknown";
    public string DetectedLanguage { get; set; } = string.Empty;
    public string CategoryId { get; set; } = "other";
    public string SubcategoryId { get; set; } = "other";
    public double Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = nameof(FileStatus.Detected);
    public string SourceType { get; set; } = "manual";
    public string? ProviderId { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed record FileEvent(string Id, string FileId, string Status, string? Details, DateTime CreatedAtUtc);

public sealed record SortingRule(string Pattern, string CategoryId, string SubcategoryId);

public sealed record ClassificationCandidate(string CategoryId, string SubcategoryId, double Confidence, string ReasonCode);

public sealed record ClassificationResult(
    string CategoryId,
    string SubcategoryId,
    string DetectedFileType,
    string DetectedLanguage,
    string Topic,
    string Summary,
    double Confidence,
    string? SuggestedExtension,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<ClassificationCandidate> Candidates,
    string ProviderId = "local");

public sealed record FileAnalysisInput(
    string FileName,
    string FileExtension,
    string MimeType,
    string DetectedFileType,
    long SizeBytes,
    string ExtractedContent,
    bool ContainsSecrets,
    ClassificationResult LocalClassification,
    byte[]? ImageData = null);

public sealed record FileSummary(
    string Id,
    string OriginalName,
    string CurrentPath,
    long SizeBytes,
    string Sha256,
    string DetectedMimeType,
    string DetectedFileType,
    string DetectedLanguage,
    string CategoryId,
    string SubcategoryId,
    double Confidence,
    string Summary,
    string Status,
    string SourceType,
    string? ProviderId,
    string? Reason,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record DashboardStats(int PendingCount, int ProcessedToday, int ReviewCount, int ErrorCount, int QuarantineCount);

public sealed record ProviderHealth(string ProviderId, bool Configured, string Status, string? Model);

public sealed record AiClassification(
    string CategoryId,
    string SubcategoryId,
    string DetectedFileType,
    string DetectedLanguage,
    string Topic,
    string Summary,
    double Confidence,
    string? SuggestedExtension,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ReasonCodes,
    string ProviderId = "local");

public sealed record FileAnalysisRecord(
    string Id,
    string FileId,
    string ProviderId,
    string ClassifierVersion,
    double Confidence,
    string ResultJson,
    long DurationMilliseconds,
    DateTime CreatedAtUtc);
