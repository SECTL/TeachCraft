using Avalonia.Controls;
using Homework.Core.Roster;

namespace Homework.App.Cloud;

/// <summary>
/// 未接入 / 走查 / 测试用的空桥。所有方法都是无副作用的空操作，
/// 保证「没有云端能力」时辑册仍能正常打开、按钮不会抛异常。
/// </summary>
public sealed class NullHomeworkRosterBridge : IHomeworkRosterBridge
{
    public static NullHomeworkRosterBridge Instance { get; } = new();

    private NullHomeworkRosterBridge() { }

    public bool IsAvailable => false;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task<bool> EnsureSignedInAsync(Window owner) => Task.FromResult(false);

    public Task<IReadOnlyList<HomeworkRosterEntry>> ListRostersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HomeworkRosterEntry>>(Array.Empty<HomeworkRosterEntry>());

    public Task<RosterImportResult> DownloadAsync(IReadOnlyList<string> rosterIds, CancellationToken ct)
        => Task.FromResult(new RosterImportResult(
            Array.Empty<Homework.Core.Models.HomeworkClass>(),
            Array.Empty<Homework.Core.Models.HomeworkStudent>(),
            Array.Empty<string>(), 0));

    public void OpenAccountSettings(Window owner) { }
}
