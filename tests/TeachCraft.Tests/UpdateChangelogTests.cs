using System.Net;
using System.Net.Http;
using System.Text;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 更新日志契约：
/// 1. 仅在通过软件内的更新模块升级成功之后弹窗显示一次；
/// 2. 未走更新模块（首次安装 / 外部覆盖）、取消安装、替换失败或版本未变均不展示；
/// 3. 展示后或无效触发立即清除标记，绝不重复弹窗；
/// 4. 支持内置 CHANGELOG.md 多版本聚合与上游 Release 说明回退。
/// </summary>
public sealed class UpdateChangelogTests : IDisposable
{
    private readonly string _root;

    public UpdateChangelogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "Changelog", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class MemoryPendingStore : IPendingChangelogStore
    {
        public PendingChangelogEntry? Entry { get; set; }
        public int ClearCalls { get; private set; }
        public PendingChangelogEntry? Load() => Entry;
        public void Save(PendingChangelogEntry entry) => Entry = entry;
        public void Clear()
        {
            ClearCalls++;
            Entry = null;
        }
    }

    private sealed class MemoryStateStore : IUpdateStateStore
    {
        public UpdateState State { get; private set; } = UpdateState.Empty;
        public UpdateState Load() => State;
        public void Save(UpdateState state) => State = state;
    }

    private sealed class FeedStub : IUpdateCheckService
    {
        private readonly UpdateCheckResult _result;
        public FeedStub(UpdateCheckResult result) => _result = result;
        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, UpdateChannel channel = UpdateChannel.Stable, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class GuidanceDownloadHandler : HttpMessageHandler
    {
        public const int PackageSize = 32;
        private readonly string _assetName;
        private readonly byte[] _package;
        private readonly string _sha256;

        public GuidanceDownloadHandler(string assetName)
        {
            _assetName = assetName;
            _package = new byte[PackageSize];
            "teachcraft-changelog-test-pkg!!"u8.CopyTo(_package);
            _sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_package)).ToLowerInvariant();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            string body;
            if (url.Contains("releases", StringComparison.OrdinalIgnoreCase))
            {
                body = $$"""
                    [{"tag_name":"v1.46.0","prerelease":false,"body":"### 新增\n- 远端发布说明测试条目","assets":[
                      {"name":"{{_assetName}}","size":{{_package.Length}},"browser_download_url":"https://e/{{_assetName}}"},
                      {"name":"{{AssetNaming.ChecksumsName("v1.46.0")}}","size":1,"browser_download_url":"https://e/sums"}]}]
                    """;
            }
            else if (url.Contains("sums", StringComparison.OrdinalIgnoreCase))
            {
                body = $"{_sha256}  {_assetName}\n";
            }
            else
            {
                body = Encoding.UTF8.GetString(_package);
            }

