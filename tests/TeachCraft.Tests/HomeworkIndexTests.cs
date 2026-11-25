using ClosedXML.Excel;
using Homework.Core.Export;

namespace TeachCraft.Tests;

// ClosedXML 0.105 没有 GetInt()，这里补一个测试用的读取助手。
internal static class ClosedXmlCellReadExtensions
{
    public static int GetInt(this IXLCell cell) => (int)cell.GetDouble();
}

/// <summary>索引工作簿：三张表的表头、行数、关键值与样式。全部基于合成夹具。</summary>
public sealed class HomeworkIndexTests
{
    private static string WriteIndex(HomeworkExportFixture f)
    {
        var plan = f.Plan(HomeworkExportMode.ArchiveWithIndex);
        var path = plan.IndexPath!;
        HomeworkIndexWorkbook.Write(path, f.Batch, plan);
        Assert.True(File.Exists(path));
        return path;
    }

    [Fact]
    public void Write_CreatesThreeSheets_WithExactHeadersAndBold()
    {
        using var f = new HomeworkExportFixture();
        var path = WriteIndex(f);

        using var wb = new XLWorkbook(path);
        Assert.Equal(3, wb.Worksheets.Count);
        Assert.Equal(new[] { "汇总", "明细", "未匹配" }, wb.Worksheets.Select(w => w.Name).ToArray());

        var summary = wb.Worksheet("汇总");
        Assert.Equal(
            new[] { "班级", "学号", "姓名", "页数", "输出目录" },
            Enumerable.Range(1, 5).Select(c => summary.Cell(1, c).GetString()).ToArray());
        Assert.True(summary.Cell(1, 1).Style.Font.Bold);

        var detail = wb.Worksheet("明细");
        Assert.Equal(
            new[] { "序号", "班级", "学号", "姓名", "页码", "原文件名", "原页码", "输出文件" },
            Enumerable.Range(1, 8).Select(c => detail.Cell(1, c).GetString()).ToArray());
        Assert.True(detail.Cell(1, 1).Style.Font.Bold);

        var unmatched = wb.Worksheet("未匹配");
        Assert.Equal(
            new[] { "序号", "未知标签", "原文件名", "原页码" },
            Enumerable.Range(1, 4).Select(c => unmatched.Cell(1, c).GetString()).ToArray());
        Assert.True(unmatched.Cell(1, 1).Style.Font.Bold);
    }

    [Fact]
    public void Write_SummaryRows_OnePerStudentPlusTotal()
    {
        using var f = new HomeworkExportFixture();
        var path = WriteIndex(f);

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("汇总");
        // 1 表头 + 3 名学生 + 1 合计
        Assert.Equal(5, ws.LastRowUsed()!.RowNumber());

        Assert.Equal("八年级1班", ws.Cell(2, 1).GetString());
        Assert.Equal("01", ws.Cell(2, 2).GetString());
        Assert.Equal("苏晚晴", ws.Cell(2, 3).GetString());
        Assert.Equal(2, ws.Cell(2, 4).GetInt()); // S1 有 2 页
        Assert.Equal(Path.Combine(f.OutputDir, "八年级1班"), ws.Cell(2, 5).GetString());

        Assert.Equal("陈屿", ws.Cell(3, 3).GetString());
        Assert.Equal(1, ws.Cell(3, 4).GetInt());

        Assert.Equal("八年级2班", ws.Cell(4, 1).GetString());
        Assert.Equal("许砚", ws.Cell(4, 3).GetString());
        Assert.Equal(1, ws.Cell(4, 4).GetInt());

        Assert.Equal("合计", ws.Cell(5, 1).GetString());
        Assert.Equal(4, ws.Cell(5, 4).GetInt()); // 总页数
    }

    [Fact]
    public void Write_DetailRows_OnePerPageEntry_WithPageNumbersAndOriginals()
    {
        using var f = new HomeworkExportFixture();
        var path = WriteIndex(f);

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("明细");
        // 1 表头 + 4 条单页条目
        Assert.Equal(5, ws.LastRowUsed()!.RowNumber());

        // 计划条目顺序：S1 的页（按 Index）→ S2 → S3。
        // 第 1 条：S1 第 1 页（图片 a.png）
        Assert.Equal(1, ws.Cell(2, 1).GetInt());
        Assert.Equal("苏晚晴", ws.Cell(2, 4).GetString());
        Assert.Equal(1, ws.Cell(2, 5).GetInt());
        Assert.Equal("a.png", ws.Cell(2, 6).GetString());
        Assert.Equal(1, ws.Cell(2, 7).GetInt());
        Assert.Equal(
            Path.Combine(f.OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png"),
            ws.Cell(2, 8).GetString());

        // 第 2 条：S1 第 2 页（PDF 第 1 页）
        Assert.Equal("苏晚晴", ws.Cell(3, 4).GetString());
        Assert.Equal(2, ws.Cell(3, 5).GetInt());
        Assert.Equal("two.pdf", ws.Cell(3, 6).GetString());
        Assert.Equal(1, ws.Cell(3, 7).GetInt());

        // 第 3 条：S2 第 1 页（图片 b.png）
        Assert.Equal("陈屿", ws.Cell(4, 4).GetString());
        Assert.Equal(1, ws.Cell(4, 5).GetInt());
        Assert.Equal("b.png", ws.Cell(4, 6).GetString());

        // 第 4 条：S3 第 1 页（PDF 第 2 页）
        Assert.Equal("许砚", ws.Cell(5, 4).GetString());
        Assert.Equal(1, ws.Cell(5, 5).GetInt());
        Assert.Equal("two.pdf", ws.Cell(5, 6).GetString());
        Assert.Equal(2, ws.Cell(5, 7).GetInt());
    }

    [Fact]
    public void Write_UnmatchedSheet_HasOnlyHeader_WhenAllMatched()
    {
        using var f = new HomeworkExportFixture();
        var path = WriteIndex(f);

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("未匹配");
        Assert.Equal(1, ws.LastRowUsed()!.RowNumber());
    }
}
