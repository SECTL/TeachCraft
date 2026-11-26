using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TeachCraft.App.Cloud;
using TeachCraft.App.ControlCenter;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Control;

namespace TeachCraft.App.ViewModels;

public sealed record ControlClassOption(string Id, string GradeName, string ClassName)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(GradeName) ? ClassName : $"{GradeName} · {ClassName}";
}

/// <summary>
/// 集控中心侧栏抽屉状态机。未启用或走查模式下绝不建立连接、不写本机目录。
/// </summary>
public sealed partial class ControlViewModel : ObservableObject, IDisposable
{
    public const string NotConfiguredStatus = "集控中心 · 走查或受限模式下已禁用。";
    public const string DefaultOffStatus = "集控默认关闭 · 开启后才连接 SECTL 集控中心";

    private readonly ControlRuntime _runtime;
    private readonly CloudRuntime _cloudRuntime;
    private ControlSettings _snapshot;
    private bool _applyingSnapshot;
    private bool _disposed;

    public ControlViewModel(ControlRuntime runtime, CloudRuntime? cloudRuntime = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _cloudRuntime = cloudRuntime ?? CloudRuntime.Disabled;
        IsConfigured = runtime.IsConfigured && runtime.Service is not null;
        _snapshot = runtime.Settings.Load();

        if (_runtime.Service is { } service)
        {
            service.StateChanged += OnServiceStateChanged;
            service.EnrollCodeConsumed += OnEnrollCodeConsumed;
            service.CommandReceived += OnCommandReceived;
        }

        ApplySettingsSnapshot(_snapshot);
        Refresh();
    }

    public ControlRuntime Runtime => _runtime;
    public bool IsConfigured { get; }

    [ObservableProperty] private bool _controlEnabled;
    [ObservableProperty] private string _hostText = SectlPlatformDefaults.WebBaseUrl;
    [ObservableProperty] private string _deviceNameText = $"{Environment.MachineName}-TeachCraft";
    [ObservableProperty] private bool _localRemoteAllowed = true;
    [ObservableProperty] private string _siteIdInput = string.Empty;
    [ObservableProperty] private string _pairCodeInput = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _stateWire = "stopped";
    [ObservableProperty] private string _statusLabel = DefaultOffStatus;
    [ObservableProperty] private string _siteLabel = "未接入地点";
    [ObservableProperty] private string _deviceIdText = "—";
    [ObservableProperty] private string _selectedClassText = "未占用班级";
    [ObservableProperty] private string _seatGeometrySummary = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private string _settingsSaveWarning = string.Empty;
    [ObservableProperty] private bool _hasCredential;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSuperseded;
    [ObservableProperty] private bool _isRevoked;

    [ObservableProperty] private ControlSiteSummary? _selectedAccountSite;
    [ObservableProperty] private ControlClassOption? _selectedTreeClass;

    public ObservableCollection<ControlSiteSummary> AccountSites { get; } = new();
    public ObservableCollection<ControlClassOption> TreeClasses { get; } = new();
    public ObservableCollection<string> RecentLogs { get; } = new();

    public bool CanUseAccountEnroll => IsConfigured && _cloudRuntime.IsConfigured && (_cloudRuntime.Account?.IsSignedIn ?? false) && !IsBusy;
    public bool HasAccountSites => AccountSites.Count > 0;
    public bool HasTreeClasses => TreeClasses.Count > 0;
    public bool HasSeatGeometrySummary => !string.IsNullOrWhiteSpace(SeatGeometrySummary);
    public bool HasFeedbackMessage => !string.IsNullOrWhiteSpace(FeedbackMessage);
    public bool HasSettingsSaveWarning => !string.IsNullOrWhiteSpace(SettingsSaveWarning);
    public bool CanOccupyClass => IsConfigured && IsConnected && SelectedTreeClass is not null && !IsBusy;
    public bool CanReleaseClass => IsConfigured && IsConnected && StateWire == "selected" && !IsBusy;

