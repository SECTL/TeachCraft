using System.Net;
using System.Net.Sockets;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 接线测试：证明安装形态确实从探测流到提案，而不是被写死。
///
/// 之前的测试全部注入「被测对象」（注入 InstallKind、注入 InstallRecord、注入 identity），
/// 唯独 MainViewModel 拿到什么形态这条接线没人测，而这正是功能不可用的根因。
/// </summary>
public sealed class UpdateWiringTests
{
    private sealed class FeedStub : IUpdateCheckService
    {
        private readonly UpdateCheckResult _result;
        public FeedStub(UpdateCheckResult result) => _result = result;
        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, UpdateChannel channel = UpdateChannel.Stable, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class MemoryStateStore : IUpdateStateStore
    {
        public UpdateState State { get; private set; } = UpdateState.Empty;
        public UpdateState Load() => State;
        public void Save(UpdateState state) => State = state;
    }

    private static ReleaseEntry ReleaseWithAllAssets()
    {
        SemanticVersion.TryParse("1.18.0", out var v);
        var names = new[]
        {
            "TeachCraft-v1.18.0-win-x64-sc-Setup.exe",
            "TeachCraft-v1.18.0-win-x64-sc.zip",
            "TeachCraft-v1.18.0-win-x64-fdd-Setup.exe",
            "TeachCraft-v1.18.0-win-x64-fdd.zip",
        };
        return new ReleaseEntry("v1.18.0", v, false,
            names.Select(n => new ReleaseAsset(n, 1, $"https://e/{n}")).ToList());
    }

    private static UpdateCheckResult HasUpdate()
    {
        var release = ReleaseWithAllAssets();
        return UpdateCheckResult.Ok(release, new[] { release });
    }

    [Fact]
    public void OpenUpdatePanel_IsMutuallyExclusiveWithOtherPanels()
    {
        // 三个面板共用同一个抽屉；同时开两个会叠在一起。
        var vm = new MainViewModel();
        vm.OpenProject();
        Assert.True(vm.IsProjectPanelOpen);

        vm.OpenUpdatePanel();

        Assert.True(vm.IsUpdatePanelOpen);
        Assert.False(vm.IsProjectPanelOpen);
        Assert.False(vm.IsToolPanelOpen);
        Assert.True(vm.IsPanelOpen, "抽屉必须算作打开，否则整块不可见");
    }

    [Fact]
    public void OpenUpdatePanel_DoesNotGoOnline()
    {
        // 打开页面只摆信息；联网必须由用户在页面里再点一次。
        // 注入一个会抛「真实网络请求」的服务：打开面板若联网，这里就会炸。
        var vm = new MainViewModel(
            checkService: new ThrowingCheckService(),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:pp", "sc", "win-x64"),
            stateStore: new MemoryStateStore());

        vm.OpenUpdatePanel();

        Assert.True(vm.IsUpdatePanelOpen);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public void OpenUpdatePanel_DescribesWhichBuildIsInstalled()
    {
        var vm = new MainViewModel(
            identity: BuildIdentity.Parse("1.17.0.fdd.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:pp", "fdd", "win-x64"),
            stateStore: new MemoryStateStore());

        vm.OpenUpdatePanel();

        // 老师要能看出自己装的是哪一支，而不用懂 sc/fdd 这些词。
        Assert.Contains("安装包版本", vm.InstallDescription);
        Assert.Contains("win-x64", vm.InstallDescription);
        Assert.Contains("依赖 .NET 运行时", vm.InstallDescription);
    }

    [Fact]
    public void ClosePanels_AlsoClosesUpdatePanel()
    {
        var vm = new MainViewModel();
        vm.OpenUpdatePanel();

        vm.ClosePanels();

        Assert.False(vm.IsUpdatePanelOpen);
        Assert.False(vm.IsPanelOpen);
    }

    private sealed class ThrowingCheckService : IUpdateCheckService
    {
        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, UpdateChannel channel = UpdateChannel.Stable, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("打开更新页不该联网");
    }

    [Fact]
    public void UpdateExplanation_RaisesPropertyChanged()
    {
        // 工具提示绑定在这个属性上；不发出通知的话，界面永远显示绑定时的空串，
        // 那两句解释（信任边界、预览升级路径）用户根本看不到。
        var vm = new MainViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.UpdateExplanation)) raised.Add(e.PropertyName!); };

