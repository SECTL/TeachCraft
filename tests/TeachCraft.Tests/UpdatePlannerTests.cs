using System.Net;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 从检查到提案的整条链路（真实 feed 解析 + 真实资产挑选），不是喂一个手搓的提案。
/// </summary>
public sealed class UpdatePlannerTests
{
    private sealed class StubCheckService : IUpdateCheckService
    {
        private readonly UpdateCheckResult _result;

        public StubCheckService(UpdateCheckResult result) => _result = result;

        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private static ReleaseEntry Entry(string tag, bool pre, params string[] assetNames)
    {
        SemanticVersion.TryParse(tag.TrimStart('v'), out var version);
        var assets = assetNames
            .Select(n => new ReleaseAsset(n, 120_000_000, $"https://e/{n}"))
            .ToList();
        return new ReleaseEntry(tag, version, pre, assets);
    }

    [Fact]
    public async Task Plan_PicksAssetForCurrentInstallKind()
    {
        var release = Entry("v1.18.0", false,
            "TeachCraft-v1.18.0-win-x64-sc.zip",
            "TeachCraft-v1.18.0-win-x64-sc-Setup.exe",
            "TeachCraft-v1.18.0-win-x64-fdd-Setup.exe");

        var planner = new UpdatePlanner(
            new StubCheckService(UpdateCheckResult.Ok(release, new[] { release })),
            BuildIdentity.Parse("1.17.0.sc.win-x64"),
            InstallKind.InnoInstall);

        var result = await planner.PlanAsync();

        Assert.Equal(UpdatePlanStatus.UpdateAvailable, result.Status);
        Assert.Equal("TeachCraft-v1.18.0-win-x64-sc-Setup.exe", result.Proposal!.Asset.Name);
        Assert.Equal("1.18.0", result.Proposal.VersionText);
    }

    [Fact]
    public async Task Plan_UpToDate_IsExplicit()
    {
        var planner = new UpdatePlanner(
            new StubCheckService(UpdateCheckResult.Ok(null, Array.Empty<ReleaseEntry>())),
            BuildIdentity.Parse("1.17.0.sc.win-x64"),
            InstallKind.InnoInstall);

        var result = await planner.PlanAsync();

        Assert.Equal(UpdatePlanStatus.UpToDate, result.Status);
        Assert.Equal("已是最新版本", result.Message);
    }

    [Fact]
    public async Task Plan_Failure_CarriesHumanReason()
    {
        var planner = new UpdatePlanner(
            new StubCheckService(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork)),
            BuildIdentity.Parse("1.17.0.sc.win-x64"),
            InstallKind.InnoInstall);

        var result = await planner.PlanAsync();

        Assert.Equal(UpdatePlanStatus.Failed, result.Status);
        Assert.Contains("连不上网", result.Message!);
    }

