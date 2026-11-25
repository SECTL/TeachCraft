using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace TeachCraft.Core.Tools;

public static class ExamPaperLayoutPlanner
{
    public static IReadOnlyList<int?> BuildSlots(int pageCount, ExamPaperLayoutMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        if (mode == ExamPaperLayoutMode.TwoUp)
        {
            var slots = Enumerable.Range(0, pageCount).Select(index => (int?)index).ToList();
            if (slots.Count % 2 != 0) slots.Add(null);
            return slots;
        }

        var paddedCount = ((pageCount + 3) / 4) * 4;
        var bookletSlots = new List<int?>(paddedCount);
        for (var sheet = 0; sheet < paddedCount / 4; sheet++)
        {
            var order = new[]
            {
                paddedCount - 1 - sheet * 2,
                sheet * 2,
                sheet * 2 + 1,
                paddedCount - 2 - sheet * 2,
            };
            foreach (var index in order) bookletSlots.Add(index < pageCount ? index : null);
        }
        return bookletSlots;
    }

    public static XRect VisiblePageRect(PdfPage page, XRect slot)
    {
        var rotated = page.Rotate % 360 is 90 or 270;
        var sourceWidth = rotated ? page.Height.Point : page.Width.Point;
        var sourceHeight = rotated ? page.Width.Point : page.Height.Point;
        var scale = Math.Min(slot.Width / sourceWidth, slot.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new XRect(slot.X + (slot.Width - width) / 2, slot.Y + (slot.Height - height) / 2, width, height);
    }

    public static (int Width, int Height) RenderPixelSize(PdfPage page, XRect slot, int outputDpi)
    {
        var visible = VisiblePageRect(page, slot);
        var scale = visible.Width / (page.Rotate % 360 is 90 or 270 ? page.Height.Point : page.Width.Point);
        var dpi = outputDpi * scale;
        var rotated = page.Rotate % 360 is 90 or 270;
        var width = rotated ? page.Height.Point : page.Width.Point;
        var height = rotated ? page.Width.Point : page.Height.Point;
        return ((int)Math.Ceiling(width * dpi / 72), (int)Math.Ceiling(height * dpi / 72));
    }
}
