using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TeachCraft.App.Views;

public partial class ControlPanelView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? SaveConfigRequested;
    public event EventHandler? EnrollPairCodeRequested;
    public event EventHandler? UnenrollRequested;
    public event EventHandler? LoadAccountSitesRequested;
    public event EventHandler? EnrollWithAccountRequested;
    public event EventHandler? RefreshTreeRequested;
    public event EventHandler? AssignClassRequested;
    public event EventHandler? OccupyClassRequested;
    public event EventHandler? ReleaseClassRequested;
    public event EventHandler? PreviewSeatsRequested;

    public ControlPanelView()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnSaveConfig(object? sender, RoutedEventArgs e) => SaveConfigRequested?.Invoke(this, EventArgs.Empty);
    private void OnEnrollPairCode(object? sender, RoutedEventArgs e) => EnrollPairCodeRequested?.Invoke(this, EventArgs.Empty);
    private void OnUnenroll(object? sender, RoutedEventArgs e) => UnenrollRequested?.Invoke(this, EventArgs.Empty);
    private void OnLoadAccountSites(object? sender, RoutedEventArgs e) => LoadAccountSitesRequested?.Invoke(this, EventArgs.Empty);
    private void OnEnrollWithAccount(object? sender, RoutedEventArgs e) => EnrollWithAccountRequested?.Invoke(this, EventArgs.Empty);
    private void OnRefreshTree(object? sender, RoutedEventArgs e) => RefreshTreeRequested?.Invoke(this, EventArgs.Empty);
    private void OnAssignClass(object? sender, RoutedEventArgs e) => AssignClassRequested?.Invoke(this, EventArgs.Empty);
    private void OnOccupyClass(object? sender, RoutedEventArgs e) => OccupyClassRequested?.Invoke(this, EventArgs.Empty);
    private void OnReleaseClass(object? sender, RoutedEventArgs e) => ReleaseClassRequested?.Invoke(this, EventArgs.Empty);
    private void OnPreviewSeats(object? sender, RoutedEventArgs e) => PreviewSeatsRequested?.Invoke(this, EventArgs.Empty);
}
