using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 更新通道（正式 / 预览）。
///
/// 之前通道由「装的是哪一支」隐式决定，用户无法干预。加可选之后要守住的：
///   1. 默认仍跟随安装形态——预览版用户不该因为升级一版就悄悄掉回正式版；
///   2. 预览版用户切到正式通道时**允许降级**——正式版号低于同号预览，
///      按「必须更高」筛会把唯一该给他的版本筛掉；
///   3. 正式版用户切到预览通道能看到预览版。
/// </summary>
public sealed class UpdateChannelTests
{
    private static ReleaseEntry Release(string tag, bool prerelease)
    {
        SemanticVersion.TryParse(tag.TrimStart('v'), out var v);
        return new ReleaseEntry(tag, v, prerelease,
            new[] { new ReleaseAsset($"TeachCraft-{tag}-win-x64-sc-Setup.exe", 1, $"https://e/{tag}") });
    }

    /// <summary>一个典型发布列表：正式 1.18.0，预览 1.19.0-preview.3。</summary>
    private static IReadOnlyList<ReleaseEntry> Feed() => new[]
    {
        Release("v1.19.0-preview.3", prerelease: true),
        Release("v1.18.0", prerelease: false),
        Release("v1.17.0", prerelease: false),
    };

    private static BuildIdentity Identity(string value) => BuildIdentity.Parse(value);

    [Fact]
    public void StableUser_StableChannel_OnlySeesStable()
    {
        var candidates = UpdateFeed.SelectCandidates(Feed(), Identity("1.17.0.sc.win-x64"), UpdateChannel.Stable);

        Assert.Single(candidates);
        Assert.Equal("v1.18.0", candidates[0].TagName);
    }

    [Fact]
    public void StableUser_PreviewChannel_SeesPreviewToo()
    {
        // 正式版用户想尝鲜：切到预览通道就该看到预览版。
        var candidates = UpdateFeed.SelectCandidates(Feed(), Identity("1.17.0.sc.win-x64"), UpdateChannel.Preview);

        Assert.Equal("v1.19.0-preview.3", candidates[0].TagName);
    }

    [Fact]
    public void PreviewUser_StableChannel_CanDowngradeToStable()
    {
        // 这是最容易写错的一条：预览版 1.19.0-preview.3 切到稳定通道，
        // 唯一该给他的正式版是 1.18.0——版本号比当前低。
        // 按「必须更高」筛会把这条筛掉，用户就永远回不到正式版。
        var candidates = UpdateFeed.SelectCandidates(
            Feed(), Identity("1.19.0-preview.3.sc.win-x64"), UpdateChannel.Stable);

        Assert.Single(candidates);
        Assert.Equal("v1.18.0", candidates[0].TagName);
        Assert.False(candidates[0].IsPrerelease);
    }

    [Fact]
    public void PreviewUser_StableChannel_PicksHighestStable()
    {
        // 降级只给「最高的那个正式版」，不能把 1.17.0 也列出来。
        var feed = new[]
        {
            Release("v1.20.0-preview.1", prerelease: true),
            Release("v1.19.0", prerelease: false),
            Release("v1.18.0", prerelease: false),
        };

        var candidates = UpdateFeed.SelectCandidates(
            feed, Identity("1.20.0-preview.1.sc.win-x64"), UpdateChannel.Stable);

        Assert.Single(candidates);
        Assert.Equal("v1.19.0", candidates[0].TagName);
    }

    [Fact]
    public void PreviewUser_PreviewChannel_SeesHigherPreview()
    {
        var candidates = UpdateFeed.SelectCandidates(
            Feed(), Identity("1.18.0-preview.5.sc.win-x64"), UpdateChannel.Preview);

        Assert.Equal("v1.19.0-preview.3", candidates[0].TagName);
    }

    [Fact]
    public void PreviewUser_StableChannel_AlreadyOnStable_NoCandidates()
    {
        // 已经在最高正式版上，稳定通道不该再给任何东西。
        var candidates = UpdateFeed.SelectCandidates(
            Feed(), Identity("1.18.0.sc.win-x64"), UpdateChannel.Stable);

        Assert.Empty(candidates);
    }

    [Fact]
    public void DefaultChannel_FollowsInstalledKind()
    {
        // 没手动选过时跟随安装形态：预览版用户不该因为升级一版就悄悄掉回正式版。
        Assert.Equal(UpdateChannel.Stable, UpdateChannelText.DefaultFor(SemanticVersion.Parse("1.18.0")));
        Assert.Equal(UpdateChannel.Preview, UpdateChannelText.DefaultFor(SemanticVersion.Parse("1.18.0-preview.5")));
    }

    [Fact]
    public void ChannelStorage_RoundTrips()
    {
        foreach (var channel in new[] { UpdateChannel.Stable, UpdateChannel.Preview })
        {
            var stored = UpdateChannelText.ToStorageValue(channel);
            Assert.Equal(channel, UpdateChannelText.Parse(stored));
        }
    }

    [Fact]
    public void StateStore_RoundTripsPreferredChannel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "channel", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateStateStore(dir);
            store.Save(new UpdateState(null, Array.Empty<string>(), null, UpdateChannel.Preview));

            Assert.Equal(UpdateChannel.Preview, new UpdateStateStore(dir).Load().PreferredChannel);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void StateStore_UnsetChannelStaysNull()
    {
        // null 表示「没手动选过」，要能区分于「选过正式版」——
        // 否则预览版用户第一次启动就会被钉死在稳定通道。
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "channel", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateStateStore(dir);
            store.Save(new UpdateState(null, Array.Empty<string>()));

            Assert.Null(new UpdateStateStore(dir).Load().PreferredChannel);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void StateStore_UnknownChannelIsTreatedAsUnset()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "channel", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"),
                """{"LastCheckUtc":null,"SkippedVersions":[],"PreferredChannel":"beta" }""");

            Assert.Null(new UpdateStateStore(dir).Load().PreferredChannel);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
