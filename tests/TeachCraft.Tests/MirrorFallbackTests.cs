using System.Net;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 镜像回退与源选择。
///
/// 这里锁住的是「用户点的是哪个源，实际用的又是哪个」——
/// 两者不一致时必须让界面看得见，否则出问题时无从排查。
/// </summary>
public sealed class MirrorFallbackTests
{
    private sealed class StubCheck : IUpdateCheckService
    {
        private readonly UpdateCheckResult _result;
        public int Calls { get; private set; }

        public StubCheck(UpdateCheckResult result) => _result = result;

        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static ReleaseEntry Release(string tag, bool prerelease = true)
    {
        SemanticVersion.TryParse(tag.TrimStart('v'), out var v);
        return new ReleaseEntry(tag, v, prerelease,
            new[] { new ReleaseAsset($"TeachCraft-{tag}-win-x64-sc-Setup.exe", 1, $"https://e/{tag}") });
    }

    private static UpdateCheckResult Ok(string tag, bool prerelease = true)
    {
        var release = Release(tag, prerelease);
        return UpdateCheckResult.Ok(release, new[] { release });
    }

    private static readonly BuildIdentity Current = BuildIdentity.Parse("1.17.0-preview.1.sc.win-x64");

    [Fact]
    public async Task DefaultSource_UsesGitHubWhenItWorks()
    {
        var github = new StubCheck(Ok("v1.18.0-preview.2"));
        var mirror = new StubCheck(Ok("v1.18.0-preview.2"));

        var service = new MirroringUpdateCheckService(github, mirror, UpdateSource.GitHub);
        var result = await service.CheckAsync(Current);

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdateSource.GitHub, service.UsedSource);
        Assert.False(service.DidFallBack);
        Assert.Equal(1, github.Calls);
        Assert.Equal(0, mirror.Calls);
    }

    [Fact]
    public async Task DefaultSource_FallsBackToMirrorWhenGitHubFails()
    {
        var github = new StubCheck(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork));
        var mirror = new StubCheck(Ok("v1.18.0-preview.2"));

        var service = new MirroringUpdateCheckService(github, mirror, UpdateSource.GitHub);
        var result = await service.CheckAsync(Current);

        Assert.True(result.IsSuccess, "GitHub 不通时镜像必须顶上，否则校园网用户永远升不了级");
        Assert.True(service.DidFallBack);
        Assert.Equal(UpdateSource.SectlMirror, service.UsedSource);
        Assert.Equal(UpdateErrorText.NoNetwork, service.PrimaryFailure);
    }

    [Fact]
    public async Task ManualMirrorSelection_DoesNotFallBackToGitHub()
    {
        // 用户明确选了镜像；替他回退到 GitHub 等于无视他的选择。
        var github = new StubCheck(Ok("v1.18.0-preview.2"));
        var mirror = new StubCheck(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork));

        var service = new MirroringUpdateCheckService(
            github, mirror, UpdateSource.SectlMirror, allowFallback: false);
        var result = await service.CheckAsync(Current);

        Assert.False(result.IsSuccess);
        Assert.False(service.DidFallBack);
        Assert.Equal(0, github.Calls);
    }

    [Fact]
    public async Task ManualGitHubSelection_DoesNotFallBackToMirror()
    {
        var github = new StubCheck(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork));
        var mirror = new StubCheck(Ok("v1.18.0-preview.2"));

        var service = new MirroringUpdateCheckService(
            github, mirror, UpdateSource.GitHub, allowFallback: false);
        var result = await service.CheckAsync(Current);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, mirror.Calls);
    }

    [Fact]
    public async Task BothSourcesFailing_MentionsBoth()
    {
        var github = new StubCheck(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork));
        var mirror = new StubCheck(UpdateCheckResult.Fail(UpdateErrorText.MirrorHasNoProject));

        var service = new MirroringUpdateCheckService(github, mirror, UpdateSource.GitHub);
        var result = await service.CheckAsync(Current);

        Assert.False(result.IsSuccess);
        // 用户需要知道「两个都试过了」，否则会以为只试了一次就去投诉。
        Assert.Contains("镜像", result.Failure);
        Assert.Contains(UpdateErrorText.NoNetwork, result.Failure);
        Assert.False(service.DidFallBack);
        Assert.Equal(UpdateSource.GitHub, service.UsedSource);
    }

    [Fact]
    public async Task Cancellation_DoesNotTriggerFallback()
    {
        // 用户点了取消；这时再去打另一个源，会让「取消」变成没取消。
        var github = new StubCheck(UpdateCheckResult.Fail(string.Empty));
        var mirror = new StubCheck(Ok("v1.18.0-preview.2"));

        var service = new MirroringUpdateCheckService(github, mirror, UpdateSource.GitHub);
        var result = await service.CheckAsync(Current);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, mirror.Calls);
        Assert.False(service.DidFallBack);
    }

    // ---- 源选择持久化 ----

    [Fact]
    public void StateStore_RoundTripsPreferredSource()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "state", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateStateStore(dir);
            store.Save(new UpdateState(null, Array.Empty<string>(), UpdateSource.SectlMirror));

            Assert.Equal(UpdateSource.SectlMirror, new UpdateStateStore(dir).Load().PreferredSource);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void StateStore_UnsetSourceStaysNull()
    {
        // null 表示「没手动选过」，要能区分于「选过 GitHub」——
        // 否则第一次启动就会把 GitHub 当成用户的选择，永久禁掉自动回退。
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "state", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateStateStore(dir);
            store.Save(new UpdateState(null, Array.Empty<string>()));

            Assert.Null(new UpdateStateStore(dir).Load().PreferredSource);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void StateStore_UnknownSourceValueIsTreatedAsUnset()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "state", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"),
                """{"LastCheckUtc":null,"SkippedVersions":[],"PreferredSource":"未来的新源"}""");

            Assert.Null(new UpdateStateStore(dir).Load().PreferredSource);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SourceText_StorageValueIsStableAndParseable()
    {
        // 存进文件的是稳定标识，不是界面文案；改文案不该让用户的选择失效。
        foreach (var source in new[] { UpdateSource.GitHub, UpdateSource.SectlMirror })
        {
            var stored = UpdateSourceText.ToStorageValue(source);
            Assert.Equal(source, UpdateSourceText.Parse(stored));
        }
    }
}
