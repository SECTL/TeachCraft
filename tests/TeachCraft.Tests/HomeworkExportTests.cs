using System.Security.Cryptography;
using Homework.Core;
using Homework.Core.Export;
using Homework.Core.Models;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace TeachCraft.Tests;

/// <summary>
/// 合成夹具：2 个班 / 3 名学生 / 2 张 PNG / 1 份两页 PDF。
/// 姓名全为虚构（苏晚晴 / 陈屿 / 许砚），所有文件落在临时目录，用完即删。
/// </summary>
internal sealed class HomeworkExportFixture : IDisposable
{
    public string Root { get; }
    public string OutputDir => Path.Combine(Root, "out");
    public string SourceDir => Path.Combine(Root, "src");

    public HomeworkBatch Batch { get; }
    public HomeworkClass Class1 { get; }
    public HomeworkClass Class2 { get; }
    public HomeworkStudent S1 { get; }
    public HomeworkStudent S2 { get; }
    public HomeworkStudent S3 { get; }

    public string Png1 { get; }
    public string Png2 { get; }
    public string Pdf { get; }

    public HomeworkPage Page1 { get; } // 图片 -> S1（index 0）
    public HomeworkPage Page2 { get; } // 图片 -> S2（index 1）
    public HomeworkPage Page3 { get; } // PDF 第 1 页 -> S1（index 2）
    public HomeworkPage Page4 { get; } // PDF 第 2 页 -> S3（index 3）

    public HomeworkExportFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "TeachCraft", "Homework", "export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SourceDir);

        Png1 = MakePng("a.png", 120, 80, new SKColor(200, 60, 60));
        Png2 = MakePng("b.png", 60, 90, new SKColor(60, 120, 200));
        Pdf = MakePdf("two.pdf", 2);

        Batch = HomeworkBatch.Create("第一次作业");
        Class1 = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        Class2 = new HomeworkClass(Guid.NewGuid(), "八年级2班");
        S1 = new HomeworkStudent(Guid.NewGuid(), Class1.Id, "01", "苏晚晴");
        S2 = new HomeworkStudent(Guid.NewGuid(), Class1.Id, "02", "陈屿");
        S3 = new HomeworkStudent(Guid.NewGuid(), Class2.Id, "03", "许砚");
        Batch.LoadRoster([Class1, Class2], [S1, S2, S3]);

        Page1 = Add(Png1, HomeworkPageKind.Image, 1, 120, 80);
        Page2 = Add(Png2, HomeworkPageKind.Image, 1, 60, 90);
        Page3 = Add(Pdf, HomeworkPageKind.PdfPage, 1, 595, 842);
        Page4 = Add(Pdf, HomeworkPageKind.PdfPage, 2, 595, 842);

        Batch.Match(Page1.Id, S1.Id);
        Batch.Match(Page2.Id, S2.Id);
        Batch.Match(Page3.Id, S1.Id);
        Batch.Match(Page4.Id, S3.Id);
    }

    private HomeworkPage Add(string path, HomeworkPageKind kind, int page, int w, int h)
    {
        Assert.True(Batch.TryAddPage(path, kind, page, "h" + Guid.NewGuid().ToString("N"), w, h,
            out var added, out var error), error);
        return added!;
    }

    private string MakePng(string name, int w, int h, SKColor color)
    {
        var path = Path.Combine(SourceDir, name);
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private string MakePdf(string name, int pages)
    {
        var path = Path.Combine(SourceDir, name);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Size = PageSize.A4;
            using var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(XBrushes.LightGray, 40, 40, 160, 80);
        }
        doc.Save(path);
        return path;
    }

    public HomeworkExportPlan Plan(HomeworkExportMode mode, bool overwrite = false) =>
        HomeworkExportPlanner.Build(
            Batch, new HomeworkExportOptions(OutputDir, mode, overwrite), new RealProbe());

    public HomeworkExportPlan PlanFor(HomeworkBatch batch, HomeworkExportMode mode) =>
        HomeworkExportPlanner.Build(
            batch, new HomeworkExportOptions(OutputDir, mode), new RealProbe());

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private sealed class RealProbe : IFileProbe
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
    }
}

internal sealed class SyncProgress : IProgress<int>
{
    private readonly Action<int> _action;
    public SyncProgress(Action<int> action) => _action = action;
    public void Report(int value) => _action(value);
}

/// <summary>导出执行器：落盘路径、字节一致、PDF 提取 / 合并、守卫、取消与进度。</summary>
public sealed class HomeworkExportTests
{
    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ----------------------------- A: 逐页重命名归档 -----------------------------

