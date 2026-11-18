namespace TeachCraft.Core.Updating;

public enum InstallerAction
{
    /// <summary>运行安装包（静默、关闭占用、装完重启）。仅 Windows 安装包形态。</summary>
    RunSetup,

    /// <summary>
    /// 解压覆盖当前程序目录并重启。仅 Windows 绿色版。
    ///
    /// 绿色版此前只给一句「请解压到另一个文件夹再手动替换」——那是把机械活推给老师。
    /// 运行中的映像确实不能自我替换，但可以让独立助手进程在本进程退出后做：
    /// 单文件发布下只有 exe 被占用，助手等它释放后覆盖即可。
    /// </summary>
    ReplacePortable,

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
/// 分级原则：
///   - Windows 安装包（Inno）与 Windows 绿色版都能真正自动完成——
///     前者运行静默安装包，后者由独立助手进程在退出后解压覆盖；
///   - macOS / Linux 给指引：替换 .app 会牵涉 Gatekeeper，
///     Linux 压缩包无法保留可执行权限，都必须人工确认。
///
/// 绿色版之所以能做自动替换：单文件发布下运行中的只有 exe 本身，
/// 助手等本进程退出后覆盖即可。**不能**由本进程自己覆盖——运行中的映像
/// 不能自我替换，这是 Windows 的硬限制。
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
                InstallerAction.ReplacePortable,
                null,
                WaitForPreviousProcessExit: true,
                RestartAfterwards: true,
                Guidance: null),

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

/// <summary>
/// 「二次启动唤回已有窗口」的命名信号（审计 P2-25）。
///
/// 互斥锁只解决「不弹第二个窗口」——第二个进程退出后，老师点任务栏图标
/// 仍然没有任何反应，看起来像程序坏了。有了这条信号，第二实例在退出前把
/// 命名事件置为有信号状态，首实例的等待线程被唤醒后把主窗口激活置前。
///
/// 命名 <see cref="EventWaitHandle"/>（AutoReset）做跨进程唤醒是 Windows
/// 常规做法；名字与互斥锁同域（Local\），跨用户会话互不可见，与守卫的
/// 隔离语义一致。所有成员静态、内部全部走同一把锁，线程安全。
///
/// 这一层只做「信号与等待」的纯机制，不碰 UI；激活在 App 层的回调里
/// Post 回 UI 线程。失败一律静默：唤回窗口是体验增强，不是数据安全线，
/// 信号断掉的表现必须恰好是修复前的行为（第二实例退出、什么都不发生）。
/// </summary>
public static class SingleInstanceSignal
{
    private const string HandleName = @"Local\TeachCraft.App.Activate";

    private static readonly object Gate = new();
    private static readonly ManualResetEvent Quit = new(initialState: false);
    private static EventWaitHandle? _handle;
    private static Thread? _listener;
    private static bool _listenerStarted;
    private static volatile Action? _onSignal;

    /// <summary>
    /// 唤醒首实例：把信号置为有信号状态。首实例尚未注册等待也不丢——
    /// 句柄是命名内核对象，信号留在系统里，首实例注册后第一次等待立即返回。
    /// </summary>
    public static void SignalFirstInstance()
    {
        try
        {
            EnsureHandle().Set();
        }
        catch (Exception)
        {
            // 信号发不出去就当没有唤回能力，绝不能影响第二实例的正常退出。
        }
    }

    /// <summary>
    /// 注册等待线程，信号到达时在专用线程上触发 <paramref name="signal"/>。
    /// 回调应自行 Post 到 UI 线程——等待线程碰不了 Avalonia。
    /// 重复调用只替换回调，不叠加线程；先于注册发出的信号也会送达。
    /// </summary>
    public static void StartListener(Action signal)
    {
        if (signal is null) throw new ArgumentNullException(nameof(signal));

        lock (Gate)
        {
            if (_listenerStarted) { _onSignal = signal; return; }

            Quit.Reset();
            _onSignal = signal;
            var listener = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "TeachCraft.SingleInstanceSignal",
            };
            _listener = listener;
            _listenerStarted = true;
            listener.Start();
        }
    }

    /// <summary>停止等待线程并释放句柄。供测试复位；进程退出不需要调用（后台线程随进程消亡）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            if (!_listenerStarted && _handle is null) return;

            try { Quit.Set(); } catch (Exception) { }
            var listener = _listener;
            _listener = null;
            _listenerStarted = false;
            _onSignal = null;
            listener?.Join(TimeSpan.FromSeconds(2));
            try { _handle?.Dispose(); } catch (Exception) { }
            _handle = null;
            try { Quit.Reset(); } catch (Exception) { }
        }
    }

    private static EventWaitHandle EnsureHandle()
    {
        lock (Gate)
        {
            if (_handle is null)
                _handle = new EventWaitHandle(false, EventResetMode.AutoReset, HandleName, out _);
            return _handle;
        }
    }

    private static void ListenLoop()
    {
        EventWaitHandle handle;
        try
        {
            handle = EnsureHandle();
        }
        catch (Exception)
        {
            // 起不来等待线程就当没有唤回能力；别让一次内核对象失败掀掉进程。
            return;
        }

        while (true)
        {
            int signaled;
            try
            {
                signaled = WaitHandle.WaitAny(new WaitHandle[] { handle, Quit });
            }
            catch (Exception)
            {
                // 句柄失效（测试 Reset 后遗留等极端情形）：等待线程安静退出。
                return;
            }

            if (signaled != 0) return; // Quit 或意外索引：退出线程

            try
            {
                _onSignal?.Invoke();
            }
            catch (Exception)
            {
                // 回调炸了不能掀掉等待线程，下一次信号还要能送达。
            }
        }
    }
}
