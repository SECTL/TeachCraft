using System.IO.Compression;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 绿色版自替换。
///
/// 这里锁住的是「替换会不会弄坏用户的东西」——自动替换比给指引风险高得多，
/// 写错一个文件就是数据丢失，所以每条约束都要有测试。
/// </summary>
public sealed class PortableUpdaterTests : IDisposable
{
    private readonly string _root;

    public PortableUpdaterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "Portable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>造一个绿色版目录：exe + 字体资源 + 用户自己放的文件。</summary>
    private string MakeInstallDir(params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(_root, "install");
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return dir;
    }

    /// <summary>造一个更新包。</summary>
    private string MakeArchive(params (string Name, string Content)[] files)
    {
        var path = Path.Combine(_root, "update.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private string StagingRoot()
    {
        var dir = Path.Combine(_root, "staging");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Apply_ReplacesProgramFiles()
    {
        var install = MakeInstallDir(("TeachCraft.exe", "old"), ("QuestPDF.Fonts.Lato.br", "oldfont"));
        var archive = MakeArchive(("TeachCraft.exe", "new"), ("QuestPDF.Fonts.Lato.br", "newfont"));

        var result = PortableUpdater.Apply(archive, install, StagingRoot());

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "TeachCraft.exe")));
        Assert.Equal("newfont", File.ReadAllText(Path.Combine(install, "QuestPDF.Fonts.Lato.br")));
    }

    [Fact]
    public void Apply_DoesNotTouchFilesNotInArchive()
    {
        // 最要紧的一条：用户可能把配置、自己的脚本放在同一目录。
        // 整目录清空再解压会把它们删掉——那是数据丢失，不是「更新」。
        var install = MakeInstallDir(
            ("TeachCraft.exe", "old"),
            ("我的配置.txt", "用户自己放的东西"),
            ("scripts/backup.bat", "用户自己的脚本"));
        var archive = MakeArchive(("TeachCraft.exe", "new"));

        var result = PortableUpdater.Apply(archive, install, StagingRoot());

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "TeachCraft.exe")));
        Assert.Equal("用户自己放的东西", File.ReadAllText(Path.Combine(install, "我的配置.txt")));
        Assert.Equal("用户自己的脚本", File.ReadAllText(Path.Combine(install, "scripts", "backup.bat")));
    }

    [Fact]
    public void Apply_AddsNewFilesFromArchive()
    {
        // 新版本可能带来新文件（比如新的资源），这些也要到位。
        var install = MakeInstallDir(("TeachCraft.exe", "old"));
        var archive = MakeArchive(("TeachCraft.exe", "new"), ("new-resource.dat", "hello"));

        var result = PortableUpdater.Apply(archive, install, StagingRoot());

        Assert.True(result.IsSuccess, result.Failure);
        Assert.True(File.Exists(Path.Combine(install, "new-resource.dat")));
    }

    [Fact]
    public void Apply_HandlesSubdirectories()
    {
        var install = MakeInstallDir(("TeachCraft.exe", "old"));
        var archive = MakeArchive(("TeachCraft.exe", "new"), ("sub/deep/file.txt", "nested"));

        var result = PortableUpdater.Apply(archive, install, StagingRoot());

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal("nested", File.ReadAllText(Path.Combine(install, "sub", "deep", "file.txt")));
    }

    [Fact]
    public void Apply_FailsWhenArchiveMissing()
    {
        var install = MakeInstallDir(("TeachCraft.exe", "old"));

        var result = PortableUpdater.Apply(Path.Combine(_root, "nope.zip"), install, StagingRoot());

        Assert.False(result.IsSuccess);
        Assert.Contains("重新下载", result.Failure);
        // 原程序必须原封不动。
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "TeachCraft.exe")));
    }

    [Fact]
    public void Apply_FailsWhenInstallDirectoryMissing()
    {
        var archive = MakeArchive(("TeachCraft.exe", "new"));

        var result = PortableUpdater.Apply(archive, Path.Combine(_root, "nope"), StagingRoot());

        Assert.False(result.IsSuccess);
        Assert.Contains("找不到", result.Failure);
    }

    [Fact]
    public void Apply_FailsOnCorruptArchive()
    {
        var install = MakeInstallDir(("TeachCraft.exe", "old"));
        var bad = Path.Combine(_root, "bad.zip");
        File.WriteAllText(bad, "这不是一个 zip 文件");

        var result = PortableUpdater.Apply(bad, install, StagingRoot());

        Assert.False(result.IsSuccess);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "TeachCraft.exe")));
    }

    [Fact]
    public void Apply_FailsOnEmptyArchive()
    {
        var install = MakeInstallDir(("TeachCraft.exe", "old"));
        var empty = MakeArchive();

        var result = PortableUpdater.Apply(empty, install, StagingRoot());

        Assert.False(result.IsSuccess);
        Assert.Contains("空", result.Failure);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "TeachCraft.exe")));
    }

    [Fact]
    public void Apply_CleansUpStagingDirectory()
    {
        var install = MakeInstallDir(("TeachCraft.exe", "old"));
        var archive = MakeArchive(("TeachCraft.exe", "new"));
        var staging = StagingRoot();

        PortableUpdater.Apply(archive, install, staging);

        // 临时解压目录不能留在用户机器上——一次更新留下几十 MB 没人会去清。
        Assert.Empty(Directory.GetDirectories(staging));
    }

    [Fact]
    public void BuildPlan_RejectsPathTraversal()
    {
        // 包里出现 .. 说明这个包有问题；宁可失败也不能写到目标目录之外。
        // 这里用真实 zip 构造一个越界条目。
        var install = MakeInstallDir(("TeachCraft.exe", "old"));
        var archive = Path.Combine(_root, "evil.zip");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escaped.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("escaped");
        }

        var result = PortableUpdater.Apply(archive, install, StagingRoot());

        // 越界条目被跳过；如果包里只剩它，就是「没有需要替换的文件」。
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")), "绝不能写到目标目录之外");
    }
}
