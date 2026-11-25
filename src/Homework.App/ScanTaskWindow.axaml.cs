using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Homework.App.Scanning;
using Homework.Core.Scanning;
using TeachCraft.Shared;

namespace Homework.App;

public partial class ScanTaskWindow : Window
{
    private readonly ScanTaskViewModel _viewModel;
    private readonly ScanTaskPreferencesStore _preferencesStore;
    private ScanInboxWatcher? _watcher;
    private bool _closing;

    public ScanTaskWindow() : this(CreateStandaloneViewModel())
    {
    }

    public ScanTaskWindow(ScanTaskViewModel viewModel, ScanTaskPreferencesStore? preferencesStore = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _preferencesStore = preferencesStore ?? new ScanTaskPreferencesStore();
        DataContext = viewModel;
        Opened += OnOpened;
        Activated += OnActivated;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public ScanTaskViewModel ViewModel => _viewModel;

    private static ScanTaskViewModel CreateStandaloneViewModel()
    {
        var preferences = new ScanTaskPreferencesStore().Load();
        return new ScanTaskViewModel(
            ScannerServiceFactory.Create(),
            new NullScanImportSink(),
            preferences);
    }

    // ----------------------------- 生命周期 -----------------------------

    private async void OnOpened(object? sender, EventArgs e)
    {
        WindowFit.Clamp(this);
        _viewModel.RefreshCurrentBatch();
        if (_viewModel.IsWindows)
            await _viewModel.RefreshScannersAsync();
    }

    private void OnActivated(object? sender, EventArgs e) => _viewModel.RefreshCurrentBatch();

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        StopWatcher();
        _preferencesStore.TrySave(_viewModel.ToPreferences());
        _viewModel.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopWatcher();
        _viewModel.Dispose();
    }

    // ----------------------------- 模式 -----------------------------

    private void OnSwitchDirect(object? sender, RoutedEventArgs e) => _viewModel.SwitchToDirect();

    private void OnSwitchInbox(object? sender, RoutedEventArgs e) => _viewModel.SwitchToInbox();

    // ----------------------------- 直接扫描 -----------------------------

    private async void OnRefreshScanners(object? sender, RoutedEventArgs e) =>
        await _viewModel.RefreshScannersAsync();

    private async void OnStartScan(object? sender, RoutedEventArgs e) =>
        await _viewModel.ScanAsync();

    private async void OnAcquireWithDriverUi(object? sender, RoutedEventArgs e) =>
        await _viewModel.AcquireWithDriverUiAsync();

    private void OnCancelScan(object? sender, RoutedEventArgs e) => _viewModel.CancelScan();

    // ----------------------------- 收件文件夹 -----------------------------

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择扫描收件文件夹",
            AllowMultiple = false,
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(folder)) return;

        var wasWatching = _viewModel.IsWatching;
        StopWatcher();
        _viewModel.SetInboxFolder(folder);
        if (wasWatching) StartWatcher();
        _preferencesStore.TrySave(_viewModel.ToPreferences());
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folder = _viewModel.InboxFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            Directory.CreateDirectory(folder);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null)
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
        catch (Exception ex)
        {
            _viewModel.AddIncomingFailure(folder, $"打不开文件夹：{ex.Message}");
        }
    }

    private async void OnCopyFolder(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetValueAsync(DataFormat.Text, _viewModel.InboxFolder);
            _viewModel.SetStatus("收件文件夹路径已复制。");
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus($"复制路径失败：{ex.Message}");
        }
    }

    private void OnToggleWatch(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsWatching) StopWatcher();
        else StartWatcher();
    }

    private void StartWatcher()
    {
        if (_watcher is not null) return;

        var folder = _viewModel.InboxFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;

        ScanInboxWatcher? watcher = null;
        try
        {
            Directory.CreateDirectory(folder);
            watcher = new ScanInboxWatcher(folder);
            watcher.FileReady += OnFileReady;
            watcher.FileFailed += OnFileFailed;
            watcher.Start();
            _watcher = watcher;
            _viewModel.SetWatching(true);
            _preferencesStore.TrySave(_viewModel.ToPreferences());
        }
        catch (Exception ex)
        {
            watcher?.Dispose();
            _viewModel.AddIncomingFailure(folder, $"无法开始接收：{ex.Message}");
        }
    }

    private void StopWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.FileReady -= OnFileReady;
            watcher.FileFailed -= OnFileFailed;
            watcher.Dispose();
        }

        if (_viewModel.IsWatching)
            _viewModel.SetWatching(false);
    }

    private void OnFileReady(string path)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await _viewModel.ImportIncomingAsync(path);
            }
            catch (Exception ex)
            {
                _viewModel.AddIncomingFailure(path, $"导入失败：{ex.Message}");
            }
        });
    }

    private void OnFileFailed(string path, string reason) =>
        Dispatcher.UIThread.Post(() => _viewModel.AddIncomingFailure(path, reason));

    private async void OnImportExisting(object? sender, RoutedEventArgs e) =>
        await _viewModel.ImportExistingAsync();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private sealed class NullScanImportSink : IScanImportSink
    {
        public bool HasCurrentBatch => false;

        public Task<ScanImportOutcome> ImportAsync(
            IReadOnlyList<string> paths,
            bool createNewBatch,
            CancellationToken token) =>
            Task.FromResult(ScanImportOutcome.Failed("扫描窗口没有连接到作业整理。"));
    }
}