    partial void OnSeatGeometrySummaryChanged(string value) => OnPropertyChanged(nameof(HasSeatGeometrySummary));
    partial void OnFeedbackMessageChanged(string value) => OnPropertyChanged(nameof(HasFeedbackMessage));
    partial void OnSettingsSaveWarningChanged(string value) => OnPropertyChanged(nameof(HasSettingsSaveWarning));
    partial void OnSelectedTreeClassChanged(ControlClassOption? value) => OnPropertyChanged(nameof(CanOccupyClass));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseAccountEnroll));
        OnPropertyChanged(nameof(CanOccupyClass));
        OnPropertyChanged(nameof(CanReleaseClass));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOccupyClass));
        OnPropertyChanged(nameof(CanReleaseClass));
    }

    partial void OnStateWireChanged(string value)
    {
        OnPropertyChanged(nameof(CanReleaseClass));
    }

    partial void OnControlEnabledChanged(bool value)
    {
        if (_applyingSnapshot || !IsConfigured || _runtime.Service is null) return;
        _snapshot = _snapshot with
        {
            Enabled = value,
            Host = ControlSettings.NormalizeHost(HostText),
            DeviceName = string.IsNullOrWhiteSpace(DeviceNameText) ? null : DeviceNameText.Trim(),
            LocalRemoteAllowed = LocalRemoteAllowed,
        };
        PersistSettings();
        _runtime.Service.UpdateRuntimeOptions(_snapshot.Host, _snapshot.DeviceName, _snapshot.LocalRemoteAllowed);
        if (value)
            _ = _runtime.Service.StartAsync();
        else
            _ = _runtime.Service.StopAsync();
    }

    partial void OnLocalRemoteAllowedChanged(bool value)
    {
        if (_applyingSnapshot || !IsConfigured || _runtime.Service is null) return;
        _snapshot = _snapshot with { LocalRemoteAllowed = value };
        PersistSettings();
        _runtime.Service.SetLocalRemoteAllowed(value);
        AppendLog(value ? "已允许响应集控指令" : "已拒绝远程指令（仅保留连接状态）");
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(CanUseAccountEnroll));
        if (!IsConfigured || _runtime.Service is null)
        {
            StatusLabel = NotConfiguredStatus;
            SiteLabel = "未启用";
            DeviceIdText = "—";
            return;
        }

        _snapshot = _runtime.Settings.Load();
        ApplySettingsSnapshot(_snapshot);
        ApplyServiceSnapshot(_runtime.Service.Snapshot);

        if (!string.IsNullOrEmpty(_runtime.Settings.LastError))
            SettingsSaveWarning = _runtime.Settings.LastError!;
    }

    public void SaveHostAndDeviceName()
    {
        if (!IsConfigured || _runtime.Service is null) return;
        var host = ControlSettings.NormalizeHost(HostText);
        var deviceName = string.IsNullOrWhiteSpace(DeviceNameText)
            ? $"{Environment.MachineName}-TeachCraft"
            : DeviceNameText.Trim();

        _applyingSnapshot = true;
        HostText = host;
        DeviceNameText = deviceName;
        _applyingSnapshot = false;

        _snapshot = _snapshot with { Host = host, DeviceName = deviceName };
        PersistSettings();
        _runtime.Service.UpdateRuntimeOptions(host, deviceName, LocalRemoteAllowed);
        FeedbackMessage = "已保存服务器地址与设备名称。";
    }

    public async Task EnrollWithPairCodeAsync()
    {
        if (!IsConfigured || _runtime.Service is null || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            SaveHostAndDeviceName();
            EnsureEnabledInSettings();
            var (ok, msg, cred) = await _runtime.Service.EnrollAsync(SiteIdInput, PairCodeInput, DeviceNameText);
            FeedbackMessage = msg;
            if (ok && cred is not null)
            {
                _snapshot = _snapshot with { SiteId = cred.SitePublicId };
                PersistSettings();
                AppendLog(msg);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAccountSitesAsync()
    {
        if (!IsConfigured || _runtime.Service is null || _cloudRuntime.Account is null || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            SaveHostAndDeviceName();
            var token = await _cloudRuntime.Account.GetValidAccessTokenAsync(CancellationToken.None);
            var sites = await _runtime.Service.FetchSitesWithAccountAsync(token);
            AccountSites.Clear();
            foreach (var s in sites) AccountSites.Add(s);
            SelectedAccountSite = AccountSites.FirstOrDefault(s =>
                string.Equals(s.PublicId, _snapshot.SiteId, StringComparison.OrdinalIgnoreCase))
                ?? AccountSites.FirstOrDefault();
            OnPropertyChanged(nameof(HasAccountSites));
            FeedbackMessage = sites.Count == 0
                ? "当前 SECTL 账号下还没有集控地点，请先到 SECTL 网页创建地点。"
                : $"已读取 {sites.Count} 个可选地点。";
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task EnrollWithAccountAsync()
    {
        if (!IsConfigured || _runtime.Service is null || _cloudRuntime.Account is null || IsBusy) return;
        if (SelectedAccountSite is null)
        {
            FeedbackMessage = "请先点击「读取我的地点」并选择一个地点。";
            return;
        }

        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            SaveHostAndDeviceName();
            EnsureEnabledInSettings();
            var token = await _cloudRuntime.Account.GetValidAccessTokenAsync(CancellationToken.None);
            var siteKey = string.IsNullOrWhiteSpace(SelectedAccountSite.PublicId)
                ? SelectedAccountSite.Id
                : SelectedAccountSite.PublicId;
            var (ok, msg, cred) = await _runtime.Service.EnrollWithAccountAsync(
                token, siteKey, DeviceNameText, SelectedTreeClass?.Id);
            FeedbackMessage = msg;
            if (ok && cred is not null)
            {
                _snapshot = _snapshot with { SiteId = cred.SitePublicId };
                PersistSettings();
                AppendLog(msg);
            }
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshDeviceTreeAsync()
    {
        if (!IsConfigured || _runtime.Service is null || !HasCredential || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            var tree = await _runtime.Service.FetchDeviceTreeAsync();
            TreeClasses.Clear();
            foreach (var grade in tree.Grades)
            {
                foreach (var cls in grade.Classes)
                    TreeClasses.Add(new ControlClassOption(cls.Id, grade.Name, cls.Name));
            }

            SelectedTreeClass = TreeClasses.FirstOrDefault(c => c.Id == tree.AssignedClassId)
                ?? TreeClasses.FirstOrDefault(c => c.Id == _snapshot.SelectedClassId)
                ?? TreeClasses.FirstOrDefault();
            OnPropertyChanged(nameof(HasTreeClasses));
            FeedbackMessage = TreeClasses.Count == 0 ? "该地点暂未配置年级/班级。" : $"已加载 {TreeClasses.Count} 个班级。";
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AssignSelectedClassAsync()
    {
        if (!IsConfigured || _runtime.Service is null || SelectedTreeClass is null || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            var (classId, className) = await _runtime.Service.AssignDeviceClassAsync(SelectedTreeClass.Id);
            _snapshot = _snapshot with { SelectedClassId = classId, SelectedClassName = className };
            PersistSettings();
            FeedbackMessage = string.IsNullOrWhiteSpace(className) ? "已更新设备所属班级。" : $"已将本机归属到 {className}。";
            AppendLog(FeedbackMessage);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OccupySelectedClassAsync()
    {
        if (!IsConfigured || _runtime.Service is null || SelectedTreeClass is null || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            var res = await _runtime.Service.SelectClassAsync(SelectedTreeClass.Id);
            if (res["ok"]?.GetValue<bool>() == true)
            {
                FeedbackMessage = $"已占用「{SelectedTreeClass.DisplayLabel}」，等待平台调度。";
                AppendLog(FeedbackMessage);
            }
            else
            {
                var errCode = res["code"]?.ToString() ?? string.Empty;
                var errMsg = res["message"]?.ToString() ?? string.Empty;
                FeedbackMessage = string.IsNullOrWhiteSpace(errMsg) ? $"占班失败（{errCode}）" : errMsg;
            }
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReleaseClassAsync()
    {
        if (!IsConfigured || _runtime.Service is null || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            await _runtime.Service.ReleaseClassAsync();
            FeedbackMessage = "已释放当前占用的班级。";
            AppendLog(FeedbackMessage);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PreviewDeviceSeatsSummaryAsync()
    {
        if (!IsConfigured || _runtime.Service is null || !HasCredential || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            var seats = await _runtime.Service.FetchDeviceSeatsAsync(SelectedTreeClass?.Id);
            SeatGeometrySummary = seats.ClassRequired
                ? "当前尚未绑定班级，无法读取座签几何。"
                : $"{seats.ClassName} · {seats.Rows} 排 × {seats.Columns} 列 · 共 {seats.LabelCount} 格座签（仅几何/标签，不含学生姓名）";
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UnenrollAsync()
    {
        if (!IsConfigured || _runtime.Service is null || IsBusy) return;
        IsBusy = true;
        FeedbackMessage = string.Empty;
        try
        {
            var remoteRemoved = await _runtime.Service.UnenrollAsync();
            TreeClasses.Clear();
            SelectedTreeClass = null;
            SeatGeometrySummary = string.Empty;
            OnPropertyChanged(nameof(HasTreeClasses));

            _applyingSnapshot = true;
            ControlEnabled = false;
            _applyingSnapshot = false;

            _snapshot = _snapshot with { Enabled = false, SelectedClassId = null, SelectedClassName = null };
            PersistSettings();
            await _runtime.Service.StopAsync();

            FeedbackMessage = remoteRemoved
                ? "已从 SECTL 集控中心移除并清除本机凭证。"
                : "已清除本机集控凭证（远端记录如仍存在，可在网页端删除）。";
            AppendLog(FeedbackMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AppendLog(string entry)
    {
        void Add()
        {
            RecentLogs.Insert(0, entry);
            while (RecentLogs.Count > 8) RecentLogs.RemoveAt(RecentLogs.Count - 1);
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    private void EnsureEnabledInSettings()
    {
        if (_snapshot.Enabled) return;
        _applyingSnapshot = true;
        ControlEnabled = true;
        _applyingSnapshot = false;
        _snapshot = _snapshot with { Enabled = true };
        PersistSettings();
    }

    private void PersistSettings()
    {
        var ok = _runtime.Settings.TrySave(_snapshot);
        SettingsSaveWarning = ok ? string.Empty : "集控配置未能保存到本机，本次修改仅本次运行有效。";
    }

    private void ApplySettingsSnapshot(ControlSettings settings)
    {
        _applyingSnapshot = true;
        try
        {
            ControlEnabled = settings.Enabled;
            HostText = settings.Host;
            DeviceNameText = string.IsNullOrWhiteSpace(settings.DeviceName)
                ? $"{Environment.MachineName}-TeachCraft"
                : settings.DeviceName;
            LocalRemoteAllowed = settings.LocalRemoteAllowed;
            if (!string.IsNullOrWhiteSpace(settings.SiteId) && string.IsNullOrWhiteSpace(SiteIdInput))
                SiteIdInput = settings.SiteId;
        }
        finally
        {
            _applyingSnapshot = false;
        }
    }

    private void ApplyServiceSnapshot(ControlCenterSnapshot s)
    {
        StateWire = s.State;
        StatusLabel = s.Label;
        SiteLabel = string.IsNullOrWhiteSpace(s.SiteLabel) ? "未接入地点" : s.SiteLabel;
        DeviceIdText = string.IsNullOrWhiteSpace(s.DeviceId) ? "—" : s.DeviceId;
        HasCredential = !string.IsNullOrWhiteSpace(s.DeviceId);
        IsConnected = s.Connected;
        IsSuperseded = s.State == "superseded";
        IsRevoked = s.State == "revoked";
        SelectedClassText = string.IsNullOrWhiteSpace(s.SelectedClass)
            ? (_snapshot.SelectedClassName ?? "未占用班级")
            : s.SelectedClass;
    }

    private void OnServiceStateChanged(object? sender, ControlCenterSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) ApplyServiceSnapshot(snapshot);
        });
    }

    private void OnEnrollCodeConsumed(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) PairCodeInput = string.Empty;
        });
    }

    private void OnCommandReceived(object? sender, CommandInvocation inv)
    {
        AppendLog($"收到指令：{inv.Cmd}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime.Service is { } service)
        {
            service.StateChanged -= OnServiceStateChanged;
            service.EnrollCodeConsumed -= OnEnrollCodeConsumed;
            service.CommandReceived -= OnCommandReceived;
        }
    }
}
