namespace TeachCraft.Core.Cloud;

/// <summary>
/// 云端状态的 JSON 文件读写助手。沿用 UpdateStateStore 的约定：
/// 原子写（先写 .tmp 再 File.Move 覆盖）、损坏即空、所有 IO 异常一律吞掉。
/// </summary>
internal static class CloudJsonFile
{
    public static void WriteAtomic(string path, string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                // overwrite 语义保证要么是旧的完整文件，要么是新的完整文件。
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { /* 临时文件删不掉也不影响结果。 */ }
            }
        }
        catch (Exception)
        {
            // 状态存不下来不影响本次操作，静默即可。
        }
    }

    public static string? ReadAllOrNull(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            // 文件损坏、权限不足都当作「没有状态」。
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉不影响本次操作。
        }
    }
}
