using System;
using System.IO;
using System.Threading.Tasks;
using GradeWeave.Core.Models;
using GradeWeave.Core.Project;

namespace GradeWeave.App;

/// <summary>
/// 工作簿导入的两条不变量（审计 P1-4 / T12），抽成纯函数是为了在没有 Avalonia
/// 应用与渲染后端的测试里直接覆盖——窗口本身不能 headless 构造。
/// </summary>
public static class WorkbookImportGuard
{
    /// <summary>
    /// 拖入导入前是否需要先确认切换工程。有当前工程、或本机还躺着上一份恢复文件时都要问：
    /// 导入成功会清掉恢复副本，拖一下就把崩溃前的成果抹掉，老师连后悔的机会都没有。
    /// 空状态（无工程、无恢复文件）天然不触发确认，走查通道不受影响。
    /// </summary>
    public static bool NeedsSwitchConfirm(bool hasProject, WeaveAutosaveInfo? recovery) =>
        hasProject || recovery is not null;

    /// <summary>拖入的文件是不是织序能吃的工作簿。</summary>
    public static bool IsSupportedWorkbook(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 拖入导入的前置关卡：格式 → 切换确认。只回答「要不要继续导入」，
    /// 真正的导入仍走窗口的 ImportProjectAsync（它负责忙碌状态与状态栏）。
    /// </summary>
    /// <param name="confirmSwitch">确认切换；返回 false 表示老师选择留在当前工程。</param>
    /// <param name="onStatus">把提示写进状态栏。</param>
    /// <returns>true 表示可以继续导入。</returns>
    public static async Task<bool> ConfirmDropAsync(
        string path,
        bool hasProject,
        WeaveAutosaveInfo? recovery,
        Func<string, bool, Task<bool>> confirmSwitch,
        Action<string> onStatus)
    {
        ArgumentNullException.ThrowIfNull(confirmSwitch);
        ArgumentNullException.ThrowIfNull(onStatus);

        if (!IsSupportedWorkbook(path))
        {
            onStatus("织序支持 .xlsx 与 .xls 工作簿，其他格式请先在 Excel 中另存。");
            return false;
        }

        // 有当前工程、或本机还躺着上一份恢复文件时都要问：导入成功会清掉恢复副本，
        // 拖一下就把崩溃前的成果抹掉，老师连后悔的机会都没有。
        return !NeedsSwitchConfirm(hasProject, recovery) ||
               await confirmSwitch("拖入导入", true);
    }

    /// <summary>
    /// 先解析、成功后才开始新会话（其中会清恢复文件）。
    /// 解析抛异常时异常直接穿出去，resetSession 不会执行——导入一份坏文件不该
    /// 连带清掉上一次的恢复副本。窗口的 ImportProjectAsync 必须走这里，不要自己排顺序。
    /// 解析在 Task.Run 里跑；resetSession 回到调用方的同步上下文（窗口里即 UI 线程），
    /// 因为它会写自动保存状态与恢复提示，不能在后台线程碰。
    /// </summary>
    public static async Task<WeaveImportResult> ImportThenResetSessionAsync(
        string path,
        Func<string, WeaveImportResult> import,
        Action resetSession)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(resetSession);

        var result = await Task.Run(() => import(path));
        resetSession();
        return result;
    }
}