    [Fact]
    public void RenameArchive_WritesExpectedTargets_ImageBytesIdentical_PdfPageExtracted()
    {
        using var f = new HomeworkExportFixture();
        var plan = f.Plan(HomeworkExportMode.RenameArchive);
        Assert.Empty(plan.Conflicts);

        var result = HomeworkExporter.Execute(
            f.Batch, new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.RenameArchive), plan);

        var p1 = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png");
        var p2 = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_02_陈屿_第1页.png");
        var p3 = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第2页.pdf");
        var p4 = Path.Combine(f.OutputDir, "八年级2班", "八年级2班_03_许砚_第1页.pdf");
        Assert.True(File.Exists(p1));
        Assert.True(File.Exists(p2));
        Assert.True(File.Exists(p3));
        Assert.True(File.Exists(p4));

        // 图片字节原样复制。
        Assert.Equal(Sha256(f.Png1), Sha256(p1));
        Assert.Equal(Sha256(f.Png2), Sha256(p2));

        // 提取出的 PDF 只有一页，页面尺寸与来源一致。
        using var src = PdfReader.Open(f.Pdf, PdfDocumentOpenMode.Import);
        using var out1 = PdfReader.Open(p3, PdfDocumentOpenMode.Import);
        Assert.Single(out1.Pages);
        Assert.Equal(src.Pages[0].Width.Point, out1.Pages[0].Width.Point, 1);
        Assert.Equal(src.Pages[0].Height.Point, out1.Pages[0].Height.Point, 1);

