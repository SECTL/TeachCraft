using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkScanInboxWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hw-scan-watch-" + Guid.NewGuid().ToString("N"));

    public HomeworkScanInboxWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task FileReady_RaisesForNewImportableFile()
    {
        using var watcher = new ScanInboxWatcher(
            _root, TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(5));
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileReady += path => ready.TrySetResult(path);
        watcher.Start();

        var path = Path.Combine(_root, "scan-001.pdf");
        await File.WriteAllTextAsync(path, "pdf");

        var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(8)));

        Assert.Same(ready.Task, completed);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(await ready.Task));
    }

    [Fact]
    public async Task FileReady_IgnoresTemporaryAndUnsupportedFiles()
    {
        using var watcher = new ScanInboxWatcher(
            _root, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(800));
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileReady += path => ready.TrySetResult(path);
        watcher.Start();

        await File.WriteAllTextAsync(Path.Combine(_root, "scan.tmp"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "scan.tif"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "~$scan.pdf"), "x");

        var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.NotSame(ready.Task, completed);
    }

    [Fact]
    public async Task FileReady_DoesNotRaiseTwiceForSameFile()
    {
        using var watcher = new ScanInboxWatcher(
            _root, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        var count = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileReady += _ =>
        {
            Interlocked.Increment(ref count);
            first.TrySetResult();
        };
        watcher.Start();

        var path = Path.Combine(_root, "scan-001.png");
        await File.WriteAllTextAsync(path, "png");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(500);

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task FileFailed_RaisesForFileThatNeverStabilizes()
    {
        using var watcher = new ScanInboxWatcher(
            _root, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileFailed += (path, _) => failed.TrySetResult(path);
        watcher.Start();

        var path = Path.Combine(_root, "scan-empty.pdf");
        await File.WriteAllTextAsync(path, string.Empty);

        var completed = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(failed.Task, completed);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(await failed.Task));
    }

    [Fact]
    public async Task SeenCache_EvictsOldestAtCapacity_AndDeduplicatesRecentFiles()
    {
        using var watcher = new ScanInboxWatcher(
            _root, TimeSpan.FromMilliseconds(35), TimeSpan.FromSeconds(2), maxSeenEntries: 3);

        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var signal = new SemaphoreSlim(0);
        watcher.FileReady += path =>
        {
            received.Enqueue(Path.GetFileName(path));
            signal.Release();
        };
        watcher.Start();

        var f1 = Path.Combine(_root, "scan-01.png");
        var f2 = Path.Combine(_root, "scan-02.png");
        var f3 = Path.Combine(_root, "scan-03.png");
        var f4 = Path.Combine(_root, "scan-04.png");

        foreach (var f in new[] { f1, f2, f3, f4 })
        {
            await File.WriteAllTextAsync(f, "png-bytes");
            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(6)), $"等待接收 {Path.GetFileName(f)} 超时");
        }

        Assert.Equal(4, received.Count);
        Assert.Equal(3, watcher.SeenCount);

        // 显式触发重新导入现有文件：被逐出的第 1 个文件应重新入队，仍驻留在缓存中的后 3 个文件应被去重跳过。
        while (received.TryDequeue(out _)) { }
        watcher.EnqueueExistingFiles();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(6)), "等待被逐出文件重新通知超时");
        await Task.Delay(250);

        var renotified = received.ToArray();
        Assert.Single(renotified);
        Assert.Equal("scan-01.png", renotified[0]);
        Assert.Equal(3, watcher.SeenCount);
    }

    [Fact]
    public async Task EnqueueExistingFiles_ImportsOnlyImportableFiles_AndIgnoresTempOrText()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "ok-01.png"), "png");
        await File.WriteAllTextAsync(Path.Combine(_root, "ok-02.pdf"), "pdf");
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_root, "~$temp.png"), "lock");
        await File.WriteAllTextAsync(Path.Combine(_root, "part.tmp"), "tmp");
        await File.WriteAllTextAsync(Path.Combine(_root, "dl.crdownload"), "cr");

        using var watcher = new ScanInboxWatcher(_root, TimeSpan.FromMilliseconds(35), TimeSpan.FromSeconds(2));
        var arrived = new System.Collections.Concurrent.ConcurrentBag<string>();
        var two = new SemaphoreSlim(0);
        watcher.FileReady += p =>
        {
            arrived.Add(Path.GetFileName(p));
            two.Release();
        };

        watcher.EnqueueExistingFiles();

        Assert.True(await two.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await two.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(250);

        Assert.Equal(["ok-01.png", "ok-02.pdf"], arrived.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
