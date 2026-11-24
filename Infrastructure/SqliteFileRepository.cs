using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OptiumPlusFileManager.Infrastructure;

public sealed class SqliteFileRepository : IFileRepository, IFileSearchIndex
{
    private const int CurrentSearchIndexVersion = 1;
    private readonly string _connectionString;
    private readonly string _logFolder;
    private readonly SemaphoreSlim _logLock = new(1, 1);

    public SqliteFileRepository(FileSorterPaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Database)!);
        _logFolder = paths.Logs;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS Files (
                Id TEXT PRIMARY KEY,
                OriginalName TEXT NOT NULL,
                OriginalPath TEXT NOT NULL,
                CurrentPath TEXT NOT NULL,
                PendingDestinationPath TEXT NULL,
                SizeBytes INTEGER NOT NULL,
                Sha256 TEXT NOT NULL DEFAULT '',
                DetectedMimeType TEXT NOT NULL DEFAULT 'application/octet-stream',
                DetectedFileType TEXT NOT NULL DEFAULT 'unknown',
                DetectedLanguage TEXT NOT NULL DEFAULT '',
                CategoryId TEXT NOT NULL DEFAULT 'other',
                SubcategoryId TEXT NOT NULL DEFAULT 'other',
                Confidence REAL NOT NULL DEFAULT 0,
                Summary TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL,
                SourceType TEXT NOT NULL,
                ProviderId TEXT NULL,
                Reason TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                UpdatedAtUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Files_Sha256 ON Files(Sha256);
            CREATE INDEX IF NOT EXISTS IX_Files_Status_Created ON Files(Status, CreatedAtUtc DESC);
            CREATE TABLE IF NOT EXISTS AnalysisResults (
                Id TEXT PRIMARY KEY,
                FileId TEXT NOT NULL REFERENCES Files(Id),
                ProviderId TEXT NOT NULL,
                ClassifierVersion TEXT NOT NULL,
                Confidence REAL NOT NULL,
                ResultJson TEXT NOT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AnalysisResults_FileId ON AnalysisResults(FileId, CreatedAtUtc DESC);
            CREATE TABLE IF NOT EXISTS FileEvents (
                Id TEXT PRIMARY KEY,
                FileId TEXT NOT NULL REFERENCES Files(Id),
                Status TEXT NOT NULL,
                Details TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FileEvents_FileId ON FileEvents(FileId, CreatedAtUtc);
            CREATE TABLE IF NOT EXISTS WatchFolders (
                Path TEXT PRIMARY KEY,
                Enabled INTEGER NOT NULL DEFAULT 1,
                Recursive INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Categories (
                Id TEXT PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                DirectoryName TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SortingRules (
                Id TEXT PRIMARY KEY,
                Pattern TEXT NOT NULL,
                CategoryId TEXT NOT NULL,
                SubcategoryId TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS ProviderSettings (
                ProviderId TEXT PRIMARY KEY,
                Model TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS AiUsage (
                MonthUtc TEXT PRIMARY KEY,
                RequestCount INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SearchIndexDocuments (
                RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId TEXT NOT NULL UNIQUE,
                FilePath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                FileType TEXT NOT NULL,
                Content TEXT NOT NULL,
                NameTerms TEXT NOT NULL,
                ContentTerms TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                LastWriteTicks INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndexFts USING fts5(
                NameTerms, ContentTerms, tokenize='unicode61 remove_diacritics 2'
            );
            CREATE TABLE IF NOT EXISTS SearchIndexState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                IsReady INTEGER NOT NULL DEFAULT 0,
                IndexVersion INTEGER NOT NULL DEFAULT 1
            );
            INSERT OR IGNORE INTO SearchIndexState (Id, IsReady, IndexVersion) VALUES (1, 0, 1);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsurePendingDestinationColumnAsync(connection, cancellationToken);
        await EnsureSearchIndexVersionColumnAsync(connection, cancellationToken);
        await ResetOutdatedSearchIndexAsync(connection, cancellationToken);
        foreach (var category in Domain.CategoryCatalog.Categories)
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = "INSERT OR IGNORE INTO Categories (Id, DisplayName, DirectoryName) VALUES ($id,$name,$directory);";
            seed.Parameters.AddWithValue("$id", category.Id);
            seed.Parameters.AddWithValue("$name", category.DisplayName);
            seed.Parameters.AddWithValue("$directory", category.DirectoryName);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertSearchDocumentAsync(SearchIndexDocument document, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long? rowId;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT RowId FROM SearchIndexDocuments WHERE FileId=$id;";
            find.Parameters.AddWithValue("$id", document.FileId);
            var value = await find.ExecuteScalarAsync(cancellationToken);
            rowId = value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (rowId is not null)
        {
            await using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM SearchIndexFts WHERE rowid=$rowid;";
            remove.Parameters.AddWithValue("$rowid", rowId.Value);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO SearchIndexDocuments
                    (FileId, FilePath, FileName, FileType, Content, NameTerms, ContentTerms, SizeBytes, LastWriteTicks)
                VALUES ($id, $path, $name, $type, $content, $nameTerms, $contentTerms, $size, $lastWrite)
                ON CONFLICT(FileId) DO UPDATE SET
                    FilePath=excluded.FilePath, FileName=excluded.FileName, FileType=excluded.FileType,
                    Content=excluded.Content, NameTerms=excluded.NameTerms, ContentTerms=excluded.ContentTerms,
                    SizeBytes=excluded.SizeBytes, LastWriteTicks=excluded.LastWriteTicks;
                """;
            AddSearchDocumentParameters(save, document);
            await save.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT RowId FROM SearchIndexDocuments WHERE FileId=$id;";
            find.Parameters.AddWithValue("$id", document.FileId);
            rowId = Convert.ToInt64(await find.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var addToFts = connection.CreateCommand())
        {
            addToFts.Transaction = transaction;
            addToFts.CommandText = "INSERT INTO SearchIndexFts (rowid, NameTerms, ContentTerms) VALUES ($rowid, $nameTerms, $contentTerms);";
            addToFts.Parameters.AddWithValue("$rowid", rowId.Value);
            addToFts.Parameters.AddWithValue("$nameTerms", document.NormalizedName);
            addToFts.Parameters.AddWithValue("$contentTerms", document.NormalizedContent);
            await addToFts.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveSearchDocumentAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var removeFts = connection.CreateCommand())
        {
            removeFts.Transaction = transaction;
            removeFts.CommandText = "DELETE FROM SearchIndexFts WHERE rowid=(SELECT RowId FROM SearchIndexDocuments WHERE FileId=$id);";
            removeFts.Parameters.AddWithValue("$id", fileId);
            await removeFts.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var removeDocument = connection.CreateCommand())
        {
            removeDocument.Transaction = transaction;
            removeDocument.CommandText = "DELETE FROM SearchIndexDocuments WHERE FileId=$id;";
            removeDocument.Parameters.AddWithValue("$id", fileId);
            await removeDocument.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SearchIndexMetadata?> GetSearchIndexMetadataAsync(string fileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SizeBytes, LastWriteTicks FROM SearchIndexDocuments WHERE FileId=$id;";
        command.Parameters.AddWithValue("$id", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SearchIndexMetadata(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    public async Task<bool> HasSearchIndexDocumentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM SearchIndexDocuments LIMIT 1);";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async Task<IReadOnlyList<SearchIndexDocument>> SearchIndexAsync(string matchExpression,
        IReadOnlyList<string> terms, int take,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(matchExpression) || terms.Count == 0) return [];
        take = Math.Clamp(take, 1, 5000);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var nameMatches = BuildMatchedTermsSql("d.NameTerms", terms.Count, "nameTerm");
        var contentMatches = BuildMatchedTermsSql("d.ContentTerms", terms.Count, "contentTerm");
        command.CommandText = $"""
            SELECT d.FileId, COALESCE(f.CurrentPath, d.FilePath), d.FileName, d.FileType, substr(d.Content, 1, 8000),
                d.SizeBytes, d.LastWriteTicks, {nameMatches}, {contentMatches}
            FROM SearchIndexFts
            JOIN SearchIndexDocuments d ON d.RowId=SearchIndexFts.rowid
            LEFT JOIN Files f ON f.Id=d.FileId
            WHERE SearchIndexFts MATCH $match AND
                ((f.Id IS NULL AND d.FileId LIKE 'manual:%') OR f.Status='Completed')
            ORDER BY bm25(SearchIndexFts, 4.0, 1.0), d.FileName COLLATE NOCASE
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$match", matchExpression);
        command.Parameters.AddWithValue("$take", take);
        for (var index = 0; index < terms.Count; index++)
        {
            command.Parameters.AddWithValue($"$nameTerm{index}", terms[index]);
            command.Parameters.AddWithValue($"$contentTerm{index}", terms[index]);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<SearchIndexDocument>();
        while (await reader.ReadAsync(cancellationToken))
            documents.Add(new SearchIndexDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), string.Empty, string.Empty, reader.GetInt64(5),
                reader.GetInt64(6), reader.GetString(7), reader.GetString(8)));
        return documents;
    }

    public async Task<bool> IsSearchIndexReadyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsReady, IndexVersion FROM SearchIndexState WHERE Id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && reader.GetInt32(0) != 0 &&
               reader.GetInt32(1) == CurrentSearchIndexVersion;
    }

    public async Task SetSearchIndexReadyAsync(bool ready, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SearchIndexState SET IsReady=$ready, IndexVersion=$version WHERE Id=1;";
        command.Parameters.AddWithValue("$ready", ready ? 1 : 0);
        command.Parameters.AddWithValue("$version", CurrentSearchIndexVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertAsync(FileRecord file, string details, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO Files (Id, OriginalName, OriginalPath, CurrentPath, PendingDestinationPath, SizeBytes, Sha256, DetectedMimeType,
                    DetectedFileType, DetectedLanguage, CategoryId, SubcategoryId, Confidence, Summary, Status,
                    SourceType, ProviderId, Reason, CreatedAtUtc, CompletedAtUtc, UpdatedAtUtc)
                VALUES ($id, $name, $original, $current, $pending, $size, $sha, $mime, $type, $language, $category, $subcategory,
                    $confidence, $summary, $status, $source, $provider, $reason, $created, $completed, $updated);
                """;
            AddFileParameters(command, file);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEventAsync(connection, (SqliteTransaction)transaction, file.Id, file.Status, details, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await WriteStructuredLogAsync(file.Id, file.Status, details, cancellationToken);
    }

    public async Task UpdateAsync(FileRecord file, string eventDetails, CancellationToken cancellationToken = default)
    {
        file.UpdatedAtUtc = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE Files SET OriginalName=$name, OriginalPath=$original, CurrentPath=$current, PendingDestinationPath=$pending, SizeBytes=$size,
                    Sha256=$sha, DetectedMimeType=$mime, DetectedFileType=$type, DetectedLanguage=$language,
                    CategoryId=$category, SubcategoryId=$subcategory, Confidence=$confidence, Summary=$summary,
                    Status=$status, SourceType=$source, ProviderId=$provider, Reason=$reason,
                    CompletedAtUtc=$completed, UpdatedAtUtc=$updated WHERE Id=$id;
                """;
            AddFileParameters(command, file);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Запись файла не найдена в истории.");
        }
        await InsertEventAsync(connection, (SqliteTransaction)transaction, file.Id, file.Status, eventDetails, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await WriteStructuredLogAsync(file.Id, file.Status, eventDetails, cancellationToken);
    }

    public async Task AddAnalysisAsync(FileAnalysisRecord analysis, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AnalysisResults (Id, FileId, ProviderId, ClassifierVersion, Confidence, ResultJson, DurationMilliseconds, CreatedAtUtc)
            VALUES ($id, $file, $provider, $version, $confidence, $json, $duration, $created);
            """;
        command.Parameters.AddWithValue("$id", analysis.Id);
        command.Parameters.AddWithValue("$file", analysis.FileId);
        command.Parameters.AddWithValue("$provider", analysis.ProviderId);
        command.Parameters.AddWithValue("$version", analysis.ClassifierVersion);
        command.Parameters.AddWithValue("$confidence", analysis.Confidence);
        command.Parameters.AddWithValue("$json", analysis.ResultJson);
        command.Parameters.AddWithValue("$duration", analysis.DurationMilliseconds);
        command.Parameters.AddWithValue("$created", ToDb(analysis.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FileRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    public async Task<FileRecord?> GetByOriginalPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE OriginalPath=$path ORDER BY CreatedAtUtc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$path", fullPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    public async Task<FileRecord?> FindDuplicateAsync(string sha256, string exceptId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE Sha256=$sha AND Id<>$id AND Status IN ('Completed','NeedsReview','Quarantined') ORDER BY CreatedAtUtc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$id", exceptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    public async Task<IReadOnlyList<FileRecord>> ListAsync(string? status, int take, CancellationToken cancellationToken = default,
        int skip = 0, string? categoryId = null, string? sourceType = null, DateTime? createdAfterUtc = null)
    {
        take = Math.Clamp(take, 1, 1000);
        skip = Math.Max(0, skip);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) filters.Add("Status=$status");
        if (!string.IsNullOrWhiteSpace(categoryId)) filters.Add("CategoryId=$category");
        if (!string.IsNullOrWhiteSpace(sourceType)) filters.Add("SourceType=$source");
        if (createdAfterUtc is not null) filters.Add("CreatedAtUtc >= $createdAfter");
        command.CommandText = $"SELECT * FROM Files{(filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters))} ORDER BY CreatedAtUtc DESC, Id DESC LIMIT $take OFFSET $skip;";
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);
        if (!string.IsNullOrWhiteSpace(status)) command.Parameters.AddWithValue("$status", status);
        if (!string.IsNullOrWhiteSpace(categoryId)) command.Parameters.AddWithValue("$category", categoryId);
        if (!string.IsNullOrWhiteSpace(sourceType)) command.Parameters.AddWithValue("$source", sourceType);
        if (createdAfterUtc is not null) command.Parameters.AddWithValue("$createdAfter", ToDb(createdAfterUtc.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<FileRecord>();
        while (await reader.ReadAsync(cancellationToken)) files.Add(ReadFile(reader));
        return files;
    }

    public async Task<IReadOnlyList<FileEvent>> GetEventsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FileId, Status, Details, CreatedAtUtc FROM FileEvents WHERE FileId=$file ORDER BY CreatedAtUtc;";
        command.Parameters.AddWithValue("$file", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<FileEvent>();
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new FileEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), ParseDate(reader.GetString(4))));
        return events;
    }

    public async Task<SortingRule?> FindFileNameRuleAsync(string fileName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Pattern, CategoryId, SubcategoryId FROM SortingRules WHERE Pattern=$pattern AND Enabled=1 LIMIT 1;";
        command.Parameters.AddWithValue("$pattern", FileNameRulePattern(fileName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SortingRule(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task SaveFileNameRuleAsync(string fileName, string categoryId, string subcategoryId,
        CancellationToken cancellationToken = default)
    {
        if (!CategoryCatalog.IsAllowed(categoryId, subcategoryId))
            throw new ArgumentException("Правило содержит категорию вне разрешённого справочника.");
        var pattern = FileNameRulePattern(fileName);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pattern))).ToLowerInvariant();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SortingRules (Id, Pattern, CategoryId, SubcategoryId, Enabled)
            VALUES ($id, $pattern, $category, $subcategory, 1)
            ON CONFLICT(Id) DO UPDATE SET Pattern=$pattern, CategoryId=$category, SubcategoryId=$subcategory, Enabled=1;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$pattern", pattern);
        command.Parameters.AddWithValue("$category", categoryId);
        command.Parameters.AddWithValue("$subcategory", subcategoryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DashboardStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN Status IN ('Detected','Ready','Extracting','Classifying','Moving') THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status='Completed' AND CompletedAtUtc >= $today THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status='NeedsReview' THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status='Failed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status='Quarantined' THEN 1 ELSE 0 END)
            FROM Files;
            """;
        command.Parameters.AddWithValue("$today", ToDb(DateTime.UtcNow.Date));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(0, 0, 0, 0, 0);
        return new(IntValue(reader, 0), IntValue(reader, 1), IntValue(reader, 2), IntValue(reader, 3), IntValue(reader, 4));
    }

    public async Task<IReadOnlyList<FileRecord>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE Status IN ('Detected','Ready','Extracting','Classifying','Moving') ORDER BY CreatedAtUtc;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<FileRecord>();
        while (await reader.ReadAsync(cancellationToken)) files.Add(ReadFile(reader));
        return files;
    }

    public async Task ReplaceWatchFoldersAsync(IEnumerable<string> folders, bool recursive, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM WatchFolders;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var folder in folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO WatchFolders (Path, Enabled, Recursive, CreatedAtUtc) VALUES ($path,1,$recursive,$created);";
            insert.Parameters.AddWithValue("$path", folder);
            insert.Parameters.AddWithValue("$recursive", recursive ? 1 : 0);
            insert.Parameters.AddWithValue("$created", ToDb(DateTime.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceProviderSettingsAsync(IEnumerable<(string ProviderId, string Model, bool Enabled)> providers,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var provider in providers)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO ProviderSettings (ProviderId, Model, Enabled) VALUES ($provider,$model,$enabled) " +
                                  "ON CONFLICT(ProviderId) DO UPDATE SET Model=excluded.Model, Enabled=excluded.Enabled;";
            command.Parameters.AddWithValue("$provider", provider.ProviderId);
            command.Parameters.AddWithValue("$model", provider.Model);
            command.Parameters.AddWithValue("$enabled", provider.Enabled ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryReserveAiRequestsAsync(int requestCount, int monthlyLimit,
        CancellationToken cancellationToken = default)
    {
        requestCount = Math.Clamp(requestCount, 1, 10);
        monthlyLimit = Math.Clamp(monthlyLimit, 1, 100_000);
        if (requestCount > monthlyLimit) return false;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AiUsage (MonthUtc, RequestCount) VALUES ($month, $count)
            ON CONFLICT(MonthUtc) DO UPDATE SET RequestCount=AiUsage.RequestCount + $count
            WHERE AiUsage.RequestCount + $count <= $limit;
            """;
        command.Parameters.AddWithValue("$month", DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$count", requestCount);
        command.Parameters.AddWithValue("$limit", monthlyLimit);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> GetAiRequestsUsedThisMonthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestCount FROM AiUsage WHERE MonthUtc=$month;";
        command.Parameters.AddWithValue("$month", DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task WriteStructuredLogAsync(string fileId, string status, string? details, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_logFolder);
            var normalizedDetails = string.IsNullOrWhiteSpace(details) ? "state_changed" : details.Trim();
            var entry = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow,
                fileId,
                status,
                eventName = normalizedDetails[..Math.Min(normalizedDetails.Length, 160)]
            });
            var path = Path.Combine(_logFolder, $"filesorter-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            await _logLock.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(path, entry + Environment.NewLine, cancellationToken);
            }
            finally { _logLock.Release(); }
        }
        catch (IOException)
        {
            // SQLite is the authoritative event history. A secondary text-log failure must not
            // turn a successfully committed file operation into an application error.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep processing if optional JSONL logging is unavailable.
        }
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string fileId, string status, string? details, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO FileEvents (Id, FileId, Status, Details, CreatedAtUtc) VALUES ($id,$file,$status,$details,$created);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$file", fileId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFileParameters(SqliteCommand command, FileRecord file)
    {
        command.Parameters.AddWithValue("$id", file.Id);
        command.Parameters.AddWithValue("$name", file.OriginalName);
        command.Parameters.AddWithValue("$original", file.OriginalPath);
        command.Parameters.AddWithValue("$current", file.CurrentPath);
        command.Parameters.AddWithValue("$pending", (object?)file.PendingDestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", file.SizeBytes);
        command.Parameters.AddWithValue("$sha", file.Sha256);
        command.Parameters.AddWithValue("$mime", file.DetectedMimeType);
        command.Parameters.AddWithValue("$type", file.DetectedFileType);
        command.Parameters.AddWithValue("$language", file.DetectedLanguage);
        command.Parameters.AddWithValue("$category", file.CategoryId);
        command.Parameters.AddWithValue("$subcategory", file.SubcategoryId);
        command.Parameters.AddWithValue("$confidence", file.Confidence);
        command.Parameters.AddWithValue("$summary", file.Summary);
        command.Parameters.AddWithValue("$status", file.Status);
        command.Parameters.AddWithValue("$source", file.SourceType);
        command.Parameters.AddWithValue("$provider", (object?)file.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)file.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", ToDb(file.CreatedAtUtc));
        command.Parameters.AddWithValue("$completed", file.CompletedAtUtc is null ? DBNull.Value : ToDb(file.CompletedAtUtc.Value));
        command.Parameters.AddWithValue("$updated", file.UpdatedAtUtc is null ? DBNull.Value : ToDb(file.UpdatedAtUtc.Value));
    }

    private static void AddSearchDocumentParameters(SqliteCommand command, SearchIndexDocument document)
    {
        command.Parameters.AddWithValue("$id", document.FileId);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(document.FilePath));
        command.Parameters.AddWithValue("$name", document.FileName);
        command.Parameters.AddWithValue("$type", document.FileType);
        command.Parameters.AddWithValue("$content", document.Content);
        command.Parameters.AddWithValue("$nameTerms", document.NormalizedName);
        command.Parameters.AddWithValue("$contentTerms", document.NormalizedContent);
        command.Parameters.AddWithValue("$size", document.SizeBytes);
        command.Parameters.AddWithValue("$lastWrite", document.LastWriteTicks);
    }

    private static string BuildMatchedTermsSql(string column, int termCount, string parameterPrefix) =>
        string.Join(" || ", Enumerable.Range(0, termCount).Select(index =>
            $"CASE WHEN instr(' ' || {column} || ' ', ' ' || ${parameterPrefix}{index} || ' ') > 0 " +
            $"THEN ${parameterPrefix}{index} || ' ' ELSE '' END"));

    private static FileRecord ReadFile(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        OriginalName = reader.GetString(reader.GetOrdinal("OriginalName")),
        OriginalPath = reader.GetString(reader.GetOrdinal("OriginalPath")),
        CurrentPath = reader.GetString(reader.GetOrdinal("CurrentPath")),
        PendingDestinationPath = NullableString(reader, "PendingDestinationPath"),
        SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
        Sha256 = reader.GetString(reader.GetOrdinal("Sha256")),
        DetectedMimeType = reader.GetString(reader.GetOrdinal("DetectedMimeType")),
        DetectedFileType = reader.GetString(reader.GetOrdinal("DetectedFileType")),
        DetectedLanguage = reader.GetString(reader.GetOrdinal("DetectedLanguage")),
        CategoryId = reader.GetString(reader.GetOrdinal("CategoryId")),
        SubcategoryId = reader.GetString(reader.GetOrdinal("SubcategoryId")),
        Confidence = reader.GetDouble(reader.GetOrdinal("Confidence")),
        Summary = reader.GetString(reader.GetOrdinal("Summary")),
        Status = reader.GetString(reader.GetOrdinal("Status")),
        SourceType = reader.GetString(reader.GetOrdinal("SourceType")),
        ProviderId = NullableString(reader, "ProviderId"),
        Reason = NullableString(reader, "Reason"),
        CreatedAtUtc = ParseDate(reader.GetString(reader.GetOrdinal("CreatedAtUtc"))),
        CompletedAtUtc = NullableDate(reader, "CompletedAtUtc"),
        UpdatedAtUtc = NullableDate(reader, "UpdatedAtUtc")
    };

    private static string? NullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? NullableDate(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
    }

    private static int IntValue(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);

    private static async Task EnsurePendingDestinationColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Files);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
            if (reader.GetString(1).Equals("PendingDestinationPath", StringComparison.OrdinalIgnoreCase)) found = true;
        await reader.DisposeAsync();
        if (found) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Files ADD COLUMN PendingDestinationPath TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSearchIndexVersionColumnAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(SearchIndexState);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
            if (reader.GetString(1).Equals("IndexVersion", StringComparison.OrdinalIgnoreCase)) found = true;
        await reader.DisposeAsync();
        if (found) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE SearchIndexState ADD COLUMN IndexVersion INTEGER NOT NULL DEFAULT 1;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ResetOutdatedSearchIndexAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionQuery = connection.CreateCommand();
        versionQuery.CommandText = "SELECT IndexVersion FROM SearchIndexState WHERE Id=1;";
        var versionValue = await versionQuery.ExecuteScalarAsync(cancellationToken);
        if (versionValue is not null and not DBNull &&
            Convert.ToInt32(versionValue, System.Globalization.CultureInfo.InvariantCulture) == CurrentSearchIndexVersion)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM SearchIndexFts;
            DELETE FROM SearchIndexDocuments;
            UPDATE SearchIndexState SET IsReady=0, IndexVersion=$version
            WHERE Id=1 AND IndexVersion<>$version;
            """;
        command.Parameters.AddWithValue("$version", CurrentSearchIndexVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static string ToDb(DateTime value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static string FileNameRulePattern(string fileName) => "filename:" + Path.GetFileName(fileName).Trim().ToUpperInvariant();
    private static DateTime ParseDate(string value) => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
