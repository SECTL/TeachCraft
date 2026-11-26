using TeachCraft.Shared;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 基于 <see cref="ICloudDocumentStore"/> 的共享名单读写入口。
/// 只做投影、校验、编解码与版本映射，不碰 HTTP、不记录令牌或文档内容。
/// </summary>
public sealed class SectlSharedRosterSource : ISharedRosterSource
{
    private readonly ICloudDocumentStore _store;
    private readonly TimeProvider _time;

    public SectlSharedRosterSource(ICloudDocumentStore store, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SharedRosterInfo>> ListAsync(CancellationToken ct)
    {
        var infos = await _store.ListAsync(ct);
        var result = new List<SharedRosterInfo>(infos.Count);
        foreach (var info in infos)
        {
            SharedRosterDocument? doc = null;
            string? loadError = null;
            try
            {
                var payload = _store is SectlCloudDocumentStore kvStore
                    ? await kvStore.GetWithKnownInfoAsync(info, ct)
                    : await _store.GetAsync(info.Id, ct);
                if (payload is not null)
                    doc = SharedRosterCodec.Decode(payload.Json);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 单份文档读取失败（网络、损坏、版本不兼容）不影响整张列表，但必须如实标记，
                // 否则界面会把读不出的名单显示成 0 人的空壳。取消则照常向上抛。
                loadError = ErrorText.Describe(ex, "读取共享名单");
            }

            result.Add(FromInfo(info, doc, loadError is not null, loadError));
        }
        return result;
    }

    public async Task<SharedRosterDocument?> GetAsync(string rosterId, CancellationToken ct)
    {
        var payload = await _store.GetAsync(rosterId, ct);
        // 版本不兼容时让 Codec 的中文异常原样上抛，不包装成含糊的「处理失败」。
        return payload is null ? null : SharedRosterCodec.Decode(payload.Json);
    }

    public async Task<SharedRosterInfo> SaveAsync(
        SharedRosterDocument roster,
        RosterFieldSelection selection,
        string? existingId,
        int expectedVersion,
        CancellationToken ct)
    {
        // 新建时先定好 id：存储只在传入空 id 时自己生成一个，但文档里的 RosterId 必须
        // 与存储实际使用的 id 一致，否则拉取回来的名单带着空 id，关联座位表会失败。
        var id = string.IsNullOrWhiteSpace(existingId) ? Guid.NewGuid().ToString("N") : existingId!;

        // 先投影再校验：即使调用方对象里带着未勾选的值，也一律置空后才允许发布。
        var projected = SharedRosterProjection.Apply(roster.Students, selection);
        var doc = new SharedRosterDocument
        {
            V = 1,
            Kind = CloudDocumentNamespace.Roster.Kind,
            RosterId = id,
            Name = roster.Name,
            // 存储写入后的 revision：新建为 1，更新为 expectedVersion + 1。
            // 文档内版本必须与权威 revision 一致，拉取后才有意义的版本提示与冲突检测。
            Version = expectedVersion + 1,
            UpdatedAtUtc = _time.GetUtcNow(),
            Fields = selection.ToPresence(),
            Students = projected.ToList(),
        };
        SharedRosterValidator.Validate(doc);

        var json = SharedRosterCodec.Encode(doc);
        var info = await _store.PutAsync(
            id, roster.Name, CloudDocumentNamespace.Roster.Kind, json, expectedVersion, ct);
        return FromInfo(info, doc);
    }

    public Task DeleteAsync(string rosterId, CancellationToken ct)
        => _store.DeleteAsync(rosterId, ct);

    private static SharedRosterInfo FromInfo(
        CloudDocumentInfo info, SharedRosterDocument? doc,
        bool loadFailed = false, string? loadError = null)
        => new(
            info.Id,
            info.Name,
            info.Revision,
            info.UpdatedAtUtc,
            doc?.Students.Count ?? 0,
            info.Bytes,
            doc?.Fields ?? RosterFieldPresence.None,
            loadFailed,
            loadError);
}
