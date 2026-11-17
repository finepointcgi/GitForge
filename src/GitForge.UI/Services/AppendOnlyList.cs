using System.Collections;

namespace GitForge.UI.Services;

/// <summary>
/// Single-writer / multi-reader list: the history loader appends on a background thread while the
/// virtualized view reads published items without locks or full copies.
/// </summary>
public sealed class AppendOnlyList<T> : IReadOnlyList<T>
{
    private T[] _items = new T[1024];
    private volatile int _count;

    public int Count => _count;

    public T this[int index] => (uint)index < (uint)_count ? Volatile.Read(ref _items)[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public void Add(T item)
    {
        var items = _items;
        if (_count == items.Length)
        {
            var grown = new T[items.Length * 2];
            Array.Copy(items, grown, _count);
            Volatile.Write(ref _items, grown);
            items = grown;
        }

        items[_count] = item;
        _count++; // publish after the write
    }

    public IEnumerable<T> Slice(int start, int count)
    {
        var items = Volatile.Read(ref _items);
        var end = Math.Min(_count, start + count);
        for (var i = start; i < end; i++) yield return items[i];
    }

    public IEnumerator<T> GetEnumerator() => Slice(0, _count).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
