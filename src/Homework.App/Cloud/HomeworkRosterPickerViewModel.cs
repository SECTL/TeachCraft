using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Homework.Core.Roster;
using TeachCraft.Shared;

namespace Homework.App.Cloud;

/// <summary>选择器里的一行。同名班级的第二份不可选，避免把两个版本并进同一批名单。</summary>
public sealed class HomeworkRosterPickerRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public HomeworkRosterPickerRow(HomeworkRosterEntry entry, bool canSelect, string disabledReason)
    {
        Entry = entry;
        CanSelect = canSelect;
        DisabledReason = disabledReason;
    }

    public HomeworkRosterEntry Entry { get; }
    public bool CanSelect { get; }
    public string DisabledReason { get; }
    public string Name => Entry.Name;
    public string VersionText => $"v{Entry.Version}";
    public string StudentsText => $"{Entry.StudentCount} 名学生";
    public string UpdatedText => Entry.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string FieldsSummary => Entry.FieldsSummary;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || _isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 共享名单拉取选择器的状态机。只经只读桥访问云端，不持有任何服务实现；
/// 同名班级只允许选一份，选中数量与学生数全部由行状态实时算出。
/// </summary>
public sealed class HomeworkRosterPickerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IHomeworkRosterBridge _bridge;
    private bool _isAvailable;
    private bool _isBusy;
    private bool _isEmpty;
    private bool _hasError;
    private bool _canPull;
    private int _selectedCount;
    private int _totalStudents;
    private string _statusText = string.Empty;
    private string _confirmText = string.Empty;
    private bool _disposed;

    public HomeworkRosterPickerViewModel(IHomeworkRosterBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _isAvailable = bridge.IsAvailable;
        _bridge.StateChanged += OnBridgeStateChanged;
        Recompute();
    }

    public ObservableCollection<HomeworkRosterPickerRow> Rows { get; } = new();

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => Set(ref _isAvailable, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => Set(ref _hasError, value);
    }

    public bool CanPull
    {
        get => _canPull;
        private set => Set(ref _canPull, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set => Set(ref _selectedCount, value);
    }

    public int TotalStudents
    {
        get => _totalStudents;
        private set => Set(ref _totalStudents, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ConfirmText
    {
        get => _confirmText;
        private set => Set(ref _confirmText, value);
    }

    public bool ShowList => IsAvailable && !IsEmpty;

    public IReadOnlyList<string> SelectedIds =>
        Rows.Where(row => row.IsSelected).Select(row => row.Entry.RosterId).ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsAvailable = _bridge.IsAvailable;
        if (!IsAvailable)
        {
            ApplyUnavailableState();
            return;
        }

        IsBusy = true;
        HasError = false;
        StatusText = "正在读取共享名单…";
        Recompute();
        try
        {
            var entries = await _bridge.ListRostersAsync(ct);
            ClearRows();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var duplicate = !seen.Add(SharedRosterImport.NormalizeClassName(entry.Name));
                var row = new HomeworkRosterPickerRow(
                    entry,
                    !duplicate,
                    duplicate ? "同名班级只能选一份" : string.Empty);
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }

            IsEmpty = Rows.Count == 0;
            StatusText = IsEmpty
                ? "共享名单库还是空的。请先在「归位」把班级名单保存到共享库。"
                : $"共享名单：{Rows.Count} 份";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusText = "已取消读取共享名单。";
        }
        catch (Exception ex)
        {
            HasError = true;
            ClearRows();
            StatusText = ErrorText.Describe(ex, "读取共享名单");
        }
        finally
        {
            IsBusy = false;
            Recompute();
        }
    }

    public void SelectAll()
    {
        foreach (var row in Rows)
            row.IsSelected = true;
        Recompute();
    }

    public void ClearSelection()
    {
        foreach (var row in Rows)
            row.IsSelected = false;
        Recompute();
    }

    public async Task<RosterImportResult?> PullAsync(CancellationToken ct)
    {
        if (!CanPull) return null;

        IsBusy = true;
        HasError = false;
        Recompute();
        try
        {
            return await _bridge.DownloadAsync(SelectedIds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusText = "已取消拉取共享名单。";
            return null;
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = ErrorText.Describe(ex, "拉取共享名单");
            return null;
        }
        finally
        {
            IsBusy = false;
            Recompute();
        }
    }

    public async Task EnsureSignedInAsync(Window owner)
    {
        if (IsBusy) return;
        var signedIn = await _bridge.EnsureSignedInAsync(owner);
        if (signedIn && _bridge.IsAvailable)
        {
            await RefreshAsync(CancellationToken.None);
            return;
        }

        ApplyUnavailableState();
    }

    private void ApplyUnavailableState()
    {
        IsAvailable = _bridge.IsAvailable;
        ClearRows();
        IsEmpty = false;
        HasError = false;
        StatusText = _bridge.IsSignedIn && !IsAvailable
            ? "已登录，请在「账号与云同步」开启云同步开关后拉取共享名单"
            : "登录后可以拉取共享名单";
        Recompute();
    }

    private void OnBridgeStateChanged(object? sender, EventArgs e)
    {
        void Handle()
        {
            if (_disposed || IsBusy) return;
            if (_bridge.IsAvailable && !IsAvailable)
            {
                _ = RefreshAsync(CancellationToken.None);
                return;
            }

            if (!_bridge.IsAvailable)
                ApplyUnavailableState();
        }

        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Handle);
            return;
        }
        Handle();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bridge.StateChanged -= OnBridgeStateChanged;
        ClearRows();
    }

    public void OpenAccountSettings(Window owner) => _bridge.OpenAccountSettings(owner);

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeworkRosterPickerRow.IsSelected)) Recompute();
    }

    private void ClearRows()
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowPropertyChanged;
        Rows.Clear();
    }

    private void Recompute()
    {
        SelectedCount = Rows.Count(row => row.IsSelected);
        TotalStudents = Rows.Where(row => row.IsSelected).Sum(row => row.Entry.StudentCount);
        CanPull = IsAvailable && !IsBusy && !HasError && SelectedCount > 0;
        ConfirmText = $"将导入 {SelectedCount} 个班、{TotalStudents} 名学生。当前名单与已做的匹配会被替换。";
        OnPropertyChanged(nameof(ShowList));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
