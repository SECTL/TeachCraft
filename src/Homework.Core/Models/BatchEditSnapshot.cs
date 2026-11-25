using Homework.Core.Recognition;

namespace Homework.Core.Models;

/// <summary>
/// 一次编辑前后的整份快照：页顺序 + 每页匹配的学生 + 每页的识别结果。
/// <para>
/// 这是 record，但默认的引用相等对 IReadOnlyList / IReadOnlyDictionary 不成立，
/// 因此显式实现「序列相等 + 字典相等」，撤销栈的去重与内容比较都依赖它。
/// 识别结果纳入快照后，只改识别元数据也能撤销 / 重做，且相同识别内容不会重复入栈。
/// </para>
/// </summary>
public sealed record BatchEditSnapshot(
    IReadOnlyList<Guid> PageOrder,
    IReadOnlyDictionary<Guid, Guid?> Matches,
    IReadOnlyDictionary<Guid, PageRecognition?> Recognitions)
{
    /// <summary>兼容只关心页序与匹配的调用方：识别结果为空。</summary>
    private static readonly IReadOnlyDictionary<Guid, PageRecognition?> EmptyRecognitions =
        new Dictionary<Guid, PageRecognition?>();

    public BatchEditSnapshot(IReadOnlyList<Guid> pageOrder, IReadOnlyDictionary<Guid, Guid?> matches)
        : this(pageOrder, matches, EmptyRecognitions)
    {
    }

    public bool Equals(BatchEditSnapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (!PageOrder.SequenceEqual(other.PageOrder)) return false;
        if (!DictionaryEquals(Matches, other.Matches)) return false;
        if (!DictionaryEquals(Recognitions, other.Recognitions)) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pageId in PageOrder) hash.Add(pageId);
        hash.Add(DictionaryHash(Matches));
        hash.Add(DictionaryHash(Recognitions));
        return hash.ToHashCode();
    }

    /// <summary>字典相等与插入顺序无关：逐键比对，值用默认相等比较器（PageRecognition 是 record，按值相等）。</summary>
    private static bool DictionaryEquals<TValue>(
        IReadOnlyDictionary<Guid, TValue> left, IReadOnlyDictionary<Guid, TValue> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var otherValue)) return false;
            if (!EqualityComparer<TValue>.Default.Equals(value, otherValue)) return false;
        }
        return true;
    }

    /// <summary>与 DictionaryEquals 对应：逐项异或，保证哈希与插入顺序无关。</summary>
    private static int DictionaryHash<TValue>(IReadOnlyDictionary<Guid, TValue> dict)
    {
        var result = 0;
        foreach (var (key, value) in dict)
            result ^= HashCode.Combine(key, value);
        return result;
    }
}
