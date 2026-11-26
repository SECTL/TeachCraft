using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core;
using Homework.Core.Models;
using Homework.Core.Recognition;
using TeachCraft.Shared;

namespace Homework.App;

/// <summary>模板生成范围：全部班级 / 某个班级 / 通用（不印班级名）。</summary>
public enum FillInTemplateScope
{
    AllClasses,
    OneClass,
    Generic,
}

/// <summary>模板输出格式。</summary>
public enum FillInTemplateFormat
{
    Png,
    Pdf,
}

/// <summary>范围下拉的一行（枚举值 + 中文显示）。</summary>
public sealed record FillInScopeOption(FillInTemplateScope Value, string Label);

/// <summary>格式下拉的一行（枚举值 + 中文显示）。</summary>
public sealed record FillInFormatOption(FillInTemplateFormat Value, string Label);

/// <summary>
/// 「生成填涂模板」弹窗的状态。列数默认取名单自动推断值（1–12），范围 / 格式 / 输出路径
/// 由老师明确指定，绝不猜列。所有渲染都走 Core 的 <see cref="FillInTemplateRenderer"/>，
/// 本类只负责组装选项、落盘与预览位图，不引入任何网络或平台专属代码。
/// </summary>
public sealed partial class FillInTemplateViewModel : ObservableObject
{
    private readonly HomeworkBatch _batch;
    private int _columns;
    private FillInTemplateScope _scope = FillInTemplateScope.AllClasses;
    private HomeworkClass? _selectedClass;
    private FillInTemplateFormat _format = FillInTemplateFormat.Png;
    private string _outputPath = string.Empty;
    private Bitmap? _previewImage;
    private string _statusText = string.Empty;

    public FillInTemplateViewModel(HomeworkBatch batch)
    {
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
        _columns = batch.AutoColumns();
        _selectedClass = batch.Classes.FirstOrDefault();
    }

    public int Columns
    {
        get => _columns;
        set => SetProperty(ref _columns, value);
    }

    public FillInTemplateScope Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value))
            {
                OnPropertyChanged(nameof(IsOneClass));
                OnPropertyChanged(nameof(SelectedScopeOption));
            }
        }
    }

    public HomeworkClass? SelectedClass
    {
        get => _selectedClass;
        set => SetProperty(ref _selectedClass, value);
    }

    public FillInTemplateFormat Format
    {
        get => _format;
        set
        {
            if (SetProperty(ref _format, value))
                OnPropertyChanged(nameof(SelectedFormatOption));
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value);
    }

    /// <summary>列数越界、缺输出路径、或选了「某个班级」却没选班时都不能生成。</summary>
    public bool CanGenerate =>
        Columns >= FillInOptions.MinColumns && Columns <= FillInOptions.MaxColumns
        && !string.IsNullOrWhiteSpace(OutputPath)
        && (Scope != FillInTemplateScope.OneClass || SelectedClass is not null);

    public bool IsOneClass => Scope == FillInTemplateScope.OneClass;

    public IReadOnlyList<HomeworkClass> Classes => _batch.Classes;

    public IReadOnlyList<FillInScopeOption> Scopes { get; } =
    [
        new(FillInTemplateScope.AllClasses, "全部班级"),
        new(FillInTemplateScope.OneClass, "某个班级"),
        new(FillInTemplateScope.Generic, "通用（不印班级）"),
    ];

    public IReadOnlyList<FillInFormatOption> Formats { get; } =
    [
        new(FillInTemplateFormat.Png, "PNG 图片"),
        new(FillInTemplateFormat.Pdf, "PDF 文档"),
    ];

    public FillInScopeOption? SelectedScopeOption
    {
        get => Scopes.FirstOrDefault(o => o.Value == _scope);
        set
        {
            if (value is not null) Scope = value.Value;
        }
    }

    public FillInFormatOption? SelectedFormatOption
    {
        get => Formats.FirstOrDefault(o => o.Value == _format);
        set
        {
            if (value is not null) Format = value.Value;
        }
    }

    /// <summary>组装一份模板选项；标题固定，班级名由调用方按范围给出，列数取当前设置。</summary>
    public FillInTemplateOptions BuildOptions(string? className) =>
        new("作业填涂卡", className, Columns);

    /// <summary>
    /// 按范围算出要写出的目标。单班 / 通用时 OutputPath 是文件路径；全部班级时是目录，
    /// 每个班一份「填涂卡_{班级}.{ext}」。班级名中的非法文件字符替换为下划线。
    /// </summary>
    public IReadOnlyList<(string? ClassName, string OutputPath)> BuildTargets()
    {
        var ext = Format == FillInTemplateFormat.Pdf ? "pdf" : "png";
        return Scope switch
        {
            FillInTemplateScope.OneClass => SelectedClass is null
                ? []
                : [(SelectedClass.Name, OutputPath)],
            FillInTemplateScope.Generic => [(null, OutputPath)],
            _ => _batch.Classes
                .Select(c => ((string?)c.Name,
                    Path.Combine(OutputPath, $"填涂卡_{Sanitize(c.Name)}.{ext}")))
                .ToList(),
        };
    }

    /// <summary>把班级名清成安全的文件名片段；空名回落到「班级」。</summary>
    internal static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "班级";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "班级" : cleaned;
    }

    /// <summary>逐目标渲染并落盘；异常只写进状态栏，绝不抛回命令。</summary>
    public async Task GenerateAsync()
    {
        try
        {
            if (!CanGenerate)
            {
                StatusText = "请检查列数、范围与输出路径。";
                return;
            }

            var targets = BuildTargets();
            if (targets.Count == 0)
            {
                StatusText = "没有可生成的班级。";
                return;
            }

            // 先校验全部目标再落盘：任何一个目标已存在就整体拒绝，绝不覆盖导入来源 / 旧模板，
            // 也不留下「写了一半」的批次。HomeworkException 由下方 catch 原样写进状态栏。
            foreach (var (_, outputPath) in targets)
            {
                if (File.Exists(outputPath))
                    throw new HomeworkException($"文件已存在，拒绝覆盖：{outputPath}");
            }

            await Task.Run(() =>
            {
                foreach (var (className, outputPath) in targets)
                {
                    var options = BuildOptions(className);
                    var bytes = Format == FillInTemplateFormat.Pdf
                        ? FillInTemplateRenderer.RenderPdf(options)
                        : FillInTemplateRenderer.RenderPng(options);
                    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllBytes(outputPath, bytes);
                }
            });

            StatusText = $"已生成 {targets.Count} 份模板。";
        }
        catch (HomeworkException ex)
        {
            StatusText = ErrorText.Describe(ex, "生成模板");
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe(ex, "生成模板");
        }
    }

    /// <summary>把第一个目标渲染成 PNG 并解码为预览位图；先替换再释放旧位图。</summary>
    public async Task LoadPreviewAsync()
    {
        try
        {
            var targets = BuildTargets();
            var className = targets.Count > 0 ? targets[0].ClassName : null;
            var options = BuildOptions(className);
            var bytes = await Task.Run(() => FillInTemplateRenderer.RenderPng(options));
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            var previous = _previewImage;
            PreviewImage = bitmap;
            previous?.Dispose();
        }
        catch (HomeworkException ex)
        {
            StatusText = ErrorText.Describe(ex, "预览模板");
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe(ex, "预览模板");
        }
    }
}
