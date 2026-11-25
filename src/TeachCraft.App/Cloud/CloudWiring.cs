using System.Net.Http;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Updating;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 云端服务的唯一装配点。App 启动时调用一次，其余任何地方都不许直接 new
/// SectlAccountService / SectlCloudDocumentStore / SectlSharedRosterSource——
/// 否则走查路径会漏掉「默认关闭」这道闸，一次截图就真去连平台了。
/// </summary>
public static class CloudWiring
{
    /// <summary>
    /// 走查、配置不全、平台未配置三种情况一律返回 <see cref="CloudRuntime.Disabled"/>，
    /// 即使本机 settings.json 里写着 Enabled=true 也不装配服务。
    /// </summary>
    public static CloudRuntime Create(
        bool isWalkthrough,
        string? clientId,
        string? redirectUri,
        string? appDataRoot = null,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null)
    {
        if (isWalkthrough || string.IsNullOrWhiteSpace(clientId) || !SectlPlatformDefaults.IsConfigured)
            return CloudRuntime.Disabled;

        var deviceUuid = new FileInstallIdentityStore(appDataRoot).GetOrCreateDeviceUuid();
        var options = SectlCloudOptions.TryCreate(clientId, redirectUri, deviceUuid);
        if (options is null) return CloudRuntime.Disabled;

        var settingsStore = appDataRoot is null
            ? new FileCloudSyncSettingsStore()
            : new FileCloudSyncSettingsStore(appDataRoot);
        var http = handler ?? SharedHandlers.Default;

        var account = new SectlAccountService(
            http, options,
            new FileCloudTokenStore(appDataRoot),
            new FileCloudAccountStore(appDataRoot),
            time);

        var seatDocuments = new SectlCloudDocumentStore(
            http, options, account, CloudDocumentNamespace.SeatGrid, time);

        // 名单与座位表是两个独立键空间：同一个账号、同一个 handler，但命名空间不同，
        // 不能共用一个 store 实例，否则一份的索引会写进另一份的键里。
        var rosterStore = new SectlCloudDocumentStore(
            http, options, account, CloudDocumentNamespace.Roster, time);
        var rosters = new SectlSharedRosterSource(rosterStore, time);

        return new CloudRuntime(true, account, seatDocuments, rosters, settingsStore, settingsStore.Load());
    }
}
