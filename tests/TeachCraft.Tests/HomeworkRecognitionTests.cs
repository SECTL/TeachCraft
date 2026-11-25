using Homework.App;
using Homework.Core.Models;
using Homework.Core.Pages;
using Homework.Core.Recognition;
using Homework.Core.Roster;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace TeachCraft.Tests;

/// <summary>
/// 填涂识别编排：全部注入假 <see cref="IFillInEngine"/>，页来源用 SkiaSharp 合成 PNG
/// 与 PdfSharp 合成 PDF，不依赖真实扫描件、OCR 或网络。全部使用合成姓名。
/// </summary>
public sealed class HomeworkRecognitionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "TeachCraft", "Homework", "recognition", Guid.NewGuid().ToString("N"));

    public HomeworkRecognitionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ----------------------------- 假引擎与合成文件 -----------------------------

    private sealed class FakeEngine : IFillInEngine
    {
        private readonly Queue<FillInDetectionResult> _results;

        public FakeEngine(params FillInDetectionResult[] results) =>
            _results = new Queue<FillInDetectionResult>(results);

        public int CallCount { get; private set; }

        public FillInDetectionResult Detect(SKBitmap bitmap, NormalizedRect region, int columns)
        {
            CallCount++;
            return _results.Count > 0 ? _results.Dequeue() : Ok("000");
        }

        public static FillInDetectionResult Ok(string digits) =>
            new(digits.Select(c => c - '0').ToArray(),
                Enumerable.Repeat(0.5d, digits.Length).ToArray(), null);

        public static FillInDetectionResult Fail(string reason) =>
            new([0], [0d], reason);
    }

    /// <summary>在第 N 次 Detect 时取消令牌，用于验证「先算后写」的取消语义。</summary>
    private sealed class CancelAfterEngine : IFillInEngine
    {
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfter;

        public CancelAfterEngine(CancellationTokenSource cts, int cancelAfter)
        {
            _cts = cts;
            _cancelAfter = cancelAfter;
        }

        public int CallCount { get; private set; }

        public FillInDetectionResult Detect(SKBitmap bitmap, NormalizedRect region, int columns)
        {
            CallCount++;
            if (CallCount >= _cancelAfter) _cts.Cancel();
            return FakeEngine.Ok("001");
        }
    }

    private sealed class ProgressCollector : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }

    private string NewPng(string name, int width = 120, int height = 160)
    {
        var path = Path.Combine(_root, name + ".png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.White);
        using (var stream = File.Create(path))
            Assert.True(bitmap.Encode(stream, SKEncodedImageFormat.Png, 100));
        return path;
    }

    private string MakePdf(string name, int pages)
    {
        var path = Path.Combine(_root, name);
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.LightGray, 40, 40, 160, 80);
        }
        document.Save(path);
        return path;
    }

    private static HomeworkPage Add(HomeworkBatch batch, string path)
    {
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.Image, 1, "h" + Guid.NewGuid().ToString("N"),
            120, 160, out var page, out var error), error);
        return page!;
    }

    private static (HomeworkBatch Batch, HomeworkStudent A, HomeworkStudent B) NewBatch(
        string numberA = "001", string numberB = "002")
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, numberA, "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), cls.Id, numberB, "陈屿");
        batch.LoadRoster([cls], [a, b]);
        batch.SetFillInRegion(new NormalizedRect(0, 0, 1, 1));
        return (batch, a, b);
    }

    // ----------------------------- 唯一匹配与待处理 -----------------------------

    [Fact]
    public async Task ThreePages_TwoMatched_OneNoMatch()
    {
        var (batch, a, b) = NewBatch();
        var p1 = Add(batch, NewPng("a"));
        var p2 = Add(batch, NewPng("b"));
        var p3 = Add(batch, NewPng("c"));
        var engine = new FakeEngine(FakeEngine.Ok("001"), FakeEngine.Ok("002"), FakeEngine.Ok("999"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Equal(a.Id, p1.StudentId);
        Assert.Equal(b.Id, p2.StudentId);
        Assert.Null(p3.StudentId);
        Assert.Contains("没有学号为 999 的学生", p3.Recognition!.MatchFailureReason!);
        Assert.Equal(3, engine.CallCount);
        Assert.Contains("2 页匹配", vm.StatusText);
        Assert.Contains("1 页待处理", vm.StatusText);
    }

    [Fact]
    public async Task CrossClassDuplicate_PendingWithBothClassNames()
    {
        var batch = HomeworkBatch.Create("批次1");
        var c1 = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var c2 = new HomeworkClass(Guid.NewGuid(), "八年级2班");
        var a = new HomeworkStudent(Guid.NewGuid(), c1.Id, "001", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), c2.Id, "001", "陈屿");
        batch.LoadRoster([c1, c2], [a, b]);
        batch.SetFillInRegion(new NormalizedRect(0, 0, 1, 1));
        var page = Add(batch, NewPng("a"));
        var engine = new FakeEngine(FakeEngine.Ok("001"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Null(page.StudentId);
        var reason = page.Recognition!.MatchFailureReason!;
        Assert.Contains("八年级1班", reason);
        Assert.Contains("八年级2班", reason);
    }

    [Fact]
    public async Task ExpectedOnePage_SecondPageToSameStudent_PendingAlreadyComplete()
    {
        var (batch, a, _) = NewBatch();
        var p1 = Add(batch, NewPng("a"));
        var p2 = Add(batch, NewPng("b"));
        var engine = new FakeEngine(FakeEngine.Ok("001"), FakeEngine.Ok("001"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Equal(a.Id, p1.StudentId);
        Assert.Null(p2.StudentId);
        Assert.Contains("该生已交齐 1 页", p2.Recognition!.MatchFailureReason!);
    }

    [Fact]
    public async Task PreMatchedPage_IsPreservedAndNotRecognized()
    {
        var (batch, a, _) = NewBatch();
        var p1 = Add(batch, NewPng("a"));
        var p2 = Add(batch, NewPng("b"));
        batch.Match(p1.Id, a.Id);
        var engine = new FakeEngine(FakeEngine.Ok("002"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Equal(a.Id, p1.StudentId);
        Assert.Null(p1.Recognition);
        Assert.Equal(1, engine.CallCount);
        Assert.NotNull(p2.Recognition);
    }

    [Fact]
    public async Task EmptyNumberStudent_IsNeverCandidate_PagePendingNoMatch()
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var empty = new HomeworkStudent(Guid.NewGuid(), cls.Id, string.Empty, "空号学生");
        batch.LoadRoster([cls], [empty]);
        batch.SetFillInRegion(new NormalizedRect(0, 0, 1, 1));
        var page = Add(batch, NewPng("a"));
        var engine = new FakeEngine(FakeEngine.Ok("001"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Null(page.StudentId);
        Assert.Contains("没有学号为 001 的学生", page.Recognition!.MatchFailureReason!);
        Assert.Empty(batch.PagesOf(empty.Id));
    }

    [Fact]
    public async Task NonDigitNumberStudent_IsExcluded_PagePendingNoMatch()
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var alpha = new HomeworkStudent(Guid.NewGuid(), cls.Id, "A01", "字母学号学生");
        batch.LoadRoster([cls], [alpha]);
        batch.SetFillInRegion(new NormalizedRect(0, 0, 1, 1));
        var page = Add(batch, NewPng("a"));
        var engine = new FakeEngine(FakeEngine.Ok("001"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Null(page.StudentId);
        Assert.Contains("没有学号为 001 的学生", page.Recognition!.MatchFailureReason!);
        Assert.Empty(batch.PagesOf(alpha.Id));
    }

    [Fact]
    public async Task LeadingZeroDigits_MatchStudentByNumericFallback()
    {
        var (batch, a, _) = NewBatch(numberA: "001");
        var page = Add(batch, NewPng("a"));
        var engine = new FakeEngine(FakeEngine.Ok("1"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Equal(a.Id, page.StudentId);
        Assert.Equal("1", page.Recognition!.Digits);
        Assert.Null(page.Recognition.MatchFailureReason);
    }

    // ----------------------------- 取消与撤销 -----------------------------

    [Fact]
    public async Task CancelBeforeApply_NoChanges()
    {
        var (batch, _, _) = NewBatch();
        Add(batch, NewPng("a"));
        Add(batch, NewPng("b"));
        var engine = new FakeEngine(FakeEngine.Ok("001"), FakeEngine.Ok("002"));
        var vm = new BatchWorkspaceViewModel(batch, engine);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await vm.RunFillInRecognitionAsync(null, cts.Token);

        Assert.All(batch.Pages, p =>
        {
            Assert.Null(p.StudentId);
            Assert.Null(p.Recognition);
        });
        Assert.Contains("已取消", vm.StatusText);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task FortyPages_CancelMidRun_NoWritesButProgressReported()
    {
        var (batch, _, _) = NewBatch();
        for (var i = 0; i < 40; i++) Add(batch, NewPng("page" + i));
        using var cts = new CancellationTokenSource();
        var engine = new CancelAfterEngine(cts, cancelAfter: 5);
        var progress = new ProgressCollector();
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync(progress, cts.Token);

        Assert.Equal(40, batch.Pages.Count);
        Assert.NotEmpty(progress.Values);
        Assert.All(batch.Pages, p =>
        {
            Assert.Null(p.StudentId);
            Assert.Null(p.Recognition);
        });
        Assert.Contains("已取消", vm.StatusText);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task OneUndo_RestoresMatchesAndRecognitions()
    {
        var (batch, a, b) = NewBatch();
        var p1 = Add(batch, NewPng("a"));
        var p2 = Add(batch, NewPng("b"));
        var engine = new FakeEngine(FakeEngine.Ok("001"), FakeEngine.Ok("002"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Equal(a.Id, p1.StudentId);
        Assert.Equal(b.Id, p2.StudentId);
        Assert.NotNull(p1.Recognition);
        Assert.NotNull(p2.Recognition);
        Assert.True(vm.CanUndo);
        Assert.Equal("识别填涂", vm.UndoLabel);

        vm.Undo();

        Assert.All(batch.Pages, p =>
        {
            Assert.Null(p.StudentId);
            Assert.Null(p.Recognition);
        });
    }

    [Fact]
    public async Task RosterReloadAfterRecognition_ClearsAllRecognitionsAndMatches()
    {
        var (batch, _, _) = NewBatch();
        var p1 = Add(batch, NewPng("a"));
        var p2 = Add(batch, NewPng("b"));
        var engine = new FakeEngine(FakeEngine.Ok("001"), FakeEngine.Ok("002"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.NotNull(p1.Recognition);
        Assert.NotNull(p2.Recognition);
        Assert.NotNull(p1.StudentId);
        Assert.NotNull(p2.StudentId);

        var newCls = new HomeworkClass(Guid.NewGuid(), "八年级3班");
        var newA = new HomeworkStudent(Guid.NewGuid(), newCls.Id, "001", "林知夏");
        var newB = new HomeworkStudent(Guid.NewGuid(), newCls.Id, "002", "周予安");
        vm.ApplyRoster(new RosterImportResult([newCls], [newA, newB], [], 0));

        Assert.All(batch.Pages, p =>
        {
            Assert.Null(p.Recognition);
            Assert.Null(p.StudentId);
        });
        Assert.Contains("请重新识别", vm.StatusText);
    }

    // ----------------------------- 失败原因 -----------------------------

    [Fact]
    public async Task DetectionFailure_PendingWithDetectorReason()
    {
        var (batch, _, _) = NewBatch();
        var page = Add(batch, NewPng("a"));
        var engine = new FakeEngine(FakeEngine.Fail("第 2 列涂了多格"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Null(page.StudentId);
        Assert.Contains("第 2 列涂了多格", page.Recognition!.FailureReason!);
        Assert.Null(page.Recognition.MatchFailureReason);
    }

    [Fact]
    public async Task NoRegion_PendingWithMessage()
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "001", "苏晚晴");
        batch.LoadRoster([cls], [a]);
        // 故意不设置 FillInRegion。
        var page = Add(batch, NewPng("a"));
        var engine = new FakeEngine(FakeEngine.Ok("001"));
        var vm = new BatchWorkspaceViewModel(batch, engine);

        await vm.RunFillInRecognitionAsync();

        Assert.Null(page.StudentId);
        Assert.Contains("未设置填涂区", page.Recognition!.FailureReason!);
        Assert.Equal(0, engine.CallCount);
    }

    // ----------------------------- 解码 -----------------------------

    [Fact]
    public void DecodePage_ImageAndPdf_CapLongEdgeAndEmptyPdfHash()
    {
        var batch = HomeworkBatch.Create("解码");

        var png = NewPng("big", 3200, 1600);
        Assert.True(batch.TryAddPage(png, HomeworkPageKind.Image, 1, "h", 3200, 1600,
            out var imagePage, out var e1), e1);
        using (var decoded = PageImageDecoder.DecodePage(imagePage!, 2800, 200))
        {
            Assert.True(decoded.Width > 0 && decoded.Height > 0);
            Assert.True(Math.Max(decoded.Width, decoded.Height) <= 2800);
            Assert.False(string.IsNullOrEmpty(decoded.ContentHash));
        }

        var pdf = MakePdf("one.pdf", 1);
        Assert.True(batch.TryAddPage(pdf, HomeworkPageKind.PdfPage, 1, string.Empty, 0, 0,
            out var pdfPage, out var e2), e2);
        using (var decoded = PageImageDecoder.DecodePage(pdfPage!, 2800, 200))
        {
            Assert.True(decoded.Width > 0 && decoded.Height > 0);
            Assert.True(Math.Max(decoded.Width, decoded.Height) <= 2800);
            Assert.Equal(string.Empty, decoded.ContentHash);
        }
    }
}
