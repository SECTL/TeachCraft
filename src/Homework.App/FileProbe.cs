using Homework.Core.Export;

namespace Homework.App;

/// <summary>真实文件系统探针：导出规划阶段用它判断目标是否已存在，不写盘。</summary>
public sealed class FileProbe : IFileProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
