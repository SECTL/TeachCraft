using System.Net;
using System.Text.Json;
using SeatGrid.Core.Cloud;
using TeachCraft.App.Cloud;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 云同步开关的跨窗口传播与传输闸门。HTTP 全部走 FakeSectlApiHandler，
/// 断言「零请求」而不是「请求失败」；姓名与班级均为合成数据。
/// </summary>
public sealed class CloudTogglePropagationTests
{
    private sealed class FakeAccount : ICloudAccountService
    {
        public CloudAccount? Current { get; private set; } =
            new("synthetic-user", "示例教师", Array.Empty<string>(),
                SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow);

        public bool IsSignedIn => Current is not null;
        public string? LastSignOutNotice => null;
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("synthetic-token");

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSettings : ICloudSyncSettingsStore
    {
        public CloudSyncSettings Settings { get; private set; } = new(Enabled: true, ConsentVersion: 1);
        public CloudSyncSettings Load() => Settings;
        public void Save(CloudSyncSettings settings) => Settings = settings;
        public string Path => "memory";
    }

    private sealed class Fixture
    {
        public required FakeSectlApiHandler Handler { get; init; }
        public required FakeSettings Settings { get; init; }
        public required CloudRuntime Runtime { get; init; }
        public required AccountViewModel ViewModel { get; init; }
        public required SeatGridCloudBridge SeatBridge { get; init; }
        public required HomeworkRosterBridge HomeworkBridge { get; init; }
    }

    private static Fixture Create()
    {
        var handler = new FakeSectlApiHandler();
        handler.Respond("/api/cloud/kv", (request, _) => Task.FromResult(FakeSectlApiHandler.Response(
            HttpStatusCode.OK,
            request.Method == HttpMethod.Get ? """{"keys":[]}""" : """{}""")));
        handler.Respond("/api/cloud/kv/synthetic-key", (_, _) => Task.FromResult(
            FakeSectlApiHandler.Response(HttpStatusCode.OK, """{}""")));

        var account = new FakeAccount();
        var settings = new FakeSettings();
        var options = SectlTestData.Options();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-24T10:00:00Z"));
        var seatDocuments = new SectlCloudDocumentStore(
            handler, options, account, CloudDocumentNamespace.SeatGrid, time);
        var rosterStore = new SectlCloudDocumentStore(
            handler, options, account, CloudDocumentNamespace.Roster, time);
        var rosters = new SectlSharedRosterSource(rosterStore, time);

        var runtime = new CloudRuntime(true, account, seatDocuments, rosters, settings, settings.Load());
        return new Fixture
        {
            Handler = handler,
            Settings = settings,
            Runtime = runtime,
            ViewModel = new AccountViewModel(runtime),
            SeatBridge = new SeatGridCloudBridge(account, rosters, seatDocuments, settings),
            HomeworkBridge = new HomeworkRosterBridge(account, rosters, settings),
        };
    }

    [Fact]
    public void SetCloudEnabledFalse_RaisesNotifier_AndDisablesBothBridgesImmediately()
    {
        var f = Create();
        Assert.True(f.SeatBridge.IsAvailable);
        Assert.True(f.HomeworkBridge.IsAvailable);

        var notifierCalls = 0;
        var seatCalls = 0;
        var homeworkCalls = 0;
        f.Runtime.Notifier.StateChanged += (_, _) => notifierCalls++;
        f.SeatBridge.StateChanged += (_, _) => seatCalls++;
        f.HomeworkBridge.StateChanged += (_, _) => homeworkCalls++;

        f.ViewModel.SetCloudEnabled(false);

        Assert.True(notifierCalls >= 1);
        Assert.True(seatCalls >= 1);
        Assert.True(homeworkCalls >= 1);
        Assert.False(f.SeatBridge.IsAvailable);
        Assert.False(f.HomeworkBridge.IsAvailable);
        Assert.False(f.Settings.Load().Enabled);
    }

    [Fact]
    public async Task Uploads_WhenSyncOff_ThrowCloudSyncOff_AndMakeZeroHttpCalls()
    {
        var f = Create();
        f.ViewModel.SetCloudEnabled(false);

        var rosterError = await Assert.ThrowsAsync<CloudAuthException>(() =>
            f.SeatBridge.UploadRosterAsync(
                "合成名单", Array.Empty<SeatGridRosterStudent>(), SeatGridRosterFields.None,
                null, 0, CancellationToken.None));
        Assert.Equal(CloudErrorText.CloudSyncOff, rosterError.Message);

        var documentError = await Assert.ThrowsAsync<CloudAuthException>(() =>
            f.SeatBridge.UploadDocumentAsync(
                new SeatGridCloudDocument { Name = "合成座位表" }, null, 0, CancellationToken.None));
        Assert.Equal(CloudErrorText.CloudSyncOff, documentError.Message);

        Assert.Equal(0, f.Handler.RequestCount);
    }

    [Fact]
    public async Task RefreshAndDelete_WhenSyncOff_MakeZeroHttpCalls()
    {
        var f = Create();
        f.ViewModel.SetCloudEnabled(false);

        await f.ViewModel.RefreshRostersAsync();
        await f.ViewModel.DeleteRosterAsync("synthetic-key");

        Assert.Equal(0, f.Handler.RequestCount);
        Assert.Equal(CloudErrorText.CloudSyncOff, f.ViewModel.StatusText);
    }

    [Fact]
    public void TogglingBackOn_ReenablesBothBridges()
    {
        var f = Create();
        f.ViewModel.SetCloudEnabled(false);
        Assert.False(f.SeatBridge.IsAvailable);

        f.ViewModel.SetCloudEnabled(true);

        Assert.True(f.SeatBridge.IsAvailable);
        Assert.True(f.HomeworkBridge.IsAvailable);
        Assert.True(f.Settings.Load().Enabled);
    }

    [Fact]
    public void AccountPanelView_RefreshAndDelete_AreGatedOnCloudEnabled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "TeachCraft.App", "Views", "AccountPanelView.axaml");
            if (File.Exists(candidate)) { path = candidate; break; }
            directory = directory.Parent;
        }
        Assert.NotNull(path);

        var xaml = File.ReadAllText(path);
        Assert.Contains("IsEnabled=\"{Binding CanAccessCloud}\"", xaml);
        Assert.Contains("$parent[UserControl].((vm:AccountViewModel)DataContext).CanAccessCloud", xaml);
    }

    [Fact]
    public void DisposeBridges_StopsReceivingNotifierEvents()
    {
        var f = Create();
        var seatCalls = 0;
        var homeworkCalls = 0;
        f.SeatBridge.StateChanged += (_, _) => seatCalls++;
        f.HomeworkBridge.StateChanged += (_, _) => homeworkCalls++;

        f.SeatBridge.Dispose();
        f.HomeworkBridge.Dispose();
        f.ViewModel.SetCloudEnabled(false);

        Assert.Equal(0, seatCalls);
        Assert.Equal(0, homeworkCalls);
    }
}
