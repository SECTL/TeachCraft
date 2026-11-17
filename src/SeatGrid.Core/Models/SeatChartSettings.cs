using System;
using System.Globalization;

namespace SeatGrid.Core.Models;

public enum ViewMode
{
    Teacher,
    Student,
}

public enum ArrangeMode
{
    RosterOrder,
    GenderAlternate,
    GenderPair,
    Random,
    Height,
    Vision,

    // 只能追加在末尾：界面用 (ArrangeMode)ArrangeModeIndex 强转。
    ScoreSnake,
    ScoreLayers,
}

/// <summary>座位表的所有可调参数。颜色统一以 #RRGGBB 保存，便于 Excel / PDF 共用。</summary>
public sealed class SeatChartSettings
{
    public const int BaseCellSize = 80;

    public string Title { get; set; } = "班级座位表";
    public bool BoldTitle { get; set; } = true;
    public string HeadTeacher { get; set; } = string.Empty;
    public string Monitor { get; set; } = string.Empty;

    public ViewMode View { get; set; } = ViewMode.Teacher;

    public int Groups { get; set; } = 4;
    public int Rows { get; set; } = 6;
    public int Columns { get; set; } = 2;

    /// <summary>格子缩放百分比，50–200，基准 80px。</summary>
    public int CellScalePercent { get; set; } = 100;

    public int PodiumGap { get; set; } = 16;
    public int NameFontSize { get; set; } = 14;

    /// <summary>
    /// 教室画布（过道 / 走廊 / 门窗）。不可变对象，所以 Clone() 的浅拷贝共享引用是安全的；
    /// 但任何变换都必须返回新实例，不得就地修改。
    /// </summary>
    public SeatChartCanvas Canvas { get; set; } = SeatChartCanvas.Empty;

    /// <summary>排距，基准格子的百分比。</summary>
    public int RowGapPercent { get; set; } = 7;

    /// <summary>组内列距。</summary>
    public int ColumnGapPercent { get; set; } = 7;

    /// <summary>组间间距。</summary>
    public int GroupGapPercent { get; set; } = 28;

    /// <summary>阶梯教室：每往后退一排，排距额外增加的百分比。0 = 平层。</summary>
    public int TierRowGrowthPercent { get; set; }

    /// <summary>是否画门窗。</summary>
    public bool ShowDoors { get; set; } = true;

    /// <summary>讲台文字，有些教室写「讲台 / 黑板」。</summary>
    public string PodiumLabel { get; set; } = "讲　台";

    /// <summary>讲台宽度占内容宽度的百分比。</summary>
    public int PodiumWidthPercent { get; set; } = 55;

    /// <summary>讲台高度（像素）。</summary>
    public int PodiumHeight { get; set; } = 40;

    public bool BoldNames { get; set; }
    public bool HideEmptySeats { get; set; }
    public bool MarkFemales { get; set; } = true;
    public bool SpaceTwoCharNames { get; set; } = true;
    public bool ShowGroupRowHints { get; set; } = true;

    public string SeatBackground { get; set; } = "#FFFFFF";
    public string EmptySeatBackground { get; set; } = "#FAF9F5";
    public string SeatText { get; set; } = "#141413";
    public string SeatBorder { get; set; } = "#E7E3D8";
    public string PodiumBackground { get; set; } = "#F4F2EC";
    public string PodiumText { get; set; } = "#85806F";
    public string GroupLabel { get; set; } = "#C6613F";
    public string RowLabel { get; set; } = "#85806F";
    public string FemaleMark { get; set; } = "#C6613F";

    public int CellSizePx =>
        Math.Max(40, (int)Math.Round(BaseCellSize * (CellScalePercent / 100.0)));

    /// <summary>上次使用的排座方式。此前不持久化，导出配置后重开总回到「名单顺序」。</summary>
    public ArrangeMode ArrangeMode { get; set; } = ArrangeMode.RosterOrder;

    /// <summary>成绩模式的组内顺序。</summary>
    public Seating.WithinGroupOrder WithinGroup { get; set; } = Seating.WithinGroupOrder.Fold;

    /// <summary>小组导师制：组内成绩第一任组长，组员环绕。</summary>
    public bool MentorEnabled { get; set; }

    /// <summary>组长坐哪：组中心 / 第一排。</summary>
    public Seating.MentorSeat MentorSeat { get; set; } = Seating.MentorSeat.Center;

