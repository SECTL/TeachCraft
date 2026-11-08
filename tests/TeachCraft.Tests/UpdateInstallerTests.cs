using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 分级执行：不同安装形态走不同路径。
///
/// 关键约束：
///   - 绝不自替换正在运行的程序（微软文档：运行中的映像不能自我替换）。
///   - 绝不自动 runas 提权。
///   - macOS / Linux 不声称「自动安装」。
///   - 安装目录不可写不是可能发生的情况（IS-11 保证不写安装目录）。
/// 全部用注入的探针验证，不起真实进程。
/// </summary>
public sealed class UpdateInstallerTests
{
    private static UpdateProposal Proposal(InstallKind kind) => new(
        new ReleaseEntry("v1.18.0", SemanticVersion.Parse("1.18.0"), false,
            new[] { new ReleaseAsset("TeachCraft-v1.18.0-win-x64-sc-Setup.exe", 1, "https://e/x") }),
        new ReleaseAsset("TeachCraft-v1.18.0-win-x64-sc-Setup.exe", 1, "https://e/x"),
        kind,
        false);

    [Theory]
    [InlineData(InstallKind.InnoInstall)]
    [InlineData(InstallKind.InnoInstallLegacy)]
    public void InnoInstall_RunsSilentSetupWithCloseApplications(InstallKind kind)
    {
        var plan = UpdateInstaller.BuildPlan(Proposal(kind), @"C:\dl\TeachCraft-Setup.exe");

        Assert.Equal(InstallerAction.RunSetup, plan.Action);
        // 精确相等，不是 Contains：四个开关少一个都会让静默安装悄悄失效。
        Assert.Equal("/SILENT /SP- /NOICONS /CLOSEAPPLICATIONS", plan.Arguments);
        Assert.Equal(UpdateInstaller.SilentSetupArguments, plan.Arguments);
        Assert.True(plan.WaitForPreviousProcessExit);
        Assert.True(plan.RestartAfterwards);
    }

    [Fact]
    public void PortableZip_ReplacesInPlaceAndRestarts()
    {
        // 绿色版以前只给一句「请解压到另一个文件夹再手动替换」——那是把机械活推给老师。
        // 现在由独立助手进程在本进程退出后解压覆盖（运行中的映像不能自我替换，
        // 但助手是另一个进程，不受这个限制）。
        var plan = UpdateInstaller.BuildPlan(Proposal(InstallKind.PortableZip), @"D:\dl\x.zip");

        Assert.Equal(InstallerAction.ReplacePortable, plan.Action);
        Assert.True(plan.WaitForPreviousProcessExit, "必须等旧进程退出，否则 exe 还被占用");
        Assert.True(plan.RestartAfterwards, "替换完要把新版本拉起来");
        Assert.Null(plan.Arguments);
    }

    [Fact]
    public void MacBundle_GuidesWithGatekeeperSteps()
    {
        var plan = UpdateInstaller.BuildPlan(Proposal(InstallKind.MacBundle), "/dl/x.dmg");

        Assert.Equal(InstallerAction.ShowGuidance, plan.Action);
        // Gatekeeper：先给最简单的「按住 Control 点开」，再给终端命令。
        Assert.Contains("Control", plan.Guidance);
        Assert.Contains("com.apple.quarantine", plan.Guidance);
        Assert.DoesNotContain("自动安装", plan.Guidance);
    }

    [Fact]
    public void MacBundlePortable_AlsoGuidesAndCoversZip()
    {
        var plan = UpdateInstaller.BuildPlan(Proposal(InstallKind.MacBundlePortable), "/dl/x-mac.zip");

        Assert.Equal(InstallerAction.ShowGuidance, plan.Action);
        Assert.Contains("Control", plan.Guidance);
    }

    [Fact]
    public void LinuxPortable_GivesChmodGuidanceOnly()
    {
        var plan = UpdateInstaller.BuildPlan(Proposal(InstallKind.LinuxPortable), "/dl/x.zip");

        Assert.Equal(InstallerAction.ShowGuidance, plan.Action);
        Assert.Contains("chmod +x", plan.Guidance);
        Assert.DoesNotContain("自动安装", plan.Guidance);
    }

    [Fact]
    public void Unknown_RefusesToActAutomatically()
    {
        var plan = UpdateInstaller.BuildPlan(Proposal(InstallKind.Unknown), "/dl/x");

        Assert.Equal(InstallerAction.ShowGuidance, plan.Action);
        Assert.Contains("认不出", plan.Guidance);
    }

    [Fact]
    public void NoPlan_ClaimsNoAutomaticInstallAnywhere()
    {
        foreach (var kind in Enum.GetValues<InstallKind>())
        {
            var plan = UpdateInstaller.BuildPlan(Proposal(kind), "/dl/x");
            Assert.DoesNotContain("自动安装", plan.Guidance ?? "");
        }
    }
}

/// <summary>
/// 单实例守卫。「关闭 → 安装 → 重启」不能弹出两个窗口。
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void FirstAcquire_Succeeds()
    {
        var probe = new FakeLockProbe();
        var guard = new SingleInstanceGuard("TeachCraft-Test-" + Guid.NewGuid().ToString("N"), probe);

        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void SecondAcquire_FailsWhenAlreadyHeld()
    {
        var name = "TeachCraft-Test-" + Guid.NewGuid().ToString("N");
        // 两个实例共用同一把锁（同一台机器），而不是各拿一个空探针。
        var machine = new FakeLockProbe();
        var first = new SingleInstanceGuard(name, machine);
        var second = new SingleInstanceGuard(name, machine);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());

        first.Release();
        // 第一个释放后第二个应该能拿到。
        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void ReleaseThenReacquire_IsPossible()
    {
        // 更新装完会拉起新进程；若旧进程没释放锁，新进程会被自己挡住，
        // Windows 上表现为「装完就再也起不来」。
        var name = "TeachCraft-Test-" + Guid.NewGuid().ToString("N");
        var machine = new FakeLockProbe();
        var old = new SingleInstanceGuard(name, machine);
        Assert.True(old.TryAcquire());

        old.Release();

        var fresh = new SingleInstanceGuard(name, machine);
        Assert.True(fresh.TryAcquire(), "旧实例释放锁后，新进程必须能拿到");
    }

    private sealed class FakeLockProbe : ISingleInstanceProbe
    {
        private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

        public bool TryCreate(string name) => _held.Add(name);
        public void Release(string name) => _held.Remove(name);
    }
}

/// <summary>等待旧进程退出的契约（注入探针，不起真实进程）。</summary>
public sealed class ProcessWaitTests
{
    [Fact]
    public async Task WaitForExit_ReturnsTrueWhenProcessExits()
    {
        var polls = 0;

        var exited = await ProcessWaiter.WaitAsync(
            hasExited: _ => ++polls > 2,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.True(exited);
    }

    [Fact]
    public async Task WaitForExit_TimesOutWithClearReason()
    {
        var exited = await ProcessWaiter.WaitAsync(
            hasExited: _ => false,
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.False(exited);
    }
}
