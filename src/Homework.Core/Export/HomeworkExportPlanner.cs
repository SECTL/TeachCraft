using Homework.Core;
using Homework.Core.Models;

namespace Homework.Core.Export;

/// <summary>只读文件系统探针：规划阶段不碰磁盘，存在性由调用方注入。</summary>
public interface IFileProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
}

/// <summary>
/// 作业导出的纯规划器（dry run）：只算出要写什么、会撞什么，不创建任何文件 / 目录。
/// 所有命名走 <see cref="HomeworkFileNamer"/>，所有路径比对走 <see cref="HomeworkPathGuard"/>。
/// </summary>
public static class HomeworkExportPlanner
{
    private sealed record ClassInfo(string Name, string Folder);

    public static HomeworkExportPlan Build(
        HomeworkBatch batch,
        HomeworkExportOptions options,
        IFileProbe probe,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);

        if (batch.State != HomeworkBatchState.Editing)
            throw new HomeworkException("当前批次已结束编辑，不能导出。");
        if (batch.Pages.Count == 0)
            throw new HomeworkException("还没有加入任何页，不能导出。");

        var warnings = new List<string>();
        var conflicts = new List<string>();
        var classes = BuildClassMap(batch.Classes, options.OutputDirectory, warnings);

        var pagesByStudent = batch.Pages
            .Where(p => p.StudentId.HasValue)
            .GroupBy(p => p.StudentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Index).ToList());

        var sources = batch.Pages.Select(p => p.SourcePath).ToList();
        var entries = new List<HomeworkExportEntry>();
        var namesPerFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var student in batch.Students)
        {
            if (!pagesByStudent.TryGetValue(student.Id, out var pages) || pages.Count == 0) continue;

            classes.TryGetValue(student.ClassId, out var classInfo);
            var className = classInfo?.Name ?? string.Empty;
            var classFolder = classInfo?.Folder
                              ?? Path.Combine(options.OutputDirectory, HomeworkFileNamer.ClassFolderName(className));

            switch (options.Mode)
            {
                case HomeworkExportMode.MergePdf:
                {
                    var file = HomeworkFileNamer.StudentPdfFileName(className, student.Number, student.Name);
                    var first = pages[0];
                    entries.Add(new HomeworkExportEntry(
                        HomeworkExportEntryKind.MergedPdf, null, student.Id,
                        first.SourcePath, 0, Path.Combine(classFolder, file)));
                    break;
                }

                case HomeworkExportMode.CopyToFolders:
                {
                    var stem = HomeworkFileNamer.StudentStem(className, student.Number, student.Name);
                    var folder = Path.Combine(classFolder, stem);
                    if (!namesPerFolder.TryGetValue(folder, out var used))
                        namesPerFolder[folder] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var page in pages)
                    {
                        var original = Path.GetFileName(page.SourcePath);
                        if (string.IsNullOrEmpty(original)) original = "未命名";
                        var targetName = DedupeName(original, used, warnings);
                        entries.Add(new HomeworkExportEntry(
                            HomeworkExportEntryKind.FolderCopy, page.Id, student.Id,
                            page.SourcePath, page.SourcePageNumber, Path.Combine(folder, targetName)));
                    }
                    break;
                }

                default: // RenameArchive / ArchiveWithIndex
                {
                    for (var i = 0; i < pages.Count; i++)
                    {
                        var page = pages[i];
                        var ext = page.Kind == HomeworkPageKind.PdfPage
                            ? "pdf"
                            : Path.GetExtension(page.SourcePath);
                        var file = HomeworkFileNamer.StudentFileName(
                            className, student.Number, student.Name, i + 1, ext);
                        var kind = page.Kind == HomeworkPageKind.PdfPage
                            ? HomeworkExportEntryKind.PdfPageExtract
                            : HomeworkExportEntryKind.ImageCopy;
                        entries.Add(new HomeworkExportEntry(
                            kind, page.Id, student.Id, page.SourcePath, page.SourcePageNumber,
                            Path.Combine(classFolder, file)));
                    }
                    break;
                }
            }
        }

        // 目标路径去重：任何两条最终指向同一路径都记冲突，并确定性地改名，保证 Entries 内无重复。
        var finalEntries = new List<HomeworkExportEntry>(entries.Count);
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var target = entry.TargetPath;
            if (usedTargets.Contains(HomeworkPathGuard.Normalize(target)))
            {
                var resolved = NextUniqueTarget(target, usedTargets);
                conflicts.Add($"目标路径冲突：多个条目指向「{target}」，已改名为「{resolved}」。");
                warnings.Add($"目标路径「{target}」被多个条目占用，已改名为「{resolved}」。");
                finalEntries.Add(entry with { TargetPath = resolved });
            }
            else
            {
                usedTargets.Add(HomeworkPathGuard.Normalize(target));
                finalEntries.Add(entry);
            }
        }

        // 外部冲突：目标是导入来源（永远封锁）/ 目标已存在且未开启覆盖。
        foreach (var entry in finalEntries)
        {
            if (HomeworkPathGuard.IsBlockedTarget(entry.TargetPath, sources))
                conflicts.Add($"目标「{entry.TargetPath}」是导入来源，禁止覆盖。");
            if (!options.OverwriteExisting && probe.FileExists(entry.TargetPath))
                conflicts.Add($"目标「{entry.TargetPath}」已存在，未开启覆盖时不能写出。");
        }

        string? indexPath = null;
        if (options.Mode == HomeworkExportMode.ArchiveWithIndex)
        {
            var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
            var name = $"作业索引_{HomeworkFileNamer.Sanitize(batch.Name)}_{stamp}.xlsx";
            indexPath = Path.Combine(options.OutputDirectory, name);
        }

        var pageCount = finalEntries.Count(e => e.PageId.HasValue);
        var studentCount = finalEntries
            .Where(e => e.StudentId.HasValue)
            .Select(e => e.StudentId!.Value)
            .Distinct()
            .Count();

        return new HomeworkExportPlan(
            finalEntries, conflicts, batch.StudentsWithoutPages.ToList(), warnings,
            indexPath, pageCount, studentCount);
    }

    private static Dictionary<Guid, ClassInfo> BuildClassMap(
        IReadOnlyList<HomeworkClass> classes, string outputDirectory, List<string> warnings)
    {
        var result = new Dictionary<Guid, ClassInfo>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in classes)
        {
            var baseName = HomeworkFileNamer.ClassFolderName(cls.Name);
            var folderName = baseName;
            var n = 2;
            while (!used.Add(folderName)) folderName = $"{baseName}({n++})";
            if (!string.Equals(folderName, baseName, StringComparison.Ordinal))
                warnings.Add($"班级「{cls.Name}」的文件夹名与其他班级重复，已改为「{folderName}」。");
            result[cls.Id] = new ClassInfo(cls.Name, Path.Combine(outputDirectory, folderName));
        }
        return result;
    }

    private static string DedupeName(string fileName, HashSet<string> used, List<string> warnings)
    {
        if (used.Add(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var n = 2;
        string candidate;
        do { candidate = $"{stem} ({n++}){ext}"; } while (!used.Add(candidate));
        warnings.Add($"同一文件夹内存在同名文件「{fileName}」，已改名为「{candidate}」。");
        return candidate;
    }

    private static string NextUniqueTarget(string target, HashSet<string> used)
    {
        var dir = Path.GetDirectoryName(target) ?? string.Empty;
        var file = Path.GetFileName(target);
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var n = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{stem} ({n++}){ext}"); }
        while (!used.Add(HomeworkPathGuard.Normalize(candidate)));
        return candidate;
    }
}
