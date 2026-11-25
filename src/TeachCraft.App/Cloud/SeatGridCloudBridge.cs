using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SeatGrid.App;
using SeatGrid.App.Cloud;
using SeatGrid.Core.Cloud;
using TeachCraft.App.ViewModels;
using TeachCraft.App.Views;
using TeachCraft.Core.Cloud;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 归位云端能力的宿主实现。只做协议转换与编排：名单 / 座位表的读写全部交给注入的 Core 服务，
/// 本类不碰 HTTP、不碰令牌、不缓存学生明细。
/// </summary>
public sealed class SeatGridCloudBridge : ISeatGridCloudBridge, ISeatGridLastDocumentSource, IDisposable
{
    private readonly ICloudAccountService _account;
    private readonly ISharedRosterSource _rosters;
    private readonly ICloudDocumentStore _seatDocuments;
    private readonly ICloudSyncSettingsStore _settings;
    private readonly CloudRuntime _runtime;
    private readonly AccountViewModel _accountViewModel;
    private readonly CloudStateNotifier _notifier;
    private bool _disposed;

    public SeatGridCloudBridge(
        ICloudAccountService account,
        ISharedRosterSource rosters,
        ICloudDocumentStore seatDocuments,
        ICloudSyncSettingsStore settings)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _seatDocuments = seatDocuments ?? throw new ArgumentNullException(nameof(seatDocuments));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _runtime = new CloudRuntime(true, _account, _seatDocuments, _rosters, _settings, _settings.Load());
        var signIn = new DesktopAccountSignInFlow(_account, new ShellBrowserLauncher(), new LoopbackOAuthReceiver());
        _accountViewModel = new AccountViewModel(_runtime, signIn);
        _account.StateChanged += OnAccountStateChanged;
        _notifier = _runtime.Notifier;
        _notifier.StateChanged += OnCloudStateChanged;
    }

    public bool IsAvailable => _settings.Load().Enabled && _account.IsSignedIn;

    public bool IsSignedIn => _account.IsSignedIn;

    public bool IsAutoSyncEnabled => _settings.Load().AutoSyncSeatDocument;

    public void SetAutoSyncEnabled(bool value)
    {
        var current = _settings.Load();
        if (current.AutoSyncSeatDocument == value) return;
        Persist(current with { AutoSyncSeatDocument = value });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? _settingsWarning;

    /// <summary>写入偏好；失败时记下原因并在状态里如实说明，不假装成功。</summary>
    private void Persist(CloudSyncSettings next)
        => _settingsWarning = _settings.TrySave(next)
            ? null
            : "设置未能保存到本机，本次修改仅在本次运行有效。";

    public SeatGridRosterFields LastRosterFields
    {
        get
        {
            var s = _settings.Load();
            return new SeatGridRosterFields(
                s.LastRosterHeight, s.LastRosterVision, s.LastRosterScore,
                s.LastRosterNotes, s.LastRosterOriginalGroup);
        }
    }

    public void RememberRosterFields(SeatGridRosterFields fields)
    {
        var current = _settings.Load();
        Persist(current with
        {
            LastRosterHeight = fields.Height,
            LastRosterVision = fields.Vision,
            LastRosterScore = fields.Score,
            LastRosterNotes = fields.Notes,
            LastRosterOriginalGroup = fields.OriginalGroup,
        });
    }

    public string StatusText
    {
        get
        {
            var status = !IsAvailable
                ? "云端存档未开启：请在「账号与共享名单」里登录并打开云同步。"
                : $"已登录：{_account.Current?.DisplayName} · 云同步已开启（仅手动上传 / 拉取）";
            return _settingsWarning is null ? status : $"{status}（{_settingsWarning}）";
        }
    }

    public string RosterConsentText =>
        $"共享名单必填：{CloudConsentText.RequiredFields}；可选：{CloudConsentText.OptionalFields}（默认全关）。" +
        $"{CloudConsentText.UncheckedFieldsNotWritten}；{CloudConsentText.NoRawFiles}；" +
        $"{CloudConsentText.NoAutoUpload}。{CloudConsentText.CheckSchoolPolicy}。";

    public string BuildSeatConsentText(string rosterName, int rosterVersion, string fieldsSummary) =>
        $"这份座位表将关联共享名单「{rosterName}」（版本 {rosterVersion}），并内嵌一份名单快照（{fieldsSummary}）。" +
        $"{CloudConsentText.UncheckedFieldsNotWritten}；{CloudConsentText.NoAutoUpload}。" +
        $"{CloudConsentText.CheckSchoolPolicy}。";

    public event EventHandler? StateChanged;

    public async Task<bool> EnsureSignedInAsync(Window owner)
    {
        if (_account.IsSignedIn) return true;
        await AccountWindow.ShowModalAsync(_accountViewModel, owner);
        return _account.IsSignedIn;
    }

    public void OpenAccountSettings(Window owner)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (desktop.MainWindow is not { } main) return;

        main.Activate();
        (main as MainWindow)?.DataContextAsMainViewModel?.OpenAccountPanel();
    }

    public async Task<IReadOnlyList<SeatGridRosterEntry>> ListRostersAsync(CancellationToken ct)
    {
        if (!IsAvailable) return Array.Empty<SeatGridRosterEntry>();
        var list = await _rosters.ListAsync(ct);
        return list.Select(info => new SeatGridRosterEntry(
            info.RosterId, info.Name, info.Version, info.UpdatedAtUtc, info.StudentCount,
            SeatGridRosterBridgeMapper.ToFields(info.Fields),
            info.LoadFailed ? info.LoadError ?? "读取失败" : string.Empty)).ToList();
    }

    public async Task<SeatGridRosterPick?> DownloadRosterAsync(string rosterId, CancellationToken ct)
    {
        if (!IsAvailable) return null;
        SharedRosterDocument? doc;
        try
        {
            doc = await _rosters.GetAsync(rosterId, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Core 解码/版本校验抛标准异常以守住单测契约；桥层转为带 UserFacingMessage 的云端异常供界面原样提示。
            throw new CloudAuthException(ex.Message, "invalid_roster", ex);
        }
        if (doc is null) return null;

        var input = SeatGridRosterBridgeMapper.ToInput(doc);
        return new SeatGridRosterPick(
            doc.RosterId, doc.Name, doc.Version, input.Students,
            SeatGridRosterBridgeMapper.ToFields(doc.Fields));
    }

    public async Task<SeatGridRosterEntry> UploadRosterAsync(
        string name,
        IReadOnlyList<SeatGridRosterStudent> projectedStudents,
        SeatGridRosterFields fields,
        string? existingId,
        int expectedVersion,
        CancellationToken ct)
    {
        EnsureAvailable();
        var doc = SeatGridRosterBridgeMapper.ToDocument(name, projectedStudents, fields);
        SharedRosterInfo info;
        try
        {
            info = await _rosters.SaveAsync(
                doc,
                RosterFieldSelection.FromPresence(SeatGridRosterBridgeMapper.ToPresence(fields)),
                existingId, expectedVersion, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new CloudAuthException(ex.Message, "validation", ex);
        }
        return new SeatGridRosterEntry(
            info.RosterId, info.Name, info.Version, info.UpdatedAtUtc, info.StudentCount,
            SeatGridRosterBridgeMapper.ToFields(info.Fields));
    }

    public Task DeleteRosterAsync(string rosterId, CancellationToken ct)
    {
        EnsureAvailable();
        return _rosters.DeleteAsync(rosterId, ct);
    }

    public async Task<IReadOnlyList<SeatGridCloudEntry>> ListDocumentsAsync(CancellationToken ct)
    {
        if (!IsAvailable) return Array.Empty<SeatGridCloudEntry>();
        var list = await _seatDocuments.ListAsync(ct);
        return list
            .Where(info => string.Equals(info.Kind, CloudDocumentNamespace.SeatGrid.Kind, StringComparison.Ordinal))
            .Select(info => new SeatGridCloudEntry(
                info.Id, info.Name, info.UpdatedAtUtc, info.Bytes, info.Revision))
            .ToList();
    }

    public async Task<SeatGridCloudPull?> DownloadDocumentAsync(string documentId, CancellationToken ct)
    {
        if (!IsAvailable) return null;
        var payload = await _seatDocuments.GetAsync(documentId, ct);
        if (payload is null) return null;

        var doc = SeatGridCloudCodec.Decode(payload.Json);
        var warnings = new List<string>();

        SeatGridRosterInput? latest = null;
        if (!string.IsNullOrWhiteSpace(doc.RosterId))
        {
            try
            {
                var roster = await _rosters.GetAsync(doc.RosterId, ct);
                if (roster is not null) latest = SeatGridRosterBridgeMapper.ToInput(roster);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                warnings.Add("关联的共享名单暂时读不到，已改用座位表内嵌的名单快照。");
            }
        }

        if (latest is null && doc.RosterSnapshot is null)
            warnings.Add("找不到关联的共享名单，且这份座位表没有内嵌名单快照，无法还原名单。");

        var snapshot = doc.RosterSnapshot is null
            ? null
            : SeatGridRosterBridgeMapper.ToSnapshot(doc.RosterSnapshot);

        return new SeatGridCloudPull(
            doc, latest, snapshot, warnings, payload.Info.Id, payload.Info.Revision);
    }

    public async Task<SeatGridCloudSaveResult> UploadDocumentAsync(
        SeatGridCloudDocument document, string? replacingId, int expectedRevision, CancellationToken ct)
    {
        EnsureAvailable();
        var json = SeatGridCloudCodec.Encode(document);
        var info = await _seatDocuments.PutAsync(
            replacingId, document.Name, CloudDocumentNamespace.SeatGrid.Kind, json, expectedRevision, ct);

        RememberDocument(info.Id, info.Revision);

        return new SeatGridCloudSaveResult(
            new SeatGridCloudEntry(info.Id, info.Name, info.UpdatedAtUtc, info.Bytes, info.Revision),
            expectedRevision > 0);
    }

    /// <summary>读取上次上传或拉取的座位表身份，供窗口重启后继续更新同一份而不是新建。</summary>
    public (string? Id, int Revision) GetLastDocument()
    {
        var settings = _settings.Load();
        return (settings.LastDocumentId, settings.LastDocumentRevision);
    }

    public void RememberDocument(string? id, int revision)
    {
        var current = _settings.Load();
        Persist(current with { LastDocumentId = id, LastDocumentRevision = revision });
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct)
    {
        EnsureAvailable();
        await _seatDocuments.DeleteAsync(documentId, ct);

        // 若删掉的正是上次记忆的云端座位表，清掉设置里的旧 ID/版本，避免下次上传命中 NotFound 死锁。
        var current = _settings.Load();
        if (string.Equals(current.LastDocumentId, documentId, StringComparison.Ordinal))
            Persist(current with { LastDocumentId = null, LastDocumentRevision = 0 });
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new CloudAuthException(CloudErrorText.CloudSyncOff);
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
