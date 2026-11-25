using Homework.Core.Models;

namespace Homework.Core.Recognition;

/// <summary>学号解析结果：唯一命中、无匹配、或多人歧义。</summary>
public enum FillInNumberStatus
{
    Matched,
    NoMatch,
    Ambiguous,
}

/// <summary>
/// 一次学号解析的结果。只有 Matched 才带 StudentId；其余状态 StudentId 恒为 null，
/// FailureReason 给出面向老师的中文原因。本记录不写匹配、不修改名单。
/// </summary>
public sealed record FillInNumberResolution(
    FillInNumberStatus Status,
    Guid? StudentId,
    string? FailureReason);

/// <summary>
/// 把填涂读到的数字串解析成唯一学生。规则固定、不猜、不按姓名匹配：
/// <list type="number">
/// <item>只考虑 Number 非空且全为数字的学生；</item>
/// <item>先按 Ordinal 精确匹配，命中 1 人即 Matched，多于 1 人即 Ambiguous；</item>
/// <item>精确 0 人时双侧去前导零（全零归一为 "0"）再匹配，1 人 Matched、多于 1 人 Ambiguous、0 人 NoMatch；</item>
/// <item>digits 为空或全空白直接 NoMatch。</item>
/// </list>
/// </summary>
public static class FillInNumberResolver
{
    /// <param name="classNames">
    /// 可选的 ClassId → 班级名映射，仅用于把歧义原因里的班级 Id 换成老师可读的班级名。
    /// 调用方（识别编排器）可传 <c>batch.Classes.ToDictionary(c =&gt; c.Id, c =&gt; c.Name)</c>；
    /// 不传或查不到时回退为「班级 {ClassId}」，保证旧的 2 参调用照常工作。
    /// </param>
    public static FillInNumberResolution Resolve(
        string digits,
        IReadOnlyList<HomeworkStudent> students,
        IReadOnlyDictionary<Guid, string>? classNames = null)
    {
        if (string.IsNullOrWhiteSpace(digits))
            return NoMatch(digits);

        var eligible = students
            .Where(s => !string.IsNullOrEmpty(s.Number) && s.Number.All(char.IsDigit))
            .ToList();

        var exact = eligible.Where(s => string.Equals(s.Number, digits, StringComparison.Ordinal)).ToList();
        if (exact.Count == 1)
            return Matched(exact[0]);
        if (exact.Count > 1)
            return Ambiguous(digits, exact, classNames);

        var normalizedDigits = StripLeadingZeros(digits);
        var numeric = eligible.Where(s => string.Equals(StripLeadingZeros(s.Number), normalizedDigits, StringComparison.Ordinal)).ToList();
        if (numeric.Count == 1)
            return Matched(numeric[0]);
        if (numeric.Count > 1)
            return Ambiguous(digits, numeric, classNames);

        return NoMatch(digits);
    }

    /// <summary>去前导零；全零（TrimStart 后为空）归一为 "0"。</summary>
    private static string StripLeadingZeros(string value)
    {
        var trimmed = value.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static FillInNumberResolution Matched(HomeworkStudent student) =>
        new(FillInNumberStatus.Matched, student.Id, null);

    private static FillInNumberResolution NoMatch(string digits) =>
        new(FillInNumberStatus.NoMatch, null, $"没有学号为 {digits} 的学生。");

    private static FillInNumberResolution Ambiguous(
        string digits,
        IReadOnlyList<HomeworkStudent> matches,
        IReadOnlyDictionary<Guid, string>? classNames)
    {
        // 有班级名映射时给出老师可读的班级名；否则回退到班级 Id，保证 2 参调用照常工作。
        var details = string.Join("、", matches.Select(Describe));
        return new FillInNumberResolution(
            FillInNumberStatus.Ambiguous,
            null,
            $"学号 {digits} 对应多名学生，无法确定归属：{details}。");

        string Describe(HomeworkStudent s) =>
            classNames is not null && classNames.TryGetValue(s.ClassId, out var className)
                ? $"{s.Name}（{className}）"
                : $"{s.Name}（班级 {s.ClassId}）";
    }
}
