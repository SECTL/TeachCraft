namespace TeachCraft.App.Updating;

/// <summary>安装前置闸门的裁定结果。</summary>
public enum InstallGateOutcome
{
    /// <summary>没有未保存内容，或用户明确确认——可以启动安装包 / 替换助手。</summary>
    Proceed,

    /// <summary>用户在确认弹窗里点了取消（或关掉弹窗）——绝不能启动任何安装进程。</summary>
    Cancelled,
}

/// <summary>
/// 安装更新前扫描仍持有未保存工作的常备窗口。
///
/// 审计 P1-3：`desktop.Shutdown()` 会直接关掉所有窗口，绕过归位 / 竞名 / 分晓
/// 各自写在 OnClosing 里的未保存确认——手动点 X 有防线，点「更新」反而没有，
/// 而更新恰恰是更容易被随手点的那个动作。
///
/// 审计 P1-1：这道闸门必须在安装包 / 替换助手**启动之前**生效——排在 Process.Start
/// 之后时，「暂不安装」拦不住已在后台运行的 Inno。扫描与弹窗的位置由
/// <see cref="DecideInstallGate"/> 的契约锁住：Cancelled 时调用方必须先于任何
/// 助手进程启动就返回。
///
/// 抽成不依赖 Avalonia 的静态类是为了可测：判定逻辑只吃「标题 + 是否有未保存内容」，
/// 窗口枚举留在 App 层。
/// </summary>
public static class UnsavedWindowGuard
{
    /// <summary>按 (标题, 是否有未保存内容) 分拣出需要提醒的窗口标题。</summary>
    public static IReadOnlyList<string> FindUnsaved(IEnumerable<(string Title, bool HasUnsaved)> windows) =>
        windows.Where(w => w.HasUnsaved).Select(w => w.Title).ToList();

    /// <summary>
    /// 安装前置闸门的可测契约：有未保存内容时必须拿到用户确认，任何取消都不得启动安装。
    /// 干净的窗口集合不需要弹窗，直接放行。
    /// </summary>
    public static InstallGateOutcome DecideInstallGate(bool hasUnsavedWindows, bool userConfirmed) =>
        hasUnsavedWindows && !userConfirmed ? InstallGateOutcome.Cancelled : InstallGateOutcome.Proceed;

    /// <summary>
    /// 各常备窗口的「有未保存内容」判定。DataContext 类型不匹配时视为无。
    ///
    /// 织序刻意不在名单里：它有 2 秒自动保存 + 关窗同步落盘，数据不会丢；
    /// 把它算进来只会让每次更新都多一道没有实际意义的确认。
    /// </summary>
    public static bool HasUnsavedWork(object? dataContext) => dataContext switch
    {
        SeatGrid.App.SeatGridViewModel seat => seat.StudentCount > 0,
        MeetRoster.App.MeetRosterViewModel meet => meet.IsDirty,
        // 只导入过分析报表（没有成绩会话）同样只存在内存里（审计 P3-5），
        // 所以判 HasUnsavedContent 而不是 HasData。
        GradeStrip.App.ViewModels.MainViewModel strip => strip.HasUnsavedContent,
        // 时序导出的课表只存在于内存里，关掉就没了——有总表就值得问一句（审计 P2-26）。
        ClassBlock.App.ClassBlockViewModel block => block.HasMaster,
        _ => false,
    };
}
