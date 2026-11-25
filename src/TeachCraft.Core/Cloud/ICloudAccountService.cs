namespace TeachCraft.Core.Cloud;

/// <summary>账号状态。界面只绑定这一处，不自己缓存登录态。</summary>
public interface ICloudAccountService
{
    bool IsSignedIn { get; }
    CloudAccount? Current { get; }
    event EventHandler? StateChanged;

    /// <summary>
    /// 退出时远端撤销失败才填这句提示；成功或从未登录时为空。
    /// 默认空：走查用的 CaptureAccountService 与测试替身无需实现，真实服务自己覆盖。
    /// </summary>
    string? LastSignOutNotice => null;

    CloudSignInRequest CreateSignInRequest();

    Task<CloudAccount> CompleteSignInAsync(
        CloudSignInRequest request,
        string code,
        string state,
        CancellationToken cancellationToken);

    Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 401 后按当前失败凭据强制刷新一次；并发下若已被其他调用方刷新则直接采纳新凭据。
    /// 默认回落到常规取票，避免现有测试替身实现成本过高。
    /// </summary>
    Task<string> ForceRefreshAccessTokenAsync(string failedAccessToken, CancellationToken cancellationToken)
        => GetValidAccessTokenAsync(cancellationToken);

    Task SignOutAsync(CancellationToken cancellationToken);
}
