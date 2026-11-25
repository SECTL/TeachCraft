namespace TeachCraft.Core.Cloud;

/// <summary>
/// 共享名单的云端读写入口。只暴露名单语义，不把底层文档存储与 HTTP 细节泄漏给调用方。
/// </summary>
public interface ISharedRosterSource
{
    Task<IReadOnlyList<SharedRosterInfo>> ListAsync(CancellationToken ct);

    /// <summary>读取一份共享名单；不存在返回 null。</summary>
    Task<SharedRosterDocument?> GetAsync(string rosterId, CancellationToken ct);

    /// <summary>
    /// 发布或更新一份共享名单。<paramref name="selection"/> 决定哪些可选字段允许进云端；
    /// <paramref name="existingId"/> 为 null 表示新建，否则必须与 <paramref name="expectedVersion"/> 一起比对云端版本。
    /// </summary>
    Task<SharedRosterInfo> SaveAsync(
        SharedRosterDocument roster,
        RosterFieldSelection selection,
        string? existingId,
        int expectedVersion,
        CancellationToken ct);

    Task DeleteAsync(string rosterId, CancellationToken ct);
}
