using Homework.Core.Models;

namespace Homework.Core.Roster;

/// <summary>共享名单里的一名学生：只消费姓名与学号，不含性别等可选字段。</summary>
public sealed record SharedRosterStudentInput(string Name, string Number);

/// <summary>共享名单里的一个班级及其学生，按输入顺序排列。</summary>
public sealed record SharedRosterClassInput(string Name, IReadOnlyList<SharedRosterStudentInput> Students);

/// <summary>跳过原因的分类计数，三项之和等于 <see cref="RosterImportResult.SkippedRows"/>。</summary>
public readonly record struct RosterSkipBreakdown(int EmptyNameRows, int DuplicateRows, int MissingRosters)
{
    public int Total => EmptyNameRows + DuplicateRows + MissingRosters;
}

/// <summary>
/// 把中性的共享名单输入转换成辑册既有的 <see cref="RosterImportResult"/>。
/// 纯映射：不引用任何云 DTO / HTTP 类型；宿主桥接层负责把云名单映射成这里的输入。
/// 语义对齐 <see cref="HomeworkRosterImporter"/>：新 Guid、按首次出现的班级归并、
/// 文本规整复用 <see cref="RosterText"/>；差异仅在重复行改为「告警 + 跳过」而非抛错。
/// </summary>
public static class SharedRosterImport
{
    private const string FallbackClassName = "未分班";

    /// <summary>跳过原因的可机读标记，文案本身保持中文可读；生产者与状态栏共用同一套。</summary>
    public const string EmptyNameMarker = "没有姓名";
    public const string DuplicateMarker = "重复";
    public const string MissingRosterMarker = "名单缺失";

    /// <summary>
    /// 班级名归一化：trim + 折叠空白 + 忽略大小写。
    /// 选择器去重与名单合并必须共用它，否则「八年级 1班」与「八年级1班」会被当成两份，
    /// 拉取后合并时冲突学生又被静默跳过。
    /// </summary>
    public static string NormalizeClassName(string? name) => RosterText.Clean(name).ToUpperInvariant();

    /// <summary>从告警文案反推跳过原因分类，供状态栏如实计数。</summary>
    public static RosterSkipBreakdown CountSkipReasons(IReadOnlyList<string> warnings)
    {
        var empty = warnings.Count(w => w.Contains(EmptyNameMarker, StringComparison.Ordinal));
        var duplicates = warnings.Count(w => w.Contains(DuplicateMarker, StringComparison.Ordinal));
        var missing = warnings.Count(w => w.Contains(MissingRosterMarker, StringComparison.Ordinal));
        return new RosterSkipBreakdown(empty, duplicates, missing);
    }

    public static RosterImportResult ToRosterImportResult(
        IReadOnlyList<SharedRosterClassInput> classes,
        IReadOnlyList<string>? sourceWarnings = null,
        int extraSkipped = 0)
    {
        var resultClasses = new List<HomeworkClass>();
        var classByKey = new Dictionary<string, HomeworkClass>(StringComparer.Ordinal);
        var students = new List<HomeworkStudent>();
        var warnings = sourceWarnings is null
            ? new List<string>()
            : new List<string>(sourceWarnings);
        var seenNames = new HashSet<(string Class, string Name)>();
        var seenNumbers = new HashSet<(string Class, string Number)>();
        var emptyNameSkipped = 0;
        var duplicateSkipped = 0;

        foreach (var input in classes)
        {
            var className = RosterText.Clean(input.Name);
            if (className.Length == 0)
            {
                className = FallbackClassName;
                warnings.Add($"有一行没有班级名称，已归入「{FallbackClassName}」。");
            }

            var classKey = NormalizeClassName(className);
            if (!classByKey.TryGetValue(classKey, out var homeworkClass))
            {
                homeworkClass = new HomeworkClass(Guid.NewGuid(), className);
                classByKey.Add(classKey, homeworkClass);
                resultClasses.Add(homeworkClass);
            }

            foreach (var student in input.Students)
            {
                var name = RosterText.Clean(student.Name);
                if (name.Length == 0)
                {
                    emptyNameSkipped++;
                    warnings.Add($"「{className}」有一行{EmptyNameMarker}，已跳过。");
                    continue;
                }

                var number = RosterText.Clean(student.Number);

                if (number.Length > 0 && !seenNumbers.Add((classKey, number)))
                {
                    duplicateSkipped++;
                    warnings.Add($"班级「{className}」内学号「{number}」{DuplicateMarker}，已跳过重复行。");
                    continue;
                }

                if (!seenNames.Add((classKey, name)))
                {
                    duplicateSkipped++;
                    warnings.Add($"班级「{className}」内存在同名学生「{name}」，已跳过{DuplicateMarker}行。");
                    continue;
                }

                students.Add(new HomeworkStudent(Guid.NewGuid(), homeworkClass.Id, number, name));
            }
        }

        return new RosterImportResult(
            resultClasses, students, warnings, emptyNameSkipped + duplicateSkipped + extraSkipped);
    }
}
