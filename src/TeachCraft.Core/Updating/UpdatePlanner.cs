namespace TeachCraft.Core.Updating;

/// <summary>
/// 把「检查结果」变成「用户能看懂的提案」：
/// 选哪一支版本、下哪个资产、该走哪种安装方式。
///
/// 构建身份与安装形态都从外部注入，这样界面层不需要知道探测细节，
/// 也保证测试能把 GuardedHttpHandler 一路穿到下载路径。
/// </summary>
public sealed class UpdatePlanner
{
    private readonly IUpdateCheckService _checkService;
    private readonly BuildIdentity _identity;
    private readonly InstallKind _kind;

    public UpdatePlanner(IUpdateCheckService checkService, BuildIdentity identity, InstallKind kind)
    {
        _checkService = checkService;
        _identity = identity;
        _kind = kind;
    }

    public BuildIdentity Identity => _identity;

    public InstallKind Kind => _kind;

    public async Task<UpdatePlanResult> PlanAsync(CancellationToken cancellationToken = default)
    {
        if (_identity.Rid is null)
        {
            return UpdatePlanResult.Fail("认不出这台电脑上装的是哪一支（缺少运行标识），暂时无法自动选择更新包。");
        }

        var check = await _checkService.CheckAsync(_identity, cancellationToken).ConfigureAwait(false);
        if (!check.IsSuccess)
        {
            // 空字符串表示用户主动取消，此时不该弹任何错误。
            return string.IsNullOrEmpty(check.Failure)
                ? UpdatePlanResult.Canceled()
                : UpdatePlanResult.Fail(check.Failure);
        }

        if (check.Best is null)
            return UpdatePlanResult.UpToDate(_identity.Version.ToString());

        var asset = UpdateAssetSelector.Select(check.Best.Assets, _identity, _kind);
        if (asset is null)
        {
            // 版本一个资产都没有：镜像同步是分步的（版本先登记、包后到位），
            // 这时说「暂未提供适合当前安装方式」会误导——不是没有这一支，是整个版本还没同步完。
            if (check.Best.Assets.Count == 0)
            {
                return UpdatePlanResult.Fail(
                    $"新版本 {check.Best.Version} 已在镜像上登记，但安装包还没同步完成，请稍后再试。");
            }

            return UpdatePlanResult.Fail(
                $"新版本 {check.Best.Version} 暂未提供适合当前安装方式的安装包（{DescribeKind(_kind)}），请稍后再试或到发布页手动选择。");
        }

        return UpdatePlanResult.HasUpdate(new UpdateProposal(
            check.Best, asset, _kind, UpdateAssetSelector.NeedsModeExplanation(_identity)));
    }

    private static string DescribeKind(InstallKind kind) => kind switch
    {
        InstallKind.InnoInstall => "安装包版本",
        InstallKind.InnoInstallLegacy => "安装包版本",
        InstallKind.PortableZip => "绿色版",
        InstallKind.MacBundle => "macOS 应用",
        InstallKind.MacBundlePortable => "macOS 便携应用",
        InstallKind.LinuxPortable => "Linux 绿色版",
        _ => "当前平台",
    };
}

/// <summary>一次规划的结果。三种终态：有新版 / 已最新 / 失败（另含取消）。</summary>
public sealed record UpdatePlanResult(UpdatePlanStatus Status, UpdateProposal? Proposal, string? Message, string? CurrentVersion)
{
    public static UpdatePlanResult HasUpdate(UpdateProposal proposal)
        => new(UpdatePlanStatus.UpdateAvailable, proposal, null, null);

    public static UpdatePlanResult UpToDate(string currentVersion)
        => new(UpdatePlanStatus.UpToDate, null, "已是最新版本", currentVersion);

    public static UpdatePlanResult Fail(string message)
        => new(UpdatePlanStatus.Failed, null, message, null);

    public static UpdatePlanResult Canceled()
        => new(UpdatePlanStatus.Canceled, null, null, null);
}

public enum UpdatePlanStatus
{
    UpdateAvailable,
    UpToDate,
    Failed,
    Canceled,
}
