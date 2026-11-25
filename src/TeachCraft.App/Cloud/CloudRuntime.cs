using System.Runtime.CompilerServices;
using TeachCraft.Core.Cloud;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 界面层拿到的全部云端能力。服务为 null 就是「这条通道不存在」——
/// 界面据此显示未接入状态，绝不自己 new 一个服务出来补位。
/// </summary>
public sealed record CloudRuntime(
    bool IsConfigured,
    ICloudAccountService? Account,
    ICloudDocumentStore? SeatDocuments,
    ISharedRosterSource? Rosters,
    ICloudSyncSettingsStore Settings,
    CloudSyncSettings Snapshot)
{
    // 宿主把同一个 Settings 实例交给主面板与各模块桥；按它缓存广播点，
    // 主面板关掉同步时，已经打开的模块窗口才能立刻收到通知。
    // 用弱表而不是静态单例：不同设置存储（含走查的 Noop）互不串扰，也不留引用。
    private static readonly ConditionalWeakTable<ICloudSyncSettingsStore, CloudStateNotifier> Notifiers = new();

    /// <summary>云同步开关变更的唯一广播点。可用性本身仍以 <see cref="Settings"/> 为准。</summary>
    public CloudStateNotifier Notifier => Notifiers.GetValue(Settings, static _ => new CloudStateNotifier());

    /// <summary>
    /// 默认 / 走查 / 未配置时的唯一实例。用空存储，构造与读写都不碰本机账号目录，
    /// 更不会发请求——<c>new MainViewModel()</c> 必须能安全地在测试里跑。
    /// </summary>
    public static CloudRuntime Disabled { get; } =
        new(false, null, null, null, new NoopCloudSyncSettingsStore(), CloudSyncSettings.Default);
}
