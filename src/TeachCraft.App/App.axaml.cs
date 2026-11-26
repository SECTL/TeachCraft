using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TeachCraft.App.Cloud;
using TeachCraft.App.ControlCenter;
using TeachCraft.App.ViewModels;
using TeachCraft.App.Views;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Control;
using TeachCraft.Core.Oobe;
using TeachCraft.Core.Updating;
using Homework.App.Cloud;
using Homework.Core.Roster;
using SeatGrid.App.Cloud;
using SeatGrid.Core.Cloud;
using TeachCraft.Shared;

namespace TeachCraft.App;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;

    /// <summary>
    /// 释放单实例锁。更新安装前必须调用，否则重启的新进程会被自己的锁挡住。
    /// 等待线程一并停掉：本进程马上退出，不再需要唤回，也避免重启后的新进程
    /// 在这两三秒窗口里把信号发进一个正在关闭的实例。
    /// </summary>
    private void ReleaseSingleInstance()
    {
        try
        {
            SingleInstanceSignal.Reset();
        }
        catch (Exception)
        {
            // 停不下来也无所谓：后台线程随进程消亡，不影响安装。
        }
        try
        {
            _singleInstance?.Release();
            _singleInstance = null;
        }
        catch (Exception)
        {
            // 释放失败不该拦住退出。
        }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --check-update 必须早于窗口与 ViewModel 创建：
            // 一旦走正常启动路径，状态存储可能已经在构造期落盘，「无副作用」就无从保证。
            // 这条路径也不弹窗、不抢单实例锁，退出码是唯一契约。
            if (RunCheckUpdateIfRequested(desktop))
                return;

            // 单实例守卫：更新时「关闭 → 安装 → 重启」若与旧实例并存，
            // 两个进程会抢同一个二进制文件。走查参数不参与，否则会和自动化打架。
            // 第二实例退出前还会发唤醒信号（SingleInstanceSignal）：互斥锁只保证
            // 不弹第二个窗口，但老师点任务栏图标时窗口毫无反应、看起来像程序坏了——
            // 首实例收到信号后把主窗口激活置前（审计 P2-25）。
            var args = desktop.Args ?? Array.Empty<string>();
            var isWalkthrough = args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
            if (!isWalkthrough)
            {
                _singleInstance = new SingleInstanceGuard(@"Local\TeachCraft.App");
                if (!_singleInstance.TryAcquire())
                {
                    // 已有实例在跑：发信号唤回它的主窗口，然后直接退出，不再开第二个窗口。
                    SingleInstanceSignal.SignalFirstInstance();
                    Environment.Exit(0);
                    return;
                }

                // 首实例注册等待线程。回调必须 Post 回 UI 线程——等待线程碰不了 Avalonia。
                // 信号在窗口尚未创建的极端时序下也不会丢：命名事件留在内核里，
                // Post 执行时 MainWindow 早已赋值。
                SingleInstanceSignal.StartListener(() =>
                    Dispatcher.UIThread.Post(() => desktop.MainWindow?.Activate()));
            }

            // 首次引导：走查参数默认抑制，但 --oobe 与 --oobe-* 自己例外
            // （只写精确匹配 --oobe 会让 --oobe-shot 截到一张没有遮罩的图）。
            // --oobe-reset 不算——它只删状态、不开窗口。
            // 「强制打开遮罩」只认 --oobe 与真正要取证的通道；
            // --oobe-metrics 与 --oobe-reset 不在其中——前者要如实反映「按状态该不该弹」，
            // 后者只删状态。否则前缀匹配会把它们也算成强制，判定就永远测不出来。
            var forceOobe = args.Contains("--oobe") ||
                args.Contains("--oobe-shot") ||
                args.Contains("--oobe-keys") ||
                args.Contains("--oobe-motion") ||
                args.Contains("--oobe-interact");
            var showOobe = forceOobe || !isWalkthrough;

            // 走查路径一律注入空存储：否则在 --oobe-shot 里按一次 Esc
            // 就会把 Skipped 写进开发者真实的 %LOCALAPPDATA%。
            // 走查一律注入空存储，**除了** --oobe-metrics：那条通道的用途正是验证「按真实状态该不该弹」，
            // 用空存储会让它永远读到「首次运行」。它只读不写，且 --oobe 仍会强制覆盖判定。
            // --oobe-metrics 尊重启动判定；--oobe-shot / --oobe-keys 等仍强制打开（它们要取证界面本身）。
        var readsRealState = args.Contains("--oobe-metrics") && !forceOobe && !args.Contains("--oobe-shot");
            var oobeStore = isWalkthrough && !readsRealState
                ? (IOobeStateStore)new NoopOobeStateStore()
                : new OobeStateStore();

            // oobeOverride 按四种组合取值（调研 P1-6：此前正常启动误传 showOobe=true，
            // 引导每次启动都强制弹出，OobeStateStore 记的「已结束」形同虚设）：
            //   --oobe-metrics → null：判定权交回 MainViewModel，按真实状态决定该不该弹；
            //   普通走查（--shot 家族等）→ showOobe（此处为 false）：截图不带遮罩；
            //   强制通道（--oobe / --oobe-shot 等）→ showOobe（此处为 true）：取证界面本身；
            //   正常启动 → null：由 OobeStateStore 的状态决定，只弹一次，结束过就不再打扰。
            var oobeOverride = readsRealState ? (bool?)null : (isWalkthrough ? showOobe : (bool?)null);
            var cloudRuntime = CloudWiring.Create(
                isWalkthrough, SectlPlatformDefaults.ClientId, SectlPlatformDefaults.RedirectUri, handler: null);
            var controlRuntime = ControlWiring.Create(
                isWalkthrough, BuildIdentity.Current(typeof(MainViewModel)).Describe());
            IPendingChangelogStore pendingChangelogStore = isWalkthrough
                ? new NoopPendingChangelogStore()
                : new PendingChangelogStore();
            var viewModel = new MainViewModel(
                oobeStateStore: oobeStore, showOobe: oobeOverride, cloudRuntime: cloudRuntime,
                pendingChangelogStore: pendingChangelogStore, controlRuntime: controlRuntime);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            if (!isWalkthrough && controlRuntime.Service is { } controlService)
            {
                if (controlRuntime.Snapshot.Enabled)
                    _ = controlService.StartAsync();

                desktop.ShutdownRequested += (_, _) =>
                {
                    try { Task.Run(() => controlService.DisposeAsync().AsTask()).Wait(1500); }
                    catch (Exception) { }
                };
            }

            // 真正启动安装包 / 替换文件只能由 App 层做：它决定何时关窗、何时退出、何时重启。
            // 两条路径都先过同一道「未保存窗口」前置（ConfirmUnsavedBeforeInstall），
            // 确认不通过就不会有任何助手进程被启动（审计 P1-1）。
            viewModel.InstallerRequested += (path, plan) => plan.Action == InstallerAction.ReplacePortable
                ? this.RunPortableReplace(desktop, path, plan, viewModel, pendingChangelogStore)
                : this.RunInstallerAsync(desktop, path, plan, viewModel, pendingChangelogStore);

            // 启动时清掉上次被强杀留下的半截下载（进程被杀时 finally 不会执行）。
            // 这一步不联网、不碰状态文件，失败也不影响启动。
            if (!isWalkthrough)
            {
                try { new UpdateDownloader(SharedHandlers.Default).CleanupStaleParts(); }
                catch (Exception) { /* 清理失败不该拦住启动 */ }

                // 上一次绿色版自替换的结果。失败时用户刚经历「程序关了又开回来还是旧的」，
                // 不说清楚他会以为更新没生效，反复点。
                var portableOutcome = ReportLastPortableReplace(viewModel);

                // 安装包路径的自动重启失败痕迹以前只写不读：程序关了没再回来，
                // 老师不知道安装其实成功了（审计 P3-19）。
                ReportLastRestartFailure(viewModel);

                // 仅在通过应用内更新模块升级成功并进入目标版本后展示一次更新日志；
                // 安装失败、取消或外部手动覆盖均不触发，消费即清除标记。
                var currentVersion = BuildIdentity.Current(typeof(MainViewModel)).Version;
                var startupChangelog = UpdateChangelogPlanner.ResolveOnStartup(
                    pendingChangelogStore,
                    currentVersion,
                    portableReplaceFailed: portableOutcome == PortableReplaceOutcome.Failed,
                    EmbeddedChangelogProvider.ReadOrNull());
                if (startupChangelog is not null)
                {
                    viewModel.SetStartupChangelog(startupChangelog);
                    WireStartupChangelog(window, viewModel);
                }
            }

            if (!CaptureGradeStripIfRequested(desktop) && !CaptureSeatGridIfRequested(desktop) &&
                !CaptureClassBlockIfRequested(desktop) && !CaptureGradeWeaveIfRequested(desktop) &&
                !CaptureGradeWeaveTimetableIfRequested(desktop) &&
                !CaptureGradeWeaveAssignmentIfRequested(desktop) &&
                !CaptureGradeWeaveNewProjectIfRequested(desktop) &&
                !CaptureGradeWeaveConflictIfRequested(desktop) &&
                !CaptureGradeWeaveExportIfRequested(desktop) &&
                !CaptureMeetRosterIfRequested(desktop) &&
                !CaptureSeatGridCloudIfRequested(desktop) &&
                !CaptureHomeworkRosterIfRequested(desktop) &&
                !CaptureChangelogIfRequested(desktop) &&
                !RunSliderBenchIfRequested(desktop) &&
                !RunPerfBenchIfRequested(desktop) &&
                !ShowGradeWeaveDemoIfRequested(desktop) &&
                !ShowSeatGridDemoIfRequested(desktop))
                CaptureIfRequested(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>设计走查：打开「时序」并导入指定总表后截图。</summary>
    private static bool CaptureClassBlockIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--classblock-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var master = index + 2 < args.Length ? args[index + 2] : null;
        var className = index + 3 < args.Length ? args[index + 3] : null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new ClassBlock.App.ClassBlockWindow();
                window.Show();

                if (master is not null)
                {
                    await window.LoadMasterAsync(master);
                    if (className is not null) await window.SelectClassAsync(className);
                    await Task.Delay(400);
                }

                await Task.Delay(900);
                window.CaptureTo(target);
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>设计走查：打开「织序」窗口并截图。</summary>
    private static bool CaptureGradeWeaveIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradeweave-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var source = index + 2 < args.Length && args[index + 2] != "-" ? args[index + 2] : null;
        var stepText = index + 3 < args.Length ? args[index + 3] : null;
        var lockText = index + 4 < args.Length ? args[index + 4] : null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new GradeWeave.App.GradeWeaveWindow
                {
                    Width = 1440,
                    Height = 920,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };

                window.Show();
                await Task.Delay(400);

                if (!string.IsNullOrWhiteSpace(source))
                {
                    if (!string.Equals(source, "empty", StringComparison.OrdinalIgnoreCase))
                        await window.LoadProjectFromFileAsync(source);
                }
                else
                {
                    window.LoadDemoProject();
                }

                if (int.TryParse(stepText, out var step))
                {
                    window.SetStepForCapture(step);
                    if (step == 5)
                    {
                        await window.GenerateForCaptureAsync();

                        // 走查锁课角标：给当前班级的所有普通课加锁。
                        if (string.Equals(lockText, "lock", StringComparison.OrdinalIgnoreCase) &&
                            window.DataContext is GradeWeave.App.GradeWeaveViewModel viewModel &&
                            viewModel.FilteredClasses.FirstOrDefault() is { } classItem)
                        {
                            viewModel.SetClassLessonsLocked(classItem.Class.Id, locked: true);
                        }
                    }
                }

                // 走查忙碌遮罩标题：按导出入口的真实顺序置忙，确认遮罩写的是这一次的动作。
                if (string.Equals(lockText, "busy", StringComparison.OrdinalIgnoreCase))
                {
                    window.SetStepForCapture(5);
                    await window.ShowBusyOverlayForCaptureAsync("正在导出 Excel 课表…");
                }

                await Task.Delay(900);
                window.UpdateLayout();

                var pixelSize = new PixelSize(
                    (int)Math.Max(1, window.Bounds.Width),
                    (int)Math.Max(1, window.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(window);

                using var stream = File.Create(target);
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                // 遮罩状态下窗口拒绝关闭（正在处理中），先收遮罩再关，否则走查会挂在关窗守卫上。
                window.HideBusyOverlayForCapture();
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>设计走查：打开织序「作息时间表」编辑窗口并截图。</summary>
    private static bool CaptureGradeWeaveTimetableIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradeweave-timetable-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var source = index + 2 < args.Length && args[index + 2] != "-" ? args[index + 2] : null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);

                GradeWeave.Core.Models.WeaveProject project;
                if (source is not null)
                {
                    project = new GradeWeave.Core.Import.WeaveWorkbookImporter().Import(source).Project;
                    GradeWeave.Core.Timetable.TimetableService.Ensure(project);
                }
                else
                {
                    project = GradeWeave.Core.Models.WeaveSamples.CreateDemo();
                }

                var window = new GradeWeave.App.TimetableEditorWindow(project)
                {
                    Width = 1040,
                    Height = 800,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                await Task.Delay(900);
                window.UpdateLayout();

                var pixelSize = new PixelSize(
                    (int)Math.Max(1, window.Bounds.Width),
                    (int)Math.Max(1, window.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(window);

                using var stream = File.Create(target);
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>设计走查：打开织序「教师分工」窗口并截图。</summary>
    private static bool CaptureGradeWeaveAssignmentIfRequested(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradeweave-assignment-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var source = index + 2 < args.Length && args[index + 2] != "-" ? args[index + 2] : null;
        var viewText = index + 3 < args.Length ? args[index + 3] : null;
        var teacherName = index + 4 < args.Length ? args[index + 4] : null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);

                GradeWeave.Core.Models.WeaveProject project;
                if (source is null)
                {
                    project = GradeWeave.Core.Models.WeaveSamples.CreateDemo();
                }
                else if (string.Equals(Path.GetExtension(source), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    project = GradeWeave.Core.Project.WeaveProjectIO.Load(source);
                }
                else
                {
                    project = new GradeWeave.Core.Import.WeaveWorkbookImporter().Import(source).Project;
                }

                var window = new GradeWeave.App.AssignmentEditorWindow(project)
                {
                    Width = 1120,
                    Height = 760,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                await Task.Delay(300);
                if (int.TryParse(viewText, out var viewMode))
                    window.SetViewForCapture(viewMode, teacherName);

                await Task.Delay(900);
                window.UpdateLayout();

                var pixelSize = new PixelSize(
                    (int)Math.Max(1, window.Bounds.Width),
                    (int)Math.Max(1, window.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(window);

                using var stream = File.Create(target);
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>
    /// 设计走查：打开织序「全新制作」窗口并截图。
    /// 用法：--gradeweave-new-shot &lt;png&gt; [课时合计]
    /// 课时合计用来取两种提示：超过 35 是超额红字，恰好 35 是满额中性灰（审计 P3-8）。
    /// </summary>
    private static bool CaptureGradeWeaveNewProjectIfRequested(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradeweave-new-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var hoursText = index + 2 < args.Length ? args[index + 2] : null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new GradeWeave.App.NewProjectWindow
                {
                    Width = 520,
                    Height = 720,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                await Task.Delay(300);
                if (int.TryParse(hoursText, out var hours))
                    window.SetHoursForCapture(hours);

                await Task.Delay(900);
                window.UpdateLayout();

                var pixelSize = new PixelSize(
                    (int)Math.Max(1, window.Bounds.Width),
                    (int)Math.Max(1, window.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(window);

                using var stream = File.Create(target);
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>设计走查：打开织序冲突聚合台并截图。</summary>
    private static bool CaptureGradeWeaveConflictIfRequested(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradeweave-conflicts-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var source = index + 2 < args.Length && args[index + 2] != "-" ? args[index + 2] : null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new GradeWeave.App.GradeWeaveWindow
                {
                    Width = 1440,
                    Height = 920,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                await Task.Delay(300);

                if (source is not null) await window.LoadProjectFromFileAsync(source);
                else window.LoadDemoProject();

                window.SetStepForCapture(5);
                if (window.DataContext is not GradeWeave.App.GradeWeaveViewModel viewModel)
                    throw new InvalidOperationException("织序窗口没有可用的视图模型。");

                if (viewModel.Project.Lessons.Count == 0) await window.GenerateForCaptureAsync();
                await viewModel.RefreshConflictsAsync(quiet: true, announce: false);

                var center = new GradeWeave.App.ConflictCenterWindow(viewModel, _ => { })
                {
                    Width = 880,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                center.Show(window);
                await Task.Delay(900);
                center.UpdateLayout();

                var pixelSize = new PixelSize(
                    (int)Math.Max(1, center.Bounds.Width),
                    (int)Math.Max(1, center.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(center);

                using var stream = File.Create(target);
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                center.Close();
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>
    /// 设计走查：打开织序的导出设置弹窗并截图。
    /// 用法：--gradeweave-export-shot &lt;png&gt; [excel|png|pdf] [software|reference|seewo]
    /// </summary>
    private static bool CaptureGradeWeaveExportIfRequested(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradeweave-export-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var formatText = index + 2 < args.Length ? args[index + 2] : "excel";
        var styleText = index + 3 < args.Length ? args[index + 3] : "software";

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new GradeWeave.App.GradeWeaveWindow
                {
                    Width = 1440,
                    Height = 920,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                await Task.Delay(300);

                window.LoadDemoProject();
                window.SetStepForCapture(5);
                if (window.DataContext is GradeWeave.App.GradeWeaveViewModel viewModel &&
                    viewModel.Project.Lessons.Count == 0)
                    await window.GenerateForCaptureAsync();

                var project = (window.DataContext as GradeWeave.App.GradeWeaveViewModel)?.Project
                              ?? new GradeWeave.Core.Models.WeaveProject();
                // PNG / PDF 导出当前选中的班级，走查时给第一个班，否则预览只能是「未选择」。
                var demoClass = project.Classes.FirstOrDefault();
                var dialog = new GradeWeave.App.ExportOptionsWindow(project, demoClass, null, false)
                {
                    Width = 1040,
                    Height = 760,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                dialog.Show(window);
                await Task.Delay(300);

                if (dialog.DataContext is GradeWeave.App.ExportOptionsViewModel options)
                {
                    options.SelectedFormat = options.Formats.FirstOrDefault(f =>
                        string.Equals(f.Format.ToString(), formatText, StringComparison.OrdinalIgnoreCase))
                        ?? options.Formats[0];
                    options.SelectedStyle = options.Styles.FirstOrDefault(s =>
                        string.Equals(s.Style.ToString(), styleText, StringComparison.OrdinalIgnoreCase))
                        ?? options.Styles[0];

                    // 预览是后台渲染 + 200ms 防抖，等它画完再截。
                    options.RequestPreview();
                    await Task.Delay(1200);
                }

                dialog.UpdateLayout();
                var pixelSize = new PixelSize(
                    (int)Math.Max(1, dialog.Bounds.Width),
                    (int)Math.Max(1, dialog.Bounds.Height));
                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(dialog);

                using var stream = File.Create(target);
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                dialog.Close();
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>设计走查：打开「竞名」并截取报名界面。</summary>
    private static bool CaptureMeetRosterIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--meetroster-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var view = index + 2 < args.Length ? args[index + 2] : null;
        var dayText = index + 3 < args.Length ? args[index + 3] : null;
        var category = index + 4 < args.Length ? args[index + 4] : null;
        var widthIndex = Array.IndexOf(args, "--meetroster-width");
        var heightIndex = Array.IndexOf(args, "--meetroster-height");
        var highlightIndex = Array.IndexOf(args, "--meetroster-highlight");
        var highlightClass = highlightIndex >= 0 && highlightIndex + 1 < args.Length
            ? args[highlightIndex + 1]
            : null;
        var registrationIndex = Array.IndexOf(args, "--meetroster-registration");
        var registrationPath = registrationIndex >= 0 && registrationIndex + 1 < args.Length
            ? args[registrationIndex + 1]
            : null;
        var scheduleIndex = Array.IndexOf(args, "--meetroster-schedule");
        var schedulePath = scheduleIndex >= 0 && scheduleIndex + 1 < args.Length
            ? args[scheduleIndex + 1]
            : null;
        var orderBookIndex = Array.IndexOf(args, "--meetroster-orderbook");
        var orderBookPath = orderBookIndex >= 0 && orderBookIndex + 1 < args.Length
            ? args[orderBookIndex + 1]
            : null;
        var showDemo = args.Contains("--meetroster-demo");
        var showEventDialog = args.Contains("--meetroster-dialog");
        var scaleIndex = Array.IndexOf(args, "--meetroster-scale");
        var scale = scaleIndex >= 0 && scaleIndex + 1 < args.Length &&
                    double.TryParse(args[scaleIndex + 1], out var parsedScale)
            ? parsedScale
            : 1;
        var dialogSlotIndex = Array.IndexOf(args, "--meetroster-dialog-slot");
        var dialogSlot = dialogSlotIndex >= 0 && dialogSlotIndex + 1 < args.Length
            ? args[dialogSlotIndex + 1]
            : MeetRoster.App.EventEditorViewModel.IndividualGroupKey;
        var width = widthIndex >= 0 && widthIndex + 1 < args.Length &&
                    int.TryParse(args[widthIndex + 1], out var parsedWidth)
            ? parsedWidth
            : 1400;
        var height = heightIndex >= 0 && heightIndex + 1 < args.Length &&
                     int.TryParse(args[heightIndex + 1], out var parsedHeight)
            ? parsedHeight
            : 900;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new MeetRoster.App.MeetRosterWindow
                {
                    Width = width,
                    Height = height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                await Task.Delay(400);

                if (showDemo)
                {
                    window.LoadDemoForCapture();
                    await Task.Delay(400);
                }

                if (!string.IsNullOrWhiteSpace(registrationPath))
                {
                    await window.LoadRegistrationForCaptureAsync(registrationPath);
                    await Task.Delay(400);
                }

                if (!string.IsNullOrWhiteSpace(schedulePath))
                {
                    await window.LoadScheduleForCaptureAsync(schedulePath);
                    await Task.Delay(600);
                }

                if (!string.IsNullOrWhiteSpace(orderBookPath))
                {
                    await window.LoadOrderBookForCaptureAsync(orderBookPath);
                    await Task.Delay(800);
                }

                var teamRegIndex = Array.IndexOf(args, "--meetroster-team-registration");
                if (teamRegIndex >= 0 && teamRegIndex + 1 < args.Length)
                {
                    await window.LoadRegistrationForTeamCaptureAsync(args[teamRegIndex + 1]);
                    await Task.Delay(600);
                }

                var removeIndex = Array.IndexOf(args, "--meetroster-remove");
                if (removeIndex >= 0 && removeIndex + 1 < args.Length)
                {
                    window.SimulateRemoveForCapture(args[removeIndex + 1].Split(','));
                    await Task.Delay(300);
                }

                if (string.Equals(view, "schedule", StringComparison.OrdinalIgnoreCase))
                {
                    var dayIndex = int.TryParse(dayText, out var parsedDay) ? parsedDay : 1;
                    window.ShowScheduleForCapture(dayIndex, category, highlightClass);
                    window.ShowOnlyClassForCapture(args.Contains("--meetroster-only-class"));
                }

                if (args.Contains("--meetroster-export-dialog"))
                {
                    var layoutIndex = Array.IndexOf(args, "--meetroster-export-layout");
                    var layout = layoutIndex >= 0 && layoutIndex + 1 < args.Length
                        ? args[layoutIndex + 1]
                        : null;
                    var styleIndex = Array.IndexOf(args, "--meetroster-export-style");
                    var style = styleIndex >= 0 && styleIndex + 1 < args.Length
                        ? args[styleIndex + 1]
                        : null;
                    var dialog = window.OpenExportDialogForCapture(layout, style);
                    if (dialog is null)
                        throw new InvalidOperationException("导出弹窗走查需要先注入报名表或秩序册。");

                    dialog.Show();
                    await Task.Delay(900);
                    dialog.UpdateLayout();
                    dialog.CaptureTo(target);

                    // 下拉弹层是独立 popup，截图抓不到：把每项的可选状态写进同名 .log，
                    // 用来核对「全部班级 → 原表高亮置灰」真的生效（审计 P2-17）。
                    var styleMetricsIndex = Array.IndexOf(args, "--meetroster-style-metrics");
                    if (styleMetricsIndex >= 0 && styleMetricsIndex + 1 < args.Length)
                    {
                        await Task.Delay(200);
                        dialog.DumpStyleAvailability(args[styleMetricsIndex + 1]);
                    }

                    dialog.Close();
                    window.Close();
                    return;
                }

                if (showEventDialog)
                {
                    var dialog = window.OpenEventDialogForCapture(dialogSlot);
                    if (dialog is null)
                        throw new InvalidOperationException("弹窗走查需要先注入报名表（--meetroster-registration）。");

                    dialog.Show();
                    await Task.Delay(900);
                    dialog.UpdateLayout();
                    dialog.CaptureTo(target);
                    dialog.Close();
                    window.Close();
                    return;
                }

                if (args.Contains("--meetroster-busy"))
                    window.ShowBusyForCapture();

                await Task.Delay(1000);
                window.UpdateLayout();

                var metricsIndex = Array.IndexOf(args, "--meetroster-metrics");
                if (metricsIndex >= 0 && metricsIndex + 1 < args.Length)
                    window.DumpRowMetrics(args[metricsIndex + 1]);

                window.CaptureTo(target, scale);
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>本地走查：打开一个已生成课表的织序窗口，便于人工检查拖拽与冲突（P1-B 取证）。</summary>
    private static bool ShowGradeWeaveDemoIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        if (!args.Contains("--gradeweave-demo")) return false;

        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(450);
            var window = new GradeWeave.App.GradeWeaveWindow
            {
                Width = 1440,
                Height = 920,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.LoadDemoProject();
            window.SetStepForCapture(5);
            window.Show();
            await window.GenerateForCaptureAsync();
        });

        return true;
    }

    /// <summary>本地走查：打开一个带演示名单的归位窗口，便于人工检查交互。</summary>
    private static bool ShowSeatGridDemoIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        if (!args.Contains("--seatgrid-demo")) return false;

        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(450);
            var window = new SeatGrid.App.SeatGridWindow();
            window.LoadDemoData();
            window.Show();
        });

        return true;
    }

    /// <summary>
    /// 滑块手感取证（P1-C）：--slider-bench &lt;md路径&gt;。在真实 Avalonia 运行时里
    /// 连续拖动布局滑块 60 帧，把每次实时刷新的 p50/p95 写入给定文件后退出。
    /// </summary>
    private static bool RunSliderBenchIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--slider-bench");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                File.WriteAllText(target, await SeatGrid.App.SeatGridDiagnostics.RunLayoutBenchAsync());
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>
    /// 性能基准走查：--perf-bench &lt;md路径&gt;。跑一遍 <see cref="Diagnostics.PerfBench"/> 里登记的自检基准，
    /// 把环境段与 p50 / p95 表写到给定文件后退出。与 --slider-bench 同构：
    /// 参数解析在 UI 线程之外完成，真正的采集与写文件放进 Dispatcher，异常落到
    /// 「目标 + .error.txt」，无论成败都在 finally 里关掉进程——
    /// 走查通道卡住不退出比报错更难排查。
    ///
    /// 与 --slider-bench 的两处差异都是为自动化服务的：目标目录不存在时先建出来
    /// （否则连 .error.txt 都写不下去、异常会直接掀掉进程）；写 .error.txt 本身失败也不再向外抛，
    /// finally 一定走到 Shutdown。成功是 0、失败是 2，与 --shot 的退出码契约一致。
    /// </summary>
    private static bool RunPerfBenchIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--perf-bench");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];

        Dispatcher.UIThread.Post(() =>
        {
            var exitCode = 0;
            try
            {
                // 目标必须是文件，不是目录。把目录当文件名传是走查里最容易犯的错，
                // 原来会抛 UnauthorizedAccessException，栈里全是 Win32 细节、看不出真正原因。
                var fullTarget = Path.GetFullPath(target);
                if (Directory.Exists(fullTarget))
                {
                    throw new InvalidOperationException(
                        $"--perf-bench 需要一个文件路径，但「{target}」是已存在的目录；" +
                        "请写成类似 outputs\\perf-bench-2026-11-10.md 的形式。");
                }

                var directory = Path.GetDirectoryName(fullTarget);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(fullTarget, RunPerfBenchReport());
            }
            catch (Exception ex)
            {
                exitCode = 2;
                try
                {
                    File.WriteAllText(target + ".error.txt", ex.ToString());
                }
                catch (Exception)
                {
                    // 连错误文件都写不下去时已经没有更好的落点：让退出码说话。
                }
            }
            finally
            {
                desktop.Shutdown(exitCode);
            }
        });

        return true;
    }

    /// <summary>
    /// 组装一份完整的性能报告：先登记业务基准（竞名 / 时序 / 分晓），再驱动它们跑一遍，
    /// 最后渲染。抽成独立方法是为了让自检项与业务项走同一条路，报告里只有一张表。
    /// </summary>
    private static string RunPerfBenchReport()
    {
        Diagnostics.MeetRosterBench.Register();
        Diagnostics.ClassBlockBench.Register();
        Diagnostics.PerfBench.RunBusinessBenchmarks();
        return Diagnostics.PerfBench.RenderReport();
    }

    /// <summary>设计走查：打开「归位」并装载演示名单后截图。</summary>
    private static bool CaptureSeatGridIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--seatgrid-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var mode = index + 2 < args.Length ? args[index + 2] : string.Empty;
        var empty = mode == "empty";

        // 导出走查：--seatgrid-chart-export <路径> [excel]
        // 截窗口的同时把「导出用的那一份文档」真渲染一遍。本次修复的缺陷正是在导出这一侧——
        // 界面网格一直是对的，只截界面看不出导出把后排切掉了。
        // 不指定格式时导出 PNG；路径为空则只截图。
        var exportIndex = Array.IndexOf(args, "--seatgrid-chart-export");
        var exportPath = exportIndex >= 0 && exportIndex + 1 < args.Length ? args[exportIndex + 1] : null;
        var exportExcel = exportIndex >= 0 && exportIndex + 2 < args.Length &&
                          string.Equals(args[exportIndex + 2], "excel", StringComparison.OrdinalIgnoreCase);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new SeatGrid.App.SeatGridWindow();
                if (!empty) window.LoadDemoData();
                if (mode == "constraints") window.LoadDemoConstraints();
                if (mode == "canvas") window.LoadDemoCanvas();
                if (mode == "exam") window.LoadDemoExam();
                if (mode == "exam-packed") window.LoadDemoExamPacked();
                if (mode == "undo") window.LoadDemoUndoState("can-undo");
                if (mode == "ushape") window.LoadDemoClearCanvas("ushape");
                if (mode == "cleared") window.LoadDemoClearCanvas("cleared");

                // 顶栏宽度走查：--seatgrid-width <px>。专治「按钮在小窗口下被挤到一起」——
                // 1440 下永远看不出问题，1180（最小宽度）才照得出来。
                var widthIndex = Array.IndexOf(args, "--seatgrid-width");
                if (widthIndex >= 0 && widthIndex + 1 < args.Length &&
                    double.TryParse(args[widthIndex + 1], out var shotWidth) && shotWidth > 0)
                    window.Width = shotWidth;
                if (mode == "redo") window.LoadDemoUndoState("can-redo");
                window.Show();
                await Task.Delay(1600);
                window.CaptureTo(target);
                if (!string.IsNullOrWhiteSpace(exportPath)) window.ExportChartForCapture(exportPath!, exportExcel);
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>
    /// 设计走查：--seatgrid-cloud-shot &lt;png&gt;。用内存假桥打开归位云端存档窗的共享名单页，
    /// 列出三份合成名单并选中其中一份后截图；不联网、不读真实凭据、不装配 CloudRuntime。
    /// </summary>
    private static bool CaptureSeatGridCloudIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--seatgrid-cloud-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = Path.GetFullPath(args[index + 1]);
        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bridge = new CaptureSeatGridCloudBridge();
                var cloudWindow = SeatGridCloudWindow.CreateForCapture(bridge);
                cloudWindow.Show();
                await Task.Delay(300);
                // WindowFit.Clamp 在 Opened 时跑过一次；这里再定尺寸，确保截图尺寸就是预期值。
                cloudWindow.Width = 820;
                cloudWindow.Height = 740;
                await Task.Delay(400);
                cloudWindow.SelectRosterForCapture("高一(3)班");
                await Task.Delay(200);
                CaptureWindowTo(cloudWindow, target);
                File.WriteAllText(target + ".state.txt",
                    $"rosters={bridge.Rosters.Count}; selected=高一(3)班; window={cloudWindow.Bounds.Size}");
                cloudWindow.Close();
            }
            catch (Exception ex)
            {
                exitCode = 1;
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally { desktop.Shutdown(exitCode); }
        });
        return true;
    }

    /// <summary>
    /// 设计走查：--homework-roster-shot &lt;png&gt;。用内存假桥打开辑册共享名单拉取窗，
    /// 列出三份合成名单并选中两份后截图；不联网、不读真实凭据、不装配 CloudRuntime。
    /// </summary>
    private static bool CaptureHomeworkRosterIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--homework-roster-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = Path.GetFullPath(args[index + 1]);
        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bridge = new CaptureHomeworkRosterBridge();
                var pickerViewModel = new HomeworkRosterPickerViewModel(bridge);
                await pickerViewModel.RefreshAsync(CancellationToken.None);
                pickerViewModel.Rows[0].IsSelected = true;
                pickerViewModel.Rows[1].IsSelected = true;
                var pickerWindow = new SharedRosterPickerWindow(pickerViewModel);
                pickerWindow.Show();
                await Task.Delay(300);
                // WindowFit.Clamp 在 Opened 时跑过一次；这里再定尺寸，确保截图尺寸就是预期值。
                pickerWindow.Width = 760;
                pickerWindow.Height = 620;
                await Task.Delay(400);
                CaptureWindowTo(pickerWindow, target);
                File.WriteAllText(target + ".state.txt",
                    $"rosters={bridge.Entries.Count}; selected={pickerViewModel.SelectedCount}; window={pickerWindow.Bounds.Size}");
                pickerWindow.Close();
            }
            catch (Exception ex)
            {
                exitCode = 1;
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally { desktop.Shutdown(exitCode); }
        });
        return true;
    }

    /// <summary>设计走查：只打开「分晓」窗口并截图，确认它能在工具箱里正常渲染。</summary>
    private static bool CaptureGradeStripIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--gradestrip-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];

        // 截图参数之后的路径按顺序收集，遇到下一个 -- 参数即停止。
        var paths = new System.Collections.Generic.List<string>();
        for (var i = index + 2; i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            if (File.Exists(args[i])) paths.Add(args[i]);
        }

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var viewModel = new GradeStrip.App.ViewModels.MainViewModel();
                var window = new GradeStrip.App.Views.MainWindow { DataContext = viewModel };
                // 走查截图默认停在表格底部（滚到底才看得到分析报表入口）；
                // README 之类要的是表头，用 --gradestrip-height 调矮窗口即可露出。
                var shotHeightIndex = Array.IndexOf(args, "--gradestrip-height");
                var shotHeight = shotHeightIndex >= 0 && shotHeightIndex + 1 < args.Length &&
                                 int.TryParse(args[shotHeightIndex + 1], out var parsedShotHeight)
                    ? parsedShotHeight
                    : 860;
                if (paths.Count > 0) window.Height = shotHeight;
                window.Show();

                if (paths.Count > 0)
                {
                    // 与拖拽一致：成绩表建立会话，分析报表挂到当前会话
                    await viewModel.ImportDroppedFilesAsync(paths);

                    viewModel.Section = 0;
                    window.UpdateLayout();

                    // 默认滚到表格底部，是为了走查时能看到最下面的「分析报表」入口。
                    // README 之类要的是标题与表头，用 --gradestrip-top 停在顶部。
                    var stayAtTop = args.Contains("--gradestrip-top");
                    if (!stayAtTop &&
                        window.FindControl<ScrollViewer>("OverviewScrollViewer") is { } overview)
                    {
                        overview.UpdateLayout();
                        overview.Offset = new Vector(0, overview.Extent.Height);
                    }

                    await Task.Delay(600);
                }

                await Task.Delay(1200);

                // 截图走查：--busy-preview 强制显示分晓的忙碌遮罩，检查加载动画
                if (args.Contains("--busy-preview"))
                {
                    viewModel.BusyText = "正在解析成绩总表…";
                    viewModel.IsBusy = true;
                    await Task.Delay(500);
                }

                // 截图走查：--gradestrip-exam-manager 打开考试管理遮罩，检查五个操作按钮
                // 与忙碌置灰的观感（审计 P3-4）。置忙后再截一张，验证按钮确实变灰。
                if (args.Contains("--gradestrip-exam-manager"))
                {
                    viewModel.OpenExamManager();
                    await Task.Delay(300);

                    if (args.Contains("--gradestrip-busy-buttons"))
                    {
                        viewModel.IsBusy = true;
                        await Task.Delay(300);
                    }
                }

                // 截图走查：--gradestrip-remove-report 摘掉第一份报表（审计 P2-23）。
                // 摘完应看到空态 + 状态栏「已移除报表，可从概览重新挂载」。
                if (args.Contains("--gradestrip-remove-report") && viewModel.ReportCount > 0)
                {
                    viewModel.RemoveReport(viewModel.ReportCards[0].Report);
                    window.UpdateLayout();
                    await Task.Delay(300);
                }

                window.CaptureTo(target);
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown();
            }
        });

        return true;
    }

    /// <summary>
    /// 安装包 / 绿色版替换共用的未保存前置：先扫描，再按需确认。
    ///
    /// 审计 P1-1：确认曾经排在安装包启动之后——点「暂不安装」时 Inno 已在后台跑，
    /// 遇到文件占用会弹自己的「自动关闭应用程序」对话框，绕过全部确认；绿色版路径则完全没接。
    /// 现在两条路径都必须先走这里：确认不通过就什么都不启动（安装包、重启帮手、替换助手）。
    /// 「有未保存才弹窗、拒绝即取消」这条判定由
    /// <see cref="Updating.UnsavedWindowGuard.DecideInstallGate"/> 纯函数锁住。
    /// </summary>
    private async Task<bool> ConfirmUnsavedBeforeInstall(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var unsaved = Updating.UnsavedWindowGuard.FindUnsaved(
            desktop.Windows.Select(w => (
                w.Title ?? "某个窗口",
                Updating.UnsavedWindowGuard.HasUnsavedWork(w.DataContext))));

        if (unsaved.Count == 0)
            return Updating.UnsavedWindowGuard.DecideInstallGate(hasUnsavedWindows: false, userConfirmed: false)
                == Updating.InstallGateOutcome.Proceed;

        var proceed = await TeachCraft.Shared.ConfirmDialog.ShowAsync(
            desktop.MainWindow ?? desktop.Windows.First(),
            "安装更新会关闭所有窗口",
            $"{string.Join("、", unsaved)} 还有未保存的内容。建议先导出或保存，再安装更新。仍要继续吗？",
            "继续安装",
            "暂不安装",
            danger: true);

        return Updating.UnsavedWindowGuard.DecideInstallGate(hasUnsavedWindows: true, userConfirmed: proceed)
            == Updating.InstallGateOutcome.Proceed;
    }

    /// <summary>
    /// 执行安装包：静默升级、装完重启。
    ///
    /// 三个必须守住的点：
    ///   1. **不能**先 Shutdown 再 await —— Dispatcher 关闭后续体永远不会跑。
    ///      正确做法是：先确认安装包能启动，再把「等待 + 重启」交给一个独立的助手进程，
    ///      本进程随后干净退出。
    ///   2. 必须用 UseShellExecute=false + 显式 WorkingDirectory：
    ///      ShellExecute 配 /SILENT 在 Win11 上会被静默忽略，安装根本不发生。
    ///   3. Inno 的 [Run] 带 skipifsilent，静默安装不会自己拉起程序，重启得由我们自己安排。
    /// </summary>
    private async Task<string> RunInstallerAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string setupPath,
        InstallerPlan plan,
        MainViewModel viewModel,
        IPendingChangelogStore pendingChangelogStore)
    {
        try
        {
            if (!File.Exists(setupPath))
                return "安装包不见了，请重新下载。";

            // 守卫必须在安装包启动之前（审计 P1-1）：确认排在 Process.Start 之后时，
            // 点「暂不安装」拦不住已在后台运行的 Inno——它会弹自己的「自动关闭应用程序」
            // 对话框，把带着未保存工作的窗口一并关掉，UnsavedWindowGuard 形同虚设。
            if (!await ConfirmUnsavedBeforeInstall(desktop).ConfigureAwait(true))
                return "已取消安装。请先保存各窗口的工作，再重新点「下载并安装」。";

            var installerDir = Path.GetDirectoryName(setupPath) ?? ".";
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = plan.Arguments ?? string.Empty,
                // ShellExecute + /SILENT 会被系统忽略；必须直接创建进程。
                UseShellExecute = false,
                WorkingDirectory = installerDir,
            };

            using var installer = System.Diagnostics.Process.Start(startInfo);
            if (installer is null)
                return "安装程序没能启动，请手动运行下载好的安装包。";

            if (plan.RestartAfterwards && Environment.ProcessPath is { } exe)
            {
                // 助手进程负责：等安装包退出 → 等本进程退出 → 重启新版本。
                // 它看不见本进程的 Dispatcher，所以不会被 Shutdown 打断。
                // 必须等确认通过才安排——否则取消安装后帮手残留，等一个不会退出的进程。
                StartRestartHelper(installer.Id, Environment.ProcessId, exe);
            }

            if (viewModel.CreatePendingChangelogEntry() is { } pendingEntry)
                pendingChangelogStore.Save(pendingEntry);

            // 到这里安装包已经在跑，可以安全退出。
            // **必须先释放单实例锁**：帮手进程装完后会拉起新版本，
            // 若此时锁仍被本进程持有（Mutex 还没随进程销毁），新进程 TryAcquire 会失败并直接 Exit(0)，
            // 装完就再也起不来了。
            ReleaseSingleInstance();
            desktop.Shutdown();
            await Task.CompletedTask.ConfigureAwait(false);

            return plan.Guidance ?? "正在安装，完成后会自动重新打开。";
        }
        catch (Exception ex)
        {
            return UpdateErrorText.DescribeForUi(ex, "安装更新");
        }
    }

    /// <summary>
    /// 绿色版自替换：启动一个独立助手，等本进程退出后解压覆盖，再拉起新版本。
    ///
    /// 为什么不能自己干：运行中的映像不能自我替换（Windows 硬限制）。
    /// 助手是独立进程，不受这个限制；它等的就是本进程的 PID。
    ///
    /// 失败处理：助手把结果写进一个标记文件，新版本启动时读它并提示用户。
    /// 没有这一步，替换失败会表现成「点了更新，程序关了再没回来」。
    /// </summary>
    private async Task<string> RunPortableReplace(
        IClassicDesktopStyleApplicationLifetime desktop,
        string archivePath,
        InstallerPlan plan,
        MainViewModel viewModel,
        IPendingChangelogStore pendingChangelogStore)
    {
        if (Environment.ProcessPath is not { } exe)
            return "认不出当前程序的路径，无法自动替换，请手动解压更新包。";

        var targetDir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(targetDir))
            return "认不出当前程序所在的文件夹，无法自动替换，请手动解压更新包。";

        try
        {
            // 与安装包路径共用同一条前置（审计 P1-1）：替换助手一旦启动，
            // 就会在本进程退出后覆盖文件，取消必须发生在助手启动之前。
            if (!await ConfirmUnsavedBeforeInstall(desktop).ConfigureAwait(true))
                return "已取消安装。请先保存各窗口的工作，再重新点「下载并安装」。";

            StartPortableReplaceHelper(archivePath, targetDir, exe, Environment.ProcessId);

            if (viewModel.CreatePendingChangelogEntry() is { } pendingEntry)
                pendingChangelogStore.Save(pendingEntry);

            // 与安装包路径同理：必须先释放单实例锁，否则重启的新版本 TryAcquire 失败会直接退出。
            ReleaseSingleInstance();
            desktop.Shutdown();

            return plan.Guidance ?? "正在替换为新版本，完成后会自动重新打开。";
        }
        catch (Exception ex)
        {
            return UpdateErrorText.DescribeForUi(ex, "替换更新");
        }
    }

    /// <summary>
    /// 启动「等退出 → 解压覆盖 → 重启」的助手进程。
    ///
    /// 用 PowerShell 的理由与 <see cref="StartRestartHelper"/> 一致：不引入新依赖。
    /// 脚本内联在 -EncodedCommand 里，路径含空格或中文都不会被拆坏。
    /// </summary>
    private static void StartPortableReplaceHelper(string archivePath, string targetDir, string exePath, int currentPid)
    {
        static string Ps(string s) => s.Replace("'", "''");

        var resultFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Update", "last-replace.txt");

        // 助手脚本：
        //   1. 等本进程退出（exe 被占用，必须等）
        //   2. 解压到临时目录
        //   3. 备份 → 覆盖 → 失败回滚
        //   4. 写结果标记，然后拉起新版本
        var script =
            $"Wait-Process -Id {currentPid} -ErrorAction SilentlyContinue; " +
            $"$staging = Join-Path $env:TEMP ('TeachCraft-Replace-' + [guid]::NewGuid().ToString('N').Substring(0,12)); " +
            $"$backup = $staging + '-bak'; " +
            $"$result = '{Ps(resultFile)}'; " +
            $"New-Item -ItemType Directory -Force -Path (Split-Path $result) | Out-Null; " +
            "try { " +
            $"  Expand-Archive -LiteralPath '{Ps(archivePath)}' -DestinationPath $staging -Force; " +
            $"  New-Item -ItemType Directory -Force -Path $backup | Out-Null; " +
            $"  Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {{ " +
            $"    $rel = [System.IO.Path]::GetRelativePath($staging, $_.FullName); " +
            $"    $dst = Join-Path '{Ps(targetDir)}' $rel; " +
            $"    if (Test-Path -LiteralPath $dst) {{ $bak = Join-Path $backup $rel; " +
            "      New-Item -ItemType Directory -Force -Path (Split-Path $bak) | Out-Null; " +
            "      Copy-Item -LiteralPath $dst -Destination $bak -Force } " +
            "    New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null; " +
            "    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force }; " +
            "  'OK' | Set-Content -LiteralPath $result -Encoding UTF8 " +
            "} catch { " +
            "  if (Test-Path -LiteralPath $backup) { " +
            $"    Get-ChildItem -LiteralPath $backup -Recurse -File | ForEach-Object {{ " +
            $"      $rel = [System.IO.Path]::GetRelativePath($backup, $_.FullName); " +
            $"      Copy-Item -LiteralPath $_.FullName -Destination (Join-Path '{Ps(targetDir)}' $rel) -Force }} }}; " +
            "  ('FAIL ' + $_.Exception.Message) | Set-Content -LiteralPath $result -Encoding UTF8 } " +
            "finally { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue; " +
            "  Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }; " +
            $"Start-Process -FilePath '{Ps(exePath)}'";

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>
    /// 启动一个独立的等待/重启帮手。
    ///
    /// 用 PowerShell 而不是额外进程：不引入新依赖，且 .bat/.ps1 在 Windows 上到处都有。
    /// 帮手自己等待两个 PID，然后拉起新版本；任何一步失败都只影响重启，不影响已完成的安装。
    /// </summary>
    private static void StartRestartHelper(int installerPid, int currentPid, string exePath)
    {
        try
        {
            // 用 -EncodedCommand：路径里即使有双引号也不会把命令行拆坏，
            // 也不需要手动转义引号（之前用 -Command "..." 拼串会断）。
            var script =
                $"Wait-Process -Id {installerPid} -ErrorAction SilentlyContinue; " +
                $"Wait-Process -Id {currentPid} -ErrorAction SilentlyContinue; " +
                $"Start-Process -FilePath '{exePath.Replace("'", "''")}'";

            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            // 帮手起不来（企业策略禁用 PowerShell 等）时，安装仍会完成，
            // 但程序不会自动回来——必须留下痕迹，否则用户只会以为「装坏了」。
            WriteRestartFailure(ex);
        }
    }

    private enum PortableReplaceOutcome
    {
        None,
        Succeeded,
        Failed,
    }

    /// <summary>
    /// 读取上一次绿色版自替换的结果并显示给用户。
    ///
    /// 助手进程写 `last-replace.txt`：`OK` 或 `FAIL <原因>`。
    /// 成功时悄悄删掉，不打扰；失败时必须说出来——用户刚经历「程序关了又开回来」，
    /// 不说清楚他会以为更新没生效，反复点「下载并安装」。
    /// </summary>
    private static PortableReplaceOutcome ReportLastPortableReplace(MainViewModel viewModel)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeachCraft", "Update", "last-replace.txt");

            if (!File.Exists(path)) return PortableReplaceOutcome.None;

            var content = File.ReadAllText(path).Trim();
            File.Delete(path);

            if (content.StartsWith("FAIL", StringComparison.Ordinal))
            {
                var reason = content.Length > 5 ? content[5..].Trim() : "未知原因";
                viewModel.SetStatus($"上次自动替换没有成功：{reason}。请手动解压更新包覆盖旧文件。", error: true);
                return PortableReplaceOutcome.Failed;
            }
            if (content == "OK")
            {
                viewModel.SetStatus("已更新到新版本。");
                return PortableReplaceOutcome.Succeeded;
            }
        }
        catch (Exception)
        {
            // 读不到就算了，不能因为一条提示拦住启动。
        }

        return PortableReplaceOutcome.None;
    }

    private static void WireStartupChangelog(MainWindow window, MainViewModel viewModel)
    {
        void OnOpened(object? _, EventArgs __)
        {
            window.Opened -= OnOpened;

            void ShowNow()
            {
                if (viewModel.ConsumeStartupChangelog() is not { } doc) return;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        var dialog = new UpdateChangelogWindow(doc);
                        await dialog.ShowDialog(window);
                    }
                    catch (Exception)
                    {
                        // 弹窗展示失败不阻断主界面使用；标记已在读取时清理，不再重复弹出。
                    }
                }, DispatcherPriority.ApplicationIdle);
            }

            if (viewModel.Oobe.IsOpen)
            {
                void OnOobeClosed()
                {
                    viewModel.Oobe.Closed -= OnOobeClosed;
                    ShowNow();
                }

                viewModel.Oobe.Closed += OnOobeClosed;
            }
            else
            {
                ShowNow();
            }
        }

        window.Opened += OnOpened;
    }

    /// <summary>
    /// 设计走查：--changelog-shot &lt;png&gt; [--changelog-from &lt;ver&gt;] [--changelog-to &lt;ver&gt;]。
    /// 全部使用内存存储与内置 CHANGELOG.md，不联网、不读写真实 %LOCALAPPDATA%。
    /// </summary>
    private static bool CaptureChangelogIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--changelog-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = Path.GetFullPath(args[index + 1]);
        var fromIndex = Array.IndexOf(args, "--changelog-from");
        var toIndex = Array.IndexOf(args, "--changelog-to");
        var fromText = fromIndex >= 0 && fromIndex + 1 < args.Length ? args[fromIndex + 1] : "1.44.0";
        var toText = toIndex >= 0 && toIndex + 1 < args.Length
            ? args[toIndex + 1]
            : BuildIdentity.Current(typeof(MainViewModel)).Version.ToString();

        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var store = new CapturePendingChangelogStore(new PendingChangelogEntry(fromText, toText));
                var targetVersion = SemanticVersion.Parse(toText);
                var doc = UpdateChangelogPlanner.ResolveOnStartup(
                    store,
                    targetVersion,
                    portableReplaceFailed: false,
                    EmbeddedChangelogProvider.ReadOrNull())
                    ?? throw new InvalidOperationException("更新日志走查未能生成展示文档。");

                var dialog = new UpdateChangelogWindow(doc);
                dialog.Show();
                await Task.Delay(500);
                dialog.CaptureTo(target);
                File.WriteAllText(target + ".state.txt",
                    $"headline={doc.Headline}; releases={doc.Releases.Count}; fallback={doc.IsFallbackSummary}; window={dialog.Bounds.Size}");
                dialog.Close();
            }
            catch (Exception ex)
            {
                exitCode = 1;
                File.WriteAllText(target + ".error.txt", ex.ToString());
            }
            finally
            {
                desktop.Shutdown(exitCode);
            }
        });
        return true;
    }

    private sealed class CapturePendingChangelogStore : IPendingChangelogStore
    {
        private PendingChangelogEntry? _entry;
        public CapturePendingChangelogStore(PendingChangelogEntry entry) => _entry = entry;
        public PendingChangelogEntry? Load() => _entry;
        public void Save(PendingChangelogEntry entry) => _entry = entry;
        public void Clear() => _entry = null;
    }

    /// <summary>重启帮手失败时留一条本机痕迹，供用户与支持者定位。</summary>
    /// <summary>
    /// 上一次安装后自动重启失败的痕迹。写完就读、读完即删——
    /// 程序关了没再回来，老师只看到「更新装完就没了」，而安装其实已经成功（审计 P3-19）。
    /// </summary>
    private static void ReportLastRestartFailure(MainViewModel viewModel)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeachCraft", "Update", "restart-failure.txt");
            if (!File.Exists(path)) return;

            var content = File.ReadAllText(path).Trim();
            File.Delete(path);

            var firstLine = content.Split('\n').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(firstLine)) return;

            viewModel.SetStatus(
                $"上次更新安装完成后没能自动重新打开（{firstLine}）。安装本身可能已经成功，可手动打开知微确认。",
                error: true);
        }
        catch (Exception)
        {
            // 读不到就算了，不能因为一条提示拦住启动。
        }
    }

    private static void WriteRestartFailure(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeachCraft", "Update");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "restart-failure.txt"),
                $"自动重启没能安排，请手动打开知微确认安装结果。{Environment.NewLine}" +
                $"{ErrorText.Describe(ex, "安排自动重启")}{Environment.NewLine}" +
                $"{DateTimeOffset.Now:O}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 连痕迹都写不了就只能放弃，不能再抛。
        }
    }

    /// <summary>
    /// <c>--check-update &lt;fixture.json|-&gt; [--out &lt;path&gt;] [--install-kind &lt;kind&gt;] [--install-root &lt;dir&gt;] [--print-version]</c>
    ///
    /// 退出码：0 = 无更新，1 = 有更新，2 = 失败。
    /// WinExe 没有控制台，所以结果一律写进 --out 文件，由调用方读取。
    /// 这条路径**不写** state.json（注入 NoopUpdateStateStore），也不起窗口。
    /// </summary>
    private static bool RunCheckUpdateIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();

        var index = Array.IndexOf(args, "--check-update");
        if (index < 0) return false;

        var printVersion = Array.IndexOf(args, "--print-version") >= 0;
        var outIndex = Array.IndexOf(args, "--out");
        var kindIndex = Array.IndexOf(args, "--install-kind");
        var rootIndex = Array.IndexOf(args, "--install-root");

        try
        {
            // 身份与安装形态都走生产检测路径——CLI 不能另开一套，
            // 否则「GUI 能选包、CLI 选不出」这种分裂会一直存在。
            var identity = BuildIdentity.Current(typeof(App));
            var detected = InstallKindDetector.Current(InstallRecordReader.ReadAll());

            // RID：优先用构建身份里的（CI 注入的最准），否则用运行时架构推出来的。
            // 注意不能用 ?? ：BuildIdentity 在无身份时给的是空串而不是 null。
            if (string.IsNullOrEmpty(identity.Rid))
            {
                var rid = string.IsNullOrEmpty(detected.Rid)
                    ? InstallKindDetector.InferCurrentRid()
                    : detected.Rid;
                if (!string.IsNullOrEmpty(rid)) identity = identity.WithRid(rid);
            }

            // 模式：优先用安装包的身份记录，其次允许走查显式指定。
            if (!string.IsNullOrEmpty(detected.Mode) && string.IsNullOrEmpty(identity.Mode))
                identity = identity.WithMode(detected.Mode);

            var modeIndex = Array.IndexOf(args, "--install-mode");
            if (modeIndex >= 0 && modeIndex + 1 < args.Length)
                identity = identity.WithMode(args[modeIndex + 1]);

            if (printVersion)
                Console.WriteLine($"identity={identity.Describe()} rid={identity.Rid ?? "-"} mode={identity.Mode ?? "-"}");

            // 没给 fixture 时只做身份打印。
            // 「只打印身份」：--check-update 后面没跟 fixture，或跟的是另一个 -- 开关。
            // 之前只判断 index + 1 >= Length，而 --print-version 恰好占那个位置，
            // 结果去 File.ReadAllText("--print-version")，把「找不到文件」报成网络失败、退出码 2。
            var hasSource = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal);
            if (!hasSource)
            {
                Environment.Exit(0);
                return true;
            }

            var source = args[index + 1];

            // 默认形态必须来自生产检测结果，不能写死 PortableZip——
            // 否则 CLI 与 GUI 会给出不同结论，「有更新」也可能伴随 AssetName=null。
            var kind = kindIndex >= 0 && kindIndex + 1 < args.Length
                ? ParseKind(args[kindIndex + 1])
                : detected.Kind;

            List<ReleaseEntry> entries;
            var usedSource = "fixture";

            // `--check-update mirror`：真实打 SECTL 镜像。这条路径存在的意义是
            // 让镜像接入能在真实网络下取证，而不是只靠注入的 handler 自证。
            if (string.Equals(source, "mirror", StringComparison.OrdinalIgnoreCase))
            {
                var mirror = new SectlMirrorCheckService(SharedHandlers.Default);
                var check = mirror.CheckAsync(identity).GetAwaiter().GetResult();
                if (!check.IsSuccess)
                {
                    var failPayload = new
                    {
                        Identity = identity.Describe(),
                        IsUpdateAvailable = false,
                        Version = (string?)null,
                        AssetName = (string?)null,
                        Reason = check.Failure,
                        Status = "Failed",
                        Source = "sectl",
                    };
                    WriteResult(outIndex, args, failPayload);
                    Environment.Exit(2);
                    return true;
                }

                entries = check.Candidates.ToList();
                usedSource = "sectl";
            }
            else
            {
                var json = source == "-" ? Console.In.ReadToEnd() : File.ReadAllText(source);
                entries = UpdateFeed.Parse(json).ToList();
            }

            var candidates = UpdateFeed.SelectCandidates(entries, identity);
            var best = candidates.FirstOrDefault();
            var asset = best is null ? null : UpdateAssetSelector.Select(best.Assets, identity, kind);

            // 与 GUI 的 UpdatePlanner 对齐：有新版但选不出资产 = 失败，不是成功。
            var failed = best is not null && asset is null;
            var reason = failed
                ? $"新版本 {best!.Version} 暂未提供适合当前安装方式的安装包，请稍后再试或到发布页手动选择。"
                : null;

            var payload = new
            {
                Identity = identity.Describe(),
                IsUpdateAvailable = best is not null && asset is not null,
                Version = best?.Version.ToString(),
                AssetName = asset?.Name,
                Reason = reason,
                Status = failed ? "Failed" : best is null ? "UpToDate" : "UpdateAvailable",
                Source = usedSource,
            };

            WriteResult(outIndex, args, payload);
            Environment.Exit(failed ? 2 : best is null ? 0 : 1);
            return true;
        }
        catch (Exception ex)
        {
            // JSON 坏了 ≠ 网络坏了。之前两者都落进「请检查网络后重试」，
            // 用户被门禁页劫持时看到的话术与 fixture 写错时完全一样，没法区分。
            var reason = ex is System.Text.Json.JsonException
                ? UpdateErrorText.ForNonJsonBody("检查更新")
                : UpdateErrorText.DescribeForUi(ex, "检查更新");

            var payload = new
            {
                Identity = (string?)null,
                IsUpdateAvailable = false,
                Version = (string?)null,
                AssetName = (string?)null,
                Reason = reason,
                Status = "Failed",
            };
            WriteResult(outIndex, args, payload);
            Environment.Exit(2);
            return true;
        }
    }

    /// <summary>
    /// 把结果写到 --out 指定的文件。
    ///
    /// --out 来自命令行，属于外部输入，因此必须收窄：
    ///   - 只接受绝对路径（相对路径的基准是当前目录，会随调用方漂移）；
    ///   - 扩展名必须是 .json；
    ///   - 不许写进程序自身所在目录（会覆盖安装内容）；
    ///   - 不许写进本 App 的 %LOCALAPPDATA%\TeachCraft（会覆盖状态与下载缓存）。
    /// 任何一条不满足都拒绝写入，而不是「尽力而为」。
    /// </summary>
    private static void WriteResult(int outIndex, string[] args, object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });

        if (outIndex < 0 || outIndex + 1 >= args.Length)
        {
            Console.WriteLine(json);
            return;
        }

        var target = ValidateOutPath(args[outIndex + 1]);
        if (target is null)
        {
            Console.WriteLine(json);
            return;
        }

        File.WriteAllText(target, json);
    }

    /// <summary>校验 --out；不合法返回 null（调用方退回 stdout）。</summary>
    private static string? ValidateOutPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw)) return null;
        if (!string.Equals(Path.GetExtension(raw), ".json", StringComparison.OrdinalIgnoreCase)) return null;

        var full = Path.GetFullPath(raw);

        var appDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(appDir) && IsUnder(full, appDir)) return null;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachCraft");
        if (IsUnder(full, appData)) return null;

        return full;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallKind ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "inno" => InstallKind.InnoInstall,
        "inno-legacy" => InstallKind.InnoInstallLegacy,
        "portable" => InstallKind.PortableZip,
        "mac" => InstallKind.MacBundle,
        "mac-portable" => InstallKind.MacBundlePortable,
        "linux" => InstallKind.LinuxPortable,
        _ => InstallKind.Unknown,
    };

    private static void CaptureIfRequested(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var args = desktop.Args ?? Array.Empty<string>();

        // 账号页走查：只用假账号与内存偏好，绝不装配真实服务、绝不发请求。
        var accountShotIndex = Array.IndexOf(args, "--account-shot");
        if (accountShotIndex >= 0 && accountShotIndex + 1 < args.Length)
        {
            var accountTarget = Path.GetFullPath(args[accountShotIndex + 1]);
            var state = accountShotIndex + 2 < args.Length ? args[accountShotIndex + 2] : "signedout";
            Dispatcher.UIThread.Post(async () =>
            {
                var exitCode = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(accountTarget)!);
                    var fakeAccount = new CaptureAccountService();
                    if (string.Equals(state, "signedin", StringComparison.OrdinalIgnoreCase))
                        fakeAccount.SetSignedIn("示例教师");
                    var settings = new CaptureSettingsStore();
                    var runtime = new CloudRuntime(true, fakeAccount, null, null, settings, settings.Load());
                    var accountViewModel = new AccountViewModel(runtime);
                    accountViewModel.ApplyCaptureState(state);

                    var dialog = AccountWindow.OpenForCapture(accountViewModel, window);
                    await Task.Delay(600);
                    dialog.UpdateLayout();
                    CaptureWindowTo(dialog, accountTarget);
                    dialog.Close();
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    File.WriteAllText(accountTarget + ".error.txt", ex.ToString());
                }
                finally { desktop.Shutdown(exitCode); }
            });
            return;
        }

        var homeworkShotIndex = Array.IndexOf(args, "--homework-shot");
        if (homeworkShotIndex >= 0 && homeworkShotIndex + 1 < args.Length)
        {
            var homeworkTarget = Path.GetFullPath(args[homeworkShotIndex + 1]);
            var homeworkWidthIndex = Array.IndexOf(args, "--width");
            var homeworkWidth = homeworkWidthIndex >= 0 && homeworkWidthIndex + 1 < args.Length
                && double.TryParse(args[homeworkWidthIndex + 1], out var parsedHomeworkWidth)
                ? parsedHomeworkWidth : 1440;
            Dispatcher.UIThread.Post(async () =>
            {
                var exitCode = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(homeworkTarget)!);
                    var homeworkWindow = new Homework.App.HomeworkWindow { Width = homeworkWidth };
                    homeworkWindow.Show();
                    await Task.Delay(500);
                    var viewModel = (Homework.App.HomeworkViewModel)homeworkWindow.DataContext!;
                    viewModel.BeginNewBatch();
                    await Task.Delay(500);
                    var workspace = viewModel.Workspace!;
                    using var homeworkCache = new Homework.Core.Pages.PageThumbnailCache();
                    if (args.Contains("--homework-demo"))
                    {
                        var (demoImages, demoPdf) = CreateHomeworkDemoInputs(
                            args.Contains("--homework-stress") ? 40 : 2);
                        workspace.ApplyPaste(Homework.Core.Roster.RosterPasteParser.Parse(
                            "八年级1班,01,苏晚晴\n八年级1班,02,陈屿\n八年级2班,01,许砚", "八年级1班"));
                        workspace.ImportImages(demoImages, homeworkCache, null, CancellationToken.None);
                        workspace.ImportPdf(demoPdf, homeworkCache, 150, null, CancellationToken.None);
                        if (workspace.VisiblePages.Count > 0)
                        {
                            workspace.SelectPage(workspace.VisiblePages[0]);
                            if (workspace.Students.Count > 0) workspace.MatchStudent(workspace.Students[0]);
                        }
                        workspace.ShowOnlyMatchedStudents = true;
                        workspace.SelectedStudent = workspace.Students.FirstOrDefault();
                        await Task.Delay(800);
                        if (args.Contains("--homework-dialogs"))
                        {
                            var optionsViewModel = new Homework.App.ExportOptionsViewModel
                            {
                                OutputDirectory = Path.GetTempPath(),
                            };
                            optionsViewModel.UpdatePreview(workspace.Batch, new Homework.App.FileProbe());
                            var exportDialog = new Homework.App.ExportOptionsWindow(optionsViewModel);
                            exportDialog.Show();
                            await Task.Delay(500);
                            CaptureWindowTo(exportDialog, homeworkTarget + ".export.png");
                            exportDialog.Close(null);

                            var rosterDialog = new Homework.App.RosterImportWindow();
                            rosterDialog.Show();
                            var rosterPath = Path.Combine(Path.GetDirectoryName(homeworkTarget)!, "homework-roster.csv");
                            File.WriteAllText(rosterPath,
                                "班级,学号,姓名\n八年级1班,01,苏晚晴\n八年级1班,02,陈屿\n八年级2班,01,许砚\n",
                                new System.Text.UTF8Encoding(true));
                            rosterDialog.LoadForCapture(rosterPath);
                            await Task.Delay(500);
                            File.WriteAllText(homeworkTarget + ".roster.state.txt",
                                rosterDialog.DescribeSelectionsForCapture());
                            CaptureWindowTo(rosterDialog, homeworkTarget + ".roster.png");
                            rosterDialog.Close(false);
                        }
                    }
                    homeworkWindow.CaptureTo(homeworkTarget);
                    File.WriteAllText(homeworkTarget + ".state.txt",
                    $"batch={workspace.Batch.Name}; pages={workspace.Batch.Pages.Count}; " +
                    $"students={workspace.Batch.Students.Count}; pending={workspace.PendingCount}; " +
                    $"incomplete={workspace.IncompleteStudentsCount}; " +
                    $"matched={workspace.Batch.Students.Count - workspace.IncompleteStudentsCount}; " +
                    $"canCommit={workspace.CanCommit}; window={homeworkWindow.Bounds.Size}");
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    File.WriteAllText(homeworkTarget + ".error.txt", ex.ToString());
                }
                finally { desktop.Shutdown(exitCode); }
            });
            return;
        }

        // 填涂识别走查：合成 3 名学号 001/002/003 的学生与 3 张已知数字的填涂页，
        // 框选数字格区域、列数 3，运行识别后截主窗口，并生成模板 PNG/PDF 校验几何。
        // 与 --width 组合；--homework-fillin-demo 为显式演示开关（本分支默认即演示）。
        var homeworkFillInShotIndex = Array.IndexOf(args, "--homework-fillin-shot");
        if (homeworkFillInShotIndex >= 0 && homeworkFillInShotIndex + 1 < args.Length)
        {
            var fillInTarget = Path.GetFullPath(args[homeworkFillInShotIndex + 1]);
            var fillInWidthIndex = Array.IndexOf(args, "--width");
            var fillInWidth = fillInWidthIndex >= 0 && fillInWidthIndex + 1 < args.Length
                && double.TryParse(args[fillInWidthIndex + 1], out var parsedFillInWidth)
                ? parsedFillInWidth : 1440;
            Dispatcher.UIThread.Post(async () =>
            {
                var exitCode = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fillInTarget)!);
                    var fillInWindow = new Homework.App.HomeworkWindow { Width = fillInWidth };
                    fillInWindow.Show();
                    await Task.Delay(500);
                    var viewModel = (Homework.App.HomeworkViewModel)fillInWindow.DataContext!;
                    viewModel.BeginNewBatch();
                    await Task.Delay(300);
                    var workspace = viewModel.Workspace!;
                    var batch = workspace.Batch;

                    var classId = Guid.NewGuid();
                    var syntheticClass = new Homework.Core.Models.HomeworkClass(classId, "八年级1班");
                    var syntheticStudents = new[]
                    {
                        new Homework.Core.Models.HomeworkStudent(Guid.NewGuid(), classId, "001", "苏晚晴"),
                        new Homework.Core.Models.HomeworkStudent(Guid.NewGuid(), classId, "002", "陈屿"),
                        new Homework.Core.Models.HomeworkStudent(Guid.NewGuid(), classId, "003", "许砚"),
                    };
                    batch.LoadRoster(new[] { syntheticClass }, syntheticStudents);

                    var pageDir = Path.Combine(Path.GetTempPath(), "TeachCraft", "homework-fillin-shot",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(pageDir);
                    using var fillInCache = new Homework.Core.Pages.PageThumbnailCache();
                    var pagePaths = new List<string>();
                    foreach (var number in new[] { "001", "002", "003" })
                        pagePaths.Add(CreateFillInDemoPage(number, pageDir));
                    // 走生产导入路径：PageThumbnailCache 生成缩略图，预览才有内容。
                    // 走查 lambda 已在 UI 线程，UI 绑定的集合不能切到 Task.Run。
                    workspace.ImportImages(pagePaths, fillInCache, null, CancellationToken.None);

                    var region = new Homework.Core.Recognition.NormalizedRect(
                        100 / 900.0, 100 / 1200.0, 600 / 900.0, 1000 / 1200.0);
                    batch.SetFillInRegion(region);
                    batch.SetFillInColumns(3);

                    // 经 VM 公开 API 应用区域：内部写回批次并触发 Refresh，让页/名单进入界面集合。
                    workspace.DraftFillInRegion = region;
                    workspace.ApplyFillInRegion(false);
                    if (workspace.VisiblePages.Count > 0)
                        workspace.SelectPage(workspace.VisiblePages[0]);

                    await workspace.RunFillInRecognitionAsync();
                    await Task.Delay(300);

                    var templateDir = Path.Combine(Path.GetTempPath(), "TeachCraft", "homework-fillin-shot",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(templateDir);
                    var templateOptions = new Homework.Core.Recognition.FillInTemplateOptions(
                        "作业填涂卡", "八年级1班", 3);
                    var pngBytes = Homework.Core.Recognition.FillInTemplateRenderer.RenderPng(templateOptions);
                    var templatePngPath = Path.Combine(templateDir, "template.png");
                    File.WriteAllBytes(templatePngPath, pngBytes);
                    int templatePngWidth, templatePngHeight;
                    using (var decoded = SkiaSharp.SKBitmap.Decode(pngBytes))
                    {
                        templatePngWidth = decoded.Width;
                        templatePngHeight = decoded.Height;
                    }
                    var pdfBytes = Homework.Core.Recognition.FillInTemplateRenderer.RenderPdf(templateOptions);
                    var templatePdfPath = Path.Combine(templateDir, "template.pdf");
                    File.WriteAllBytes(templatePdfPath, pdfBytes);
                    int templatePdfPages;
                    using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(
                               templatePdfPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                        templatePdfPages = pdf.PageCount;

                    fillInWindow.CaptureTo(fillInTarget);
                    var recognized = batch.Pages.Count(p => p.Recognition is not null);
                    var matched = batch.Pages.Count(p => p.StudentId is not null);
                    File.WriteAllText(fillInTarget + ".state.txt",
                        $"pages={batch.Pages.Count}; recognized={recognized}; matched={matched}; " +
                        $"pending={workspace.PendingCount}; columns={workspace.FillInColumns}; " +
                        $"templatePng={templatePngWidth}x{templatePngHeight}; " +
                        $"templatePdfPages={templatePdfPages}; window={fillInWindow.Bounds.Size}");
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    File.WriteAllText(fillInTarget + ".error.txt", ex.ToString());
                }
                finally { desktop.Shutdown(exitCode); }
            });
            return;
        }

        var homeworkScanShotIndex = Array.IndexOf(args, "--homework-scan-shot");
        if (homeworkScanShotIndex >= 0 && homeworkScanShotIndex + 1 < args.Length)
        {
            var scanTarget = Path.GetFullPath(args[homeworkScanShotIndex + 1]);
            Dispatcher.UIThread.Post(async () =>
            {
                var exitCode = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(scanTarget)!);
                    var demoRoot = Path.Combine(Path.GetTempPath(), "TeachCraft", "homework-scan-shot",
                        Guid.NewGuid().ToString("N"));
                    var inbox = Path.Combine(demoRoot, "扫描收件");
                    Directory.CreateDirectory(inbox);

                    var preferences = new Homework.Core.Scanning.ScanTaskPreferences
                    {
                        InboxFolder = inbox,
                        Source = Homework.Core.Scanning.ScanSource.FeederDuplex,
                        PageSize = Homework.Core.Scanning.ScanPageSize.A4,
                        ResolutionDpi = 300,
                        ColorMode = Homework.Core.Scanning.ScanColorMode.BlackWhite,
                        PageLimit = Homework.Core.Scanning.ScanPageLimit.All,
                        ImportToCurrent = true,
                    };
                    var viewModel = new Homework.App.Scanning.ScanTaskViewModel(
                        new ScanDemoScannerService(),
                        new ScanDemoImportSink(),
                        preferences);
                    var window = new Homework.App.ScanTaskWindow(
                        viewModel,
                        new Homework.Core.Scanning.ScanTaskPreferencesStore(
                            Path.Combine(demoRoot, "scan-task.json")))
                    {
                        Width = 820,
                        Height = 740,
                    };
                    window.Show();
                    await Task.Delay(500);
                    await viewModel.RefreshScannersAsync();
                    await viewModel.ImportIncomingAsync("扫描件-001.pdf");
                    await viewModel.ImportIncomingAsync("扫描件-002.pdf");
                    viewModel.AddIncomingFailure(
                        "扫描件-003.jpg",
                        "文件仍在写入或无法读取，稍后可点「导入现有文件」重试。");
                    await Task.Delay(300);

                    CaptureWindowTo(window, scanTarget);
                    var capabilities = viewModel.Capabilities;
                    var capabilityText = capabilities is null
                        ? "capabilities=unknown"
                        : $"sources={string.Join("/", capabilities.Sources)}; " +
                          $"pages={string.Join("/", capabilities.PageSizes)}; " +
                          $"resolutions={string.Join("/", capabilities.Resolutions)}; " +
                          $"colors={string.Join("/", capabilities.ColorModes)}";
                    File.WriteAllText(scanTarget + ".state.txt",
                        $"scanners={viewModel.Scanners.Count}; selected={viewModel.SelectedScanner?.Name}; " +
                        $"direct={viewModel.IsDirectMode}; received={viewModel.ReceivedFiles.Count}; " +
                        $"{capabilityText}; window={window.Bounds.Size}");

                    viewModel.SwitchToInbox();
                    viewModel.SetWatching(true);
                    await Task.Delay(300);
                    CaptureWindowTo(window, scanTarget + ".inbox.png");
                    File.WriteAllText(scanTarget + ".inbox.state.txt",
                        $"inbox={viewModel.InboxFolder}; watching={viewModel.IsWatching}; " +
                        $"received={viewModel.ReceivedFiles.Count}; window={window.Bounds.Size}");
                    window.Close();
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    File.WriteAllText(scanTarget + ".error.txt", ex.ToString());
                }
                finally { desktop.Shutdown(exitCode); }
            });
            return;
        }

        var scanCapsIndex = Array.IndexOf(args, "--homework-scan-caps");
        if (scanCapsIndex >= 0 && scanCapsIndex + 1 < args.Length)
        {
            var capsTarget = Path.GetFullPath(args[scanCapsIndex + 1]);
            Dispatcher.UIThread.Post(() =>
            {
                var exitCode = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(capsTarget)!);
                    var service = Homework.App.Scanning.ScannerServiceFactory.Create();
                    var scanners = service.GetScannersAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (scanners.Count == 0)
                    {
                        File.WriteAllText(capsTarget, "未找到扫描仪。请确认 WIA 驱动已安装。");
                    }
                    else
                    {
                        var scannerIdIndex = Array.IndexOf(args, "--scanner-id");
                        var wantedId = scannerIdIndex >= 0 && scannerIdIndex + 1 < args.Length
                            ? args[scannerIdIndex + 1]
                            : null;
                        var selected = wantedId is null
                            ? scanners[0]
                            : scanners.FirstOrDefault(scanner =>
                                string.Equals(scanner.Id, wantedId, StringComparison.OrdinalIgnoreCase))
                              ?? scanners[0];
                        var caps = service.GetCapabilitiesAsync(selected, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        File.WriteAllText(
                            capsTarget,
                            string.IsNullOrWhiteSpace(caps.RawDump)
                                ? $"scanner: {selected.Name}\nparsed: {caps.Summary()}\nreported: {caps.IsReported}\n未读到驱动属性原文（连接失败或驱动未暴露 WIA 属性）。"
                                : caps.RawDump);
                    }
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    File.WriteAllText(capsTarget, ex.ToString());
                }
                finally
                {
                    desktop.Shutdown(exitCode);
                }
            });
            return;
        }

        var certificateShotIndex = Array.IndexOf(args, "--certificate-shot");
        if (certificateShotIndex >= 0 && certificateShotIndex + 1 < args.Length)
        {
            var certificateTarget = Path.GetFullPath(args[certificateShotIndex + 1]);
            var certificateWidthIndex = Array.IndexOf(args, "--width");
            var certificateWidth = certificateWidthIndex >= 0 && certificateWidthIndex + 1 < args.Length
                && double.TryParse(args[certificateWidthIndex + 1], out var certificateParsedWidth) ? certificateParsedWidth : 1440;
            var portrait = args.Contains("--certificate-portrait");
            Dispatcher.UIThread.Post(async () =>
            {
                var exitCode = 0;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(certificateTarget)!);
                    window.Width = certificateWidth;
                    var heightIndex = Array.IndexOf(args, "--certificate-height");
                    if (heightIndex >= 0 && heightIndex + 1 < args.Length
                        && double.TryParse(args[heightIndex + 1], out var certificateHeight))
                        window.Height = certificateHeight;
                    await window.ShowForShotAsync("certificate-batch");
                    var tool = ((MainViewModel)window.DataContext!).FindTool("certificate-batch")!;
                    var draft = tool.Certificate!;

                    async Task CaptureCertificateAsync(string state)
                    {
                        await Task.Delay(500);
                        if (args.Contains("--certificate-scroll-bottom"))
                        {
                            window.ScrollCertificateSettingsToEnd();
                            await Task.Delay(50);
                        }
                        window.CaptureTo(certificateTarget);
                        File.WriteAllText(certificateTarget + ".layout.json",
                            System.Text.Json.JsonSerializer.Serialize(window.CaptureCertificateLayoutMetrics()));
                        File.WriteAllText(certificateTarget + ".state.txt", state);
                    }

                    if (args.Contains("--certificate-empty"))
                    {
                        await CaptureCertificateAsync(
                            $"tool={tool.Id}; state=empty; canRun={tool.CanRun}; " +
                            $"preview={tool.HasCertificatePreview}; showEmpty={tool.ShowCertificatePreviewEmpty}; window={window.Bounds.Size}");
                        return;
                    }
                    var inputs = await Task.Run(() => CreateCertificateShotInputs(portrait));
                    var (applied, error) = await draft.AssignPathsAsync(new[] { inputs.Template, inputs.Roster });
                    if (!applied) throw new InvalidOperationException(error ?? draft.StatusText);
                    draft.SelectedWorksheetName = draft.WorksheetNames[0];
                    draft.SelectedNameColumn = draft.NameColumnChoices[0];
                    draft.SelectedAwardColumn = args.Contains("--certificate-no-award")
                        ? CertificateColumnOption.NoAward : draft.AwardColumnChoices[1];
                    draft.NameX = 48;
                    draft.NameY = 48;
                    draft.AwardY = 65;
                    if (args.Contains("--certificate-preview-error"))
                    {
                        var missingTemplate = Path.Combine(Path.GetDirectoryName(inputs.Template)!, "missing-template.png");
                        draft.AssignTemplate(missingTemplate);
                        for (var attempt = 0; attempt < 100 && !tool.HasCertificatePreviewError; attempt++)
                            await Task.Delay(100);
                        if (!tool.HasCertificatePreviewError || tool.ShowCertificatePreviewEmpty || tool.HasCertificatePreview)
                            throw new InvalidOperationException("缺失底图未进入预览失败状态：" + tool.CertificatePreviewStatus);
                        await CaptureCertificateAsync(
                            $"tool={tool.Id}; state=preview-error; previewError={tool.HasCertificatePreviewError}; " +
                            $"preview={tool.HasCertificatePreview}; showEmpty={tool.ShowCertificatePreviewEmpty}; " +
                            $"records={draft.Records.Count}; canRun={tool.CanRun}; window={window.Bounds.Size}");
                        return;
                    }

                    for (var attempt = 0; attempt < 100 && !tool.HasCertificatePreview; attempt++)
                        await Task.Delay(100);
                    if (!tool.HasCertificatePreview) throw new InvalidOperationException(draft.StatusText);
                    var exportIndex = Array.IndexOf(args, "--certificate-export");
                    var exportPath = exportIndex >= 0 && exportIndex + 1 < args.Length
                        ? Path.GetFullPath(args[exportIndex + 1]) : null;
                    var alternate = args.Contains("--certificate-interact")
                        ? await Task.Run(() => CreateCertificateShotInputs(!portrait)) : inputs;
                    var evidence = await window.ProbeCertificateAsync(alternate.Template, exportPath,
                        args.Contains("--certificate-interact"));
                    await CaptureCertificateAsync(
                        $"tool={tool.Id}; records={draft.Records.Count}; canRun={tool.CanRun}; " +
                        $"name={draft.NameX},{draft.NameY}; award={draft.AwardX},{draft.AwardY}; " +
                        $"preview={tool.CertificatePreviewImage!.PixelSize}; template={inputs.Template}; roster={inputs.Roster}" +
                        Environment.NewLine + evidence);
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    File.WriteAllText(certificateTarget + ".error.txt", ex.ToString());
                }
                finally { desktop.Shutdown(exitCode); }
            });
            return;
        }

        if (CaptureOobeIfRequested(desktop, window, args)) return;

        var selfTestIndex = Array.IndexOf(args, "--selftest");
        if (selfTestIndex >= 0 && selfTestIndex + 4 < args.Length)
        {
            var toolId = args[selfTestIndex + 1];
            var input = args[selfTestIndex + 2];
            var output = args[selfTestIndex + 3];
            var shot = args[selfTestIndex + 4];

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Task.Delay(900);
                    await window.RunForShotAsync(toolId, input, output);
                    await Task.Delay(700);
                    window.CaptureTo(shot);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(shot + ".error.txt", ex.ToString());
                }
                finally
                {
                    desktop.Shutdown();
                }
            });
            return;
        }

        var shotIndex = Array.IndexOf(args, "--shot");
        if (shotIndex < 0 || shotIndex + 1 >= args.Length) return;

        var target = args[shotIndex + 1];
        var panelIndex = Array.IndexOf(args, "--panel");
        var panel = panelIndex >= 0 && panelIndex + 1 < args.Length ? args[panelIndex + 1] : null;
        if (args.Contains("--account-panel")) panel = "account";
        var expandIndex = Array.IndexOf(args, "--expand");
        var expand = expandIndex >= 0 && expandIndex + 1 < args.Length ? args[expandIndex + 1] : null;
        var scrollIndex = Array.IndexOf(args, "--scroll");
        var scrollTo = scrollIndex >= 0 && scrollIndex + 1 < args.Length &&
                       double.TryParse(args[scrollIndex + 1], out var parsedOffset)
            ? parsedOffset
            : double.NaN;
        var animIndex = Array.IndexOf(args, "--expand-anim");
        var animTool = animIndex >= 0 && animIndex + 1 < args.Length ? args[animIndex + 1] : null;
        var widthIndex = Array.IndexOf(args, "--width");
        var width = widthIndex >= 0 && widthIndex + 1 < args.Length &&
                    double.TryParse(args[widthIndex + 1], out var parsedWidth)
            ? parsedWidth
            : double.NaN;

        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try
            {
                await Task.Delay(1100);

                // 走查窄窗：钳到最小宽度附近，确认双列卡片与说明区不横向溢出
                if (!double.IsNaN(width))
                {
                    window.Width = width;
                    await Task.Delay(400);
                }

                if (panel is not null)
                {
                    await window.ShowForShotAsync(panel);
                    await Task.Delay(600);
                }

                // 走查展开说明：等动画跑完再截，确认揭示后的静态版式
                if (expand is not null && window.ExpandForShot(expand))
                {
                    await Task.Delay(500);
                }

                // 走查动画：逐帧采样，证明高度是渐变的而不是一步到位
                if (animTool is not null &&
                    args.IndexOf("--shot-metrics") is var metricsForAnim && metricsForAnim >= 0)
                {
                    await window.RecordExpandAnimationAsync(args[metricsForAnim + 1] + ".anim.txt", animTool);
                }

                var interactIndex = Array.IndexOf(args, "--interact");
                if (interactIndex >= 0 && interactIndex + 1 < args.Length &&
                    args.IndexOf("--shot-metrics") is var metricsForInteract && metricsForInteract >= 0)
                {
                    await window.RecordInteractionAsync(args[metricsForInteract + 1] + ".interact.txt",
                        args[interactIndex + 1]);
                }

                if (args.Contains("--app-interact") &&
                    args.IndexOf("--shot-metrics") is var metricsForApp && metricsForApp >= 0)
                {
                    window.RecordAppInteraction(args[metricsForApp + 1] + ".app.txt");
                }

                if (args.Contains("--expand-visual") &&
                    args.IndexOf("--shot-metrics") is var metricsForVisual && metricsForVisual >= 0)
                {
                    await window.RecordExpandVisualsAsync(args[metricsForVisual + 1] + ".visual.txt");
                }

                if (args.Contains("--title-click") &&
                    args.IndexOf("--shot-metrics") is var metricsForTitle && metricsForTitle >= 0)
                {
                    window.RecordTitleClickBubbling(args[metricsForTitle + 1] + ".title.txt");
                }

                if (args.Contains("--reduced-motion") &&
                    args.IndexOf("--shot-metrics") is var metricsForReduced && metricsForReduced >= 0)
                {
                    await window.RecordReducedMotionAsync(args[metricsForReduced + 1] + ".reduced.txt");
                }

                var repeatIndex = Array.IndexOf(args, "--repeat-toggle");
                if (repeatIndex >= 0 && repeatIndex + 1 < args.Length &&
                    int.TryParse(args[repeatIndex + 1], out var rounds) &&
                    args.IndexOf("--shot-metrics") is var metricsForRepeat && metricsForRepeat >= 0)
                {
                    await window.RecordRepeatedToggleAsync(
                        args[metricsForRepeat + 1] + ".repeat.txt", rounds);
                }

                var resizeIndex = Array.IndexOf(args, "--expand-resize");
                if (resizeIndex >= 0 && resizeIndex + 1 < args.Length &&
                    double.TryParse(args[resizeIndex + 1], out var resizeTo) &&
                    args.IndexOf("--shot-metrics") is var metricsForResize && metricsForResize >= 0)
                {
                    await window.RecordExpandResizeAsync(
                        args[metricsForResize + 1] + ".resize.txt", resizeTo);
                }

                if (args.Contains("--search-expand") &&
                    args.IndexOf("--shot-metrics") is var metricsForSearch && metricsForSearch >= 0)
                {
                    await window.RecordSearchExpandAsync(args[metricsForSearch + 1] + ".search.txt");
                }

                if (args.Contains("--binding-audit") &&
                    args.IndexOf("--shot-metrics") is var metricsForBind && metricsForBind >= 0)
                {
                    window.RecordDescriptionBinding(args[metricsForBind + 1] + ".binding.txt");
                }

                var searchIndex = Array.IndexOf(args, "--search");
                if (searchIndex >= 0 && searchIndex + 1 < args.Length &&
                    window.DataContext is MainViewModel viewModel)
                {
                    viewModel.SearchText = args[searchIndex + 1];
                    await Task.Delay(250);
                }

                if (args.Contains("--shot-bottom") &&
                    window.FindControl<ScrollViewer>("MainScroll") is { } scroll)
                {
                    scroll.UpdateLayout();
                    scroll.Offset = new Vector(0, scroll.Extent.Height);
                    await Task.Delay(300);
                }

                if (!double.IsNaN(scrollTo) &&
                    window.FindControl<ScrollViewer>("MainScroll") is { } sized)
                {
                    sized.UpdateLayout();
                    sized.Offset = new Vector(0, scrollTo);
                    await Task.Delay(300);
                }

                // 走查缝：把「有新版」的界面状态构造出来，否则全新进程里徽章永远为空，
                // 「提示条正确显示」这件事根本无从取证。只影响走查，不落盘、不改状态文件。
                var badgeIndex = Array.IndexOf(args, "--preview-update-badge");
                if (badgeIndex >= 0 && badgeIndex + 1 < args.Length)
                {
                    window.DataContextAsMainViewModel?.ArmPreviewBadge(args[badgeIndex + 1]);
                    // 绑定刷新与抽屉动画都需要时间；不等就会截到半透明的中间态。
                    await Task.Delay(400);
                }

                // 走查缝：把「正在下载 + 速度行」摆出来。
                // 真跑去下 60MB 不现实，这里只喂一段假进度给界面，不联网、不落盘。
                var speedIndex = Array.IndexOf(args, "--preview-update-speed");
                if (speedIndex >= 0 && window.DataContextAsMainViewModel is { } updateVm)
                {
                    updateVm.IsUpdateBusy = true;
                    updateVm.UpdateProgress = 45;
                    updateVm.UpdateStateText = "正在下载… 45%";
                    updateVm.UpdateProgressDetail = TeachCraft.Core.Updating.DownloadText.AmountLine(
                        new TeachCraft.Core.Updating.DownloadProgress(
                            Percent: 45,
                            BytesReceived: (long)(26.2 * 1024 * 1024),
                            TotalBytes: (long)(58.2 * 1024 * 1024),
                            BytesPerSecond: 1.42 * 1024 * 1024,
                            IsSizeKnown: true));
                    await Task.Delay(300);
                }

                var metricsIndex = Array.IndexOf(args, "--shot-metrics");
                if (metricsIndex >= 0 && metricsIndex + 1 < args.Length)
                    window.DumpCardLayout(args[metricsIndex + 1]);

                window.CaptureTo(target);
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".error.txt", ex.ToString());
                exitCode = 2;
            }
            finally
            {
                // 带退出码：这是为自动化补的确定契约（0 成功 / 2 失败）。
                // 注意这不是「修复卡死」——该分支本来就会退出，缺的只是可控退出码。
                desktop.Shutdown(exitCode);
            }
        });
    }

    /// <summary>走查专用归位云端假桥：三份合成名单，全部内存数据，不联网、不读凭据。</summary>
    private sealed class CaptureSeatGridCloudBridge : ISeatGridCloudBridge
    {
        public IReadOnlyList<SeatGridRosterEntry> Rosters { get; } = new[]
        {
            new SeatGridRosterEntry("demo-1", "高一(1)班", 1,
                new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), 42, SeatGridRosterFields.None),
            new SeatGridRosterEntry("demo-2", "高一(2)班", 2,
                new DateTimeOffset(2026, 11, 24, 9, 5, 0, TimeSpan.Zero), 40, SeatGridRosterFields.None),
            new SeatGridRosterEntry("demo-3", "高一(3)班", 3,
                new DateTimeOffset(2026, 11, 24, 9, 10, 0, TimeSpan.Zero), 45, SeatGridRosterFields.None),
        };

        public bool IsAvailable => true;
        public bool IsSignedIn => true;
        public string StatusText => "已登录（演示数据 · 不联网）";
        public string RosterConsentText => "共享名单默认只含姓名、性别、学号（演示）";
        public bool IsAutoSyncEnabled => false;
        public void SetAutoSyncEnabled(bool value) { }
        public SeatGridRosterFields LastRosterFields => SeatGridRosterFields.None;
        public void RememberRosterFields(SeatGridRosterFields fields) { }

        public string BuildSeatConsentText(string rosterName, int rosterVersion, string fieldsSummary)
            => $"{rosterName} v{rosterVersion} {fieldsSummary}";

        public event EventHandler? StateChanged { add { } remove { } }

        public Task<bool> EnsureSignedInAsync(Window owner) => Task.FromResult(true);
        public void OpenAccountSettings(Window owner) { }

        public Task<IReadOnlyList<SeatGridRosterEntry>> ListRostersAsync(CancellationToken ct)
            => Task.FromResult(Rosters);
        public Task<SeatGridRosterPick?> DownloadRosterAsync(string rosterId, CancellationToken ct)
            => Task.FromResult<SeatGridRosterPick?>(null);
        public Task<SeatGridRosterEntry> UploadRosterAsync(
            string name, IReadOnlyList<SeatGridRosterStudent> projectedStudents, SeatGridRosterFields fields,
            string? existingId, int expectedVersion, CancellationToken ct)
            => Task.FromResult(new SeatGridRosterEntry(
                existingId ?? "demo-new", name, expectedVersion + 1, DateTimeOffset.UtcNow,
                projectedStudents.Count, fields));
        public Task DeleteRosterAsync(string rosterId, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SeatGridCloudEntry>> ListDocumentsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SeatGridCloudEntry>>(Array.Empty<SeatGridCloudEntry>());
        public Task<SeatGridCloudPull?> DownloadDocumentAsync(string documentId, CancellationToken ct)
            => Task.FromResult<SeatGridCloudPull?>(null);
        public Task<SeatGridCloudSaveResult> UploadDocumentAsync(
            SeatGridCloudDocument document, string? replacingId, int expectedRevision, CancellationToken ct)
            => Task.FromResult(new SeatGridCloudSaveResult(
                new SeatGridCloudEntry(replacingId ?? "demo-new", document.Name, DateTimeOffset.UtcNow, 100, 1),
                expectedRevision > 0));
        public Task DeleteDocumentAsync(string documentId, CancellationToken ct) => Task.CompletedTask;
        public void RememberDocument(string? id, int revision) { }
    }

    /// <summary>走查专用辑册共享名单假桥：三份合成名单，只读、不联网、不读凭据。</summary>
    private sealed class CaptureHomeworkRosterBridge : IHomeworkRosterBridge
    {
        public IReadOnlyList<HomeworkRosterEntry> Entries { get; } = new[]
        {
            new HomeworkRosterEntry("demo-1", "高一(1)班", 1,
                new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), 42, "姓名、性别、学号"),
            new HomeworkRosterEntry("demo-2", "高一(2)班", 2,
                new DateTimeOffset(2026, 11, 24, 9, 5, 0, TimeSpan.Zero), 40, "姓名、性别、学号"),
            new HomeworkRosterEntry("demo-3", "高一(3)班", 3,
                new DateTimeOffset(2026, 11, 24, 9, 10, 0, TimeSpan.Zero), 45, "姓名、性别、学号"),
        };

        public bool IsAvailable => true;
        public event EventHandler? StateChanged { add { } remove { } }

        public Task<bool> EnsureSignedInAsync(Window owner) => Task.FromResult(true);
        public Task<IReadOnlyList<HomeworkRosterEntry>> ListRostersAsync(CancellationToken ct)
            => Task.FromResult(Entries);
        public Task<RosterImportResult> DownloadAsync(IReadOnlyList<string> rosterIds, CancellationToken ct)
            => Task.FromResult(new RosterImportResult([], [], [], 0));
        public void OpenAccountSettings(Window owner) { }
    }

    private static void CaptureWindowTo(Window target, string path)
    {
        var size = new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(target);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private static (string[] Images, string Pdf) CreateHomeworkDemoInputs(int imageCount = 2)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TeachCraft", "homework-shot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        for (var index = 1; index <= Math.Max(1, imageCount); index++)
        {
            var path = Path.Combine(directory, $"scan_{index}.png");
            using var bitmap = new SkiaSharp.SKBitmap(800, 1100);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                canvas.Clear(SkiaSharp.SKColor.Parse("#FBF7EE"));
                using var line = new SkiaSharp.SKPaint
                {
                    Color = SkiaSharp.SKColor.Parse("#D97757"),
                    StrokeWidth = 3,
                    IsAntialias = true,
                };
                canvas.DrawRect(new SkiaSharp.SKRect(40, 40, 760, 1060), line);
            }
            using var stream = File.Create(path);
            bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
            images.Add(path);
        }

        var pdf = Path.Combine(directory, "scan.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            for (var index = 0; index < 2; index++)
            {
                var page = document.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                graphics.DrawRectangle(PdfSharp.Drawing.XBrushes.LightGray, 40, 40, 160, 80);
            }
            document.Save(pdf);
        }
        return (images.ToArray(), pdf);
    }

    private static string CreateFillInDemoPage(string number, string directory)
    {
        const int gridX = 100, gridY = 100, gridWidth = 600, gridHeight = 1000;
        const int columns = 3, rows = 10;
        const double fillRatio = 0.60;

        var path = Path.Combine(directory, $"fillin-{number}.png");
        using var bitmap = new SkiaSharp.SKBitmap(900, 1200);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var grid = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.Black,
                Style = SkiaSharp.SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = false,
            };
            var cellWidth = gridWidth / columns;
            var cellHeight = gridHeight / rows;
            for (var c = 0; c <= columns; c++)
            {
                var x = gridX + c * cellWidth;
                canvas.DrawLine(x, gridY, x, gridY + gridHeight, grid);
            }
            for (var r = 0; r <= rows; r++)
            {
                var y = gridY + r * cellHeight;
                canvas.DrawLine(gridX, y, gridX + gridWidth, y, grid);
            }

            using var ink = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.Black,
                Style = SkiaSharp.SKPaintStyle.Fill,
                IsAntialias = false,
            };
            for (var c = 0; c < number.Length && c < columns; c++)
            {
                var digit = number[c] - '0';
                if (digit is < 0 or > 9) continue;
                var cellX = gridX + c * cellWidth;
                var cellY = gridY + digit * cellHeight;
                var fillWidth = cellWidth * fillRatio;
                var fillHeight = cellHeight * fillRatio;
                var fillX = cellX + (cellWidth - fillWidth) / 2;
                var fillY = cellY + (cellHeight - fillHeight) / 2;
                canvas.DrawRect(new SkiaSharp.SKRect(
                    (float)fillX, (float)fillY,
                    (float)(fillX + fillWidth), (float)(fillY + fillHeight)), ink);
            }
        }
        using var stream = File.Create(path);
        bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
        return path;
    }

    private static (string Template, string Roster) CreateCertificateShotInputs(bool portrait)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TeachCraft", "certificate-shot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var template = Path.Combine(directory, "synthetic-template.png");
        var roster = Path.Combine(directory, "synthetic-roster.xlsx");
        using (var bitmap = new SkiaSharp.SKBitmap(portrait ? 800 : 1400, portrait ? 1200 : 800))
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var line = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColor.Parse("#D97757"),
                   Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true })
        using (var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 56))
        using (var ink = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColor.Parse("#141413"), IsAntialias = true })
        {
            canvas.Clear(SkiaSharp.SKColor.Parse("#FAF9F5"));
            canvas.DrawRect(new SkiaSharp.SKRect(32, 32, bitmap.Width - 32, bitmap.Height - 32), line);
            canvas.DrawText("CERTIFICATE", bitmap.Width / 2f, bitmap.Height * 0.25f, SkiaSharp.SKTextAlign.Center, font, ink);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(template);
            data.SaveTo(stream);
        }
        using (var workbook = new ClosedXML.Excel.XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("合成名单");
            sheet.Cell(1, 1).Value = "姓名";
            sheet.Cell(1, 2).Value = "奖项";
            sheet.Cell(2, 1).Value = "示例甲";
            sheet.Cell(2, 2).Value = "学习进步奖";
            sheet.Cell(3, 1).Value = "示例乙";
            sheet.Cell(3, 2).Value = "阅读之星";
            sheet.Cell(4, 1).Value = "示例甲";
            sheet.Cell(4, 2).Value = "积极参与奖";
            workbook.SaveAs(roster);
        }
        return (template, roster);
    }

    /// <summary>
    /// 首次引导的走查通道：
    ///   --oobe-shot &lt;png&gt;      截图（配合 --oobe-step 指定页）
    ///   --oobe-step &lt;n&gt;        指定页，1–4，越界钳制
    ///   --oobe-metrics &lt;txt&gt;   回读版式数值（卡宽 / 是否裁切 / 滚动条数 / 内容区是否禁用）
    ///   --oobe-interact &lt;txt&gt;  回读输入隔离五项
    ///   --oobe-reset            删除本机引导状态后退出（唯一允许碰真实状态的参数）
    /// 参数不存在时返回 false，交回既有的走查分支。
    /// </summary>
    private static bool CaptureOobeIfRequested(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        string[] args)
    {
        var resetIndex = Array.IndexOf(args, "--oobe-reset");
        if (resetIndex >= 0)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TeachCraft", "Oobe", "state.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 删不掉就当作本来就没有；退出码仍为 0。
            }

            Environment.Exit(0);
            return true;
        }

        var shotIndex = Array.IndexOf(args, "--oobe-shot");
        var metricsIndex = Array.IndexOf(args, "--oobe-metrics");
        var interactIndex = Array.IndexOf(args, "--oobe-interact");
        var keysIndex = Array.IndexOf(args, "--oobe-keys");
        var motionIndex = Array.IndexOf(args, "--oobe-motion");
        var hasStep = Array.IndexOf(args, "--oobe-step") >= 0;
        var forceOpen = args.Contains("--oobe");

        if (shotIndex < 0 && metricsIndex < 0 && interactIndex < 0 && keysIndex < 0 &&
            motionIndex < 0 && !forceOpen && !hasStep)
            return false;

        var shot = shotIndex >= 0 && shotIndex + 1 < args.Length ? args[shotIndex + 1] : null;
        var metrics = metricsIndex >= 0 && metricsIndex + 1 < args.Length ? args[metricsIndex + 1] : null;
        var interact = interactIndex >= 0 && interactIndex + 1 < args.Length ? args[interactIndex + 1] : null;
        var keys = keysIndex >= 0 && keysIndex + 1 < args.Length ? args[keysIndex + 1] : null;
        var motion = motionIndex >= 0 && motionIndex + 1 < args.Length ? args[motionIndex + 1] : null;

        var stepIndex = Array.IndexOf(args, "--oobe-step");
        var step = 1;
        if (stepIndex >= 0 && stepIndex + 1 < args.Length)
            int.TryParse(args[stepIndex + 1], out step);
        step = Math.Clamp(step, 1, 4);

        var widthIndex = Array.IndexOf(args, "--oobe-width");
        var heightIndex = Array.IndexOf(args, "--oobe-height");

        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try
            {
                await Task.Delay(900);
                if (widthIndex >= 0 && widthIndex + 1 < args.Length &&
                    double.TryParse(args[widthIndex + 1], out var w) && w > 0)
                    window.Width = w;
                if (heightIndex >= 0 && heightIndex + 1 < args.Length &&
                    double.TryParse(args[heightIndex + 1], out var h) && h > 0)
                    window.Height = h;
                if (widthIndex >= 0 || heightIndex >= 0) await Task.Delay(400);

                var respectDecision = metrics is not null && shot is null && !args.Contains("--oobe");
                window.ShowOobeForShot(step, force: !respectDecision);
                // 焦点是在 IsVisible 变 true 之后异步抢的，读数前要让它落定。
                await Task.Delay(700);
                window.UpdateLayout();

                if (metrics is not null) window.DumpOobeMetrics(metrics);
                if (interact is not null) window.DumpOobeInteraction(interact);
                if (keys is not null) window.DumpOobeKeys(keys);
                if (motion is not null) await window.DumpOobeMotionAsync(motion);
                if (shot is not null) window.CaptureTo(shot);
            }
            catch (Exception ex)
            {
                exitCode = 2;
                try
                {
                    File.WriteAllText((shot ?? metrics ?? interact ?? "oobe") + ".error.txt", ex.ToString());
                }
                catch (Exception) { }
            }
            finally
            {
                desktop.Shutdown(exitCode);
            }
        });

        return true;
    }

    private sealed class ScanDemoScannerService : Homework.App.Scanning.IScannerService
    {
        public bool IsSupported => true;

        public Task<IReadOnlyList<Homework.App.Scanning.ScannerInfo>> GetScannersAsync(
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<Homework.App.Scanning.ScannerInfo>>(
                new[]
                {
                    new Homework.App.Scanning.ScannerInfo("canon-c5560i", "Canon iR-ADV C5560i"),
                    new Homework.App.Scanning.ScannerInfo("canon-lide-400", "Canon LiDE 400"),
                });

        public Task<Homework.Core.Scanning.ScannerCapabilities> GetCapabilitiesAsync(
            Homework.App.Scanning.ScannerInfo scanner,
            CancellationToken token) =>
            Task.FromResult(scanner.Id == "canon-c5560i"
                ? new Homework.Core.Scanning.ScannerCapabilities(
                    [
                        Homework.Core.Scanning.ScanSource.FeederSingle,
                        Homework.Core.Scanning.ScanSource.FeederDuplex,
                    ],
                    [
                        Homework.Core.Scanning.ScanPageSize.A4,
                        Homework.Core.Scanning.ScanPageSize.A3,
                    ],
                    [200, 300, 600],
                    [
                        Homework.Core.Scanning.ScanColorMode.BlackWhite,
                        Homework.Core.Scanning.ScanColorMode.Grayscale,
                    ],
                    100,
                    true)
                : Homework.Core.Scanning.ScannerCapabilities.All);

        public Task<Homework.App.Scanning.ScanOutput> AcquireWithDriverUiAsync(
            CancellationToken token) =>
            Task.FromResult(new Homework.App.Scanning.ScanOutput(
                ["driver-ui-demo.png"], string.Empty));

        public Task<Homework.App.Scanning.ScanOutput> ScanAsync(
            Homework.App.Scanning.ScannerInfo scanner,
            Homework.Core.Scanning.ScanSettings settings,
            IProgress<int>? progress,
            CancellationToken token) =>
            Task.FromResult(new Homework.App.Scanning.ScanOutput(
                Array.Empty<string>(), string.Empty));
    }

    private sealed class ScanDemoImportSink : Homework.App.Scanning.IScanImportSink
    {
        public bool HasCurrentBatch => true;

        public Task<Homework.App.Scanning.ScanImportOutcome> ImportAsync(
            IReadOnlyList<string> paths,
            bool createNewBatch,
            CancellationToken token) =>
            Task.FromResult(Homework.App.Scanning.ScanImportOutcome.Ok(3, "批次1"));
    }

    private sealed class CaptureAccountService : ICloudAccountService
    {
        public bool IsSignedIn => Current is not null;
        public CloudAccount? Current { get; private set; }
        public event EventHandler? StateChanged;

        public void SetSignedIn(string displayName)
        {
            Current = new CloudAccount("capture-user", displayName, Array.Empty<string>(),
                SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public CloudSignInRequest CreateSignInRequest() => new(
            new Uri("https://example.invalid/oauth/authorize"), "capture-state", "capture-verifier",
            SectlPlatformDefaults.RedirectUri, DateTimeOffset.UtcNow);

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException("走查不执行登录。");

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("走查不取令牌。");

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            Current = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureSettingsStore : ICloudSyncSettingsStore
    {
        private CloudSyncSettings _settings = CloudSyncSettings.Default;
        public CloudSyncSettings Load() => _settings;
        public void Save(CloudSyncSettings settings) => _settings = settings;
        public string Path => "";
    }
}
