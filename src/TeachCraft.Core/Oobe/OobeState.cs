namespace TeachCraft.Core.Oobe;

/// <summary>用户结束引导的方式。跳过与看完等价——两者都表示「不再自动显示」。</summary>
public enum OobeDismissal
{
    Completed,
    Skipped,
}

/// <summary>
/// 首次引导的本机状态。只记「用户是否已经结束过引导」，不记任何与身份有关的东西。
///
/// 没有版本字段：引导只在从未结束过时显示，与版本无关。
/// 早期版本存过 `LastNotifiedVersion`（用于「升级后再告知一次」），那个功能已删除——
/// 它让引导在升级后又冒出来，与「只在第一次打开软件时显示」冲突。
/// 旧文件里多出的字段会被忽略，不影响读取。
/// </summary>
/// <param name="DismissedUtc">结束引导的时间；null 表示从未结束过（即首次运行）。</param>
/// <param name="DismissedHow">结束方式：看完 / 跳过。null 表示从未结束过。</param>
/// <param name="SchemaVersion">磁盘格式版本，当前为 1。</param>
public sealed record OobeState(
    DateTimeOffset? DismissedUtc,
    OobeDismissal? DismissedHow,
    int SchemaVersion = OobeState.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;

    public static OobeState Empty { get; } = new(null, null);

    /// <summary>是否已经结束过引导（看完或跳过都算）。</summary>
    public bool IsDismissed => DismissedUtc is not null;
}

/// <summary>状态存储的抽象，便于走查与测试注入一个什么都不做的实现。</summary>
public interface IOobeStateStore
{
    OobeState Load();
    void Save(OobeState state);
}

/// <summary>走查 / 自动化用的空实现：读永远为空，写永远丢弃。</summary>
public sealed class NoopOobeStateStore : IOobeStateStore
{
    public OobeState Load() => OobeState.Empty;
    public void Save(OobeState state) { }
}
