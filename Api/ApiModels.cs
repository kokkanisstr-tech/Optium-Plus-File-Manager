using OptiumPlusFileManager.Domain;

namespace OptiumPlusFileManager.Api;

public sealed record FileDto(
    string Id,
    string Name,
    long SizeBytes,
    string Sha256,
    string MimeType,
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

public sealed record ApproveFileRequest(string CategoryId, string SubcategoryId, bool RememberFileNameRule = false);
public sealed record CreateTicketResponse(string UploadUrl, DateTime ExpiresAtUtc);
public sealed record DownloadLinkResponse(string DownloadUrl, DateTime ExpiresAtUtc);

public static class ApiModelMapper
{
    public static FileDto ToDto(FileRecord file) => new(
        file.Id, file.OriginalName, file.SizeBytes, file.Sha256, file.DetectedMimeType, file.DetectedFileType,
        file.DetectedLanguage, file.CategoryId, file.SubcategoryId, file.Confidence, file.Summary, file.Status,
        file.SourceType, file.ProviderId, file.Reason, file.CreatedAtUtc, file.CompletedAtUtc);
}