    [Fact]
    public async Task Plan_Canceled_ProducesNoMessage()
    {
        var planner = new UpdatePlanner(
            new StubCheckService(UpdateCheckResult.Fail("")),
            BuildIdentity.Parse("1.17.0.sc.win-x64"),
            InstallKind.InnoInstall);

        var result = await planner.PlanAsync();

        Assert.Equal(UpdatePlanStatus.Canceled, result.Status);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Plan_NoMatchingAsset_FailsWithExplanation()
    {
        var release = Entry("v1.18.0", false, "TeachCraft-v1.18.0-osx-arm64-sc.dmg");

        var planner = new UpdatePlanner(
            new StubCheckService(UpdateCheckResult.Ok(release, new[] { release })),
            BuildIdentity.Parse("1.17.0.sc.win-x64"),
            InstallKind.InnoInstall);

        var result = await planner.PlanAsync();

        Assert.Equal(UpdatePlanStatus.Failed, result.Status);
        Assert.Contains("暂未提供", result.Message!);
    }

    [Fact]
    public async Task Plan_LegacyInstall_FlagsModeExplanation()
    {
        var release = Entry("v1.18.0", false, "TeachCraft-v1.18.0-win-x64-sc-Setup.exe");

        var planner = new UpdatePlanner(
            new StubCheckService(UpdateCheckResult.Ok(release, new[] { release })),
            BuildIdentity.Parse(null, "1.17.0.0").WithRid("win-x64"),
            InstallKind.InnoInstallLegacy);

        var result = await planner.PlanAsync();

        Assert.True(result.Proposal!.NeedsModeExplanation);
        Assert.EndsWith("-sc-Setup.exe", result.Proposal.Asset.Name);
    }
}

/// <summary>
/// 状态栏入口的 ViewModel 契约。
/// 注意：现有 9 处 new MainViewModel() 必须继续可用且全绿。
/// </summary>
public sealed class MainViewModelUpdateTests
{
    [Fact]
    public void ExistingParameterlessConstruction_StillWorks()
    {
        var vm = new MainViewModel();

        Assert.False(string.IsNullOrWhiteSpace(vm.VersionLine));
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public void VersionText_ReflectsInjectedIdentity_NotTestAssembly()
    {
        // 测试程序集没有版本信息，若错误地从 typeof(MainViewModel).Assembly 读，
        // 这里会退化成 0.0.0-preview.0。
        var vm = new MainViewModel(identity: BuildIdentity.Parse("1.17.0-preview.142.sc.win-x64"));

        Assert.Contains("1.17.0-preview.142", vm.VersionLine);
    }

    [Fact]
    public void UpdateBadgeText_IsExactAndEmptyByDefault()
    {
        var vm = new MainViewModel(identity: BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.Equal("", vm.UpdateBadgeText);
    }

    [Fact]
    public void ApplyProposal_SetsExactBadgeText()
    {
        SemanticVersion.TryParse("1.18.0-preview.142", out var version);
        var release = new ReleaseEntry("v1.18.0-preview.142", version, true,
            new[] { new ReleaseAsset("TeachCraft-v1.18.0-preview.142-win-x64-sc-Setup.exe", 1, "https://e/x") });

        var vm = new MainViewModel(identity: BuildIdentity.Parse("1.17.0.sc.win-x64"));
        vm.ApplyUpdatePlan(UpdatePlanResult.HasUpdate(
            new UpdateProposal(release, release.Assets[0], InstallKind.InnoInstall, false)));

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal("新版本 1.18.0-preview.142", vm.UpdateBadgeText);
    }

    [Fact]
    public void WarningBrush_IsNotTheErrorBrush()
    {
        // AccentDeepBrush 与 SealBrush 都是 #A34A36，用错会让「有新版本」看起来像报错。
        // 必须先进入错误态才能拿到真正的错误色（默认态是绿色 Moss）。
        // 画刷实现是可变的 SolidColorBrush 或不可变的 ImmutableSolidColorBrush，
        // 因此一律按 ISolidColorBrush 接口取色，不硬转具体类型。
        var vm = new MainViewModel(identity: BuildIdentity.Parse("1.17.0.sc.win-x64"));
        vm.SetStatus("故意制造错误态", error: true);
        var errorColor = ((Avalonia.Media.ISolidColorBrush)vm.StatusBrush).Color;

        var warningColor = ((Avalonia.Media.ISolidColorBrush)vm.WarningBrush).Color;

        Assert.True(errorColor != warningColor, $"错误色={errorColor}，强调色={warningColor}，两者不应相同");
    }

    [Fact]
    public async Task SilentStartup_Failure_DoesNotTouchStatusText()
    {
        var vm = new MainViewModel(
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            checkService: new FailingCheckService());
        var before = vm.StatusText;

        await vm.CheckUpdateAsync(fromUserGesture: false);

        Assert.Equal(before, vm.StatusText);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task UserGesture_Failure_ShowsSpecificReason()
    {
        var vm = new MainViewModel(
            identity: BuildIdentity.Parse("1.17.0.sc.win-x64"),
            checkService: new FailingCheckService());

        await vm.CheckUpdateAsync(fromUserGesture: true);

        Assert.Contains("连不上网", vm.StatusText);
        Assert.False(vm.IsUpdateAvailable);
    }

    private sealed class FailingCheckService : IUpdateCheckService
    {
        public Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default)
            => Task.FromResult(UpdateCheckResult.Fail(UpdateErrorText.NoNetwork));
    }
}
