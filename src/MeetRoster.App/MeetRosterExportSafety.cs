using System;
using System.Collections.Generic;
using System.IO;

namespace MeetRoster.App;

/// <summary>
/// 「另存为」的目标清单与计数（审计 P2-15，上轮 P2-7 的正式回补）。
///
/// 目标路径可能**就是导入的原件**：老师选了当初导入报名表的那个文件夹时，
/// 目标文件名与原件同名。这种目标既不能照写（导出器与 Core 守卫都会拒绝），
/// 也不该算进「将覆盖 N 份」——那是在向老师承诺一个不会发生、也不该发生的动作。
/// 全被拦时直说「这个文件夹就是原件所在位置」；部分被拦时分开计数，并且**真的跳过**。
/// </summary>
public sealed record RegistrationExportPlan(
    IReadOnlyList<string> ClassNames,
    IReadOnlyList<string> BlockedNames,
    int OverwriteCount)
{
    public int SkippedCount => BlockedNames.Count;

    /// <summary>可写的目标一份都没有，且原因全是「目标就是原件」。</summary>
    public bool AllBlocked => ClassNames.Count == 0 && BlockedNames.Count > 0;
}

public static class RegistrationExportPlanner
{
    /// <summary>与 <c>RegistrationWorkbookExporter.Export</c> 的目标文件名一致。</summary>
    public static string TargetPath(string directory, string className) =>
        Path.Combine(directory, className + "运动会报名表.xlsx");

    /// <summary>
    /// 把班级分成「可写」与「目标就是原件、跳过」两组，并只对可写目标统计同名文件数。
    /// <paramref name="isBlocked"/> / <paramref name="fileExists"/> 由调用方注入，
    /// 判定逻辑与文件系统都可替换，便于按真实临时目录做回归。
    /// </summary>
    public static RegistrationExportPlan Plan(
        IEnumerable<string> classNames,
        string directory,
        Func<string, bool> isBlocked,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(classNames);
        ArgumentNullException.ThrowIfNull(isBlocked);
        ArgumentNullException.ThrowIfNull(fileExists);

        var writable = new List<string>();
        var blocked = new List<string>();
        var overwrite = 0;

        foreach (var name in classNames)
        {
            var target = TargetPath(directory, name);
            if (isBlocked(target))
            {
                blocked.Add(name);
                continue;
            }

            writable.Add(name);
            if (fileExists(target)) overwrite++;
        }

        return new RegistrationExportPlan(writable, blocked, overwrite);
    }
}

/// <summary>
/// 关窗「先导出留档」的判定（审计 P2-16）。
///
/// 旧实现把「上一次导出的路径」一直留着：这次点「先导出留档」→ 在保存框里取消
/// → 没有任何文件写出，但旧路径还在，<c>File.Exists</c> 判定为「已留档」，
/// 于是直接关窗、改动全丢。现在每次进入导出先清空，只有**本次真的写出**才记路径。
/// </summary>
public sealed class ArchiveExportTracker
{
    public string? LastExportedPath { get; private set; }

    /// <summary>进入导出流程：上一次的路径立刻作废。</summary>
    public void BeginExport() => LastExportedPath = null;

    /// <summary>导出结束：只有本次成功写出才记路径。</summary>
    public void CompleteExport(bool exported, string? path)
    {
        if (exported && !string.IsNullOrWhiteSpace(path)) LastExportedPath = path;
    }

    /// <summary>本次是否真的留下了一份文件（关窗留档的放行条件）。</summary>
    public bool HasArchive =>
        !string.IsNullOrEmpty(LastExportedPath) && File.Exists(LastExportedPath);
}
