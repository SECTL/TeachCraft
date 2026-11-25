namespace Homework.Core.Export;

/// <summary>
/// 输出路径防线：导出目标不得覆盖任何导入来源。路径先归一化成完整路径再按
/// OrdinalIgnoreCase 比较（Windows / macOS 默认文件系统大小写不敏感）。
/// </summary>
public static class HomeworkPathGuard
{
    public static string Normalize(string path) => Path.GetFullPath(path);

    public static bool IsSamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>目标命中任一非空来源即视为被封锁。</summary>
    public static bool IsBlockedTarget(string target, IEnumerable<string> sources)
    {
        var normalizedTarget = Normalize(target);
        return sources.Any(source =>
            !string.IsNullOrWhiteSpace(source) &&
            string.Equals(normalizedTarget, Normalize(source), StringComparison.OrdinalIgnoreCase));
    }
}
