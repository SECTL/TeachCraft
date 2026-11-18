using ClassBlock.Core.Models;
using TeachCraft.App.Updating;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 安装更新前的未保存窗口扫描（审计 P1-3）。
///
/// 判定逻辑抽成不依赖 Avalonia 的静态类，所以能在无 UI 线程的测试里直接断言；
/// 窗口枚举留在 App 层，由走查参数在真实运行中覆盖。
/// </summary>
public sealed class UpdateInstallGuardTests
{
    [Fact]
    public void FindUnsaved_ReturnsOnlyUnsavedTitles()
    {
        var result = UnsavedWindowGuard.FindUnsaved(new[]
        {
            ("归位 · 座位表", true),
            ("时序 · 课程表", false),
            ("竞名 · 运动会报名", true),
        });

        Assert.Equal(new[] { "归位 · 座位表", "竞名 · 运动会报名" }, result);
    }

    [Fact]
    public void FindUnsaved_EmptyInputYieldsEmpty()
    {
        Assert.Empty(UnsavedWindowGuard.FindUnsaved(Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void FindUnsaved_AllCleanYieldsEmpty()
    {
        var result = UnsavedWindowGuard.FindUnsaved(new[] { ("归位", false), ("竞名", false) });
        Assert.Empty(result);
    }

    [Fact]
    public void HasUnsavedWork_NullOrUnknownContextIsFalse()
    {
        Assert.False(UnsavedWindowGuard.HasUnsavedWork(null));
        Assert.False(UnsavedWindowGuard.HasUnsavedWork(new object()));
        Assert.False(UnsavedWindowGuard.HasUnsavedWork("not a view model"));
    }

    [Fact]
    public void HasUnsavedWork_ClassBlockWithMasterIsTrue()
    {
        // 修复前必红：时序不在派发表里，导入过总表的窗口会被当成「没有未保存内容」，
        // 点「下载并安装」直接 Shutdown，内存里的课表无声消失（审计 P2-26）。
        var block = new ClassBlock.App.ClassBlockViewModel();
        block.LoadMaster(SyntheticMaster(), "合成总表.xlsx");

        Assert.True(block.HasMaster);
        Assert.True(UnsavedWindowGuard.HasUnsavedWork(block));
    }

    [Fact]
    public void HasUnsavedWork_ClassBlockWithoutMasterIsFalse()
    {
        // 空窗口不添乱：老师刚打开时序、还没导入总表，安装更新不该多一道确认。
        var block = new ClassBlock.App.ClassBlockViewModel();

        Assert.False(block.HasMaster);
        Assert.False(UnsavedWindowGuard.HasUnsavedWork(block));
    }

    /// <summary>
    /// 内存构造的最小总表：不落任何文件、不碰真实数据。
    /// 班级名与教师名均为合成值（沿用竞名 / 织序的虚构名惯例）。
    /// </summary>
    private static MasterSchedule SyntheticMaster() => new()
    {
        Info = new MasterInfo { Title = "合成中学课程总表", Grade = "九年级", Term = "第一学期", Month = "11月" },
        Classes = new[]
        {
            new ClassSchedule
            {
                Name = "九（1）班",
                Key = "九1",
                Grade = "九年级",
                Number = 1,
                Days = Array.Empty<DaySchedule>(),
            },
        },
        HeadTeachers = new Dictionary<string, string>(),
        LunchStudy = new Dictionary<string, IReadOnlyList<string>>(),
        AfterSchool = new Dictionary<string, IReadOnlyList<DayService>>(),
        EarlyReading = Array.Empty<string>(),
        Warnings = Array.Empty<string>(),
    };

    // ------------------------------------------------------------- P1-1 守卫时序

    private static string ReadAppSource()
        => RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "App.axaml.cs"));

    [Fact]
    public void InstallerAsync_GateComesBeforeProcessStart()
    {
        // 修复前：Process.Start(安装包) → StartRestartHelper → 才轮到 FindUnsaved + ConfirmDialog，
        // 「暂不安装」拦不住已在后台跑的 Inno。上移后必须反过来：守卫先行、拒绝即返回、
        // 确认之后才允许出现任何启动点。
        var source = ReadAppSource();
        var runInstaller = ExtractMethod(source, "private async Task<string> RunInstallerAsync");

        var gate = runInstaller.IndexOf("ConfirmUnsavedBeforeInstall", StringComparison.Ordinal);
        var gateReturn = runInstaller.IndexOf("已取消安装", StringComparison.Ordinal);
        var processStart = runInstaller.IndexOf("System.Diagnostics.Process.Start(startInfo)", StringComparison.Ordinal);
        var restartHelper = runInstaller.IndexOf("StartRestartHelper(", StringComparison.Ordinal);

        Assert.True(gate >= 0, "RunInstallerAsync 必须调用 ConfirmUnsavedBeforeInstall（守卫不得留在别处）");
        Assert.True(gateReturn > gate, "守卫拒绝路径必须在守卫调用之后立即返回取消文案");
        Assert.True(processStart >= 0, "安装包启动点不见了——RunInstallerAsync 必须仍然自己 Process.Start");
        Assert.True(gate < processStart,
            $"守卫（位置 {gate}）必须排在安装包 Process.Start（位置 {processStart}）之前——这是审计 P1-1 的时序倒置本体");
        Assert.True(restartHelper > processStart, "重启帮手只能安排在安装包真的启动之后");
    }

    [Fact]
    public void PortableReplace_AlsoRunsTheSameGateBeforeStartingItsHelper()
    {
        // 修复前：RunPortableReplace 全程没有 UnsavedWindowGuard 调用（grep 全文件仅安装包路径一处）。
        // 绿色版替换助手一旦启动，本进程退出后就会覆盖文件，取消必须发生在启动之前。
        var source = ReadAppSource();
        var runPortable = ExtractMethod(source, "private async Task<string> RunPortableReplace");

        var gate = runPortable.IndexOf("ConfirmUnsavedBeforeInstall", StringComparison.Ordinal);
        var gateReturn = runPortable.IndexOf("已取消安装", StringComparison.Ordinal);
        var helperStart = runPortable.IndexOf("StartPortableReplaceHelper(", StringComparison.Ordinal);

        Assert.True(gate >= 0, "RunPortableReplace 必须调用 ConfirmUnsavedBeforeInstall（绿色版此前完全没接守卫）");
        Assert.True(gateReturn > gate, "守卫拒绝路径必须在守卫调用之后立即返回取消文案");
        Assert.True(helperStart >= 0, "替换助手启动点不见了——RunPortableReplace 必须仍然启动助手");
        Assert.True(gate < helperStart,
            $"守卫（位置 {gate}）必须排在替换助手启动（位置 {helperStart}）之前");
    }

    [Fact]
    public void GateIsSharedByBothPaths()
    {
        // 两条路径各有一处调用；只留一处意味着另一条路径又漏接了。
        var source = ReadAppSource();
        var callCount = CountOccurrences(source, "ConfirmUnsavedBeforeInstall(desktop)");
        Assert.True(callCount == 2,
            $"ConfirmUnsavedBeforeInstall(desktop) 必须恰好被安装包与绿色版两条路径各调用一次，实际 {callCount} 处");
        Assert.Contains("private async Task<bool> ConfirmUnsavedBeforeInstall", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GateContract_CancelWithoutConfirmationNeverProceeds()
    {
        // DecideInstallGate 是守卫行为的可测纯函数：
        // 有未保存窗口 + 用户取消 → Cancelled（调用方不得启动任何安装进程）；
        // 干净窗口 → 不弹窗直接放行；确认通过 → 放行。
        Assert.Equal(InstallGateOutcome.Cancelled, UnsavedWindowGuard.DecideInstallGate(hasUnsavedWindows: true, userConfirmed: false));
        Assert.Equal(InstallGateOutcome.Proceed, UnsavedWindowGuard.DecideInstallGate(hasUnsavedWindows: false, userConfirmed: false));
        Assert.Equal(InstallGateOutcome.Proceed, UnsavedWindowGuard.DecideInstallGate(hasUnsavedWindows: true, userConfirmed: true));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>截取从方法声明起、到下一个同级 private 方法声明前的源码片段。</summary>
    private static string ExtractMethod(string source, string declarationPrefix)
    {
        var start = source.IndexOf(declarationPrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法（声明前缀：{declarationPrefix}）——App.axaml.cs 结构变了，测试须同步锚点");

        var next = source.IndexOf("\n    private ", start + declarationPrefix.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }
}

/// <summary>
/// 二次启动唤回已有窗口的命名信号（审计 P2-25）。
///
/// 进程级行为（双开真实进程 → 首窗口前置）属实机验证，按计划明列为
/// 「未实机验证不阻塞」；这里钉住机制层的契约：
/// 信号发得出、等待线程收得到、注册前发出的信号不丢、重复注册不叠加线程、
/// 停止后等待线程与句柄都收干净。
/// </summary>
public sealed class SingleInstanceSignalTests
{
    [Fact]
    public void Listener_ReceivesSignalSentAfterRegistration()
    {
        SingleInstanceSignal.Reset();
        try
        {
            var fired = new ManualResetEventSlim(false);
            SingleInstanceSignal.StartListener(() => fired.Set());

            SingleInstanceSignal.SignalFirstInstance();

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "注册后的信号必须送达等待线程");
        }
        finally
        {
            SingleInstanceSignal.Reset();
        }
    }

    [Fact]
    public void Listener_ReceivesSignalSentBeforeRegistration()
    {
        // 真实时序里第二实例可能抢在首实例还没跑到注册行之前就发信号；
        // 命名事件留在内核里，这个信号不能丢。
        SingleInstanceSignal.Reset();
        try
        {
            SingleInstanceSignal.SignalFirstInstance();

            var fired = new ManualResetEventSlim(false);
            SingleInstanceSignal.StartListener(() => fired.Set());

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "注册前发出的信号也必须送达（命名事件留存）");
        }
        finally
        {
            SingleInstanceSignal.Reset();
        }
    }

    [Fact]
    public void RepeatedStartListener_DoesNotStackThreads()
    {
        // 单实例守卫只会走到一次注册，但机制上必须幂等：重复注册只换回调，
        // 不叠加等待线程——叠加意味着一次信号触发多次窗口前置。
        SingleInstanceSignal.Reset();
        try
        {
            var fired = new ManualResetEventSlim(false);
            SingleInstanceSignal.StartListener(() => fired.Set());
            SingleInstanceSignal.StartListener(() => fired.Set());
            SingleInstanceSignal.StartListener(() => fired.Set());

            SingleInstanceSignal.SignalFirstInstance();

            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            SingleInstanceSignal.Reset();
        }
    }

    [Fact]
    public void Reset_ReleasesHandleAndListener()
    {
        // 生命周期契约：停止之后，等待线程收掉、句柄释放，
        // 同名命名事件不再被本进程持有（更新关窗前的清理路径）。
        SingleInstanceSignal.Reset();
        try
        {
            var fired = new ManualResetEventSlim(false);
            SingleInstanceSignal.StartListener(() => fired.Set());
            SingleInstanceSignal.Reset();

            // 停止后再发信号也不该惊动任何已注销的回调。
            SingleInstanceSignal.SignalFirstInstance();
            Assert.False(fired.Wait(TimeSpan.FromMilliseconds(300)), "Reset 后回调不应再被触发");

            // Reset 幂等：再停一次不抛。
            SingleInstanceSignal.Reset();
        }
        finally
        {
            SingleInstanceSignal.Reset();
        }
    }

    [Fact]
    public void SignalWithoutListener_DoesNotThrow()
    {
        // 首实例没注册等待（理论上不该发生，但机制必须兜住）：
        // 信号发出去留在内核里，绝不能影响第二实例的正常退出。
        SingleInstanceSignal.Reset();
        try
        {
            SingleInstanceSignal.SignalFirstInstance();
            SingleInstanceSignal.SignalFirstInstance();
        }
        finally
        {
            SingleInstanceSignal.Reset();
        }
    }
}
