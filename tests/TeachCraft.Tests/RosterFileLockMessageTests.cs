using System;
using System.IO;
using ClassBlock.Core.Import;
using Homework.Core.Roster;
using SeatGrid.Core.Roster;
using Xunit;

namespace TeachCraft.Tests;

public sealed class RosterFileLockMessageTests
{
    [Fact]
    public void SeatGridRoster_WhenFileIsLocked_ReportsChineseMessage()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithLockedFile(path =>
        {
            var ex = Assert.Throws<SeatGrid.Core.SeatGridException>(() => RosterImporter.Import(path));
            AssertLockedMessageIsChinese(ex.Message);
        });
    }

    [Fact]
    public void ClassBlockSchedule_WhenFileIsLocked_ReportsChineseMessage()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithLockedFile(path =>
        {
            var ex = Assert.Throws<ClassBlock.Core.ClassBlockException>(() => MasterScheduleImporter.Import(path));
            AssertLockedMessageIsChinese(ex.Message);
        });
    }

    [Fact]
    public void HomeworkRoster_WhenFileIsLocked_ReportsChineseMessage()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithLockedFile(path =>
        {
            var ex = Assert.Throws<Homework.Core.HomeworkException>(() => HomeworkRosterImporter.LoadWorkbook(path));
            AssertLockedMessageIsChinese(ex.Message);
        });
    }

    private static void AssertLockedMessageIsChinese(string message)
    {
        Assert.Contains("占用", message, StringComparison.Ordinal);
        Assert.DoesNotContain("being used", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("because", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The process cannot access", message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WithLockedFile(Action<string> act)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teachcraft-lock-{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        try
        {
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            act(path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
