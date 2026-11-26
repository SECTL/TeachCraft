using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ClassBlock.Core;
using ClassBlock.Core.Import;
using ClassBlock.Core.Rendering;
using ClassBlock.Core.Timetable;
using TeachCraft.Shared;

namespace ClassBlock.App;

public partial class ClassBlockWindow : Window
{
    private string? _masterPath;

    public ClassBlockWindow() : this(new ClassBlockViewModel())
    {
    }

    public ClassBlockWindow(ClassBlockViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => WindowFit.Clamp(this);
    }

    private ClassBlockViewModel ViewModel => (ClassBlockViewModel)DataContext!;

    protected override void OnClosed(EventArgs e)
    {
        // 先取消仍在跑的渲染，再释放当前位图，避免后台任务回写一张无人引用的图。
        ViewModel.CancelPendingPreview();
        ViewModel.Preview = null;
        base.OnClosed(e);
    }

    /// <summary>设计走查用：直接导入总表并等待课表渲染完成。</summary>
    public async Task LoadMasterAsync(string path)
    {
        var master = await Task.Run(() => MasterScheduleImporter.Import(path));
        ViewModel.LoadMaster(master, path);
        await ViewModel.RefreshPreviewAsync();
        _masterPath = path;
    }

    /// <summary>设计走查用：选中指定班级并等待课表刷新。</summary>
    public async Task SelectClassAsync(string key)
    {
        var item = ViewModel.FilteredClasses.FirstOrDefault(c => c.Key == key || c.Name == key);
        if (item is null) return;

        ViewModel.SelectedClass = item;
        await ViewModel.RefreshPreviewAsync();
    }

    public async Task<string?> ShowRemoteClassAsync(string? className)
    {
        if (!string.IsNullOrWhiteSpace(className))
        {
            if (!ViewModel.HasMaster)
                throw new InvalidOperationException("时序尚未导入年级课程总表。");
            if (!ViewModel.TrySelectClassRemote(className, out var matched))
                throw new InvalidOperationException($"时序总表中未找到班级「{className.Trim()}」。");

            await ViewModel.RefreshPreviewAsync();
            return matched;
        }

        return ViewModel.SelectedClass?.Name;
    }

    public (bool HasMaster, string Grade, string Term, string? SelectedClass, IReadOnlyList<string> Classes) ReadRemoteSummary()
        => ViewModel.ReadRemoteSummary();

    public void CaptureTo(string path)
    {
        var pixelSize = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    // ----------------------------- 作息模板 -----------------------------

    private async void OnEditTemplate(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTemplate is null) return;

        var editor = new TimetableTemplateEditorWindow(ViewModel.SelectedTemplate);
        await editor.ShowDialog(this);

        if (editor.SavedTemplateId is not null)
            ViewModel.AdoptSavedTemplate(editor.SavedTemplateId);
        else
            ViewModel.LoadTemplates();
    }

    private async void OnNewTemplate(object? sender, RoutedEventArgs e)
    {
        var source = ViewModel.SelectedTemplate ?? TimetableTemplates.DefaultForGrade(null);
        var editor = new TimetableTemplateEditorWindow(source, startAsCopy: true);
        await editor.ShowDialog(this);

        if (editor.SavedTemplateId is not null)
            ViewModel.AdoptSavedTemplate(editor.SavedTemplateId);
    }

    // ----------------------------- 导入 -----------------------------

    private void OnClearClassSearch(object? sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        ViewModel.SetStatus("已清除搜索");
    }

    private async void OnImportMaster(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenAsync("导入课程总表", new FilePickerFileType("Excel 总表")
        {
            // .xls 也放进来：Core 里有一条「请先把 Excel 另存为 xlsx」的友好提示，
            // 过滤器挡在外面它就永远没机会说出来，老师只会看到文件「隐身」（审计 P2-11）。
            Patterns = new[] { "*.xlsx", "*.xls" },
        });
        if (path is null) return;

        // 导入是整表替换：已有的班级选择、标题、导出的图都会作废。
        // 时序是五只应用里唯一没有确认弹窗的（审计 P2-36）。
        if (ViewModel.HasMaster && !await ConfirmDialog.ShowAsync(
                this, "替换当前总表？",
                $"当前已识别 {ViewModel.MasterSummary}，导入新总表会整体替换，无法撤销。",
                "替换", "取消", danger: true))
            return;

        ViewModel.BeginBusy("正在读取总表…", "识别年级 / 班级 / 五天课表与课服、午学、晚修；文件只在本机处理。");
        try
        {
            var master = await Task.Run(() => MasterScheduleImporter.Import(path));
            ViewModel.LoadMaster(master, path);
            _masterPath = path;
        }
        catch (Exception ex)
        {
            Report(ex, "导入总表失败");
        }
        finally
        {
            ViewModel.EndBusy();
        }
    }