        Assert.Equal(4, result.FilesWritten);
        Assert.Equal(3, result.StudentsWritten);
        Assert.Null(result.IndexPath);
    }

    // ----------------------------- B: 合并 PDF -----------------------------

    [Fact]
    public void MergePdf_MultipleImagePages_KeepsEveryPage()
    {
        using var f = new HomeworkExportFixture();
        var batch = HomeworkBatch.Create("合并多图");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var student = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        batch.LoadRoster([cls], [student]);
        Assert.True(batch.TryAddPage(f.Png1, HomeworkPageKind.Image, 1, "x1", 120, 80, out var first, out var e1), e1);
        Assert.True(batch.TryAddPage(f.Png2, HomeworkPageKind.Image, 1, "x2", 60, 90, out var second, out var e2), e2);
        batch.Match(first!.Id, student.Id);
        batch.Match(second!.Id, student.Id);

        var plan = f.PlanFor(batch, HomeworkExportMode.MergePdf);
        Assert.Empty(plan.Conflicts);
        HomeworkExporter.Execute(batch, new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.MergePdf), plan);

        var target = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴.pdf");
        using var pdf = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(120, pdf.Pages[0].Width.Point, 0);
        Assert.Equal(80, pdf.Pages[0].Height.Point, 0);
        Assert.Equal(60, pdf.Pages[1].Width.Point, 0);
        Assert.Equal(90, pdf.Pages[1].Height.Point, 0);
    }

    [Fact]
    public void MergePdf_OnePdfPerStudent_PageCountAndOrderByIndex()
    {
        using var f = new HomeworkExportFixture();
        var plan = f.Plan(HomeworkExportMode.MergePdf);
        Assert.Empty(plan.Conflicts);

        var result = HomeworkExporter.Execute(
            f.Batch, new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.MergePdf), plan);

        var s1 = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴.pdf");
        var s2 = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_02_陈屿.pdf");
        var s3 = Path.Combine(f.OutputDir, "八年级2班", "八年级2班_03_许砚.pdf");
        Assert.True(File.Exists(s1));
        Assert.True(File.Exists(s2));
        Assert.True(File.Exists(s3));

        using var d1 = PdfReader.Open(s1, PdfDocumentOpenMode.Import);
        Assert.Equal(2, d1.PageCount);
        // 第一页是图片页：尺寸按像素点数（120×80，横向），图片页确实存在。
        Assert.Equal(120, d1.Pages[0].Width.Point, 0);
        Assert.Equal(80, d1.Pages[0].Height.Point, 0);
        // 第二页是导入的 PDF 页（A4），顺序按 Index。
        Assert.True(d1.Pages[1].Width.Point > 500);
        Assert.True(d1.Pages[1].Height.Point > 800);

        using var d2 = PdfReader.Open(s2, PdfDocumentOpenMode.Import);
        Assert.Single(d2.Pages);
        Assert.Equal(60, d2.Pages[0].Width.Point, 0);
        Assert.Equal(90, d2.Pages[0].Height.Point, 0);

        using var d3 = PdfReader.Open(s3, PdfDocumentOpenMode.Import);
        Assert.Single(d3.Pages);

        Assert.Equal(3, result.FilesWritten);
        Assert.Equal(3, result.StudentsWritten);
    }

    // ----------------------------- D: 按文件夹复制与同名 -----------------------------

    [Fact]
    public void CopyToFolders_SameNamedSources_GetSpaceTwoSuffixAndWarning()
    {
        using var f = new HomeworkExportFixture();
        var d1 = Path.Combine(f.SourceDir, "d1");
        var d2 = Path.Combine(f.SourceDir, "d2");
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        var s1 = Path.Combine(d1, "scan.png");
        var s2 = Path.Combine(d2, "scan.png");
        File.WriteAllBytes(s1, [1, 2, 3, 4]);
        File.WriteAllBytes(s2, [5, 6, 7, 8, 9]);
        Assert.True(f.Batch.TryAddPage(s1, HomeworkPageKind.Image, 1, "x1", 10, 10, out var p1, out _));
        Assert.True(f.Batch.TryAddPage(s2, HomeworkPageKind.Image, 1, "x2", 10, 10, out var p2, out _));
        f.Batch.Match(p1!.Id, f.S1.Id);
        f.Batch.Match(p2!.Id, f.S1.Id);

        var plan = f.Plan(HomeworkExportMode.CopyToFolders);
        Assert.Empty(plan.Conflicts);
        var result = HomeworkExporter.Execute(
            f.Batch, new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.CopyToFolders), plan);

        var folder = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴");
        var first = Path.Combine(folder, "scan.png");
        var second = Path.Combine(folder, "scan (2).png");
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(Sha256(s1), Sha256(first));
        Assert.Equal(Sha256(s2), Sha256(second));
        Assert.Contains(result.Warnings, w => w.Contains("scan"));
    }

    // ----------------------------- 冲突与覆盖 -----------------------------

    [Fact]
    public void Conflicts_ThrowsAndWritesNothing()
    {
        using var f = new HomeworkExportFixture();
        var target = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "OLD");
        var plan = f.Plan(HomeworkExportMode.RenameArchive);
        Assert.NotEmpty(plan.Conflicts);

        var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        var ex = Assert.Throws<HomeworkException>(() => HomeworkExporter.Execute(
            f.Batch, new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.RenameArchive), plan));
        Assert.Contains("请先处理冲突后再导出。", ex.Message);
        var after = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public void ExistingTarget_WithoutOverwrite_Throws_WithOverwrite_Replaces()
    {
        using var f = new HomeworkExportFixture();
        var target = Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "OLD");

        // 手工构造无冲突计划，专门验证执行器自己的「目标已存在」守卫。
        var entry = new HomeworkExportEntry(
            HomeworkExportEntryKind.ImageCopy, f.Page1.Id, f.S1.Id, f.Png1, 1, target);
        var plan = new HomeworkExportPlan([entry], [], [], [], null, 1, 1);

        var strict = new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.RenameArchive, false);
        Assert.Throws<HomeworkException>(() => HomeworkExporter.Execute(f.Batch, strict, plan));
        Assert.Equal("OLD", File.ReadAllText(target));

        var lenient = new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.RenameArchive, true);
        var result = HomeworkExporter.Execute(f.Batch, lenient, plan);
        Assert.Equal(Sha256(f.Png1), Sha256(target));
        Assert.Equal(1, result.FilesWritten);
    }

    [Fact]
    public void SourceAsTarget_Throws_SourceUnchanged()
    {
        using var f = new HomeworkExportFixture();
        var before = Sha256(f.Png1);
        var entry = new HomeworkExportEntry(
            HomeworkExportEntryKind.ImageCopy, f.Page1.Id, f.S1.Id, f.Png1, 1, f.Png1);
        var plan = new HomeworkExportPlan([entry], [], [], [], null, 1, 1);
        var options = new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.RenameArchive, true);

        var ex = Assert.Throws<HomeworkException>(() => HomeworkExporter.Execute(f.Batch, options, plan));
        Assert.Contains("来源", ex.Message);
        Assert.Equal(before, Sha256(f.Png1));
    }

    // ----------------------------- 取消与进度 -----------------------------

    [Fact]
    public void CancelAfterFirstEntry_DeletesOnlyFilesCreatedThisRun()
    {
        using var f = new HomeworkExportFixture();
        var plan = f.Plan(HomeworkExportMode.RenameArchive);
        Assert.Equal(4, plan.Entries.Count);
        var sourceHashes = new[] { f.Png1, f.Png2, f.Pdf }.ToDictionary(p => p, Sha256);

        var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p >= 25) cts.Cancel(); });
        var options = new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.RenameArchive);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HomeworkExporter.Execute(f.Batch, options, plan, progress, cts.Token));

        Assert.Empty(Directory.GetFiles(f.OutputDir, "*", SearchOption.AllDirectories));
        Assert.All(sourceHashes, kv => Assert.Equal(kv.Value, Sha256(kv.Key)));
    }

    [Fact]
    public void Progress_Reaches100_AndIndexWritten()
    {
        using var f = new HomeworkExportFixture();
        var plan = f.Plan(HomeworkExportMode.ArchiveWithIndex);
        var values = new List<int>();
        var progress = new SyncProgress(values.Add);
        var options = new HomeworkExportOptions(f.OutputDir, HomeworkExportMode.ArchiveWithIndex);

        var result = HomeworkExporter.Execute(f.Batch, options, plan, progress);

        Assert.Contains(100, values);
        Assert.Equal(5, result.FilesWritten); // 4 条页条目 + 1 份索引
        Assert.NotNull(result.IndexPath);
        Assert.True(File.Exists(result.IndexPath!));
    }

    [Fact]
    public void AtomicWrite_OnMidWriteFailure_LeavesNoTempFiles_AndPreservesExistingTargets()
    {
        // 场景 A：目标文件尚不存在时中途失败 -> 目标不留半成品、不留 .tmp-* 文件。
        using (var f1 = new HomeworkExportFixture())
        {
            var plan = f1.Plan(HomeworkExportMode.MergePdf);
            var options = new HomeworkExportOptions(f1.OutputDir, HomeworkExportMode.MergePdf, OverwriteExisting: true);

            Assert.Throws<IOException>(() =>
                HomeworkExporter.ExecuteWithOverrideForTest(f1.Batch, options, plan, (_, tempPath) =>
                {
                    File.WriteAllBytes(tempPath, [0x25, 0x50, 0x44, 0x46, 0x00]); // 半截 PDF 字节
                    throw new IOException("模拟写入中断");
                }));

            var leftovers = Directory.GetFiles(f1.OutputDir, "*", SearchOption.AllDirectories);
            Assert.Empty(leftovers);
        }

        // 场景 B：目标文件已存在且允许覆盖时中途失败 -> 原字节完整保留、无 .tmp-* 残留。
        using (var f2 = new HomeworkExportFixture())
        {
            var plan = f2.Plan(HomeworkExportMode.MergePdf, overwrite: true);
            var firstTarget = plan.Entries[0].TargetPath;
            Directory.CreateDirectory(Path.GetDirectoryName(firstTarget)!);
            var sentinel = "EXISTING-INTACT-PDF-BYTES"u8.ToArray();
            File.WriteAllBytes(firstTarget, sentinel);

            var options = new HomeworkExportOptions(f2.OutputDir, HomeworkExportMode.MergePdf, OverwriteExisting: true);
            Assert.Throws<IOException>(() =>
                HomeworkExporter.ExecuteWithOverrideForTest(f2.Batch, options, plan, (_, tempPath) =>
                {
                    File.WriteAllBytes(tempPath, "PARTIAL-CORRUPT"u8.ToArray());
                    throw new IOException("模拟覆盖写中断");
                }));

            Assert.Equal(sentinel, File.ReadAllBytes(firstTarget));
            Assert.Empty(Directory.GetFiles(f2.OutputDir, "*.tmp-*", SearchOption.AllDirectories));
        }

        // 场景 C：归档含索引且不允许覆盖时，若索引文件已先存在，抛 HomeworkException、原索引不被破坏且无临时文件残留。
        using (var f3 = new HomeworkExportFixture())
        {
            var plan = f3.Plan(HomeworkExportMode.ArchiveWithIndex, overwrite: false);
            var indexTarget = plan.IndexPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(indexTarget)!);
            var sentinelIndex = "EXISTING-INDEX-WORKBOOK"u8.ToArray();
            File.WriteAllBytes(indexTarget, sentinelIndex);

            var options = new HomeworkExportOptions(f3.OutputDir, HomeworkExportMode.ArchiveWithIndex, OverwriteExisting: false);
            Assert.Throws<HomeworkException>(() => HomeworkExporter.Execute(f3.Batch, options, plan));

            Assert.Equal(sentinelIndex, File.ReadAllBytes(indexTarget));
            Assert.Empty(Directory.GetFiles(f3.OutputDir, "*.tmp-*", SearchOption.AllDirectories));
        }
    }
}
