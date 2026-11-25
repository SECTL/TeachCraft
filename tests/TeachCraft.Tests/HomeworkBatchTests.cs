using Homework.Core;
using Homework.Core.Matching;
using Homework.Core.Models;
using Homework.Core.Recognition;

namespace TeachCraft.Tests;

/// <summary>
/// 作业整理批次聚合的不变量。全部使用合成姓名（苏晚晴 / 陈屿 / 许砚），
/// 页来源用临时文件，测试结束即删除。
/// </summary>
public sealed class HomeworkBatchTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
            if (File.Exists(file)) File.Delete(file);
    }

    private string NewSource()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        return path;
    }

    private static HomeworkPage Add(HomeworkBatch batch, string path, string hash = "h", int page = 1)
    {
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.Image, page, hash, 100, 200, out var added, out var error), error);
        return added!;
    }

    private static void SetRegionOverride(HomeworkPage page, NormalizedRect? region) =>
        typeof(HomeworkPage).GetProperty(nameof(HomeworkPage.FillInRegionOverride))!
            .GetSetMethod(nonPublic: true)!.Invoke(page, [region]);

    private static (HomeworkClass Cls, HomeworkStudent A, HomeworkStudent B) Roster()
    {
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        return (
            cls,
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "02", "陈屿"));
    }

    // ----------------------------- 加入与压紧 -----------------------------

    [Fact]
    public void AddPage_AssignsDenseIndexAndMonotonicUnknownLabel()
    {
        var batch = HomeworkBatch.Create("第一次作业");
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());
        var p3 = Add(batch, NewSource());

        Assert.Equal(new[] { 0, 1, 2 }, new[] { p1.Index, p2.Index, p3.Index });
        Assert.Equal("未知1", p1.UnknownLabel);
        Assert.Equal("未知2", p2.UnknownLabel);
        Assert.Equal("未知3", p3.UnknownLabel);
        Assert.Equal(3, batch.Pages.Count);
    }

    [Fact]
    public void DeletePage_CompactsIndexButNeverRenumbersUnknownLabels()
    {
        var batch = HomeworkBatch.Create("作业");
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());
        var p3 = Add(batch, NewSource());

        batch.DeletePage(p2.Id);

        Assert.Equal(2, batch.Pages.Count);
        Assert.Equal(new[] { 0, 1 }, batch.Pages.Select(p => p.Index).ToArray());
        Assert.Equal("未知1", p1.UnknownLabel);
        Assert.Equal("未知3", p3.UnknownLabel);
        Assert.DoesNotContain(batch.Pages, p => p.Id == p2.Id);

        // 再删首项，剩下的页仍保留原标签。
        batch.DeletePage(p1.Id);
        Assert.Single(batch.Pages);
        Assert.Equal("未知3", batch.Pages[0].UnknownLabel);
        Assert.Equal(0, batch.Pages[0].Index);
    }

    [Fact]
    public void MovePage_ClampsAtBothEndsAndCompacts()
    {
        var batch = HomeworkBatch.Create("作业");
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());
        var p3 = Add(batch, NewSource());

        batch.MovePage(p1.Id, -5);
        Assert.Equal(p1.Id, batch.Pages[0].Id);          // 夹到 0

        batch.MovePage(p3.Id, 5);
        Assert.Equal(p3.Id, batch.Pages[2].Id);          // 夹到末尾

        batch.MovePage(p1.Id, 1);
        Assert.Equal(new[] { p2.Id, p1.Id, p3.Id }, batch.Pages.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, batch.Pages.Select(p => p.Index).ToArray());
    }

    // ----------------------------- 去重与提示 -----------------------------

    [Fact]
    public void DuplicateSourcePathAndPage_IsRejected()
    {
        var batch = HomeworkBatch.Create("作业");
        var path = NewSource();
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.Image, 1, "h", 10, 10, out _, out _));

        var ok = batch.TryAddPage(path.ToUpperInvariant(), HomeworkPageKind.Image, 1, "h", 10, 10, out var page, out var error);
        Assert.False(ok);
        Assert.Null(page);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Single(batch.Pages);

        // 同一文件的不同页码允许（PDF 多页）。
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.PdfPage, 2, "h2", 10, 10, out _, out _));
        Assert.Equal(2, batch.Pages.Count);
    }

    [Fact]
    public void NonExistentPath_IsRejected()
    {
        var batch = HomeworkBatch.Create("作业");
        var ok = batch.TryAddPage(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"),
            HomeworkPageKind.Image, 1, "h", 10, 10, out var page, out var error);
        Assert.False(ok);
        Assert.Null(page);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(batch.Pages);
    }

    [Fact]
    public void DuplicateContentHash_SetsHintOnLaterPageOnly()
    {
        var batch = HomeworkBatch.Create("作业");
        var first = Add(batch, NewSource(), "same-hash");
        var second = Add(batch, NewSource(), "same-hash");
        var third = Add(batch, NewSource(), "other-hash");

        Assert.False(first.IsDuplicateHint);
        Assert.True(second.IsDuplicateHint);
        Assert.False(third.IsDuplicateHint);
        Assert.Equal(3, batch.Pages.Count);   // 提示不阻断
    }

    // ----------------------------- 名单 -----------------------------

    [Fact]
    public void LoadRoster_DuplicateNumber_Throws()
    {
        var batch = HomeworkBatch.Create("作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var students = new[]
        {
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "陈屿"),
        };
        Assert.Throws<HomeworkException>(() => batch.LoadRoster([cls], students));
    }

    [Fact]
    public void LoadRoster_EmptyNumberIsAllowed()
    {
        var batch = HomeworkBatch.Create("作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var students = new[]
        {
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "陈屿"),
        };
        batch.LoadRoster([cls], students);
        Assert.Equal(2, batch.Students.Count);
        Assert.Empty(batch.Warnings);
    }

    [Fact]
    public void LoadRoster_DuplicateName_Warns()
    {
        var batch = HomeworkBatch.Create("作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var students = new[]
        {
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "02", "苏晚晴"),
        };
        batch.LoadRoster([cls], students);
        Assert.Single(batch.Warnings);
        Assert.Contains("苏晚晴", batch.Warnings[0]);
    }

    [Fact]
    public void LoadRoster_ClearsAllMatches()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a, Roster().B]);
        var page = Add(batch, NewSource());
        batch.Match(page.Id, a.Id);
        Assert.NotNull(page.StudentId);

        batch.LoadRoster([cls], [a, Roster().B]);
        Assert.Null(page.StudentId);
        Assert.Equal(1, batch.UnmatchedCount);
    }

    [Fact]
    public void LoadRoster_ClearsAllRecognitions()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a, Roster().B]);
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());
        var recognition = new PageRecognition(RecognitionMethod.FillIn, "001", 0.9, null, DateTimeOffset.UtcNow);

        batch.SetRecognition(p1.Id, recognition);
        batch.Match(p1.Id, a.Id);
        batch.SetRecognition(p2.Id, recognition with { Digits = "002" });
        batch.Match(p2.Id, a.Id);
        Assert.All(batch.Pages, p => Assert.NotNull(p.Recognition));
        Assert.All(batch.Pages, p => Assert.NotNull(p.StudentId));

        batch.LoadRoster([cls], [a, Roster().B]);

        Assert.All(batch.Pages, p => Assert.Null(p.Recognition));
        Assert.All(batch.Pages, p => Assert.Null(p.StudentId));
    }

    // ----------------------------- 匹配与查询 -----------------------------

    [Fact]
    public void MatchUnmatch_AndPagesOf()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, b) = Roster();
        batch.LoadRoster([cls], [a, b]);
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());

        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, a.Id);

        Assert.Equal(a.Id, p1.StudentId);
        Assert.Equal(2, batch.PagesOf(a.Id).Count);
        Assert.Empty(batch.PagesOf(b.Id));
        Assert.Equal(0, batch.UnmatchedCount);

        batch.Unmatch(p1.Id);
        Assert.Null(p1.StudentId);
        Assert.Single(batch.PagesOf(a.Id));
        Assert.Single(batch.UnmatchedPages);
    }

    [Fact]
    public void Match_WithUnknownStudent_Throws()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        Assert.Throws<HomeworkException>(() => batch.Match(page.Id, Guid.NewGuid()));
    }

    [Fact]
    public void StudentsWithoutPages_ExcludesMatchedStudents()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, b) = Roster();
        batch.LoadRoster([cls], [a, b]);
        var page = Add(batch, NewSource());
        batch.Match(page.Id, a.Id);

        var without = batch.StudentsWithoutPages;
        Assert.Single(without);
        Assert.Equal(b.Id, without[0].Id);
    }

    // ----------------------------- 提交 / 放弃 / 只读 -----------------------------

    [Fact]
    public void Commit_WithUnmatchedPages_Throws()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a, Roster().B]);
        Add(batch, NewSource());
        Assert.Throws<HomeworkException>(() => batch.Commit());
        Assert.Equal(HomeworkBatchState.Editing, batch.State);
    }

    [Fact]
    public void Commit_WithZeroPages_Throws()
    {
        var batch = HomeworkBatch.Create("作业");
        Assert.Throws<HomeworkException>(() => batch.Commit());
    }

    [Fact]
    public void Commit_WhenAllMatched_SetsCommitted()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a, Roster().B]);
        var page = Add(batch, NewSource());
        batch.Match(page.Id, a.Id);

        batch.Commit();
        Assert.Equal(HomeworkBatchState.Committed, batch.State);
    }

    [Fact]
    public void CommittedBatch_RejectsAllMutations()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a, Roster().B]);
        var page = Add(batch, NewSource());
        batch.Match(page.Id, a.Id);
        batch.Commit();

        Assert.Throws<HomeworkException>(() => batch.TryAddPage(NewSource(), HomeworkPageKind.Image, 1, "h", 1, 1, out _, out _));
        Assert.Throws<HomeworkException>(() => batch.Match(page.Id, a.Id));
        Assert.Throws<HomeworkException>(() => batch.Unmatch(page.Id));
        Assert.Throws<HomeworkException>(() => batch.DeletePage(page.Id));
        Assert.Throws<HomeworkException>(() => batch.MovePage(page.Id, 1));
        Assert.Throws<HomeworkException>(() => batch.LoadRoster([cls], [a]));
        Assert.Throws<HomeworkException>(() => batch.Commit());
        Assert.Throws<HomeworkException>(() => batch.Abandon());
    }

    [Fact]
    public void Abandon_SetsAbandonedAndLocks()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        batch.Abandon();

        Assert.Equal(HomeworkBatchState.Abandoned, batch.State);
        Assert.Throws<HomeworkException>(() => batch.DeletePage(page.Id));
        Assert.Throws<HomeworkException>(() => batch.Commit());
    }

    // ----------------------------- 快照 -----------------------------

    [Fact]
    public void ExpectedPages_DefaultsToOneAndValidates()
    {
        var batch = HomeworkBatch.Create("作业");
        Assert.Equal(1, batch.ExpectedPagesPerStudent);
        Assert.Throws<HomeworkException>(() => batch.SetExpectedPages(0));
        Assert.Throws<HomeworkException>(() => batch.SetExpectedPages(100));
        batch.SetExpectedPages(3);
        Assert.Equal(3, batch.ExpectedPagesPerStudent);

        batch.Abandon();
        Assert.Throws<HomeworkException>(() => batch.SetExpectedPages(2));
    }

    [Fact]
    public void CaptureRestore_RestoresOrderAndMatches()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, b) = Roster();
        batch.LoadRoster([cls], [a, b]);
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());
        var p3 = Add(batch, NewSource());
        batch.Match(p1.Id, a.Id);
        batch.Match(p2.Id, b.Id);
        var snapshot = batch.Capture();

        batch.MovePage(p1.Id, 2);
        batch.Unmatch(p1.Id);
        batch.Match(p3.Id, a.Id);
        Assert.NotEqual(snapshot, batch.Capture());

        batch.Restore(snapshot);

        Assert.Equal(snapshot, batch.Capture());
        Assert.Equal(new[] { p1.Id, p2.Id, p3.Id }, batch.Pages.Select(p => p.Id).ToArray());
        Assert.Equal(a.Id, p1.StudentId);
        Assert.Equal(b.Id, p2.StudentId);
        Assert.Null(p3.StudentId);
    }

    [Fact]
    public void Restore_AfterDelete_BringsPageBackInOriginalPosition()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a]);
        var p1 = Add(batch, NewSource());
        var p2 = Add(batch, NewSource());
        batch.Match(p2.Id, a.Id);
        var snapshot = batch.Capture();

        batch.DeletePage(p2.Id);
        Assert.Single(batch.Pages);
        Assert.DoesNotContain(p2.Id, batch.Pages.Select(p => p.Id));

        batch.Restore(snapshot);

        Assert.Equal(new[] { p1.Id, p2.Id }, batch.Pages.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { 0, 1 }, batch.Pages.Select(p => p.Index).ToArray());
        Assert.Equal(a.Id, p2.StudentId);
    }

    [Fact]
    public void ReAddingDeletedSource_ResurrectsSamePageInsteadOfDuplicating()
    {
        var batch = HomeworkBatch.Create("作业");
        var source = NewSource();
        var page = Add(batch, source);
        batch.DeletePage(page.Id);
        Assert.Empty(batch.Pages);

        var again = Add(batch, source);

        Assert.Equal(page.Id, again.Id);
        Assert.Single(batch.Pages);
    }

    [Fact]
    public void ReAddingDeletedSource_ClearsRecognitionAndRegionOverride()
    {
        var batch = HomeworkBatch.Create("作业");
        var source = NewSource();
        var page = Add(batch, source);
        var recognition = new PageRecognition(RecognitionMethod.FillIn, "001", 0.9, null, DateTimeOffset.UtcNow);
        batch.SetRecognition(page.Id, recognition);
        page.FillInRegionOverride = new NormalizedRect(0.1, 0.1, 0.5, 0.5);
        Assert.NotNull(page.Recognition);
        Assert.NotNull(page.FillInRegionOverride);

        batch.DeletePage(page.Id);
        var again = Add(batch, source);

        Assert.Equal(page.Id, again.Id);
        Assert.Null(again.Recognition);
        Assert.Null(again.FillInRegionOverride);
    }

    [Fact]
    public void Restore_WithUnknownPageId_Throws()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var bad = new BatchEditSnapshot([page.Id, Guid.NewGuid()],
            new Dictionary<Guid, Guid?> { [page.Id] = null, [Guid.NewGuid()] = null });
        Assert.Throws<HomeworkException>(() => batch.Restore(bad));
    }

    [Fact]
    public void Restore_WithUnknownStudentId_Throws()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var bad = new BatchEditSnapshot([page.Id],
            new Dictionary<Guid, Guid?> { [page.Id] = Guid.NewGuid() });
        Assert.Throws<HomeworkException>(() => batch.Restore(bad));
    }

    // ----------------------------- 识别模型与批次 API -----------------------------

    [Fact]
    public void NormalizedRect_Clamp_RejectsNonFiniteAndClampsBounds()
    {
        var clamped = new NormalizedRect(-0.5, 1.5, 2d, -1d).Clamp();
        Assert.Equal(new NormalizedRect(0d, 1d, 1d, 0d), clamped);

        Assert.Throws<HomeworkException>(() => new NormalizedRect(double.NaN, 0, 0.5, 0.5).Clamp());
        Assert.Throws<HomeworkException>(() => new NormalizedRect(0, double.PositiveInfinity, 0.5, 0.5).Clamp());
        Assert.Throws<HomeworkException>(() => new NormalizedRect(0, 0, double.NegativeInfinity, 0.5).Clamp());
        Assert.Throws<HomeworkException>(() => new NormalizedRect(0, 0, 0.5, double.NaN).Clamp());
    }

    [Fact]
    public void NormalizedRect_IsUsable_RequiresBothDimensionsAtLeastTwoPercent()
    {
        Assert.True(new NormalizedRect(0, 0, 0.02, 0.02).IsUsable);
        Assert.True(new NormalizedRect(0.5, 0.5, 0.3, 0.4).IsUsable);
        Assert.False(new NormalizedRect(0, 0, 0.019, 0.02).IsUsable);
        Assert.False(new NormalizedRect(0, 0, 0.02, 0.019).IsUsable);
    }

    [Fact]
    public void NormalizedRect_ToPixelRect_MapsNormalizedToPixels()
    {
        var rect = new NormalizedRect(0.1, 0.2, 0.5, 0.4).ToPixelRect(1000, 800);
        Assert.Equal(100, rect.Left);
        Assert.Equal(160, rect.Top);
        Assert.Equal(500, rect.Width);
        Assert.Equal(320, rect.Height);
    }

    [Fact]
    public void PageRecognition_Succeeded_RequiresNoFailureAndNonEmptyDigits()
    {
        var ok = new PageRecognition(RecognitionMethod.FillIn, "0123", 0.8, null, DateTimeOffset.UtcNow);
        Assert.True(ok.Succeeded);

        var failed = ok with { FailureReason = "第 2 列涂了多格" };
        Assert.False(failed.Succeeded);

        var empty = ok with { Digits = "" };
        Assert.False(empty.Succeeded);
    }

    [Fact]
    public void SetFillInColumns_AcceptsNullAndBounds_RejectsOutOfRange()
    {
        var batch = HomeworkBatch.Create("作业");
        Assert.Null(batch.FillInColumns);

        batch.SetFillInColumns(1);
        Assert.Equal(1, batch.FillInColumns);
        batch.SetFillInColumns(12);
        Assert.Equal(12, batch.FillInColumns);
        batch.SetFillInColumns(null);
        Assert.Null(batch.FillInColumns);

        Assert.Throws<HomeworkException>(() => batch.SetFillInColumns(0));
        Assert.Throws<HomeworkException>(() => batch.SetFillInColumns(13));

        batch.Abandon();
        Assert.Throws<HomeworkException>(() => batch.SetFillInColumns(3));
    }

    [Fact]
    public void SetRecognition_And_ClearRecognitions()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var recognition = new PageRecognition(RecognitionMethod.FillIn, "001", 0.9, null, DateTimeOffset.UtcNow);

        batch.SetRecognition(page.Id, recognition);
        Assert.Equal(recognition, page.Recognition);

        batch.SetRecognition(page.Id, null);
        Assert.Null(page.Recognition);

        batch.SetRecognition(page.Id, recognition);
        batch.ClearRecognitions();
        Assert.Null(page.Recognition);

        Assert.Throws<HomeworkException>(() => batch.SetRecognition(Guid.NewGuid(), recognition));

        batch.Abandon();
        Assert.Throws<HomeworkException>(() => batch.SetRecognition(page.Id, recognition));
        Assert.Throws<HomeworkException>(() => batch.ClearRecognitions());
    }

    [Fact]
    public void AutoColumns_UsesLongestNumericNumberAndClamps()
    {
        var batch = HomeworkBatch.Create("作业");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");

        batch.LoadRoster([cls], [
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "001", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "002", "陈屿"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "003", "许砚"),
        ]);
        Assert.Equal(3, batch.AutoColumns());

        // 空学号与非数字学号都不计入；全无合格学号时返回 1。
        batch.LoadRoster([cls], [
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "A01", "陈屿"),
        ]);
        Assert.Equal(1, batch.AutoColumns());

        // 合格与不合格混排，只取合格的最长位数。
        batch.LoadRoster([cls], [
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "0001", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "A01", "陈屿"),
            new HomeworkStudent(Guid.NewGuid(), cls.Id, "", "许砚"),
        ]);
        Assert.Equal(4, batch.AutoColumns());

        // 超过上限夹到 12。
        batch.LoadRoster([cls], [
            new HomeworkStudent(Guid.NewGuid(), cls.Id, new string('1', 15), "苏晚晴"),
        ]);
        Assert.Equal(12, batch.AutoColumns());
    }

    [Fact]
    public void EffectiveFillInRegion_PageOverrideWinsOverBatchRegion()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var batchRegion = new NormalizedRect(0.1, 0.1, 0.8, 0.2);
        var pageRegion = new NormalizedRect(0.2, 0.3, 0.5, 0.4);

        Assert.Null(batch.EffectiveFillInRegion(page));

        batch.SetFillInRegion(batchRegion);
        Assert.Equal(batchRegion, batch.EffectiveFillInRegion(page));

        SetRegionOverride(page, pageRegion);
        Assert.Equal(pageRegion, batch.EffectiveFillInRegion(page));

        batch.SetFillInRegion(null);
        Assert.Equal(pageRegion, batch.EffectiveFillInRegion(page));

        SetRegionOverride(page, null);
        Assert.Null(batch.EffectiveFillInRegion(page));

        batch.Abandon();
        Assert.Throws<HomeworkException>(() => batch.SetFillInRegion(batchRegion));
    }

    // ----------------------------- 识别元数据与撤销 -----------------------------

    private static void ApplyChange(HomeworkBatch batch, HomeworkUndoStack stack, string label, Action action)
    {
        var before = batch.Capture();
        action();
        var after = batch.Capture();
        if (before.Equals(after)) return; // 内容没变不入栈（与 RecordChange 同语义）。
        stack.Record(label, before, after);
    }

    private static void Undo(HomeworkBatch batch, HomeworkUndoStack stack)
    {
        var target = stack.Undo(batch.Capture());
        if (target is not null) batch.Restore(target);
    }

    private static void Redo(HomeworkBatch batch, HomeworkUndoStack stack)
    {
        var target = stack.Redo(batch.Capture());
        if (target is not null) batch.Restore(target);
    }

    private static PageRecognition Recognition(string digits) =>
        new(RecognitionMethod.FillIn, digits, 0.9, null, DateTimeOffset.UtcNow);

    [Fact]
    public void RecognitionOnlyChange_IsUndoable()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var stack = new HomeworkUndoStack();
        var recognition = Recognition("001");

        ApplyChange(batch, stack, "识别填涂", () => batch.SetRecognition(page.Id, recognition));

        Assert.True(stack.CanUndo);
        Assert.Equal("识别填涂", stack.UndoLabel);
        Assert.Equal(recognition, page.Recognition);
        Assert.Null(page.StudentId); // 只有识别，没有匹配。

        Undo(batch, stack);

        Assert.Null(page.Recognition);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Undo_RestoresBothMatchAndRecognition()
    {
        var batch = HomeworkBatch.Create("作业");
        var (cls, a, _) = Roster();
        batch.LoadRoster([cls], [a, Roster().B]);
        var page = Add(batch, NewSource());
        var stack = new HomeworkUndoStack();
        var recognition = Recognition("01");

        ApplyChange(batch, stack, "识别并匹配", () =>
        {
            batch.SetRecognition(page.Id, recognition);
            batch.Match(page.Id, a.Id);
        });

        Assert.Equal(recognition, page.Recognition);
        Assert.Equal(a.Id, page.StudentId);

        Undo(batch, stack);

        Assert.Null(page.Recognition);
        Assert.Null(page.StudentId);
    }

    [Fact]
    public void ReapplyingIdenticalRecognition_DoesNotCreateNewStep()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var stack = new HomeworkUndoStack();
        var recognition = Recognition("001");

        ApplyChange(batch, stack, "识别填涂", () => batch.SetRecognition(page.Id, recognition));
        Assert.True(stack.CanUndo);

        // 再次设置完全相同的识别内容：值相等，不入栈。
        ApplyChange(batch, stack, "重复识别", () => batch.SetRecognition(page.Id, recognition));

        Assert.Equal("识别填涂", stack.UndoLabel);
        Undo(batch, stack);
        Assert.False(stack.CanUndo); // 只有一步，撤销后栈空。
    }

    [Fact]
    public void Redo_RestoresRecognitionState()
    {
        var batch = HomeworkBatch.Create("作业");
        var page = Add(batch, NewSource());
        var stack = new HomeworkUndoStack();
        var recognition = Recognition("002");

        ApplyChange(batch, stack, "识别填涂", () => batch.SetRecognition(page.Id, recognition));
        Undo(batch, stack);
        Assert.Null(page.Recognition);
        Assert.True(stack.CanRedo);

        Redo(batch, stack);

        Assert.Equal(recognition, page.Recognition);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void SnapshotEquality_IncludesRecognitions()
    {
        var page = Guid.NewGuid();
        var r1 = Recognition("001");
        var r2 = Recognition("002");

        var withR1 = new BatchEditSnapshot([page],
            new Dictionary<Guid, Guid?> { [page] = null },
            new Dictionary<Guid, PageRecognition?> { [page] = r1 });
        var withR1Again = new BatchEditSnapshot([page],
            new Dictionary<Guid, Guid?> { [page] = null },
            new Dictionary<Guid, PageRecognition?> { [page] = r1 });
        var withR2 = new BatchEditSnapshot([page],
            new Dictionary<Guid, Guid?> { [page] = null },
            new Dictionary<Guid, PageRecognition?> { [page] = r2 });

        Assert.Equal(withR1, withR1Again);
        Assert.Equal(withR1.GetHashCode(), withR1Again.GetHashCode());
        Assert.NotEqual(withR1, withR2);
    }
}
