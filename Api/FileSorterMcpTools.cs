using System.ComponentModel;
using System.Text.Json;
using OptiumPlusFileManager.Domain;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace OptiumPlusFileManager.Api;

[McpServerToolType]
public sealed class FileSorterMcpTools(FileSorterRuntime runtime, ApiTokenStore tokens, IHttpContextAccessor contextAccessor,
    LocalApiServer apiServer)
{
    [McpServerTool(Name = "list_files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Показывает недавние файлы и их статус. Можно ограничить выборку статусом и числом записей.")]
    public async Task<string> ListFiles(
        [Description("Статус для фильтрации или пустая строка для всех статусов.")] string status,
        [Description("Категория для фильтрации или пустая строка для всех категорий.")] string categoryId,
        [Description("Источник watch, manual, api или mcp либо пустая строка для всех источников.")] string sourceType,
        [Description("Максимум записей от 1 до 1000.")] int take,
        CancellationToken cancellationToken)
    {
        EnsureRead();
        var files = await runtime.ListFilesAsync(string.IsNullOrWhiteSpace(status) ? null : status,
            take is < 1 or > 1000 ? 100 : take, cancellationToken,
            categoryId: string.IsNullOrWhiteSpace(categoryId) ? null : categoryId,
            sourceType: string.IsNullOrWhiteSpace(sourceType) ? null : sourceType);
        return JsonSerializer.Serialize(files.Select(ApiModelMapper.ToDto));
    }

    [McpServerTool(Name = "get_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Возвращает метаданные, распознанную категорию и текущий статус файла по его ID.")]
    public async Task<string> GetFile([Description("UUID записи файла.")] string fileId, CancellationToken cancellationToken)
    {
        EnsureRead();
        var file = await runtime.GetFileAsync(fileId, cancellationToken);
        return file is null ? "Файл не найден." : JsonSerializer.Serialize(ApiModelMapper.ToDto(file));
    }

    [McpServerTool(Name = "submit_local_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Копирует файл из разрешённой наблюдаемой входящей папки в защищённый шлюз приложения и ставит его в очередь.")]
    public async Task<string> SubmitLocalFile([Description("Полный путь внутри одной из разрешённых входящих папок.")] string path,
        CancellationToken cancellationToken)
    {
        EnsureWrite();
        if (!runtime.IsAllowedLocalInput(path))
            throw new UnauthorizedAccessException("Путь не входит в разрешённые наблюдаемые папки.");
        var file = await runtime.IngestFileAsync(path, "mcp", cancellationToken);
        return JsonSerializer.Serialize(ApiModelMapper.ToDto(file));
    }

    [McpServerTool(Name = "create_upload_ticket", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Создаёт одноразовую ссылку на загрузку файла. Ссылка действует 15 минут и принимает только одну загрузку.")]
    public string CreateUploadTicket()
    {
        EnsureWrite();
        var ticket = apiServer.CreateUploadLink();
        return JsonSerializer.Serialize(new CreateTicketResponse(ticket.Url, ticket.ExpiresAtUtc));
    }

    [McpServerTool(Name = "put_text_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Создаёт небольшой UTF-8 текстовый файл (до 1 МБ) и передаёт его на локальную классификацию.")]
    public async Task<string> PutTextFile([Description("Безопасное имя нового файла.")] string fileName,
        [Description("Содержимое нового файла.")] string content, CancellationToken cancellationToken)
    {
        EnsureWrite();
        var file = await runtime.IngestTextAsync(fileName, content, "mcp", cancellationToken);
        return JsonSerializer.Serialize(ApiModelMapper.ToDto(file));
    }

    [McpServerTool(Name = "get_download_link", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Создаёт одноразовую ссылку на скачивание уже проверенного файла; ссылка действует 5 минут.")]
    public async Task<string> GetDownloadLink([Description("UUID записи файла.")] string fileId, CancellationToken cancellationToken)
    {
        EnsureRead();
        var file = await runtime.GetFileAsync(fileId, cancellationToken) ?? throw new FileNotFoundException("Файл не найден.");
        if (file.Status is not (nameof(FileStatus.Completed) or nameof(FileStatus.NeedsReview)))
            throw new UnauthorizedAccessException("Скачать можно только готовый файл или файл очереди проверки.");
        var ticket = apiServer.CreateDownloadLink(fileId);
        return JsonSerializer.Serialize(new DownloadLinkResponse(ticket.Url, ticket.ExpiresAtUtc));
    }

    [McpServerTool(Name = "request_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Запускает повторный глубокий анализ файла. Если облачный ИИ выключен или недоступен, локальная очередь сохраняется.")]
    public async Task<string> RequestAnalysis([Description("UUID записи файла.")] string fileId, CancellationToken cancellationToken)
    {
        EnsureWrite();
        return JsonSerializer.Serialize(ApiModelMapper.ToDto(await runtime.ReanalyzeAsync(fileId, cancellationToken)));
    }

    [McpServerTool(Name = "suggest_category", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Возвращает уже сохранённое предложение категории и список фиксированных допустимых категорий без перемещения файла.")]
    public async Task<string> SuggestCategory([Description("UUID записи файла.")] string fileId, CancellationToken cancellationToken)
    {
        EnsureRead();
        var file = await runtime.GetFileAsync(fileId, cancellationToken) ?? throw new FileNotFoundException("Файл не найден.");
        return JsonSerializer.Serialize(new
        {
            file.Id, file.CategoryId, file.SubcategoryId, file.Confidence, file.Summary, file.Reason,
            allowedCategories = CategoryCatalog.AllChoices().Select(choice => new
            {
                choice.CategoryId, choice.SubcategoryId, choice.DisplayName
            })
        });
    }

    [McpServerTool(Name = "approve_classification", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Подтверждает категорию файла из очереди ручной проверки и перемещает его только в разрешённую папку категории.")]
    public async Task<string> ApproveClassification([Description("UUID записи из очереди проверки.")] string fileId,
        [Description("Идентификатор категории из справочника.")] string categoryId,
        [Description("Идентификатор подкатегории из справочника.")] string subcategoryId,
        [Description("Сохранить точное правило для файлов с таким же именем.")] bool rememberFileNameRule,
        CancellationToken cancellationToken)
    {
        EnsureWrite();
        return JsonSerializer.Serialize(ApiModelMapper.ToDto(await runtime.ApproveAsync(fileId, categoryId, subcategoryId, rememberFileNameRule, cancellationToken)));
    }

    [McpServerTool(Name = "move_file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Перемещает файл очереди проверки в одну из фиксированных разрешённых категорий. Пути назначения от агента не принимаются.")]
    public async Task<string> MoveFile([Description("UUID записи из очереди проверки.")] string fileId,
        [Description("Идентификатор разрешённой категории.")] string categoryId,
        [Description("Идентификатор разрешённой подкатегории.")] string subcategoryId,
        [Description("Сохранить точное правило для файлов с таким же именем.")] bool rememberFileNameRule,
        CancellationToken cancellationToken)
    {
        EnsureWrite();
        return JsonSerializer.Serialize(ApiModelMapper.ToDto(await runtime.ApproveAsync(fileId, categoryId, subcategoryId, rememberFileNameRule, cancellationToken)));
    }

    private void EnsureRead()
    {
        var context = contextAccessor.HttpContext;
        if (context is not null && !tokens.HasRead(ApiTokenStore.ReadBearer(context.Request)))
            throw new UnauthorizedAccessException("Для чтения требуется read-токен.");
    }

    private void EnsureWrite()
    {
        var context = contextAccessor.HttpContext;
        if (context is not null && !tokens.HasWrite(ApiTokenStore.ReadBearer(context.Request)))
            throw new UnauthorizedAccessException("Для изменения данных требуется write-токен.");
    }
}
