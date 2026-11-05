using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 安装形态识别。
///
/// 最要紧的一条：老安装包（Release A 之前装的）没有身份记录，
/// 如果把它当成都可携版，程序会在一个带卸载器的安装目录旁边建议「解压替换」——
/// 这是必须堵住的坏流程。
///
/// 全部通过注入的探针验证，不读真实注册表、不碰真实文件系统布局。
/// </summary>
public sealed class InstallKindDetectorTests
{
    private static InstallKind Detect(string? processPath, InstallRecord? record, bool isMac = false, bool isLinux = false)
        => InstallKindDetector.Detect(
            processPath,
            _ => record,
            os: new OsProbe(isMac, isLinux));

    [Fact]
    public void RegistryRecord_WinsOverPathHeuristics()
    {
        var record = new InstallRecord("sc", "win-x64", @"C:\Users\x\AppData\Local\Programs\TeachCraft");

        var kind = Detect(@"C:\Somewhere\TeachCraft.exe", record);

        Assert.Equal(InstallKind.InnoInstall, kind);
    }

    [Fact]
    public void OldInnoInstall_WithoutRecord_IsLegacyNotPortable()
    {
        // Release A 之前装的安装包没有任何身份记录，但路径在 %LOCALAPPDATA%\Programs 下。
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "TeachCraft", "TeachCraft.exe");

        var kind = Detect(path, record: null);

        Assert.Equal(InstallKind.InnoInstallLegacy, kind);
    }

    [Fact]
    public void OldInnoInstall_UnderProgramFiles_IsLegacy()
    {
        var kind = Detect(@"C:\Program Files\TeachCraft\TeachCraft.exe", record: null);

        Assert.Equal(InstallKind.InnoInstallLegacy, kind);
    }

    [Fact]
    public void ZipUnzippedIntoPrograms_IsConservativelyTreatedAsLegacy()
    {
        // 与上一条不可区分（用户把绿色包解压进了 Programs），
        // 接受这个保守判定：宁可走安装包升级路径，也不要建议替换。
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "TeachCraft", "TeachCraft.exe");

        Assert.Equal(InstallKind.InnoInstallLegacy, Detect(path, record: null));
    }

    [Fact]
    public void ZipElsewhere_IsPortable()
    {
        var kind = Detect(@"D:\教具\TeachCraft\TeachCraft.exe", record: null);

        Assert.Equal(InstallKind.PortableZip, kind);
    }

    [Fact]
    public void MacBundle_InApplications_IsMacBundle()
    {
        var kind = Detect("/Applications/TeachCraft.app/Contents/MacOS/TeachCraft", record: null, isMac: true);

        Assert.Equal(InstallKind.MacBundle, kind);
    }

    [Fact]
    public void MacBundle_Elsewhere_IsPortable()
    {
        var kind = Detect("/Users/x/下载/TeachCraft.app/Contents/MacOS/TeachCraft", record: null, isMac: true);

        Assert.Equal(InstallKind.MacBundlePortable, kind);
    }

    [Fact]
    public void Linux_IsLinuxPortable()
    {
        var kind = Detect("/home/x/TeachCraft/TeachCraft", record: null, isLinux: true);

        Assert.Equal(InstallKind.LinuxPortable, kind);
    }

    [Fact]
    public void NullProcessPath_IsUnknown()
    {
        Assert.Equal(InstallKind.Unknown, Detect(null, record: null));
    }

    [Fact]
    public void RegistryReadFailure_DegradesToPathInferenceWithoutThrowing()
    {
        var kind = InstallKindDetector.Detect(
            @"D:\教具\TeachCraft\TeachCraft.exe",
            _ => throw new System.Security.SecurityException("拒绝访问"),
            os: new OsProbe(false, false));

        Assert.Equal(InstallKind.PortableZip, kind);
    }

    [Fact]
    public void InstallRoot_IsDirectoryOfProcessPath()
    {
        var path = @"D:\教具\TeachCraft\TeachCraft.exe";
        var info = InstallKindDetector.Probe(path, _ => null, new OsProbe(false, false));

        Assert.Equal(@"D:\教具\TeachCraft", info.InstallRoot);
        Assert.Equal(InstallKind.PortableZip, info.Kind);
    }

    [Fact]
    public void MultipleRecords_ArbitratesByInstallRoot()
    {
        // 同一用户先后装过 sc 与 fdd，靠安装根与当前 exe 目录匹配挑出当前这一支。
        var records = new[]
        {
            new InstallRecord("sc", "win-x64", @"C:\Users\x\AppData\Local\Programs\TeachCraft"),
            new InstallRecord("fdd", "win-x64", @"C:\Program Files\TeachCraft"),
        };

        var info = InstallKindDetector.Probe(
            @"C:\Program Files\TeachCraft\TeachCraft.exe",
            _ => null,
            new OsProbe(false, false),
            records);

        Assert.Equal(InstallKind.InnoInstall, info.Kind);
        Assert.Equal("fdd", info.Mode);
    }
}
