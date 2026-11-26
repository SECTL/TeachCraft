using Avalonia.Controls;
using Homework.Core.Roster;

namespace Homework.App.Cloud;

/// <summary>共享名单库里的一份班级名单摘要。只带展示所需字段，不含学生明细。</summary>
public sealed record HomeworkRosterEntry(
    string RosterId,
    string Name,
    int Version,
    DateTimeOffset UpdatedAtUtc,
    int StudentCount,
    string FieldsSummary);

/// <summary>
/// 辑册访问共享名单的只读桥。刻意不暴露任何上传 / 保存 / 删除入口：
/// 辑册只拉取，发布名单是「归位」的职责。
/// </summary>
public interface IHomeworkRosterBridge
{
    /// <summary>已配置、已登录且用户已开启云同步时为 true。</summary>
    bool IsAvailable { get; }

    /// <summary>是否已登录 SECTL 账号；未登录与已登录未开同步需分别给出引导文案。</summary>
    bool IsSignedIn => IsAvailable;

    event EventHandler? StateChanged;

    /// <summary>未登录时打开账号窗口；返回结束时是否已登录。</summary>
    Task<bool> EnsureSignedInAsync(Window owner);

    Task<IReadOnlyList<HomeworkRosterEntry>> ListRostersAsync(CancellationToken ct);

    /// <summary>按选中的名单 Id 拉取并映射成辑册既有的名单结果。</summary>
    Task<RosterImportResult> DownloadAsync(IReadOnlyList<string> rosterIds, CancellationToken ct);

    void OpenAccountSettings(Window owner);
}
