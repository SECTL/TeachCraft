using System.Collections.Generic;

namespace SeatGrid.Core.Exam;

/// <summary>一张桌贴的内容：贴在某张桌子左上角，让考生对号入座。</summary>
public sealed record DeskSticker(
    string Name,
    string Number,
    int SeatNumber,
    string ClassName,
    string ExamRoom);

/// <summary>一份桌贴文档：一页 A4 排若干张，按顺序装订。</summary>
public sealed record DeskStickerDocument(
    string Title,
    IReadOnlyList<DeskSticker> Stickers,
    string Footnote);

/// <summary>桌贴的排版选项。</summary>
public sealed record DeskStickerOptions
{
    /// <summary>每页列数。</summary>
    public int Columns { get; init; } = 3;

    /// <summary>每页行数。</summary>
    public int Rows { get; init; } = 4;

    /// <summary>班级名，印在桌贴底部。</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>考场名，印在桌贴底部。</summary>
    public string ExamRoom { get; init; } = string.Empty;

    /// <summary>是否印考号（学号）。</summary>
    public bool ShowNumber { get; init; } = true;

    public int PerPage => Columns * Rows;
}
