using System;
using System.IO;

namespace SeatGrid.Core.Cloud;

/// <summary>
/// 从导入文件名或用户标题里猜一个班级名单名。按「用户标题优先、文件名兜底」取候选，
/// 去掉座位表 / 学生名单 / 花名册 / 名单这些后缀；猜不出时给「班级名单」。
/// </summary>
public static class SeatGridRosterName
{
    private const string Default = "班级名单";

    // 长的在前：「学生名单」里也含「名单」，先剥短的会留下「学生」。
    private static readonly string[] Suffixes = { "学生名单", "座位表", "花名册", "名单" };

    public static string Suggest(string? importSource, string? title)
    {
        var candidate = !string.IsNullOrWhiteSpace(title)
            ? title.Trim()
            : !string.IsNullOrWhiteSpace(importSource)
                ? Path.GetFileNameWithoutExtension(importSource.Trim())
                : string.Empty;

        if (candidate.Length == 0) return Default;

        foreach (var suffix in Suffixes)
        {
            if (!candidate.EndsWith(suffix, StringComparison.Ordinal)) continue;
            candidate = candidate[..^suffix.Length].TrimEnd();
            break;
        }

        return candidate.Length == 0 ? Default : candidate;
    }
}
