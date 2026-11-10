using ClosedXML.Excel;
using MeetRoster.Core.Export;
using MeetRoster.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 原表高亮的行为守卫（T14）。
/// <para>
/// T14 把块尾空行修剪从 <c>Row(endRow).CellsUsed()</c> 换成逐列检查。这类改动
/// 最容易悄悄改变「哪里算到块尾」——涂色范围多一行或少一行，肉眼几乎看不出来，
/// 但导出的文件已经不同了。这里把涂色结果完整取出来比对，钉住语义。
/// </para>
/// </summary>
public sealed class OriginalHighlightTests
{
    [Fact]
    public void HighlightPaintsClassBlock_AndStopsAtTrailingBlankRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HighlightGuard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "秩序册.xlsx");
            WriteOrderBookWithTrailingBlanks(source);

            var project = new MeetProject
            {
                Title = "示例中学田径运动会",
                SourcePath = source,
            };
            project.Classes.Add(new MeetClass { Name = "七年级1班", Group = "七年级" });
            project.Classes.Add(new MeetClass { Name = "七年级2班", Group = "七年级" });

            var target = Path.Combine(dir, "高亮.xlsx");
            var options = new MeetExportOptions { ClassName = "七年级1班" };
            MeetOriginalWorkbookHighlighter.Export(project, options, target);

            using var result = new XLWorkbook(target);
            var sheet = result.Worksheet("代表队名单");

            // 1 班的块（第 3 行起）应涂色；2 班的块不应涂色。
            Assert.Equal("FFFBE4D7", FillOf(sheet, 3, 1));   // 块头
            Assert.Equal("FFFBE4D7", FillOf(sheet, 4, 1));   // 男子行

            // 块之间插了 3 行全空行：它们不能被刷上色（这就是 T14 要守的语义）。
            var blankPainted = 0;
            for (var r = 5; r <= 7; r++)
                if (FillOf(sheet, r, 1) == "FFFBE4D7") blankPainted++;

            Assert.Equal(0, blankPainted);

            // 该班块之外的内容不应被涂色。
            Assert.NotEqual("FFFBE4D7", FillOf(sheet, 8, 1));   // 2 班块头

            // 高亮说明页必须存在。
            Assert.True(result.Worksheets.TryGetWorksheet("高亮说明", out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HighlightRefusesToOverwriteSource()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HighlightGuard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "秩序册.xlsx");
            WriteOrderBookWithTrailingBlanks(source);

            var project = new MeetProject { Title = "示例中学田径运动会", SourcePath = source };
            project.Classes.Add(new MeetClass { Name = "七年级1班", Group = "七年级" });

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeetOriginalWorkbookHighlighter.Export(
                    project, new MeetExportOptions { ClassName = "七年级1班" }, source));

            Assert.Contains("不能覆盖", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string FillOf(IXLWorksheet sheet, int row, int column) =>
        sheet.Cell(row, column).Style.Fill.BackgroundColor.ToString();

    /// <summary>两班块之间夹 3 行全空行，考验块尾修剪。</summary>
    private static void WriteOrderBookWithTrailingBlanks(string path)
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("代表队名单");

        sheet.Cell(1, 1).Value = "示例中学田径运动会";
        sheet.Cell(2, 1).Value = "代表队名单";

        // 1 班块：第 3、4 行
        sheet.Cell(3, 1).Value = "七年级 1班";
        sheet.Cell(4, 1).Value = "男子";
        sheet.Cell(4, 2).Value = 4001;
        sheet.Cell(4, 3).Value = "商砚清";
        sheet.Cell(4, 4).Value = "洛听雪";

        // 3 行全空
        // 5, 6, 7 留空

        // 2 班块：第 8 行起
        sheet.Cell(8, 1).Value = "七年级 2班";
        sheet.Cell(9, 1).Value = "男子";
        sheet.Cell(9, 2).Value = 4002;
        sheet.Cell(9, 3).Value = "岑惊蛰";

        wb.SaveAs(path);
    }
}
