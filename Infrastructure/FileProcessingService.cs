using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;
using Microsoft.Data.Sqlite;

namespace OptiumPlusFileManager.Infrastructure;

public sealed class FileProcessingService
{
    private readonly FileSorterPaths _paths;
    private readonly IFileRepository _repository;
    private readonly FileFormatDetector _formatDetector;
    private readonly IFileContentExtractor _extractor;
    private readonly IFileClassifier _classifier;
    private readonly Func<AppSettings> _settings;

    public FileProcessingService(FileSorterPaths paths, IFileRepository repository, FileFormatDetector formatDetector,
        IFileContentExtractor extractor, IFileClassifier classifier, Func<AppSettings> settings)
    {
        _paths = paths;
        _repository = repository;
        _formatDetector = formatDetector;
        _extractor = extractor;
        _classifier = classifier;
        _settings = settings;
    }

    public async Task<ProcessingOutcome> ProcessAsync(string filePath, string sourceType, IReadOnlyCollection<string> allowedRoots,
        bool deepAnalysis = false, FileRecord? existing = null, CancellationToken cancellationToken = default)
    {
        FileRecord? record = existing;
        FileStream? processLock = null;
        var pathLockAcquired = false;
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            processLock = await AcquirePathLockAsync(fullPath, cancellationToken);
            pathLockAcquired = true;
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Файл больше не найден.");
            if (!allowedRoots.Any(root => FileSafety.IsWithin(fullPath, root)))
                throw new UnauthorizedAccessException("Путь находится вне разрешённых папок.");
            if (FileSafety.HasReparsePoint(fullPath))
                throw new UnauthorizedAccessException("Символические ссылки и точки повторной обработки запрещены.");
            if (FileTypeClassifier.IsTemporary(fullPath))
                throw new InvalidOperationException("Временные файлы не обрабатываются.");

            var info = new FileInfo(fullPath);
            var normalizedName = FileSafety.SanitizeFileName(info.Name);
            string? precomputedSha = null;
            var alreadyWaitedForStableFile = false;
            if (record is null)
            {
                var previous = await _repository.GetByOriginalPathAsync(fullPath, cancellationToken);
                if (previous is not null && previous.Status is nameof(FileStatus.Completed) or nameof(FileStatus.NeedsReview)
                    or nameof(FileStatus.Quarantined) or nameof(FileStatus.Duplicate) or nameof(FileStatus.Failed) or nameof(FileStatus.Reverted))
                {
                    if (!previous.CurrentPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // A later file can reuse the same name and incoming path. Preserve history by creating a new record.
                        previous = null;
                    }
                    else
                    {
                        if (!await FileSafety.WaitUntilReadyAsync(fullPath, cancellationToken))
                            return new(previous, false, "Файл пока записывается; он будет проверен при следующем событии.");
                        alreadyWaitedForStableFile = true;
                        precomputedSha = await FileSafety.ComputeSha256Async(fullPath, cancellationToken);
                        if (string.Equals(precomputedSha, previous.Sha256, StringComparison.OrdinalIgnoreCase))
                            return new(previous, false, "Файл с этим содержимым уже обработан по данному пути.");
                        previous = null;
                    }
                }

                record = previous ?? new FileRecord
                {
                    OriginalName = normalizedName,
                    OriginalPath = fullPath,
                    CurrentPath = fullPath,
                    SizeBytes = info.Length,
                    SourceType = sourceType,
                    Status = nameof(FileStatus.Detected)
                };
                if (previous is null)
                    await _repository.InsertAsync(record, "Файл принят в очередь.", cancellationToken);
                else
                {
                    record.Status = nameof(FileStatus.Detected);
                    record.Reason = null;
                    await _repository.UpdateAsync(record, "Повторная обработка файла.", cancellationToken);
                }
            }
            else
            {
                record.CurrentPath = fullPath;
                record.SizeBytes = info.Length;
                record.SourceType = sourceType;
                record.Status = nameof(FileStatus.Detected);
                record.Reason = null;
                await _repository.UpdateAsync(record, "Запрошен повторный анализ.", cancellationToken);
            }

            if (!alreadyWaitedForStableFile && !await FileSafety.WaitUntilReadyAsync(fullPath, cancellationToken))
                return await FailAsync(record, "Файл не завершил запись за 30 секунд; он оставлен на месте.", cancellationToken);

            record.Status = nameof(FileStatus.Ready);
            record.SizeBytes = new FileInfo(fullPath).Length;
            await _repository.UpdateAsync(record, "Файл стабилен и доступен для чтения.", cancellationToken);

            var maximumBytes = Math.Clamp(_settings().MaxFileSizeMb, 1, 2048) * 1024L * 1024L;
            if (record.SizeBytes > maximumBytes)
            {
                record.Sha256 = await FileSafety.ComputeSha256Async(fullPath, cancellationToken);
                record.Reason = $"Размер файла превышает лимит {maximumBytes / 1024 / 1024} МБ.";
                return await RouteWithoutAnalysisAsync(record, _paths.Quarantine, nameof(FileStatus.Quarantined), cancellationToken);
            }

            record.Sha256 = precomputedSha ?? await FileSafety.ComputeSha256Async(fullPath, cancellationToken);
            var duplicate = await _repository.FindDuplicateAsync(record.Sha256, record.Id, cancellationToken);
            if (duplicate is not null)
            {
                record.Status = nameof(FileStatus.Duplicate);
                record.Reason = $"Совпадает SHA-256 с записью {duplicate.Id}. Исходный файл оставлен без изменений.";
                await _repository.UpdateAsync(record, "Найден точный дубликат по SHA-256; файл не перемещён.", cancellationToken);
                return new(record, false, record.Reason);
            }

            record.Status = nameof(FileStatus.Extracting);
            await _repository.UpdateAsync(record, "Определение формата и извлечение содержимого.", cancellationToken);
            var format = await _formatDetector.DetectAsync(fullPath, cancellationToken);
            record.DetectedMimeType = format.MimeType;
            record.DetectedFileType = format.FileType;

            if (format.SuspiciousArchive)
            {
                record.Reason = format.Warning ?? "Архив не прошёл безопасную проверку.";
                return await RouteWithoutAnalysisAsync(record, _paths.Quarantine, nameof(FileStatus.Quarantined), cancellationToken);
            }

            if (format.ExtensionConflict)
            {
                record.CategoryId = "other";
                record.SubcategoryId = "other";
                record.Confidence = 0.20;
                record.Summary = "Формат содержимого не совпадает с расширением.";
                record.Reason = format.Warning;
                return await RouteToReviewAsync(record, cancellationToken);
            }

            var extracted = await _extractor.ExtractAsync(fullPath, format.FileType, cancellationToken);
            var local = _classifier.Classify(format, normalizedName, Path.GetExtension(normalizedName), extracted.Text);
            record.Status = nameof(FileStatus.Classifying);
            record.CategoryId = local.CategoryId;
            record.SubcategoryId = local.SubcategoryId;
            record.DetectedLanguage = local.DetectedLanguage;
            record.Confidence = local.Confidence;
            record.Summary = local.Summary;
            record.ProviderId = "local";

            if (extracted.Warning is not null && extracted.Text.Length == 0 &&
                format.FileType is "pdf" or "docx" or "xlsx" or "pptx")
            {
                record.Reason = extracted.Warning;
                return await RouteToReviewAsync(record, cancellationToken);
            }

            if (extracted.Text.Length > 0 && format.FileType is "text" or "pdf" or "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp")
            {
                var rule = await _repository.FindFileNameRuleAsync(normalizedName, cancellationToken);
                if (rule is not null)
                {
                    local = local with
                    {
                        CategoryId = rule.CategoryId,
                        SubcategoryId = rule.SubcategoryId,
                        Confidence = 0.98,
                        Summary = "Категория выбрана по сохранённому правилу для этого имени файла.",
                        ReasonCodes = ["USER_FILENAME_RULE"],
                        Candidates = [new ClassificationCandidate(rule.CategoryId, rule.SubcategoryId, 0.98, "USER_FILENAME_RULE")]
                    };
                    record.CategoryId = local.CategoryId;
                    record.SubcategoryId = local.SubcategoryId;
                    record.Confidence = local.Confidence;
                    record.Summary = local.Summary;
                    record.ProviderId = local.ProviderId;
                    record.Reason = "Применено пользовательское правило по точному имени файла.";
                }
            }

            var containsSecrets = FileSafety.ContainsSecrets(normalizedName, extracted.Text);
            string? cloudError = null;
            var settings = _settings();
            var visualAnalysisAllowed = AiFilePolicy.CanSendVisionImage(settings, fullPath, format.FileType);
            var wantsCloud = deepAnalysis || local.Confidence < settings.AutoSortConfidence || visualAnalysisAllowed;
            var ai = new CompositeAiFileAnalyzer(_settings(), (requestCount, token) =>
                _repository.TryReserveAiRequestsAsync(requestCount, _settings().MonthlyAiRequestLimit, token));
            var cloudTypeAllowed = AiFilePolicy.IsAllowed(settings, fullPath, format.FileType);
            byte[]? imageData = null;
            string? imagePreparationError = null;
            if (wantsCloud && visualAnalysisAllowed && !containsSecrets && cloudTypeAllowed && ai.IsConfigured)
            {
                (imageData, imagePreparationError) = await ReadVisionImageAsync(fullPath, cancellationToken);
            }

            var hasCloudContent = imageData is not null || extracted.Text.Length > 0 || deepAnalysis;
            if (visualAnalysisAllowed && imagePreparationError is not null)
                hasCloudContent = false;
            if (wantsCloud && !containsSecrets && cloudTypeAllowed && ai.IsConfigured && hasCloudContent)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    var aiResult = await ai.AnalyzeAsync(new FileAnalysisInput(normalizedName, Path.GetExtension(normalizedName),
                        format.MimeType, format.FileType, record.SizeBytes, extracted.Text, containsSecrets, local, imageData), cancellationToken);
                    timer.Stop();
                    await _repository.AddAnalysisAsync(new FileAnalysisRecord(Guid.NewGuid().ToString("N"), record.Id,
                        aiResult.ProviderId, "1.0.0", aiResult.Confidence, JsonSerializer.Serialize(aiResult),
                        timer.ElapsedMilliseconds, DateTime.UtcNow), cancellationToken);

                    var compareDisagreement = aiResult.ReasonCodes.Contains("PROVIDER_DISAGREEMENT", StringComparer.Ordinal) ||
                                              aiResult.ReasonCodes.Contains("COMPARISON_INCOMPLETE", StringComparer.Ordinal);
                    var imageSubcategoryRefinement = local.CategoryId == "images" && aiResult.CategoryId == "images" &&
                                                     local.SubcategoryId != aiResult.SubcategoryId;
                    var conflictsWithLocal = compareDisagreement || !imageSubcategoryRefinement &&
                        local.Confidence >= settings.ReviewConfidence && aiResult.Confidence >= 0.60 &&
                        (local.CategoryId != aiResult.CategoryId || local.SubcategoryId != aiResult.SubcategoryId);
                    if (conflictsWithLocal)
                    {
                        record.Confidence = Math.Min(0.79, Math.Max(local.Confidence, aiResult.Confidence));
                        record.Reason = compareDisagreement
                            ? "Провайдеры не согласовали категорию; требуется ручная проверка."
                            : "Локальный классификатор и ИИ предложили разные категории.";
                    }
                    else if (imageSubcategoryRefinement && aiResult.Confidence < settings.ReviewConfidence)
                    {
                        record.Confidence = 0.79;
                        record.Reason = "Визуальный результат слишком неопределённый; выберите подкатегорию вручную.";
                    }
                    else if (aiResult.Confidence >= local.Confidence || imageSubcategoryRefinement && aiResult.Confidence >= settings.ReviewConfidence)
                    {
                        record.CategoryId = aiResult.CategoryId;
                        record.SubcategoryId = aiResult.SubcategoryId;
                        record.DetectedFileType = aiResult.DetectedFileType;
                        record.DetectedLanguage = aiResult.DetectedLanguage;
                        record.Confidence = aiResult.Confidence;
                        record.Summary = aiResult.Summary;
                        record.ProviderId = aiResult.ProviderId;
                        record.Reason = null;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (AiRequestLimitExceededException exception)
                {
                    cloudError = exception.Message;
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or TaskCanceledException)
                {
                    cloudError = "Облачный анализ недоступен; использован локальный результат.";
                }
            }
            else if (containsSecrets && wantsCloud)
            {
                cloudError = "Облачный анализ отключён: в файле обнаружены вероятные секреты.";
            }
            else if (imagePreparationError is not null)
            {
                cloudError = imagePreparationError;
            }
            else if (wantsCloud && !cloudTypeAllowed)
            {
                cloudError = _settings().LocalOnlyPaths.Any(root => FileSafety.IsWithin(fullPath, root))
                    ? "Облачный анализ запрещён политикой приватной папки."
                    : "Облачный анализ запрещён для этого формата настройками приватности.";
            }
            else if (wantsCloud && !ai.IsConfigured)
            {
                cloudError = "Облачный анализ не настроен; использован локальный результат.";
            }

            if (record.Reason is null)
                record.Reason = cloudError ?? extracted.Warning;

            if (!string.Equals(await FileSafety.ComputeSha256Async(fullPath, cancellationToken), record.Sha256, StringComparison.OrdinalIgnoreCase))
                return await FailAsync(record, "Файл изменился во время анализа; перемещение отменено.", cancellationToken);

            if (record.Confidence >= _settings().AutoSortConfidence)
                return await RouteToCategoryAsync(record, cancellationToken);
            return await RouteToReviewAsync(record, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Each state transition is committed before the next filesystem step. Keep that
            // checkpoint intact so startup recovery can safely resume after shutdown/cancellation.
            throw;
        }
        catch (SqliteException)
        {
            // A database failure must not trigger a compensating file move; the last committed
            // checkpoint is recovered on the next start.
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            if (record is null || !pathLockAcquired)
                throw;
            return await FailAsync(record, SafeFailureReason(exception), cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (record is null || !pathLockAcquired)
                throw;
            return await FailAsync(record, SafeFailureReason(exception), cancellationToken);
        }
        finally
        {
            if (processLock is not null)
                await processLock.DisposeAsync();
        }
    }

    public async Task<FileRecord> ApproveAsync(string id, string categoryId, string subcategoryId,
        bool rememberFileNameRule = false, CancellationToken cancellationToken = default)
    {
        if (!CategoryCatalog.IsAllowed(categoryId, subcategoryId))
            throw new ArgumentException("Выбрана категория вне разрешённого справочника.");
        var file = await _repository.GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Запись не найдена.");
        if (file.Status != nameof(FileStatus.NeedsReview))
            throw new InvalidOperationException("Подтвердить можно только файл из очереди проверки.");
        await using var processLock = await AcquirePathLockAsync(file.CurrentPath, cancellationToken);
        if (!File.Exists(file.CurrentPath) || !FileSafety.IsWithin(file.CurrentPath, _paths.Review) || FileSafety.HasReparsePoint(file.CurrentPath))
            throw new InvalidOperationException("Файл проверки отсутствует или находится вне разрешённой папки.");
        if (await FileSafety.ComputeSha256Async(file.CurrentPath, cancellationToken) != file.Sha256)
            throw new InvalidOperationException("Хеш файла изменился; перемещение отменено.");

        file.CategoryId = categoryId;
        file.SubcategoryId = subcategoryId;
        file.Confidence = 1.0;
        file.Reason = "Категория подтверждена пользователем.";
        file.Status = nameof(FileStatus.Moving);
        await _repository.UpdateAsync(file, "Пользователь подтвердил категорию.", cancellationToken);
        var target = CategoryCatalog.GetPath(categoryId, subcategoryId);
        var directory = target.Subcategory is null ? Path.Combine(_paths.Sorted, target.Category) : Path.Combine(_paths.Sorted, target.Category, target.Subcategory);
        file.CurrentPath = await MoveSafelyAsync(file.CurrentPath, directory, _paths.Sorted, file.Sha256, cancellationToken,
            path => ReserveDestinationAsync(file, path, "Подготовлено перемещение после подтверждения.", cancellationToken));
        file.PendingDestinationPath = null;
        file.Status = nameof(FileStatus.Completed);
        file.CompletedAtUtc = DateTime.UtcNow;
        file.Reason = null;
        await _repository.UpdateAsync(file, "Файл перемещён после подтверждения пользователя.", cancellationToken);
        if (rememberFileNameRule)
            await _repository.SaveFileNameRuleAsync(file.OriginalName, categoryId, subcategoryId, cancellationToken);
        return file;
    }

    public async Task<FileRecord> SkipAsync(string id, CancellationToken cancellationToken = default)
    {
        var initial = await _repository.GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Запись не найдена.");
        await using var processLock = await AcquirePathLockAsync(initial.CurrentPath, cancellationToken);
        var file = await _repository.GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Запись не найдена.");
        if (!file.CurrentPath.Equals(initial.CurrentPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь файла изменился; обновите очередь проверки и повторите действие.");
        if (file.Status != nameof(FileStatus.NeedsReview))
            throw new InvalidOperationException("Не сортировать можно только файл из очереди проверки.");
        if (!File.Exists(file.CurrentPath) || !FileSafety.IsWithin(file.CurrentPath, _paths.Review) || FileSafety.HasReparsePoint(file.CurrentPath))
            throw new InvalidOperationException("Файл проверки отсутствует или находится вне разрешённой папки.");
        if (!string.Equals(await FileSafety.ComputeSha256Async(file.CurrentPath, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Хеш файла изменился; решение не сохранено.");

        file.Status = nameof(FileStatus.Skipped);
        file.CompletedAtUtc = DateTime.UtcNow;
        file.Reason = "Пользователь решил оставить файл несортированным в папке проверки.";
        await _repository.UpdateAsync(file, file.Reason, cancellationToken);
        return file;
    }

    public async Task<FileRecord> ReanalyzeAsync(string id, IReadOnlyCollection<string> allowedRoots, CancellationToken cancellationToken = default)
    {
        var file = await _repository.GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Запись не найдена.");
        if (file.Status is not (nameof(FileStatus.NeedsReview) or nameof(FileStatus.Completed) or nameof(FileStatus.Failed)))
            throw new InvalidOperationException("Повторный анализ доступен для готовых файлов, очереди проверки и ошибок.");
        var result = await ProcessAsync(file.CurrentPath, file.SourceType, allowedRoots, deepAnalysis: true, existing: file,
            cancellationToken: cancellationToken);
        return result.File;
    }

    public async Task<FileRecord> RevertAsync(string id, IReadOnlyCollection<string> allowedOriginalRoots,
        CancellationToken cancellationToken = default)
    {
        var file = await _repository.GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Запись не найдена.");
        if (file.Status is not (nameof(FileStatus.Completed) or nameof(FileStatus.NeedsReview) or nameof(FileStatus.Quarantined)))
            throw new InvalidOperationException("Отменить можно только завершённое перемещение.");
        if (!File.Exists(file.CurrentPath) || !FileSafety.IsWithin(file.CurrentPath, _paths.DataRoot) && !FileSafety.IsWithin(file.CurrentPath, _paths.Sorted) ||
            FileSafety.HasReparsePoint(file.CurrentPath))
            throw new InvalidOperationException("Файл результата отсутствует или находится вне разрешённых папок.");
        if (await FileSafety.ComputeSha256Async(file.CurrentPath, cancellationToken) != file.Sha256)
            throw new InvalidOperationException("Хеш файла изменился; отмена не выполнена.");
        if (!allowedOriginalRoots.Any(root => FileSafety.IsWithin(file.OriginalPath, root)))
            throw new InvalidOperationException("Исходный путь больше не разрешён.");
        await using var processLock = await AcquirePathLockAsync(file.CurrentPath, cancellationToken);
        var parent = Path.GetDirectoryName(file.OriginalPath) ?? throw new InvalidOperationException("Не удалось определить исходную папку.");
        if (!Directory.Exists(parent) || File.Exists(file.OriginalPath) || FileSafety.HasReparsePoint(parent))
            throw new IOException("Исходная папка недоступна или исходное имя уже занято.");

        var from = file.CurrentPath;
        file.Status = nameof(FileStatus.Moving);
        file.PendingDestinationPath = file.OriginalPath;
        await _repository.UpdateAsync(file, "Подготовлено восстановление исходного пути.", cancellationToken);
        File.Move(from, file.OriginalPath, overwrite: false);
        file.CurrentPath = file.OriginalPath;
        file.PendingDestinationPath = null;
        file.Status = nameof(FileStatus.Reverted);
        file.CompletedAtUtc = DateTime.UtcNow;
        file.Reason = null;
        await _repository.UpdateAsync(file, "Перемещение отменено; файл возвращён по исходному пути.", cancellationToken);
        return file;
    }

    public async Task<IReadOnlyList<FileRecord>> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _repository.GetPendingAsync(cancellationToken);
        var retry = new List<FileRecord>();
        foreach (var pendingFile in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = pendingFile;
            await using var processLock = await AcquirePathLockAsync(file.CurrentPath, cancellationToken);
            var latest = await _repository.GetAsync(file.Id, cancellationToken);
            if (latest is null || latest.Status is not (nameof(FileStatus.Detected) or nameof(FileStatus.Ready) or
                    nameof(FileStatus.Extracting) or nameof(FileStatus.Classifying) or nameof(FileStatus.Moving)))
                continue;
            file = latest;

            if (file.Status == nameof(FileStatus.Moving))
            {
                var reservedPath = !string.IsNullOrWhiteSpace(file.PendingDestinationPath) && File.Exists(file.PendingDestinationPath)
                    ? file.PendingDestinationPath
                    : file.CurrentPath;
                if (!File.Exists(reservedPath))
                {
                    if (File.Exists(file.CurrentPath))
                    {
                        if (file.CurrentPath.Equals(file.OriginalPath, StringComparison.OrdinalIgnoreCase))
                        {
                            file.Status = nameof(FileStatus.Reverted);
                            file.CompletedAtUtc = DateTime.UtcNow;
                        }
                        else if (FileSafety.IsWithin(file.CurrentPath, _paths.Sorted)) file.Status = nameof(FileStatus.Completed);
                        else if (FileSafety.IsWithin(file.CurrentPath, _paths.Review)) file.Status = nameof(FileStatus.NeedsReview);
                        else if (FileSafety.IsWithin(file.CurrentPath, _paths.Quarantine)) file.Status = nameof(FileStatus.Quarantined);
                        else if (FileSafety.IsWithin(file.CurrentPath, _paths.Errors)) file.Status = nameof(FileStatus.Failed);
                        else file.Status = nameof(FileStatus.Detected);
                        file.PendingDestinationPath = null;
                        if (file.Status is nameof(FileStatus.Completed) or nameof(FileStatus.Reverted)) file.CompletedAtUtc ??= DateTime.UtcNow;
                        await _repository.UpdateAsync(file, "Незавершённое перемещение восстановлено по исходному расположению.", cancellationToken);
                        if (file.Status == nameof(FileStatus.Detected)) retry.Add(file);
                        continue;
                    }

                    if (File.Exists(file.OriginalPath))
                    {
                        file.CurrentPath = file.OriginalPath;
                        file.PendingDestinationPath = null;
                        file.Status = nameof(FileStatus.Detected);
                        await _repository.UpdateAsync(file, "Исходный файл найден после прерывания; обработка продолжится.", cancellationToken);
                        retry.Add(file);
                        continue;
                    }

                    file.Status = nameof(FileStatus.Failed);
                    file.PendingDestinationPath = null;
                    file.Reason = "Незавершённый перенос не найден; требуется ручное восстановление.";
                    await _repository.UpdateAsync(file, "Файл не найден по текущему и зарезервированному путям.", cancellationToken);
                    continue;
                }

                if (!FileSafety.IsWithin(reservedPath, _paths.Sorted) && !FileSafety.IsWithin(reservedPath, _paths.Review) &&
                    !FileSafety.IsWithin(reservedPath, _paths.Quarantine) && !FileSafety.IsWithin(reservedPath, _paths.Errors) &&
                    !reservedPath.Equals(file.OriginalPath, StringComparison.OrdinalIgnoreCase))
                {
                    file.Status = nameof(FileStatus.Failed);
                    file.CurrentPath = reservedPath;
                    file.PendingDestinationPath = null;
                    file.Reason = "После прерывания найденный путь не относится к разрешённым рабочим папкам; файл оставлен на месте.";
                    await _repository.UpdateAsync(file, "Восстановление остановлено для неизвестного пути.", cancellationToken);
                    continue;
                }

                if (FileSafety.HasReparsePoint(reservedPath))
                {
                    file.Status = nameof(FileStatus.Failed);
                    file.PendingDestinationPath = null;
                    file.Reason = "После прерывания найден путь с символической ссылкой; автоматическое восстановление остановлено.";
                    await _repository.UpdateAsync(file, "Восстановление остановлено для reparse-пути.", cancellationToken);
                    continue;
                }

                var actualHash = await FileSafety.ComputeSha256Async(reservedPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(file.Sha256) && !string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    file.Status = nameof(FileStatus.Failed);
                    file.PendingDestinationPath = null;
                    file.Reason = "После прерывания путь назначения содержит другой файл; запись требует ручной проверки.";
                    await _repository.UpdateAsync(file, "Восстановление остановлено из-за несовпадения хеша.", cancellationToken);
                    continue;
                }

                if (!reservedPath.Equals(file.CurrentPath, StringComparison.OrdinalIgnoreCase) && File.Exists(file.CurrentPath) &&
                    !FileSafety.HasReparsePoint(file.CurrentPath) &&
                    (FileSafety.IsWithin(file.CurrentPath, _paths.DataRoot) || _settings().WatchFolders.Any(root => FileSafety.IsWithin(file.CurrentPath, root))))
                {
                    var sourceHash = await FileSafety.ComputeSha256Async(file.CurrentPath, cancellationToken);
                    if (string.Equals(sourceHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        File.Delete(file.CurrentPath);
                }

                file.Sha256 = actualHash;
                file.CurrentPath = reservedPath;
                file.PendingDestinationPath = null;
                if (FileSafety.IsWithin(reservedPath, _paths.Sorted))
                {
                    file.Status = nameof(FileStatus.Completed);
                    file.CompletedAtUtc = DateTime.UtcNow;
                }
                else if (FileSafety.IsWithin(reservedPath, _paths.Review))
                {
                    file.Status = nameof(FileStatus.NeedsReview);
                    file.CompletedAtUtc = null;
                }
                else if (FileSafety.IsWithin(reservedPath, _paths.Quarantine))
                {
                    file.Status = nameof(FileStatus.Quarantined);
                    file.CompletedAtUtc = null;
                }
                else if (FileSafety.IsWithin(reservedPath, _paths.Errors))
                {
                    file.Status = nameof(FileStatus.Failed);
                    file.CompletedAtUtc = null;
                }
                else if (reservedPath.Equals(file.OriginalPath, StringComparison.OrdinalIgnoreCase))
                {
                    file.Status = nameof(FileStatus.Reverted);
                    file.CompletedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    file.Status = nameof(FileStatus.Failed);
                    file.Reason = "После прерывания путь не относится к разрешённым рабочим папкам.";
                }

                if (file.Status is nameof(FileStatus.Completed) or nameof(FileStatus.NeedsReview) or nameof(FileStatus.Quarantined) or nameof(FileStatus.Reverted))
                    file.Reason = null;
                await _repository.UpdateAsync(file, "Незавершённое перемещение восстановлено по пути и SHA-256.", cancellationToken);
                continue;
            }

            var source = File.Exists(file.CurrentPath) ? file.CurrentPath : file.OriginalPath;
            if (File.Exists(source) && !FileSafety.HasReparsePoint(source))
            {
                file.CurrentPath = source;
                file.Status = nameof(FileStatus.Detected);
                file.Reason = null;
                await _repository.UpdateAsync(file, "Обработка продолжится после перезапуска приложения.", cancellationToken);
                retry.Add(file);
            }
            else
            {
                file.Status = nameof(FileStatus.Failed);
                file.Reason = "Незавершённая операция не найдена ни по текущему, ни по исходному пути.";
                await _repository.UpdateAsync(file, "Не удалось автоматически восстановить исходный файл.", cancellationToken);
            }
        }
        return retry;
    }

    private async Task<ProcessingOutcome> RouteToCategoryAsync(FileRecord record, CancellationToken cancellationToken)
    {
        var target = CategoryCatalog.GetPath(record.CategoryId, record.SubcategoryId);
        var folder = target.Subcategory is null ? Path.Combine(_paths.Sorted, target.Category) : Path.Combine(_paths.Sorted, target.Category, target.Subcategory);
        var message = $"Файл находится в {target.Category}{(target.Subcategory is null ? string.Empty : "/" + target.Subcategory)}.";
        if (IsInExactDirectory(record.CurrentPath, folder))
        {
            record.PendingDestinationPath = null;
            record.Status = nameof(FileStatus.Completed);
            record.CompletedAtUtc = DateTime.UtcNow;
            await _repository.UpdateAsync(record, "Повторный анализ подтвердил текущую категорию; файл не перемещался.", cancellationToken);
            return new(record, false, message);
        }

        record.Status = nameof(FileStatus.Moving);
        await _repository.UpdateAsync(record, "Уверенность достигла порога автосортировки.", cancellationToken);
        record.CurrentPath = await MoveSafelyAsync(record.CurrentPath, folder, _paths.Sorted, record.Sha256, cancellationToken,
            path => ReserveDestinationAsync(record, path, "Подготовлено перемещение в отсортированную папку.", cancellationToken));
        record.PendingDestinationPath = null;
        record.Status = nameof(FileStatus.Completed);
        record.CompletedAtUtc = DateTime.UtcNow;
        message = $"Файл перемещён в {target.Category}{(target.Subcategory is null ? string.Empty : "/" + target.Subcategory)}.";
        await _repository.UpdateAsync(record, message, cancellationToken);
        return new(record, true, message);
    }

    private async Task<ProcessingOutcome> RouteToReviewAsync(FileRecord record, CancellationToken cancellationToken)
    {
        var target = CategoryCatalog.GetPath(record.CategoryId, record.SubcategoryId);
        var reviewFolder = record.Confidence < _settings().ReviewConfidence
            ? Path.Combine(_paths.Review, "Unknown")
            : target.Subcategory is null ? Path.Combine(_paths.Review, target.Category) : Path.Combine(_paths.Review, target.Category, target.Subcategory);
        if (IsInExactDirectory(record.CurrentPath, reviewFolder))
        {
            record.PendingDestinationPath = null;
            record.Status = nameof(FileStatus.NeedsReview);
            record.CompletedAtUtc = null;
            var existingMessage = record.Reason ?? "Требуется подтверждение категории.";
            await _repository.UpdateAsync(record, "Повторный анализ подтвердил текущую очередь проверки; файл не перемещался.", cancellationToken);
            return new(record, false, existingMessage);
        }

        record.Status = nameof(FileStatus.Moving);
        await _repository.UpdateAsync(record, "Результат требует ручной проверки; сохранение в очередь.", cancellationToken);
        record.CurrentPath = await MoveSafelyAsync(record.CurrentPath, reviewFolder, _paths.Review, record.Sha256, cancellationToken,
            path => ReserveDestinationAsync(record, path, "Подготовлено перемещение в очередь проверки.", cancellationToken));
        record.PendingDestinationPath = null;
        record.Status = nameof(FileStatus.NeedsReview);
        var message = record.Reason ?? "Требуется подтверждение категории.";
        await _repository.UpdateAsync(record, message, cancellationToken);
        return new(record, true, message);
    }

    private async Task<ProcessingOutcome> RouteWithoutAnalysisAsync(FileRecord record, string root, string status,
        CancellationToken cancellationToken)
    {
        record.Status = nameof(FileStatus.Moving);
        await _repository.UpdateAsync(record, record.Reason ?? "Файл подготовлен к карантину.", cancellationToken);
        record.CurrentPath = await MoveSafelyAsync(record.CurrentPath, root, root, record.Sha256, cancellationToken,
            path => ReserveDestinationAsync(record, path, "Подготовлено безопасное помещение файла.", cancellationToken));
        record.PendingDestinationPath = null;
        record.Status = status;
        await _repository.UpdateAsync(record, record.Reason ?? "Файл помещён в карантин.", cancellationToken);
        return new(record, true, record.Reason ?? "Файл помещён в карантин.");
    }

    private async Task<ProcessingOutcome> FailAsync(FileRecord record, string reason, CancellationToken cancellationToken)
    {
        record.Status = nameof(FileStatus.Moving);
        record.Reason = reason;
        record.CompletedAtUtc = null;
        if (File.Exists(record.CurrentPath) && !FileSafety.HasReparsePoint(record.CurrentPath))
        {
            var source = record.CurrentPath;
            try
            {
                if (string.IsNullOrWhiteSpace(record.Sha256))
                    record.Sha256 = await FileSafety.ComputeSha256Async(source, cancellationToken);
                if (FileSafety.IsWithin(source, _paths.DataRoot) || FileSafety.IsWithin(source, _paths.Sorted) ||
                    _settings().WatchFolders.Any(root => FileSafety.IsWithin(source, root)))
                {
                    await _repository.UpdateAsync(record, "Подготовлено сохранение файла после ошибки.", cancellationToken);
                    record.CurrentPath = await MoveSafelyAsync(source, _paths.Errors, _paths.Errors, record.Sha256, cancellationToken,
                        path => ReserveDestinationAsync(record, path, "Подготовлено перемещение файла с ошибкой.", cancellationToken));
                    record.PendingDestinationPath = null;
                }
            }
            catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
            {
                record.CurrentPath = source;
                record.PendingDestinationPath = null;
                reason += " Файл оставлен в исходной папке.";
                record.Reason = reason;
            }
        }
        record.Status = nameof(FileStatus.Failed);
        await _repository.UpdateAsync(record, reason, cancellationToken);
        return new(record, false, reason);
    }

    private async Task<string> MoveSafelyAsync(string sourcePath, string destinationFolder, string allowedDestinationRoot,
        string expectedHash, CancellationToken cancellationToken, Func<string, Task>? reserveDestination = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationFolder);
        if (!File.Exists(source)) throw new FileNotFoundException("Исходный файл отсутствует.");
        if (!FileSafety.IsWithin(destination, allowedDestinationRoot))
            throw new UnauthorizedAccessException("Путь назначения находится вне разрешённого корня.");
        if (FileSafety.HasReparsePoint(source) || FileSafety.HasReparsePoint(destination))
            throw new UnauthorizedAccessException("Перемещение через символическую ссылку запрещено.");

        Directory.CreateDirectory(destination);
        var safeName = FileSafety.SanitizeFileName(Path.GetFileName(source));
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 0; index < 10_000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = index == 0 ? safeName : $"{stem} ({index}){extension}";
            var candidate = Path.Combine(destination, name);
            if (!FileSafety.IsWithin(candidate, allowedDestinationRoot))
                throw new UnauthorizedAccessException("Путь назначения вышел за разрешённый корень.");
            if (File.Exists(candidate)) continue;
            if (FileSafety.HasReparsePoint(destination))
                throw new UnauthorizedAccessException("Папка назначения связана с символической ссылкой.");
            if (reserveDestination is not null)
                await reserveDestination(candidate);

            try
            {
                File.Move(source, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                continue;
            }
            catch (IOException)
            {
                // Cross-volume moves are completed by copying into a temporary target and verifying the hash.
                var temporary = Path.Combine(destination, $".optium-{Guid.NewGuid():N}.tmp");
                try
                {
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                                     128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                     128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await input.CopyToAsync(output, 128 * 1024, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }

                    var sourceHash = string.IsNullOrWhiteSpace(expectedHash)
                        ? await FileSafety.ComputeSha256Async(source, cancellationToken)
                        : expectedHash;
                    if (!string.Equals(await FileSafety.ComputeSha256Async(temporary, cancellationToken), sourceHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Хеш копии не совпадает с хешем исходного файла.");
                    if (!string.Equals(await FileSafety.ComputeSha256Async(source, cancellationToken), sourceHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Исходный файл изменился во время копирования.");
                    File.Move(temporary, candidate, overwrite: false);
                    File.Delete(source);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                    throw;
                }
            }
        }

        throw new IOException("Не удалось подобрать свободное имя файла назначения.");
    }

    private async Task ReserveDestinationAsync(FileRecord record, string candidate, string eventDetails, CancellationToken cancellationToken)
    {
        record.PendingDestinationPath = candidate;
        await _repository.UpdateAsync(record, eventDetails, cancellationToken);
    }

    private static bool IsInExactDirectory(string filePath, string directoryPath) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(filePath)), Path.GetFullPath(directoryPath), StringComparison.OrdinalIgnoreCase);

    private async Task<FileStream> AcquirePathLockAsync(string path, CancellationToken cancellationToken)
    {
        var lockFolder = Path.Combine(Path.GetDirectoryName(_paths.Database)!, "locks");
        Directory.CreateDirectory(lockFolder);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant();
        var lockPath = Path.Combine(lockFolder, key + ".lock");
        for (var attempt = 0; attempt < 600; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 599)
            {
                await Task.Delay(250, cancellationToken);
            }
        }
        throw new IOException("Не удалось получить блокировку обработки файла.");
    }

    private static async Task<(byte[]? Data, string? Error)> ReadVisionImageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0)
                return (null, "Изображение пустое; визуальный облачный анализ пропущен.");
            if (stream.Length > AiFilePolicy.MaximumVisionImageBytes)
                return (null, $"Изображение превышает лимит облачного анализа {AiFilePolicy.MaximumVisionImageBytes / 1024 / 1024} МБ; локальная сортировка продолжена.");

            var data = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(data.AsMemory(), cancellationToken);
            var extra = new byte[1];
            if (await stream.ReadAsync(extra.AsMemory(), cancellationToken) != 0)
                return (null, "Изображение изменилось при подготовке; облачный анализ пропущен.");
            return (data, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, "Изображение не удалось безопасно подготовить для облачного анализа; локальная сортировка продолжена.");
        }
    }

    private static string SafeFailureReason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Путь не прошёл проверку безопасности или у приложения нет доступа.",
        FileNotFoundException => "Исходный файл не найден; операция сохранена в журнале.",
        InvalidDataException => "Структура файла повреждена или не поддерживается.",
        _ => "Ошибка обработки. Файл сохранён; подробности доступны в журнале событий."
    };
}
