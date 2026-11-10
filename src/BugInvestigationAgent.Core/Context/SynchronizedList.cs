using System.Collections;

namespace BugInvestigationAgent.Core.Context;

/// <summary>
/// Thread-safe list for state written by the running pipeline and read concurrently by observers
/// (worker progress sync, Blazor UI). Enumeration works on a snapshot.
/// </summary>
/// <remarks>
/// LINQ and <see cref="List{T}"/> treat any <see cref="ICollection{T}"/> as "read Count, allocate, then CopyTo".
/// Those are separate calls, so the list can grow in between; <see cref="CopyTo"/> therefore never writes past
/// the destination (it copies what fits). Readers that need a consistent view should use
/// <see cref="Snapshot"/> or <see cref="SynchronizedListExtensions.SafeSnapshot{T}"/>.
/// </remarks>
public sealed class SynchronizedList<T> : IList<T>
{
    private readonly List<T> _items = new();
    private readonly object _gate = new();

    public T this[int index]
    {
        get { lock (_gate) return _items[index]; }
        set { lock (_gate) _items[index] = value; }
    }

    public int Count { get { lock (_gate) return _items.Count; } }
    public bool IsReadOnly => false;

    public void Add(T item) { lock (_gate) _items.Add(item); }
    public void Clear() { lock (_gate) _items.Clear(); }
    public bool Contains(T item) { lock (_gate) return _items.Contains(item); }

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        lock (_gate)
        {
            // The list may have grown since the caller read Count: copy only what fits.
            var count = Math.Min(_items.Count, array.Length - arrayIndex);
            if (count > 0)
                _items.CopyTo(0, array, arrayIndex, count);
        }
    }

    public int IndexOf(T item) { lock (_gate) return _items.IndexOf(item); }
    public void Insert(int index, T item) { lock (_gate) _items.Insert(index, item); }
    public bool Remove(T item) { lock (_gate) return _items.Remove(item); }
    public void RemoveAt(int index) { lock (_gate) _items.RemoveAt(index); }

    public List<T> Snapshot() { lock (_gate) return new List<T>(_items); }

    public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class SynchronizedListExtensions
{
    /// <summary>Consistent copy of a list that may be modified concurrently (atomic for <see cref="SynchronizedList{T}"/>).</summary>
    public static List<T> SafeSnapshot<T>(this IList<T> list) =>
        list is SynchronizedList<T> synchronized ? synchronized.Snapshot() : new List<T>(list);
}
