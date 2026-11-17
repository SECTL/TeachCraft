namespace TeachCraft.App.Updating;

/// <summary>
/// 安装更新前扫描仍持有未保存工作的常备窗口。
///
/// 审计 P1-3：`desktop.Shutdown()` 会直接关掉所有窗口，绕过归位 / 竞名 / 分晓
/// 各自写在 OnClosing 里的未保存确认——手动点 X 有防线，点「更新」反而没有，
/// 而更新恰恰是更容易被随手点的那个动作。
///
/// 抽成不依赖 Avalonia 静态类是为了可测：判定逻辑只吃「标题 + 是否有未保存内容」，
/// 窗口枚举留在 App 层。
/// </summary>
public static class UnsavedWindowGuard
{
    /// <summary>按 (标题, 是否有未保存内容) 分拣出需要提醒的窗口标题。</summary>
    public static IReadOnlyList<string> FindUnsaved(IEnumerable<(string Title, bool HasUnsaved)> windows) =>
        windows.Where(w => w.HasUnsaved).Select(w => w.Title).ToList();

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
        GradeStrip.App.ViewModels.MainViewModel strip => strip.HasData,
        _ => false,
    };
}
