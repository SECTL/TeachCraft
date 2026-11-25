namespace Homework.App.Scanning;

public enum WiaPropertyKind
{
    Unspecified = 0,
    Range = 1,
    List = 2,
    Flag = 3,
}

public sealed record WiaPropertyInfo(
    WiaPropertyKind Kind,
    int Min,
    int Max,
    int Step,
    IReadOnlyList<int> Values,
    int? SingleValue = null)
{
    public static WiaPropertyInfo Range(int min, int max, int step) =>
        new(WiaPropertyKind.Range, min, max, step, [], null);

    public static WiaPropertyInfo List(params int[] values) =>
        new(WiaPropertyKind.List, 0, 0, 0, values, null);

    public static WiaPropertyInfo Flag(params int[] values) =>
        new(WiaPropertyKind.Flag, 0, 0, 0, values, null);

    public static WiaPropertyInfo Single(int value) =>
        new(WiaPropertyKind.Unspecified, 0, 0, 0, [], value);
}

public sealed record WiaPropertySnapshot(IReadOnlyDictionary<int, WiaPropertyInfo> Properties)
{
    public WiaPropertyInfo? Get(int id) =>
        Properties.TryGetValue(id, out var info) ? info : null;
}
