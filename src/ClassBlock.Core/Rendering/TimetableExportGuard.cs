using System;
using System.IO;

namespace ClassBlock.Core.Rendering;

/// <summary>Core 层课表输出保护：任何导出入口均不得覆盖已导入的总表文件。</summary>
public static class TimetableExportGuard
{
    public static void EnsureNotOverwritingSource(string outputPath, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new ClassBlockException("导出路径不能覆盖已导入的总表文件，请换一个文件名。");
    }
}