            var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(ms) };
            response.Content.Headers.ContentLength = ms.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class FailingDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("网络不可达（测试注入）");
    }

    [Fact]
    public void EmbeddedChangelog_IsBundledAndParsesCleanly()
    {
        var markdown = EmbeddedChangelogProvider.ReadOrNull();
        Assert.False(string.IsNullOrWhiteSpace(markdown), "TeachCraft.Core 必须内嵌 CHANGELOG.md");

        var releases = ChangelogParser.ParseDocument(markdown);
        Assert.NotEmpty(releases);
        Assert.Contains(releases, r => r.Version.Equals(SemanticVersion.Parse("1.45.0")));
        Assert.All(releases, r => Assert.NotEmpty(r.Sections));
        Assert.All(releases.SelectMany(r => r.Sections), s => Assert.NotEmpty(s.Items));

        // 1.45.0 条目需反映「辑册」定名与更新日志功能。
        var latestText = string.Join("\n", releases.First(r => r.Version.Equals(SemanticVersion.Parse("1.45.0")))
            .Sections.SelectMany(s => s.Items));
        Assert.Contains("辑册", latestText, StringComparison.Ordinal);
        Assert.Contains("更新日志", latestText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_HandlesFormatsStripsMarkdownAndFiltersBoilerplate()
    {
        const string markdown = """
            # 更新日志

            ## [v1.46.0] - 2026-12-01
            ### 新增
            - 支持 **导出预设** 与 `WorkFolio`
              自动同步。

            ## 1.45.0 · 2026-11-25
            - 常规改进项
            """;

        var releases = ChangelogParser.ParseDocument(markdown);
        Assert.Equal(2, releases.Count);
        Assert.Equal(SemanticVersion.Parse("1.46.0"), releases[0].Version);
        Assert.Equal("2026-12-01", releases[0].DateText);
        Assert.Equal("支持 导出预设 与 WorkFolio 自动同步。", releases[0].Sections[0].Items[0]);

        Assert.True(ChangelogParser.IsBoilerplateReleaseBody("## 知微 · TeachCraft 全矩阵跨平台构建（v1.45.0）\n..."));
        Assert.False(ChangelogParser.IsBoilerplateReleaseBody("### 修复\n- 修掉已知问题"));
    }

    [Fact]
    public void Feeds_ExtractReleaseNotesWhenPresent()
    {
        const string githubJson = """
            [
              {
                "tag_name": "v1.46.0",
                "draft": false,
                "prerelease": false,
                "body": "### 新增\n- GitHub 发行说明",
                "assets": []
              }
            ]
            """;

        var gh = UpdateFeed.Parse(githubJson).Single();
        Assert.Equal("### 新增\n- GitHub 发行说明", gh.ReleaseNotes);

        const string mirrorJson = """
            {
              "projects": [{ "$id": "p1", "slug": "知微 · TeachCraft" }],
              "versions": [{ "project_id": "p1", "tag": "v1.46.0", "prerelease": false, "enabled": true, "release_notes": "镜像更新说明" }],
              "packages": []
            }
            """;

        var mirror = SectlMirrorFeed.Parse(mirrorJson, SectlMirrorFeed.DefaultProjectSlug).Single();
        Assert.Equal("镜像更新说明", mirror.ReleaseNotes);
    }

    [Fact]
    public void PendingChangelogStore_RoundTripsAtomicallyAndSurvivesCorruption()
    {
        var store = new PendingChangelogStore(_root);
        Assert.Null(store.Load());

        var entry = new PendingChangelogEntry("1.44.0", "1.45.0", "v1.45.0", "### 新增\n- 测试");
        store.Save(entry);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("1.44.0", loaded!.FromVersion);
        Assert.Equal("1.45.0", loaded.TargetVersion);
        Assert.Equal("v1.45.0", loaded.TagName);
        Assert.Equal("### 新增\n- 测试", loaded.FeedNotes);

        store.Clear();
        Assert.Null(store.Load());

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "pending-changelog.json"), "{bad json");
        Assert.Null(store.Load());
    }

    [Fact]
    public void ResolveOnStartup_ReturnsNullWithoutInAppUpdateMarker()
    {
        var store = new MemoryPendingStore();

        // 首次安装 / 手动外部覆盖安装都没有 pending 标记，绝不弹窗。
        var doc = UpdateChangelogPlanner.ResolveOnStartup(
            store, SemanticVersion.Parse("1.45.0"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull());

        Assert.Null(doc);
    }

    [Fact]
    public void ResolveOnStartup_ShowsOnceAfterSuccessfulInAppUpgrade()
    {
        var store = new MemoryPendingStore
        {
            Entry = new PendingChangelogEntry("1.44.0", "1.45.0", "v1.45.0"),
        };

        var firstLaunch = UpdateChangelogPlanner.ResolveOnStartup(
            store, SemanticVersion.Parse("1.45.0"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull());

        Assert.NotNull(firstLaunch);
        Assert.Equal("已更新至 v1.45.0", firstLaunch!.Headline);
        Assert.Single(firstLaunch.Releases);
        Assert.Equal(SemanticVersion.Parse("1.45.0"), firstLaunch.Releases[0].Version);
        Assert.False(firstLaunch.IsFallbackSummary);
        Assert.Null(store.Entry);

        // 第二次启动同一版本不能再弹出。
        var secondLaunch = UpdateChangelogPlanner.ResolveOnStartup(
            store, SemanticVersion.Parse("1.45.0"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull());
        Assert.Null(secondLaunch);
    }

    [Fact]
    public void ResolveOnStartup_AggregatesMultiVersionUpgradeNewestFirst()
    {
        var store = new MemoryPendingStore
        {
            Entry = new PendingChangelogEntry("1.42.0", "1.45.0", "v1.45.0"),
        };

        var doc = UpdateChangelogPlanner.ResolveOnStartup(
            store, SemanticVersion.Parse("1.45.0"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull());

        Assert.NotNull(doc);
        Assert.Equal(
            new[]
            {
                SemanticVersion.Parse("1.45.0"),
                SemanticVersion.Parse("1.44.0"),
                SemanticVersion.Parse("1.43.0"),
            },
            doc!.Releases.Select(r => r.Version).ToArray());
        Assert.Contains("3 个版本", doc.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOnStartup_SuppressesWhenVersionUnchangedOrPortableFailedOrMismatched()
    {
        var unchanged = new MemoryPendingStore { Entry = new PendingChangelogEntry("1.44.0", "1.45.0") };
        Assert.Null(UpdateChangelogPlanner.ResolveOnStartup(
            unchanged, SemanticVersion.Parse("1.44.0"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull()));
        Assert.Null(unchanged.Entry);

        var failedPortable = new MemoryPendingStore { Entry = new PendingChangelogEntry("1.44.0", "1.45.0") };
        Assert.Null(UpdateChangelogPlanner.ResolveOnStartup(
            failedPortable, SemanticVersion.Parse("1.45.0"), portableReplaceFailed: true, EmbeddedChangelogProvider.ReadOrNull()));
        Assert.Null(failedPortable.Entry);

        var differentVersion = new MemoryPendingStore { Entry = new PendingChangelogEntry("1.44.0", "1.45.0") };
        Assert.Null(UpdateChangelogPlanner.ResolveOnStartup(
            differentVersion, SemanticVersion.Parse("1.46.0"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull()));
        Assert.Null(differentVersion.Entry);
    }

    [Fact]
    public void ResolveOnStartup_UsesFeedNotesOrFallbackWhenVersionMissingFromEmbeddedLog()
    {
        var withFeed = new MemoryPendingStore
        {
            Entry = new PendingChangelogEntry("1.46.0-preview.1", "1.46.0-preview.2", "v1.46.0-preview.2", "### 修复\n- 预览热修"),
        };
        var feedDoc = UpdateChangelogPlanner.ResolveOnStartup(
            withFeed, SemanticVersion.Parse("1.46.0-preview.2"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull());
        Assert.NotNull(feedDoc);
        Assert.False(feedDoc!.IsFallbackSummary);
        Assert.Equal("预览热修", feedDoc.Releases[0].Sections[0].Items[0]);

        var withBoilerplate = new MemoryPendingStore
        {
            Entry = new PendingChangelogEntry("1.46.0-preview.1", "1.46.0-preview.2", "v1.46.0-preview.2", "## 知微 · TeachCraft 全矩阵跨平台构建（v1.46.0-preview.2）"),
        };
        var fallbackDoc = UpdateChangelogPlanner.ResolveOnStartup(
            withBoilerplate, SemanticVersion.Parse("1.46.0-preview.2"), portableReplaceFailed: false, EmbeddedChangelogProvider.ReadOrNull());
        Assert.NotNull(fallbackDoc);
        Assert.True(fallbackDoc!.IsFallbackSummary);
    }

    [Fact]
    public async Task MainViewModel_GuidanceSuccessSavesPendingChangelog_AndFailuresOrCancelDoNot()
    {
        var pendingStore = new MemoryPendingStore();
        SemanticVersion.TryParse("1.46.0", out var next);
        var release = new ReleaseEntry("v1.46.0", next, false,
            new[] { new ReleaseAsset("TeachCraft-v1.46.0-osx-arm64-sc.dmg", GuidanceDownloadHandler.PackageSize, "https://e/dmg") },
            "### 新增\n- Mac 远端日志");

        var vm = new MainViewModel(
            checkService: new FeedStub(UpdateCheckResult.Ok(release, new[] { release })),
            identity: BuildIdentity.Parse("1.45.0.sc.osx-arm64"),
            installInfo: new InstallInfo(InstallKind.MacBundle, @"/Applications", "sc", "osx-arm64"),
            stateStore: new MemoryStateStore(),
            downloaderFactory: () => new UpdateDownloader(new GuidanceDownloadHandler("TeachCraft-v1.46.0-osx-arm64-sc.dmg")),
            pendingChangelogStore: pendingStore);

        await vm.CheckUpdateAsync(fromUserGesture: true);
        await vm.DownloadAndInstallAsync();

        Assert.NotNull(pendingStore.Entry);
        Assert.Equal("1.45.0", pendingStore.Entry!.FromVersion);
        Assert.Equal("1.46.0", pendingStore.Entry.TargetVersion);
        Assert.Equal("### 新增\n- Mac 远端日志", pendingStore.Entry.FeedNotes);

        // 下载失败绝不写入待展示日志。
        var failPendingStore = new MemoryPendingStore();
        var failVm = new MainViewModel(
            checkService: new FeedStub(UpdateCheckResult.Ok(release, new[] { release })),
            identity: BuildIdentity.Parse("1.45.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: new MemoryStateStore(),
            downloaderFactory: () => new UpdateDownloader(new FailingDownloadHandler()),
            pendingChangelogStore: failPendingStore);

        await failVm.CheckUpdateAsync(fromUserGesture: true);
        await failVm.DownloadAndInstallAsync();
        Assert.Null(failPendingStore.Entry);
    }

    [Fact]
    public void AppWiring_SavesPendingChangelogOnlyAfterStartAndResolvesBeforeShow()
    {
        var app = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "App.axaml.cs"));

        Assert.Contains("UpdateChangelogPlanner.ResolveOnStartup", app, StringComparison.Ordinal);
        Assert.Contains("WireStartupChangelog", app, StringComparison.Ordinal);
        Assert.Contains("CaptureChangelogIfRequested", app, StringComparison.Ordinal);

        // 绿色版与安装包两条路径都必须在「未保存窗口确认 + 启动安装/替换进程」之后才写标记。
        var runInstallerStart = app.IndexOf("private async Task<string> RunInstallerAsync", StringComparison.Ordinal);
        var runPortableStart = app.IndexOf("private async Task<string> RunPortableReplace", StringComparison.Ordinal);
        Assert.True(runInstallerStart >= 0 && runPortableStart > runInstallerStart);

        var runInstaller = app[runInstallerStart..runPortableStart];
        var nextPrivate = app.IndexOf("\n    private ", runPortableStart + 20, StringComparison.Ordinal);
        var runPortable = app[runPortableStart..nextPrivate];

        Assert.True(
            runInstaller.IndexOf("ConfirmUnsavedBeforeInstall", StringComparison.Ordinal) <
            runInstaller.IndexOf("pendingChangelogStore.Save", StringComparison.Ordinal));
        Assert.True(
            runInstaller.IndexOf("Process.Start(startInfo)", StringComparison.Ordinal) <
            runInstaller.IndexOf("pendingChangelogStore.Save", StringComparison.Ordinal));

        Assert.True(
            runPortable.IndexOf("ConfirmUnsavedBeforeInstall", StringComparison.Ordinal) <
            runPortable.IndexOf("pendingChangelogStore.Save", StringComparison.Ordinal));
        Assert.True(
            runPortable.IndexOf("StartPortableReplaceHelper(", StringComparison.Ordinal) <
            runPortable.IndexOf("pendingChangelogStore.Save", StringComparison.Ordinal));
    }
}
