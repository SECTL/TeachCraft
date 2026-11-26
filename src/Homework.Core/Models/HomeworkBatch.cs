using Homework.Core;
using Homework.Core.Recognition;

namespace Homework.Core.Models;

/// <summary>
/// 辑册批次聚合：名单、页、匹配与生命周期的唯一入口。
/// <para>
/// 不变量：页顺序由 Index 表达且始终密集；UnknownLabel 单调分配、永不重编号；
/// 只有 Editing 态可改动；提交前必须每页都已匹配且至少有一页。
/// </para>
/// </summary>
public sealed class HomeworkBatch
{
    private readonly List<HomeworkClass> _classes = [];
    private readonly List<HomeworkStudent> _students = [];
    private readonly List<HomeworkPage> _pages = [];
    private readonly Dictionary<Guid, HomeworkPage> _removedPages = [];
    private readonly List<string> _warnings = [];
    private int _unknownCounter;

    private HomeworkBatch(string name)
    {
        Id = Guid.NewGuid();
        Name = name;
        State = HomeworkBatchState.Editing;
    }

    public static HomeworkBatch Create(string name) => new(name);

    public Guid Id { get; }
    public string Name { get; }
    public HomeworkBatchState State { get; private set; }

    /// <summary>每人应交页数，用于判断谁还缺页；默认 1 页。</summary>
    public int ExpectedPagesPerStudent { get; private set; } = 1;
    public IReadOnlyList<HomeworkClass> Classes => _classes;
    public IReadOnlyList<HomeworkStudent> Students => _students;
    /// <summary>按 Index 升序排列。</summary>
    public IReadOnlyList<HomeworkPage> Pages => _pages;
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>批次级填涂区域；null 表示尚未框选。</summary>
    public NormalizedRect? FillInRegion { get; private set; }

    /// <summary>填涂列数；null 表示按名单自动推断。</summary>
    public int? FillInColumns { get; private set; }

    /// <summary>替换名单并清空全部匹配；同班重复非空学号拒绝，同班同名只记警告。</summary>
    public void LoadRoster(IReadOnlyList<HomeworkClass> classes, IReadOnlyList<HomeworkStudent> students)
    {
        EnsureEditing();

        var seenNumbers = new HashSet<(Guid ClassId, string Number)>();
        foreach (var student in students)
        {
            if (string.IsNullOrWhiteSpace(student.Number)) continue;
            if (!seenNumbers.Add((student.ClassId, student.Number)))
                throw new HomeworkException($"班级内学号「{student.Number}」重复，请检查名单。");
        }

        _classes.Clear();
        _classes.AddRange(classes);
        _students.Clear();
        _students.AddRange(students);

        foreach (var page in _pages)
        {
            page.StudentId = null;
            page.Recognition = null;
        }

        _warnings.Clear();
        foreach (var group in students.GroupBy(s => (s.ClassId, s.Name)).Where(g => g.Count() > 1))
            _warnings.Add($"班级内存在同名学生「{group.Key.Name}」，请核对。");
    }

    /// <summary>
    /// 加入一页。同一完整路径（忽略大小写）+ 同一页码视为重复而拒绝；路径必须存在。
    /// 内容哈希与更早的页相同只在后一页打提示，不阻断。
    /// </summary>
    public bool TryAddPage(
        string sourcePath,
        HomeworkPageKind kind,
        int sourcePageNumber,
        string contentHash,
        int width,
        int height,
        out HomeworkPage? page,
        out string? error)
    {
        EnsureEditing();
        page = null;
        error = null;

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            error = "源文件不存在，无法加入。";
            return false;
        }

        var fullPath = Path.GetFullPath(sourcePath);
        if (_pages.Any(p => IsSameSource(p, fullPath, sourcePageNumber)))
        {
            error = "这一页已经加入过，不能重复加入。";
            return false;
        }

        var resurrected = _removedPages.Values.FirstOrDefault(p => IsSameSource(p, fullPath, sourcePageNumber));
        if (resurrected is not null)
        {
            _removedPages.Remove(resurrected.Id);
            resurrected.StudentId = null;
            resurrected.Recognition = null;
            resurrected.FillInRegionOverride = null;
            resurrected.Index = _pages.Count;
            _pages.Add(resurrected);
            page = resurrected;
            return true;
        }

        var isDuplicateHint = !string.IsNullOrEmpty(contentHash) &&
                              _pages.Any(p => string.Equals(p.ContentHash, contentHash, StringComparison.Ordinal));

