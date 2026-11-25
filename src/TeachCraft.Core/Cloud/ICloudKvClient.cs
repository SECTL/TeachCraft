using System.Text.Json;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// SECTL 云 KV 的最小接口。只暴露文档存储需要的四个动作，
/// 具体 HTTP 形状由 <see cref="SectlCloudKvClient"/> 承担，测试可换成内存实现。
/// </summary>
public interface ICloudKvClient
{
    /// <summary>读取一条；键不存在返回 null（404 不是异常）。</summary>
    Task<CloudKvEntry?> GetAsync(string key, CancellationToken ct);

    /// <summary>
    /// 列出当前用户的全部键。实现负责跟完所有分页，调用方拿到的是完整结果；
    /// 返回形状未确认，实现须兼容 keys、kv_list 与 keys+values。
    /// </summary>
    Task<CloudKvList> ListAsync(CancellationToken ct);

    /// <summary>写入一条。value 必须是 JSON 对象。</summary>
    Task PutAsync(string key, JsonElement value, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>一条 KV。List 只给键时 <see cref="Value"/> 为 null，调用方需再 Get。</summary>
public sealed record CloudKvEntry(string Key, JsonElement? Value);

/// <summary>
/// List 的结果。<see cref="Entries"/> 已含所有分页；正常返回时 <see cref="HasMore"/> 为 false
/// （已读完），<see cref="Total"/> 是服务端报告的总数，供调用方核对是否漏读。
/// </summary>
public sealed record CloudKvList(IReadOnlyList<CloudKvEntry> Entries, bool HasMore, int Total);
