using Homework.Core;
using Homework.Core.Recognition;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace TeachCraft.Tests;

/// <summary>
/// 填涂模板输出的几何与格式断言。只验证尺寸 / 页数 / 边框像素 / 异常，
/// 不做视觉判断，不读取截图；更深的用例由后续 todo 追加。
/// </summary>
public sealed class FillInTemplateTests
{
    private static FillInTemplateOptions Options(int columns = 4) =>
        new("学号填涂卡", "八年级1班", columns);

    [Fact]
    public void RenderPng_DecodesToA4At300Dpi()
    {
        var png = FillInTemplateRenderer.RenderPng(Options());
        Assert.True(png.Length > 0);
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(2480, bitmap.Width);
        Assert.Equal(3508, bitmap.Height);
    }

    [Fact]
    public void RenderPdf_IsSingleA4Page()
    {
        var pdf = FillInTemplateRenderer.RenderPdf(Options());
        Assert.True(pdf.Length > 0);
        using var stream = new MemoryStream(pdf);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
        var mediaBox = document.Pages[0].MediaBox;
        Assert.Equal(0d, mediaBox.X1);
        Assert.Equal(0d, mediaBox.Y1);
        Assert.True(Math.Abs(mediaBox.Width - 595.28) <= 0.5,
            $"MediaBox width {mediaBox.Width} must be within 0.5 pt of A4 595.28.");
        Assert.True(Math.Abs(mediaBox.Height - 841.89) <= 0.5,
            $"MediaBox height {mediaBox.Height} must be within 0.5 pt of A4 841.89.");
    }

    [Fact]
    public void RenderPng_ColumnsFourUsesFormulaAndDarkBorder()
    {
        var png = FillInTemplateRenderer.RenderPng(Options(4));
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);

        const int columns = 4;
        const int rows = 10;
        var cellWidth = Math.Min(260, (2480 - 360 - 120) / columns);
        Assert.Equal(260, cellWidth);
        var gridWidth = 120 + columns * cellWidth;
        var gridLeft = (2480 - gridWidth) / 2;
        var cellsLeft = gridLeft + 120;
        var cellsRight = cellsLeft + columns * cellWidth;
        const int gridTop = 760;
        const int cellHeight = 120;
        var gridBottom = gridTop + rows * cellHeight;

        Assert.True(IsDark(bitmap.GetPixel(cellsLeft, gridTop + cellHeight / 2)), "左边框应为深色");
        Assert.True(IsDark(bitmap.GetPixel(cellsLeft + cellWidth / 2, gridTop)), "上边框应为深色");
        Assert.True(IsDark(bitmap.GetPixel(cellsRight - 1, gridTop + cellHeight / 2)), "右边框应为深色");
        Assert.True(IsDark(bitmap.GetPixel(cellsLeft + cellWidth / 2, gridBottom - 1)), "下边框应为深色");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void InvalidColumns_ThrowsHomeworkException(int columns)
    {
        var error = Assert.Throws<HomeworkException>(() => FillInTemplateRenderer.RenderPng(Options(columns)));
        Assert.Contains("列数", error.Message);
    }

    [Fact]
    public void MissingCjkFont_ThrowsReadableChineseException()
    {
        var error = Assert.Throws<HomeworkException>(() =>
            FillInTemplateRenderer.RenderPng(Options(), _ => null));
        Assert.Contains("中文字体", error.Message);
    }

    // ---- todo 15：更深的模板输出几何用例（additive，不改动上面的既有测试） ----

    [Fact]
    public void RenderPng_ColumnsTwelveUsesNarrowCellsAndIsCentered()
    {
        var png = FillInTemplateRenderer.RenderPng(Options(12));
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);

        const int columns = 12;
        var cellWidth = Math.Min(260, (2480 - 360 - 120) / columns); // min(260, 2000/12) = 166
        Assert.Equal(166, cellWidth);
        var cellsWidth = cellWidth * columns;
        var gridWidth = 120 + cellsWidth;
        var gridLeft = (2480 - gridWidth) / 2;
        var cellsLeft = gridLeft + 120;
        var cellsRight = cellsLeft + cellsWidth;
        const int gridTop = 760;
        const int cellHeight = 120;

        // 左右外框落在公式预测的位置；整张网格（含行标列）左右留白相等即水平居中。
        Assert.True(IsDark(bitmap.GetPixel(cellsLeft, gridTop + cellHeight / 2)), "左边框应为深色");
        Assert.True(IsDark(bitmap.GetPixel(cellsRight - 1, gridTop + cellHeight / 2)), "右边框应为深色");
        Assert.Equal(gridLeft, 2480 - cellsRight); // 左留白 == 右留白，即水平居中