        var created = new HomeworkPage
        {
            Id = Guid.NewGuid(),
            SourcePath = fullPath,
            Kind = kind,
            SourcePageNumber = sourcePageNumber,
            Index = _pages.Count,
            StudentId = null,
            UnknownLabel = $"未知{++_unknownCounter}",
            ContentHash = contentHash ?? string.Empty,
            PixelWidth = width,
            PixelHeight = height,
            IsDuplicateHint = isDuplicateHint,
        };
        _pages.Add(created);
        page = created;
        return true;
    }

    /// <summary>把一页匹配给学生。</summary>
    public void Match(Guid pageId, Guid studentId)
    {
        EnsureEditing();
        var page = FindPage(pageId) ?? throw new HomeworkException("找不到这一页，无法匹配。");
        if (!_students.Any(s => s.Id == studentId))
            throw new HomeworkException("找不到该学生，无法匹配。");
        page.StudentId = studentId;
    }

    /// <summary>清除一页的匹配。</summary>
    public void Unmatch(Guid pageId)
    {
        EnsureEditing();
        var page = FindPage(pageId) ?? throw new HomeworkException("找不到这一页，无法取消匹配。");
        page.StudentId = null;
    }

    /// <summary>删除一页并压紧 Index；UnknownLabel 不重编号。</summary>
    public void DeletePage(Guid pageId)
    {
        EnsureEditing();
        var page = FindPage(pageId) ?? throw new HomeworkException("找不到这一页，无法删除。");
        _pages.Remove(page);
        _removedPages[page.Id] = page;
        Compact();
    }

    /// <summary>移动一页，delta 越界时夹到两端；随后压紧 Index。</summary>
    public void MovePage(Guid pageId, int delta)
    {
        EnsureEditing();
        var page = FindPage(pageId) ?? throw new HomeworkException("找不到这一页，无法移动。");
        var oldIndex = page.Index;
        var newIndex = Math.Clamp(oldIndex + delta, 0, _pages.Count - 1);
        if (newIndex == oldIndex) return;
        _pages.RemoveAt(oldIndex);
        _pages.Insert(newIndex, page);
        Compact();
    }

    /// <summary>某学生已匹配的页（按 Index 升序）。</summary>
    public IReadOnlyList<HomeworkPage> PagesOf(Guid studentId) =>
        _pages.Where(p => p.StudentId == studentId).ToList();

    public int UnmatchedCount => _pages.Count(p => p.StudentId is null);

    public IReadOnlyList<HomeworkPage> UnmatchedPages =>
        _pages.Where(p => p.StudentId is null).ToList();

    /// <summary>名单里还没有任何页的学生。</summary>
    public IReadOnlyList<HomeworkStudent> StudentsWithoutPages =>
        _students.Where(s => !_pages.Any(p => p.StudentId == s.Id)).ToList();

    /// <summary>已匹配页数少于应交页数的学生。</summary>
    public IReadOnlyList<HomeworkStudent> IncompleteStudents =>
        _students.Where(s => _pages.Count(p => p.StudentId == s.Id) < ExpectedPagesPerStudent).ToList();

    /// <summary>所有缺页学生合计还差多少页。</summary>
    public int MissingPageCount =>
        IncompleteStudents.Sum(s => ExpectedPagesPerStudent - _pages.Count(p => p.StudentId == s.Id));

    /// <summary>设置每人应交页数；只允许在编辑态修改。</summary>
    public void SetExpectedPages(int pages)
    {
        EnsureEditing();
        if (pages is < 1 or > 99)
            throw new HomeworkException("每人应交页数必须在 1 到 99 之间。");
        ExpectedPagesPerStudent = pages;
    }

    /// <summary>设置一页的识别结果；传 null 表示清除该页识别。未知页拒绝。</summary>
    public void SetRecognition(Guid pageId, PageRecognition? recognition)
    {
        EnsureEditing();
        var page = FindPage(pageId) ?? throw new HomeworkException("找不到这一页，无法记录识别结果。");
        page.Recognition = recognition;
    }

    /// <summary>清空全部页的识别结果。</summary>
    public void ClearRecognitions()
    {
        EnsureEditing();
        foreach (var page in _pages) page.Recognition = null;
    }

    /// <summary>设置批次级填涂区域；传 null 表示清除。</summary>
    public void SetFillInRegion(NormalizedRect? region)
    {
        EnsureEditing();
        FillInRegion = region;
    }

    /// <summary>设置填涂列数；null 表示自动，显式值必须在 1 到 12 之间。</summary>
    public void SetFillInColumns(int? columns)
    {
        EnsureEditing();
        if (columns.HasValue && (columns.Value < FillInOptions.MinColumns || columns.Value > FillInOptions.MaxColumns))
            throw new HomeworkException($"填涂列数必须在 {FillInOptions.MinColumns} 到 {FillInOptions.MaxColumns} 之间。");
        FillInColumns = columns;
    }

    /// <summary>该页实际使用的填涂区域：单页覆盖优先于批次区域。</summary>
    public NormalizedRect? EffectiveFillInRegion(HomeworkPage page) =>
        page.FillInRegionOverride ?? FillInRegion;

    /// <summary>
    /// 按名单推断列数：取非空且全为数字的学号中最长的位数，夹到 1..12；
    /// 没有合格学号时返回 1。
    /// </summary>
    public int AutoColumns()
    {
        var longest = _students
            .Where(s => !string.IsNullOrEmpty(s.Number) && s.Number.All(char.IsDigit))
            .Select(s => s.Number.Length)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Clamp(longest, FillInOptions.MinColumns, FillInOptions.MaxColumns);
    }

    /// <summary>拍下当前页顺序、匹配与识别结果，供撤销栈使用。</summary>
    public BatchEditSnapshot Capture()
    {
        var order = _pages.Select(p => p.Id).ToList();
        var matches = new Dictionary<Guid, Guid?>();
        var recognitions = new Dictionary<Guid, PageRecognition?>();
        foreach (var page in _pages)
        {
            matches[page.Id] = page.StudentId;
            recognitions[page.Id] = page.Recognition;
        }
        return new BatchEditSnapshot(order, matches, recognitions);
    }

    /// <summary>
    /// 恢复页顺序、匹配与识别结果。快照里的页 / 学生 Id 必须都还存在于本批次，否则拒绝；
    /// 已删除的页仍保留在批次内，因此删除也可以撤销。识别结果按页写回（含 null）。
    /// </summary>
    public void Restore(BatchEditSnapshot snapshot)
    {
        EnsureEditing();

        var pagesById = _pages.Concat(_removedPages.Values).ToDictionary(p => p.Id);
        foreach (var pageId in snapshot.PageOrder)
            if (!pagesById.ContainsKey(pageId))
                throw new HomeworkException("快照包含已不存在的页，无法恢复。");
        foreach (var pageId in snapshot.Matches.Keys)
            if (!pagesById.ContainsKey(pageId))
                throw new HomeworkException("快照包含已不存在的页，无法恢复。");
        foreach (var pageId in snapshot.Recognitions.Keys)
            if (!pagesById.ContainsKey(pageId))
                throw new HomeworkException("快照包含已不存在的页，无法恢复。");

        var studentIds = _students.Select(s => s.Id).ToHashSet();
        foreach (var studentId in snapshot.Matches.Values)
            if (studentId.HasValue && !studentIds.Contains(studentId.Value))
                throw new HomeworkException("快照包含已不存在的学生，无法恢复。");

        var restored = snapshot.PageOrder.ToHashSet();
        foreach (var page in _pages.Where(p => !restored.Contains(p.Id)))
            _removedPages[page.Id] = page;

        _pages.Clear();
        foreach (var pageId in snapshot.PageOrder)
        {
            var page = pagesById[pageId];
            _removedPages.Remove(page.Id);
            _pages.Add(page);
            page.StudentId = snapshot.Matches.TryGetValue(page.Id, out var studentId) ? studentId : null;
            page.Recognition = snapshot.Recognitions.TryGetValue(page.Id, out var recognition) ? recognition : null;
        }
        Compact();
    }

    /// <summary>提交：必须至少一页且全部已匹配。</summary>
    public void Commit()
    {
        EnsureEditing();
        if (_pages.Count == 0)
            throw new HomeworkException("还没有加入任何页，不能提交。");
        if (UnmatchedCount > 0)
            throw new HomeworkException($"还有 {UnmatchedCount} 页未匹配学生，不能提交。");
        State = HomeworkBatchState.Committed;
    }

    /// <summary>放弃整批。</summary>
    public void Abandon()
    {
        EnsureEditing();
        State = HomeworkBatchState.Abandoned;
    }

    private void EnsureEditing()
    {
        if (State != HomeworkBatchState.Editing)
            throw new HomeworkException("当前批次已结束编辑，不能修改。");
    }

    private HomeworkPage? FindPage(Guid pageId) => _pages.FirstOrDefault(p => p.Id == pageId);

    private static bool IsSameSource(HomeworkPage page, string fullPath, int sourcePageNumber) =>
        page.SourcePageNumber == sourcePageNumber &&
        string.Equals(page.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase);

    private void Compact()
    {
        for (var i = 0; i < _pages.Count; i++) _pages[i].Index = i;
    }
}
