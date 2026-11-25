namespace TeachCraft.Core.Cloud;

/// <summary>
/// 把名单投影成「只含已勾选字段」的副本。这是隐私不变量的第二道防线：
/// 即使输入对象里带着未勾选的值，投影后也一律置空，绝不进云端 JSON。
/// </summary>
public static class SharedRosterProjection
{
    public static IReadOnlyList<SharedRosterStudent> Apply(
        IReadOnlyList<SharedRosterStudent> students, RosterFieldSelection selection)
    {
        var result = new List<SharedRosterStudent>(students.Count);
        foreach (var student in students)
        {
            result.Add(new SharedRosterStudent
            {
                Index = student.Index,
                Name = student.Name,
                Gender = student.Gender,
                Number = student.Number,
                Height = selection.Height ? student.Height : null,
                Vision = selection.Vision ? student.Vision : null,
                Score = selection.Score ? student.Score : null,
                Notes = selection.Notes ? student.Notes : null,
                OriginalGroup = selection.OriginalGroup ? student.OriginalGroup : null,
            });
        }
        return result;
    }

    public static RosterFieldSelection SelectionFromFlags(
        bool height, bool vision, bool score, bool notes, bool originalGroup)
        => new(height, vision, score, notes, originalGroup);
}
