using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Homework.App.Cloud;
using Homework.Core.Roster;
using TeachCraft.App.ViewModels;
using TeachCraft.App.Views;
using TeachCraft.Core.Cloud;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 宿主侧把云端能力翻译成作业整理的只读桥。
/// <para>
/// 「已配置」由宿主保证：未配置 / 走查时宿主注入 <see cref="NullHomeworkRosterBridge"/>，
/// 本类根本不会被构造，因此这里只判登录态与用户开关。
/// </para>
/// 拉取路径只调用 <see cref="ISharedRosterSource.GetAsync"/>，没有写接口、没有 HTTP 类型。
/// </summary>
public sealed class HomeworkRosterBridge : IHomeworkRosterBridge, IDisposable
{
    private readonly ICloudAccountService _account;
    private readonly ISharedRosterSource _rosters;
    private readonly ICloudSyncSettingsStore _settings;
    private readonly AccountViewModel _accountViewModel;
    private readonly CloudStateNotifier _notifier;
    private bool _disposed;

    public HomeworkRosterBridge(
        ICloudAccountService account,
        ISharedRosterSource rosters,
        ICloudSyncSettingsStore settings)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        var runtime = new CloudRuntime(true, _account, null, _rosters, _settings, _settings.Load());
        var signIn = new DesktopAccountSignInFlow(
            _account, new ShellBrowserLauncher(), new LoopbackOAuthReceiver());
        _accountViewModel = new AccountViewModel(runtime, signIn);
        _account.StateChanged += OnAccountStateChanged;
        _notifier = runtime.Notifier;
        _notifier.StateChanged += OnCloudStateChanged;
    }

    public bool IsAvailable => _account.IsSignedIn && _settings.Load().Enabled;
    public bool IsSignedIn => _account.IsSignedIn;

    public event EventHandler? StateChanged;

    public async Task<bool> EnsureSignedInAsync(Window owner)
    {
        if (_account.IsSignedIn) return true;
        await AccountWindow.ShowModalAsync(_accountViewModel, owner);
        return _account.IsSignedIn;
    }

    public async Task<IReadOnlyList<HomeworkRosterEntry>> ListRostersAsync(CancellationToken ct)
    {
        if (!IsAvailable) return Array.Empty<HomeworkRosterEntry>();
        var infos = await _rosters.ListAsync(ct);
        return infos.Select(info => new HomeworkRosterEntry(
            info.RosterId,
            info.Name,
            info.Version,
            info.UpdatedAtUtc,
            info.StudentCount,
            info.LoadFailed ? info.LoadError ?? "读取失败" : DescribeFields(info.Fields))).ToArray();
    }

    public async Task<RosterImportResult> DownloadAsync(
        IReadOnlyList<string> rosterIds, CancellationToken ct)
    {
        if (!IsAvailable) throw new CloudAuthException(CloudErrorText.CloudSyncOff);
        var classes = new List<SharedRosterClassInput>();
        var warnings = new List<string>();
        var missingRosters = 0;

        foreach (var id in rosterIds)
        {
            ct.ThrowIfCancellationRequested();
            SharedRosterDocument? doc;
            try
            {
                doc = await _rosters.GetAsync(id, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new CloudAuthException(ex.Message, "invalid_roster", ex);
            }
            if (doc is null)
            {
                missingRosters++;
                warnings.Add($"{SharedRosterImport.MissingRosterMarker}：{CloudErrorText.NotFound}（{id}）");
                continue;
            }

            classes.Add(new SharedRosterClassInput(
                doc.Name,
                doc.Students.Select(student => new SharedRosterStudentInput(student.Name, student.Number)).ToArray()));
        }

        return SharedRosterImport.ToRosterImportResult(classes, warnings, missingRosters);
    }

    public void OpenAccountSettings(Window owner)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (desktop.MainWindow is not { } main) return;

        main.Activate();
        (main as MainWindow)?.DataContextAsMainViewModel?.OpenAccountPanel();
    }

    /// <summary>摘要只说缺了哪些可选字段；必填的姓名 / 学号始终存在。</summary>
    private static string DescribeFields(RosterFieldPresence fields)
    {
        var missing = fields.MissingOptionalFields;
        return missing.Count == 0
            ? "可选字段齐全"
            : "缺少可选字段：" + string.Join("、", missing);
    }

    private void OnAccountStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _accountViewModel.Refresh();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloudStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _accountViewModel.Refresh();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _account.StateChanged -= OnAccountStateChanged;
        _notifier.StateChanged -= OnCloudStateChanged;
        _accountViewModel.Dispose();
    }
}