        vm.ArmPreviewBadge("1.18.0-preview.142");

        Assert.NotEmpty(raised);
    }

    [Fact]
    public void DismissUpdate_ClearsExplanationToo()
    {
        // 徽章藏起来后，工具提示不能还挂着上一次的解释。
        var vm = new MainViewModel();

        vm.ArmPreviewBadge("1.18.0-preview.142");
        Assert.NotEmpty(vm.UpdateExplanation);

        vm.DismissUpdate();

        Assert.Equal("", vm.UpdateBadgeText);
        Assert.Equal("", vm.UpdateExplanation);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task SilentCheck_Failure_LeavesStatusTextUntouched()
    {
        var vm = new MainViewModel(
            checkService: new FailingStub(),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:pp", "sc", "win-x64"),
            stateStore: new MemoryStateStore());
        var before = vm.StatusText;

        await vm.SilentCheckAsync();

        Assert.Equal(before, vm.StatusText);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task UpdateExplanation_StatesTrustBoundary()
    {
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0-preview.5.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:pp", "sc", "win-x64"),
            stateStore: new MemoryStateStore());

        await vm.CheckUpdateAsync(fromUserGesture: true);

        // 两条必须对用户说清的事都要在。
        Assert.Contains("未签名", vm.UpdateExplanation);
        Assert.Contains("正式版", vm.UpdateExplanation);
    }

    private sealed class FailingStub : IUpdateCheckService
    {
        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, UpdateChannel channel = UpdateChannel.Stable, CancellationToken cancellationToken = default)
            => Task.FromResult(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork));
    }

    [Fact]
    public async Task InjectedInnoInstall_ProducesAProposalInsteadOfFailure()
    {
        // 安装包形态 + 身份齐全 → 必须拿到提案，而不是「暂未提供」。
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: new MemoryStateStore());

        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.True(vm.IsUpdateAvailable, "安装包形态必须能生成提案，否则真实用户永远点不出更新");
        Assert.Equal("TeachCraft-v1.18.0-win-x64-sc-Setup.exe", vm.PendingUpdate!.Asset.Name);
    }

    [Fact]
    public async Task DetectedModeFlowsIntoAssetSelection()
    {
        // 探测到 fdd 时，选出的必须是 fdd 的包，不能被升级成 sc。
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.fdd.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "fdd", "win-x64"),
            stateStore: new MemoryStateStore());

        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.Equal("TeachCraft-v1.18.0-win-x64-fdd-Setup.exe", vm.PendingUpdate!.Asset.Name);
    }

    [Fact]
    public async Task UnknownInstallKind_StillFailsWithExplanation_ButIsNotTheOnlyPath()
    {
        // Unknown 仍应给出人话，但上面两条证明它不是唯一结局。
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.Unknown, null, null, null),
            stateStore: new MemoryStateStore());

        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.False(vm.IsUpdateAvailable);
        Assert.Contains("暂未提供", vm.StatusText);
    }

    [Fact]
    public async Task SkippedVersion_IsNotOfferedAgain()
    {
        // 「稍后」必须真的生效：跳过之后同一版本不再提示。
        var store = new MemoryStateStore();
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: store);

        await vm.CheckUpdateAsync(fromUserGesture: true);
        Assert.True(vm.IsUpdateAvailable);

        vm.DismissUpdate();
        Assert.False(vm.IsUpdateAvailable);

        // 再检查一次：同一版本应被跳过列表挡掉。
        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.False(vm.IsUpdateAvailable, "用户已经点过稍后，不该再次提示同一版本");
        Assert.Contains("1.18.0", string.Join(",", store.State.SkippedVersions));
    }

    [Fact]
    public async Task AfterSkip_StatusDoesNotClaimNewVersionWithoutButton()
    {
        // 之前的问题：跳过后徽章消失了，但状态栏仍说「发现新版本」，
        // 而界面上根本没有更新按钮——自相矛盾。
        var store = new MemoryStateStore();
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:pp", "sc", "win-x64"),
            stateStore: store);

        await vm.CheckUpdateAsync(fromUserGesture: true);
        vm.DismissUpdate();

        // 再次检查：同一版本已被跳过。
        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.False(vm.IsUpdateAvailable);
        Assert.Equal("", vm.UpdateBadgeText);
        Assert.DoesNotContain("发现新版本", vm.StatusText);
        Assert.Contains("跳过", vm.StatusText);
    }

    [Fact]
    public async Task HigherVersionAfterSkip_IsStillOffered()
    {
        // 跳过 1.18.0 之后，1.19.0 仍然要提示。
        var store = new MemoryStateStore();
        store.Save(new UpdateState(null, new[] { "1.18.0" }));

        SemanticVersion.TryParse("1.19.0", out var v);
        var release = new ReleaseEntry("v1.19.0", v, false,
            new[] { new ReleaseAsset("TeachCraft-v1.19.0-win-x64-sc-Setup.exe", 1, "https://e/x") });

        var vm = new MainViewModel(
            checkService: new FeedStub(UpdateCheckResult.Ok(release, new[] { release })),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: store);

        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal("1.19.0", vm.PendingUpdate!.VersionText);
    }

    // ------------------------------------------------------------ P2-28 失败配色 / P3-35 跳过话术

    [Fact]
    public async Task InstallFailure_MarksIsLastInstallFailureSoStatusBarKeepsRed()
    {
        // 修复前：失败在 ViewModel 里已按 error:true 画红点，但状态栏 mini 按钮的处理器
        // 拿到返回文案后无条件 SetStatus（默认绿）——失败被自己的收尾冲成绿色。
        // ViewModel 必须把「这是失败终态」如实告诉界面（IsLastInstallFailure），
        // 调用方据此沿用 error:true。
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: new MemoryStateStore(),
            downloaderFactory: () => new UpdateDownloader(new FailingDownloadHandler()));
        await vm.CheckUpdateAsync(fromUserGesture: true);
        Assert.True(vm.IsUpdateAvailable);

        var message = await vm.DownloadAndInstallAsync();

        Assert.False(string.IsNullOrEmpty(message));
        Assert.True(vm.IsLastInstallFailure, "失败终态必须标记 IsLastInstallFailure，否则状态栏会把它刷成绿色");
        Assert.Contains(vm.StatusText, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulGuidance_IsNotMarkedAsFailure()
    {
        // ShowGuidance 形态（macOS / Linux / 认不出安装形态）是成功终态：
        // 状态栏沿用 error: ViewModel.IsLastInstallFailure 时绝不能误报成红色。
        SemanticVersion.TryParse("1.18.0", out var v);
        // 资产声明的大小必须与 OKDownloadHandler 的占位包字节数一致，否则长度校验会拦下载。
        var release = new ReleaseEntry("v1.18.0", v, false,
            new[] { new ReleaseAsset("TeachCraft-v1.18.0-osx-arm64-sc.dmg", OKDownloadHandler.PackageSize, "https://e/dmg") });
        var vm = new MainViewModel(
            checkService: new FeedStub(UpdateCheckResult.Ok(release, new[] { release })),
            identity: BuildIdentity.Parse("1.17.0.sc.osx-arm64"),
            installInfo: new InstallInfo(InstallKind.MacBundle, @"/Applications", "sc", "osx-arm64"),
            stateStore: new MemoryStateStore(),
            downloaderFactory: () => new UpdateDownloader(new OKDownloadHandler("TeachCraft-v1.18.0-osx-arm64-sc.dmg")));

        await vm.CheckUpdateAsync(fromUserGesture: true);
        Assert.True(vm.IsUpdateAvailable);

        await vm.DownloadAndInstallAsync();

        Assert.False(vm.IsLastInstallFailure);
    }

    [Fact]
    public async Task CancelledInstall_IsMarkedAsFailureForStatusBarColor()
    {
        // 安装前置确认被拒（「暂不安装」）也是失败终态：绿色会让人误读成安装已在进行。
        // App 层的取消话术以「已取消安装」开头——用假 handler 驱动真实判定链。
        SemanticVersion.TryParse("1.18.0", out var v);
        var release = new ReleaseEntry("v1.18.0", v, false,
            new[] { new ReleaseAsset("TeachCraft-v1.18.0-win-x64-sc-Setup.exe", OKDownloadHandler.PackageSize, "https://e/x") });

        var vm = new MainViewModel(
            checkService: new FeedStub(UpdateCheckResult.Ok(release, new[] { release })),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: new MemoryStateStore(),
            downloaderFactory: () => new UpdateDownloader(new OKDownloadHandler("TeachCraft-v1.18.0-win-x64-sc-Setup.exe")));

        await vm.CheckUpdateAsync(fromUserGesture: true);
        vm.InstallerRequested += (_, _) => Task.FromResult("已取消安装。请先保存各窗口的工作，再重新点「下载并安装」。");

        await vm.DownloadAndInstallAsync();

        Assert.True(vm.IsLastInstallFailure, "「已取消安装」必须按失败终态标记，状态栏保持红点");
    }

    [Fact]
    public void DismissUpdate_RecordsSkippedVersionForStatusFeedback()
    {
        // P3-35：跳过后状态栏要有结果话术；话术里的版本号来自 LastSkippedVersion。
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: new MemoryStateStore());

        Assert.Null(vm.LastSkippedVersion);

        vm.DismissUpdate();
        Assert.Null(vm.LastSkippedVersion);

        vm.ArmPreviewBadge("1.18.0");
        Assert.Null(vm.PendingUpdate); // 走查布景不设提案，跳过记不下版本号。

        vm.DismissUpdate();
        Assert.Null(vm.LastSkippedVersion);
    }

    [Fact]
    public async Task DismissUpdate_AfterRealProposal_RecordsVersion()
    {
        var store = new MemoryStateStore();
        var vm = new MainViewModel(
            checkService: new FeedStub(HasUpdate()),
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            installInfo: new InstallInfo(InstallKind.InnoInstall, @"C:\app", "sc", "win-x64"),
            stateStore: store);

        await vm.CheckUpdateAsync(fromUserGesture: true);
        vm.DismissUpdate();

        Assert.Equal("1.18.0", vm.LastSkippedVersion);
        Assert.Contains("1.18.0", string.Join(",", store.State.SkippedVersions));
    }

    private sealed class FailingDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("网络不可达（测试注入）", new SocketException());
    }

    /// <summary>
    /// 按请求地址路由的最小假下载源：Releases API 喂 feed JSON、清单地址喂校验和、
    /// 其余（真正的安装包下载）喂一个字节内容与校验和都自洽的占位包。
    /// </summary>
    private sealed class OKDownloadHandler : HttpMessageHandler
    {
        /// <summary>占位包字节数；fixture 里资产声明的 size 必须与此一致，否则长度校验会拦。</summary>
        public const int PackageSize = 32;

        private readonly string _assetName;
        private readonly byte[] _package;
        private readonly string _sha256;

        public OKDownloadHandler(string assetName)
        {
            _assetName = assetName;
            _package = new byte[PackageSize];
            "teachcraft-placeholder-package"u8.CopyTo(_package);
            _sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_package)).ToLowerInvariant();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            string body;
            if (url.Contains("releases", StringComparison.OrdinalIgnoreCase))
            {
                // Releases API：一份带目标资产 + 清单资产的最小 feed。
                var assetUrl = $"https://e/{_assetName}";
                var sumsUrl = $"https://e/sums";
                body = $$"""
                    [{"tag_name":"v1.18.0","prerelease":false,"assets":[
                      {"name":"{{_assetName}}","size":{{_package.Length}},"browser_download_url":"{{assetUrl}}"},
                      {"name":"{{AssetNaming.ChecksumsName("v1.18.0")}}","size":1,"browser_download_url":"{{sumsUrl}}"}]}]
                    """;
            }
            else if (url.Contains("sums", StringComparison.OrdinalIgnoreCase))
            {
                body = $"{_sha256}  {_assetName}\n";
            }
            else
            {
                body = System.Text.Encoding.UTF8.GetString(_package);
            }

            var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(ms) };
            response.Content.Headers.ContentLength = ms.Length;
            return Task.FromResult(response);
        }
    }
}
