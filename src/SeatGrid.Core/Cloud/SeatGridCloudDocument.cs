using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SeatGrid.Core.Models;
using SeatGrid.Core.Seating;

namespace SeatGrid.Core.Cloud;

/// <summary>
/// 归位日常座位表在云端的文档形状。只引用 SeatGrid.Core 自己的类型与中立输入，
/// 不依赖任何网络或鉴权类型——宿主桥接层负责在共享名单文档与
/// <see cref="SeatGridRosterInput"/> 之间转换。
/// <para>
/// 文档通过 RosterId + RosterVersion 引用共享名单，并内嵌一份投影后的名单快照作为兜底；
/// 约束按「序号 + 姓名」持久化，绝不写 <c>Student.Id</c>。
/// </para>
/// </summary>
public sealed class SeatGridCloudDocument
{
    [JsonPropertyName("v")]
    public int V { get; set; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "seatgrid.daily";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("roster_id")]
    public string RosterId { get; set; } = string.Empty;

    [JsonPropertyName("roster_version")]
    public int RosterVersion { get; set; }

    [JsonPropertyName("roster_snapshot")]
    public SharedRosterSnapshot? RosterSnapshot { get; set; }

    [JsonPropertyName("settings")]
    public SeatGridSettingsDto Settings { get; set; } = new();

    [JsonPropertyName("assignments")]
    public List<SeatGridAssignmentDto> Assignments { get; set; } = new();

    [JsonPropertyName("constraints")]
    public SeatGridConstraintsDto Constraints { get; set; } = new();

    /// <summary>画布 JSON，由 SeatChartCanvasCodec 编码；空画布是空串。</summary>
    [JsonPropertyName("canvas_json")]
    public string CanvasJson { get; set; } = string.Empty;

