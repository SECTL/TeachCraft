using System;
using System.Threading;
using System.Threading.Tasks;
using SeatGrid.App.Cloud;
using SeatGrid.Core.Cloud;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class SeatGridCloudPrefillTests
{
    private static SeatGridRosterFields Fields(bool h, bool v, bool s, bool n, bool g)
        => new(h, v, s, n, g);

    private static SeatGridRosterEntry Entry(string id, string name, SeatGridRosterFields fields) => new(
        id, name, 3, DateTimeOffset.UtcNow, 4, fields);

    [Fact]
    public void ResolveUploadPrefill_SelectedDifferentRoster_UsesSelectedRow()
    {
        var selected = Entry("roster-B", "合成名单B", Fields(false, false, true, false, false));
        var linked = Fields(true, false, false, false, false);
        var last = Fields(false, true, false, false, false);

        var (fields, name) = SeatGridCloudWindow.ResolveUploadPrefill(
            selected, "roster-A", linked, last, "合成名单A");

        Assert.Equal(selected.Fields, fields);
        Assert.Equal("合成名单B", name);
    }

    [Fact]
    public void ResolveUploadPrefill_SelectedLinkedRoster_UsesLinkedFields()
    {
        var selected = Entry("roster-A", "列表里的A", Fields(false, false, true, false, false));
        var linked = Fields(true, false, false, false, false);
        var last = Fields(false, true, false, false, false);

        var (fields, name) = SeatGridCloudWindow.ResolveUploadPrefill(
            selected, "roster-A", linked, last, "关联侧的A");

        Assert.Equal(linked, fields);
        Assert.Equal("关联侧的A", name);
    }

    [Fact]
    public void ResolveUploadPrefill_NoSelection_UsesRememberedFields()
    {
        var linked = Fields(true, false, false, false, false);
        var last = Fields(false, false, false, true, false);

        var (fields, name) = SeatGridCloudWindow.ResolveUploadPrefill(
            null, "roster-A", linked, last, "新建名单");

        Assert.Equal(last, fields);
        Assert.Equal("新建名单", name);
    }

    [Theory]
    [InlineData("roster-A", "roster-A", true)]
    [InlineData("roster-B", "roster-A", false)]
    [InlineData("roster-A", "", false)]
    [InlineData("roster-A", null, false)]
    public void ShouldClearRosterLink_OnlyForTheLinkedRoster(string deleted, string? linked, bool expected)
        => Assert.Equal(expected, SeatGridCloudWindow.ShouldClearRosterLink(deleted, linked));

    [Fact]
    public void RosterDeletedLinkClearedMessage_IsChinese()
    {
        var message = SeatGridCloudWindow.RosterDeletedLinkClearedMessage;
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains(message, c => c is >= '一' and <= '鿿');
        Assert.Contains("关联已解除", message);
    }

    [Fact]
    public void NullBridge_LastRosterFields_IsNoneAndRememberIsNoop()
    {
        var bridge = new NullSeatGridCloudBridge();
        Assert.Equal(SeatGridRosterFields.None, bridge.LastRosterFields);
        bridge.RememberRosterFields(Fields(true, true, true, true, true));
        Assert.Equal(SeatGridRosterFields.None, bridge.LastRosterFields);
    }

    [Fact]
    public void RealBridge_RemembersRosterFieldsAcrossRestart()
    {
        var settings = new MemorySettings();
        var bridge = new SeatGridCloudBridge(
            new FakeAccount(), new SectlSharedRosterSource(new MemoryDocumentStore()),
            new MemoryDocumentStore(), settings);
        Assert.Equal(SeatGridRosterFields.None, bridge.LastRosterFields);

        var fields = Fields(true, false, true, false, true);
        bridge.RememberRosterFields(fields);

        var restarted = new SeatGridCloudBridge(
            new FakeAccount(), new SectlSharedRosterSource(new MemoryDocumentStore()),
            new MemoryDocumentStore(), settings);
        Assert.Equal(fields, restarted.LastRosterFields);
        Assert.True(settings.Settings.LastRosterHeight);
        Assert.False(settings.Settings.LastRosterVision);
        Assert.True(settings.Settings.LastRosterScore);
        Assert.False(settings.Settings.LastRosterNotes);
        Assert.True(settings.Settings.LastRosterOriginalGroup);
    }

    [Fact]
    public void DeleteRoster_ClearsLinkAndShowsMessage()
    {
        var window = RepoRoot.Read("src/SeatGrid.App/Cloud/SeatGridCloudWindow.cs");
        var start = window.IndexOf("private async Task ExecuteDeleteRosterAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 ExecuteDeleteRosterAsync。");
        var end = window.IndexOf("private async Task UploadDocumentAsync", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 ExecuteDeleteRosterAsync 的结束位置。");
        var method = window[start..end];

        Assert.Contains("ShouldClearRosterLink(selected.RosterId, _linkedRosterId)", method);
        Assert.Contains("_onRosterDeleted?.Invoke(selected.RosterId)", method);
        Assert.Contains("ShowNotice(RosterDeletedLinkClearedMessage)", method);

        var host = RepoRoot.Read("src/SeatGrid.App/SeatGridWindow.axaml.cs");
        var handler = host.IndexOf("private void OnCloudRosterDeleted", StringComparison.Ordinal);
        Assert.True(handler >= 0, "找不到 OnCloudRosterDeleted。");
        var handlerEnd = host.IndexOf("private void ClearLastDocument", handler, StringComparison.Ordinal);
        Assert.True(handlerEnd > handler, "找不到 OnCloudRosterDeleted 的结束位置。");
        var body = host[handler..handlerEnd];
        Assert.Contains("ViewModel.ClearRosterLink()", body);
        Assert.Contains("SeatGridCloudWindow.RosterDeletedLinkClearedMessage", body);
    }

    [Fact]
    public void UploadRoster_PrefillFromSelection_RemembersFields_AndHasNoDeadBuild()
    {
        var source = RepoRoot.Read("src/SeatGrid.App/Cloud/SeatGridCloudWindow.cs");
        var start = source.IndexOf("private async Task UploadRosterAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 UploadRosterAsync。");
        var end = source.IndexOf("private async Task PullRosterAsync", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 UploadRosterAsync 的结束位置。");
        var method = source[start..end];

        Assert.Contains("ResolveUploadPrefill(", method);
        Assert.Contains("_cloud.LastRosterFields", method);
        Assert.Contains("ExecuteUploadRosterAsync(name, fields, selected)", method);
        Assert.Contains("_cloud.RememberRosterFields(fields)", method);
        Assert.DoesNotContain("_document ??= _buildDocument?.Invoke();", method);
    }

    private sealed class MemorySettings : ICloudSyncSettingsStore
    {
        public CloudSyncSettings Settings { get; private set; } = new(Enabled: true, ConsentVersion: 1);
        public CloudSyncSettings Load() => Settings;
        public void Save(CloudSyncSettings settings) => Settings = settings;
        public string Path => "memory";
    }

    private sealed class FakeAccount : ICloudAccountService
    {
        public CloudAccount? Current { get; } = new(
            "synthetic-user", "示例教师", Array.Empty<string>(),
            SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow);
        public bool IsSignedIn => Current is not null;
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("synthetic-token");

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