        // 第一条内部分隔线应恰好落在格宽处，确认格宽 166 而非仅由公式回显。
        var scanY = gridTop + cellHeight - 15; // 行 0 内、避开行标文字
        Assert.True(HasGridLineNear(bitmap, cellsLeft + cellWidth, scanY),
            "第一条竖分隔线应位于 cellsLeft + 166");
    }

    [Fact]
    public void RenderPng_ColumnsOneUsesMaxCellWidth()
    {
        var png = FillInTemplateRenderer.RenderPng(Options(1));
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);

        const int columns = 1;
        var cellWidth = Math.Min(260, (2480 - 360 - 120) / columns); // min(260, 2000) = 260
        Assert.Equal(260, cellWidth);
        var cellsWidth = cellWidth * columns;
        var gridWidth = 120 + cellsWidth;
        var gridLeft = (2480 - gridWidth) / 2;
        var cellsLeft = gridLeft + 120;
        var cellsRight = cellsLeft + cellsWidth;
        const int gridTop = 760;
        const int cellHeight = 120;

        Assert.True(IsDark(bitmap.GetPixel(cellsLeft, gridTop + cellHeight / 2)), "左边框应为深色");
        Assert.True(IsDark(bitmap.GetPixel(cellsRight - 1, gridTop + cellHeight / 2)), "右边框应为深色");
        Assert.Equal(gridLeft, 2480 - cellsRight); // 水平居中
    }

    [Fact]
    public void RenderPng_Dpi150DecodesToA4At150Dpi()
    {
        var options = new FillInTemplateOptions("学号填涂卡", "八年级1班", 4, Dpi: 150);
        var png = FillInTemplateRenderer.RenderPng(options);
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(1240, bitmap.Width); // round(210/25.4*150)
        Assert.Equal(1754, bitmap.Height); // round(297/25.4*150)
    }

    [Fact]
    public void RenderPng_RowsFiveDrawsFiveRowsOnly()
    {
        var options = new FillInTemplateOptions("学号填涂卡", "八年级1班", 4, Rows: 5);
        var png = FillInTemplateRenderer.RenderPng(options);
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);

        const int columns = 4;
        const int rows = 5;
        var cellWidth = Math.Min(260, (2480 - 360 - 120) / columns);
        var cellsWidth = cellWidth * columns;
        var gridWidth = 120 + cellsWidth;
        var gridLeft = (2480 - gridWidth) / 2;
        var cellsLeft = gridLeft + 120;
        var cellsRight = cellsLeft + cellsWidth;
        const int gridTop = 760;
        const int cellHeight = 120;
        var gridBottom = gridTop + rows * cellHeight; // 760 + 5*120 = 1360，网格高 600

        // 右边框是一条从 gridTop 到 gridBottom 的连续深色竖线；扫描它确认网格底边落在 1360。
        var detectedBottom = LastDarkY(bitmap, cellsRight - 1, gridTop, gridTop + 12 * cellHeight);
        Assert.InRange(detectedBottom, gridBottom - 4, gridBottom + 4);

        // 行标 0–4 存在，第 5 行标不存在（行标列在底部说明文字左侧，不受其干扰）。
        var labelX = gridLeft + 120 / 2;
        for (var r = 0; r < rows; r++)
        {
            var cy = gridTop + (int)((r + 0.5) * cellHeight);
            Assert.True(HasDarkInBox(bitmap, labelX - 15, cy - 20, labelX + 15, cy + 20),
                $"行标 {r} 应存在");
        }
        var absentCy = gridTop + (int)((rows + 0.5) * cellHeight);
        Assert.False(HasDarkInBox(bitmap, labelX - 15, absentCy - 15, labelX + 15, absentCy + 15),
            "第 5 行标不应存在");
    }

    [Fact]
    public void ZeroRows_ThrowsHomeworkException()
    {
        var options = new FillInTemplateOptions("学号填涂卡", "八年级1班", 4, Rows: 0);
        var error = Assert.Throws<HomeworkException>(() => FillInTemplateRenderer.RenderPng(options));
        Assert.Contains("行数", error.Message);
    }

    [Fact]
    public void RenderPdf_ColumnsTwelveIsSingleA4Page()
    {
        var pdf = FillInTemplateRenderer.RenderPdf(Options(12));
        Assert.True(pdf.Length > 0);
        using var stream = new MemoryStream(pdf);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
        var mediaBox = document.Pages[0].MediaBox;
        Assert.True(Math.Abs(mediaBox.Width - 595.28) <= 0.5,
            $"MediaBox width {mediaBox.Width} must be within 0.5 pt of A4 595.28.");
        Assert.True(Math.Abs(mediaBox.Height - 841.89) <= 0.5,
            $"MediaBox height {mediaBox.Height} must be within 0.5 pt of A4 841.89.");
    }

    private static bool IsDark(SKColor color) =>
        (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000 < 100;

    private static int LastDarkY(SKBitmap bitmap, int x, int fromY, int toY)
    {
        var last = -1;
        for (var y = fromY; y <= toY && y < bitmap.Height; y++)
            if (IsDark(bitmap.GetPixel(x, y))) last = y;
        return last;
    }

    private static bool HasDarkInBox(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y <= y1 && y < bitmap.Height; y++)
            for (var x = x0; x <= x1 && x < bitmap.Width; x++)
                if (IsDark(bitmap.GetPixel(x, y))) return true;
        return false;
    }

    private static bool HasGridLineNear(SKBitmap bitmap, int centerX, int y, int radius = 3)
    {
        for (var x = centerX - radius; x <= centerX + radius; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red < 240 && c.Red > 100) return true; // 浅灰分隔线 (198,198,198)
        }
        return false;
    }
}
