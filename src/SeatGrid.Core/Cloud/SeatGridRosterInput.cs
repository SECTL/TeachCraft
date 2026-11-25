using System.Text.Json.Serialization;

namespace SeatGrid.Core.Cloud;

/// <summary>
/// 共享名单里的一名学生（归位侧的**中立输入**，不依赖任何云端 / HTTP 类型）。
/// <para>
/// Index 是名单里的 1 基序号，与姓名一起构成约束的持久键——<c>Student.Id</c> 每次解析都会重新生成，
/// 绝不能当键。姓名 / 性别 / 学号是共享名单的必填字段；身高 / 视力 / 成绩 / 备注 / 原小组是可选字段，
/// 未选择时为 null（隐私：未选字段不进云端文档）。
/// </para>
/// </summary>
public sealed record SeatGridRosterStudent(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("gender")] string Gender,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("height")] double? Height,
    [property: JsonPropertyName("vision")] double? Vision,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("original_group")] string? OriginalGroup);

/// <summary>共享名单里哪些可选字段对归位可见。默认全关（保守，最小披露）。</summary>
public sealed record SeatGridRosterFields(
    bool Height, bool Vision, bool Score, bool Notes, bool OriginalGroup)
{
    public static SeatGridRosterFields None { get; } = new(false, false, false, false, false);
}

/// <summary>
/// 宿主桥接层交给归位的名单快照 / 当前名单。FieldsPresent 用规范字段名：
/// height / vision / score / notes / originalGroup。
/// </summary>
public sealed record SeatGridRosterInput(
    int Version,
    string Name,
    IReadOnlyCollection<string> FieldsPresent,
    IReadOnlyList<SeatGridRosterStudent> Students);
