using System.Net;
using System.Text.Json;
using OptiumPlusFileManager.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace OptiumPlusFileManager.Api;

public sealed class LocalApiServer : IAsyncDisposable
{
    private readonly FileSorterRuntime _runtime;
    private readonly ApiTokenStore _tokens;
    private readonly UploadTicketStore _uploadTickets;
    private readonly DownloadTicketStore _downloadTickets;
    private readonly bool _dynamicPort;
    private readonly string? _publicBaseAddress;
    private WebApplication? _application;
    private string _baseAddress;

    public LocalApiServer(FileSorterRuntime runtime, ApiTokenStore tokens, bool dynamicPort = false,
        UploadTicketStore? uploadTickets = null, DownloadTicketStore? downloadTickets = null)
    {
        _runtime = runtime;
        _tokens = tokens;
        _dynamicPort = dynamicPort;
        _uploadTickets = uploadTickets ?? new UploadTicketStore();
        _downloadTickets = downloadTickets ?? new DownloadTicketStore();
        _publicBaseAddress = ValidatePublicBaseAddress(Environment.GetEnvironmentVariable("FILE_SORTER_PUBLIC_BASE_URL"));
        _baseAddress = _publicBaseAddress ??
                       $"http://127.0.0.1:{(_dynamicPort ? 0 : Port)}";
    }

