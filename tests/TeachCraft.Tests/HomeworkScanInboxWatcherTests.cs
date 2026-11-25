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
}
