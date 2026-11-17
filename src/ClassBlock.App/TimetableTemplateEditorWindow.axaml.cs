using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassBlock.Core;
using ClassBlock.Core.Timetable;
using TeachCraft.Shared;

namespace ClassBlock.App;

public partial class TimetableTemplateEditorWindow : Window
{
    /// <summary>保存成功时带回模板编号；关闭/删除时为 null。</summary>
    public string? SavedTemplateId { get; private set; }

    private bool _closeReady;
    private bool _guardOpen;

    public TimetableTemplateEditorWindow()
        : this(new TimetableTemplate(), startAsCopy: false)
    {
    }

    public TimetableTemplateEditorWindow(TimetableTemplate source, bool startAsCopy = false)
    {
        InitializeComponent();
        DataContext = new TimetableTemplateEditorViewModel(source, startAsCopy);
        Opened += (_, _) => WindowFit.Clamp(this);
    }

    private TimetableTemplateEditorViewModel ViewModel => (TimetableTemplateEditorViewModel)DataContext!;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanSave)
        {
            SetEditorStatus("请先填写模板名称并至少保留一行。");
            return;
        }

        try
        {
            var template = ViewModel.BuildTemplate();
            TimetableTemplateIO.SaveCustom(template);
            SavedTemplateId = template.Id;
            _closeReady = true;
            Close();
        }
        catch (ClassBlockException ex)
        {
            SetEditorStatus(ex.Message);
        }
        catch (Exception ex)
        {
            SetEditorStatus($"保存模板失败：{ex.Message}");
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBuiltin || !ViewModel.IsPersisted) return;

        var delete = await ConfirmDialog.ShowAsync(
            this, "删除模板？",
            $"「{ViewModel.TemplateName}」将被删除，且无法恢复（会先留一份 .bak 备份）。",
            "删除", "取消", danger: true);
        if (!delete) return;

        try
        {
            TimetableTemplateIO.DeleteCustom(ViewModel.TemplateId, backup: true);
            _closeReady = true;
            Close();
        }
        catch (Exception ex)
        {
            SetEditorStatus($"删除模板失败：{ex.Message}");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeReady || _guardOpen || !ViewModel.IsDirty) return;

        e.Cancel = true;
        _guardOpen = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var discard = await ConfirmDialog.ShowAsync(
            this, "放弃未保存的模板改动？",
            "当前模板的改动尚未保存，关闭后会丢失。", "放弃并关闭", "继续编辑", danger: true);
        _guardOpen = false;
        if (discard)
        {
            _closeReady = true;
            Close();
        }
    }

    private void SetEditorStatus(string message)
    {
        EditorStatus.Text = message;
        EditorStatus.Foreground = new SolidColorBrush(Color.Parse("#C6613F"));
    }
}