    public const int Port = 37582;
    public string BaseAddress => _publicBaseAddress ?? _baseAddress;
    public UploadTicketStore UploadTickets => _uploadTickets;
    public DownloadTicketStore DownloadTickets => _downloadTickets;
    public bool IsRunning => _application is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null) return;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost;[::1]";
        builder.WebHost.UseUrls($"http://127.0.0.1:{(_dynamicPort ? 0 : Port)}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024 + 1024 * 1024);
        builder.Services.AddSingleton(_runtime);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(this);
        builder.Services.AddSingleton(_uploadTickets);
        builder.Services.AddSingleton(_downloadTickets);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<FileSorterMcpTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var token = ApiTokenStore.ReadBearer(context.Request);
            if (path.StartsWithSegments("/api/downloads") || path.StartsWithSegments("/api/uploads"))
            {
                await next();
                return;
            }

            if (path.StartsWithSegments("/mcp"))
            {
                if (!_tokens.HasRead(token))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
                return;
            }

            if (path.StartsWithSegments("/api"))
            {
                var authorized = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                    ? _tokens.HasRead(token)
                    : _tokens.HasWrite(token);
                if (!authorized)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Требуется токен с соответствующими правами." }, cancellationToken);
                    return;
                }
            }
            await next();
        });

        MapRoutes(app);
        app.MapMcp("/mcp");
        await app.StartAsync(cancellationToken);
        if (_dynamicPort)
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.FirstOrDefault(value => value.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            if (address is not null) _baseAddress = address.TrimEnd('/');
        }
        _application = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_application is null) return;
        await _application.StopAsync(cancellationToken);
        await _application.DisposeAsync();
        _application = null;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", loopback = true }));
        app.MapGet("/api/stats", async (CancellationToken ct) => Results.Ok(await _runtime.GetStatsAsync(ct)));
        app.MapGet("/api/files", async (string? status, string? categoryId, string? sourceType, DateTime? createdAfterUtc,
            int? take, int? skip, CancellationToken ct) =>
        {
            var files = await _runtime.ListFilesAsync(status, take ?? 100, ct, skip ?? 0, categoryId, sourceType, createdAfterUtc);
            return Results.Ok(files.Select(ApiModelMapper.ToDto));
        });
        app.MapGet("/api/files/{id}", async (string id, CancellationToken ct) =>
        {
            var file = await _runtime.GetFileAsync(id, ct);
            return file is null ? Results.NotFound() : Results.Ok(ApiModelMapper.ToDto(file));
        });
        app.MapGet("/api/files/{id}/events", async (string id, CancellationToken ct) =>
        {
            if (await _runtime.GetFileAsync(id, ct) is null) return Results.NotFound();
            return Results.Ok(await _runtime.GetEventsAsync(id, ct));
        });
        app.MapGet("/api/jobs/{id}", async (string id, CancellationToken ct) =>
        {
            var file = await _runtime.GetFileAsync(id, ct);
            return file is null ? Results.NotFound() : Results.Ok(new { id = file.Id, status = file.Status, reason = file.Reason });
        });
        app.MapGet("/api/providers/health", () => Results.Ok(_runtime.GetProviderHealth()));
        app.MapGet("/api/categories", () => Results.Ok(CategoryCatalog.AllChoices().Select(item => new
        {
            categoryId = item.CategoryId, subcategoryId = item.SubcategoryId, name = item.DisplayName
        })));

        app.MapPost("/api/files", async (HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var file = await ReceiveFileAsync(context, ct);
                return Results.Created($"/api/files/{file.Id}", ApiModelMapper.ToDto(file));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data с одним файлом или бинарный запрос с заголовком X-File-Name." });
            }
        });
        app.MapPost("/api/upload-tickets", () =>
        {
            var ticket = _uploadTickets.Create();
            return Results.Ok(new CreateTicketResponse($"{BaseAddress}/api/uploads/{ticket.Token}", ticket.ExpiresAtUtc));
        });
        app.MapPut("/api/uploads/{token}", async (string token, HttpContext context, CancellationToken ct) =>
        {
            if (!_uploadTickets.TryConsume(token)) return Results.NotFound(new { error = "Ссылка истекла или уже использована." });
            var name = context.Request.Headers["X-File-Name"].ToString();
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Укажите X-File-Name." });
            try
            {
                var file = await _runtime.IngestStreamAsync(context.Request.Body, name, "api", ct);
                return Results.Created($"/api/files/{file.Id}", ApiModelMapper.ToDto(file));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        });
        app.MapPost("/api/files/text", async (PutTextFileRequest request, CancellationToken ct) =>
        {
            try
            {
                var file = await _runtime.IngestTextAsync(request.FileName, request.Content, "api", ct);
                return Results.Created($"/api/files/{file.Id}", ApiModelMapper.ToDto(file));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        });
        app.MapGet("/api/files/{id}/content", async (string id, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var file = await _runtime.GetFileAsync(id, ct);
                if (file is null) return Results.NotFound();
                var stream = await _runtime.OpenAllowedFileAsync(id, ct);
                return Results.File(stream, file.DetectedMimeType, file.OriginalName, enableRangeProcessing: false);
            }
            catch (UnauthorizedAccessException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict); }
        });
        app.MapPost("/api/files/{id}/analyze", async (string id, CancellationToken ct) =>
        {
            try { return Results.Ok(ApiModelMapper.ToDto(await _runtime.ReanalyzeAsync(id, ct))); }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict); }
        });
        app.MapPost("/api/files/{id}/approve", async (string id, ApproveFileRequest request, CancellationToken ct) =>
        {
            try { return Results.Ok(ApiModelMapper.ToDto(await _runtime.ApproveAsync(id, request.CategoryId, request.SubcategoryId, request.RememberFileNameRule, ct))); }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict); }
        });
        app.MapPost("/api/files/{id}/revert", async (string id, CancellationToken ct) =>
        {
            try { return Results.Ok(ApiModelMapper.ToDto(await _runtime.RevertAsync(id, ct))); }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict); }
            catch (IOException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict); }
        });
        app.MapGet("/api/downloads/{token}", async (string token, CancellationToken ct) =>
        {
            if (!_downloadTickets.TryConsume(token, out var fileId)) return Results.NotFound();
            try
            {
                var file = await _runtime.GetFileAsync(fileId, ct);
                if (file is null) return Results.NotFound();
                var stream = await _runtime.OpenAllowedFileAsync(fileId, ct);
                return Results.File(stream, file.DetectedMimeType, file.OriginalName, enableRangeProcessing: false);
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
            catch (InvalidOperationException) { return Results.Conflict(); }
        });
    }

    private async Task<FileRecord> ReceiveFileAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            var contentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
            var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
            if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 256)
                throw new InvalidDataException("Некорректная граница multipart-запроса.");
            var reader = new MultipartReader(boundary, context.Request.Body)
            {
                BodyLengthLimit = 2L * 1024 * 1024 * 1024
            };
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                if (!disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrWhiteSpace(disposition.FileName.Value) && string.IsNullOrWhiteSpace(disposition.FileNameStar.Value))) continue;
                var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                return await _runtime.IngestStreamAsync(section.Body, name ?? "upload.bin", "api", cancellationToken);
            }
            throw new InvalidDataException("В multipart-запросе нет файла.");
        }

        var rawName = context.Request.Headers["X-File-Name"].ToString();
        if (string.IsNullOrWhiteSpace(rawName))
            throw new InvalidDataException("Для бинарной загрузки укажите X-File-Name.");
        return await _runtime.IngestStreamAsync(context.Request.Body, rawName, "api", cancellationToken);
    }

    public (string Url, DateTime ExpiresAtUtc) CreateDownloadLink(string fileId)
    {
        var ticket = _downloadTickets.Create(fileId);
        return ($"{BaseAddress}/api/downloads/{ticket.Token}", ticket.ExpiresAtUtc);
    }

    public (string Url, DateTime ExpiresAtUtc) CreateUploadLink()
    {
        var ticket = _uploadTickets.Create();
        return ($"{BaseAddress}/api/uploads/{ticket.Token}", ticket.ExpiresAtUtc);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static string? ValidatePublicBaseAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.IsLoopback && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("FILE_SORTER_PUBLIC_BASE_URL должен быть loopback HTTP или публичным HTTPS-адресом без учётных данных и параметров.");
        return uri.ToString().TrimEnd('/');
    }
}

public sealed record PutTextFileRequest(string FileName, string Content);

public static class McpStdioServer
{
    public static async Task RunAsync(FileSorterRuntime runtime, CancellationToken cancellationToken = default)
    {
        var tokens = new ApiTokenStore();
        await using var api = new LocalApiServer(runtime, tokens, dynamicPort: true);
        await api.StartAsync(cancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(api);
        builder.Services.AddSingleton(api.UploadTickets);
        builder.Services.AddSingleton(api.DownloadTickets);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<FileSorterMcpTools>();
        await builder.Build().RunAsync(cancellationToken);
    }
}
