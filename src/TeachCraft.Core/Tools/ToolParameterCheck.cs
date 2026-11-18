namespace TeachCraft.Core.Tools;

/// <summary>
/// 保存框之前的轻量参数预检。
///
/// 此前「合并至少两份」「页码格式」这类错误要等老师选完输出位置、点掉保存框之后
/// 才由 <see cref="ToolDispatcher"/> 抛出——先让老师白走一趟对话框，再告诉他参数不对
/// （调研 P2-24）。这里只判**不依赖输出位置、不读文件内容**就能确定的错误：
/// 真正的执行期校验仍在 Dispatcher / 各工具里，一条不少。
/// </summary>
public static class ToolParameterCheck
{
    /// <summary>预检参数；没有问题返回 null，有问题返回可直接进状态栏的中文文案。</summary>
    public static string? Validate(ToolKind kind, IReadOnlyList<string> inputs, string? pageRange)
    {
        if (inputs.Count == 0) return "请先选择要处理的文件。";

        return kind switch
        {
            ToolKind.PdfMerge when inputs.Count < 2 => "合并至少需要两份 PDF，请再选一份。",
            ToolKind.PdfSplit => ValidatePageRangeSyntax(pageRange),
            _ => null,
        };
    }

    /// <summary>
    /// 只校验页码的**写法**（不知道总页数也能判的部分）：数字、区间、起止不倒置。
    /// 超范围（如共 5 页却填 9）要读文件才知道，留给执行期。
    /// </summary>
    public static string? ValidatePageRangeSyntax(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var raw in text.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            var dash = part.IndexOfAny(new[] { '-', '–', '—', '~' });
            if (dash < 0)
            {
                if (!int.TryParse(part, out var single) || single < 1)
                    return $"页码 “{part}” 无效，请填 1 以上的整数。";
                continue;
            }

            var left = part[..dash].Trim();
            var right = part[(dash + 1)..].Trim();

            // 两端都空（“-”）既不是单页也不是区间，说清楚比猜强。
            if (left.Length == 0 && right.Length == 0)
                return $"页码 “{part}” 无效，请写成 “2-5” 或 “3” 这样的形式。";

            if (left.Length > 0 && (!int.TryParse(left, out var start) || start < 1))
                return $"页码 “{part}” 无效，起始页请填 1 以上的整数。";

            if (right.Length > 0 && (!int.TryParse(right, out var end) || end < 1))
                return $"页码 “{part}” 无效，结束页请填 1 以上的整数。";

            if (left.Length > 0 && right.Length > 0
                && int.TryParse(left, out var s) && int.TryParse(right, out var e) && s > e)
                return $"页码 “{part}” 无效：起始页不能大于结束页。";
        }

        return null;
    }
}
