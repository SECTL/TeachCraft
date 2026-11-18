namespace MeetRoster.Core.Export;

/// <summary>
/// 三个导出器（<see cref="MeetWorkbookExporter"/> / <see cref="MeetPdfExporter"/> /
/// <see cref="MeetOriginalWorkbookHighlighter"/>）共用的 Core 层源文件防线。
/// <para>
/// UI 层的 IsExportPathBlocked 是第一道；这里是纵深防御的第二道——
/// 「只含赛程的工作簿」这条路曾在两处同时漏防（审计 P1-1），单靠 UI 一处的检查
/// 一旦被新入口绕过就是原件被覆盖。任何新增导出器都必须先过这一道再写盘。
/// </para>
/// <para>
/// 三个导出器的拒绝文案各自不同（「原工作簿」/「原始报名表」），消息断言钉住了它们，
/// 所以拒绝时必须带上调用方给的分源标签——抽方法不得吞掉话术差异。
/// </para>
/// </summary>
public static class MeetExportSourceGuard
{
    /// <summary>
    /// 导出路径命中任一导入来源（秩序册 / 赛程工作簿、逐班原始报名表）时拒绝。
    /// 命中 <paramref name="projectSourceLabel"/> 描述的工程级来源抛出带该标签的异常；
    /// 命中某个班的原始报名表抛出带 <paramref name="classSourceLabel"/> 的异常。
    /// </summary>
    public static void EnsureNotOverwritingSources(
        string outputPath, MeetRoster.Core.Models.MeetProject project,
        string projectSourceLabel, string classSourceLabel)
    {
        var target = Path.GetFullPath(outputPath);

        if (!string.IsNullOrWhiteSpace(project.SourcePath) &&
            string.Equals(target, Path.GetFullPath(project.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"导出文件不能覆盖{projectSourceLabel}，请换一个文件名。");
        }

        foreach (var source in project.Classes
                     .Select(c => c.SourcePath)
                     .Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (string.Equals(target, Path.GetFullPath(source!), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"导出文件不能覆盖{classSourceLabel}，请换一个文件名。");
        }
    }
}
