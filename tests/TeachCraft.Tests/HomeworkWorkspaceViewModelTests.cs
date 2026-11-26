using Homework.App;
using Homework.Core.Models;
using Homework.Core.Recognition;

namespace TeachCraft.Tests;

/// <summary>
/// 辑册工作区视图模型。全部使用合成姓名（苏晚晴 / 陈屿 / 许砚），
/// 页来源用临时文件，测试结束即删除。
/// </summary>
public sealed class HomeworkWorkspaceViewModelTests : IDisposable
{
    private readonly string _root;

    public HomeworkWorkspaceViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hw-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string NewSource(string name)
    {
        var path = Path.Combine(_root, name + ".png");
        File.WriteAllText(path, "x");
        return path;
    }

    private static (HomeworkBatch Batch, HomeworkClass Cls, HomeworkStudent A, HomeworkStudent B) NewBatch()
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), cls.Id, "02", "陈屿");
        batch.LoadRoster([cls], [a, b]);
        return (batch, cls, a, b);
    }

    private static HomeworkPage Add(HomeworkBatch batch, string path)
    {
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.Image, 1, "h" + Guid.NewGuid().ToString("N"),
            100, 200, out var page, out var error), error);
        return page!;
    }

    private static HomeworkPage AddSized(HomeworkBatch batch, string path, int width, int height)
    {
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.Image, 1, "h" + Guid.NewGuid().ToString("N"),
            width, height, out var page, out var error), error);
        return page!;
    }

    // ----------------------------- 匹配与前进 -----------------------------

    [Fact]
    public void MatchStudent_MatchesSelectedPage_AndAdvancesToNextPending()
    {
        var (batch, _, a, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        Add(batch, NewSource("c"));
        var vm = new BatchWorkspaceViewModel(batch);

        vm.SelectPage(vm.VisiblePages[0]);
        vm.MatchStudent(vm.Students.Single(s => s.Id == a.Id));

        Assert.Equal(a.Id, p1.StudentId);
        Assert.Equal(p2.Id, vm.SelectedPage!.Id);
        Assert.Equal(2, vm.PendingCount);
        Assert.Equal(1, vm.MatchedCount);
    }

    [Fact]
    public void MatchStudent_WithoutSelectedPage_SurfacesStatus()
    {
        var (batch, _, a, _) = NewBatch();
        Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);

        vm.MatchStudent(vm.Students.Single(s => s.Id == a.Id));

        Assert.Contains("选择", vm.StatusText);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact]
    public void UnmatchSelected_ClearsMatch()
    {
        var (batch, _, a, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        batch.Match(p1.Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);

        vm.SelectPage(vm.MatchedPages[0]);
        vm.UnmatchSelected();

        Assert.Null(p1.StudentId);
        Assert.Equal(1, vm.PendingCount);
        Assert.Equal(0, vm.MatchedCount);
    }

    // ----------------------------- 删除 / 移动 / 撤销 -----------------------------

    [Fact]
    public void DeleteSelectedPage_UndoRestoresPageAtOriginalPosition()
    {
        var (batch, _, _, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        var vm = new BatchWorkspaceViewModel(batch);

        vm.SelectPage(vm.VisiblePages[0]);
        vm.DeleteSelectedPage();

        Assert.Single(batch.Pages);
        Assert.Equal(p2.Id, batch.Pages[0].Id);
        Assert.True(vm.CanUndo);
        Assert.Equal("删除页", vm.UndoLabel);

        vm.Undo();

        Assert.Equal(new[] { p1.Id, p2.Id }, batch.Pages.Select(p => p.Id));
        Assert.Equal(2, vm.VisiblePages.Count);
    }

    [Fact]
    public void MoveSelectedPage_ClampsAtEnds_AndUndo()
    {
        var (batch, _, _, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        var vm = new BatchWorkspaceViewModel(batch);

        vm.SelectPage(vm.VisiblePages[0]);
        vm.MoveSelectedPage(-5);
        Assert.False(vm.CanUndo);                       // 夹到 0，内容没变不入栈
        Assert.Equal(p1.Id, batch.Pages[0].Id);

        vm.MoveSelectedPage(1);
        Assert.Equal(new[] { p2.Id, p1.Id }, batch.Pages.Select(p => p.Id));
        Assert.True(vm.CanUndo);

        vm.Undo();
        Assert.Equal(new[] { p1.Id, p2.Id }, batch.Pages.Select(p => p.Id));
    }

    [Fact]
    public void Redo_IsClearedAfterNewEdit()
    {
        var (batch, _, a, _) = NewBatch();
        Add(batch, NewSource("a"));
        Add(batch, NewSource("b"));
        var vm = new BatchWorkspaceViewModel(batch);

        vm.SelectPage(vm.VisiblePages[0]);
        vm.MatchStudent(vm.Students.Single(s => s.Id == a.Id));
        Assert.True(vm.CanUndo);

        vm.Undo();
        Assert.True(vm.CanRedo);

        vm.SelectPage(vm.VisiblePages[0]);
        vm.MatchStudent(vm.Students.Single(s => s.Id == a.Id));

        Assert.False(vm.CanRedo);
        Assert.True(vm.CanUndo);
    }

    // ----------------------------- 页签与计数 -----------------------------

    [Fact]
    public void Tabs_AndCounts_ReflectPendingAndAllPages()
    {
        var (batch, _, a, _) = NewBatch();
        Add(batch, NewSource("a"));
        Add(batch, NewSource("b"));
        Add(batch, NewSource("c"));
        batch.Match(batch.Pages[0].Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);

        Assert.Equal(2, vm.PendingCount);
        Assert.Equal(1, vm.MatchedCount);
        Assert.Equal(2, vm.VisiblePages.Count);
        Assert.Equal("待处理 共2条", vm.LeftHeader);
        Assert.Contains("共 3 张", vm.Title);

        vm.ShowAllPages = true;
        Assert.Equal(3, vm.VisiblePages.Count);
    }

    // ----------------------------- 班级 / 搜索 / 未匹配 -----------------------------

    [Fact]
    public void ClassFilter_ShowsOnlyThatClass()
    {
        var batch = HomeworkBatch.Create("批次1");
        var c1 = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var c2 = new HomeworkClass(Guid.NewGuid(), "八年级2班");
        var a = new HomeworkStudent(Guid.NewGuid(), c1.Id, "01", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), c2.Id, "02", "陈屿");
        batch.LoadRoster([c1, c2], [a, b]);
        var vm = new BatchWorkspaceViewModel(batch);

        Assert.Equal(3, vm.ClassFilters.Count);
        Assert.Equal("全部班级", vm.ClassFilters[0].DisplayName);

        vm.SelectedClassFilter = vm.ClassFilters.Single(f => f.ClassId == c1.Id);
        Assert.Single(vm.Students);
        Assert.Equal(a.Id, vm.Students[0].Id);

        vm.SelectedClassFilter = vm.ClassFilters[0];
        Assert.Equal(2, vm.Students.Count);
    }

    [Fact]
    public void Search_MatchesNameNumberAndClass_CaseInsensitive()
    {
        var (batch, _, a, b) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);

        vm.SearchText = "陈";
        Assert.Single(vm.Students);
        Assert.Equal(b.Id, vm.Students[0].Id);

        vm.SearchText = "01";
        Assert.Single(vm.Students);
        Assert.Equal(a.Id, vm.Students[0].Id);

        vm.SearchText = "八年级";
        Assert.Equal(2, vm.Students.Count);

        vm.SearchText = "AMY";
        Assert.Empty(vm.Students);
    }

    [Fact]
    public void ShowOnlyUnmatchedStudents_AndBadges()
    {
        var (batch, _, a, b) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        batch.Match(p1.Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.ShowAllStudents = true;

        Assert.Equal(1, vm.StudentsWithoutPagesCount);
        Assert.Equal("已完成", vm.Students.Single(s => s.Id == a.Id).BadgeText);
        Assert.Equal("未匹配", vm.Students.Single(s => s.Id == b.Id).BadgeText);

        vm.ShowOnlyUnmatchedStudents = true;
        Assert.Single(vm.Students);
        Assert.Equal(b.Id, vm.Students[0].Id);
    }

    [Fact]
    public void ExpectedPages_ClassifiesStudentsAndFiltersMatchedTab()
    {
        var (batch, _, a, b) = NewBatch();
        batch.SetExpectedPages(2);
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        var p3 = Add(batch, NewSource("c"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, a.Id);
        batch.Match(p3.Id, b.Id);
        var vm = new BatchWorkspaceViewModel(batch);

        Assert.Equal(new[] { b.Id }, vm.Students.Select(s => s.Id).ToArray());

        vm.ShowAllStudents = true;
        var rowA = vm.Students.Single(s => s.Id == a.Id);
        var rowB = vm.Students.Single(s => s.Id == b.Id);
        Assert.True(rowA.IsComplete);
        Assert.Equal("已完成", rowA.BadgeText);
        Assert.False(rowB.IsComplete);
        Assert.Equal(1, rowB.MissingPages);
        Assert.Equal("缺 1 页", rowB.BadgeText);
        Assert.Equal(1, vm.IncompleteStudentsCount);
        Assert.Equal(1, vm.MissingPageCount);

        vm.ShowOnlyMatchedStudents = true;
        Assert.Equal(new[] { a.Id }, vm.Students.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void MatchStudent_OnCompleteStudent_DoesNotMatch()
    {
        var (batch, _, a, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        batch.Match(p1.Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.ShowAllPages = true;
        vm.ShowAllStudents = true;
        vm.SelectPage(vm.VisiblePages.Single(p => p.Id == p2.Id));

        vm.MatchStudent(vm.Students.Single(s => s.Id == a.Id));

        Assert.Null(batch.Pages.Single(p => p.Id == p2.Id).StudentId);
        Assert.Equal(a.Id, vm.SelectedStudent?.Id);
    }

    [Fact]
    public void UnmatchPage_ReturnsStudentToUnmatched_AndUndoRestores()
    {
        var (batch, _, a, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        batch.Match(p1.Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.ShowAllPages = true;

        vm.UnmatchPage(vm.VisiblePages.Single(p => p.Id == p1.Id));

        Assert.Null(batch.Pages.Single(p => p.Id == p1.Id).StudentId);
        vm.ShowOnlyUnmatchedStudents = true;
        Assert.Contains(vm.Students, s => s.Id == a.Id);
        Assert.True(vm.CanUndo);

        vm.Undo();
        Assert.Equal(a.Id, batch.Pages.Single(p => p.Id == p1.Id).StudentId);
    }

    [Fact]
    public void UnmatchStudent_RemovesAllPages_AndUndoRestores()
    {
        var (batch, _, a, _) = NewBatch();
        batch.SetExpectedPages(2);
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.ShowAllStudents = true;

        vm.UnmatchStudent(vm.Students.Single(s => s.Id == a.Id));

        Assert.All(batch.PagesOf(a.Id), page => Assert.Null(page.StudentId));
        Assert.True(vm.CanUndo);
        vm.Undo();
        Assert.Equal(2, batch.PagesOf(a.Id).Count);
    }

    // ----------------------------- 提交门槛 / 放弃 -----------------------------

    [Fact]
    public void CommitGate_Reasons()
    {
        var (batch, _, a, _) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);
        Assert.False(vm.CanCommit);
        Assert.Equal("还没有加入任何页。", vm.CommitBlockedReason);

        var p1 = Add(batch, NewSource("a"));
        vm = new BatchWorkspaceViewModel(batch);
        Assert.False(vm.CanCommit);
        Assert.Equal("还有 1 张未匹配，请匹配或删除后再提交。", vm.CommitBlockedReason);

        batch.Match(p1.Id, a.Id);
        vm = new BatchWorkspaceViewModel(batch);
        Assert.True(vm.CanCommit);
        Assert.Equal(string.Empty, vm.CommitBlockedReason);
    }

    [Fact]
    public void Abandon_RaisesEvent_AndClearsWorkspace()
    {
        var (batch, _, _, _) = NewBatch();
        Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        var raised = 0;
        vm.AbandonRequested += (_, _) => raised++;

        vm.Abandon();

        Assert.Equal(1, raised);
        Assert.Equal(HomeworkBatchState.Abandoned, batch.State);
        Assert.Empty(vm.VisiblePages);
        Assert.Empty(vm.Students);
    }

    [Fact]
    public void VisiblePages_OnePage_ShowsExactlyOneRow()
    {
        var (batch, _, _, _) = NewBatch();
        Add(batch, NewSource("only"));
        var vm = new BatchWorkspaceViewModel(batch);

        Assert.Single(vm.VisiblePages);
        Assert.Single(vm.VisiblePages.Select(page => page.Id).Distinct());
        Assert.Equal("未知1", vm.VisiblePages[0].Label);
    }

    // ----------------------------- 缩放与拼音 -----------------------------

    [Fact]
    public void ZoomMath_ClampStepFitAndFormat()
    {
        Assert.Equal(10d, ZoomMath.Clamp(1));
        Assert.Equal(400d, ZoomMath.Clamp(900));
        Assert.Equal(110d, ZoomMath.Step(100, 1));
        Assert.Equal(90d, ZoomMath.Step(100, -1));
        Assert.Equal(250d, ZoomMath.Step(201, 1));
        Assert.Equal(67d, ZoomMath.Step(52, 1));
        Assert.Equal(50d, ZoomMath.Step(52, -1));
        Assert.Equal(10d, ZoomMath.Fit(1000, 1000, 100, 100));
        Assert.Equal(100d, ZoomMath.Fit(0, 0, 100, 100));
        Assert.Equal("181%", ZoomMath.Format(180.6));
    }

    [Fact]
    public void WorkspaceZoom_ClampsAndFormats()
    {
        var (batch, _, _, _) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);

        vm.ZoomPercent = 999;
        Assert.Equal(400, vm.ZoomPercent);
        Assert.Equal("400%", vm.ZoomText);

        vm.ZoomIn();
        Assert.Equal(400, vm.ZoomPercent);
    }

    [Fact]
    public void ZoomIn_IncreasesPreviewDisplayWidth()
    {
        var (batch, _, _, _) = NewBatch();
        Add(batch, NewSource("a"));                       // 100 × 200
        var vm = new BatchWorkspaceViewModel(batch);
        vm.SelectPage(vm.VisiblePages[0]);

        Assert.Equal(100, vm.PreviewPixelWidth);
        Assert.Equal(200, vm.PreviewPixelHeight);
        var before = vm.PreviewDisplayWidth;
        Assert.Equal(100, before);

        vm.ZoomIn();

        Assert.Equal(110, vm.ZoomPercent);
        Assert.True(vm.PreviewDisplayWidth > before);
        Assert.Equal(110, vm.PreviewDisplayWidth);
    }

    [Fact]
    public void ZoomToFit_FitsViewport_AndPreservesAspectRatio()
    {
        var (batch, _, _, _) = NewBatch();
        Add(batch, NewSource("a"));                       // 100 × 200
        var vm = new BatchWorkspaceViewModel(batch);
        vm.SelectPage(vm.VisiblePages[0]);
        vm.PreviewViewportWidth = 400;
        vm.PreviewViewportHeight = 400;

        vm.ZoomToFit();

        // min(400/100, 400/200) = 2 → 200%
        Assert.Equal(200, vm.ZoomPercent);
        Assert.True(vm.PreviewDisplayWidth <= vm.PreviewViewportWidth);
        Assert.True(vm.PreviewDisplayHeight <= vm.PreviewViewportHeight);
        Assert.Equal(200, vm.PreviewDisplayWidth);
        Assert.Equal(400, vm.PreviewDisplayHeight);
        Assert.Equal(
            (double)vm.PreviewPixelWidth / vm.PreviewPixelHeight,
            vm.PreviewDisplayWidth / vm.PreviewDisplayHeight,
            5);

        // 视口过小 → 夹到 10%
        vm.PreviewViewportWidth = 10;
        vm.PreviewViewportHeight = 10;
        vm.ZoomToFit();
        Assert.Equal(10, vm.ZoomPercent);
    }

    [Fact]
    public void AutoFit_FirstWidePageWithSmallViewport_Fits()
    {
        var (batch, _, _, _) = NewBatch();
        AddSized(batch, NewSource("wide"), 1000, 500);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.PreviewViewportWidth = 800;
        vm.PreviewViewportHeight = 600;

        vm.SelectPage(vm.VisiblePages[0]);

        Assert.True(vm.PreviewDisplayWidth <= vm.PreviewViewportWidth);
        Assert.True(vm.ZoomPercent < 100);
    }

    [Fact]
    public void AutoFit_LargeScanFitsBelowFiftyPercent()
    {
        var (batch, _, _, _) = NewBatch();
        AddSized(batch, NewSource("scan"), 2480, 3508);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.PreviewViewportWidth = 470;
        vm.PreviewViewportHeight = 680;

        vm.SelectPage(vm.VisiblePages[0]);

        Assert.True(vm.ZoomPercent < 50);
        Assert.True(vm.PreviewDisplayWidth <= vm.PreviewViewportWidth);
    }

    [Fact]
    public void AutoFit_SecondPageSwitch_DoesNotResetManualZoom()
    {
        var (batch, _, _, _) = NewBatch();
        AddSized(batch, NewSource("wide1"), 1000, 500);
        AddSized(batch, NewSource("wide2"), 1000, 500);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.PreviewViewportWidth = 800;
        vm.PreviewViewportHeight = 600;

        vm.SelectPage(vm.VisiblePages[0]);
        Assert.True(vm.ZoomPercent < 100);

        vm.ZoomPercent = 150;
        vm.SelectPage(vm.VisiblePages[1]);

        Assert.Equal(150, vm.ZoomPercent);
    }

    [Fact]
    public void AutoFit_NoSelectedPage_DoesNotFit()
    {
        var (batch, _, _, _) = NewBatch();
        AddSized(batch, NewSource("wide"), 1000, 500);
        var vm = new BatchWorkspaceViewModel(batch);

        vm.PreviewViewportWidth = 800;
        vm.PreviewViewportHeight = 600;

        Assert.Null(vm.SelectedPage);
        Assert.Equal(100, vm.ZoomPercent);
    }

    [Fact]
    public void NoSelectedPage_DisplaySizeIsZero()
    {
        var (batch, _, _, _) = NewBatch();
        Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);

        Assert.Null(vm.SelectedPage);
        Assert.Equal(0, vm.PreviewPixelWidth);
        Assert.Equal(0, vm.PreviewPixelHeight);
        Assert.Equal(0, vm.PreviewDisplayWidth);
        Assert.Equal(0, vm.PreviewDisplayHeight);
    }

    [Fact]
    public void PinyinIndex_Initial()
    {
        Assert.Equal("S", PinyinIndex.Initial("苏晚晴"));
        Assert.Equal("C", PinyinIndex.Initial("陈屿"));
        Assert.Equal("#", PinyinIndex.Initial("123"));
        Assert.Equal("A", PinyinIndex.Initial("Amy"));
        Assert.Equal("#", PinyinIndex.Initial(""));
    }

    // ----------------------------- 识别徽标与原因 -----------------------------

    [Fact]
    public void Recognition_Success_BadgeReasonAndSummary()
    {
        var (batch, _, a, _) = NewBatch();
        var page = Add(batch, NewSource("a"));
        batch.SetRecognition(page.Id,
            new PageRecognition(RecognitionMethod.FillIn, "0123", 0.8, null, DateTimeOffset.Now));
        batch.Match(page.Id, a.Id);
        var vm = new BatchWorkspaceViewModel(batch);
        vm.ShowAllPages = true;
        var row = vm.VisiblePages.Single(p => p.Id == page.Id);

        Assert.Equal("填涂 0123", row.RecognitionBadge);
        Assert.Contains("0123", row.RecognitionReason);
        Assert.Contains(a.Name, row.RecognitionSummary);
        Assert.Equal(a.Name, row.StatusText);
    }

    [Fact]
    public void Recognition_Failure_BadgeReasonAndSummary()
    {
        var (batch, _, _, _) = NewBatch();
        var page = Add(batch, NewSource("a"));
        batch.SetRecognition(page.Id,
            new PageRecognition(RecognitionMethod.FillIn, "", 0, "第 2 列涂了多格", DateTimeOffset.Now));
        var vm = new BatchWorkspaceViewModel(batch);
        var row = vm.VisiblePages.Single(p => p.Id == page.Id);

        Assert.Equal("填涂失败", row.RecognitionBadge);
        Assert.Contains("第 2 列涂了多格", row.RecognitionReason);
        Assert.Contains("第 2 列涂了多格", row.RecognitionSummary);
        Assert.Equal("未匹配", row.StatusText);
    }

    [Fact]
    public void Recognition_None_AllEmpty_AndStatusUnmatched()
    {
        var (batch, _, _, _) = NewBatch();
        var page = Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        var row = vm.VisiblePages.Single(p => p.Id == page.Id);

        Assert.Equal(string.Empty, row.RecognitionBadge);
        Assert.Equal(string.Empty, row.RecognitionReason);
        Assert.Equal(string.Empty, row.RecognitionSummary);
        Assert.Equal("未匹配", row.StatusText);
    }

    // ----------------------------- 填涂区与框选 -----------------------------

    [Fact]
    public void FillInRegion_ProxySetter_UpdatesBatchAndSummary()
    {
        var (batch, _, _, _) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);
        var region = new NormalizedRect(0.05, 0.05, 0.9, 0.9);

        vm.FillInRegion = region;

        Assert.Equal(region, batch.FillInRegion);
        Assert.Equal(region, vm.FillInRegion);
        Assert.Equal("填涂区：已设置 · 2 列", vm.FillInRegionSummary);
    }

    [Fact]
    public void FillInColumns_Proxy_ValidatesAndNotifies()
    {
        var (batch, _, _, _) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);
        Assert.Equal(2, vm.FillInColumns); // 学号 01 / 02 → 自动推断 2 列

        var notified = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.FillInColumns)) notified++;
        };

        vm.FillInColumns = 4;
        Assert.Equal(4, batch.FillInColumns);
        Assert.Equal(4, vm.FillInColumns);
        Assert.True(notified > 0);

        vm.FillInColumns = 0;
        Assert.Equal(4, batch.FillInColumns); // 越界被 Core 拒绝，代理读回原值
        Assert.Contains("列数", vm.StatusText);

        vm.FillInColumns = 13;
        Assert.Equal(4, batch.FillInColumns);
    }

    [Fact]
    public void FillInRegionSummary_ReflectsBatchRegionAndColumns()
    {
        var (batch, _, _, _) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);
        Assert.Equal("填涂区：未设置", vm.FillInRegionSummary);

        batch.SetFillInRegion(new NormalizedRect(0.1, 0.1, 0.5, 0.5));
        vm.FillInColumns = 3;

        Assert.Equal("填涂区：已设置 · 3 列", vm.FillInRegionSummary);
    }

    [Fact]
    public void ApplyFillInRegion_BatchThenPerPage_OnlyTouchesTarget()
    {
        var (batch, _, _, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        var vm = new BatchWorkspaceViewModel(batch);
        var batchRegion = new NormalizedRect(0.1, 0.2, 0.3, 0.4);

        vm.DraftFillInRegion = batchRegion;
        vm.ApplyFillInRegion(perPage: false);

        Assert.Equal(batchRegion, batch.FillInRegion);
        Assert.Null(p1.FillInRegionOverride);
        Assert.Null(p2.FillInRegionOverride);
        Assert.False(vm.IsSelectingFillInRegion);

        var pageRegion = new NormalizedRect(0.2, 0.3, 0.4, 0.5);
        vm.SelectPage(vm.VisiblePages.Single(p => p.Id == p1.Id));
        vm.StartFillInRegionSelection();
        vm.DraftFillInRegion = pageRegion;
        vm.ApplyFillInRegion(perPage: true);

        Assert.Equal(batchRegion, batch.FillInRegion); // 批次区域不变
        Assert.Equal(pageRegion, p1.FillInRegionOverride);
        Assert.Null(p2.FillInRegionOverride);
        Assert.False(vm.IsSelectingFillInRegion);
    }

    [Fact]
    public void ApplyFillInRegion_PerPageWithoutSelection_FallsBackToBatch()
    {
        var (batch, _, _, _) = NewBatch();
        Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        Assert.False(vm.CanApplyPerPage);

        var region = new NormalizedRect(0.1, 0.1, 0.5, 0.5);
        vm.DraftFillInRegion = region;
        vm.ApplyFillInRegion(perPage: true);

        Assert.Equal(region, batch.FillInRegion);
    }

    [Fact]
    public void ApplyFillInRegion_UnusableDraft_DoesNotWrite()
    {
        var (batch, _, _, _) = NewBatch();
        var vm = new BatchWorkspaceViewModel(batch);

        vm.DraftFillInRegion = new NormalizedRect(0.1, 0.1, 0.01, 0.01); // 宽高低于最小可用尺寸
        vm.ApplyFillInRegion(perPage: false);

        Assert.Null(batch.FillInRegion);
        Assert.Contains("有效", vm.StatusText);
    }

    [Fact]
    public void ClearFillInRegion_RemovesBatchAndAllOverrides()
    {
        var (batch, _, _, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var p2 = Add(batch, NewSource("b"));
        batch.SetFillInRegion(new NormalizedRect(0.1, 0.1, 0.5, 0.5));
        p1.FillInRegionOverride = new NormalizedRect(0.2, 0.2, 0.3, 0.3);
        var vm = new BatchWorkspaceViewModel(batch);

        vm.ClearFillInRegion();

        Assert.Null(batch.FillInRegion);
        Assert.Null(p1.FillInRegionOverride);
        Assert.Null(p2.FillInRegionOverride);
        Assert.Equal("填涂区：未设置", vm.FillInRegionSummary);
        Assert.Contains("已清除", vm.StatusText);
    }

    [Fact]
    public void SelectionMode_PrefillsDraftFromEffectiveRegion_AndCancelLeavesBatchUntouched()
    {
        var (batch, _, _, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        vm.SelectPage(vm.VisiblePages.Single(p => p.Id == p1.Id));
        var region = new NormalizedRect(0.1, 0.1, 0.5, 0.5);
        batch.SetFillInRegion(region);

        vm.StartFillInRegionSelection();

        Assert.True(vm.IsSelectingFillInRegion);
        Assert.Equal(region, vm.DraftFillInRegion);
        Assert.True(vm.CanApplyPerPage);
        Assert.False(vm.ApplyRegionPerPage);

        vm.DraftFillInRegion = new NormalizedRect(0.2, 0.2, 0.3, 0.3);
        vm.CancelFillInRegion();

        Assert.False(vm.IsSelectingFillInRegion);
        Assert.Null(vm.DraftFillInRegion);
        Assert.Equal(region, batch.FillInRegion);
        Assert.Null(p1.FillInRegionOverride);
    }

    [Fact]
    public void SelectionMode_PerPageOverrideTakesPrecedenceInDraft()
    {
        var (batch, _, _, _) = NewBatch();
        var p1 = Add(batch, NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        vm.SelectPage(vm.VisiblePages.Single(p => p.Id == p1.Id));
        batch.SetFillInRegion(new NormalizedRect(0.1, 0.1, 0.5, 0.5));
        var perPage = new NormalizedRect(0.2, 0.2, 0.4, 0.4);
        p1.FillInRegionOverride = perPage;

        vm.StartFillInRegionSelection();

        Assert.Equal(perPage, vm.DraftFillInRegion);
        Assert.Equal(perPage, batch.EffectiveFillInRegion(p1));
    }

    [Fact]
    public void ApplyFillInRegion_AfterCommit_IsRejected()
    {
        var (batch, _, a, _) = NewBatch();
        var page = Add(batch, NewSource("a"));
        batch.Match(page.Id, a.Id);
        batch.Commit();
        var vm = new BatchWorkspaceViewModel(batch);
        vm.DraftFillInRegion = new NormalizedRect(0.1, 0.1, 0.5, 0.5);

        vm.ApplyFillInRegion(perPage: false);

        Assert.Null(batch.FillInRegion);
        Assert.Contains("已结束编辑", vm.StatusText);
    }

    [Fact]
    public void StartFillInRegionSelection_AfterCommit_IsRejected()
    {
        var (batch, _, a, _) = NewBatch();
        var page = Add(batch, NewSource("a"));
        batch.Match(page.Id, a.Id);
        batch.Commit();
        var vm = new BatchWorkspaceViewModel(batch);

        vm.StartFillInRegionSelection();

        Assert.False(vm.IsSelectingFillInRegion);
        Assert.Null(vm.DraftFillInRegion);
        Assert.Contains("已结束编辑", vm.StatusText);
    }
}
