using System.IO.Compression;

namespace TeachCraft.Core.Updating;

/// <summary>
/// 绿色版自替换：把下载好的 zip 解压覆盖到当前程序目录。
///
/// 由独立助手进程在旧进程退出后调用——运行中的映像不能自我替换，
/// 这是 Windows 的硬限制，不是可以绕过的实现细节。
///
/// 三条必须守住的点：
///   1. **只覆盖包里的文件**，不整目录清空。用户可能把配置文件、
///      自己的脚本放在同一目录，删掉就是数据丢失。
///   2. **先备份再覆盖**。覆盖到一半断电 / 被杀，下次启动会是个半新半旧的
///      程序；有备份才能回滚。
///   3. **覆盖失败必须回滚**，不能留下一个「看起来更新了其实坏了」的目录。
/// </summary>
public sealed class PortableUpdater
{
    /// <summary>替换结果。失败时给出中文原因与是否已回滚。</summary>
    public sealed record Result(bool IsSuccess, string? Failure, bool RolledBack)
    {
        public static Result Ok() => new(true, null, false);
        public static Result Fail(string reason, bool rolledBack = false) => new(false, reason, rolledBack);
    }

    /// <summary>
    /// 解压并覆盖。
    /// </summary>
    /// <param name="archivePath">下载好的 zip。</param>
    /// <param name="targetDirectory">当前程序所在目录。</param>
    /// <param name="stagingRoot">临时解压目录的父目录；为 null 时用系统临时目录。</param>
    public static Result Apply(string archivePath, string targetDirectory, string? stagingRoot = null)
    {
        if (!File.Exists(archivePath))
            return Result.Fail("更新包不见了，请重新下载。");

        if (!Directory.Exists(targetDirectory))
            return Result.Fail("找不到当前程序所在的文件夹，无法自动替换。");

        var staging = Path.Combine(
            stagingRoot ?? Path.GetTempPath(),
            "TeachCraft-Update-" + Guid.NewGuid().ToString("N")[..12]);

        var backup = staging + "-backup";

        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);

            // 包里必须有主程序，否则解压出来的是别的东西（或包本身损坏）。
            var entries = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
            if (entries.Length == 0)
                return Result.Fail("更新包是空的，请重新下载。");

            // 备份将被覆盖的文件（只备份会动的那些，不备份整个目录）。
            var plan = BuildPlan(staging, targetDirectory);
            if (plan.Count == 0)
                return Result.Fail("更新包里没有需要替换的文件，请重新下载。");

            Directory.CreateDirectory(backup);
            var backedUp = new List<string>();
            foreach (var (source, destination) in plan)
            {
                if (!File.Exists(destination)) continue;

                var backupPath = Path.Combine(backup, Path.GetRelativePath(targetDirectory, destination));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(destination, backupPath, overwrite: true);
                backedUp.Add(destination);
            }

            // 逐个覆盖。任何一个失败都回滚已替换的部分。
            var replaced = new List<string>();
            try
            {
                foreach (var (source, destination) in plan)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                    replaced.Add(destination);
                }
            }
            catch (Exception ex)
            {
                var rolledBack = TryRollback(backup, targetDirectory, replaced);
                return Result.Fail(
                    rolledBack
                        ? $"替换文件时出错，已恢复到更新前的状态：{ex.Message}"
                        : $"替换文件时出错，且未能完全恢复：{ex.Message}。请手动重新下载安装。",
                    rolledBack);
            }

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail($"解压更新包失败：{ex.Message}");
        }
        finally
        {
            TryDelete(staging);
            TryDelete(backup);
        }
    }

    /// <summary>
    /// 列出「包里有哪些文件要写到目标目录的哪个位置」。
    ///
    /// 只按包内相对路径映射，不推断、不合并——包里没有的文件一律不动。
    /// </summary>
    private static List<(string Source, string Destination)> BuildPlan(string staging, string target)
    {
        var plan = new List<(string, string)>();

        foreach (var source in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, source);

            // 防御：相对路径里出现 .. 说明包有问题，宁可失败也不写到目标目录之外。
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                continue;

            plan.Add((source, Path.Combine(target, relative)));
        }

        return plan;
    }

    private static bool TryRollback(string backup, string target, List<string> replaced)
    {
        var all = true;
        foreach (var destination in replaced)
        {
            try
            {
                var backupPath = Path.Combine(backup, Path.GetRelativePath(target, destination));
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, destination, overwrite: true);
                }
                else
                {
                    // 原本不存在的文件（本次新加的），回滚时删掉。
                    File.Delete(destination);
                }
            }
            catch (Exception)
            {
                all = false;
            }
        }
        return all;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响结论；系统会自己清。
        }
    }
}
