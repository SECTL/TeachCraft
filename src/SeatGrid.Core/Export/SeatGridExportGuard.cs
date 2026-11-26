using System;
using System.IO;

namespace SeatGrid.Core.Export;

/// <summary>Core 层座位表导出保护：任何导出入口均不得覆盖已导入的名单文件。</summary>
public static class SeatGridExportGuard
{
    public static void EnsureNotOverwritingSource(string outputPath, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new SeatGridException("导出路径不能覆盖已导入的名单文件，请换一个文件名。");
    }
}
