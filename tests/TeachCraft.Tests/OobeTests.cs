using TeachCraft.Core.Oobe;

namespace TeachCraft.Tests;

/// <summary>
/// 首次引导的状态层回归：状态记录、原子写、损坏容错、可注入替身。
///
/// 这些断言刻意不碰真实的 %LOCALAPPDATA%——每个用例自建临时目录，
/// 否则跑一次测试就会把开发者本机的引导状态改掉。
/// </summary>
public sealed class OobeTests : IDisposable
{
    private readonly string _dir;

    public OobeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "teachcraft-oobe-test-" + Guid.NewGuid().ToString("N")[..12]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不该让测试变红。
        }
    }

    private OobeStateStore Store() => new(_dir);

    [Fact]
    public void OobeState_EmptyIsNeverDismissed()
    {
        Assert.Null(OobeState.Empty.DismissedUtc);
        Assert.Null(OobeState.Empty.DismissedHow);
        Assert.False(OobeState.Empty.IsDismissed);
    }

    [Fact]
    public void Store_RoundTripsAllFields()
    {
        var when = DateTimeOffset.Parse("2026-11-15T09:30:00+08:00");
        var store = Store();
        store.Save(new OobeState(when, OobeDismissal.Completed));

        var loaded = Store().Load();

        Assert.Equal(when, loaded.DismissedUtc);
        Assert.Equal(OobeDismissal.Completed, loaded.DismissedHow);
        Assert.Equal(OobeState.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.True(loaded.IsDismissed);
    }

    [Fact]
    public void Store_DismissalEnumRoundTripsAsString()
    {
        var store = Store();
        store.Save(new OobeState(DateTimeOffset.UtcNow, OobeDismissal.Skipped));

        var text = File.ReadAllText(Path.Combine(_dir, "state.json"));
        // 磁盘上必须是字符串而不是数字，否则人工排查时看不懂。
        Assert.Contains("\"Skipped\"", text);
        Assert.DoesNotContain("\"DismissedHow\": 1", text);

        Assert.Equal(OobeDismissal.Skipped, Store().Load().DismissedHow);
    }

    [Fact]
    public void Store_MissingFileReturnsEmpty()
    {
        var loaded = Store().Load();
        Assert.Equal(OobeState.Empty, loaded);
    }

    [Fact]
    public void Store_CorruptJsonReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "state.json"), "{ 这不是 JSON");

        var loaded = Store().Load();

        Assert.Equal(OobeState.Empty, loaded);
    }

    [Fact]
    public void Store_WrongFieldTypesReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "state.json"), "{\"DismissedHow\": 42}");

        // 类型不符不该抛，也不该把 42 猜成某个枚举值。
        var loaded = Store().Load();
        Assert.Null(loaded.DismissedHow);
    }

    [Fact]
    public void Store_MissingFieldsBecomeNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "state.json"), "{\"SchemaVersion\":1}");

        // 少字段 = null，不是「整份文件损坏」。
        var loaded = Store().Load();
        Assert.Null(loaded.DismissedUtc);
        Assert.Null(loaded.DismissedHow);
    }

    [Fact]
    public void Store_AtomicWriteLeavesNoTmpFile()
    {
        Store().Save(new OobeState(DateTimeOffset.UtcNow, OobeDismissal.Completed));

        Assert.True(File.Exists(Path.Combine(_dir, "state.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "state.json.tmp")));
    }

    [Fact]
    public void Store_SaveIntoUnwritableLocationDoesNotThrow()
    {
        // 把「目录」指向一个已存在的**文件**：创建目录必然失败，Save 必须吞掉。
        var blocker = Path.Combine(Path.GetTempPath(), "teachcraft-oobe-blocker-" + Guid.NewGuid().ToString("N")[..12]);
        File.WriteAllText(blocker, "x");
        try
        {
            var store = new OobeStateStore(Path.Combine(blocker, "Oobe"));
            var exception = Record.Exception(() => store.Save(OobeState.Empty));
            Assert.Null(exception);
        }
        finally
        {
            try { File.Delete(blocker); } catch (Exception) { }
        }
    }

    [Fact]
    public void NoopStore_ReadsEmptyAndDiscardsWrites()
    {
        var store = new NoopOobeStateStore();
        store.Save(new OobeState(DateTimeOffset.UtcNow, OobeDismissal.Completed));
        Assert.Equal(OobeState.Empty, store.Load());
    }
}
