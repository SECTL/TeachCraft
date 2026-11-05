using Microsoft.Win32;

namespace TeachCraft.Core.Updating;

/// <summary>
/// 读取 Inno 安装包写入的身份记录（见 installer/TeachCraft.iss 的 [Registry] 段）。
///
/// 键路径：Software\TeachCraft\Install-{mode}-{arch}
/// 值：Mode / Rid / InstallRoot / Version
///
/// **两个视图都读**。安装包写的是 HKCU64，但 Inno 在 PrivilegesRequired=lowest
/// 且 ArchitecturesInstallIn64BitMode 生效时的实际落点（原生视图 vs Wow6432Node）
/// 取决于 Inno 版本与架构分支，本项目无法离线验证。
/// 与其赌一个假设，不如把 64 位与 32 位视图都扫一遍再合并——
/// 代价是两次注册表打开，收益是「读不到安装记录」这类静默降级彻底消失。
/// </summary>
public static class InstallRecordReader
{
    private const string RootPath = @"Software\TeachCraft";

    /// <summary>读出本机所有安装记录。读不到（键不存在/无权限/非 Windows）一律返回空，不抛。</summary>
    public static IReadOnlyList<InstallRecord> ReadAll()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<InstallRecord>();

        var records = new List<InstallRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先读 64 位视图（安装包写的 HKCU64），再读 32 位视图补齐可能的 Wow6432Node 落点。
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            ReadFrom(view, records, seen);
        }

        return records;
    }

    private static void ReadFrom(RegistryView view, List<InstallRecord> records, HashSet<string> seen)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view).OpenSubKey(RootPath);
            if (root is null) return;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith("Install-", StringComparison.OrdinalIgnoreCase)) continue;

                using var sub = root.OpenSubKey(subKeyName);
                if (sub is null) continue;

                var mode = sub.GetValue("Mode") as string;
                var rid = sub.GetValue("Rid") as string;
                var installRoot = sub.GetValue("InstallRoot") as string;

                if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(installRoot)) continue;

                // 两个视图可能指向同一份数据，按「模式 + 安装根」去重。
                var key = $"{mode}|{installRoot}";
                if (!seen.Add(key)) continue;

                records.Add(new InstallRecord(mode, rid ?? string.Empty, installRoot));
            }
        }
        catch (Exception)
        {
            // 该视图读不了就跳过，不影响另一个视图。
        }
    }
}
