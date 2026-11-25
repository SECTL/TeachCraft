using Homework.Core.Models;

namespace Homework.Core.Roster;

/// <summary>
/// 从表格粘贴的纯文本里逐行解析名单。支持「姓名」「学号 姓名」「学号,姓名」
/// 「班级,学号,姓名」以及 Excel 的制表符粘贴；出错的行带 1 基行号记入 Errors 并跳过。
/// </summary>
public static class RosterPasteParser
{
    public static RosterPasteResult Parse(string text, string defaultClassName)
    {
        var fallbackClass = string.IsNullOrWhiteSpace(defaultClassName)
            ? "未分班"
            : RosterText.Clean(defaultClassName);

        var classes = new List<HomeworkClass>();
        var classByName = new Dictionary<string, HomeworkClass>(StringComparer.Ordinal);
        var students = new List<HomeworkStudent>();
        var errors = new List<string>();
        var seenNumbers = new HashSet<(string Class, string Number)>();

        if (string.IsNullOrWhiteSpace(text))
            return new RosterPasteResult(classes, students, errors);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            if (string.IsNullOrWhiteSpace(lines[index])) continue;

            var parts = SplitLine(lines[index]);
            if (parts.Count == 0) continue;
            if (parts.Count > 3)
            {
                errors.Add($"第 {lineNumber} 行字段过多，无法识别，已跳过。");
                continue;
            }

            string className;
            string number;
            string name;
            switch (parts.Count)
            {
                case 1:
                    className = fallbackClass;
                    number = string.Empty;
                    name = parts[0];
                    break;
                case 2:
                    className = fallbackClass;
                    number = parts[0];
                    name = parts[1];
                    break;
                default:
                    className = parts[0];
                    number = parts[1];
                    name = parts[2];
                    break;
            }

            name = RosterText.Clean(name);
            if (name.Length == 0)
            {
                errors.Add($"第 {lineNumber} 行缺少姓名，已跳过。");
                continue;
            }

            number = RosterText.Clean(number);
            className = RosterText.Clean(className);
            if (className.Length == 0) className = fallbackClass;

            if (number.Length > 0 && !seenNumbers.Add((className, number)))
            {
                errors.Add($"第 {lineNumber} 行学号「{number}」在本班重复，已跳过。");
                continue;
            }

            if (!classByName.TryGetValue(className, out var homeworkClass))
            {
                homeworkClass = new HomeworkClass(Guid.NewGuid(), className);
                classByName.Add(className, homeworkClass);
                classes.Add(homeworkClass);
            }

            students.Add(new HomeworkStudent(Guid.NewGuid(), homeworkClass.Id, number, name));
        }

        return new RosterPasteResult(classes, students, errors);
    }

    private static List<string> SplitLine(string line)
    {
        string[] raw;
        if (line.Contains('\t'))
            raw = line.Split('\t');
        else if (line.Contains(',') || line.Contains('，'))
            raw = line.Split(new[] { ',', '，' });
        else
            raw = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // 不裁掉末尾空字段：「01,」这类缺姓名的行必须保留空字段，才能被记成错误而不是误当成姓名。
        return raw.Select(RosterText.Clean).ToList();
    }
}
