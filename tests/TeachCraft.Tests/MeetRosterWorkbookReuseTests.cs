using ClosedXML.Excel;
using MeetRoster.App;
using MeetRoster.Core.Import;

namespace TeachCraft.Tests;

/// <summary>
/// T9：一次秩序册导入只打开一次工作簿。判类型（DetectKind）与导入（Import）共用同一个
/// <see cref="XLWorkbook"/> 实例，不再各自 <c>new XLWorkbook(流)</c>。
/// <para>
/// 计数方式：导入前先把源文件**在磁盘上改坏**。会话已经把内容读进内存，
/// 凡是还要按路径重开一次的实现都会拿到「无法读取这份工作簿」——
/// 秩序册那条会把异常吞成提醒、赛程直接为空，于是「改坏后仍读到完整赛程」本身就证明
/// 整条链路一次也没回头碰路径。<see cref="PathOverloads_StillReadFromDiskWhenTheFileIsIntact"/>
/// 反过来在坏文件上跑按路径导入，证明这剂毒药真的有效（否则前面的用例会变成永远通过的摆设）。
/// </para>
/// 全部工作簿为内存构造的合成数据，姓名均为虚构。
/// </summary>
public sealed class MeetRosterWorkbookReuseTests : IDisposable
{
    private readonly string _root;

    public MeetRosterWorkbookReuseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MeetRoster.WorkbookReuse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响测试结论 */ }
    }

    // ---------- Core ----------

    [Fact]
    public void OrderBookImport_WithOpenWorkbook_DoesNotReopenThePath()
    {
        var path = CreateOrderBook();
        using var session = WorkbookSession.Open(path);

        // 会话已把内容读进内存；路径上的文件此时不再可用。
        Corrupt(path);

        var result = OrderBookImporter.Import(path, session.Workbook);

        Assert.True(result.HasRoster);
        Assert.True(result.HasSchedule);
        Assert.Equal(2, result.Project.Classes.Count);
        Assert.Equal(5, result.Project.Days[0].Slots.Count);
        // 分组表的条目也要读到：说明赛程那条分支同样没走路径重开。
        Assert.Contains(result.Project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries),
            e => e.AthleteName == "苏晚晴");
    }

    [Fact]
    public void ScheduleImport_WithOpenWorkbook_DoesNotReopenThePath()
    {
        var path = CreateOrderBook();
        using var session = WorkbookSession.Open(path);
        Corrupt(path);

        var result = ScheduleWorkbookImporter.Import(path, session.Workbook);

        Assert.Single(result.Days);
        Assert.Equal(5, result.Days[0].Slots.Count);
        // 分组表条目也读到了：姓名 / 道次 / 拼好的全名，一个都不能少。
        var entry = result.Days[0].Slots.SelectMany(s => s.Entries).Single(e => e.AthleteName == "苏晚晴");
        Assert.Equal("七年级1班", entry.ClassName);
        Assert.Equal("第 1 组 · 第 1 道", entry.Lane);
    }

    [Fact]
    public void PathOverloads_StillReadFromDiskWhenTheFileIsIntact()
    {
        var path = CreateOrderBook();

        // 向后兼容：老入口仍然自己开文件，结果与复用实例时一致。
        var byPath = OrderBookImporter.Import(path);
        Assert.True(byPath.HasSchedule);

        // 毒药有效性：文件坏掉之后，按路径导入必须失败——上两个用例的结论才有意义。
        Corrupt(path);
        var ex = Assert.Throws<InvalidDataException>(() => OrderBookImporter.Import(path));
        Assert.Contains("无法读取这份工作簿", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ScheduleWorkbookImporter.Import(path));
    }

    [Fact]
    public void DetectKind_WithOpenWorkbook_LeavesTheInstanceUsable()
    {
        var path = CreateOrderBook();
        using var session = WorkbookSession.Open(path);

        Assert.Equal(MeetWorkbookKind.OrderBook, OrderBookImporter.DetectKind(session.Workbook));

        // 判类型只读表名，不能把交进来的实例弄坏（也不能释放它）。
        var result = OrderBookImporter.Import(path, session.Workbook);

        Assert.True(result.HasSchedule);
        Assert.Contains("代表队名单", session.Workbook.Worksheets.Select(w => w.Name));
        Assert.Contains("竞赛日程", session.Workbook.Worksheets.Select(w => w.Name));
    }

    [Fact]
    public void DetectKind_WithUnknownWorkbook_ReturnsUnknownWithoutThrowing()
    {
        var path = Path.Combine(_root, "不相干.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Sheet1").Cell(1, 1).Value = "随便写点什么";
            workbook.SaveAs(path);
        }

        using var session = WorkbookSession.Open(path);

        Assert.Equal(MeetWorkbookKind.Unknown, OrderBookImporter.DetectKind(session.Workbook));
        // 判类型不消耗实例：同一个会话再判一次仍然是同一个答案。
        Assert.Equal(MeetWorkbookKind.Unknown, OrderBookImporter.DetectKind(session.Workbook));
    }

    [Fact]
    public void WorkbookSession_DisposeIsIdempotent()
    {
        var session = WorkbookSession.Open(CreateOrderBook());

        session.Dispose();
        // 重复释放不能抛：窗口里 using + 提前 Dispose 都走同一条路径。
        session.Dispose();
    }

    [Fact]
    public void WorkbookSession_ReleasesTheFileOnDispose()
    {
        var path = CreateOrderBook();
        var session = WorkbookSession.Open(path);

        // 会话存活期间以共享读打开：Excel 等程序可以同时读同一份文件。
        using (var concurrent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.True(concurrent.Length > 0);

        session.Dispose();

        // 释放后才能拿到排他句柄：说明工作簿与文件流两个句柄都由会话收干净了。
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    // ---------- ViewModel（顶栏「导入秩序册」实际走的路径） ----------

    [Fact]
    public async Task ViewModel_OrderBookImport_ReusesTheSuppliedWorkbook()
    {
        var path = CreateOrderBook();
        var vm = new MeetRosterViewModel();
        using var session = WorkbookSession.Open(path);
        Corrupt(path);

        await vm.ImportOrderBookAsync(path, session.Workbook);

        Assert.Equal(2, vm.AllClasses.Count);
        Assert.True(vm.HasScheduleDays);
        Assert.Contains("已导入秩序册", vm.StatusText, StringComparison.Ordinal);
        // ViewModel 只借用工作簿：导入完仍然可读，说明它没有替调用方 Dispose。
        Assert.Equal(3, session.Workbook.Worksheets.Count);
    }

    [Fact]
    public async Task ViewModel_ScheduleImport_ReusesTheSuppliedWorkbook()
    {
        var path = CreateOrderBook();
        var vm = new MeetRosterViewModel();
        using var session = WorkbookSession.Open(path);
        Corrupt(path);

        await vm.ImportScheduleFileAsync(path, session.Workbook);

        Assert.True(vm.HasScheduleDays);
        Assert.Contains("已导入赛程", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(3, session.Workbook.Worksheets.Count);
    }

    [Fact]
    public async Task ViewModel_OrderBookImport_WithWorkbook_MatchesThePathOverload()
    {
        var byPath = new MeetRosterViewModel();
        await byPath.ImportOrderBookAsync(CreateOrderBook());

        var byWorkbook = new MeetRosterViewModel();
        var path = CreateOrderBook("缓存版");
        using var session = WorkbookSession.Open(path);
        await byWorkbook.ImportOrderBookAsync(path, session.Workbook);

        // 结果不能因为换了入口而变：班级、运动员与赛程时段逐项一致。
        Assert.Equal(
            byPath.AllClasses.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal),
            byWorkbook.AllClasses.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(byPath.Project.Classes.Sum(c => c.Athletes.Count),
            byWorkbook.Project.Classes.Sum(c => c.Athletes.Count));
        Assert.Equal(
            byPath.Project.Days.SelectMany(d => d.Slots).Select(s => s.EventName).ToList(),
            byWorkbook.Project.Days.SelectMany(d => d.Slots).Select(s => s.EventName).ToList());
        Assert.Equal(
            byPath.Project.Days.SelectMany(d => d.Slots).Sum(s => s.Entries.Count),
            byWorkbook.Project.Days.SelectMany(d => d.Slots).Sum(s => s.Entries.Count));
    }

    // ---------- fixtures ----------

    /// <summary>
    /// 把磁盘上的文件改成一段无效内容。因为打开的句柄是 <c>FileShare.ReadWrite</c>，
    /// 这里可以原地覆写；已经读进内存的 <see cref="XLWorkbook"/> 不受影响，
    /// 而任何按路径重开的实现都会读失败。
    /// </summary>
    private static void Corrupt(string path)
    {
        using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
        stream.Write("not a workbook"u8);
    }

    /// <summary>两个班、一个比赛日五个时段的合成秩序册；姓名均为虚构。</summary>
    private string CreateOrderBook(string stem = "示例中学秩序册")
    {
        var path = Path.Combine(_root, stem + ".xlsx");
        using var workbook = new XLWorkbook();
        WriteRoster(workbook.AddWorksheet("代表队名单"));
        WriteSchedule(workbook.AddWorksheet("竞赛日程"));
        WriteGrouping(workbook.AddWorksheet("项目分组表"));
        workbook.SaveAs(path);
        return path;
    }

    private static void WriteRoster(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "代表队名单";

        sheet.Cell(4, 1).Value = "七年级 1班 (3)人";
        sheet.Cell(5, 1).Value = "男子";
        sheet.Cell(5, 2).Value = "0001";
        sheet.Cell(5, 3).Value = "陈屿";
        sheet.Cell(5, 4).Value = "0002";
        sheet.Cell(5, 5).Value = "许砚";
        sheet.Cell(6, 1).Value = "女子";
        sheet.Cell(6, 2).Value = "0003";
        sheet.Cell(6, 3).Value = "苏晚晴";

        sheet.Cell(9, 1).Value = "七年级 2班 (1)人";
        sheet.Cell(10, 1).Value = "女子";
        sheet.Cell(10, 2).Value = "0023";
        sheet.Cell(10, 3).Value = "林见夏";
    }

    private static void WriteSchedule(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "示例中学田径运动会";
        sheet.Cell(4, 1).Value = "2026-11-11  上午  径赛";
        WriteScheduleHeader(sheet, 6);
        WriteScheduleRow(sheet, 7, "女子", "100米", "预赛", 2, "11:00");
        WriteScheduleRow(sheet, 8, "男子", "100米", "预赛", 3, "11:06");
        WriteScheduleRow(sheet, 9, "男子", "200米", "预赛", 2, "11:12");

        sheet.Cell(12, 1).Value = "2026-11-11  下午  田赛";
        WriteScheduleHeader(sheet, 14);
        WriteScheduleRow(sheet, 15, "女子", "跳高", "决赛", 1, "14:30");
        WriteScheduleRow(sheet, 16, "混合", "4x100米接力", "决赛", 2, "14:40");
    }

    private static void WriteScheduleHeader(IXLWorksheet sheet, int row)
    {
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];
        for (var i = 0; i < heads.Length; i++) sheet.Cell(row, 1 + i).Value = heads[i];
    }

    private static void WriteScheduleRow(
        IXLWorksheet sheet, int row, string gender, string eventName, string stage, int count, string time)
    {
        sheet.Cell(row, 2).Value = "七年级";
        sheet.Cell(row, 3).Value = gender;
        sheet.Cell(row, 4).Value = eventName;
        sheet.Cell(row, 5).Value = stage;
        sheet.Cell(row, 6).Value = count;
        sheet.Cell(row, 7).Value = 1;
        sheet.Cell(row, 8).Value = 7;
        sheet.Cell(row, 9).Value = time;
    }

    private static void WriteGrouping(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "项目分组表";

        // 女子 100 米：两个班各一人。
        WriteIndividualBlock(sheet, 4, "女子100米", "预赛",
            ("0003", "苏晚晴", "1班"), ("0023", "林见夏", "2班"));

        // 4x100 接力：按班列队，不点名队员。
        sheet.Cell(12, 1).Value = "七年级";
        sheet.Cell(12, 3).Value = "混合4x100米接力";
        sheet.Cell(12, 6).Value = "决赛";
        sheet.Cell(12, 7).Value = "2队/1组/取 7 名";
        sheet.Cell(13, 1).Value = "第 1 组";
        sheet.Cell(13, 2).Value = 1;
        sheet.Cell(13, 3).Value = 2;
        sheet.Cell(14, 2).Value = "1班";
        sheet.Cell(14, 3).Value = "2班";
    }

    /// <summary>个人项目块：块头 / 组号与道次 / 号码 / 姓名 / 班级（与 <c>ScheduleWorkbookImporter</c> 契约一致）。</summary>
    private static void WriteIndividualBlock(
        IXLWorksheet sheet, int startRow, string eventName, string stage,
        params (string Bib, string Name, string ClassName)[] entries)
    {
        sheet.Cell(startRow, 1).Value = "七年级";
        sheet.Cell(startRow, 3).Value = eventName;
        sheet.Cell(startRow, 6).Value = stage;
        sheet.Cell(startRow, 7).Value = $"{entries.Length}人/1组/取 7 名";

        // 组号行：A 列写「第 1 组」，B 列起是道次——缺了这行整块都会被跳过。
        sheet.Cell(startRow + 1, 1).Value = "第 1 组";
        for (var i = 0; i < entries.Length; i++)
        {
            var column = 2 + i;
            sheet.Cell(startRow + 1, column).Value = i + 1;
            sheet.Cell(startRow + 2, column).Value = entries[i].Bib;
            sheet.Cell(startRow + 3, column).Value = entries[i].Name;
            sheet.Cell(startRow + 4, column).Value = entries[i].ClassName;
        }
    }
}
