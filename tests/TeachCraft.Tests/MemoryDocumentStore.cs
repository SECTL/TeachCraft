using System.Text;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 内存版 <see cref="ICloudDocumentStore"/>，仅供测试：不联网、不落盘、不碰真实凭据。
/// 文档按 id 存原始 JSON，revision 从 1 起递增；可注入单份读取失败与版本冲突。
/// </summary>
internal sealed class MemoryDocumentStore : ICloudDocumentStore
{
    private readonly Dictionary<string, CloudDocumentPayload> _docs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failGet = new(StringComparer.Ordinal);
    private int _nextId;

    /// <summary>让指定 id 的下一次（及后续）读取抛错，用于验证列表的容错。</summary>
    public void FailGetFor(string id) => _failGet.Add(id);

    /// <summary>直接替换某份文档的 JSON（保留 revision），用于构造 V=2 等异常内容。</summary>
    public void Seed(string id, string json)
    {
        if (_docs.TryGetValue(id, out var existing))
        {
            _docs[id] = existing with
            {
                Json = json,
                Info = existing.Info with { Bytes = Encoding.UTF8.GetByteCount(json) },
            };
            return;
        }

        var info = new CloudDocumentInfo(
            id, "合成名单", CloudDocumentNamespace.Roster.Kind,
            DateTimeOffset.UtcNow, Encoding.UTF8.GetByteCount(json), 1);
        _docs[id] = new CloudDocumentPayload(info, json);
    }

    public Task<IReadOnlyList<CloudDocumentInfo>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<CloudDocumentInfo> result = _docs.Values.Select(p => p.Info).ToList();
        return Task.FromResult(result);
    }

    public Task<CloudDocumentPayload?> GetAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_failGet.Contains(id))
            throw new InvalidOperationException("模拟云端读取失败");
        return Task.FromResult(_docs.GetValueOrDefault(id));
    }

    public Task<CloudDocumentInfo> PutAsync(
        string? id, string name, string kind, string json, int expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (expectedRevision == 0)
        {
            if (id is not null && _docs.ContainsKey(id))
                throw new CloudConflictException(_docs[id].Info);
            id = (++_nextId).ToString("x32");
            var created = new CloudDocumentInfo(
                id, name, kind, DateTimeOffset.UtcNow, Encoding.UTF8.GetByteCount(json), 1);
            _docs[id] = new CloudDocumentPayload(created, json);
            return Task.FromResult(created);
        }

        if (!_docs.TryGetValue(id!, out var existing))
            throw new CloudConflictException(
                new CloudDocumentInfo(id!, name, kind, DateTimeOffset.UtcNow, 0, 0));
        if (existing.Info.Revision != expectedRevision)
            throw new CloudConflictException(existing.Info);

        var updated = existing.Info with
        {
            Name = name,
            Kind = kind,
            Bytes = Encoding.UTF8.GetByteCount(json),
            Revision = expectedRevision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _docs[id!] = new CloudDocumentPayload(updated, json);
        return Task.FromResult(updated);
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _docs.Remove(id);
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _docs.Clear();
        return Task.CompletedTask;
    }
}
