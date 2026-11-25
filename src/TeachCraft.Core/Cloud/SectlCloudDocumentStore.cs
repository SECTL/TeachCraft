using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 按命名空间隔离的云端文档存储。一份文档一个 KV，索引单独一份；
/// 覆盖前先读索引比较 revision，冲突不合并、不写入。
/// 所有容量与键长检查都在任何 HTTP 之前完成，超限不会留下半个文档。
/// </summary>
public sealed class SectlCloudDocumentStore : ICloudDocumentStore
{
    public const int MaxValueBytes = 64 * 1024;
    public const int CompressThresholdBytes = 40 * 1024;
    public const int MaxStoredBytes = 60 * 1024;
    public const int SoftDocumentLimit = 200;

    private const string GzipEncoding = "gzip+base64";

    private readonly ICloudKvClient _kv;
    private readonly CloudDocumentNamespace _ns;
    private readonly TimeProvider _time;

    public SectlCloudDocumentStore(
        HttpMessageHandler handler,
        SectlCloudOptions options,
        ICloudAccountService account,
        CloudDocumentNamespace ns,
        TimeProvider? time = null)
    {
        _kv = new SectlCloudKvClient(handler, options, account, time);
        _ns = ns;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<CloudDocumentInfo>> ListAsync(CancellationToken ct)
    {
        var index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var keys = (await _kv.ListAsync(ct).ConfigureAwait(false)).Entries;

        var docKeys = new HashSet<string>(
            keys.Select(entry => entry.Key)
                .Where(key => key.StartsWith(_ns.KeyPrefix, StringComparison.Ordinal)
                              && !string.Equals(key, _ns.IndexKey, StringComparison.Ordinal)),
            StringComparer.Ordinal);

        var byId = new HashSet<string>(index.Items.Select(item => item.Id), StringComparer.Ordinal);
        var result = new List<CloudDocumentInfo>();

        // 索引里指向已不存在文档的条目直接丢弃。
        foreach (var item in index.Items)
        {
            if (docKeys.Contains(DocKey(item.Id))) result.Add(item);
        }

        var byKey = docKeys.Count == 0
            ? new Dictionary<string, CloudKvEntry>(StringComparer.Ordinal)
            : keys.Where(entry => docKeys.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry, StringComparer.Ordinal);

        // 索引丢失 / 损坏时，按文档键重建摘要；若 list 已带回 value 直接解析，否则单键回读。
        foreach (var key in docKeys)
        {
            var id = key[_ns.KeyPrefix.Length..];
            if (byId.Contains(id)) continue;
            var cached = byKey.TryGetValue(key, out var listEntry) ? listEntry.Value : null;
            var recovered = cached is not null
                ? TryDeriveFromEntry(id, cached.Value)
                : await RecoverInfoAsync(id, key, ct).ConfigureAwait(false);
            if (recovered is not null) result.Add(recovered);
        }

        return result.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<CloudDocumentPayload?> GetAsync(string id, CancellationToken ct)
    {
        var entry = await _kv.GetAsync(DocKey(id), ct).ConfigureAwait(false);
        if (entry?.Value is null) return null;

        var json = DecodeValue(entry.Value.Value);
        var index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var info = index.Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
                   ?? DeriveInfo(id, json);

        return new CloudDocumentPayload(info, json);
    }

    /// <summary>已知列表摘要时只读文档键本身，避免列表批量读取时 N 次重复读索引。</summary>
    internal async Task<CloudDocumentPayload?> GetWithKnownInfoAsync(CloudDocumentInfo knownInfo, CancellationToken ct)
    {
        var entry = await _kv.GetAsync(DocKey(knownInfo.Id), ct).ConfigureAwait(false);
        if (entry?.Value is null) return null;

        var json = DecodeValue(entry.Value.Value);
        return new CloudDocumentPayload(knownInfo, json);
    }

    public async Task<CloudDocumentInfo> PutAsync(
        string? id, string name, string kind, string json, int expectedRevision, CancellationToken ct)
    {
        // 1) 纯预检：键长与容量都在任何 HTTP 之前判定。
        var hasExplicitId = !string.IsNullOrWhiteSpace(id);
        var documentId = hasExplicitId ? id! : Guid.NewGuid().ToString("N");
        var docKey = DocKey(documentId);
        EnsureKeyLength(docKey);
        EnsureKeyLength(_ns.IndexKey);
        var rawBytes = Encoding.UTF8.GetByteCount(json);
        var value = EncodeValue(json, rawBytes);

        // 2) 读索引：既做软上限计数，也做 revision 比对；索引缺项但传入显式 id 时先列键确认再回读 KV，
        // 避免对尚未创建的新随机 id 直接单键 GET 触发未注册路径错误。
        var index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var existing = index.Items.FirstOrDefault(item => string.Equals(item.Id, documentId, StringComparison.Ordinal));
        if (existing is null && (hasExplicitId || expectedRevision > 0))
        {
            var listed = await _kv.ListAsync(ct).ConfigureAwait(false);
            var listMatch = listed.Entries.FirstOrDefault(e => string.Equals(e.Key, docKey, StringComparison.Ordinal));
            if (listMatch is not null)
            {
                if (listMatch.Value is not null)
                {
                    existing = TryDeriveFromEntry(documentId, listMatch.Value.Value);
                }
                else
                {
                    var kvEntry = await _kv.GetAsync(docKey, ct).ConfigureAwait(false);
                    if (kvEntry?.Value is not null)
                        existing = TryDeriveFromEntry(documentId, kvEntry.Value.Value);
                }
                existing ??= new CloudDocumentInfo(
                    documentId, name, string.IsNullOrWhiteSpace(kind) ? _ns.Kind : kind, _time.GetUtcNow(), 0, 1);
            }
        }

        if (expectedRevision == 0)
        {
            if (existing is not null) throw new CloudConflictException(existing);
            if (index.Items.Count >= SoftDocumentLimit)
                throw new CloudAuthException(CloudErrorText.KeyLimitReached);
        }
        else
        {
            if (existing is null) throw new CloudAuthException(CloudErrorText.NotFound);
            if (existing.Revision != expectedRevision) throw new CloudConflictException(existing);
        }

        var info = new CloudDocumentInfo(
            documentId,
            name,
            string.IsNullOrWhiteSpace(kind) ? _ns.Kind : kind,
            _time.GetUtcNow(),
            rawBytes,
            expectedRevision == 0 ? 1 : expectedRevision + 1);

        var items = index.Items
            .Where(item => !string.Equals(item.Id, documentId, StringComparison.Ordinal))
            .Append(info)
            .ToArray();
        // 先预编码并校验索引体积（<= 60 KB 兜底），避免文档写入成功后索引因超限失败留下孤儿。
        var encodedIndex = EncodeIndex(items);

        // 3) 先写文档再写索引：文档写失败不会插入一条指向空文档的索引项。
        await _kv.PutAsync(docKey, value, ct).ConfigureAwait(false);
        await _kv.PutAsync(_ns.IndexKey, encodedIndex, ct).ConfigureAwait(false);
        return info;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var docDeleted = false;
        var indexWritten = false;
        Exception? failure = null;

        try
        {
            await _kv.DeleteAsync(DocKey(id), ct).ConfigureAwait(false);
            docDeleted = true;
        }
        catch (CloudAuthException ex) when (string.Equals(ex.ErrorCode, "not_found", StringComparison.Ordinal))
        {
            // 文档已经不在了，按删除成功处理，索引仍要清掉。
            docDeleted = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // 仅当文档确已删除（或本就缺失）时才更新索引，避免文档仍在 KV 却从索引抹除。
        if (docDeleted)
        {
            try
            {
                var remaining = index.Items
                    .Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal))
                    .ToArray();
                await WriteIndexAsync(remaining, ct).ConfigureAwait(false);
                indexWritten = true;
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        if (!docDeleted || !indexWritten)
        {
            var succeeded = (docDeleted ? 1 : 0) + (indexWritten ? 1 : 0);
            var failed = docDeleted ? 1 : 2 - succeeded;
            throw new CloudAuthException(
                $"云端删除未完成：成功 {succeeded} 项，失败 {failed} 项（文档与索引）。请重试。",
                "partial_delete",
                failure);
        }
    }

    public async Task DeleteAllAsync(CancellationToken ct)
    {
        var items = await ListAsync(ct).ConfigureAwait(false);
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        var succeeded = 0;
        var failed = 0;

        foreach (var item in items)
        {
            try
            {
                await _kv.DeleteAsync(DocKey(item.Id), ct).ConfigureAwait(false);
                deletedIds.Add(item.Id);
                succeeded++;
            }
            catch (CloudAuthException ex) when (string.Equals(ex.ErrorCode, "not_found", StringComparison.Ordinal))
            {
                deletedIds.Add(item.Id);
                succeeded++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        try
        {
            var remaining = items.Where(item => !deletedIds.Contains(item.Id)).ToArray();
            await WriteIndexAsync(remaining, ct).ConfigureAwait(false);
            succeeded++;
        }
        catch (Exception)
        {
            failed++;
        }

        if (failed > 0)
            throw new CloudAuthException(
                $"云端清理未完成：成功 {succeeded} 项，失败 {failed} 项。请重试。",
                "partial_delete_all");
    }

    private string DocKey(string id) => _ns.KeyPrefix + id;

    private static void EnsureKeyLength(string key)
    {
        if (key.Length > 255) throw new CloudAuthException(CloudErrorText.KeyTooLong, "key_too_long");
    }

    private static JsonElement EncodeIndex(IReadOnlyList<CloudDocumentInfo> items)
    {
        var payload = new
        {
            V = 1,
            Items = items.Select(item => new
            {
                item.Id,
                item.Name,
                item.Kind,
                UpdatedAtUtc = item.UpdatedAtUtc.ToString("O"),
                item.Bytes,
                item.Revision,
            }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        var rawBytes = Encoding.UTF8.GetByteCount(json);
        if (rawBytes <= CompressThresholdBytes)
            return ParseObject(json);

        // 索引汇总最多 200 篇文档，长名称下原始 JSON 可能超过 64 KB 但压缩后仅几 KB，只拦截压缩后的信封上限。
        return CompressToEnvelope(json);
    }

    /// <summary>原始 JSON 超过 40 KB 才压缩；压缩后 envelope 超过 60 KB 但原文仍在硬上限内时按原文直传。</summary>
    private static JsonElement EncodeValue(string json, int rawBytes)
    {
        if (rawBytes > MaxValueBytes)
            throw new CloudAuthException(CloudErrorText.PayloadTooLarge, "payload_too_large");

        if (rawBytes <= CompressThresholdBytes)
            return ParseObject(json);

        var envelope = BuildGzipEnvelope(json);
        return Encoding.UTF8.GetByteCount(envelope) <= MaxStoredBytes
            ? ParseObject(envelope)
            : ParseObject(json);
    }

    private static JsonElement CompressToEnvelope(string json)
    {
        var envelope = BuildGzipEnvelope(json);
        if (Encoding.UTF8.GetByteCount(envelope) > MaxStoredBytes)
            throw new CloudAuthException(CloudErrorText.PayloadTooLarge, "payload_too_large");

        return ParseObject(envelope);
    }

    private static string BuildGzipEnvelope(string json)
    {
        var compressed = CompressGzip(Encoding.UTF8.GetBytes(json));
        return JsonSerializer.Serialize(new
        {
            V = 1,
            Encoded = GzipEncoding,
            Body = Convert.ToBase64String(compressed),
        });
    }

    private static byte[] CompressGzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private static string DecodeValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return value.GetRawText();

        if (TryGetCI(value, "Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.String
            && string.Equals(encoded.GetString(), GzipEncoding, StringComparison.Ordinal)
            && TryGetCI(value, "Body", out var body) && body.ValueKind == JsonValueKind.String)
        {
            try
            {
                var bytes = Convert.FromBase64String(body.GetString() ?? "");
                using var source = new MemoryStream(bytes);
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch (Exception ex)
            {
                throw new CloudAuthException(CloudErrorText.ServerError, "invalid_compression", ex);
            }
        }

        return value.GetRawText();
    }

    private static JsonElement ParseObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("文档内容不是合法的 JSON 对象，无法保存。");
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("文档内容不是合法的 JSON 对象，无法保存。", ex);
        }
    }

    private async Task<IndexData> ReadIndexAsync(CancellationToken ct)
    {
        CloudKvEntry? entry;
        try
        {
            entry = await _kv.GetAsync(_ns.IndexKey, ct).ConfigureAwait(false);
        }
        catch (CloudAuthException ex) when (string.Equals(ex.ErrorCode, "not_found", StringComparison.Ordinal)
                                            || string.Equals(ex.ErrorCode, "invalid_json", StringComparison.Ordinal))
        {
            return new IndexData(Array.Empty<CloudDocumentInfo>());
        }

        if (entry?.Value is null) return new IndexData(Array.Empty<CloudDocumentInfo>());

        try
        {
            var root = entry.Value.Value;
            if (root.ValueKind == JsonValueKind.Object
                && TryGetCI(root, "Encoded", out var encoded)
                && encoded.ValueKind == JsonValueKind.String)
            {
                using var decoded = JsonDocument.Parse(DecodeValue(root));
                return ParseIndexRoot(decoded.RootElement);
            }

            return ParseIndexRoot(root);
        }
        catch (Exception)
        {
            // 索引损坏即当空，ListAsync 会按文档键重建。
            return new IndexData(Array.Empty<CloudDocumentInfo>());
        }
    }

    private static IndexData ParseIndexRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetCI(root, "Items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return new IndexData(Array.Empty<CloudDocumentInfo>());

        var list = new List<CloudDocumentInfo>();
        foreach (var item in items.EnumerateArray())
        {
            var info = ParseInfo(item);
            if (info is not null) list.Add(info);
        }
        return new IndexData(list.ToArray());
    }

    private static CloudDocumentInfo? ParseInfo(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetCI(item, "Id", out var idNode) || idNode.ValueKind != JsonValueKind.String) return null;
        var id = idNode.GetString();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var name = ReadString(item, "Name") ?? id!;
        var kind = ReadString(item, "Kind") ?? "";
        var bytes = ReadInt(item, "Bytes") ?? 0;
        var revision = ReadInt(item, "Revision") ?? 1;
        var updated = DateTimeOffset.MinValue;
        if (TryGetCI(item, "UpdatedAtUtc", out var timeNode) && timeNode.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timeNode.GetString(), out var parsed))
            updated = parsed;

        return new CloudDocumentInfo(id!, name, kind, updated, bytes, revision);
    }

    private Task WriteIndexAsync(IReadOnlyList<CloudDocumentInfo> items, CancellationToken ct)
        => _kv.PutAsync(_ns.IndexKey, EncodeIndex(items), ct);

    private async Task<CloudDocumentInfo?> RecoverInfoAsync(string id, string key, CancellationToken ct)
    {
        CloudKvEntry? entry;
        try
        {
            entry = await _kv.GetAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        if (entry?.Value is null) return null;

        return TryDeriveFromEntry(id, entry.Value.Value);
    }

    private CloudDocumentInfo? TryDeriveFromEntry(string id, JsonElement rawValue)
    {
        string json;
        try
        {
            json = DecodeValue(rawValue);
        }
        catch (Exception)
        {
            return null;
        }
        return DeriveInfo(id, json);
    }

    /// <summary>索引丢失时的保守摘要：从文档自身的 name / kind / version / 时间读取，缺什么用什么兜底。</summary>
    private CloudDocumentInfo DeriveInfo(string id, string json)
    {
        var name = id;
        var kind = _ns.Kind;
        var updated = _time.GetUtcNow();
        var revision = 1;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                name = ReadString(root, "name") ?? ReadString(root, "Name") ?? id;
                kind = ReadString(root, "kind") ?? ReadString(root, "Kind") ?? _ns.Kind;
                if (TryGetCI(root, "updated_at_utc", out var timeNode) && timeNode.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(timeNode.GetString(), out var parsed))
                    updated = parsed;
                // 座位表文档把修订号序列化为 "v"，旧文档用 "version"；先读新键再回退旧键。
                var parsedRevision = ReadInt(root, "v") ?? ReadInt(root, "version");
                if (parsedRevision is { } value && value >= 1)
                    revision = value;
            }
        }
        catch (JsonException)
        {
            // 文档内容损坏也保留条目，摘要用保守默认值。
        }

        return new CloudDocumentInfo(id, name, kind, updated, Encoding.UTF8.GetByteCount(json), revision);
    }

    private static bool TryGetCI(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
        => TryGetCI(obj, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement obj, string name)
        => TryGetCI(obj, name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private sealed record IndexData(IReadOnlyList<CloudDocumentInfo> Items);
}
