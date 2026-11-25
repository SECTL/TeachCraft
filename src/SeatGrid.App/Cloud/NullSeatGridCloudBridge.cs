using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SeatGrid.Core.Cloud;

namespace SeatGrid.App.Cloud;

/// <summary>
/// 宿主未接入云端时的唯一实现。所有属性都是「不可用」，所有方法都是空操作或返回空集合，
/// 构造与调用都不碰本机账号目录、不发请求。走查与单元测试一律注入它。
/// </summary>
public sealed class NullSeatGridCloudBridge : ISeatGridCloudBridge
{
    public bool IsAvailable => false;
    public bool IsSignedIn => false;
    public string StatusText => "云端存档未接入。";
    public string RosterConsentText => string.Empty;
    public bool IsAutoSyncEnabled => false;
    public void SetAutoSyncEnabled(bool value) { }
    public SeatGridRosterFields LastRosterFields => SeatGridRosterFields.None;
    public void RememberRosterFields(SeatGridRosterFields fields) { }

    public string BuildSeatConsentText(string rosterName, int rosterVersion, string fieldsSummary)
        => string.Empty;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task<bool> EnsureSignedInAsync(Window owner) => Task.FromResult(false);

    public void OpenAccountSettings(Window owner) { }

    public Task<IReadOnlyList<SeatGridRosterEntry>> ListRostersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SeatGridRosterEntry>>(Array.Empty<SeatGridRosterEntry>());

    public Task<SeatGridRosterPick?> DownloadRosterAsync(string rosterId, CancellationToken ct)
        => Task.FromResult<SeatGridRosterPick?>(null);

    public Task<SeatGridRosterEntry> UploadRosterAsync(
        string name,
        IReadOnlyList<SeatGridRosterStudent> projectedStudents,
        SeatGridRosterFields fields,
        string? existingId,
        int expectedVersion,
        CancellationToken ct)
        => Task.FromException<SeatGridRosterEntry>(
            new InvalidOperationException("云端存档未接入，无法上传共享名单。"));

    public Task DeleteRosterAsync(string rosterId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<SeatGridCloudEntry>> ListDocumentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SeatGridCloudEntry>>(Array.Empty<SeatGridCloudEntry>());

    public Task<SeatGridCloudPull?> DownloadDocumentAsync(string documentId, CancellationToken ct)
        => Task.FromResult<SeatGridCloudPull?>(null);

    public Task<SeatGridCloudSaveResult> UploadDocumentAsync(
        SeatGridCloudDocument document, string? replacingId, int expectedRevision, CancellationToken ct)
        => Task.FromException<SeatGridCloudSaveResult>(
            new InvalidOperationException("云端存档未接入，无法上传座位表。"));

    public Task DeleteDocumentAsync(string documentId, CancellationToken ct) => Task.CompletedTask;

    public void RememberDocument(string? id, int revision) { }
}
