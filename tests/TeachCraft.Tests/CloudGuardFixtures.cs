namespace TeachCraft.Tests;

/// <summary>
/// 云同步守护测试用的临时目录。每个实例独占一个 GUID 子目录，Dispose 时递归删除。
/// <para>
/// 说明：任务描述里写的是 <c>static class TempDir : IDisposable</c>，但 C# 的静态类
/// 不能实现接口、也不能有实例构造函数，因此这里落成 sealed 实例类，语义不变。
/// </para>
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "teachcraft-cloud-guards", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* 清理失败不影响结论 */ }
    }
}

/// <summary>
/// 云同步层的结构扫描助手。
/// <para>
/// 路径定位直接复用 <see cref="RepoRoot"/>：它从测试程序集向上找到含 TeachCraft.slnx
/// 的那一级作为仓库根，目录深度改了只需改 RepoRoot 一处，不在这里另写一套
/// AppContext.BaseDirectory 向上回溯的逻辑。
/// </para>
/// </summary>
internal static class CloudSourceScan
{
    private static readonly string[] CloudDirs =
    [
        System.IO.Path.Combine("src", "TeachCraft.Core", "Cloud"),
        System.IO.Path.Combine("src", "SeatGrid.Core", "Cloud"),
    ];

    /// <summary>两个云同步目录下的全部 .cs 文本（跳过 bin/obj，沿用 RepoRoot.ReadAll 的约定）。</summary>
    public static IReadOnlyList<(string Path, string Text)> AllCloudSourceFiles()
        => CloudDirs.SelectMany(d => RepoRoot.ReadAll(d, ".cs")).ToList();
}
