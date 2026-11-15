using System;
using System.Collections.Generic;
using System.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SeatGrid.Core.Exam;

/// <summary>
/// 桌贴 PDF：每页一张高分辨率 PNG 铺进 A4 纵向。
/// 与座位表 PDF 同一条管线——不改成矢量，避免整套中文字体内嵌。
/// </summary>
public static class DeskStickerPdfExporter
{
    public static string Export(
        DeskStickerDocument document, DeskStickerOptions options, string path)
    {
        if (document.Stickers.Count == 0)
            throw new SeatGridException("还没有考生，无法导出桌贴。");

        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        QuestPDF.Settings.License = LicenseType.Community;

        var pageCount = DeskStickerRenderer.PageCount(document.Stickers.Count, options);
        var images = new List<byte[]>(pageCount);
        for (var page = 0; page < pageCount; page++)
            images.Add(DeskStickerRenderer.Render(document, options, page));

        Document.Create(container =>
        {
            foreach (var image in images)
            {
                var png = image;
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(9, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.Content().Image(png).FitArea();
                });
            }
        }).GeneratePdf(target);

        return target;
    }
}
