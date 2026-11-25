namespace TeachCraft.Core.Cloud;

/// <summary>
/// 一份云端文档集合的键空间。文档键 = <see cref="KeyPrefix"/> + 32 位 hex id，
/// 索引单独一份。两个命名空间互不重叠，归位与名单不会互相覆盖。
/// </summary>
public sealed record CloudDocumentNamespace(string KeyPrefix, string IndexKey, string Kind)
{
    /// <summary>归位日常座位表。文档类型沿用已冻结的 <c>seatgrid.daily</c>。</summary>
    public static CloudDocumentNamespace SeatGrid { get; } =
        new("teachcraft.seatgrid.v1.", "teachcraft.seatgrid.v1.index", "seatgrid.daily");

    /// <summary>共享名单。文档类型沿用已冻结的 <c>teachcraft.roster.v1</c>。</summary>
    public static CloudDocumentNamespace Roster { get; } =
        new("teachcraft.roster.v1.", "teachcraft.roster.v1.index", "teachcraft.roster.v1");
}
