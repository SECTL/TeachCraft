namespace TeachCraft.Core.Cloud;

/// <summary>
/// 按命名空间隔离的云端文档存储。文档内容原样存取，索引只记摘要；
/// 覆盖前比对 revision，冲突不合并、不写入。
/// </summary>
public interface ICloudDocumentStore
{
    Task<IReadOnlyList<CloudDocumentInfo>> ListAsync(CancellationToken ct);

    /// <summary>读取一份文档；不存在返回 null。</summary>
    Task<CloudDocumentPayload?> GetAsync(string id, CancellationToken ct);

    /// <summary>
    /// 新建或覆盖。<paramref name="expectedRevision"/> 为 0 表示新建（生成新 id），
    /// 否则必须与云端当前 revision 一致，不一致抛 <see cref="CloudConflictException"/>。
    /// </summary>
    Task<CloudDocumentInfo> PutAsync(
        string? id, string name, string kind, string json, int expectedRevision, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>清空本命名空间下的全部文档与索引。</summary>
    Task DeleteAllAsync(CancellationToken ct);
}
