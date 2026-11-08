using System.Collections;

namespace BugInvestigationAgent.Core.Context;

/// <summary>
/// Thread-safe list for state written by the running pipeline and read concurrently by observers
/// (worker progress sync, Blazor UI). Enumeration works on a snapshot.
/// </summary>
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
    public void CopyTo(T[] array, int arrayIndex) { lock (_gate) _items.CopyTo(array, arrayIndex); }
    public int IndexOf(T item) { lock (_gate) return _items.IndexOf(item); }
    public void Insert(int index, T item) { lock (_gate) _items.Insert(index, item); }
    public bool Remove(T item) { lock (_gate) return _items.Remove(item); }
    public void RemoveAt(int index) { lock (_gate) _items.RemoveAt(index); }

    public List<T> Snapshot() { lock (_gate) return new List<T>(_items); }

    public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
