using System.Collections.Concurrent;

namespace Homework.Core.Scanning;

/// <summary>
/// 监听一个收件文件夹。扫描仪通过 SMB / 本地保存把文件写进来，这里等文件写完、
/// 能稳定读取后再通知上层导入。事件在线程池线程触发，UI 层需要自行切回界面线程。
/// </summary>
public sealed class ScanInboxWatcher : IDisposable
{
    public const int DefaultMaxSeenEntries = 4096;

    private readonly FileSystemWatcher _watcher;
    private readonly object _seenLock = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _seenOrder = new();
    private readonly int _maxSeenEntries;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _settleDelay;
    private readonly TimeSpan _maxWait;
    private volatile bool _disposed;

    /// <summary>文件已写完、可以导入。参数为完整路径。</summary>
    public event Action<string>? FileReady;

    /// <summary>等待超时或文件一直读不了。参数为完整路径与中文原因。</summary>
    public event Action<string, string>? FileFailed;

    public ScanInboxWatcher(
        string folder,
        TimeSpan? settleDelay = null,
        TimeSpan? maxWait = null,
        int maxSeenEntries = DefaultMaxSeenEntries)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("收件文件夹不能为空。", nameof(folder));

        Folder = Path.GetFullPath(folder);
        _maxSeenEntries = Math.Max(1, maxSeenEntries);
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(400);
        if (_settleDelay < TimeSpan.FromMilliseconds(50))
            _settleDelay = TimeSpan.FromMilliseconds(50);
        _maxWait = maxWait ?? TimeSpan.FromSeconds(20);
        if (_maxWait < _settleDelay * 2)
            _maxWait = _settleDelay * 2;

        _watcher = new FileSystemWatcher(Folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
    }

    public string Folder { get; }

    internal int SeenCount
    {
        get { lock (_seenLock) return _seen.Count; }
    }

    public bool IsRunning => !_disposed && _watcher.EnableRaisingEvents;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScanInboxWatcher));
        if (!Directory.Exists(Folder))
            throw new DirectoryNotFoundException($"收件文件夹不存在：{Folder}");
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_disposed) return;
        _watcher.EnableRaisingEvents = false;
    }

    /// <summary>显式把文件夹里已有的文件排队导入；监听本身不会自动导入旧文件。</summary>
    public void EnqueueExistingFiles()
    {
        if (_disposed || !Directory.Exists(Folder)) return;
        var candidates = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Folder))
        {
            if (!ScanFileClassifier.IsImportable(path)) continue;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            try
            {
                var length = new FileInfo(fullPath).Length;
                if (length > 0 && IsSeen(SeenKey(fullPath, length))) continue;
            }
            catch
            {
                // 文件正被占用或暂不可读时交由 QueueFile / WaitAndRaiseAsync 处理。
            }

            candidates.Add(fullPath);
        }

        foreach (var fullPath in candidates)
            QueueFile(fullPath);
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => QueueFile(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => QueueFile(e.FullPath);

    private void QueueFile(string path)
    {
        if (_disposed) return;
        if (!ScanFileClassifier.IsImportable(path)) return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        if (!_pending.TryAdd(fullPath, 0)) return;
        _ = Task.Run(() => WaitAndRaiseAsync(fullPath));
    }

    private async Task WaitAndRaiseAsync(string path)
    {
        try
        {
            var deadline = DateTime.UtcNow + _maxWait;
            long lastLength = -1;
            var stableChecks = 0;

            while (!_disposed && DateTime.UtcNow < deadline)
            {
                if (!File.Exists(path)) return;

                long length;
                try
                {
                    length = new FileInfo(path).Length;
                }
                catch
                {
                    await Task.Delay(_settleDelay).ConfigureAwait(false);
                    continue;
                }

                if (length > 0 && length == lastLength)
                    stableChecks++;
                else
                    stableChecks = 0;

                lastLength = length;

                if (length > 0 && stableChecks >= 1 && CanOpenForRead(path))
                {
                    var key = SeenKey(path, length);
                    if (TryRememberSeen(key))
                    {
                        if (!_disposed) FileReady?.Invoke(path);
                    }
                    return;
                }

                await Task.Delay(_settleDelay).ConfigureAwait(false);
            }

            if (!_disposed && File.Exists(path))
                FileFailed?.Invoke(path, "文件仍在写入或无法读取，稍后可点「导入现有文件」重试。");
        }
        finally
        {
            _pending.TryRemove(path, out _);
        }
    }

    private bool IsSeen(string key)
    {
        lock (_seenLock)
            return _seen.Contains(key);
    }

    private bool TryRememberSeen(string key)
    {
        lock (_seenLock)
        {
            if (!_seen.Add(key)) return false;
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > _maxSeenEntries)
            {
                var oldest = _seenOrder.Dequeue();
                _seen.Remove(oldest);
            }
            return true;
        }
    }

    private static string SeenKey(string path, long length)
    {
        DateTime lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            lastWrite = DateTime.MinValue;
        }

        return $"{path}|{length}|{lastWrite.Ticks}";
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
    }
}
