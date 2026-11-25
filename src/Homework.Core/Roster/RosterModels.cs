using System.Text.RegularExpressions;
using Homework.Core.Models;

namespace Homework.Core.Roster;

/// <summary>工作簿里的一张工作表摘要。</summary>
public sealed record RosterWorksheetInfo(string Name, int RowCount, int ColumnCount)
{
    public string DisplayName => RowCount > 0 ? $"{Name}（{RowCount} 行）" : Name;

    /// <summary>下拉框直接显示可读名称，不能露出记录的原始转储。</summary>
    public override string ToString() => DisplayName;
}

/// <summary>工作簿预览：让界面先列出工作表，再决定读哪一张。</summary>
public sealed record RosterWorkbookPreview(IReadOnlyList<RosterWorksheetInfo> Worksheets);

/// <summary>表头行里的一列。Index 为 0 基列号，Letter 为 Excel 列字母。</summary>
public sealed record RosterColumnChoice(int Index, string Letter, string Header)
{
    public string DisplayName => $"{Letter} · {Header}";

    /// <summary>下拉框直接显示「C · 姓名」，不能露出记录的原始转储。</summary>
    public override string ToString() => DisplayName;
}

/// <summary>一张工作表的表头预览。RowCount 为表头之后的数据行数。</summary>
public sealed record RosterWorksheetPreview(
    string Worksheet,
    int HeaderRow,
    int RowCount,
    IReadOnlyList<RosterColumnChoice> Columns);

/// <summary>
/// 列映射。HeaderRow 沿用「1 基表头行号 = 数据起始的 0 基下标」约定：
/// 0（或 -1）表示没有表头、数据从第 0 行开始；3 表示表头在第 3 行、数据从第 3 行下标开始。
/// 三个列号 -1 表示该列不存在。
/// </summary>
public sealed record RosterColumnMap(int HeaderRow, int NameColumn, int NumberColumn, int ClassColumn);

/// <summary>名单读取结果。班级按首次出现顺序，学生与班级都带新 Guid。</summary>
public sealed record RosterImportResult(
    IReadOnlyList<HomeworkClass> Classes,
    IReadOnlyList<HomeworkStudent> Students,
    IReadOnlyList<string> Warnings,
    int SkippedRows);

/// <summary>粘贴解析结果。Errors 带 1 基行号，出错的行已跳过。</summary>
public sealed record RosterPasteResult(
    IReadOnlyList<HomeworkClass> Classes,
    IReadOnlyList<HomeworkStudent> Students,
    IReadOnlyList<string> Errors);

/// <summary>名单模块共用的文本规整（trim + 折叠空白 + 列字母）。</summary>
internal static class RosterText
{
    public static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim().Replace('\u3000', ' '), @"\s+", " ");

    public static string ColumnLetter(int column)
    {
        var current = column + 1;
        var letters = string.Empty;
        while (current > 0)
        {
            current--;
            letters = (char)('A' + current % 26) + letters;
            current /= 26;
        }
        return letters;
    }
}
