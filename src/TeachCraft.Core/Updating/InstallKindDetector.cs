namespace TeachCraft.Core.Updating;

/// <summary>Inno 安装期间写入的身份记录（见 installer/TeachCraft.iss 的 [Registry] 段）。</summary>
public sealed record InstallRecord(string Mode, string Rid, string InstallRoot);

/// <summary>探测结果。</summary>
public sealed record InstallInfo(InstallKind Kind, string? InstallRoot, string? Mode, string? Rid);

/// <summary>
/// 操作系统探针。抽成一层是为了让测试能在任意平台上验证 macOS/Linux 分支，
/// 也避免识别逻辑里出现散落的 OperatingSystem.IsXxx() 调用。
/// </summary>
public sealed record OsProbe(bool IsMac, bool IsLinux)
{
    public static OsProbe Current => new(OperatingSystem.IsMacOS(), OperatingSystem.IsLinux());
}

/// <summary>
/// 认出「我是谁装上去的」。
///
/// 判定顺序：
///   1. 注册表有身份记录 → InnoInstall（mode 已知）
///   2. 没有记录但在 %LOCALAPPDATA%\Programs 或 %ProgramFiles% 下 → InnoInstallLegacy
///      （所有 Release A 之前的安装包都是这个形态；当成便携版会建议用户去替换一个带卸载器的目录）
///   3. macOS：在 .app/Contents/MacOS 下 → MacBundle（系统级放置）或 MacBundlePortable
///   4. Windows 其余路径 → PortableZip
///   5. Linux → LinuxPortable
///
/// 安装根一律取 Environment.ProcessPath 的目录：
/// 单文件发布下 Assembly.Location 返回空串，不能依赖。
/// 探测全程只读，绝不写安装目录。
/// </summary>
public static class InstallKindDetector
{
    public static InstallInfo Probe(
        string? processPath,
        Func<string, InstallRecord?> readRecord,
        OsProbe os,
        IReadOnlyList<InstallRecord>? knownRecords = null)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return new InstallInfo(InstallKind.Unknown, null, null, null);

        var installRoot = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(installRoot))
            return new InstallInfo(InstallKind.Unknown, null, null, null);

        if (os.IsMac)
        {
            var isBundle = processPath.Contains(".app" + Path.DirectorySeparatorChar + "Contents" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           || processPath.Contains(".app/Contents/", StringComparison.Ordinal);

            if (isBundle)
            {
                var systemPlacement = processPath.StartsWith("/Applications/", StringComparison.Ordinal)
                                      || processPath.StartsWith(
                                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
                                          StringComparison.Ordinal);
                return new InstallInfo(
                    systemPlacement ? InstallKind.MacBundle : InstallKind.MacBundlePortable,
                    installRoot, null, null);
            }

            return new InstallInfo(InstallKind.MacBundlePortable, installRoot, null, null);
        }

        if (os.IsLinux)
            return new InstallInfo(InstallKind.LinuxPortable, installRoot, null, null);

        // Windows：先看注册表。读取失败（权限、键不存在）一律降级为路径推断，不抛。
        if (knownRecords is not null)
        {
            if (knownRecords.Count == 1)
            {
                var only = knownRecords[0];
                return new InstallInfo(InstallKind.InnoInstall, only.InstallRoot, only.Mode, only.Rid);
            }

            if (knownRecords.Count > 1)
            {
                // 同一用户装过多个 SKU：按安装根与当前 exe 目录匹配挑出这一支。
                var matched = knownRecords.FirstOrDefault(r =>
                    string.Equals(Normalize(r.InstallRoot), Normalize(installRoot), StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                    return new InstallInfo(InstallKind.InnoInstall, matched.InstallRoot, matched.Mode, matched.Rid);
            }
        }
        else
        {
            InstallRecord? record = null;
            try
            {
                record = readRecord(installRoot);
            }
            catch (Exception)
            {
                // 读不到注册表不影响后面按路径推断。
                record = null;
            }

            if (record is not null)
                return new InstallInfo(InstallKind.InnoInstall, record.InstallRoot, record.Mode, record.Rid);
        }

        // 没有身份记录：若位置像是安装目录，判为 legacy 安装包。
        if (LooksLikeInstallDirectory(installRoot))
            return new InstallInfo(InstallKind.InnoInstallLegacy, installRoot, null, InferCurrentRid());

        return new InstallInfo(InstallKind.PortableZip, installRoot, null, InferCurrentRid());
    }

    /// <summary>
    /// 从运行时的进程架构推出 RID。
    ///
    /// 这是「我装的是哪一支」里唯一不依赖 CI 注入的部分：
    /// 架构在运行时是确定的，而 sc/fdd 只有安装包的身份记录才知道。
    /// 没有它，`UpdateAssetSelector` 拿不到 rid，资产永远选不出来——
    /// 这正是之前真实进程里 rid 为 null 的根因。
    /// </summary>
    public static string InferCurrentRid()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

        var archName = arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (archName is null) return string.Empty;

        if (OperatingSystem.IsWindows()) return $"win-{archName}";
        if (OperatingSystem.IsMacOS()) return $"osx-{archName}";
        if (OperatingSystem.IsLinux()) return $"linux-{archName}";

        return string.Empty;
    }

    public static InstallKind Detect(
        string? processPath,
        Func<string, InstallRecord?> readRecord,
        OsProbe os,
        IReadOnlyList<InstallRecord>? knownRecords = null)
        => Probe(processPath, readRecord, os, knownRecords).Kind;

    /// <summary>进程自身的安装形态。</summary>
    public static InstallInfo Current(Func<string, InstallRecord?> readRecord, IReadOnlyList<InstallRecord>? knownRecords = null)
        => Probe(Environment.ProcessPath, readRecord, OsProbe.Current, knownRecords);

    /// <summary>进程自身的形态：直接接受已读出的注册表记录，不再回调读取。</summary>
    public static InstallInfo Current(IReadOnlyList<InstallRecord> knownRecords)
        => Probe(Environment.ProcessPath, _ => null, OsProbe.Current, knownRecords);

    private static bool LooksLikeInstallDirectory(string installRoot)
    {
        var normalized = Normalize(installRoot);

        var localPrograms = Normalize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));

        var programFilesX86 = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        var programFiles = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        return IsUnder(normalized, localPrograms)
               || (!string.IsNullOrEmpty(programFilesX86) && IsUnder(normalized, programFilesX86))
               || (!string.IsNullOrEmpty(programFiles) && IsUnder(normalized, programFiles));
    }

    private static bool IsUnder(string path, string root)
        => root.Length > 0
           && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && path.Length > root.Length;

    private static string Normalize(string path)
        => path.Replace('/', '\\').TrimEnd('\\');
}
