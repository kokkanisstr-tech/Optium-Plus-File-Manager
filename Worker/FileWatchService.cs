using System.Collections.Concurrent;
using System.Threading.Channels;
using OptiumPlusFileManager.Domain;
using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Infrastructure;

namespace OptiumPlusFileManager.Worker;

public sealed class FileWatchService : IAsyncDisposable
{
    private readonly FileSorterRuntime _runtime;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<string, byte> _scheduled = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _lifetime;
    private Task[] _workers = [];

    public FileWatchService(FileSorterRuntime runtime) => _runtime = runtime;

    public event Action<string>? StatusChanged;

    public async Task ConfigureAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        var settings = _runtime.Settings;
        if (!settings.AutomaticSorting) return;

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workers = [WorkerAsync(_lifetime.Token), WorkerAsync(_lifetime.Token), ReconcileAsync(_lifetime.Token)];

        foreach (var folder in settings.WatchFolders)
        {
            if (!Directory.Exists(folder))
                continue;
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = settings.WatchSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false
            };
            watcher.Created += (_, args) => QueueIncoming(args.FullPath);
            watcher.Changed += (_, args) => QueueIncoming(args.FullPath);
            watcher.Renamed += (_, args) => QueueIncoming(args.FullPath);
            watcher.Error += (_, args) => StatusChanged?.Invoke($"События наблюдателя потеряны; будет выполнена сверка папки: {args.GetException().GetType().Name}.");
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        QueueExistingFiles();
        StatusChanged?.Invoke($"Автосортировка включена для папок: {settings.WatchFolders.Count}.");
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        StopWatchers();
        if (_lifetime is null) return;
        _lifetime.Cancel();
        try { await Task.WhenAll(_workers); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _lifetime = null;
        _workers = [];
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private void QueueIncoming(string path)
    {
        if (FileTypeClassifier.IsTemporary(path) || _runtime.IsExcludedPath(path) || !File.Exists(path))
            return;
        var fullPath = Path.GetFullPath(path);
        if (_scheduled.TryAdd(fullPath, 0))
        {
            if (!_queue.Writer.TryWrite(fullPath))
            {
                _scheduled.TryRemove(fullPath, out _);
                StatusChanged?.Invoke("Очередь временно заполнена; пропущенные файлы будут найдены при следующей сверке.");
            }
        }
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (var path in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var outcome = await _runtime.ProcessWatchedFileAsync(path, cancellationToken);
                if (outcome.WasMoved)
                    StatusChanged?.Invoke($"«{Path.GetFileName(outcome.File.CurrentPath)}»: {outcome.Message}");
                else
                    StatusChanged?.Invoke(outcome.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                StatusChanged?.Invoke($"Автоматическая обработка завершилась ошибкой: {exception.GetType().Name}.");
            }
            finally
            {
                _scheduled.TryRemove(path, out _);
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            QueueExistingFiles();
    }

    private void QueueExistingFiles()
    {
        var settings = _runtime.Settings;
        foreach (var root in settings.WatchFolders)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", settings.WatchSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    if (!FileTypeClassifier.IsTemporary(file) && !_runtime.IsExcludedPath(file))
                        QueueIncoming(file);
            }
            catch (IOException exception) { StatusChanged?.Invoke($"Сверка входящей папки пропущена: {exception.GetType().Name}."); }
            catch (UnauthorizedAccessException) { StatusChanged?.Invoke("Сверка входящей папки пропущена из-за отсутствия доступа."); }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
