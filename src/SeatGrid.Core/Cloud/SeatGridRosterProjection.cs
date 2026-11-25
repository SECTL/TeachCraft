using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Cloud;

/// <summary>
/// 归位 <see cref="Student"/> 与共享名单中立输入之间的投影。
/// <para>
/// 只投影调用方明确选择的可选字段；未选字段一律置 null，这样序列化后的云端文档不会带上
/// 老师没同意共享的身高 / 视力 / 成绩 / 备注 / 原小组（隐私红线）。
/// </para>
/// </summary>
public static class SeatGridRosterProjection
{
    /// <summary>把归位名单投影成共享名单学生（1 基序号，性别归一到 男 / 女 / 未知）。</summary>
    public static IReadOnlyList<SeatGridRosterStudent> FromStudents(
        IReadOnlyList<Student> students, SeatGridRosterFields fields)
    {
        var result = new List<SeatGridRosterStudent>(students.Count);
        for (var i = 0; i < students.Count; i++)
        {
            var student = students[i];
            result.Add(new SeatGridRosterStudent(
                i + 1,
                student.Name,
                student.GenderLabel,
                student.Number,
                fields.Height ? student.Height : null,
                fields.Vision ? student.Vision : null,
                fields.Score ? student.Score : null,
                fields.Notes ? student.Notes : null,
                fields.OriginalGroup ? student.OriginalGroup : null));
        }

        return result;
    }

    /// <summary>
    /// 由共享名单学生重建归位 <see cref="Student"/>：一律新建对象、新 Id，绝不复用旧 Id。
    /// 空姓名与重名只告警、不丢人，保证序号与名单行一一对应（约束按序号 + 姓名定位）。
    /// </summary>
    public static List<Student> ToStudents(
        IReadOnlyList<SeatGridRosterStudent> students, List<string> warnings)
    {
        var result = new List<Student>(students.Count);
        var emptyNames = 0;
        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in students)
        {
            var name = item.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) emptyNames++;
            else nameCounts[name] = nameCounts.GetValueOrDefault(name) + 1;

            result.Add(new Student
            {
                Name = name,
                Gender = ParseGender(item.Gender),
                Number = item.Number ?? string.Empty,
                Height = item.Height,
                Vision = item.Vision,
                Score = item.Score,
                Notes = item.Notes ?? string.Empty,
                OriginalGroup = item.OriginalGroup ?? string.Empty,
            });
        }

        if (emptyNames > 0)
            warnings.Add($"名单中有 {emptyNames} 条空姓名，已保留但相关座位或约束可能无法匹配。");

        var duplicates = nameCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (duplicates.Count > 0)
            warnings.Add($"名单中有 {duplicates.Count} 个重名（如 {string.Join("、", duplicates.Take(3))}），约束按序号优先匹配。");

        return result;
    }

    private static Gender ParseGender(string? value) => value?.Trim() switch
    {
        "男" => Gender.Male,
        "女" => Gender.Female,
        _ => Gender.Unknown,
    };
}
