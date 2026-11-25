using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SeatGrid.Core.Cloud;

namespace SeatGrid.App.Cloud;

/// <summary>共享名单库里的一行摘要。只带展示所需字段，不把学生明细拉进列表。</summary>
public sealed record SeatGridRosterEntry(
    string RosterId,
    string Name,
    int Version,
    DateTimeOffset UpdatedAtUtc,
    int StudentCount,
    SeatGridRosterFields Fields,
    string StatusText = "");

/// <summary>拉取一份共享名单后的完整内容。</summary>
public sealed record SeatGridRosterPick(
    string RosterId,
    string Name,
    int Version,
    IReadOnlyList<SeatGridRosterStudent> Students,
    SeatGridRosterFields Fields);

/// <summary>云端座位表索引里的一行摘要。</summary>
public sealed record SeatGridCloudEntry(
    string Id,
    string Name,
    DateTimeOffset UpdatedAtUtc,
    int Bytes,
    int Revision);

/// <summary>
/// 拉取一份云端座位表的结果。<see cref="LatestRoster"/> 是当前共享名单，
/// <see cref="SnapshotRoster"/> 是文档内嵌的兜底快照；两者都可能为 null，
/// 由调用方决定用哪一份还原。
/// </summary>
public sealed record SeatGridCloudPull(
    SeatGridCloudDocument Document,
    SeatGridRosterInput? LatestRoster,
    SeatGridRosterInput? SnapshotRoster,
    IReadOnlyList<string> Warnings,
    string? DocumentId = null,
    int Revision = 0);

/// <summary>一次座位表上传的结果。</summary>
public sealed record SeatGridCloudSaveResult(SeatGridCloudEntry Entry, bool Overwritten);

/// <summary>
/// 归位与宿主之间的云端能力边界。归位侧只认这些语义化方法，
/// 不引用任何 HTTP / 鉴权 / 存储实现；宿主没接入时注入 <see cref="NullSeatGridCloudBridge"/>。
/// </summary>
public interface ISeatGridCloudBridge
{
    bool IsAvailable { get; }
    bool IsSignedIn { get; }
    string StatusText { get; }
    string RosterConsentText { get; }

    /// <summary>座位表 / 约束的自动保存开关。默认关闭，由用户在归位右侧面板主动打开。</summary>
    bool IsAutoSyncEnabled { get; }

    /// <summary>把自动保存开关写进本机偏好；不建立连接、不立即上传。</summary>
    void SetAutoSyncEnabled(bool value);

    /// <summary>上次发布共享名单时勾选的可选字段，用于新建名单时预填勾选框。默认全关。</summary>
    SeatGridRosterFields LastRosterFields { get; }

    /// <summary>记住本次发布勾选的字段；只写本机偏好，不建立连接。</summary>
    void RememberRosterFields(SeatGridRosterFields fields);

    string BuildSeatConsentText(string rosterName, int rosterVersion, string fieldsSummary);

    event EventHandler? StateChanged;

    Task<bool> EnsureSignedInAsync(Window owner);
    void OpenAccountSettings(Window owner);

    Task<IReadOnlyList<SeatGridRosterEntry>> ListRostersAsync(CancellationToken ct);
    Task<SeatGridRosterPick?> DownloadRosterAsync(string rosterId, CancellationToken ct);
    Task<SeatGridRosterEntry> UploadRosterAsync(
        string name,
        IReadOnlyList<SeatGridRosterStudent> projectedStudents,
        SeatGridRosterFields fields,
        string? existingId,
        int expectedVersion,
        CancellationToken ct);
    Task DeleteRosterAsync(string rosterId, CancellationToken ct);

    Task<IReadOnlyList<SeatGridCloudEntry>> ListDocumentsAsync(CancellationToken ct);
    Task<SeatGridCloudPull?> DownloadDocumentAsync(string documentId, CancellationToken ct);
    Task<SeatGridCloudSaveResult> UploadDocumentAsync(
        SeatGridCloudDocument document, string? replacingId, int expectedRevision, CancellationToken ct);
    Task DeleteDocumentAsync(string documentId, CancellationToken ct);

    /// <summary>
    /// 记住拉取到的座位表身份。宿主把它写进设置，重启后继续更新同一份而不是静默新建副本。
    /// </summary>
    void RememberDocument(string? id, int revision);
}