    /// <summary>
    /// 显式布局。<see cref="GetLayout"/> 直接返回它，不再读 <see cref="Groups"/> /
    /// <see cref="Rows"/> / <see cref="Columns"/>，因此那三个字段的夹取对它无效。
    ///
    /// 只为考场模式存在：考场布局是「单组、8 列上限」的单人桌快照，而日常滑块最多 4 列。
    /// 早先导出走的是 ToSettings() → Compose → Normalize，布局取自日常滑块并被夹到 4 列，
    /// 30 人考场 6 列 × 5 排被切成 4 列 × N 排，后排的人直接消失。
    ///
    /// **除了画布那一行，<see cref="Normalize"/> 一个字都没改**——三维字段的夹取是给日常滑块用的
    /// 不变量，改它就会动到日常导出的每个字节。画布那行加守卫是因为它读 <see cref="GetLayout"/>，
    /// 有了显式布局之后会拿考场尺寸去截日常画布，把调用方手里的过道 / 门窗截掉。
    /// 日常路径（不设这个字段）因此完全走旧逻辑。
    /// </summary>
    public SeatChartLayout? LayoutOverride { get; set; }

    /// <summary>当前布局：有显式布局就是它，否则由三个日常维度字段组装。</summary>
    public SeatChartLayout GetLayout() => LayoutOverride ?? new(Groups, Rows, Columns, Canvas);

    public SeatChartSettings Clone() => (SeatChartSettings)MemberwiseClone();

    public void Normalize()
    {
        // 日常滑块的三维字段照旧夹到界面允许范围。考场布局不走这里（见 LayoutOverride）。
        Groups = Math.Clamp(Groups, 1, 8);
        Rows = Math.Clamp(Rows, 1, 10);
        Columns = Math.Clamp(Columns, 1, 4);
        CellScalePercent = Math.Clamp(CellScalePercent, 50, 200);
        PodiumGap = Math.Clamp(PodiumGap, 0, 100);
        NameFontSize = Math.Clamp(NameFontSize, 10, 36);
        RowGapPercent = Math.Clamp(RowGapPercent, 0, 100);
        ColumnGapPercent = Math.Clamp(ColumnGapPercent, 0, 100);
        GroupGapPercent = Math.Clamp(GroupGapPercent, 0, 100);
        TierRowGrowthPercent = Math.Clamp(TierRowGrowthPercent, 0, 50);
        PodiumWidthPercent = Math.Clamp(PodiumWidthPercent, 20, 100);
        PodiumHeight = Math.Clamp(PodiumHeight, 24, 80);
        if (string.IsNullOrWhiteSpace(PodiumLabel)) PodiumLabel = "讲　台";

        // 显式布局下不裁日常画布：Compose 读的是 layout.Surface（考场布局自带空画布），
        // 拿日常过道去按考场尺寸截一遍既没意义，又会让调用方手里的日常画布凭空少东西
        // ——ToSettings() 交出去的是同一个 Canvas 引用，截掉就回不去了。
        if (LayoutOverride is null) Canvas = Canvas.Normalize(GetLayout());

        Title = string.IsNullOrWhiteSpace(Title) ? "座位表" : Title.Trim();

        SeatBackground = NormalizeColor(SeatBackground, "#FFFFFF");
        EmptySeatBackground = NormalizeColor(EmptySeatBackground, "#FAF9F5");
        SeatText = NormalizeColor(SeatText, "#141413");
        SeatBorder = NormalizeColor(SeatBorder, "#E7E3D8");
        PodiumBackground = NormalizeColor(PodiumBackground, "#F4F2EC");
        PodiumText = NormalizeColor(PodiumText, "#85806F");
        GroupLabel = NormalizeColor(GroupLabel, "#C6613F");
        RowLabel = NormalizeColor(RowLabel, "#85806F");
        FemaleMark = NormalizeColor(FemaleMark, "#C6613F");

        if (!Enum.IsDefined(ArrangeMode)) ArrangeMode = ArrangeMode.RosterOrder;
        if (!Enum.IsDefined(WithinGroup)) WithinGroup = Seating.WithinGroupOrder.Fold;
    }

    public static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;

        // Avalonia 的 Color.ToString() 会输出 #AARRGGBB，这里统一压成 #RRGGBB
        if (text.Length == 9) text = "#" + text[3..];

        if (text.Length != 7) return fallback;
        return int.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? text.ToUpperInvariant()
            : fallback;
    }
}
