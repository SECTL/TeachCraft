namespace TeachCraft.Core.Cloud;

/// <summary>
/// 共享名单文档的发布前校验。核心不变量：任何可选字段只要没在 <c>Fields</c> 里勾选，
/// 就必须是空值——否则宁可拒绝发布，也不能把用户没同意的字段送进云端。
/// </summary>
public static class SharedRosterValidator
{
    private static readonly string[] AllowedGenders = { "男", "女", "未知" };

    public static void Validate(SharedRosterDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Students is null || doc.Students.Count == 0)
            throw new InvalidOperationException(CloudErrorText.RosterEmpty);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var student in doc.Students)
        {
            if (student is null || string.IsNullOrWhiteSpace(student.Name))
                throw new ArgumentException("共享名单中有学生姓名为空，请先补全后再导入。");

            if (!seen.Add(student.Name))
                throw new ArgumentException(CloudErrorText.RosterNameDuplicate);

            EnsureNotPopulated(doc.Fields.Height, student.Height.HasValue, "身高");
            EnsureNotPopulated(doc.Fields.Vision, student.Vision.HasValue, "视力");
            EnsureNotPopulated(doc.Fields.Score, student.Score.HasValue, "成绩");
            EnsureNotPopulated(doc.Fields.Notes, !string.IsNullOrWhiteSpace(student.Notes), "备注");
            EnsureNotPopulated(
                doc.Fields.OriginalGroup, !string.IsNullOrWhiteSpace(student.OriginalGroup), "原小组");
        }

        // 性别不是发布前阻断项：认不出就归为「未知」，不臆造、不抛错。
        foreach (var student in doc.Students)
        {
            if (student.Gender is null || Array.IndexOf(AllowedGenders, student.Gender) < 0)
                student.Gender = "未知";
        }
    }

    private static void EnsureNotPopulated(bool selected, bool populated, string label)
    {
        if (!selected && populated)
            throw new ArgumentException($"共享名单包含未勾选的字段「{label}」，请取消该字段后再发布。");
    }
}
