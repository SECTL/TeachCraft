using Homework.Core;
using Homework.Core.Export;
using Homework.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 作业导出纯规划器。全部使用合成班级 / 姓名（苏晚晴 / 陈屿 / 许砚），
/// 页来源用临时文件，测试结束即删除；规划器本身绝不创建文件。
/// </summary>
public sealed class HomeworkExportPlanTests : IDisposable
{
    private readonly string _root;

    public HomeworkExportPlanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hw-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string OutputDir => Path.Combine(_root, "out");

    private string NewSource(string name, string ext = ".png")
    {
        var dir = Path.Combine(_root, "src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ext);
        File.WriteAllText(path, "x");
        return path;
    }

    private HomeworkExportOptions Out(HomeworkExportMode mode, bool overwrite = false) =>
        new(OutputDir, mode, overwrite);

    private static HomeworkPage Add(
        HomeworkBatch batch, string path, HomeworkPageKind kind = HomeworkPageKind.Image, int page = 1)
    {
        Assert.True(batch.TryAddPage(path, kind, page, "h" + Guid.NewGuid().ToString("N"), 10, 10,
            out var added, out var error), error);
        return added!;
    }

    private static (HomeworkBatch Batch, HomeworkClass Cls, HomeworkStudent A, HomeworkStudent B)
        BatchWithRoster(string className = "八年级1班")
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var cls = new HomeworkClass(Guid.NewGuid(), className);
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), cls.Id, "02", "陈屿");
        batch.LoadRoster([cls], [a, b]);
        return (batch, cls, a, b);
    }

    private sealed class FakeProbe : IFileProbe
    {
        public Func<string, bool>? OnFileExists { get; init; }
        public Func<string, bool>? OnDirectoryExists { get; init; }
        public bool FileExists(string path) => OnFileExists?.Invoke(path) ?? false;
        public bool DirectoryExists(string path) => OnDirectoryExists?.Invoke(path) ?? false;
    }

    // ----------------------------- RenameArchive -----------------------------

    [Fact]
    public void RenameArchive_OneEntryPerPage_WithOriginalExtensionAndPerStudentSequence()
    {
        var (batch, _, a, b) = BatchWithRoster();
        var p1 = Add(batch, NewSource("a", ".png"));
        var p2 = Add(batch, NewSource("b", ".pdf"), HomeworkPageKind.PdfPage, 3);
        var p3 = Add(batch, NewSource("c", ".jpg"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, a.Id);
        batch.Match(p3.Id, b.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        Assert.Equal(3, plan.Entries.Count);
        Assert.Equal(new[]
        {
            HomeworkExportEntryKind.ImageCopy,
            HomeworkExportEntryKind.PdfPageExtract,
            HomeworkExportEntryKind.ImageCopy,
        }, plan.Entries.Select(e => e.Kind));
        Assert.Equal(new Guid?[] { p1.Id, p2.Id, p3.Id }, plan.Entries.Select(e => e.PageId));
        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png"), plan.Entries[0].TargetPath);
        // 第 2 页按 Index 编号，与来源页码 3 无关；PDF 页强制 .pdf。
        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第2页.pdf"), plan.Entries[1].TargetPath);
        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_02_陈屿_第1页.jpg"), plan.Entries[2].TargetPath);
        Assert.Equal(3, plan.PageCount);
        Assert.Equal(2, plan.StudentCount);
        Assert.Null(plan.IndexPath);
        Assert.Empty(plan.Conflicts);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.StudentsWithoutPages);
    }

    // ----------------------------- MergePdf -----------------------------

    [Fact]
    public void MergePdf_OneEntryPerStudentWithPages_NoEntryForEmptyStudent()
    {
        var (batch, cls, a, _) = BatchWithRoster();
        var empty = new HomeworkStudent(Guid.NewGuid(), cls.Id, "03", "许砚");
        batch.LoadRoster([cls], [a, empty]);
        var p1 = Add(batch, NewSource("a", ".png"));
        var p2 = Add(batch, NewSource("b", ".png"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.MergePdf), new FakeProbe());

        Assert.Single(plan.Entries);
        var entry = plan.Entries[0];
        Assert.Equal(HomeworkExportEntryKind.MergedPdf, entry.Kind);
        Assert.Null(entry.PageId);
        Assert.Equal(a.Id, entry.StudentId);
        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴.pdf"), entry.TargetPath);
        Assert.Equal(0, plan.PageCount);   // 合并条目不映射单页
        Assert.Equal(1, plan.StudentCount);
        Assert.Single(plan.StudentsWithoutPages);
        Assert.Equal(empty.Id, plan.StudentsWithoutPages[0].Id);
    }

    // ----------------------------- ArchiveWithIndex -----------------------------

    [Fact]
    public void ArchiveWithIndex_SameEntriesAsRenameArchive_AndTimestampedIndex()
    {
        var (batch, cls, a, _) = BatchWithRoster();
        batch.LoadRoster([cls], [a]);
        var p = Add(batch, NewSource("a", ".png"));
        batch.Match(p.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(
            batch, Out(HomeworkExportMode.ArchiveWithIndex), new FakeProbe(),
            new DateTime(2026, 11, 22, 9, 5, 3));

        Assert.Single(plan.Entries);
        Assert.Equal(HomeworkExportEntryKind.ImageCopy, plan.Entries[0].Kind);
        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png"), plan.Entries[0].TargetPath);
        Assert.Equal(Path.Combine(OutputDir, "作业索引_第一次作业_20261122-090503.xlsx"), plan.IndexPath);
    }

    [Fact]
    public void IndexPath_SanitizesBatchName()
    {
        var batch = HomeworkBatch.Create("第一次/作业:*");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        batch.LoadRoster([cls], [a]);
        var p = Add(batch, NewSource("a"));
        batch.Match(p.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(
            batch, Out(HomeworkExportMode.ArchiveWithIndex), new FakeProbe(),
            new DateTime(2026, 1, 2, 3, 4, 5));

        var expected = Path.Combine(OutputDir,
            $"作业索引_{HomeworkFileNamer.Sanitize(batch.Name)}_20260102-030405.xlsx");
        Assert.Equal(expected, plan.IndexPath);
    }

    // ----------------------------- CopyToFolders -----------------------------

    [Fact]
    public void CopyToFolders_DuplicateOriginalNames_GetSpaceTwoSuffixAndWarning()
    {
        var (batch, cls, a, _) = BatchWithRoster();
        batch.LoadRoster([cls], [a]);
        var p1 = Add(batch, NewSource("scan", ".png"));
        var p2 = Add(batch, NewSource("scan", ".png"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.CopyToFolders), new FakeProbe());

        Assert.Equal(2, plan.Entries.Count);
        Assert.All(plan.Entries, e => Assert.Equal(HomeworkExportEntryKind.FolderCopy, e.Kind));
        var folder = Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴");
        Assert.Equal(Path.Combine(folder, "scan.png"), plan.Entries[0].TargetPath);
        Assert.Equal(Path.Combine(folder, "scan (2).png"), plan.Entries[1].TargetPath);
        Assert.Contains(plan.Warnings, w => w.Contains("scan"));
        Assert.Equal(2, plan.Entries.Select(e => e.TargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ----------------------------- 班级与命名 -----------------------------

    [Fact]
    public void MixedTwoClasses_UseSeparateClassFolders()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var c1 = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var c2 = new HomeworkClass(Guid.NewGuid(), "八年级2班");
        var a = new HomeworkStudent(Guid.NewGuid(), c1.Id, "01", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), c2.Id, "02", "陈屿");
        batch.LoadRoster([c1, c2], [a, b]);
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, b.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png"), plan.Entries[0].TargetPath);
        Assert.Equal(Path.Combine(OutputDir, "八年级2班", "八年级2班_02_陈屿_第1页.png"), plan.Entries[1].TargetPath);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void BlankNumber_OmitsNumberWithoutDoubleUnderscore()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "苏晚晴");
        batch.LoadRoster([cls], [a]);
        var p = Add(batch, NewSource("a"));
        batch.Match(p.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        Assert.Equal(Path.Combine(OutputDir, "八年级1班", "八年级1班_苏晚晴_第1页.png"), plan.Entries[0].TargetPath);
    }

    [Fact]
    public void BlankNameAndBlankClass_UseNamerFallback()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "");
        batch.LoadRoster([cls], [a]);
        var p = Add(batch, NewSource("a"));
        batch.Match(p.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        Assert.Equal(Path.Combine(OutputDir, "未命名", "未命名_第1页.png"), plan.Entries[0].TargetPath);
    }

    [Fact]
    public void InvalidClassName_IsSanitizedInFolderAndFileName()
    {
        var (batch, _, a, _) = BatchWithRoster("八年级/1班:*");
        batch.LoadRoster([batch.Classes[0]], [a]);
        var p = Add(batch, NewSource("a"));
        batch.Match(p.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        var folder = HomeworkFileNamer.ClassFolderName("八年级/1班:*");
        Assert.Equal(Path.Combine(OutputDir, folder, folder + "_01_苏晚晴_第1页.png"), plan.Entries[0].TargetPath);
    }

    [Fact]
    public void CollidingClassFolders_GetDeterministicTwoSuffixAndWarning()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        // 尾随空格会被 Sanitize 去掉，两个班级名归一后相同（跨平台一致）。
        var c1 = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var c2 = new HomeworkClass(Guid.NewGuid(), "八年级1班 ");
        var a = new HomeworkStudent(Guid.NewGuid(), c1.Id, "01", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), c2.Id, "02", "陈屿");
        batch.LoadRoster([c1, c2], [a, b]);
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, b.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        var baseFolder = HomeworkFileNamer.ClassFolderName("八年级1班");
        Assert.Equal(Path.Combine(OutputDir, baseFolder, baseFolder + "_01_苏晚晴_第1页.png"), plan.Entries[0].TargetPath);
        // 只有文件夹带 (2)，文件名仍用原班级名生成。
        Assert.Equal(Path.Combine(OutputDir, baseFolder + "(2)", baseFolder + "_02_陈屿_第1页.png"),
            plan.Entries[1].TargetPath);
        Assert.Contains(plan.Warnings, w => w.Contains("(2)"));
    }

    // ----------------------------- 冲突 -----------------------------

    [Fact]
    public void ExistingTarget_WithoutOverwrite_Conflicts_WithOverwrite_DoesNot()
    {
        var (batch, cls, a, _) = BatchWithRoster();
        batch.LoadRoster([cls], [a]);
        var p = Add(batch, NewSource("a"));
        batch.Match(p.Id, a.Id);
        var target = Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png");
        var probe = new FakeProbe
        {
            OnFileExists = path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase),
        };

        var strict = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), probe);
        Assert.Contains(strict.Conflicts, c => c.Contains("已存在"));

        var lenient = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive, overwrite: true), probe);
        Assert.DoesNotContain(lenient.Conflicts, c => c.Contains("已存在"));
    }

    [Fact]
    public void TargetEqualToImportedSource_AlwaysConflicts_EvenWithOverwrite()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        batch.LoadRoster([cls], [a]);
        var source = Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "x");
        var p = Add(batch, source);
        batch.Match(p.Id, a.Id);

        var plan = HomeworkExportPlanner.Build(
            batch, Out(HomeworkExportMode.RenameArchive, overwrite: true), new FakeProbe());

        Assert.Contains(plan.Conflicts, c => c.Contains("来源"));
    }

    [Fact]
    public void TwoEntriesSameTarget_CollisionIsConflictAndTargetsStayUnique()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        // 学号留空以绕过 LoadRoster 的同班重复学号校验；同名同班仍会撞目标路径。
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "苏晚晴");
        batch.LoadRoster([cls], [a, b]);
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, b.Id);

        var plan = HomeworkExportPlanner.Build(batch, Out(HomeworkExportMode.RenameArchive), new FakeProbe());

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(2, plan.Entries.Select(e => e.TargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(plan.Conflicts, c => c.Contains("冲突"));
        Assert.Contains(plan.Warnings, w => w.Contains("占用"));
    }

    // ----------------------------- 纯度与守卫 -----------------------------

    [Fact]
    public void Build_IsPure_CreatesNoFilesOrDirectories()
    {
        var (batch, cls, a, _) = BatchWithRoster();
        batch.LoadRoster([cls], [a]);
        var p = Add(batch, NewSource("a"));
        batch.Match(p.Id, a.Id);

        var filesBefore = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        var dirsBefore = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();

        _ = HomeworkExportPlanner.Build(
            batch, Out(HomeworkExportMode.ArchiveWithIndex), new FakeProbe(), DateTime.Now);

        Assert.Equal(filesBefore, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).OrderBy(x => x));
        Assert.Equal(dirsBefore, Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).OrderBy(x => x));
        Assert.False(Directory.Exists(OutputDir));
    }

    [Fact]
    public void Build_RejectedForEmptyCommittedAndAbandonedBatches()
    {
        var empty = HomeworkBatch.Create("空");
        Assert.Throws<HomeworkException>(() =>
            HomeworkExportPlanner.Build(empty, Out(HomeworkExportMode.RenameArchive), new FakeProbe()));

        var (committed, _, a, _) = BatchWithRoster();
        var p1 = Add(committed, NewSource("a"));
        committed.Match(p1.Id, a.Id);
        committed.Commit();
        Assert.Throws<HomeworkException>(() =>
            HomeworkExportPlanner.Build(committed, Out(HomeworkExportMode.RenameArchive), new FakeProbe()));

        var (abandoned, _, a2, _) = BatchWithRoster();
        var p2 = Add(abandoned, NewSource("b"));
        abandoned.Match(p2.Id, a2.Id);
        abandoned.Abandon();
        Assert.Throws<HomeworkException>(() =>
            HomeworkExportPlanner.Build(abandoned, Out(HomeworkExportMode.RenameArchive), new FakeProbe()));
    }
}
