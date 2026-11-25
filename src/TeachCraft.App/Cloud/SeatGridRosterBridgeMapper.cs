using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 共享名单的 Core 文档与归位中立输入之间的纯映射。没有网络、没有状态、没有副作用，
/// 便于单测直接钉住「未勾选字段必须是 null」这条隐私不变量。
/// </summary>
public static class SeatGridRosterBridgeMapper
{
    public static SeatGridRosterInput ToInput(SharedRosterDocument doc) => new(
        doc.Version,
        doc.Name,
        doc.Fields.PresentFieldNames,
        doc.Students.Select(ToStudent).ToList());

    /// <summary>把座位表内嵌的名单快照还原成归位能读的中立输入。</summary>
    public static SeatGridRosterInput ToSnapshot(SharedRosterSnapshot snapshot) => new(
        snapshot.Version,
        snapshot.Name,
        snapshot.FieldsPresent,
        snapshot.Students.ToList());

    /// <summary>
    /// 构造待发布的共享名单文档。Version / RosterId / UpdatedAtUtc 留给
    /// <see cref="ISharedRosterSource.SaveAsync"/> 按云端 revision 填写，这里不猜。
    /// </summary>
    public static SharedRosterDocument ToDocument(
        string name, IReadOnlyList<SeatGridRosterStudent> students, SeatGridRosterFields fields) => new()
    {
        V = 1,
        Kind = CloudDocumentNamespace.Roster.Kind,
        Name = name,
        Fields = ToPresence(fields),
        Students = students.Select(ToShared).ToList(),
    };

    public static SeatGridRosterFields ToFields(RosterFieldPresence presence) => new(
        presence.Height, presence.Vision, presence.Score, presence.Notes, presence.OriginalGroup);

    public static RosterFieldPresence ToPresence(SeatGridRosterFields fields) => new(
        fields.Height, fields.Vision, fields.Score, fields.Notes, fields.OriginalGroup);

    private static SeatGridRosterStudent ToStudent(SharedRosterStudent s) => new(
        s.Index, s.Name, s.Gender, s.Number, s.Height, s.Vision, s.Score, s.Notes, s.OriginalGroup);

    private static SharedRosterStudent ToShared(SeatGridRosterStudent s) => new()
    {
        Index = s.Index,
        Name = s.Name,
        Gender = s.Gender,
        Number = s.Number,
        Height = s.Height,
        Vision = s.Vision,
        Score = s.Score,
        Notes = s.Notes,
        OriginalGroup = s.OriginalGroup,
    };
}
