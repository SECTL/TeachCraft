using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Homework.Core.Roster;
using TeachCraft.Shared;

namespace Homework.App.Cloud;

/// <summary>
/// 共享名单拉取弹窗。窗口只负责展示与把结果交回调用方，
/// 名单映射、重复名判定与状态机全在 <see cref="HomeworkRosterPickerViewModel"/>。
/// </summary>
public sealed partial class SharedRosterPickerWindow : Window
{
    private CancellationTokenSource? _opsCts;
    private bool _closed;

    public SharedRosterPickerWindow()
        : this(new HomeworkRosterPickerViewModel(NullHomeworkRosterBridge.Instance))
    {
    }

    public SharedRosterPickerWindow(HomeworkRosterPickerViewModel viewModel, bool refreshOnOpen = false)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            WindowFit.Clamp(this);
            if (refreshOnOpen) _ = RefreshWithWindowLifetimeAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _opsCts?.Cancel();
            (DataContext as IDisposable)?.Dispose();
        };
    }

    private HomeworkRosterPickerViewModel ViewModel => (HomeworkRosterPickerViewModel)DataContext!;

    /// <summary>只有点「导入」并成功拉取后才有值。</summary>
    public RosterImportResult? PulledResult { get; private set; }

    private void OnSelectAll(object? sender, RoutedEventArgs e) => ViewModel.SelectAll();

    private void OnClearSelection(object? sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnRefresh(object? sender, RoutedEventArgs e)
        => await RefreshWithWindowLifetimeAsync();

    private async void OnSignIn(object? sender, RoutedEventArgs e)
        => await ViewModel.EnsureSignedInAsync(this);

    private void OnAccountSettings(object? sender, RoutedEventArgs e)
        => ViewModel.OpenAccountSettings(this);

    private async void OnPull(object? sender, RoutedEventArgs e) => await PullAsync();

    private async Task RefreshWithWindowLifetimeAsync()
    {
        if (_closed) return;
        _opsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _opsCts = cts;
        try
        {
            await ViewModel.RefreshAsync(cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_opsCts, cts)) _opsCts = null;
            cts.Dispose();
        }
    }

    private async Task PullAsync()
    {
        if (_closed) return;
        _opsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _opsCts = cts;
        RosterImportResult? result;
        try
        {
            result = await ViewModel.PullAsync(cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_opsCts, cts)) _opsCts = null;
            cts.Dispose();
        }
        if (result is null || _closed) return;
        PulledResult = result;
        Close(true);
    }
}
