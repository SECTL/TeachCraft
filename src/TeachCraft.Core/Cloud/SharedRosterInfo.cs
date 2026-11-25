namespace TeachCraft.Core.Cloud;

/// <summary>共享名单在索引里的一条摘要，不含学生明细。</summary>
public sealed record SharedRosterInfo(
    string RosterId,
    string Name,
    int Version,
    DateTimeOffset UpdatedAtUtc,
    int StudentCount,
    int Bytes,
    RosterFieldPresence Fields,
    bool LoadFailed = false,
    string? LoadError = null);
