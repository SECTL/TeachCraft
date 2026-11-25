using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 共享名单云端读写入口的行为测试。走内存文档存储，不联网、不落盘、全用合成姓名。
/// </summary>
public sealed class SharedRosterStoreTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-24T10:00:00Z");

    private static (SectlSharedRosterSource Source, MemoryDocumentStore Store) Create()
    {
        var store = new MemoryDocumentStore();
        return (new SectlSharedRosterSource(store, new ManualTimeProvider(Start)), store);
    }

    private static SharedRosterDocument MakeRoster(string name = "合成名单", int count = 2)
    {
        var students = Enumerable.Range(1, count).Select(i => new SharedRosterStudent
        {
            Index = i,
            Name = "合成学生" + i,
            Gender = i % 2 == 0 ? "女" : "男",
            Number = "S" + i.ToString("D3"),
        }).ToList();
        return new SharedRosterDocument { Name = name, Students = students };
    }

    [Fact]
    public async Task Create_AssignsVersionOne_AndGetReturnsDoc()
    {
        var (source, _) = Create();

        var info = await source.SaveAsync(
            MakeRoster(), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);

        Assert.Equal(1, info.Version);
        Assert.Equal("合成名单", info.Name);
        Assert.Equal(2, info.StudentCount);
        Assert.Equal(RosterFieldPresence.None, info.Fields);
        Assert.Equal(32, info.RosterId.Length);

        var loaded = await source.GetAsync(info.RosterId, CancellationToken.None);
        Assert.NotNull(loaded);
        // 文档内 Version 必须与存储 revision 一致：新建时为 1，拉取后才有可用的版本与冲突检测。
        Assert.Equal(1, loaded.Version);
        Assert.False(string.IsNullOrEmpty(loaded.RosterId));
        Assert.Equal("合成名单", loaded.Name);
        Assert.Equal(2, loaded.Students.Count);
        Assert.Equal(CloudDocumentNamespace.Roster.Kind, loaded.Kind);
        Assert.Equal(Start, loaded.UpdatedAtUtc);
    }

    [Fact]
    public async Task Update_WithMatchingVersion_IncrementsVersion_AndUpdatesFields()
    {
        var (source, _) = Create();
        var created = await source.SaveAsync(
            MakeRoster(), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);

        var changed = MakeRoster("合成名单（改）");
        changed.Students[0].Score = 88.5;
        var selection = new RosterFieldSelection(false, false, true, false, false);
        var updated = await source.SaveAsync(
            changed, selection, created.RosterId, 1, CancellationToken.None);

        Assert.Equal(2, updated.Version);
        Assert.Equal("合成名单（改）", updated.Name);
        Assert.True(updated.Fields.Score);
        Assert.False(updated.Fields.Height);

        var loaded = await source.GetAsync(created.RosterId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(88.5, loaded.Students[0].Score);
    }

    [Fact]
    public async Task StaleVersion_ThrowsConflict_AndWritesNothing()
    {
        var (source, _) = Create();
        var created = await source.SaveAsync(
            MakeRoster(), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);
        var before = await source.GetAsync(created.RosterId, CancellationToken.None);

        await Assert.ThrowsAsync<CloudConflictException>(() => source.SaveAsync(
            MakeRoster("不应写入"), RosterFieldSelection.Minimal,
            created.RosterId, 99, CancellationToken.None));

        var after = await source.GetAsync(created.RosterId, CancellationToken.None);
        Assert.Equal(before!.Name, after!.Name);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Students.Count, after.Students.Count);
    }

    [Fact]
    public async Task Save_ProjectsFields_SentinelsAbsentFromStoredJson()
    {
        var (source, store) = Create();
        var roster = MakeRoster();
        roster.Students[0].Height = 171.5;
        roster.Students[0].Vision = 5.2;
        roster.Students[0].Score = 88.5;
        roster.Students[0].Notes = "SENTINEL_NOTE";
        roster.Students[0].OriginalGroup = "SENTINEL_GROUP";
        var selection = new RosterFieldSelection(false, false, true, false, false);

        var info = await source.SaveAsync(roster, selection, null, 0, CancellationToken.None);

        Assert.True(info.Fields.Score);
        Assert.False(info.Fields.Height);
        Assert.False(info.Fields.Vision);
        Assert.False(info.Fields.Notes);
        Assert.False(info.Fields.OriginalGroup);

        var payload = await store.GetAsync(info.RosterId, CancellationToken.None);
        Assert.NotNull(payload);
        Assert.Contains("88.5", payload.Json);
        Assert.DoesNotContain("171.5", payload.Json);
        Assert.DoesNotContain("5.2", payload.Json);
        Assert.DoesNotContain("SENTINEL_NOTE", payload.Json);
        Assert.DoesNotContain("SENTINEL_GROUP", payload.Json);

        var loaded = await source.GetAsync(info.RosterId, CancellationToken.None);
        Assert.Null(loaded!.Students[0].Height);
        Assert.Null(loaded.Students[0].Vision);
        Assert.Equal(88.5, loaded.Students[0].Score);
        Assert.Null(loaded.Students[0].Notes);
        Assert.Null(loaded.Students[0].OriginalGroup);
    }

    [Fact]
    public async Task Save_StripsUnselectedValues_SoValidatorAccepts()
    {
        var (source, _) = Create();
        var roster = MakeRoster();
        foreach (var student in roster.Students)
        {
            student.Height = 170;
            student.Vision = 5.0;
            student.Score = 90;
            student.Notes = "SENTINEL_NOTE";
            student.OriginalGroup = "SENTINEL_GROUP";
        }

        // 调用方对象带着全部可选字段，但 SaveAsync 必须先投影再校验，
        // 因此不会抛「包含未勾选字段」的校验错误。
        var info = await source.SaveAsync(
            roster, RosterFieldSelection.Minimal, null, 0, CancellationToken.None);

        Assert.Equal(RosterFieldPresence.None, info.Fields);
        var loaded = await source.GetAsync(info.RosterId, CancellationToken.None);
        Assert.All(loaded!.Students, student =>
        {
            Assert.Null(student.Height);
            Assert.Null(student.Vision);
            Assert.Null(student.Score);
            Assert.Null(student.Notes);
            Assert.Null(student.OriginalGroup);
        });
    }

    [Fact]
    public async Task List_MapsVersionsCountsFields_AndToleratesFailedGet()
    {
        var (source, store) = Create();
        var first = await source.SaveAsync(
            MakeRoster("合成名单甲"), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);
        var second = await source.SaveAsync(
            MakeRoster("合成名单乙"), new RosterFieldSelection(true, false, false, false, false),
            null, 0, CancellationToken.None);
        await source.SaveAsync(
            MakeRoster("合成名单甲（改）"), RosterFieldSelection.Minimal,
            first.RosterId, 1, CancellationToken.None);
        store.FailGetFor(second.RosterId);

        var list = await source.ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
        var a = list.Single(x => x.RosterId == first.RosterId);
        Assert.Equal(2, a.Version);
        Assert.Equal("合成名单甲（改）", a.Name);
        Assert.Equal(2, a.StudentCount);
        Assert.Equal(RosterFieldPresence.None, a.Fields);

        var b = list.Single(x => x.RosterId == second.RosterId);
        Assert.Equal(1, b.Version);
        Assert.Equal("合成名单乙", b.Name);
        Assert.Equal(0, b.StudentCount);
        Assert.Equal(RosterFieldPresence.None, b.Fields);
    }

    [Fact]
    public async Task Delete_RemovesDoc_SubsequentGetReturnsNull()
    {
        var (source, _) = Create();
        var created = await source.SaveAsync(
            MakeRoster(), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);

        await source.DeleteAsync(created.RosterId, CancellationToken.None);

        Assert.Null(await source.GetAsync(created.RosterId, CancellationToken.None));
        Assert.Empty(await source.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAll_ViaStore_LeavesListEmpty()
    {
        var (source, store) = Create();
        await source.SaveAsync(
            MakeRoster("合成名单甲"), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);
        await source.SaveAsync(
            MakeRoster("合成名单乙"), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);

        await store.DeleteAllAsync(CancellationToken.None);

        Assert.Empty(await source.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Get_WithV2Document_ThrowsChineseCodecException()
    {
        var (source, store) = Create();
        var created = await source.SaveAsync(
            MakeRoster(), RosterFieldSelection.Minimal, null, 0, CancellationToken.None);
        var payload = await store.GetAsync(created.RosterId, CancellationToken.None);
        store.Seed(created.RosterId, payload!.Json.Replace("\"v\":1", "\"v\":2"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.GetAsync(created.RosterId, CancellationToken.None));
        Assert.Contains("版本 2 不受支持", ex.Message);
    }
}
