namespace TeachCraft.Core.Updating;

public enum InstallerAction
{
    /// <summary>运行安装包（静默、关闭占用、装完重启）。仅 Windows 安装包形态。</summary>
    RunSetup,

    /// <summary>不做任何自动动作，只把可执行的步骤告诉用户。</summary>
    ShowGuidance,
}

/// <summary>该走哪一种安装动作，以及具体参数/指引文案。</summary>
public sealed record InstallerPlan(
    InstallerAction Action,
    string? Arguments,
    bool WaitForPreviousProcessExit,
    bool RestartAfterwards,
    string? Guidance);

/// <summary>
/// 把「安装形态 + 已下载的包」翻译成一次可执行的动作。
///
/// 分级原则：只有 Windows 的 Inno 安装包能真正自动完成；
/// 绿色版、macOS、Linux 一律给指引——微软文档明确运行中的映像不能自我替换，
/// 而在 macOS 上替换 .app 还会牵涉 Gatekeeper。给确定可行的步骤，
/// 好过一个「有时成功」的替换器。
/// </summary>
public static class UpdateInstaller
{
    public const string SilentSetupArguments = "/SILENT /SP- /NOICONS /CLOSEAPPLICATIONS";

    public static InstallerPlan BuildPlan(UpdateProposal proposal, string downloadedPath)
    {
        return proposal.Kind switch
        {
            InstallKind.InnoInstall or InstallKind.InnoInstallLegacy => new InstallerPlan(
                InstallerAction.RunSetup,
                SilentSetupArguments,
                WaitForPreviousProcessExit: true,
                RestartAfterwards: true,
                Guidance: proposal.NeedsModeExplanation
                    ? "检测不到你当初装的是独立版还是依赖框架版，已为你选独立版（自带运行时，最稳）。"
                    : null),

            InstallKind.PortableZip => new InstallerPlan(
                InstallerAction.ShowGuidance,
                null,
                WaitForPreviousProcessExit: false,
                RestartAfterwards: false,
                Guidance: "已把新版本下载到：" + Line(downloadedPath) +
                          "请解压到另一个文件夹，试运行确认没问题后，再关掉当前程序替换旧文件。"),

            InstallKind.MacBundle => new InstallerPlan(
                InstallerAction.ShowGuidance,
                null,
                WaitForPreviousProcessExit: false,
                RestartAfterwards: false,
                Guidance: "已把新版本下载到：" + Line(downloadedPath) +
                          "打开镜像后把「知微」拖进「应用程序」覆盖旧版本即可。首次打开若提示无法验证，请按住 Control 点按图标选择「打开」；" +
                          "仍不行可在终端执行：xattr -dr com.apple.quarantine /Applications/TeachCraft.app"),

            InstallKind.MacBundlePortable => new InstallerPlan(
                InstallerAction.ShowGuidance,
                null,
                WaitForPreviousProcessExit: false,
                RestartAfterwards: false,
                Guidance: "已把新版本下载到：" + Line(downloadedPath) +
                          "解压后依然是便携应用：先关掉当前程序，再把新的「知微」放到原来的位置替换。首次打开若被拦截，" +
                          "请按住 Control 点按图标选择「打开」。"),

            InstallKind.LinuxPortable => new InstallerPlan(
                InstallerAction.ShowGuidance,
                null,
                WaitForPreviousProcessExit: false,
                RestartAfterwards: false,
                Guidance: "已把新版本下载到：" + Line(downloadedPath) +
                          "请解压后执行 chmod +x TeachCraft && ./TeachCraft。压缩包无法保留可执行权限，这一步必须手动做。"),

            _ => new InstallerPlan(
                InstallerAction.ShowGuidance,
                null,
                WaitForPreviousProcessExit: false,
                RestartAfterwards: false,
                Guidance: "认不出当前程序是用哪种方式装的，请到发布页手动选择适合的安装包。"),
        };
    }

    private static string Line(string path) => $"{path}。";
}

/// <summary>等待「旧进程退出」的可注入探针，便于测试不起真实进程。</summary>
public static class ProcessWaiter
{
    /// <summary>轮询直到旧进程退出或超时。返回 true 表示已退出。</summary>
    public static async Task<bool> WaitAsync(
        Func<int, bool> hasExited,
        int processId = 0,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasExited(processId)) return true;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return hasExited(processId);
    }
}

/// <summary>命名互斥锁的抽象，便于测试。</summary>
public interface ISingleInstanceProbe
{
    bool TryCreate(string name);
    void Release(string name);
}

/// <summary>
/// 应用级单实例守卫。没有它，「关闭 → 安装 → 重启」会与旧实例抢文件，
/// 甚至弹出两个窗口。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _name;
    private readonly ISingleInstanceProbe _probe;
    private bool _acquired;

    public SingleInstanceGuard(string name, ISingleInstanceProbe? probe = null)
    {
        _name = name;
        _probe = probe ?? new MutexProbe();
    }

    public bool TryAcquire()
    {
        if (_acquired) return true;
        _acquired = _probe.TryCreate(_name);
        return _acquired;
    }

    public void Release()
    {
        if (!_acquired) return;
        _probe.Release(_name);
        _acquired = false;
    }

    public void Dispose() => Release();

    private sealed class MutexProbe : ISingleInstanceProbe
    {
        private readonly Dictionary<string, Mutex> _held = new(StringComparer.OrdinalIgnoreCase);

        public bool TryCreate(string name)
        {
            var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            _held[name] = mutex;
            return true;
        }

        public void Release(string name)
        {
            if (!_held.TryGetValue(name, out var mutex)) return;
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            mutex.Dispose();
            _held.Remove(name);
        }
    }
}
