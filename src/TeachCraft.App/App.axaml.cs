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
using TeachCraft.App.ViewModels;
using TeachCraft.App.Views;
using TeachCraft.Core.Updating;

namespace TeachCraft.App;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;

    /// <summary>
    /// 释放单实例锁。更新安装前必须调用，否则重启的新进程会被自己的锁挡住。
    /// </summary>
    private void ReleaseSingleInstance()
    {
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
            var args = desktop.Args ?? Array.Empty<string>();
            var isWalkthrough = args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
            if (!isWalkthrough)
            {
                _singleInstance = new SingleInstanceGuard(@"Local\TeachCraft.App");
                if (!_singleInstance.TryAcquire())
                {
                    // 已有实例在跑：直接退出，不再开第二个窗口。
                    Environment.Exit(0);
                    return;
                }
            }

            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // 真正启动安装包 / 替换文件只能由 App 层做：它决定何时关窗、何时退出、何时重启。
            viewModel.InstallerRequested += (path, plan) => plan.Action == InstallerAction.ReplacePortable
                ? Task.FromResult(this.RunPortableReplace(desktop, path, plan))
                : this.RunInstallerAsync(desktop, path, plan);

            // 启动时清掉上次被强杀留下的半截下载（进程被杀时 finally 不会执行）。
            // 这一步不联网、不碰状态文件，失败也不影响启动。
            if (!isWalkthrough)
            {
                try { new UpdateDownloader(SharedHandlers.Default).CleanupStaleParts(); }
                catch (Exception) { /* 清理失败不该拦住启动 */ }

                // 上一次绿色版自替换的结果。失败时用户刚经历「程序关了又开回来还是旧的」，
                // 不说清楚他会以为更新没生效，反复点。
                ReportLastPortableReplace(viewModel);
            }

            if (!CaptureGradeStripIfRequested(desktop) && !CaptureSeatGridIfRequested(desktop) &&
                !CaptureClassBlockIfRequested(desktop) && !CaptureGradeWeaveIfRequested(desktop) &&
                !CaptureGradeWeaveTimetableIfRequested(desktop) &&
                !CaptureGradeWeaveAssignmentIfRequested(desktop) &&
                !CaptureGradeWeaveConflictIfRequested(desktop) &&
                !CaptureMeetRosterIfRequested(desktop) &&
                !RunSliderBenchIfRequested(desktop) &&
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

    /// <summary>设计走查：打开「归位」并装载演示名单后截图。</summary>
    private static bool CaptureSeatGridIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, "--seatgrid-shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        var target = args[index + 1];
        var empty = index + 2 < args.Length && args[index + 2] == "empty";

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var window = new SeatGrid.App.SeatGridWindow();
                if (!empty) window.LoadDemoData();
                window.Show();
                await Task.Delay(1600);
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
        InstallerPlan plan)
    {
        try
        {
            if (!File.Exists(setupPath))
                return "安装包不见了，请重新下载。";

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
                StartRestartHelper(installer.Id, Environment.ProcessId, exe);
            }

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
    private string RunPortableReplace(
        IClassicDesktopStyleApplicationLifetime desktop,
        string archivePath,
        InstallerPlan plan)
    {
        if (Environment.ProcessPath is not { } exe)
            return "认不出当前程序的路径，无法自动替换，请手动解压更新包。";

        var targetDir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(targetDir))
            return "认不出当前程序所在的文件夹，无法自动替换，请手动解压更新包。";

        try
        {
            StartPortableReplaceHelper(archivePath, targetDir, exe, Environment.ProcessId);

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

    /// <summary>
    /// 读取上一次绿色版自替换的结果并显示给用户。
    ///
    /// 助手进程写 `last-replace.txt`：`OK` 或 `FAIL <原因>`。
    /// 成功时悄悄删掉，不打扰；失败时必须说出来——用户刚经历「程序关了又开回来」，
    /// 不说清楚他会以为更新没生效，反复点「下载并安装」。
    /// </summary>
    private static void ReportLastPortableReplace(MainViewModel viewModel)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeachCraft", "Update", "last-replace.txt");

            if (!File.Exists(path)) return;

            var content = File.ReadAllText(path).Trim();
            File.Delete(path);

            if (content.StartsWith("FAIL", StringComparison.Ordinal))
            {
                var reason = content.Length > 5 ? content[5..].Trim() : "未知原因";
                viewModel.SetStatus($"上次自动替换没有成功：{reason}。请手动解压更新包覆盖旧文件。", error: true);
            }
            else if (content == "OK")
            {
                viewModel.SetStatus("已更新到新版本。");
            }
        }
        catch (Exception)
        {
            // 读不到就算了，不能因为一条提示拦住启动。
        }
    }

    /// <summary>重启帮手失败时留一条本机痕迹，供用户与支持者定位。</summary>
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
                $"自动重启没能安排：{ex.Message}{Environment.NewLine}" +
                $"请手动打开 TeachCraft。{Environment.NewLine}{DateTimeOffset.Now:O}{Environment.NewLine}");
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
}
