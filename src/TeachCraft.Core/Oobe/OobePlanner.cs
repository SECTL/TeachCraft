namespace TeachCraft.Core.Oobe;

public enum OobeShowReason
{
    /// <summary>从未结束过引导——唯一会显示的情况。</summary>
    FirstRun,

    /// <summary>已经结束过引导（看完或跳过），不再打扰。</summary>
    AlreadyDone,
}

/// <param name="Show">是否显示引导。</param>
/// <param name="StartAt">从第几页开始（1 起）；不显示时为 0。</param>
/// <param name="Reason">为什么显示 / 不显示，供文案与测试区分。</param>
public sealed record OobeDecision(bool Show, int StartAt, OobeShowReason Reason)
{
    public static OobeDecision Hidden(OobeShowReason reason) => new(false, 0, reason);
}

/// <summary>
/// 决定启动时要不要弹引导。
///
/// **规则只有一条**：用户从未结束过引导（既没看完也没跳过）才显示。
/// 结束过就再也不弹——不按版本号、不按天数、不按次数。
///
/// 曾经设计过「发布方推进通知版本就再告知一次」，已删除：
/// 那让引导在升级后又冒出来一次，与「只在第一次打开软件时显示」直接冲突。
/// 用户想重看有侧栏底部的「首次引导」入口，不需要程序自己冒出来。
/// </summary>
public static class OobePlanner
{
    public static OobeDecision Decide(OobeState state)
    {
        return state.IsDismissed
            ? OobeDecision.Hidden(OobeShowReason.AlreadyDone)
            : new OobeDecision(true, 1, OobeShowReason.FirstRun);
    }
}