    // ----------------------------- 打印 / 导出 -----------------------------

    private async void OnPrint(object? sender, RoutedEventArgs e)
    {
        ViewModel.BeginBusy("正在生成 PDF…", "把课程表渲染成高分辨率图并排进 A4，通常几秒。");
        try
        {
            var document = ViewModel.BuildDocument();
            var directory = Path.Combine(Path.GetTempPath(), "TeachCraft", "ClassBlock");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SafeFileName("-课程表.pdf"));

            await Task.Run(() => TimetableRenderer.RenderPdf(document, path));

            // 渲染成功与「唤起阅读器失败」必须分开报（照归位同款，审计 P3-23）：
            // 没装 PDF 阅读器的新系统上，PDF 其实已经生成在临时目录里，
            // 旧实现说「生成打印文件失败」，老师重试永远失败，也永远拿不到那个文件。
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                ViewModel.SetStatus("已生成打印 PDF，请在打开的阅读器里打印。");
            }
            catch (Exception)
            {
                ViewModel.SetStatus(
                    $"课程表 PDF 已生成，但没有找到能打开它的程序：{path}。请手动打开该文件。", true);
            }
        }
        catch (Exception ex)
        {
            Report(ex, "生成打印文件失败");
        }
        finally
        {
            ViewModel.EndBusy();
        }
    }

    private async void OnExportImage(object? sender, RoutedEventArgs e)
    {
        var path = await PickSaveAsync("导出课程表图片", SafeFileName(".png"), ".png",
            new FilePickerFileType("PNG 图片") { Patterns = new[] { "*.png" } });
        if (path is null) return;

        if (IsSamePath(path, _masterPath))
        {
            ViewModel.SetStatus("导出路径不能覆盖已导入的总表文件。", true);
            return;
        }

        ViewModel.BeginBusy("正在导出图片…", "按当前课表渲染 PNG，文件只写在本机。");
        try
        {
            var document = ViewModel.BuildDocument();
            await Task.Run(() => TimetableRenderer.RenderPng(document, path, 2.5f));
            ViewModel.SetStatus($"已导出图片：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Report(ex, "导出图片失败");
        }
        finally
        {
            ViewModel.EndBusy();
        }
    }

    // ----------------------------- 文件选择 -----------------------------

    private async Task<string?> PickOpenAsync(string title, FilePickerFileType fileType)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { fileType },
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickSaveAsync(
        string title,
        string suggestedName,
        string extension,
        FilePickerFileType fileType)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = new[] { fileType },
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    private string SafeFileName(string suffix)
    {
        var title = string.IsNullOrWhiteSpace(ViewModel.Title) ? "课程表" : ViewModel.Title.Trim();
        return BuildSuggestedFileName(title, suffix);
    }

    /// <summary>
    /// 建议文件名。标题本身常以「课程表」结尾（默认就是「九（1）班课程表」），
    /// 再拼「-课程表.pdf」会得到「九（1）班课程表-课程表.pdf」（审计 P3-29）。
    /// 静态且不依赖窗口实例，便于单测。
    /// </summary>
    public static string BuildSuggestedFileName(string title, string suffix)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "课程表" : title.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        if (suffix.StartsWith("-课程表", StringComparison.Ordinal) &&
            name.EndsWith("课程表", StringComparison.Ordinal))
        {
            suffix = suffix["-课程表".Length..];
        }

        return name + suffix;
    }

    private static bool IsSamePath(string path, string? other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(other),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Report(Exception ex, string prefix)
    {
        // Describe 原样透传业务异常的中文提示，其余异常只给中文话术（审计 P2-13）。
        // 前缀已带「失败」，去掉尾巴只传动词短语，避免拼出「失败失败」。
        var action = prefix.EndsWith("失败", StringComparison.Ordinal)
            ? prefix[..^"失败".Length]
            : prefix;
        ViewModel.SetStatus(ErrorText.Describe(ex, action), true);
    }
}