    [JsonPropertyName("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;
}

/// <summary>内嵌的共享名单投影快照（兜底用）。只含调用方同意共享的字段。</summary>
public sealed class SharedRosterSnapshot
{
    [JsonPropertyName("v")]
    public int V { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("fields_present")]
    public string[] FieldsPresent { get; set; } = Array.Empty<string>();

    [JsonPropertyName("students")]
    public List<SeatGridRosterStudent> Students { get; set; } = new();
}

/// <summary>
/// 座位表设置。覆盖 SeatChartSettings 里与导出有关的全部标量字段，含导师制开关与组长座；
/// **刻意不含 LayoutOverride**——那是考场快照专用，考场不能上云。
/// 画布单独存在文档的 CanvasJson 里，不在这里重复。
/// </summary>
public sealed class SeatGridSettingsDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "班级座位表";

    [JsonPropertyName("bold_title")]
    public bool BoldTitle { get; set; } = true;

    [JsonPropertyName("head_teacher")]
    public string HeadTeacher { get; set; } = string.Empty;

    [JsonPropertyName("monitor")]
    public string Monitor { get; set; } = string.Empty;

    [JsonPropertyName("view")]
    public ViewMode View { get; set; } = ViewMode.Teacher;

    [JsonPropertyName("groups")]
    public int Groups { get; set; } = 4;

    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 6;

    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 2;

    [JsonPropertyName("cell_scale_percent")]
    public int CellScalePercent { get; set; } = 100;

    [JsonPropertyName("podium_gap")]
    public int PodiumGap { get; set; } = 16;

    [JsonPropertyName("name_font_size")]
    public int NameFontSize { get; set; } = 14;

    [JsonPropertyName("row_gap_percent")]
    public int RowGapPercent { get; set; } = 7;

    [JsonPropertyName("column_gap_percent")]
    public int ColumnGapPercent { get; set; } = 7;

    [JsonPropertyName("group_gap_percent")]
    public int GroupGapPercent { get; set; } = 28;

    [JsonPropertyName("tier_row_growth_percent")]
    public int TierRowGrowthPercent { get; set; }

    [JsonPropertyName("show_doors")]
    public bool ShowDoors { get; set; } = true;

    [JsonPropertyName("podium_label")]
    public string PodiumLabel { get; set; } = "讲　台";

    [JsonPropertyName("podium_width_percent")]
    public int PodiumWidthPercent { get; set; } = 55;

    [JsonPropertyName("podium_height")]
    public int PodiumHeight { get; set; } = 40;

    [JsonPropertyName("bold_names")]
    public bool BoldNames { get; set; }

    [JsonPropertyName("hide_empty_seats")]
    public bool HideEmptySeats { get; set; }

    [JsonPropertyName("mark_females")]
    public bool MarkFemales { get; set; } = true;

    [JsonPropertyName("space_two_char_names")]
    public bool SpaceTwoCharNames { get; set; } = true;

    [JsonPropertyName("show_group_row_hints")]
    public bool ShowGroupRowHints { get; set; } = true;

    [JsonPropertyName("seat_background")]
    public string SeatBackground { get; set; } = "#FFFFFF";

    [JsonPropertyName("empty_seat_background")]
    public string EmptySeatBackground { get; set; } = "#FAF9F5";

    [JsonPropertyName("seat_text")]
    public string SeatText { get; set; } = "#141413";

    [JsonPropertyName("seat_border")]
    public string SeatBorder { get; set; } = "#E7E3D8";

    [JsonPropertyName("podium_background")]
    public string PodiumBackground { get; set; } = "#F4F2EC";

    [JsonPropertyName("podium_text")]
    public string PodiumText { get; set; } = "#85806F";

    [JsonPropertyName("group_label")]
    public string GroupLabel { get; set; } = "#C6613F";

    [JsonPropertyName("row_label")]
    public string RowLabel { get; set; } = "#85806F";

    [JsonPropertyName("female_mark")]
    public string FemaleMark { get; set; } = "#C6613F";

    [JsonPropertyName("arrange_mode")]
    public ArrangeMode ArrangeMode { get; set; } = ArrangeMode.RosterOrder;

    [JsonPropertyName("within_group")]
    public WithinGroupOrder WithinGroup { get; set; } = WithinGroupOrder.Fold;

    [JsonPropertyName("mentor_enabled")]
    public bool MentorEnabled { get; set; }

    [JsonPropertyName("mentor_seat")]
    public MentorSeat MentorSeat { get; set; } = MentorSeat.Center;
}

/// <summary>一条座位分配。Index 是名单 1 基序号；Group / Row / Column 是 0 基物理座位（与 SeatPosition 一致）。</summary>
public sealed class SeatGridAssignmentDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("group")]
    public int Group { get; set; }

    [JsonPropertyName("row")]
    public int Row { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>锁定：序号 + 姓名 + 1 基单格坐标。</summary>
public sealed class SeatGridPinEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public int Group { get; set; }

    [JsonPropertyName("row")]
    public int Row { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>成对约束（互斥 / 必须同桌 / 上期同桌）：两端各用序号 + 姓名。</summary>
public sealed class SeatGridPairEntry
{
    [JsonPropertyName("index_a")]
    public int IndexA { get; set; }

    [JsonPropertyName("name_a")]
    public string NameA { get; set; } = string.Empty;

    [JsonPropertyName("index_b")]
    public int IndexB { get; set; }

    [JsonPropertyName("name_b")]
    public string NameB { get; set; } = string.Empty;
}

/// <summary>区域约束（允许 / 禁止）：序号 + 姓名 + 1 基闭区间矩形。</summary>
public sealed class SeatGridZoneEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("group_from")]
    public int GroupFrom { get; set; }

    [JsonPropertyName("group_to")]
    public int GroupTo { get; set; }

    [JsonPropertyName("row_from")]
    public int RowFrom { get; set; }

    [JsonPropertyName("row_to")]
    public int RowTo { get; set; }

    [JsonPropertyName("column_from")]
    public int ColumnFrom { get; set; }

    [JsonPropertyName("column_to")]
    public int ColumnTo { get; set; }
}

/// <summary>
/// 排座约束。与「座位约束」Excel 表同构：按类型分列，每条用序号 + 姓名定位，矩形 1 基闭区间。
/// </summary>
public sealed class SeatGridConstraintsDto
{
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("mentor_enabled")]
    public bool MentorEnabled { get; set; }

    [JsonPropertyName("mentor_seat")]
    public MentorSeat MentorSeat { get; set; } = MentorSeat.Center;

    [JsonPropertyName("pins")]
    public List<SeatGridPinEntry> Pins { get; set; } = new();

    [JsonPropertyName("mutex")]
    public List<SeatGridPairEntry> Mutex { get; set; } = new();

    [JsonPropertyName("deskmates")]
    public List<SeatGridPairEntry> Deskmates { get; set; } = new();

    [JsonPropertyName("avoid_deskmates")]
    public List<SeatGridPairEntry> AvoidDeskmates { get; set; } = new();

    [JsonPropertyName("zone_allow")]
    public List<SeatGridZoneEntry> ZoneAllow { get; set; } = new();

    [JsonPropertyName("zone_deny")]
    public List<SeatGridZoneEntry> ZoneDeny { get; set; } = new();
}
