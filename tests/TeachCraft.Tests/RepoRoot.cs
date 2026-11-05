namespace TeachCraft.Tests;

/// <summary>
/// 定位仓库根目录。测试程序集在 tests/&lt;项目&gt;/bin/&lt;cfg&gt;/&lt;tfm&gt;/ 下，
/// 因此向上找到含有 TeachCraft.slnx 的那一级即为仓库根。
/// 沿用 tests/GradeStrip.Tests 的既有惯例（向上定位），改目录深度必须同步这里。
/// </summary>
public static class RepoRoot
{
    private static readonly Lazy<string> _root = new(Find);

    public static string Path => _root.Value;

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "TeachCraft.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能从测试程序集向上定位仓库根（找不到 TeachCraft.slnx）。");
    }

    /// <summary>读仓库内某个文件的全部文本；文件不存在时直接让测试失败，避免断言静默失效。</summary>
    public static string Read(string relativePath)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Assert.True(File.Exists(full), $"预期存在的文件不见了：{relativePath}（仓库根={Path}）");
        return File.ReadAllText(full);
    }

    /// <summary>读取目录下所有指定扩展名的文本内容，用于「整个目录不得出现某句话」这类断言。</summary>
    public static IEnumerable<(string Path, string Text)> ReadAll(string relativeDir, params string[] extensions)
    {
        var full = System.IO.Path.Combine(Path, relativeDir);
        Assert.True(Directory.Exists(full), $"预期存在的目录不见了：{relativeDir}");
        return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Where(p => extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(p => !p.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}")
                     && !p.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
            .Select(p => (p, File.ReadAllText(p)));
    }
}
